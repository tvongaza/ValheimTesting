using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// The vanilla-client check against a scripted client shaped like the adapter's UnresolvedPrefabs census and a scripted
// server, inside one ClientRounds round: arrival, a census that completes once the area loads, and the client's logs.
public sealed class VanillaClientCheckTests : IDisposable
{
    private const string Census = "mymod.testing/unresolved-prefabs", ServerPiece = "mymod_serverpiece";
    private static readonly HeightExpectation Point = new(100, -40, 42.5f);
    private readonly string _output = Directory.CreateTempSubdirectory("vanilla-client-").FullName;
    public void Dispose() => Directory.Delete(_output, recursive: true);

    private static object Scan(bool complete, params (int Hash, int Count)[] unresolved) => new
    {
        source = "unresolved-prefabs", complete, x = 100f, z = -40f, radius = 64f, zones = 9, zonesLoaded = complete ? 9 : 4,
        scanned = complete ? 120 : 30, withoutPrefab = 2,
        unresolved = unresolved.Select(u => new { hash = u.Hash, count = u.Count, x = 101.5f, y = 43f, z = -38f }).ToArray(),
    };

    private (ScriptedTransport Client, ScriptedTransport Server) Scripted(params (int Hash, int Count)[] unresolved)
    {
        int reads = 0;
        var client = new ScriptedTransport()
            .OnPrefix("cli_skip_intro", _ => ScriptedTransport.Ok("OK: skipped=False profileFirstSpawn=False position=0,40,0 ms=3"))
            .Extension("valheim.world", "player-support", _ => new
            {
                source = "local-player-support", complete = true, x = 0f, y = 40f, z = 0f, speed = 0f,
                grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres",
            })
            .ArrivalSignals(() => new
            {
                source = "local-player-support", complete = true, x = Point.X, y = Point.Height, z = Point.Z, speed = 0f,
                grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres",
            })
            // The first read is taken while the area still loads.
            .Extension("mymod.testing", "unresolved-prefabs", _ => Scan(++reads > 1, unresolved));
        var server = new ScriptedTransport()
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", _ => ScriptedTransport.Ok("OK: asked peer 1 to teleport"));
        return (client, server);
    }

    private string Log(string text)
    {
        string path = Path.Combine(_output, "client-LogOutput.log");
        File.WriteAllText(path, "[Message:   BepInEx] BepInEx 5.4.23.2 - valheim\n" + text + "[Message:   BepInEx] Chainloader startup complete\n");
        return path;
    }

    private (ScenarioReport Report, Exception? Error) Run(ScriptedTransport client, ScriptedTransport server, VanillaClientCheck check)
    {
        var report = new ScenarioReport("vanilla");
        using var clientActor = client.Actor("client");
        using var serverActor = server.Actor("server");
        var round = new ClientRound("first", 0, true, serverActor, clientActor, report, _output, "1");
        try { check.Measure(round); return (report, null); }
        catch (Exception error) { return (report, error); }
    }

    private VanillaClientCheck Check(string? log = null, bool points = true) => new()
    {
        Capability = Census, Points = points ? new[] { Point } : Array.Empty<HeightExpectation>(), KnownPrefabs = [ServerPiece],
        CensusInterval = TimeSpan.FromMilliseconds(5), CensusTimeout = TimeSpan.FromSeconds(5),
        ClientLogs = log == null ? null : new Func<IReadOnlyList<RunLog>>(() => new[] { new RunLog("client BepInEx log", log, Required: true) }),
    };

    [Fact] public void AClientThatResolvesEverythingAndLogsNothingPasses()
    {
        var (client, server) = Scripted();
        var (report, error) = Run(client, server, Check(Log("[Info   : Unity Log] Loading world\n")));
        Assert.Null(error);
        Assert.Equal(new[] { "first: arrive at vanilla-client point 1 (100, -40)", "first: the client resolves every prefab hash at vanilla-client point 1 (100, -40)",
            "first: the client's logs show no missing prefabs, missing RPC handlers or RemoveObjects errors" }, report.Steps.Select(s => s.Name));
        Assert.True(report.Passed);
        Assert.Equal(1, server.Count("cli_teleport_peer"));
        Assert.Equal(2, client.Count("cli_extension mymod.testing/unresolved-prefabs")); // Incomplete, then complete.
        using var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "first-vanilla-client-1.json")));
        Assert.True(evidence.RootElement.GetProperty("Complete").GetBoolean());
        Assert.True(File.Exists(Path.Combine(_output, "first-vanilla-client-1-arrival.json")));
        Assert.True(File.Exists(Path.Combine(_output, "first-vanilla-client-logs.json")));
    }

    [Fact] public void AnUnresolvedHashFailsTheCheckAndIsNamed()
    {
        // Negative control: a prefab only the server's mod registers is near the player.
        int hash = StableHash.Of(ServerPiece);
        var (client, server) = Scripted((hash, 3), (12345, 1));
        var (report, error) = Run(client, server, Check(Log(""), points: false));
        Assert.NotNull(error);
        Assert.Contains($"4 object(s) within 64 m of (100, -40) have 2 prefab hash(es) this process cannot resolve: {hash} ({ServerPiece}) x3, first at (101.5, 43, -38); 12345 x1", error!.Message);
        var failed = Assert.Single(report.Steps, s => !s.Passed);
        Assert.Equal("first: the client resolves every prefab hash where the player stands", failed.Name);
        Assert.True(File.Exists(Path.Combine(_output, "first-vanilla-client-1.json"))); // Evidence is written before the failure.
        Assert.DoesNotContain(report.Steps, s => s.Name.Contains("logs")); // Nothing after the first failure.
    }

    [Theory]
    [InlineData("[Warning: Unity Log] 09/29/2026 12:00:00: Failed to find rpc method 1234567890\n", "rpc-method-missing x1")]
    [InlineData("[Warning: Unity Log] 09/29/2026 12:00:00: Missing prefab hash: -887680680\n", "missing-prefab-hash x1")]
    [InlineData("[Error  : Unity Log] NullReferenceException: Object reference not set to an instance of an object\nStack trace:\n" +
        "ZNetScene.RemoveObjects (System.Collections.Generic.List`1[T] a, System.Collections.Generic.List`1[T] b) (at <c0ffee>:0)\n\n", "nre-remove-objects x1")]
    public void EachQuietClientFailureInTheLogFailsTheCheck(string line, string named)
    {
        var (client, server) = Scripted();
        var (report, error) = Run(client, server, Check(Log(line), points: false));
        Assert.NotNull(error);
        Assert.Contains("client BepInEx log: " + named + ", first at line 2", error!.Message);
        Assert.False(report.Passed);
    }

    [Fact] public void LogClassificationsMakeTheQuietFailuresFailTheTeardownScan()
    {
        LogScanner.CheckClassifications(VanillaClientCheck.LogClassifications);
        var scan = LogScanner.Scan(new RunLog("client", Log("[Warning: Unity Log] Failed to find rpc method 42\n")), VanillaClientCheck.LogClassifications);
        Assert.True(scan.Failed);
        Assert.False(LogScanner.Scan(new RunLog("client", Log("[Warning: Unity Log] Failed to find rpc method 42\n"))).Failed); // A warning by default.
        Assert.Equal(VanillaClientCheck.LogPatterns.Order(), VanillaClientCheck.LogClassifications.Keys.Order());
        Assert.All(VanillaClientCheck.LogPatterns, pattern => Assert.Contains(pattern, LogScanner.Names));
    }

    [Fact] public void ACensusIsJudgedOnlyWhenCompleteAndNonEmpty()
    {
        var incomplete = UnresolvedPrefabs.Parse(Json(Scan(false)));
        Assert.Contains("4 of 9 zones loaded", Assert.Throws<InvalidOperationException>(() => incomplete.RequireNone()).Message);
        var empty = UnresolvedPrefabs.Parse(Json(new { source = "unresolved-prefabs", complete = true, x = 0f, z = 0f, radius = 64f, zones = 4, zonesLoaded = 4, scanned = 0, withoutPrefab = 0, unresolved = Array.Empty<object>() }));
        Assert.Contains("read no objects", Assert.Throws<InvalidOperationException>(() => empty.RequireNone()).Message);
        UnresolvedPrefabs.Parse(Json(Scan(true))).RequireNone();
        Assert.Throws<InvalidOperationException>(() => UnresolvedPrefabs.Parse(Json(Scan(true, (0, 1)))));
        Assert.Throws<InvalidOperationException>(() => UnresolvedPrefabs.Parse(Json(Scan(true, (5, 1), (5, 2)))));
        Assert.Throws<InvalidOperationException>(() => UnresolvedPrefabs.Parse(Json(Scan(true, (5, 500)))));
        Assert.Throws<InvalidOperationException>(() => UnresolvedPrefabs.Parse(Json(new { source = "other", complete = true })));
    }

    [Fact] public void StableHashMatchesTheGamesByHand()
    {
        // "" never enters the loop: 5381 + 5381 * 1566083941 (mod 2^32). "a": ((5381 << 5) + 5381) ^ 97 = 177604 for the first
        // accumulator, and the second stays 5381.
        Assert.Equal(371857150, StableHash.Of(""));
        Assert.Equal(372029373, StableHash.Of("a"));
        Assert.Equal(StableHash.Of("ab"), StableHash.Of("ab\0cd")); // The game stops at a NUL.
        Assert.Equal(ServerPiece, StableHash.Name(StableHash.Of(ServerPiece), ["other", ServerPiece]));
        Assert.Null(StableHash.Name(1, ["other"]));
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
