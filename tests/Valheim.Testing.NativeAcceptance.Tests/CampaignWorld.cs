using System.Globalization;
using System.Text.Json;
using Valheim.Testing.NativeAcceptance;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using valheim_cli.Testing;
using Valheim.Testing.GameSessions;
using Valheim.Testing.GameSessions.Fakes;

namespace Valheim.Testing.NativeAcceptance.Tests;

/// <summary>
/// A scripted game for the native campaign's scenarios: an owned server whose saved objects, global keys and config
/// survive only what a confirmed save kept (the config file is written at once), and clients that join it, move where the
/// server teleports them, load and unload zones by distance, keep a character file in a temporary characters_local folder
/// and run AcceptanceMod (or not). Switches plant each failure and each control. It models the contracts the scenarios rely on
/// (replies, observations, their completeness), not Valheim. No game, Steam or network connection.
/// </summary>
internal sealed class CampaignWorld : IOwnedServer, IDisposable
{
    public const string WorldUid = "4242", Md5Mod = "11111111111111111111111111111111", Md5Adapter = "22222222222222222222222222222222",
        Md5Cli = "33333333333333333333333333333333", Md5Mismatched = "44444444444444444444444444444444", PlayFabId = "ENTITY42";
    public static readonly Site Dry = new() { X = 100, Z = -40, Ground = 42.5f }, Wet = new() { X = 400, Z = 300, Ground = 22f },
        Arrival = new() { X = 105, Z = -40, Ground = 42.3f }, Away = new() { X = 420, Z = -40, Ground = 36f };
    public readonly string Root = Directory.CreateTempSubdirectory("acceptancemod-campaign-").FullName;
    public void MoveClient(float x, float y, float z) { _x = x; _y = y; _z = z; }
    public string Output => Path.Combine(Root, "out");
    public string Characters => Path.Combine(Root, "characters_local");
    public string ProfileFile => Path.Combine(Characters, "tester.fch");
    public string ServerLog => Path.Combine(Root, "server-LogOutput.log");
    public string ClientLog => Path.Combine(Root, "client-LogOutput.log");

    // What the client runs and how it behaves.
    public bool ClientHasMod = true, SyncBroken, KeepFieldAcrossReload, SuppressSave, CloudCharacter, ClientSeesMarkers = true;
    // The server's installed control and fixture state.
    public bool ControlMissingTarget, ControlPatchApplied, ControlPatchAllThrew, ControlWarningMissing, OtherPluginLookupWarning, ControlServerOnlyPrefab, OversizedRoom, NoDungeon, Crossplay, RefusalSucceeds;
    public string RefusalStatus = "ErrorVersion";
    // The content census: AcceptanceMod built without its recipe (on both sides, as the control's build), an undeclared item of
    // AcceptanceMod's, and a client that reports being the server.
    public bool OmitRecipe, OmitStatusEffect, ExtraItem, ClientCensusSaysServer;

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
        var lines = new List<string>();
        // Another plugin probing an optional member the same way, logged before the control's warning.
        if (OtherPluginLookupWarning) lines.Add("[Warning:  HarmonyX] AccessTools.Field: Could not find field for type Terminal and name m_someOptionalField");
        if (ControlMissingTarget && !ControlPatchApplied && !ControlWarningMissing)
            // As the 1.0.16 dedicated server logged it (BepInEx 5.4.23.5, HarmonyX 2.9.0): one warning, no error line.
            lines.Add("[Warning:  HarmonyX] AccessTools.DeclaredMethod: Could not find method for type Player and name AcceptanceModControlMethodThatDoesNotExist and parameters ");
        // The control's line after PatchAll, there only if PatchAll returned.
        if (ControlMissingTarget && !ControlPatchAllThrew) lines.Add("[Info   :AcceptanceMod control: missing Harmony target (ValheimTesting acceptance suite)] MissingHarmonyTarget: PatchAll returned");
        lines.Add("[Info   :   BepInEx] Chainloader startup complete");
        if (Crossplay) lines.Add($"[Info   : Unity Log] Created PlayFab lobby with ID \"L1\", ConnectionString \"c\" and owned by \"{PlayFabId}\"");
        File.WriteAllLines(ServerLog, lines);
    }

    public static ClientRunPlan ClientPlan(string mod = Md5Mod, int port = 5556, bool crossplay = false) => new()
    {
        Mode = "attach", Port = port, Join = crossplay ? "" : "127.0.0.1:2456", Character = "Tester", Crossplay = crossplay, JoinSeconds = 2, ArrivalSeconds = 2,
        Pins = new() { ["valheimCLI.valheimCLI"] = Md5Cli, [AcceptancePlan.ModPlugin] = mod, [AcceptancePlan.AdapterPlugin] = Md5Adapter },
    };

    public AcceptancePlan Plan(string scenario, string? expectFailure = null)
    {
        var plan = new AcceptancePlan
        {
            Scenario = scenario, ExpectFailure = expectFailure,
            Pins = new() { ["worlduid"] = WorldUid, ["valheimCLI.valheimCLI"] = Md5Cli, [AcceptancePlan.ModPlugin] = Md5Mod, [AcceptancePlan.AdapterPlugin] = Md5Adapter },
            Client = ClientPlan(scenario is AcceptancePlan.VanillaClientScenario or AcceptancePlan.CrossplayScenario ? "absent" : Md5Mod, crossplay: scenario == AcceptancePlan.CrossplayScenario),
        };
        if (plan.MarksSites) { plan.DrySite = Dry; plan.WetSite = Wet; plan.Arrival = Arrival; }
        if (scenario == AcceptancePlan.WorldScenario)
        {
            plan.Away = Away; plan.GlobalKey = "defeated_eikthyr"; plan.Dungeon = new() { X = 150, Z = -40 };
            plan.Logout = new() { CharactersDirectory = Characters, WriteSeconds = 1 };
        }
        if (scenario == AcceptancePlan.SyncedConfigScenario) plan.NewGreeting = "goodbye";
        if (scenario == AcceptancePlan.RefusedJoinScenario) plan.RefusedClient = ClientPlan(Md5Mismatched, port: 5557);
        if (scenario == AcceptancePlan.CrossplayScenario) plan.Crossplay = true;
        return plan;
    }

    /// <summary>
    /// A started session over this world (<see cref="FakeGameSession"/>): its owned server is this world, clients attach (the
    /// refused plan's client refused), a campaign's named clients open through <paramref name="campaignClient"/>, evidence in
    /// <see cref="Output"/>.
    /// </summary>
    public GameSession Run(AcceptancePlan plan, ScenarioReport report, bool clientLog = false,
        Func<ClientRunPlan, string, ClientSession>? campaignClient = null, CancellationToken cancellation = default)
    {
        WriteServerLog();
        var session = FakeGameSession.Create(report, Output, WorldUid, this, Server,
            (client, name, output) => name is "client-a" or "client-b"
                ? (campaignClient ?? throw new InvalidOperationException("No campaign client was supplied."))(client, name)
                : ClientSession.Attach(client, output, Client(refused: client == plan.RefusedClient)),
            serverLog: ServerLog, clientLog: _ => clientLog ? ClientLog : null,
            lobby: server => CrossplayServer.WaitForLobby(server, ServerLog, TimeSpan.FromSeconds(5)), cancellation: cancellation);
        session.StartAsync().GetAwaiter().GetResult();
        return session;
    }

    /// <summary>Runs <paramref name="plan"/>'s scenario from the suite's table on a started session over this world.</summary>
    public void RunScenario(AcceptancePlan plan, ScenarioReport report, bool clientLog = false)
    {
        var session = Run(plan, report, clientLog);
        // Disposed as the runner does, also after a failure: clients left open close before the server stops.
        try { ScenarioTable.Run(session, plan).GetAwaiter().GetResult(); }
        finally { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    public GameActor Server()
    {
        bool devcommands = true;
        var transport = new ScriptedTransport()
            .Extension("valheim.world", "terrain", args => new {
                source = "generator", complete = true, units = "metres",
                x = float.Parse(args[0], CultureInfo.InvariantCulture), z = float.Parse(args[1], CultureInfo.InvariantCulture),
                height = args[0] == "105" ? 42.3f : 42.4f,
            })
            .On("devcommands", _ => ScriptedTransport.Ok("Dev commands: " + (devcommands = !devcommands)))
            .OnPrefix("acceptancemod_mark ", command =>
            {
                MarkCommands++;
                var (x, z) = Coordinates(command, 1);
                if (x < 200) { _markers.Add((x, z, CampaignSteps.DryLabel)); return ScriptedTransport.Ok($"OK: marked {x} {z} ground=42.5"); }
                return ScriptedTransport.Ok($"REFUSED: {x} {z} ground=22.0 below 31.5");
            })
            .OnPrefix("acceptancemodcontrol_spawn ", command =>
            {
                SpawnCommands++;
                if (!ControlServerOnlyPrefab) return ScriptedTransport.Ok("Unknown command acceptancemodcontrol_spawn");
                var (x, z) = Coordinates(command, 1);
                _controlObjects.Add((x, z));
                return ScriptedTransport.Ok($"OK: spawned {ControlPlugins.ServerOnlyPrefabName} at {x} {z}");
            })
            .OnPrefix("acceptancemod_greeting ", command =>
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
            .Extension("acceptancemod.testing", "session", _ => new { source = "owned-test-session", complete = true, acceptingConnections = true })
            .Extension("acceptancemod.testing", "harmony", args => args.Count == 1 && args[0] != AcceptancePlan.ModPlugin ? ControlCensus(args[0]) : TestWorld.ModCensus())
            .Extension("acceptancemod.testing", "globalkeys", _ => new { source = "global-keys", complete = true, server = true, keys = _keys.Order(StringComparer.Ordinal).ToArray() })
            .Extension("acceptancemod.testing", "globalkey", args =>
            {
                if (args[0] == "set") _keys.Add(args[1]); else _keys.RemoveAll(k => k == args[1] || k.StartsWith(args[1] + " ", StringComparison.Ordinal));
                return new { source = "global-key-change", complete = true, action = args[0], name = args[1], value = (string?)null, keys = _keys.ToArray() };
            }, readOnly: false)
            .Extension("acceptancemod.testing", "config", args => Config(args, server: true, installed: true, _serverGreeting))
            .Extension("acceptancemod.testing", "dungeon-rooms", _ => Dungeons())
            .Extension("acceptancemod.testing", "content-census", args => ContentCensusReply(args, server: true));
        Servers.Add(transport);
        return transport.Actor("server", "cli_expect worlduid=" + WorldUid);
    }

    /// <summary>As an owned server's session: the scripted server's adapter reports whether it accepts game connections.</summary>
    public void WaitUntilJoinable(GameActor server) => OwnedServerSession.WaitUntilJoinable(server, "acceptancemod.testing/session", TimeSpan.FromSeconds(5));

    /// <summary>What <c>OwnedServerSession.Restart</c> does to the world: only saved objects and keys come back.</summary>
    public GameActor Restart()
    {
        Restarts++;
        _markers.Clear(); _markers.AddRange(_savedMarkers);
        _keys = [.. _savedKeys];
        return Server();
    }

    // The client's player as its support reading reports it, wherever the last teleport put it.
    private object Support() => new
    {
        source = "local-player-support", complete = _joined, x = _x, y = _y, z = _z, speed = 0f,
        grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres",
    };

    /// <summary>A client; a refused one runs another AcceptanceMod build, so the server refuses its join.</summary>
    public ScriptedTransport Client(bool refused = false)
    {
        bool acknowledged = false;
        bool devcommands = false, refusedOnce = false;
        var transport = new ScriptedTransport()
            .On("devcommands", _ => ScriptedTransport.Ok("Dev commands: " + (devcommands = !devcommands)))
            .On("cli_access", _ => ScriptedTransport.Ok("ACCESS " + JsonSerializer.Serialize(new { schemaVersion = 1, complete = true,
                devcommands, cheatsAcknowledged = acknowledged, allowOnServerClients = true, server = false, dedicated = false,
                joinedClient = _joined && !false, localPlayer = _joined, profileAvailable = _joined })))
            .On("cli_acknowledge_local_cheats", _ => { acknowledged = true; return ScriptedTransport.Ok("OK: localCharacterCheated=True"); })
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
            .Extension("valheim.world", "player-support", _ => Support())
            .ArrivalSignals(Support)
            .OnPrefix("cli_prefabs_at ", command =>
            {
                var (x, z) = Coordinates(command, 1, 3);
                var lines = (ClientSeesMarkers ? _markers.Where(m => Near(m.X, m.Z, x, z, 8) && Loaded(m.X, m.Z)) : Enumerable.Empty<(float X, float Z, string Label)>()).Select(m => string.Create(CultureInfo.InvariantCulture,
                    $"PREFAB name={DrySiteScenario.Marker} distance=0.20 pos={m.X:F3},42.250,{m.Z:F3} rot=0.000,0.000,0.000 zdo=1:2 owner=0")).ToList();
                lines.Add($"OK: NEARBY_PREFABS radius=8.0 count={lines.Count}");
                return ScriptedTransport.Ok([.. lines]);
            })
            .Extension("acceptancemod.testing", "zones", Zones)
            .Extension("acceptancemod.testing", "markers", args =>
            {
                float x = F(args[0]), z = F(args[1]), radius = F(args[2]);
                var markers = _markers.Where(m => Near(m.X, m.Z, x, z, radius)).Select(m => new { x = m.X, y = 42.25f, z = m.Z, label = m.Label, instance = Loaded(m.X, m.Z) }).ToArray();
                return new { source = "acceptancemod-markers", complete = true, x, z, radius, markers };
            })
            .Extension("acceptancemodcontrol.fieldstate", "set", args =>
            {
                FieldSets++;
                var markers = _markers.Where(m => Near(m.X, m.Z, F(args[0]), F(args[1]), 1.5f) && Loaded(m.X, m.Z)).ToArray();
                if (markers.Length > 0) _field = args[2];
                return new { source = "field-only-state", complete = true, markers = markers.Length, values = markers.Select(_ => _field).ToArray() };
            }, readOnly: false)
            .Extension("acceptancemodcontrol.fieldstate", "read", args =>
            {
                var markers = _markers.Where(m => Near(m.X, m.Z, F(args[0]), F(args[1]), 1.5f) && Loaded(m.X, m.Z)).ToArray();
                return new { source = "field-only-state", complete = true, markers = markers.Length, values = markers.Select(_ => _field).ToArray() };
            })
            .Extension("acceptancemod.testing", "custom-data", args => !_joined ? new { source = "local-player-custom-data", complete = false } : (object)new
            {
                source = "local-player-custom-data", complete = true, prefix = args.Count == 1 ? args[0] : null, character = "Tester", profileFile = "tester",
                fileSource = CloudCharacter ? "Cloud" : "Local", profilePath = ProfileFile,
                entries = _live.Where(e => args.Count == 0 || e.Key.StartsWith(args[0], StringComparison.Ordinal)).OrderBy(e => e.Key, StringComparer.Ordinal)
                    .Select(e => new { key = e.Key, value = e.Value }).ToArray(),
            })
            .OnPrefix("acceptancemod_note ", command =>
            {
                if (!ClientHasMod) return ScriptedTransport.Ok("Unknown command acceptancemod_note");
                _live[LifecycleWorldScenario.NoteKey] = command.Split(' ')[1];
                return ScriptedTransport.Ok("OK: note " + command.Split(' ')[1]);
            })
            .Extension("acceptancemod.testing", "globalkeys", _ => new { source = "global-keys", complete = true, server = false, keys = (_joined ? _keys : new List<string>()).Order(StringComparer.Ordinal).ToArray() })
            .Extension("acceptancemod.testing", "config", args => Config(args, server: false, installed: ClientHasMod, _clientGreeting))
            .Extension("acceptancemod.testing", "content-census", args => ContentCensusReply(args, server: false))
            .Extension("acceptancemod.testing", "unresolved-prefabs", args =>
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
        // A client starts from its own file; AcceptanceMod's sync then sends the server's value.
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
        File.AppendAllText(ClientLog, $"[Info   :AcceptanceMod (ValheimTesting acceptance suite)] Greeting \"{greeting}\" received from 1 (client)\n");
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

    // The adapter's content census of a process with AcceptanceMod (or, on a client without it, of the game's content only).
    private object ContentCensusReply(IReadOnlyList<string> args, bool server)
    {
        bool installed = server || ClientHasMod;
        object Entry(string name) => new { name, hash = StableHash.Of(name), listed = 1, resolves = name };
        object Reference(string name, int amount = 0) => new { name, lookup = "resolved", amount };
        var items = new List<object>();
        if (installed) items.Add(Entry(ContentCensusScenario.ItemName));
        if (installed && ExtraItem) items.Add(Entry("AcceptanceMod_Extra"));
        object[] recipes = !installed || OmitRecipe ? [] :
        [
            new
            {
                name = ContentCensusScenario.RecipeName, enabled = true, amount = 1, item = Reference(ContentCensusScenario.ItemName),
                station = Reference("piece_workbench"), minStationLevel = 1, resources = new[] { Reference("Wood", 2) },
            },
        ];
        return new
        {
            source = "content-census", complete = true, ready = true, reason = (string?)null,
            side = server || ClientCensusSaysServer ? "server" : "client", dedicated = server,
            owner = new { guid = args[0], installed, version = installed ? "0.1.0" : null, md5 = installed ? Md5Mod : null },
            scope = args.Skip(1).ToArray(),
            totals = new { items = 900, itemIndex = 900, recipes = 400, prefabs = 3000, prefabIndex = 3000 },
            items, prefabs = installed ? new[] { Entry(ContentCensusScenario.ItemName), Entry(ContentCensusScenario.PieceName) } : [], recipes,
            pieces = installed ? new object[] { new
            {
                name = ContentCensusScenario.PieceName, hash = StableHash.Of(ContentCensusScenario.PieceName), tool = "Hammer", table = "_HammerPieceTable",
                listed = 1, resolves = ContentCensusScenario.PieceName, hasComponent = true, enabled = true,
                station = new { name = (string?)null, lookup = "none", amount = 0 }, resources = new[] { Reference("Wood", 2) },
            } } : [],
            statusEffects = installed && !OmitStatusEffect ? new[] { Entry(ContentCensusScenario.StatusName) } : [],
            collisions = Array.Empty<object>(),
        };
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
                method = "Player::AcceptanceModControlMethodThatDoesNotExist()",
                patches = new[] { new { owner, kind = "postfix", priority = 400, index = 0, before = Array.Empty<string>(), after = Array.Empty<string>(), patch = "AcceptanceMod.Controls.MissingHarmonyTarget.Plugin+PatchMissingMethod::Postfix()" } },
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
