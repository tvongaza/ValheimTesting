using System.Globalization;
using System.Text.Json;
using MyMod.SystemTests;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using valheim_cli.Testing;

namespace MyMod.IntegrationTests;

/// <summary>
/// A scripted game for the native campaign's scenarios: an owned server whose saved objects, global keys and config
/// survive only what a confirmed save kept (the config file is written at once), and clients that join it, move where the
/// server teleports them, load and unload zones by distance, keep a character file in a temporary characters_local folder
/// and run MyMod (or not). Switches plant each failure and each control. It models the contracts the scenarios rely on
/// (replies, observations, their completeness), not Valheim. No game, Steam or network connection.
/// </summary>
internal sealed class CampaignWorld : IDisposable
{
    public const string WorldUid = "4242", Md5Mod = "11111111111111111111111111111111", Md5Adapter = "22222222222222222222222222222222",
        Md5Cli = "33333333333333333333333333333333", Md5Mismatched = "44444444444444444444444444444444", PlayFabId = "ENTITY42";
    public static readonly Site Dry = new() { X = 100, Z = -40, Ground = 42.5f }, Wet = new() { X = 400, Z = 300, Ground = 22f },
        Arrival = new() { X = 105, Z = -40, Ground = 42.3f }, Away = new() { X = 420, Z = -40, Ground = 36f };
    public readonly string Root = Directory.CreateTempSubdirectory("mymod-campaign-").FullName;
    public string Output => Path.Combine(Root, "out");
    public string Characters => Path.Combine(Root, "characters_local");
    public string ProfileFile => Path.Combine(Characters, "tester.fch");
    public string ServerLog => Path.Combine(Root, "server-LogOutput.log");
    public string ClientLog => Path.Combine(Root, "client-LogOutput.log");

    // What the client runs and how it behaves.
    public bool ClientHasMod = true, SyncBroken, KeepFieldAcrossReload, SuppressSave, CloudCharacter, ClientSeesMarkers = true;
    // The server's installed control and fixture state.
    public bool ControlMissingTarget, ControlPatchApplied, ControlServerOnlyPrefab, OversizedRoom, NoDungeon, Crossplay, RefusalSucceeds;
    public string RefusalStatus = "ErrorVersion";

    private readonly List<(float X, float Z, string Label)> _markers = [], _savedMarkers = [];
    private readonly List<(float X, float Z)> _controlObjects = [];
    private List<string> _keys = [], _savedKeys = [];
    private string _serverGreeting = "hello", _clientGreeting = "hello";
    private Dictionary<string, string> _live = [], _saved = [];
    private string? _field;
    private float _x = 0, _y = 40, _z = 0;
    private bool _joined;
    private int _writes;
    public int Restarts, MarkCommands, GreetingCommands, SpawnCommands, FieldSets, Leaves;
    public List<ScriptedTransport> Servers { get; } = [];
    public List<ScriptedTransport> Clients { get; } = [];

    /// <summary>An object of a prefab only the server registers, as a server-side mod's own prefab would be.</summary>
    public void PlaceServerOnlyObject(float x, float z) => _controlObjects.Add((x, z));

    /// <summary>A global key the fixture world already has, saved with it.</summary>
    public void PresetKey(string key) { _keys.Add(key); _savedKeys.Add(key); }

    public CampaignWorld()
    {
        Directory.CreateDirectory(Output); Directory.CreateDirectory(Characters);
        File.WriteAllText(ProfileFile, "profile as the last session left it");
        File.WriteAllText(ClientLog, "[Info   :   BepInEx] Chainloader startup complete\n");
    }
    public void Dispose() => Directory.Delete(Root, recursive: true);

    /// <summary>Call after setting the switches: the server's boot log, as the game and the controls write it.</summary>
    public void WriteServerLog()
    {
        var lines = new List<string> { "[Info   :   BepInEx] Chainloader startup complete" };
        if (ControlMissingTarget && !ControlPatchApplied)
            lines.Add("[Error  : Unity Log] HarmonyException: Patching exception in method null ---> System.ArgumentException: Undefined target method for patch method static System.Void MyMod.Controls.MissingHarmonyTarget.Plugin+PatchMissingMethod::Postfix()");
        if (Crossplay) lines.Add($"[Info   : Unity Log] Created PlayFab lobby with ID \"L1\", ConnectionString \"c\" and owned by \"{PlayFabId}\"");
        File.WriteAllLines(ServerLog, lines);
    }

    public static ClientRunPlan ClientPlan(string mod = Md5Mod, int port = 5556, bool crossplay = false) => new()
    {
        Mode = "attach", Port = port, Join = crossplay ? "" : "127.0.0.1:2456", Character = "Tester", Crossplay = crossplay, JoinSeconds = 2, ArrivalSeconds = 2,
        Pins = new() { ["valheimCLI.valheimCLI"] = Md5Cli, [LifecyclePlan.ModPlugin] = mod, [LifecyclePlan.AdapterPlugin] = Md5Adapter },
    };

    public LifecyclePlan Plan(string scenario, string? expectFailure = null)
    {
        var plan = new LifecyclePlan
        {
            Scenario = scenario, ExpectFailure = expectFailure,
            Pins = new() { ["worlduid"] = WorldUid, ["valheimCLI.valheimCLI"] = Md5Cli, [LifecyclePlan.ModPlugin] = Md5Mod, [LifecyclePlan.AdapterPlugin] = Md5Adapter },
            Client = ClientPlan(scenario is LifecyclePlan.VanillaClientScenario or LifecyclePlan.CrossplayScenario ? "absent" : Md5Mod, crossplay: scenario == LifecyclePlan.CrossplayScenario),
        };
        if (plan.MarksSites) { plan.DrySite = Dry; plan.WetSite = Wet; plan.Arrival = Arrival; }
        if (scenario == LifecyclePlan.WorldScenario)
        {
            plan.Away = Away; plan.GlobalKey = "defeated_eikthyr"; plan.Dungeon = new() { X = 150, Z = -40 };
            plan.Logout = new() { CharactersDirectory = Characters, WriteSeconds = 1 };
        }
        if (scenario == LifecyclePlan.SyncedConfigScenario) plan.NewGreeting = "goodbye";
        if (scenario == LifecyclePlan.RefusedJoinScenario) plan.RefusedClient = ClientPlan(Md5Mismatched, port: 5557);
        if (scenario == LifecyclePlan.CrossplayScenario) plan.Crossplay = true;
        return plan;
    }

    /// <summary>A campaign run over this world: attached clients, the refused plan's client refused, evidence in <see cref="Output"/>.</summary>
    public CampaignRun Run(LifecyclePlan plan, ScenarioReport report, bool clientLog = false)
    {
        WriteServerLog();
        return new()
        {
            Plan = plan, Server = Server(), RestartServer = Restart, Report = report, Output = Output, SettleFor = TimeSpan.Zero, Interval = TimeSpan.FromMilliseconds(10),
            WaitUntilJoinable = server => OwnedServerSession.WaitUntilJoinable(server, "mymod.testing/session", TimeSpan.FromSeconds(5)),
            OpenClient = (client, directory) =>
            {
                string output = directory == null ? Output : Directory.CreateDirectory(Path.Combine(Output, directory)).FullName;
                return ClientSession.Attach(client, output, Client(refused: client == plan.RefusedClient));
            },
            ServerLog = () => ServerLog,
            ClientLog = _ => clientLog ? ClientLog : null,
            Lobby = server => CrossplayServer.WaitForLobby(server, ServerLog, TimeSpan.FromSeconds(5)),
        };
    }

    public GameActor Server()
    {
        bool devcommands = true;
        var transport = new ScriptedTransport()
            .On("devcommands", _ => ScriptedTransport.Ok("Dev commands: " + (devcommands = !devcommands)))
            .OnPrefix("mymod_mark ", command =>
            {
                MarkCommands++;
                var (x, z) = Coordinates(command, 1);
                if (x < 200) { _markers.Add((x, z, CampaignSteps.DryLabel)); return ScriptedTransport.Ok($"OK: marked {x} {z} ground=42.5"); }
                return ScriptedTransport.Ok($"REFUSED: {x} {z} ground=22.0 below 31.5");
            })
            .OnPrefix("mymodcontrol_spawn ", command =>
            {
                SpawnCommands++;
                if (!ControlServerOnlyPrefab) return ScriptedTransport.Ok("Unknown command mymodcontrol_spawn");
                var (x, z) = Coordinates(command, 1);
                _controlObjects.Add((x, z));
                return ScriptedTransport.Ok($"OK: spawned {ControlPlugins.ServerOnlyPrefabName} at {x} {z}");
            })
            .OnPrefix("mymod_greeting ", command =>
            {
                GreetingCommands++;
                _serverGreeting = command.Split(' ')[1]; // Saved to the server's config file at once, as BepInEx does.
                if (_joined && ClientHasMod && !SyncBroken) Receive(_serverGreeting);
                return ScriptedTransport.Ok("OK: greeting " + _serverGreeting);
            })
            .OnPrefix("cli_zdos_at ", command =>
            {
                var (x, z) = Coordinates(command, 1);
                var lines = _markers.Where(m => Near(m.X, m.Z, x, z, 8)).Select(m => Zdo(DrySiteScenario.Marker, m.X, m.Z))
                    .Concat(_controlObjects.Where(o => Near(o.X, o.Z, x, z, 8)).Select(o => Zdo(ControlPlugins.ServerOnlyPrefabName, o.X, o.Z))).ToList();
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"OK: ZDOS_AT {x:F1},{z:F1} r=8.0 zones=1 objects={lines.Count}"));
                return ScriptedTransport.Ok([.. lines]);
            })
            .On("cli_save", _ => { _savedMarkers.Clear(); _savedMarkers.AddRange(_markers); _savedKeys = [.. _keys]; return ScriptedTransport.Ok("OK: SAVE saveNumber=2"); })
            .On("cli_peers", _ => _joined ? ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character position=0.0,40.00,0.0 zone=0,0") : ScriptedTransport.Ok("OK: 0 peer(s)"))
            .OnPrefix("cli_teleport_peer ", command =>
            {
                var w = command.Split(' ');
                _x = F(w[2]); _y = F(w[3]) - .5f; _z = F(w[4]);
                return ScriptedTransport.Ok("OK: asked peer 1 to teleport");
            })
            .On("cli_multiplayer_identity", _ => ScriptedTransport.Ok(
                $"OK: steamId=0, playFabLoginState=LoggedIn, playFabId={PlayFabId}, backend={(Crossplay ? "PlayFab" : "Steamworks")}, gameState=World, connectionStatus=Connected, isServer=True, isOpenServer=True, server=fake"))
            .Extension("mymod.testing", "session", _ => new { source = "owned-test-session", complete = true, acceptingConnections = true })
            .Extension("mymod.testing", "harmony", args => args.Count == 1 && args[0] != LifecyclePlan.ModPlugin ? ControlCensus(args[0]) : TestWorld.ModCensus())
            .Extension("mymod.testing", "globalkeys", _ => new { source = "global-keys", complete = true, server = true, keys = _keys.Order(StringComparer.Ordinal).ToArray() })
            .Extension("mymod.testing", "globalkey", args =>
            {
                if (args[0] == "set") _keys.Add(args[1]); else _keys.RemoveAll(k => k == args[1] || k.StartsWith(args[1] + " ", StringComparison.Ordinal));
                return new { source = "global-key-change", complete = true, action = args[0], name = args[1], value = (string?)null, keys = _keys.ToArray() };
            }, readOnly: false)
            .Extension("mymod.testing", "config", args => Config(args, server: true, installed: true, _serverGreeting))
            .Extension("mymod.testing", "dungeon-rooms", _ => Dungeons());
        Servers.Add(transport);
        return transport.Actor("server", "cli_expect worlduid=" + WorldUid);
    }

    /// <summary>What <c>OwnedServerSession.Restart</c> does to the world: only saved objects and keys come back.</summary>
    public GameActor Restart()
    {
        Restarts++;
        _markers.Clear(); _markers.AddRange(_savedMarkers);
        _keys = [.. _savedKeys];
        return Server();
    }

    /// <summary>A client; a refused one runs another MyMod build, so the server refuses its join.</summary>
    public ScriptedTransport Client(bool refused = false)
    {
        bool devcommands = false, refusedOnce = false;
        var transport = new ScriptedTransport()
            .On("devcommands", _ => ScriptedTransport.Ok("Dev commands: " + (devcommands = !devcommands)))
            .OnPrefix("cli_extension valheim.session/join ", _ =>
            {
                if (refused && !RefusalSucceeds)
                {
                    refusedOnce = true;
                    return new CommandResult
                    {
                        Ok = false, ErrorCode = "join_failed",
                        Output = ["EXTENSION_RESULT " + JsonSerializer.Serialize(new { schemaVersion = 1, ok = false, extension = "valheim.session", instance = "fake", code = "join_failed", message = "refused", data = new { } })],
                    };
                }
                Join();
                return ScriptedTransport.Ok(ScriptedTransport.ExtensionResult("valheim.session", new { source = "session-join", complete = true, action = "join" }));
            })
            .Extension("valheim.session", "join", _ => throw new InvalidOperationException("answered by the prefix"), readOnly: false)
            .Extension("valheim.session", "leave", _ => { Leave(); return new { source = "session-leave", complete = true, action = "leave" }; }, readOnly: false)
            .Extension("valheim.session", "state", _ => new
            {
                source = "session-state", complete = true, phase = _joined ? "world-present" : "menu", worldUid = _joined ? WorldUid : null, worldPresent = _joined,
                worldReady = _joined, server = false, dedicated = false, localPlayer = _joined, playerReady = _joined, saving = false, loadError = false,
                connectionStatus = _joined ? "Connected" : refusedOnce ? RefusalStatus : "None",
            })
            .On("cli_connection_status", _ => ScriptedTransport.Ok($"OK: connectionStatus={(_joined ? "Connected" : refusedOnce ? RefusalStatus : "None")}, server=steam/0/127.0.0.1:2456"))
            .OnPrefix("cli_select_character ", _ => ScriptedTransport.Ok("OK: Selected character 'Tester' (tester, Local)"))
            .OnPrefix("cli_connect_playfab_user ", command =>
            {
                if (!Crossplay) return ScriptedTransport.Failed("ERROR: no crossplay lobby " + command);
                Join();
                return ScriptedTransport.Ok($"OK: PlayFab user join started for {command.Split(' ')[1]} using character 'Tester' (tester, Local)");
            })
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True"))
            .OnPrefix("cli_skip_intro", _ => ScriptedTransport.Ok("OK: skipped=False profileFirstSpawn=False position=0,40,0 ms=3"))
            .Extension("valheim.world", "player-support", _ => new
            {
                source = "local-player-support", complete = _joined, x = _x, y = _y, z = _z, speed = 0f,
                grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres",
            })
            .OnPrefix("cli_prefabs_at ", command =>
            {
                var (x, z) = Coordinates(command, 1, 3);
                var lines = (ClientSeesMarkers ? _markers.Where(m => Near(m.X, m.Z, x, z, 8) && Loaded(m.X, m.Z)) : Enumerable.Empty<(float X, float Z, string Label)>()).Select(m => string.Create(CultureInfo.InvariantCulture,
                    $"PREFAB name={DrySiteScenario.Marker} distance=0.20 pos={m.X:F3},42.250,{m.Z:F3} rot=0.000,0.000,0.000 zdo=1:2 owner=0")).ToList();
                lines.Add($"OK: NEARBY_PREFABS radius=8.0 count={lines.Count}");
                return ScriptedTransport.Ok([.. lines]);
            })
            .Extension("mymod.testing", "zones", Zones)
            .Extension("mymod.testing", "markers", args =>
            {
                float x = F(args[0]), z = F(args[1]), radius = F(args[2]);
                var markers = _markers.Where(m => Near(m.X, m.Z, x, z, radius)).Select(m => new { x = m.X, y = 42.25f, z = m.Z, label = m.Label, instance = Loaded(m.X, m.Z) }).ToArray();
                return new { source = "mymod-markers", complete = true, x, z, radius, markers };
            })
            .Extension("mymodcontrol.fieldstate", "set", args =>
            {
                FieldSets++;
                var markers = _markers.Where(m => Near(m.X, m.Z, F(args[0]), F(args[1]), 1.5f) && Loaded(m.X, m.Z)).ToArray();
                if (markers.Length > 0) _field = args[2];
                return new { source = "field-only-state", complete = true, markers = markers.Length, values = markers.Select(_ => _field).ToArray() };
            }, readOnly: false)
            .Extension("mymodcontrol.fieldstate", "read", args =>
            {
                var markers = _markers.Where(m => Near(m.X, m.Z, F(args[0]), F(args[1]), 1.5f) && Loaded(m.X, m.Z)).ToArray();
                return new { source = "field-only-state", complete = true, markers = markers.Length, values = markers.Select(_ => _field).ToArray() };
            })
            .Extension("mymod.testing", "custom-data", args => !_joined ? new { source = "local-player-custom-data", complete = false } : (object)new
            {
                source = "local-player-custom-data", complete = true, prefix = args.Count == 1 ? args[0] : null, character = "Tester", profileFile = "tester",
                fileSource = CloudCharacter ? "Cloud" : "Local", profilePath = ProfileFile,
                entries = _live.Where(e => args.Count == 0 || e.Key.StartsWith(args[0], StringComparison.Ordinal)).OrderBy(e => e.Key, StringComparer.Ordinal)
                    .Select(e => new { key = e.Key, value = e.Value }).ToArray(),
            })
            .OnPrefix("mymod_note ", command =>
            {
                if (!ClientHasMod) return ScriptedTransport.Ok("Unknown command mymod_note");
                _live[LifecycleWorldScenario.NoteKey] = command.Split(' ')[1];
                return ScriptedTransport.Ok("OK: note " + command.Split(' ')[1]);
            })
            .Extension("mymod.testing", "globalkeys", _ => new { source = "global-keys", complete = true, server = false, keys = (_joined ? _keys : new List<string>()).Order(StringComparer.Ordinal).ToArray() })
            .Extension("mymod.testing", "config", args => Config(args, server: false, installed: ClientHasMod, _clientGreeting))
            .Extension("mymod.testing", "unresolved-prefabs", args =>
            {
                float radius = F(args[0]);
                var near = _controlObjects.Where(o => Near(o.X, o.Z, _x, _z, radius) && Loaded(o.X, o.Z)).ToArray();
                var unresolved = near.Length == 0 ? Array.Empty<object>() : new object[] { new { hash = StableHash.Of(ControlPlugins.ServerOnlyPrefabName), count = near.Length, x = near[0].X, y = 42.25f, z = near[0].Z } };
                int scanned = 1 + _markers.Count(m => Near(m.X, m.Z, _x, _z, radius)) + near.Length; // The player's own object, the markers, the control's objects.
                return new { source = "unresolved-prefabs", complete = _joined, x = _x, z = _z, radius, zones = 4, zonesLoaded = _joined ? 4 : 0, scanned, withoutPrefab = 0, unresolved };
            });
        Clients.Add(transport);
        return transport;
    }

    private void Join()
    {
        _joined = true;
        _live = new(_saved); // The character's custom data as its file holds it.
        // A client starts from its own file; MyMod's sync then sends the server's value.
        _clientGreeting = "hello";
        if (ClientHasMod && !SyncBroken) Receive(_serverGreeting);
    }

    private void Leave()
    {
        Leaves++;
        // The game saves the character inside the logout, as a new file renamed over the old one.
        if (_joined && !SuppressSave)
        {
            _saved = new(_live);
            File.WriteAllText(ProfileFile + ".new", "profile save " + ++_writes + " " + string.Join(";", _saved.Select(e => e.Key + "=" + e.Value)));
            File.Move(ProfileFile, ProfileFile + ".old", overwrite: true);
            File.Move(ProfileFile + ".new", ProfileFile);
        }
        _joined = false;
    }

    private void Receive(string greeting)
    {
        _clientGreeting = greeting;
        File.AppendAllText(ClientLog, $"[Info   :MyMod (ValheimTesting example)] Greeting \"{greeting}\" received from 1 (client)\n");
    }

    // The client holds a zone within two rings of its player's zone (near 2, far 2), and nothing of it beyond four rings.
    private ZoneId PlayerZone => ZoneId.Of(_x, _z);
    private bool Loaded(float x, float z) => _joined && ZoneId.Of(x, z).Rings(PlayerZone) <= 2;
    private object Zones(IReadOnlyList<string> arguments)
    {
        var zones = arguments.Select(a => a.Split(',')).Select(p => new ZoneId(int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture))).Select(zone =>
        {
            int rings = zone.Rings(PlayerZone);
            bool loaded = rings <= 2, instances = rings <= 4;
            // The game recreates a zone's objects from their saved data: a component field starts empty.
            if (!instances && !KeepFieldAcrossReload && _markers.Any(m => ZoneId.Of(m.X, m.Z) == zone)) _field = null;
            int saved = _markers.Count(m => ZoneId.Of(m.X, m.Z) == zone);
            return new { x = zone.X, z = zone.Z, terrainLoaded = loaded, instances = instances ? saved + 2 : 0, nearInstances = loaded ? saved + 1 : 0, saved, withoutInstance = loaded ? 0 : saved };
        }).ToArray();
        return new { source = "zone-presence", complete = _joined, reference = new { x = PlayerZone.X, z = PlayerZone.Z }, simulation = new { near = 2, far = 2, classic = true }, zones };
    }

    private object Config(IReadOnlyList<string> args, bool server, bool installed, string value) => new
    {
        source = "bepinex-config", complete = true, guid = Uri.UnescapeDataString(args[0]), section = Uri.UnescapeDataString(args[1]), key = Uri.UnescapeDataString(args[2]),
        server, installed, found = installed, type = installed ? "System.String" : null, value = installed ? value : null, defaultValue = installed ? "hello" : null,
    };

    // The control's own Harmony ID: nothing applied (its target is missing), unless the control's patch did apply.
    private object ControlCensus(string owner) => new
    {
        source = "harmony-patches", complete = true, owner,
        methods = !ControlPatchApplied ? Array.Empty<object>() : new object[]
        {
            new
            {
                method = "Player::MyModControlMethodThatDoesNotExist()",
                patches = new[] { new { owner, kind = "postfix", priority = 400, index = 0, before = Array.Empty<string>(), after = Array.Empty<string>(), patch = "MyMod.Controls.MissingHarmonyTarget.Plugin+PatchMissingMethod::Postfix()" } },
            },
        },
    };

    // One crypt at (150, -40), zone (2,-1), its generator 5000 m up; two rooms inside the zone, or one reaching past its edge.
    private object Dungeons()
    {
        var rooms = new List<(int Hash, float X, float Z)> { (StableHash.Of("sunkencrypt_Hall"), 140, -50), (StableHash.Of("sunkencrypt_Room"), OversizedRoom ? 175 : 150, -60) };
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(rooms.Count);
            foreach (var room in rooms) { writer.Write(room.Hash); writer.Write(room.X); writer.Write(5030f); writer.Write(room.Z); writer.Write(0f); writer.Write(0f); writer.Write(0f); }
        }
        object[] dungeons = NoDungeon ? Array.Empty<object>() : new object[]
        {
            new
            {
                prefab = "DG_SunkenCrypt", uid = "5:6", x = 150f, y = 5030f, z = -40f, zoneX = 2, zoneZ = -1, customInterior = false, format = "roomData",
                roomData = Convert.ToBase64String(bytes.ToArray()), rooms = (int?)null, legacyRooms = Array.Empty<object>(),
                location = new { prefab = "SunkenCrypt4", x = 150f, y = 30f, z = -40f, zoneX = 2, zoneZ = -1 },
            },
        };
        return new { source = "dungeon-rooms", complete = true, x = 150f, z = -40f, radius = 64f, zoneSize = 64f, dungeons };
    }

    private static string Zdo(string name, float x, float z) =>
        string.Create(CultureInfo.InvariantCulture, $"ZDO {name} id=1:2 pos={x:F3},42.250,{z:F3} rot=0.00,0.00,0.00 quat=0,0,0,1 scale=- persistent=True owner=0");
    private static bool Near(float x, float z, float cx, float cz, float radius) => MathF.Abs(x - cx) <= radius && MathF.Abs(z - cz) <= radius;
    private static float F(string text) => float.Parse(text, CultureInfo.InvariantCulture);
    private static (float X, float Z) Coordinates(string command, int first, int? second = null)
    {
        var words = command.Split(' ');
        return (F(words[first]), F(words[second ?? first + 1]));
    }
}
