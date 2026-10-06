using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

// #194: a run keeps what it wrote in a game copy and removes the rest, and refuses to start copying onto a full drive.
public sealed class RunRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "retention-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Output => Path.Combine(_root, "out");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    // A small install: an executable, a managed assembly and a config the run will rewrite.
    private WorldFixture CopyOfInstall()
    {
        Directory.CreateDirectory(Path.Combine(Source, "BepInEx", "config")); Directory.CreateDirectory(Path.Combine(Source, "valheim_server_Data", "Managed"));
        File.WriteAllText(Path.Combine(Source, "valheim_server.exe"), new string('x', 4096));
        File.WriteAllText(Path.Combine(Source, "valheim_server_Data", "Managed", "assembly_valheim.dll"), new string('a', 8192));
        File.WriteAllText(Path.Combine(Source, "BepInEx", "config", "mod.cfg"), "before");
        File.WriteAllText(Path.Combine(Source, "unused.txt"), "the run deletes this");
        return WorldFixture.Copy(Source, Output, WorldFixture.Manifest(Source));
    }

    [Fact] public void RetireKeepsOnlyWhatTheRunAddedOrChangedAndRemovesTheCopy()
    {
        var copy = CopyOfInstall();
        File.WriteAllText(Path.Combine(copy.DirectoryPath, "BepInEx", "config", "mod.cfg"), "after");
        Directory.CreateDirectory(Path.Combine(copy.DirectoryPath, "BepInEx", "cache"));
        File.WriteAllText(Path.Combine(copy.DirectoryPath, "BepInEx", "cache", "audit.txt"), "made by the run");
        File.WriteAllText(Path.Combine(copy.DirectoryPath, "BepInEx", "LogOutput.log"), "[Info] loaded");
        File.Delete(Path.Combine(copy.DirectoryPath, "unused.txt"));
        string keep = Path.Combine(Output, "runtime-changes");

        var retired = copy.Retire(keep);

        Assert.False(Directory.Exists(copy.DirectoryPath));
        Assert.Equal(new[] { Path.Combine("BepInEx", "LogOutput.log"), Path.Combine("BepInEx", "cache", "audit.txt") }, retired.Added);
        Assert.Equal(new[] { Path.Combine("BepInEx", "config", "mod.cfg") }, retired.Changed);
        Assert.Equal(new[] { "unused.txt" }, retired.Missing);
        Assert.Empty(retired.NotKept);
        Assert.Equal("after", File.ReadAllText(Path.Combine(keep, "BepInEx", "config", "mod.cfg")));
        Assert.Equal("made by the run", File.ReadAllText(Path.Combine(keep, "BepInEx", "cache", "audit.txt")));
        Assert.False(File.Exists(Path.Combine(keep, "valheim_server.exe"))); // the source's own file is not kept
        Assert.True(retired.BytesFreed > 12_000);
        Assert.Equal("after".Length + "made by the run".Length + "[Info] loaded".Length, retired.KeptBytes);
        var changes = JsonDocument.Parse(File.ReadAllText(Path.Combine(keep, "changes.json"))).RootElement;
        Assert.Equal(copy.DirectoryPath, changes.GetProperty("Copy").GetString());
        Assert.Equal(2, changes.GetProperty("Added").GetArrayLength());
        Assert.StartsWith("removed " + copy.DirectoryPath, retired.ToString());
        copy.Dispose(); // already removed: nothing left to do
        Assert.Throws<ObjectDisposedException>(() => copy.Retire(keep + "2"));
    }

    // NativeSmoke stages a runtime copy and the run copies that copy again: the second copy's provenance record replaces the
    // first's, which is neither the run's change nor missing.
    [Fact] public void ACopyOfACopyReportsNothingTheRunDidNotDo()
    {
        using var staged = CopyOfInstall();
        var copy = WorldFixture.Copy(staged.DirectoryPath, Path.Combine(_root, "run"), WorldFixture.Manifest(staged.DirectoryPath));
        Assert.Contains("fixture-provenance.json", copy.SourceHashes.Keys);
        var retired = copy.Retire(Path.Combine(_root, "run", "changes"));
        Assert.Empty(retired.Added); Assert.Empty(retired.Changed); Assert.Empty(retired.Missing);
        Assert.False(Directory.Exists(copy.DirectoryPath));
    }

    [Fact] public void LargeFilesAreListedWithTheirHashInsteadOfKept()
    {
        var copy = CopyOfInstall();
        string dump = Path.Combine(copy.DirectoryPath, "crash.dmp"), log = Path.Combine(copy.DirectoryPath, "a.log"), second = Path.Combine(copy.DirectoryPath, "b.log");
        File.WriteAllText(dump, new string('d', 3000)); File.WriteAllText(log, new string('l', 600)); File.WriteAllText(second, new string('m', 600));
        string keep = Path.Combine(Output, "changes");

        var retired = copy.Retire(keep, maxFileBytes: 1000, maxKeptBytes: 1000);

        Assert.True(File.Exists(Path.Combine(keep, "a.log")));
        Assert.False(File.Exists(Path.Combine(keep, "b.log"))); // past the total
        Assert.False(File.Exists(Path.Combine(keep, "crash.dmp"))); // larger than one file may be
        Assert.Equal(new[] { ("b.log", 600L), ("crash.dmp", 3000L) }, retired.NotKept.Select(file => (file.Path, file.Bytes!.Value)).OrderBy(file => file.Path));
        Assert.All(retired.NotKept, file => Assert.Equal(64, file.Sha256!.Length));
        Assert.Contains("larger than", retired.NotKept.Single(file => file.Path == "crash.dmp").Reason);
    }

    [Fact] public void ALinkInTheCopyIsListedButNeverFollowed()
    {
        var copy = CopyOfInstall();
        string outside = Path.Combine(_root, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "not the copy's");
        try { Directory.CreateSymbolicLink(Path.Combine(copy.DirectoryPath, "linked"), outside); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException) { copy.Dispose(); return; } // Windows without the link privilege

        var retired = copy.Retire(Path.Combine(Output, "changes"));

        Assert.Equal("linked", Assert.Single(retired.NotKept).Path);
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt"))); // removing the copy removed the link, not its target
        Assert.False(Directory.Exists(copy.DirectoryPath));
    }

    // Windows refuses to delete a read-only file, and a copy keeps the source's attribute.
    [Fact] public void ReadOnlyFilesDoNotStopTheCopyFromGoing()
    {
        Directory.CreateDirectory(Source);
        string locked = Path.Combine(Source, "locked.dll");
        File.WriteAllText(locked, "read-only in the install");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        try
        {
            var retired = WorldFixture.Copy(Source, Output, WorldFixture.Manifest(Source));
            Assert.True((File.GetAttributes(Path.Combine(retired.DirectoryPath, "locked.dll")) & FileAttributes.ReadOnly) != 0);
            retired.Retire(Path.Combine(Output, "changes"));
            Assert.False(Directory.Exists(retired.DirectoryPath));
            var disposed = WorldFixture.Copy(Source, Output, WorldFixture.Manifest(Source));
            disposed.Dispose();
            Assert.False(Directory.Exists(disposed.DirectoryPath));
        }
        finally { File.SetAttributes(locked, FileAttributes.Normal); }
    }

    // A failed run keeps more of what it wrote: a large file may be what explains the failure.
    [Fact] public void AFailedRunKeepsLargerFilesThanAPass()
    {
        Assert.Equal((64L << 20, 256L << 20), PinnedServerRun.RetainLimits(passed: true));
        Assert.Equal((1L << 30, 2L << 30), PinnedServerRun.RetainLimits(passed: false));
    }

    [Fact] public void ChangesAreNeverKeptInsideTheCopyThatIsRemoved()
    {
        using var copy = CopyOfInstall();
        Assert.Throws<ArgumentException>(() => copy.Retire(Path.Combine(copy.DirectoryPath, "changes")));
        Assert.True(Directory.Exists(copy.DirectoryPath));
    }

    [Fact] public void AFullDriveIsRefusedWithTheAmountsAndTheDrive()
    {
        DiskSpace.AvailableOverride = _ => 3L << 30;
        try
        {
            var error = Assert.Throws<IOException>(() => DiskSpace.Require(Path.Combine(Output, "new", "run"), 2L << 30, "two game copies"));
            Assert.Contains("two game copies", error.Message);
            Assert.Contains("needs about 4.0 GB (2.0 GB plus 2.0 GB headroom)", error.Message);
            Assert.Contains("3.0 GB free", error.Message);
            Assert.Equal(3L << 30, DiskSpace.Require(Output, 512L << 20, "a small copy"));
            Assert.Equal(20L << 30, DiskSpace.Headroom(200L << 30)); // a tenth of a large estimate
        }
        finally { DiskSpace.AvailableOverride = null; }
    }

    [Fact] public void TheRealDriveAnswersForAPathThatDoesNotExistYet()
    {
        Assert.True(DiskSpace.Available(Path.Combine(_root, "not", "made", "yet")) > 0);
        Directory.CreateDirectory(Source); File.WriteAllText(Path.Combine(Source, "a"), "12345");
        Assert.Equal(5, DiskSpace.DirectoryBytes(Source));
        Assert.Equal(0, DiskSpace.DirectoryBytes(Path.Combine(_root, "missing")));
        Assert.Equal("812 MB", DiskSpace.Format(812L << 20)); Assert.Equal("2.5 GB", DiskSpace.Format(5L << 29));
    }

    // A server-load holds the staged server (which the run runs from) and the staged client at once.
    [Fact] public void ANativeSmokeChecksRoomForOneServerCopyAndTheClient()
    {
        string server = Path.Combine(_root, "server"), client = Path.Combine(_root, "client");
        Directory.CreateDirectory(server); Directory.CreateDirectory(client);
        File.WriteAllBytes(Path.Combine(server, "valheim_server.exe"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(client, "valheim.exe"), new byte[500]);
        long needed = 0;
        DiskSpace.AvailableOverride = _ => needed;
        try
        {
            needed = 1500 + DiskSpace.Headroom(1500) - 1;
            Assert.Contains("the staged server copy and the staged clean client",
                Assert.Throws<IOException>(() => SmokeOutput.RequireSpace(Output, server, client)).Message);
            needed++;
            SmokeOutput.RequireSpace(Output, server, client);
            needed = 1000 + DiskSpace.Headroom(1000);
            SmokeOutput.RequireSpace(Output, server, null);
        }
        finally { DiskSpace.AvailableOverride = null; }
    }
}
