using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;

public sealed class WorldSaveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-world-save-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    // A live client's worlds as the game leaves them after a save: the world's newest set (save 1, the bundled fixture), plus
    // what a live folder also holds and a fixture must not: a chunk file of an older save and the client's minimap cache.
    private WorldSave.Placement LiveWorld(string worlds = "client-worlds")
    {
        string root = Path.Combine(_root, worlds);
        DefaultSmokeWorld.Prepare(root);
        string world = Path.Combine(root, DefaultSmokeWorld.Name);
        File.WriteAllText(Path.Combine(world, "00_00__0_0.chunk"), "an older save's chunk");
        File.WriteAllText(Path.Combine(world, "cacheMinimapMask"), "minimap");
        return new WorldSave.Placement(world, DefaultSmokeWorld.Name, DefaultSmokeWorld.Uid);
    }

    // What the game does on a save: writes the next numbered set and removes the previous one.
    private static void SaveAgain(WorldSave.Placement live, int from, int to)
    {
        foreach (string part in new[] { "fwl2", "db2", "chunks", "ok" })
            File.Move(Path.Combine(live.World, $"_main.{from}.{part}"), Path.Combine(live.World, $"_main.{to}.{part}"));
    }

    private static readonly IReadOnlyDictionary<string, object?> Provenance = new Dictionary<string, object?> { ["runId"] = "run-test" };

    [Fact]
    public void ExportCopiesOnlyTheNewestSaveAndItsChunksIntoAFixtureStartStages()
    {
        var live = LiveWorld();
        SaveAgain(live, 1, 2);
        string saved = Path.Combine(_root, "saved");

        var result = WorldSave.Export(live, 2, saved, Provenance);

        Assert.Equal(2u, result.SaveNumber);
        string world = Path.Combine(saved, "worlds_local", DefaultSmokeWorld.Name);
        Assert.Equal(["00_00__0_1.chunk", "_main.2.chunks", "_main.2.db2", "_main.2.fwl2", "_main.2.ok"],
            Directory.EnumerateFiles(world).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(DefaultSmokeWorld.Uid, WorldIdentity.Read(Path.Combine(saved, "worlds_local")).UidText);
        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(saved, FixtureBake.ManifestFile))).RootElement;
        Assert.Equal(1, manifest.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(long.Parse(DefaultSmokeWorld.Uid), manifest.GetProperty("world").GetProperty("Uid").GetInt64());
        Assert.Equal("run-test", manifest.GetProperty("savedFrom").GetProperty("runId").GetString());
        Assert.Equal(2u, manifest.GetProperty("savedFrom").GetProperty("saveNumber").GetUInt32());
        var pinned = manifest.GetProperty("filesSha256").EnumerateObject().ToDictionary(file => file.Name, file => file.Value.GetString());
        Assert.Equal(5, pinned.Count);
        Assert.Equal(FileHash.Sha256(Path.Combine(live.World, "_main.2.db2")), pinned[DefaultSmokeWorld.Name + "/_main.2.db2"]);
        Assert.True(File.Exists(Path.Combine(live.World, "cacheMinimapMask"))); // the live world is only read
        Assert.DoesNotContain(Directory.EnumerateDirectories(_root), path => Path.GetFileName(path).StartsWith(".saved.tmp-", StringComparison.Ordinal));

        // start --world-fixture stages it as it stages a baked fixture: copied, checked against the manifest, which stays beside the copy.
        string run = Path.Combine(_root, "run");
        var staged = FixtureBake.Prepare(saved, run);
        Assert.Equal(DefaultSmokeWorld.Uid, staged.Identity.UidText);
        Assert.Equal(DefaultSmokeWorld.Uid, FixtureLayout.Discover(Path.Combine(run, "world-source", "worlds_local"), DefaultSmokeWorld.Uid).UidText);
        Assert.True(File.Exists(Path.Combine(run, "world-source", FixtureBake.ManifestFile)));
    }

    [Fact]
    public void ASavedFixtureWhoseFilesChangedIsRefusedBeforeStaging()
    {
        string saved = Path.Combine(_root, "saved");
        WorldSave.Export(LiveWorld(), 1, saved, Provenance);
        Assert.Equal(DefaultSmokeWorld.Uid, FixtureBake.Check(saved).UidText);
        File.AppendAllText(Path.Combine(saved, "worlds_local", DefaultSmokeWorld.Name, "_main.1.db2"), "x");
        Assert.Contains("differ from its manifest", Assert.Throws<InvalidDataException>(() => FixtureBake.Check(saved)).Message);
        File.Delete(Path.Combine(saved, "worlds_local", DefaultSmokeWorld.Name, "_main.1.ok"));
        Assert.Contains("incomplete", Assert.ThrowsAny<Exception>(() => FixtureBake.Check(saved)).Message);
        File.WriteAllText(Path.Combine(saved, "worlds_local", DefaultSmokeWorld.Name, "_main.1.ok"), "");
        var refused = Assert.Throws<InvalidDataException>(() => FixtureBake.Prepare(saved, Path.Combine(_root, "run")));
        Assert.Contains("differ from its manifest", refused.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "run", "world-source")));
    }

    [Fact]
    public void ExportRefusesAnOlderSaveThanConfirmedAnIncompleteSaveAndAnExistingOutput()
    {
        var live = LiveWorld();
        string saved = Path.Combine(_root, "saved");
        Assert.Contains("older than the save 2", Assert.Throws<InvalidDataException>(() => WorldSave.Export(live, 2, saved, Provenance)).Message);

        Directory.CreateDirectory(saved);
        Assert.Throws<IOException>(() => WorldSave.Export(live, 1, saved, Provenance));
        Directory.Delete(saved);

        File.Delete(Path.Combine(live.World, "00_00__0_1.chunk"));
        Assert.Contains("00_00__0_1.chunk", Assert.Throws<InvalidDataException>(() => WorldSave.Export(live, 1, saved, Provenance)).Message);
        File.Delete(Path.Combine(live.World, "_main.1.ok"));
        Assert.Contains("no complete save", Assert.Throws<InvalidDataException>(() => WorldSave.Export(live, 1, saved, Provenance)).Message);
        Assert.False(Path.Exists(saved));
        Assert.Empty(Directory.EnumerateDirectories(_root, ".saved.tmp-*"));
    }

    [Fact]
    public void ThePlacementIsTheOneHostedWorldTheRunJournalledAsPlacedAndNotMovedOut()
    {
        string journal = Path.Combine(_root, "journal");
        var run = new RunJournal("run-a");
        string world = Path.Combine(_root, "worlds_local", "VTDefaultSmoke");
        Assert.Contains("no run run-a", Assert.Throws<InvalidOperationException>(() => WorldSave.FindPlacement(journal, "run-a")).Message);

        run.AppendLocal("client", JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", world), ("holds", "hosted-world"), ("world", "VTDefaultSmoke"),
            ("worldUid", DefaultSmokeWorld.Uid), ("placementState", "intended")), journal);
        // Intended is not placed.
        Assert.Contains("no hosted world", Assert.Throws<InvalidOperationException>(() => WorldSave.FindPlacement(journal, "run-a")).Message);
        run.AppendLocal("client", JournalEntry.Of(JournalEntry.CopyDone, ("runtime", world), ("verified", "true"), ("placementState", "transferred")), journal);
        Assert.Equal(new WorldSave.Placement(world, "VTDefaultSmoke", DefaultSmokeWorld.Uid), WorldSave.FindPlacement(journal, "run-a"));

        run.AppendLocal("client", JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", world), ("handedOver", "true")), journal);
        Assert.Contains("no hosted world", Assert.Throws<InvalidOperationException>(() => WorldSave.FindPlacement(journal, "run-a")).Message);

        // A second arm's placement while the first is still in place is ambiguous: neither is chosen.
        string other = Path.Combine(_root, "worlds_local", "Other");
        foreach (string path in new[] { world, other })
        {
            run.AppendLocal("client", JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", path), ("holds", "hosted-world"), ("world", Path.GetFileName(path)), ("worldUid", "1")), journal);
            run.AppendLocal("client", JournalEntry.Of(JournalEntry.CopyDone, ("runtime", path), ("placementState", "transferred")), journal);
        }
        Assert.Contains("2 hosted worlds", Assert.Throws<InvalidOperationException>(() => WorldSave.FindPlacement(journal, "run-a")).Message);
    }

    [Fact]
    public void StartTakesAWorldFixtureAndAComparisonStillDoesNot()
    {
        Assert.True(SmokeCommandOptions.TryRead(["--world-fixture", "saved"], SmokeCommandOptions.Command.Start, allowImplicitMod: true, out var parsed, out _));
        Assert.Equal("saved", parsed!.Options["--world-fixture"]);
        Assert.False(SmokeCommandOptions.TryRead(["--mod", "a.dll", "--mod", "b.dll", "--world-fixture", "saved"], SmokeCommandOptions.Command.ServerLoadAb, false, out _, out string error));
        Assert.Equal("Unknown option: --world-fixture", error);
        Assert.False(SmokeCommandOptions.TryRead(["--bake-fixture", "new"], SmokeCommandOptions.Command.Start, true, out _, out error));
        Assert.Equal("Unknown option: --bake-fixture", error);
    }

    [Theory]
    [InlineData]
    [InlineData("--run", "run-a")]
    [InlineData("--run", "run-a", "--output")]
    [InlineData("--run", "run-a", "--run", "run-b")]
    [InlineData("--run", "run-a", "--output", "x", "--extra", "y")]
    public void UsageErrorsExitTwo(params string[] args)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, WorldSave.Run(args, output, error, Path.Combine(_root, "holds"), Path.Combine(_root, "journal")));
        Assert.Contains(WorldSave.Usage, error.ToString());
    }

    [Fact]
    public void ARunNotHeldHereOrFinishingIsRefusedBeforeAnythingIsSent()
    {
        string holds = Path.Combine(_root, "holds"), journal = Path.Combine(_root, "journal"), evidence = Directory.CreateDirectory(Path.Combine(_root, "evidence")).FullName;
        using (var output = new StringWriter())
        using (var error = new StringWriter())
        {
            Assert.Equal(3, WorldSave.Run(["--run", "run-none", "--output", Path.Combine(_root, "saved")], output, error, holds, journal));
            Assert.Contains("is not held on this machine", error.ToString());
        }
        using var hold = ForegroundHold.Open("run-held", evidence, holds);
        using (var output = new StringWriter())
        using (var error = new StringWriter())
        {
            Assert.Equal(3, WorldSave.Run(["--run", "run-held", "--output", Path.Combine(_root, "saved")], output, error, holds, journal));
            Assert.Contains("no run run-held", error.ToString());
        }
        ForegroundHold.Request("run-held", holds);
        using (var output = new StringWriter())
        using (var error = new StringWriter())
        {
            Assert.Equal(3, WorldSave.Run(["--run", "run-held", "--output", Path.Combine(_root, "saved")], output, error, holds, journal));
            Assert.Contains("is finishing", error.ToString());
        }
        Assert.False(Path.Exists(Path.Combine(_root, "saved")));
    }

    // The whole command against a live stand-in for the owned client: the held run's marker, its client lease and world pins,
    // its journalled placement. The stand-in answers ValheimCLI's protocol and, on the one save, writes the next save set as the
    // game does; the command copies that save out with the run's provenance.
    [Fact]
    public async Task AHeldRunsWorldIsSavedOnceAndCopiedOutWhileTheRunStaysLive()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        var live = LiveWorld();
        string holds = Path.Combine(_root, "holds"), journal = Path.Combine(_root, "journal");
        string output = Directory.CreateDirectory(Path.Combine(_root, "run-output")).FullName;
        string evidence = Directory.CreateDirectory(Path.Combine(output, "evidence", "smoke")).FullName;
        var run = new RunJournal("run-live");
        run.AppendLocal("client", JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", live.World), ("holds", "hosted-world"), ("world", live.Name), ("worldUid", live.WorldUid)), journal);
        run.AppendLocal("client", JournalEntry.Of(JournalEntry.CopyDone, ("runtime", live.World), ("placementState", "transferred")), journal);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var commands = new List<string>();
        Task serve = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync("VALHEIM_CLI_READY");
            await writer.WriteLineAsync("VALHEIM_CLI_CAPS completion");
            while (await reader.ReadLineAsync() is { } line)
            {
                string command = line[(line.IndexOf(':', 5) + 1)..];
                commands.Add(command);
                string reply;
                if (command.StartsWith("cli_expect ", StringComparison.Ordinal)) reply = "OK: EXPECT";
                else if (command == "cli_extensions")
                    reply = "EXTENSIONS " + JsonSerializer.Serialize(new { apiVersion = 1, extensions = new[] { new { id = "valheim.session", instance = "a", closing = false,
                        commands = new[] { "state", "save" }.Select(name => new { name, resultVersion = 1, readOnly = name == "state" }) } } });
                else
                {
                    object data;
                    if (command.StartsWith("cli_extension valheim.session/save", StringComparison.Ordinal))
                    {
                        SaveAgain(live, 1, 2);
                        data = new { source = "session-save", complete = true, worldUid = live.WorldUid, saved = true, before = 1u, after = 2u, milliseconds = 10 };
                    }
                    else data = new { source = "session-state", complete = true, phase = "world-present", worldUid = live.WorldUid, worldPresent = true, worldReady = true,
                        server = true, dedicated = false, localPlayer = true, playerReady = true, saving = false, loadError = false, connectionStatus = "Connected" };
                    reply = "EXTENSION_RESULT " + JsonSerializer.Serialize(new { schemaVersion = 1, ok = true, extension = "valheim.session", instance = "a", data });
                }
                await writer.WriteLineAsync("OUTPUT:1");
                await writer.WriteLineAsync(reply);
                await writer.WriteLineAsync("END_OUTPUT");
            }
        });
        try
        {
            using var process = Process.GetCurrentProcess();
            var plan = new ClientRunPlan { Install = output, Port = port, Pins = new() { ["example.mod"] = new string('a', 32) } };
            OwnedClientCommandLease.Write(evidence, process.Id, OwnedClientCommandLease.LocalStartFileTimeUtc(process.Id), plan);
            OwnedClientCommandLease.WriteVerifiedWorld(evidence, plan, live.WorldUid);
            using var hold = ForegroundHold.Open("run-live", evidence, holds);
            string saved = Path.Combine(_root, "saved");

            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            Assert.Equal(0, WorldSave.Run(["--run", "run-live", "--output", saved], stdout, stderr, holds, journal));

            Assert.Empty(stderr.ToString());
            Assert.Contains("SAVED world VTDefaultSmoke (UID " + live.WorldUid + ") save 2", stdout.ToString());
            Assert.Contains("start --world-fixture " + ProtectedPaths.Resolve(saved), stdout.ToString());
            Assert.Single(commands, command => command.StartsWith("cli_extension valheim.session/save", StringComparison.Ordinal));
            Assert.Equal(DefaultSmokeWorld.Uid, WorldIdentity.Read(Path.Combine(saved, "worlds_local")).UidText);
            var from = JsonDocument.Parse(File.ReadAllText(Path.Combine(saved, FixtureBake.ManifestFile))).RootElement.GetProperty("savedFrom");
            Assert.Equal("run-live", from.GetProperty("runId").GetString());
            Assert.Equal(2u, from.GetProperty("confirmedSaveNumber").GetUInt32());
            Assert.Equal(live.WorldUid, from.GetProperty("pins").GetProperty("worlduid").GetString());
            Assert.Equal(new string('a', 32), from.GetProperty("pins").GetProperty("example.mod").GetString());
            // The run is untouched: still held, its lease and world pins in place, its world where it was.
            Assert.Equal(evidence, ForegroundHold.HeldEvidence("run-live", holds));
            Assert.True(File.Exists(Path.Combine(evidence, OwnedClientCommandLease.WorldPins)));
            Assert.True(File.Exists(Path.Combine(live.World, "_main.2.fwl2")));
        }
        finally
        {
            listener.Stop();
            await serve.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
