using Valheim.Testing;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

public class WorldDumpCaptureTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WorldDump");

    // The 2 October 2026 bounded run on a disposable Valheim 1.0.16 dedicated server (world DumpContract1, seed DumpProbe1,
    // UID -18372600, read from the world's own metadata and agreed by cli_world before and after both dumps). The dump
    // replies are verbatim except the station's staging directory, which reads C:/dumps here; the CSVs are the files the
    // game wrote, byte for byte (SHA-256 recorded in the private run record). That run kept no cli_world line, so the one
    // below is built from those recorded facts in ValheimCLI's WORLD format.
    private const string WorldLine = "WORLD name=DumpContract1 seed=DumpProbe1 uid=-18372600 worldgen=2 files=- files_hashed=not_at_load dir=";
    private static readonly Dictionary<string, (int Step, (float, float, float) Window, string Reply, string File)> Recorded = new()
    {
        ["coarse"] = (128, (0, 0, 512), @"OK: WORLD_DUMP samples=64 step=128 window=0,0,512 extent=-400,-400..496,496 ms=1 world=C:/dumps\world.win_0_0_512_s128.csv", "world.win_0_0_512_s128.csv"),
        ["fine"] = (8, (0, 0, 64), @"OK: WORLD_DUMP samples=289 step=8 window=0,0,64 extent=-64,-64..64,64 ms=1 world=C:/dumps\world.win_0_0_64_s8.csv", "world.win_0_0_64_s8.csv"),
    };

    private static Task<WorldDumpManifest> Replay(string layer, string output, string? reply = null, string worldAfter = WorldLine, byte[]? content = null)
    {
        var run = Recorded[layer];
        bool dumped = false;
        var transport = new ScriptedTransport()
            .On("cli_world", _ => ScriptedTransport.Ok(dumped ? worldAfter : WorldLine))
            .OnPrefix("cli_world_dump ", _ => { dumped = true; return ScriptedTransport.Ok(reply ?? run.Reply); });
        var actor = transport.Actor();
        byte[] bytes = content ?? File.ReadAllBytes(Path.Combine(FixtureDirectory, $"dumpcontract1-{layer}.csv"));
        return WorldDump.CaptureCoreAsync(actor, (host, local, _) =>
        {
            Assert.Equal("C:/dumps", host);
            Directory.CreateDirectory(local);
            File.WriteAllBytes(Path.Combine(local, run.File), bytes);
            File.WriteAllText(Path.Combine(local, "an-older-dump.csv"), "not this one");
            return Task.FromResult(new FetchedDirectory(local, new string('0', 64), bytes.Length, 2));
        }, "C:/dumps", "dumpcontract1-" + layer, run.Step, run.Window, "1.0.16", output, CancellationToken.None);
    }

    [Theory]
    [InlineData("coarse")]
    [InlineData("fine")]
    public async Task ReplayingTheRecordedRunWritesExactlyTheCheckedInManifest(string layer)
    {
        using var output = new PinnedDumpFixture();
        var manifest = await Replay(layer, output.Directory);
        Assert.Equal($"cli_world_dump {Recorded[layer].Step} C:/dumps --window 0,0,{Recorded[layer].Window.Item3}", manifest.Command);
        Assert.Equal(Path.Combine(output.Directory, $"dumpcontract1-{layer}.csv"), manifest.Path);
        Assert.Equal(File.ReadAllText(Path.Combine(FixtureDirectory, $"dumpcontract1-{layer}.json")),
            File.ReadAllText(Path.Combine(output.Directory, $"dumpcontract1-{layer}.json")));
        Assert.Equal(new[] { $"dumpcontract1-{layer}.csv", $"dumpcontract1-{layer}.json" },
            Directory.GetFileSystemEntries(output.Directory).Select(Path.GetFileName).Order().ToArray()); // staging removed
    }

    [Theory]
    [InlineData("samples=289", "samples=288", "samples")]
    [InlineData("extent=-64,-64..64,64", "extent=-64,-64..64,72", "extent")]
    [InlineData("step=8", "step=5", "step")]
    [InlineData(" window=0,0,64", "", "window")]
    [InlineData("window=0,0,64", "window=0,0,80", "window")]
    [InlineData("window=0,0,64", "window=0,0", "window")]
    [InlineData(" extent=-64,-64..64,64", "", "extent")]
    [InlineData(@"world=C:/dumps\", @"world=C:/elsewhere\", "is not in C:/dumps")]
    [InlineData(" world=C:/dumps\\world.win_0_0_64_s8.csv", "", "world")]
    [InlineData("OK: WORLD_DUMP", "OK: WORLD_DUMPED", "exactly one OK: WORLD_DUMP")]
    public async Task AReplyThatDisagreesWithTheFileLeavesNothingBehind(string from, string to, string reason)
    {
        using var output = new PinnedDumpFixture();
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Replay("fine", output.Directory, Recorded["fine"].Reply.Replace(from, to)));
        Assert.Contains(reason, error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(output.Directory));
    }

    [Fact]
    public async Task AWorldChangeADamagedFileOrAnExistingDumpIsRefused()
    {
        using var output = new PinnedDumpFixture();
        Assert.Contains("world changed", (await Assert.ThrowsAsync<InvalidDataException>(() =>
            Replay("fine", output.Directory, worldAfter: WorldLine.Replace("-18372600", "1")))).Message);
        byte[] truncated = File.ReadAllBytes(Path.Combine(FixtureDirectory, "dumpcontract1-fine.csv"))[..^60];
        await Assert.ThrowsAsync<InvalidDataException>(() => Replay("fine", output.Directory, content: truncated));
        Assert.Empty(Directory.GetFileSystemEntries(output.Directory));
        await Replay("fine", output.Directory);
        await Assert.ThrowsAsync<IOException>(() => Replay("fine", output.Directory)); // never replaces a pinned pair
    }

    [Fact]
    public async Task ARefusedDumpCommandOrAnUnpinnedActorStopsBeforeAnyFile()
    {
        using var output = new PinnedDumpFixture();
        var refusing = new ScriptedTransport().On("cli_world", _ => ScriptedTransport.Ok(WorldLine))
            .OnPrefix("cli_world_dump ", _ => ScriptedTransport.Ok("That command is a cheat"));
        await Assert.ThrowsAnyAsync<Exception>(() => WorldDump.CaptureCoreAsync(refusing.Actor(), (_, _, _) => throw new InvalidOperationException("no fetch"),
            "C:/dumps", "fine", 8, (0, 0, 64), "1.0.16", output.Directory, CancellationToken.None));
        var unpinned = new ScriptedTransport().Actor(expectations: EnvironmentPinning.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorldDump.CaptureCoreAsync(unpinned, (_, _, _) => throw new InvalidOperationException("no fetch"),
            "C:/dumps", "fine", 8, null, "1.0.16", output.Directory, CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(output.Directory));
    }

    [Theory]
    [InlineData("C:/my dumps", "fine", 8, "whitespace")]
    [InlineData("dumps", "fine", 8, "absolute")]
    [InlineData("C:/dumps", "../fine", 8, "Name the dump")]
    [InlineData("C:/dumps", "fine", 4, "5..1000")]
    public async Task UnsafeArgumentsAreRefusedBeforeTheGameIsAsked(string hostDirectory, string name, int step, string reason)
    {
        using var output = new PinnedDumpFixture();
        var transport = new ScriptedTransport();
        var error = await Assert.ThrowsAnyAsync<ArgumentException>(() => WorldDump.CaptureCoreAsync(transport.Actor(), (_, _, _) => throw new InvalidOperationException(),
            hostDirectory, name, step, null, "1.0.16", output.Directory, CancellationToken.None));
        Assert.Contains(reason, error.Message);
        Assert.DoesNotContain(transport.Commands, c => c.StartsWith("cli_world", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWholeWorldReplyIsStillHeldToTheExtentItReports()
    {
        using var output = new PinnedDumpFixture();
        string csv = "x,z,height,biome,river,river_width,base_height\n-16,-16,1,Meadows,0,0,0.1\n-8,-16,1,Meadows,0,0,0.1\n-16,-8,1,Meadows,0,0,0.1\n-8,-8,1,Meadows,0,0,0.1\n";
        var transport = new ScriptedTransport().On("cli_world", _ => ScriptedTransport.Ok(WorldLine))
            .OnPrefix("cli_world_dump ", _ => ScriptedTransport.Ok(@"OK: WORLD_DUMP samples=4 step=8 extent=-16,-16..-8,0 ms=1 world=C:/dumps\world.csv"));
        Assert.Contains("extent", (await Assert.ThrowsAsync<InvalidDataException>(() => WorldDump.CaptureCoreAsync(transport.Actor(),
            (_, local, _) => PinnedDumpFixture.Fetch(local, "world.csv", csv), "C:/dumps", "whole", 8, null, "1.0.16", output.Directory, CancellationToken.None))).Message);
        Assert.Empty(Directory.GetFileSystemEntries(output.Directory));
    }
}
