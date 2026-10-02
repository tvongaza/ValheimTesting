using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;

// #194: finding the copies runs left behind, and removing only the chosen ones, never one a process uses.
public sealed class OwnedCopiesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "owned-copies-" + Guid.NewGuid().ToString("N"));
    private string Runs => Path.Combine(_root, "runs");
    public void Dispose() { OwnedCopies.ProcessesOverride = null; if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string Source(string name, params string[] files)
    {
        string source = Path.Combine(_root, "sources", name);
        foreach (string file in files) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, file))!); File.WriteAllText(Path.Combine(source, file), name + ":" + file); }
        return source;
    }
    private static void Result(string directory, bool passed) { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { Passed = passed })); }
    private string Copy(string source, string into) { Directory.CreateDirectory(into); var copy = WorldFixture.Copy(source, into, WorldFixture.Manifest(source)); copy.Preserve = true; copy.Dispose(); return copy.DirectoryPath; }

    // A pinned run's output (copies beside result.json), a NativeSmoke output (staged copies beside evidence/), and things that are not copies.
    private (string Server, string Client, string World, string Staged) Layout()
    {
        string server = Source("server", ServerLaunch.WindowsExecutable, "valheim_server_Data/Managed/assembly_valheim.dll");
        string client = Source("client", ClientLaunch.WindowsExecutable, "valheim_Data/Managed/assembly_valheim.dll");
        string world = Source("world", "worlds_local/Test.db");
        string first = Path.Combine(Runs, "first"); Result(first, passed: true);
        string smoke = Path.Combine(Runs, "smoke"); Result(Path.Combine(smoke, "evidence"), passed: false);
        var copies = (Copy(server, first), Copy(client, Path.Combine(Runs, "orphan")), Copy(world, first), Copy(server, Path.Combine(smoke, "staged-runtime")));
        Directory.CreateDirectory(Path.Combine(Runs, "valheim-test-" + new string('a', 32))); // the name without the manifest: not a copy
        Directory.CreateDirectory(Path.Combine(Runs, "unrelated", "game")); File.WriteAllText(Path.Combine(Runs, "unrelated", "game", ServerLaunch.WindowsExecutable), "a live install");
        return copies;
    }

    [Fact] public void FindListsOnlyCopiesWithTheirKindRunAndUseAndChangesNothing()
    {
        var (server, client, world, staged) = Layout();
        OwnedCopies.ProcessesOverride = () => [(42, Path.Combine(client, ClientLaunch.WindowsExecutable)), (7, Path.Combine(_root, "elsewhere.exe"))];
        var copies = OwnedCopies.Find(Runs);
        Assert.Equal(new[] { client, server, staged, world }.Order(StringComparer.Ordinal), copies.Select(copy => copy.Path).Order(StringComparer.Ordinal));
        var byPath = copies.ToDictionary(copy => copy.Path);
        Assert.Equal((OwnedCopyKind.ServerRuntime, true), (byPath[server].Kind, byPath[server].Passed!.Value));
        Assert.Equal((OwnedCopyKind.ServerRuntime, false), (byPath[staged].Kind, byPath[staged].Passed!.Value)); // its run's evidence/ beside staged-runtime/
        Assert.Equal(OwnedCopyKind.World, byPath[world].Kind);
        Assert.Equal((OwnedCopyKind.ClientRuntime, (bool?)null, (string?)null), (byPath[client].Kind, byPath[client].Passed, byPath[client].Result));
        Assert.Equal(new[] { 42 }, byPath[client].InUseBy); Assert.False(byPath[server].InUse);
        Assert.All(copies, copy => Assert.True(Directory.Exists(copy.Path)));
        Assert.True(copies[0].Bytes >= copies[^1].Bytes);
    }

    [Fact] public void RemoveTakesOnlyAChosenIdleGameCopyAndKeepsWhatItsRunChanged()
    {
        var (server, client, world, _) = Layout();
        File.WriteAllText(Path.Combine(server, "toolkit-unity.log"), "written by the run");
        OwnedCopies.ProcessesOverride = () => [(42, Path.Combine(client, ClientLaunch.WindowsExecutable))];

        var retired = OwnedCopies.Remove(server);
        Assert.False(Directory.Exists(server));
        Assert.Equal("written by the run", File.ReadAllText(Path.Combine(server + "-changes", "toolkit-unity.log")));
        Assert.Equal(new[] { "toolkit-unity.log" }, retired.Added);

        Assert.Contains("Process 42", Assert.Throws<InvalidOperationException>(() => OwnedCopies.Remove(client)).Message);
        Assert.True(Directory.Exists(client));
        Assert.Contains("world copy", Assert.Throws<InvalidOperationException>(() => OwnedCopies.Remove(world)).Message);
        Assert.True(Directory.Exists(world));
        OwnedCopies.Remove(world, allowWorld: true);
        Assert.False(Directory.Exists(world));
        Assert.Throws<ArgumentException>(() => OwnedCopies.Remove(Path.Combine(Runs, "valheim-test-" + new string('a', 32))));
        Assert.Throws<ArgumentException>(() => OwnedCopies.Remove(Path.Combine(Runs, "unrelated", "game")));
        Assert.True(File.Exists(Path.Combine(Runs, "unrelated", "game", ServerLaunch.WindowsExecutable)));
    }

    [Fact] public void TheCommandListsWithoutChangingAndRemovesOnlyWhatItIsGivenUnderItsRoot()
    {
        var (server, client, world, staged) = Layout();
        OwnedCopies.ProcessesOverride = () => [];
        var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(0, CopiesCommand.Run([Runs], output, error));
        string listing = output.ToString();
        Assert.Contains("4 copies", listing); Assert.Contains("Nothing was changed.", listing);
        Assert.Contains($"--remove \"{server}\"", listing); Assert.Contains($"--remove \"{staged}\"", listing);
        Assert.DoesNotContain($"--remove \"{client}\"", listing); // no run result: a run still going, or a killed one
        Assert.Contains("1 game copies have no run result", listing);
        Assert.DoesNotContain($"--remove \"{world}\"", listing); // a world is never offered for removal
        Assert.All(new[] { server, client, world, staged }, path => Assert.True(Directory.Exists(path)));

        output = new StringWriter();
        Assert.Equal(0, CopiesCommand.Run([Runs, "--json"], output, error));
        Assert.Equal(4, JsonDocument.Parse(output.ToString()).RootElement.GetArrayLength());
        Assert.Contains("\"ServerRuntime\"", output.ToString());

        string outside = Copy(Source("other", ServerLaunch.WindowsExecutable), Path.Combine(_root, "other-runs"));
        error = new StringWriter();
        Assert.Equal(3, CopiesCommand.Run([Runs, "--remove", staged, "--remove", outside, "--remove", world], new StringWriter(), error));
        Assert.False(Directory.Exists(staged));
        Assert.True(Directory.Exists(outside)); Assert.Contains("not under", error.ToString());
        Assert.True(Directory.Exists(world)); Assert.Contains("world copy", error.ToString());

        Assert.Equal(2, CopiesCommand.Run([Runs, "--allow-world"], new StringWriter(), new StringWriter()));
        Assert.Equal(2, CopiesCommand.Run([Runs, "--json", "--remove", server], new StringWriter(), new StringWriter()));
        Assert.Equal(2, CopiesCommand.Run([], new StringWriter(), new StringWriter()));
        Assert.Equal(3, CopiesCommand.Run([Path.Combine(_root, "missing")], new StringWriter(), new StringWriter()));
    }

    [Fact] public void TheCommandDoesNotFollowALinkOutOfTheSelectedRootToRemoveACopy()
    {
        string outside = Copy(Source("external", ServerLaunch.WindowsExecutable), Path.Combine(_root, "outside"));
        Directory.CreateDirectory(Runs);
        string link = Path.Combine(Runs, "linked");
        try { Directory.CreateSymbolicLink(link, Path.GetDirectoryName(outside)!); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException) { return; } // Windows without link privilege

        string throughLink = Path.Combine(link, Path.GetFileName(outside));
        var message = new StringWriter();
        Assert.Equal(3, CopiesCommand.Run([Runs, "--remove", throughLink], new StringWriter(), message));
        Assert.Contains("traverses a link", message.ToString());
        Assert.True(Directory.Exists(outside));
    }

    [Fact] public void AnEmptyOrCorruptProvenanceCannotAuthorizeRemoval()
    {
        string copy = Copy(Source("server", ServerLaunch.WindowsExecutable), Runs);
        string provenance = Path.Combine(copy, "fixture-provenance.json");
        File.WriteAllText(provenance, "{}");
        Assert.Throws<InvalidDataException>(() => OwnedCopies.Remove(copy));
        File.WriteAllText(provenance, "{\"valheim_server.exe\":\"not-a-sha256\"}");
        Assert.Throws<InvalidDataException>(() => OwnedCopies.Remove(copy));
        File.WriteAllText(provenance, "{\"valheim_server.exe\":null}");
        Assert.Throws<InvalidDataException>(() => OwnedCopies.Remove(copy));
        Assert.True(Directory.Exists(copy));
    }

    // A copy a live run holds is in use before any game runs from it; once the holder lets go, or has ended, it is not.
    [Fact] public void TheRunThatMadeACopyHoldsItUntilItLetsGoOrEnds()
    {
        string source = Source("server", ServerLaunch.WindowsExecutable);
        var held = WorldFixture.Copy(source, Runs, WorldFixture.Manifest(source));
        Assert.True(File.Exists(held.DirectoryPath + ".owner.json"));
        Assert.Equal(new[] { Environment.ProcessId }, OwnedCopies.Find(Runs).Single().InUseBy);
        Assert.Contains($"Process {Environment.ProcessId}", Assert.Throws<InvalidOperationException>(() => OwnedCopies.Remove(held.DirectoryPath)).Message);
        held.Preserve = true; held.Dispose();
        Assert.False(File.Exists(held.DirectoryPath + ".owner.json"));
        Assert.False(OwnedCopies.Find(Runs).Single().InUse);

        // A holder that ended without letting go: its id now names no process, or another process started later.
        using var ended = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
        ended.WaitForExit();
        File.WriteAllText(held.DirectoryPath + ".owner.json", JsonSerializer.Serialize(new { Pid = ended.Id, StartedUtc = DateTime.UtcNow.AddHours(-1) }));
        Assert.False(OwnedCopies.Find(Runs).Single().InUse);
        File.WriteAllText(held.DirectoryPath + ".owner.json", JsonSerializer.Serialize(new { Pid = Environment.ProcessId, StartedUtc = DateTime.UtcNow.AddDays(-3) }));
        Assert.False(OwnedCopies.Find(Runs).Single().InUse); // this process's id, but not its start time
    }

    // A 1.0 world is a <name>/ directory of chunks with _main.N.fwl2, and is protected like any world.
    [Fact] public void AChunkedWorldCopyIsAWorld()
    {
        string source = Source("chunked", "VTDefaultSmoke/_main.2.fwl2", "VTDefaultSmoke/_main.2.db2", "VTDefaultSmoke/1e_20__1_1.chunk");
        string copy = Copy(source, Runs);
        Assert.Equal(OwnedCopyKind.World, OwnedCopies.Find(Runs).Single().Kind);
        Assert.Throws<InvalidOperationException>(() => OwnedCopies.Remove(copy));
    }

    // A retry after a removal that could not finish keeps its changes beside the first attempt's.
    [Fact] public void ARetryKeepsItsChangesInANewFolder()
    {
        string copy = Copy(Source("server", ServerLaunch.WindowsExecutable), Runs);
        Directory.CreateDirectory(copy + "-changes"); File.WriteAllText(Path.Combine(copy + "-changes", "changes.json"), "{}");
        var retired = OwnedCopies.Remove(copy);
        Assert.Equal(copy + "-changes-2", retired.KeptIn);
        Assert.False(Directory.Exists(copy));
    }

    // A copy named through a linked directory (macOS's /var is /private/var) is matched to the program running from it.
    [Fact] public void ACopyReachedThroughALinkStillMatchesItsProgram()
    {
        if (OperatingSystem.IsWindows()) return; // the system names Windows executables as given; links there need privileges
        string real = Path.Combine(_root, "real"), linked = Path.Combine(_root, "linked");
        string copy = Path.Combine(real, "valheim-test-" + new string('c', 32));
        Directory.CreateDirectory(copy);
        File.WriteAllText(Path.Combine(copy, "fixture-provenance.json"), "{}");
        Directory.CreateSymbolicLink(linked, real);
        // The system names the executable with every link resolved, this test's temp root included.
        OwnedCopies.ProcessesOverride = () => [(99, Path.Combine(OwnedCopies.Resolved(copy), "valheim_server.x86_64"))];
        Assert.DoesNotContain("linked", OwnedCopies.Resolved(Path.Combine(linked, "valheim-test-" + new string('c', 32))));
        Assert.Equal(new[] { 99 }, Assert.Single(OwnedCopies.Find(Path.Combine(linked))).InUseBy);
    }

    // The real process list: a program running from inside a copy makes it in use, on every platform.
    [Fact] public void AProgramRunningFromACopyMakesItInUse()
    {
        string copy = Path.Combine(_root, "valheim-test-" + new string('b', 32));
        Directory.CreateDirectory(copy);
        File.WriteAllText(Path.Combine(copy, "fixture-provenance.json"), "{}");
        string program = Path.Combine(copy, OperatingSystem.IsWindows() ? "cmd.exe" : "sleep");
        File.Copy(OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sleep", program);
        // macOS kills a copied Apple system binary at launch (exit 137); an ad-hoc signature lets the copy run.
        if (OperatingSystem.IsMacOS()) System.Diagnostics.Process.Start("codesign", ["--force", "--sign", "-", program])!.WaitForExit();
        var start = new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in OperatingSystem.IsWindows() ? new[] { "/c", "ping -n 60 127.0.0.1 >nul" } : ["60"]) start.ArgumentList.Add(argument);
        using var running = System.Diagnostics.Process.Start(start)!;
        try
        {
            Thread.Sleep(200);
            Assert.False(running.HasExited, $"the program running from the copy ended at once (exit {(running.HasExited ? running.ExitCode : 0)})");
            var found = OwnedCopies.Find(_root).Single();
            Assert.Contains(running.Id, found.InUseBy);
            Assert.Throws<InvalidOperationException>(() => OwnedCopies.Remove(copy));
            Assert.True(Directory.Exists(copy));
        }
        finally { running.Kill(entireProcessTree: true); running.WaitForExit(); }
        Assert.False(OwnedCopies.Find(_root).Single().InUse);
    }
}
