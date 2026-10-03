using System.Text.Json;
using System.Globalization;
using MyMod.SystemTests;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using valheim_cli.Testing;

namespace MyMod.IntegrationTests;

/// <summary>
/// A tiny scripted game for the scenario: a server whose saved objects hold the markers the mod places, a restart that
/// keeps only what a confirmed save kept, and a client that sees the server's markers once joined. It models the
/// contracts the scenario relies on, not Valheim: replies, observations and their completeness.
/// </summary>
internal sealed class TestWorld
{
    public const string WorldUid = "4242";
    private readonly List<(float X, float Z)> _markers = [], _saved = [];
    public int Restarts, MarkCommands;
    public bool LoseMarkReply, OmitServerSummary, ConfirmSaves = true, ClientSeesMarkers = true, ConfirmProtection = true, PatchMissing;
    /// <summary>How many session readings report the socket closed before it opens (a first boot's late socket).</summary>
    public int ClosedReadings;
    public int SessionReadings;
    public List<ScriptedTransport> Servers { get; } = [];
    public ScriptedTransport? ClientTransport { get; private set; }

    public LifecyclePlan Plan(string mode = "owned") => new()
    {
        Scenario = "dry-site-lifecycle",
        Pins = new() { ["worlduid"] = WorldUid },
        DrySite = new() { X = 100, Z = -40, Ground = 42.5f },
        WetSite = new() { X = 400, Z = 300, Ground = 22f },
        Arrival = new() { X = 105, Z = -40, Ground = 42.3f },
        Client = new()
        {
            Mode = mode, Install = mode == "owned" ? Path.GetFullPath("client-install") : "", Port = 5556, Join = "127.0.0.1:2456", Character = "Tester",
            Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32), [LifecyclePlan.ModPlugin] = "absent" },
        },
    };

    public GameActor Server()
    {
        var transport = new ScriptedTransport()
            .OnPrefix("mymod_mark ", command =>
            {
                MarkCommands++;
                if (LoseMarkReply) return new CommandResult { Ok = false, ErrorCode = "timeout", Message = "reply lost" };
                var (x, z) = Coordinates(command, 1);
                if (x < 200) { _markers.Add((x, z)); return ScriptedTransport.Ok($"OK: marked {x} {z} ground=42.5"); }
                return ScriptedTransport.Ok($"REFUSED: {x} {z} ground=22.0 below 31.5");
            })
            .OnPrefix("cli_zdos_at ", command =>
            {
                var (x, z) = Coordinates(command, 1);
                var lines = _markers.Where(m => Near(m, x, z)).Select(m => string.Create(CultureInfo.InvariantCulture,
                    $"ZDO {DrySiteScenario.Marker} id=1:2 pos={m.X:F3},42.250,{m.Z:F3} rot=0.00,0.00,0.00 quat=0,0,0,1 scale=- persistent=True owner=0")).ToList();
                if (!OmitServerSummary) lines.Add(string.Create(CultureInfo.InvariantCulture, $"OK: ZDOS_AT {x:F1},{z:F1} r=8.0 zones=1 objects={lines.Count}"));
                return ScriptedTransport.Ok([.. lines]);
            })
            .On("cli_save", _ =>
            {
                if (!ConfirmSaves) return ScriptedTransport.Failed("ERROR: save failed");
                _saved.Clear(); _saved.AddRange(_markers); return ScriptedTransport.Ok("OK: SAVE saveNumber=2");
            })
            .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
            .OnPrefix("cli_teleport_peer ", _ => ScriptedTransport.Ok("OK: asked peer 1 to teleport"))
            .Extension("mymod.testing", "session", _ => new { source = "owned-test-session", complete = true, acceptingConnections = ++SessionReadings > ClosedReadings })
            .Extension("mymod.testing", "harmony", _ => Census());
        Servers.Add(transport);
        return transport.Actor("server", "cli_expect worlduid=" + WorldUid);
    }

    /// <summary>What <c>OwnedServerSession.Restart</c> does to the world: only saved objects come back.</summary>
    public GameActor Restart()
    {
        Restarts++;
        _markers.Clear(); _markers.AddRange(_saved);
        return Server();
    }

    public ScriptedTransport Client(LifecyclePlan plan)
    {
        bool acknowledged = false;
        bool devcommands = false, joined = false;
        var arrival = plan.Arrival;
        return ClientTransport = new ScriptedTransport()
            .On("devcommands", _ => ScriptedTransport.Ok("Dev commands: " + (devcommands = !devcommands)))
            .On("cli_access", _ => ScriptedTransport.Ok("ACCESS " + JsonSerializer.Serialize(new { schemaVersion = 1, complete = true,
                devcommands, cheatsAcknowledged = acknowledged, allowOnServerClients = true, server = false, dedicated = false,
                joinedClient = joined && !false, localPlayer = joined, profileAvailable = joined })))
            .On("cli_acknowledge_local_cheats", _ => { acknowledged = true; return ScriptedTransport.Ok("OK: localCharacterCheated=True"); })
            .Extension("valheim.session", "join", _ => { joined = true; return new { source = "session-join", complete = true, action = "join" }; }, readOnly: false)
            .Extension("valheim.session", "leave", _ => { joined = false; return new { source = "session-leave", complete = true, action = "leave" }; }, readOnly: false)
            .Extension("valheim.session", "state", _ => new
            {
                source = "session-state", complete = true, phase = joined ? "world-present" : "menu", worldUid = joined ? WorldUid : null, worldPresent = joined,
                worldReady = joined, server = false, dedicated = false, localPlayer = joined, playerReady = joined, saving = false, loadError = false,
                connectionStatus = joined ? "Connected" : "None",
            })
            // ValheimCLI's reply reads each mode back; one that did not take makes it an error line.
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok(ConfirmProtection
                ? "OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True"
                : "ERROR: code=safety_not_applied playerSafety enabled=True god=True ghost=False debugMode=True cheats=True"))
            .OnPrefix("cli_skip_intro", _ => ScriptedTransport.Ok("OK: skipped=False profileFirstSpawn=False position=0,40,0 ms=3"))
            .Extension("valheim.world", "player-support", _ => new
            {
                source = "local-player-support", complete = true, x = arrival.X, y = arrival.Ground, z = arrival.Z, speed = 0f,
                grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres",
            })
            .OnPrefix("cli_prefabs_at ", command =>
            {
                var (x, z) = Coordinates(command, 1, 3);
                var lines = (ClientSeesMarkers ? _markers.Where(m => Near(m, x, z)) : Enumerable.Empty<(float X, float Z)>()).Select(m => string.Create(CultureInfo.InvariantCulture,
                    $"PREFAB name={DrySiteScenario.Marker} distance=0.20 pos={m.X:F3},42.250,{m.Z:F3} rot=0.000,0.000,0.000 zdo=1:2 owner=0")).ToList();
                lines.Add($"OK: NEARBY_PREFABS radius=8.0 count={lines.Count}");
                return ScriptedTransport.Ok([.. lines]);
            });
    }

    // The adapter's census, filtered to the mod: its postfix on the terminal's command setup (unless its target went
    // missing, when HarmonyX applies nothing there) and another mod's prefix on the same method, and its handshake and
    // greeting patches on ZNet.
    private object Census() => ModCensus(PatchMissing);

    /// <summary>The adapter's census of MyMod's patches as a server or client with MyMod reports it; none when <paramref name="missing"/>.</summary>
    public static object ModCensus(bool missing = false) => new
    {
        source = "harmony-patches", complete = true, owner = LifecyclePlan.ModPlugin,
        methods = missing ? Array.Empty<object>() : new object[]
        {
            new
            {
                method = "Terminal::InitTerminal()",
                patches = new[]
                {
                    new { owner = "other.mod", kind = "prefix", priority = 400, index = 0, before = Array.Empty<string>(), after = Array.Empty<string>(), patch = "Other.Hooks::Prefix()" },
                    ModPatch("postfix", "MyMod.Plugin+RegisterCommands::Postfix()"),
                },
            },
            new { method = "ZNet::Awake()", patches = new[] { ModPatch("postfix", "MyMod.SyncedGreeting+RegisterRpc::Postfix(ZNet)") } },
            new { method = "ZNet::OnNewConnection(ZNetPeer)", patches = new[] { ModPatch("prefix", "MyMod.VersionHandshake+SendVersion::Prefix(ZNet,ZNetPeer)") } },
            new { method = "ZNet::RPC_PeerInfo(ZRpc,ZPackage)", patches = new[] { ModPatch("prefix", "MyMod.VersionHandshake+RefuseMismatched::Prefix(ZNet,ZRpc)") } },
        },
    };

    private static object ModPatch(string kind, string patch) =>
        new { owner = LifecyclePlan.ModPlugin, kind, priority = 400, index = 0, before = Array.Empty<string>(), after = Array.Empty<string>(), patch };

    private static bool Near((float X, float Z) marker, float x, float z) => MathF.Abs(marker.X - x) <= 8 && MathF.Abs(marker.Z - z) <= 8;
    private static (float X, float Z) Coordinates(string command, int first, int? second = null)
    {
        var words = command.Split(' ');
        return (float.Parse(words[first], CultureInfo.InvariantCulture), float.Parse(words[second ?? first + 1], CultureInfo.InvariantCulture));
    }
}

/// <summary>An owned client process for <see cref="ClientSession.Launch(ClientRunPlan, string, Func{IServerProcess}, Func{IGameTransport}, Func{TimeSpan, CancellationToken, Task}, CancellationToken)"/>.</summary>
internal sealed class FakeClientProcess(int? exitDuringStartup = null) : IServerProcess
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Stops { get; private set; }
    public int Id => 7331;
    public bool HasExited => _exit.Task.IsCompleted;
    public Task<int> WaitForExitAsync(CancellationToken cancellation)
    {
        if (exitDuringStartup is int code) _exit.TrySetResult(code);
        return _exit.Task.WaitAsync(cancellation);
    }
    public void Stop(TimeSpan timeout) { Stops++; _exit.TrySetResult(-1); }
    public void Dispose() { }
}
