using System.Globalization;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using valheim_cli.Testing;

namespace ExampleMod.Tests;

/// <summary>
/// A tiny scripted game for the scenario: a server whose saved objects hold the markers the mod places, a restart that
/// keeps only what a confirmed save kept, and a client that sees the server's markers once joined. It models the
/// contracts the scenario relies on, not Valheim: replies, observations and their completeness.
/// </summary>
public sealed class TestWorld : IOwnedServer
{
    public const string WorldUid = "4242";
    private readonly List<(float X, float Z)> _markers = [], _saved = [];
    public int Restarts, MarkCommands;
    public bool LoseMarkReply, ConfirmSaves = true, ClientSeesMarkers = true;
    public ScriptedTransport? ClientTransport { get; private set; }

    public MarkerPlan Plan() => new()
    {
        Scenario = MarkerPlan.ScenarioName,
        Pins = new() { ["worlduid"] = WorldUid },
        DrySite = new() { X = 100, Z = -40, Ground = 42.5f },
        WetSite = new() { X = 400, Z = 300, Ground = 22f },
        Arrival = new() { X = 105, Z = -40, Ground = 42.3f },
        Client = new()
        {
            Mode = "owned", Install = Path.GetFullPath("client-install"), Port = 5556, Join = "127.0.0.1:2456", Character = "Tester",
            Pins = new() { ["valheimCLI.valheimCLI"] = new string('a', 32), [MarkerPlan.ModPlugin] = "absent" },
        },
    };

    public GameActor Server() => new ScriptedTransport()
        .OnPrefix("examplemod_mark ", command =>
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
                $"ZDO {MarkerScenario.Marker} id=1:2 pos={m.X:F3},42.250,{m.Z:F3} rot=0.00,0.00,0.00 quat=0,0,0,1 scale=- persistent=True owner=0")).ToList();
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"OK: ZDOS_AT {x:F1},{z:F1} r=8.0 zones=1 objects={lines.Count}"));
            return ScriptedTransport.Ok([.. lines]);
        })
        .On("cli_save", _ =>
        {
            if (!ConfirmSaves) return ScriptedTransport.Failed("ERROR: save failed");
            _saved.Clear(); _saved.AddRange(_markers); return ScriptedTransport.Ok("OK: SAVE saveNumber=2");
        })
        .On("cli_peers", _ => ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0"))
        .OnPrefix("cli_teleport_peer ", _ => ScriptedTransport.Ok("OK: asked peer 1 to teleport"))
        .Extension("examplemod.testing", "session", _ => new { source = "owned-test-session", complete = true, acceptingConnections = true })
        .Actor("server", "cli_expect worlduid=" + WorldUid);

    /// <summary>As an owned server's session: the scripted server's adapter reports whether it accepts game connections.</summary>
    public void WaitUntilJoinable(GameActor server) => OwnedServerSession.WaitUntilJoinable(server, "examplemod.testing/session", TimeSpan.FromSeconds(5));

    /// <summary>What an owned server's restart does to the world: only saved objects come back.</summary>
    public GameActor Restart()
    {
        Restarts++;
        _markers.Clear(); _markers.AddRange(_saved);
        return Server();
    }

    public ScriptedTransport Client(MarkerPlan plan)
    {
        bool acknowledged = false, devcommands = false, joined = false;
        var arrival = plan.Arrival;
        object Support(float x, float y, float z) => new
        {
            source = "local-player-support", complete = true, x, y, z, speed = 0f,
            grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres",
        };
        return ClientTransport = new ScriptedTransport()
            .On("devcommands", _ => ScriptedTransport.Ok("Dev commands: " + (devcommands = !devcommands)))
            .On("cli_access", _ => ScriptedTransport.Ok("ACCESS " + JsonSerializer.Serialize(new { schemaVersion = 1, complete = true,
                devcommands, cheatsAcknowledged = acknowledged, allowOnServerClients = true, server = false, dedicated = false,
                joinedClient = joined, localPlayer = joined, profileAvailable = joined })))
            .On("cli_acknowledge_local_cheats", _ => { acknowledged = true; return ScriptedTransport.Ok("OK: localCharacterCheated=True"); })
            .Extension("valheim.session", "join", _ => { joined = true; return new { source = "session-join", complete = true, action = "join" }; }, readOnly: false)
            .Extension("valheim.session", "leave", _ => { joined = false; return new { source = "session-leave", complete = true, action = "leave" }; }, readOnly: false)
            .Extension("valheim.session", "state", _ => new
            {
                source = "session-state", complete = true, phase = joined ? "world-present" : "menu", worldUid = joined ? WorldUid : null, worldPresent = joined,
                worldReady = joined, server = false, dedicated = false, localPlayer = joined, playerReady = joined, saving = false, loadError = false,
                connectionStatus = joined ? "Connected" : "None",
            })
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True ghostReplicated=True"))
            .OnPrefix("cli_skip_intro", _ => ScriptedTransport.Ok("OK: skipped=False profileFirstSpawn=False position=0,40,0 ms=3"))
            .Extension("valheim.world", "player-support", _ => Support(0f, 40f, 0f))
            .ArrivalSignals(() => Support(arrival.X, arrival.Ground, arrival.Z))
            .OnPrefix("cli_prefabs_at ", command =>
            {
                var (x, z) = Coordinates(command, 1, 3);
                var lines = (ClientSeesMarkers ? _markers.Where(m => Near(m, x, z)) : []).Select(m => string.Create(CultureInfo.InvariantCulture,
                    $"PREFAB name={MarkerScenario.Marker} distance=0.20 pos={m.X:F3},42.250,{m.Z:F3} rot=0.000,0.000,0.000 zdo=1:2 owner=0")).ToList();
                lines.Add($"OK: NEARBY_PREFABS radius=8.0 count={lines.Count}");
                return ScriptedTransport.Ok([.. lines]);
            });
    }

    /// <summary>The adapter's census of ExampleMod's patch as a server with the mod reports it.</summary>
    public static object ModCensus() => new
    {
        source = "harmony-patches", complete = true, owner = MarkerPlan.ModPlugin,
        methods = new object[]
        {
            new
            {
                method = "Terminal::InitTerminal()",
                patches = new[] { new { owner = MarkerPlan.ModPlugin, kind = "postfix", priority = 400, index = 0, before = Array.Empty<string>(), after = Array.Empty<string>(), patch = "ExampleMod.Plugin+RegisterCommands::Postfix()" } },
            },
        },
    };

    private static bool Near((float X, float Z) marker, float x, float z) => MathF.Abs(marker.X - x) <= 8 && MathF.Abs(marker.Z - z) <= 8;
    private static (float X, float Z) Coordinates(string command, int first, int? second = null)
    {
        var words = command.Split(' ');
        return (float.Parse(words[first], CultureInfo.InvariantCulture), float.Parse(words[second ?? first + 1], CultureInfo.InvariantCulture));
    }
}
