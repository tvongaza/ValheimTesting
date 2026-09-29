// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// The process's role as the game sees it (dedicated server, listen-server host, client, main menu), the players each
// role has, plugin loading, and ObjectDB's two registration passes, as ValheimWorldScope presets.
using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>A player, as mod code finds one: its view (and so its ZDO), its name and id from the ZDO, and the list of every player object.</summary>
public sealed partial class Player
{
    internal static List<Player> s_players = new();
    /// <summary>Every player object, as the game's list: the main-menu preview and remote players count too.</summary>
    public static List<Player> GetAllPlayers() => s_players;
    /// <summary>The player's view: over its ZDO in a world, over none for the main-menu preview. Protected in the game (Character); public here, as with publicized assemblies.</summary>
    public ZNetView m_nview = new(null!);
    public bool IsOwner() => m_nview.IsValid() && m_nview.IsOwner();
    public ZDOID GetZDOID() => m_nview.IsValid() ? m_nview.GetZDO().m_uid : ZDOID.None;
    /// <summary>The name in the ZDO ("..." when unset), or "" with no ZDO, as the game's.</summary>
    public string GetPlayerName() => m_nview.IsValid() ? m_nview.GetZDO().GetString(ZDOVars.s_playerName, "...") : "";
    public long GetPlayerID() => m_nview.IsValid() ? m_nview.GetZDO().GetLong(ZDOVars.s_playerID) : 0L;
}

public sealed partial class ZNet
{
    /// <summary>A dedicated server (no local player, no Steam client). A test switch; <c>ValheimWorldScope.AsDedicatedServer</c> sets it.</summary>
    public bool Dedicated;
    public bool IsDedicated() => Dedicated;
}

public static partial class ZDOVars
{
    public static readonly int s_playerName = "playerName".GetStableHashCode();
    public static readonly int s_playerID = "playerID".GetStableHashCode();
}

namespace Valheim.Testing.Doubles
{
    /// <summary>
    /// Role presets. Each installs a fresh network, ZDOs, zone system and scene (or, at the main menu, none), and the
    /// players the role has:
    /// <list type="bullet">
    /// <item><see cref="AsDedicatedServer"/>: <c>IsServer()</c> and <c>IsDedicated()</c> true, no local player. It starts
    /// while plugins load: <see cref="PlatformPrefs"/> (and so a first <c>Localization.instance</c>, which reads the saved
    /// language) throw until <see cref="FinishStartup"/>, because the game has no Steam client then.</item>
    /// <item><see cref="AsHost"/>: a listen server: <c>IsServer()</c> true, not dedicated, a local player.</item>
    /// <item><see cref="AsClient"/>: not the server, a local player.</item>
    /// <item><see cref="AtMainMenu"/>: no world (<c>ZNet.instance</c>, <c>ZDOMan.instance</c>, <c>ZNetScene.instance</c> and the
    /// others are null) and a preview <see cref="Player"/> with no ZDO that is not the local player, as the character
    /// select screen has.</item>
    /// </list>
    /// <see cref="AddRemotePlayer"/> adds another peer's player. The scope also saves and restores ObjectDB, the Unity
    /// component list, the registries' postfix hooks, Localization, ZInput, PlatformPrefs, the config disk, the player
    /// list, the heightmap builder and the clock.
    /// </summary>
    public sealed partial class ValheimWorldScope
    {
        private readonly List<UnityEngine.Component> _components = UnityEngine.Object.s_components;
        private readonly ObjectDB? _objectDB = ObjectDB.m_instance;
        private readonly Action<ObjectDB>? _objectDBAwake = ObjectDB.AwakePostfix, _objectDBCopy = ObjectDB.CopyOtherDBPostfix;
        private readonly Action<ZNetScene>? _sceneAwake = ZNetScene.AwakePostfix;
        private readonly Localization? _localization = Localization.Current;
        private readonly Action? _languageChange = Localization.OnLanguageChange;
        private readonly ZInput? _input = ZInput.Current;
        private readonly Dictionary<string, object> _prefs = PlatformPrefs.s_values;
        private readonly string? _prefsUnavailable = PlatformPrefs.Unavailable;
        private readonly Dictionary<string, string> _configFiles = BepInEx.Configuration.ConfigFile.s_files;
        private readonly bool _dedicated = ZNet.instance?.Dedicated ?? false;
        private readonly List<Player> _players = Player.s_players;
        private readonly HeightmapBuilder? _builder = HeightmapBuilder.m_instance;
        private readonly float _gameTime = UnityEngine.Time.time, _deltaTime = UnityEngine.Time.deltaTime;
        private readonly int _frameCount = UnityEngine.Time.frameCount;
        private UnityEngine.GameObject? _pluginManager;
        private ObjectDB? _objectDBPrefab;
        private UnityEngine.GameObject[] _vanillaItems = new UnityEngine.GameObject[0];
        private Recipe[] _vanillaRecipes = new Recipe[0];
        private StatusEffect[] _vanillaEffects = new StatusEffect[0];

        /// <summary>The preview character <see cref="AtMainMenu"/> made, or null.</summary>
        public Player? PreviewPlayer { get; private set; }

        partial void RestorePresetState()
        {
            UnityEngine.Object.s_components = _components;
            ObjectDB.m_instance = _objectDB; ObjectDB.AwakePostfix = _objectDBAwake; ObjectDB.CopyOtherDBPostfix = _objectDBCopy;
            ZNetScene.AwakePostfix = _sceneAwake;
            Localization.Current = _localization; Localization.OnLanguageChange = _languageChange; ZInput.Current = _input;
            PlatformPrefs.s_values = _prefs; PlatformPrefs.Unavailable = _prefsUnavailable;
            BepInEx.Configuration.ConfigFile.s_files = _configFiles;
            if (_net != null) _net.Dedicated = _dedicated;
            Player.s_players = _players;
            HeightmapBuilder.m_instance = _builder;
            UnityEngine.Time.time = _gameTime; UnityEngine.Time.deltaTime = _deltaTime; UnityEngine.Time.frameCount = _frameCount;
        }

        /// <summary>
        /// A dedicated server while its plugins load: server, dedicated, no local player, a world's singletons, fresh
        /// preferences and localization, and <see cref="PlatformPrefs"/> unavailable until <see cref="FinishStartup"/>, so
        /// a plugin's Awake that touches <c>Localization.instance</c> or PlatformPrefs throws here as it can in the game.
        /// </summary>
        public ValheimWorldScope AsDedicatedServer()
        {
            EnterWorld(server: true, dedicated: true);
            Player.m_localPlayer = null;
            PlatformPrefs.Unavailable = "on a dedicated server Steamworks is not initialized while plugins load. " +
                "Touch Localization and PlatformPrefs later (after the game has started), not from a plugin's Awake.";
            return this;
        }
        /// <summary>The game has started: the platform is initialized, so PlatformPrefs and Localization work.</summary>
        public ValheimWorldScope FinishStartup() { PlatformPrefs.Unavailable = null; return this; }

        /// <summary>A listen-server host: server, not dedicated, with a local player at <paramref name="position"/> (owned by this session). A broadcast runs both its server and client handlers.</summary>
        public ValheimWorldScope AsHost(UnityEngine.Vector3 position = default)
        {
            EnterWorld(server: true, dedicated: false);
            SpawnLocalPlayer(position);
            return this;
        }
        /// <summary>A client in a world: not the server, with a local player at <paramref name="position"/>.</summary>
        public ValheimWorldScope AsClient(UnityEngine.Vector3 position = default)
        {
            EnterWorld(server: false, dedicated: false);
            SpawnLocalPlayer(position);
            return this;
        }
        /// <summary>
        /// The main menu: no world, network, scene or local player, and a preview <see cref="Player"/> (<see cref="PreviewPlayer"/>)
        /// whose view has no ZDO; like the game's preview it is in <see cref="Player.GetAllPlayers"/>, so a player patch runs for it.
        /// </summary>
        public ValheimWorldScope AtMainMenu()
        {
            ZNet.instance = null!; ZRoutedRpc.instance = null!;
            ZDOMan.instance = null; ZoneSystem.instance = null; ZNetScene.instance = null; WorldGenerator.instance = null;
            global::Heightmap.s_heightmaps = new List<global::Heightmap>(); _ownsHeightmaps = true;
            UnityEngine.Object.s_components = new List<UnityEngine.Component>();
            Player.m_localPlayer = null;
            Player.s_players = new List<Player>();
            PreviewPlayer = new Player();
            Player.s_players.Add(PreviewPlayer);
            return this;
        }

        /// <summary>
        /// Another peer's player at <paramref name="position"/>: its ZDO is owned by <paramref name="peerId"/> and carries
        /// its name. On a server the peer is also connected (<c>ZNet.GetPeers</c>, with the player as its character); a
        /// client connects only to the server, so there it is only a player object.
        /// </summary>
        public Player AddRemotePlayer(long peerId, UnityEngine.Vector3 position, string name = "")
        {
            var zdos = ZDOMan.instance ?? throw new InvalidOperationException("A remote player needs a world: use a preset (AsHost, AsClient, AsDedicatedServer) or WithZdos first.");
            if (peerId == zdos.m_sessionID) throw new ArgumentException("That is this session's own id.", nameof(peerId));
            var player = SpawnPlayer(position, peerId, name);
            if (ZNet.instance != null && ZNet.instance.IsServer())
                ZNet.instance.Peers[peerId] = new ZNetPeer { m_uid = peerId, m_characterID = player.GetZDOID(), m_playerName = name };
            return player;
        }

        /// <summary>
        /// Loads a plugin as BepInEx's chainloader does: adds it to the manager object, which runs its Awake at once.
        /// An exception from Awake reaches the test (BepInEx would log it and leave the plugin half set up).
        /// </summary>
        public T LoadPlugin<T>() where T : BepInEx.BaseUnityPlugin
        {
            _pluginManager ??= new UnityEngine.GameObject("BepInEx_Manager");
            return _pluginManager.AddComponent<T>();
        }

        /// <summary>A config disk with no files yet: plugin configs start from their defaults.</summary>
        public ValheimWorldScope WithConfigFiles() { BepInEx.Configuration.ConfigFile.s_files = new Dictionary<string, string>(StringComparer.Ordinal); return this; }

        /// <summary>
        /// The game's own ObjectDB content: the prefab the main menu copies and the world scene's database both start from
        /// these items, recipes and status effects. Returns the prefab (its lists are the ones the main menu shares).
        /// No ObjectDB is the instance until a pass runs.
        /// </summary>
        public ObjectDB WithObjectDB(IEnumerable<UnityEngine.GameObject>? items = null, IEnumerable<Recipe>? recipes = null, IEnumerable<StatusEffect>? statusEffects = null)
        {
            _vanillaItems = items?.ToArray() ?? new UnityEngine.GameObject[0];
            _vanillaRecipes = recipes?.ToArray() ?? new Recipe[0];
            _vanillaEffects = statusEffects?.ToArray() ?? new StatusEffect[0];
            ObjectDB.m_instance = null;
            return _objectDBPrefab = new ObjectDB { m_items = _vanillaItems.ToList(), m_recipes = _vanillaRecipes.ToList(), m_StatusEffects = _vanillaEffects.ToList() };
        }
        /// <summary>
        /// The main menu's pass, as the game runs it each time the menu loads: a new ObjectDB is added (its Awake and
        /// <see cref="ObjectDB.AwakePostfix"/> run on empty lists), then <c>CopyOtherDB</c> takes the prefab's lists themselves
        /// (and <see cref="ObjectDB.CopyOtherDBPostfix"/> runs). What a mod adds here lands in the prefab's lists, so it is
        /// still there the next time the menu loads.
        /// </summary>
        public ObjectDB LoadMainMenuObjectDB()
        {
            var prefab = _objectDBPrefab ?? throw new InvalidOperationException("Declare the game's own content first with WithObjectDB.");
            var db = new UnityEngine.GameObject("FejdStartup").AddComponent<ObjectDB>();
            db.CopyOtherDB(prefab);
            return db;
        }
        /// <summary>
        /// A world load's pass: the world scene's ObjectDB wakes with the game's own content as it was built (new lists, not
        /// the prefab's), and <see cref="ObjectDB.AwakePostfix"/> runs.
        /// </summary>
        public ObjectDB LoadWorldObjectDB()
        {
            if (_objectDBPrefab == null) throw new InvalidOperationException("Declare the game's own content first with WithObjectDB.");
            var main = new UnityEngine.GameObject("_GameMain");
            main.SetActive(false);
            var db = main.AddComponent<ObjectDB>();
            db.m_items = _vanillaItems.ToList(); db.m_recipes = _vanillaRecipes.ToList(); db.m_StatusEffects = _vanillaEffects.ToList();
            main.SetActive(true);
            return db;
        }

        /// <summary>A heightmap builder with nothing queued or ready.</summary>
        public ValheimWorldScope WithHeightmapBuilder() { HeightmapBuilder.m_instance = new HeightmapBuilder(); return this; }

        private void EnterWorld(bool server, bool dedicated)
        {
            WithNetwork(server);
            ZNet.instance.Dedicated = dedicated;
            WithZdos(); WithZoneSystem(); WithScene();
            Player.s_players = new List<Player>();
            PreviewPlayer = null;
            Localization.Current = null; ZInput.Current = null;
            PlatformPrefs.s_values = new Dictionary<string, object>();
        }
        private void SpawnLocalPlayer(UnityEngine.Vector3 position) => Player.m_localPlayer = SpawnPlayer(position, ZDOMan.instance!.m_sessionID, "");
        private static Player SpawnPlayer(UnityEngine.Vector3 position, long owner, string name)
        {
            var zdo = ZDOMan.instance!.CreateNewZDO(position, "Player".GetStableHashCode());
            zdo.SetOwner(owner);
            if (name.Length > 0) zdo.Set(ZDOVars.s_playerName, name);
            var player = new Player { m_nview = new ZNetView(zdo) };
            player.transform.position = position;
            Player.s_players.Add(player);
            return player;
        }
    }
}
