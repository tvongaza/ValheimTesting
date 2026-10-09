using System.Diagnostics;
using Valheim.Testing.Game;
using Xunit;

public sealed class ExtractOnceTests
{
    [Fact]
    public async Task AReaderHeldAcrossTheSwapIsReleasedAndItsOldFolderIsRemoved()
    {
        string root = Path.Combine(Path.GetTempPath(), "vt-extract-reader-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "bundle");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "member.dll"), "old");
        Task? release = null;
        try
        {
            var elapsed = Stopwatch.StartNew();
            bool extracted = ExtractOnce.Ensure(target,
                path => File.Exists(Path.Combine(path, "member.dll")) && File.ReadAllText(Path.Combine(path, "member.dll")) == "new",
                staging => { Directory.CreateDirectory(staging); File.WriteAllText(Path.Combine(staging, "member.dll"), "new"); },
                "test bundle",
                old =>
                {
                    // The old folder has moved aside, but the new folder has not moved in yet. On Windows this open
                    // reader blocks deletion; the retry must wait for it instead of leaving .old-* behind.
                    var reader = new FileStream(Path.Combine(old, "member.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
                    release = Task.Run(async () => { await Task.Delay(300); reader.Dispose(); });
                });
            if (release != null) await release;
            Assert.True(extracted);
            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "member.dll")));
            Assert.Empty(Directory.GetDirectories(root, "bundle.old-*"));
            if (OperatingSystem.IsWindows()) Assert.True(elapsed.ElapsedMilliseconds >= 250, "Windows cleanup did not wait for the held reader");
        }
        finally
        {
            if (release != null) await release;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AnIntactCopySweepsAnOldFolderLeftByAnEarlierRun()
    {
        string root = Path.Combine(Path.GetTempPath(), "vt-extract-sweep-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "bundle"), stale = target + ".old-earlier";
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(target, "member.dll"), "new");
        File.WriteAllText(Path.Combine(stale, "member.dll"), "old");
        try
        {
            Assert.False(ExtractOnce.Ensure(target, path => File.Exists(Path.Combine(path, "member.dll")),
                _ => throw new InvalidOperationException("an intact copy must not be replaced"), "test bundle"));
            Assert.False(Directory.Exists(stale));
            Assert.Equal("new", File.ReadAllText(Path.Combine(target, "member.dll")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
