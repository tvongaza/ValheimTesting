using Valheim.Testing;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;
using Valheim.Testing.GameSessions;

public class WorldDumpCaptureTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WorldDump");

    // The 20 November 2026 bounded run (#990) on a disposable Valheim 1.0.21 world a held valheim-test start --no-mod
    // --world-seed DumpProbe1 --world-name DumpContract1 generated and hosted (UID 588083535, ValheimCLI 21cfd74). The dump
    // replies and the cli_world line, the same before and after both dumps, are verbatim except the station's staging
    // directory, which reads C:/dumps here, and the world line's dir=, left empty; the CSVs are the files the game wrote,
    // byte for byte. The seed is the 2 October pair's, so this is that terrain, now at full precision.
    private const string WorldLine = "WORLD name=DumpContract1 seed=DumpProbe1 uid=588083535 worldgen=2 files=bda39d3bd86751fa8a5de84f8f9f2218 files_hashed=at_load dir=";
    private static readonly Dictionary<string, (int Step, (float, float, float) Window, string Reply, string File)> Recorded = new()
    {
        ["coarse"] = (128, (0, 0, 512), @"OK: WORLD_DUMP samples=64 step=128 window=0,0,512 extent=-400,-400..496,496 ms=2 world=C:/dumps\world.win_0_0_512_s128.csv", "world.win_0_0_512_s128.csv"),
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
        }, "C:/dumps", "dumpcontract1-" + layer, run.Step, run.Window, "1.0.21", output, CancellationToken.None);
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
            Replay("fine", output.Directory, worldAfter: WorldLine.Replace("588083535", "1")))).Message);
        byte[] truncated = File.ReadAllBytes(Path.Combine(FixtureDirectory, "dumpcontract1-fine.csv"))[..^60];
        await Assert.ThrowsAsync<InvalidDataException>(() => Replay("fine", output.Directory, content: truncated));
        Assert.Empty(Directory.GetFileSystemEntries(output.Directory));
        await Replay("fine", output.Directory);
        await Assert.ThrowsAsync<IOException>(() => Replay("fine", output.Directory)); // never replaces a pinned pair
    }

    [Fact]
    public async Task ARefusedDumpCommandStopsBeforeAnyFile()
    {
        using var output = new PinnedDumpFixture();
        var refusing = new ScriptedTransport().On("cli_world", _ => ScriptedTransport.Ok(WorldLine))
            .OnPrefix("cli_world_dump ", _ => ScriptedTransport.Ok("That command is a cheat"));
        await Assert.ThrowsAnyAsync<Exception>(() => WorldDump.CaptureCoreAsync(refusing.Actor(), (_, _, _) => throw new InvalidOperationException("no fetch"),
            "C:/dumps", "fine", 8, (0, 0, 64), "1.0.16", output.Directory, CancellationToken.None));
        Assert.Empty(Directory.GetFileSystemEntries(output.Directory));
    }

    [Theory]
    [InlineData("C:/my dumps", "fine", 8, "whitespace")]
    [InlineData("dumps", "fine", 8, "absolute")]
    [InlineData("C:/dumps", "../fine", 8, "Name the dump")]
    [InlineData("C:/dumps", "fine", 4, "needs a window")] // 1..4 m only for a window
    [InlineData("C:/dumps", "fine", 0, "1..1000")]
    [InlineData("C:/dumps", "fine", 1001, "1..1000")]
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
        string csv = WorldDumpManifest.Header + "\n-16,-16,1,Meadows,0,0,0.1,1\n-8,-16,1,Meadows,0,0,0.1,1\n-16,-8,1,Meadows,0,0,0.1,1\n-8,-8,1,Meadows,0,0,0.1,1\n";
        var transport = new ScriptedTransport().On("cli_world", _ => ScriptedTransport.Ok(WorldLine))
            .OnPrefix("cli_world_dump ", _ => ScriptedTransport.Ok(@"OK: WORLD_DUMP samples=4 step=8 extent=-16,-16..-8,0 ms=1 world=C:/dumps\world.csv"));
        Assert.Contains("extent", (await Assert.ThrowsAsync<InvalidDataException>(() => WorldDump.CaptureCoreAsync(transport.Actor(),
            (_, local, _) => PinnedDumpFixture.Fetch(local, "world.csv", csv), "C:/dumps", "whole", 8, null, "1.0.16", output.Directory, CancellationToken.None))).Message);
        Assert.Empty(Directory.GetFileSystemEntries(output.Directory));
    }

    // A full export's reply in ValheimCLI's own words (fd228e7 named its locations file locations= a second time, which the
    // capture refuses as a repeated key, so no whole-world capture could succeed): the count is locations=, the file locations_file=.
    [Fact]
    public async Task AWholeWorldDumpInTheGamesReplyFormatIsCaptured()
    {
        using var output = new PinnedDumpFixture();
        string csv = WorldDumpManifest.Header + "\n-16,-16,1,Meadows,0,0,0.1,1\n-8,-16,1,Meadows,0,0,0.1,1\n-16,-8,1,Meadows,0,0,0.1,1\n-8,-8,1,Meadows,0,0,0.1,1\n";
        var transport = new ScriptedTransport().On("cli_world", _ => ScriptedTransport.Ok(WorldLine))
            .OnPrefix("cli_world_dump ", _ => ScriptedTransport.Ok(@"OK: WORLD_DUMP samples=4 step=8 locations=12 ms=1 world=C:/dumps\world.csv locations_file=C:/dumps\locations.csv"));
        var manifest = await WorldDump.CaptureCoreAsync(transport.Actor(), (_, local, _) => PinnedDumpFixture.Fetch(local, "world.csv", csv),
            "C:/dumps", "whole", 8, null, "1.0.16", output.Directory, CancellationToken.None);
        Assert.Equal("cli_world_dump 8 C:/dumps", manifest.Command);
        Assert.Equal((-16f, -8f), (manifest.MinX, manifest.MaxZ));
    }

    // The game's refusal (the sample cap, no blended height yet) reaches the caller in its own words.
    [Fact]
    public async Task ADumpTheGameRefusesSaysWhy()
    {
        using var output = new PinnedDumpFixture();
        var transport = new ScriptedTransport().On("cli_world", _ => ScriptedTransport.Ok(WorldLine))
            .OnPrefix("cli_world_dump ", _ => ScriptedTransport.Ok("ERROR: WORLD_DUMP exceeds 1000000 samples; use --window or a larger step"));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => WorldDump.CaptureCoreAsync(transport.Actor(),
            (_, local, _) => PinnedDumpFixture.Fetch(local, "world.csv", ""), "C:/dumps", "big", 5, null, "1.0.16", output.Directory, CancellationToken.None));
        Assert.Contains("exceeds 1000000 samples", refused.Message);
    }

    [Fact]
    public async Task AOneMetreWindowIsCapturedAsAskedAndPinnedAtFullPrecision()
    {
        using var output = new PinnedDumpFixture();
        var csv = new System.Text.StringBuilder(WorldDumpManifest.Header + "\n");
        for (int z = 98; z <= 102; z++)
            for (int x = 98; x <= 102; x++)
                csv.Append(System.FormattableString.Invariant($"{x},{z},{29.95f + x * 1e-5f:R},Swamp,0,0,0.25,{30.0499992f - z * 1e-5f:R}\n"));
        var manifest = output.Capture("one", csv.ToString(), 1, window: (100, 100, 2));
        Assert.Equal("cli_world_dump 1 C:/dumps --window 100,100,2", manifest.Command);
        Assert.Equal((1, 98f, 102f), (manifest.Step, manifest.MinX, manifest.MaxZ));
        var grid = manifest.Verify();
        Assert.Equal(29.95f + 98 * 1e-5f, grid.GetHeight(98, 98)); // the float itself, not a 0.1 m rounding of it
        Assert.Equal(30.0499992f - 102 * 1e-5f, grid.GetBlendedHeight(100, 102));
    }
}
