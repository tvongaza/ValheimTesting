// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// World setup for tests: the game's process-wide singletons, and synthetic terrain as a WorldGenerator.
using System.Collections.Generic;
using Valheim.Testing;

namespace Valheim.Testing.Doubles
{
    /// <summary>
    /// The game build the doubles copy, which both game captures in the doubles' tests must come from: the ZPackage bytes
    /// record <see cref="Version"/>, the member list <see cref="NetworkVersion"/>. Change them only with the game pin, and
    /// recapture both.
    /// </summary>
    public static class DoubledGame
    {
        /// <summary>The game's version string; <c>ZNet.VersionString</c>'s default.</summary>
        public const string Version = "1.0.17";
        /// <summary>The game's network version (<c>Version.c_networkVersion</c>); <c>ZNet.NetworkVersion</c>'s default.</summary>
        public const uint NetworkVersion = 40;
    }

    /// <summary>A <see cref="WorldGenerator"/> over the toolkit's composable terrain (<see cref="ITerrain"/>).</summary>
    public partial class TerrainWorld : WorldGenerator
    {
        public ITerrain Terrain { get; }
        public TerrainWorld(ITerrain terrain) => Terrain = terrain;
        public override float GetHeight(float x, float z) => Terrain.GetHeight(x, z);
        public override void GetRiverWeight(float x, float z, out float weight, out float width) => Terrain.GetRiverWeight(x, z, out weight, out width);
        public override Heightmap.Biome GetBiome(float x, float z, float oceanLevel = 0.02f, bool waterAlwaysOcean = false) => Terrain.GetBiome(x, z) switch
        {
            TerrainBiome.Meadows => Heightmap.Biome.Meadows,
            TerrainBiome.BlackForest => Heightmap.Biome.BlackForest,
            TerrainBiome.Swamp => Heightmap.Biome.Swamp,
            TerrainBiome.Mountain => Heightmap.Biome.Mountain,
            TerrainBiome.Plains => Heightmap.Biome.Plains,
            TerrainBiome.Mistlands => Heightmap.Biome.Mistlands,
            TerrainBiome.Ocean => Heightmap.Biome.Ocean,
            TerrainBiome.AshLands => Heightmap.Biome.AshLands,
            TerrainBiome.DeepNorth => Heightmap.Biome.DeepNorth,
            _ => throw new System.NotSupportedException("Fixture biome is unknown."),
        };
    }

    /// <summary>
    /// Where Unity promises no order, which order the doubles use (<see cref="ValheimWorldScope.WithUnityOrder"/>): Awake,
    /// OnEnable and OnDisable across the objects of a hierarchy that is activated, deactivated or instantiated; each
    /// phase of <c>Object.RunFrame</c> (Start, Update, coroutines and Invoke, LateUpdate, end-of-frame coroutines) across
    /// behaviours; OnDestroy across the objects <c>EndOfFrame</c> destroys and within a destroyed hierarchy; and
    /// <c>FindObjectsByType</c> with <c>FindObjectsSortMode.None</c> (so <c>FindAnyObjectByType</c>). What Unity does
    /// promise is kept in both: one behaviour's own sequence (Awake, OnEnable, Start, Update, ...), a behaviour waking at
    /// once in <c>AddComponent</c> on an active object, every behaviour finishing a frame phase before the next phase, and
    /// <c>FindObjectsSortMode.InstanceID</c>. Unity's script execution order (<c>DefaultExecutionOrder</c>, the project
    /// settings) is not modelled.
    /// </summary>
    public enum UnityOrder
    {
        /// <summary>The default: the order objects and components were made or queued in. Deterministic, which Unity is not.</summary>
        Insertion = 0,
        /// <summary>
        /// The exact reverse of <see cref="Insertion"/>. Any two objects meet in the other order, so a test that passes in
        /// both does not depend on which of two objects goes first; it says nothing about orders of three or more.
        /// </summary>
        Reversed = 1,
    }

    /// <summary>
    /// The game's process-wide state for one test. Created, it records every static the doubles keep (the singletons such
    /// as <c>WorldGenerator</c>, <c>ZDOMan</c>, <c>ZoneSystem</c>, <c>ZNetScene</c>, <c>ZNet</c>, <c>ZRoutedRpc</c>, <c>ObjectDB</c> and Jotunn's
    /// <c>NetworkManager</c>; the loaded heightmaps, terrain modifiers, Unity objects and pending destroys; the log capture, console
    /// commands, cheat gates, local player and player list; the registries' hooks, localization, input, preferences, config
    /// disk, heightmap builder, clock and the random generator <c>Random.InitState</c> installed, not its position in
    /// its sequence) plus the server and dedicated flags of the <c>ZNet</c> it found;
    /// disposed, it ends the test's frame (<c>UnityEngine.Object.EndOfFrame</c>) and restores the statics through
    /// <see cref="StaticOverride"/> in reverse order, each even when another fails. A second Dispose does nothing. It restores
    /// references, except the game's three static readonly lists of heightmaps, terrain modifiers and players, whose
    /// contents are restored in place. Other objects are not deep-copied, so a test that mutates one it did not install (adds a peer to
    /// the existing <c>ZNet</c>, a ZDO to the existing <c>ZDOMan</c>) leaves that change behind. Use the builder methods to
    /// install fresh, isolated objects instead (<see cref="WithZdos"/>, <see cref="WithScene"/>, <see cref="WithNetwork"/>,
    /// <see cref="WithCommands"/>, ...). A mod's own statics are the mod's to scope, with <see cref="StaticOverride"/>. The doubles
    /// are process-wide like the game's, so tests using them must not run in parallel (disable xUnit parallelization
    /// for the assembly).
    /// </summary>
    public sealed partial class ValheimWorldScope : System.IDisposable
    {
        // Every mutable static the doubles declare, in one list. A double that adds one adds it here; the doubles' tests
        // (ValheimWorldScopeTests) change each static inside a scope and fail when one is not put back, unless they list it
        // as deliberately unscoped.
        private readonly StaticOverride _statics = StaticOverride
            .Keep(() => WorldGenerator.instance).AndKeep(() => ZDOMan.instance).AndKeep(() => ZoneSystem.instance)
            .AndKeep(() => ZNetScene.instance).AndKeep(() => ZNet.instance).AndKeep(() => ZNet.m_loadError)
            .AndKeep(() => ZRoutedRpc.instance).AndKeep(() => Jotunn.Managers.NetworkManager.Instance)
            .AndKeep(() => ObjectDB.m_instance).AndKeep(() => ObjectDB.AwakePostfix).AndKeep(() => ObjectDB.CopyOtherDBPostfix)
            .AndKeep(() => ZNetScene.AwakePostfix).AndKeep(() => HeightmapBuilder.m_instance)
            .AndKeep(() => global::TerrainModifier.s_needsSorting).AndKeep(() => ZNetView.GhostInit)
            .AndKeep(() => ZNetView.m_useInitZDO).AndKeep(() => ZNetView.m_initZDO).AndKeep(() => ZNetView.m_forceDisableInit)
            .AndKeep(() => UnityEngine.Object.s_unityComponents).AndKeep(() => UnityEngine.Object.s_unityGameObjects)
            .AndKeep(() => UnityEngine.Object.s_pendingDestroy).AndKeep(() => UnityEngine.Object.s_unityReversedOrder)
            .AndKeep(() => UnityEngine.Time.time).AndKeep(() => UnityEngine.Time.deltaTime).AndKeep(() => UnityEngine.Time.frameCount)
            .AndKeep(() => UnityEngine.Time.realtimeSinceStartup).AndKeep(() => UnityEngine.Random.s_random)
            .AndKeep(() => UnityEngine.Canvas.ForceUpdateCount)
            .AndKeep(() => BepInEx.Logging.ManualLogSource.Captured).AndKeep(() => BepInEx.Logging.ManualLogSource.ThrowOnNextInfo)
            .AndKeep(() => BepInEx.Configuration.ConfigFile.s_files).AndKeep(() => BepInEx.Paths.GameRootPath)
            .AndKeep(() => BepInEx.Paths.BepInExRootPath).AndKeep(() => BepInEx.Paths.ConfigPath).AndKeep(() => BepInEx.Paths.PluginPath)
            .AndKeep(() => Terminal.commands).AndKeep(() => Terminal.m_cheat).AndKeep(() => Achievements.CheatedAtAll)
            .AndKeep(() => Player.m_localPlayer)
            .AndKeep(() => Localization.Current).AndKeep(() => Localization.OnLanguageChange).AndKeep(() => ZInput.Current)
            .AndKeep(() => PlatformPrefs.s_values).AndKeep(() => PlatformPrefs.Unavailable);
        // Instance state, not statics: the flags of the ZNet this scope found, which AsServer and the presets change, and
        // its online backend (the game's static ZNet.m_onlineBackend reads it).
        private readonly ZNet? _net = ZNet.instance;
        private readonly bool _server = ZNet.instance?.Server ?? false, _dedicated = ZNet.instance?.Dedicated ?? false;
        private readonly OnlineBackendType _backend = ZNet.instance?.OnlineBackend ?? default;
        private readonly List<global::Heightmap> _heightmaps = new(global::Heightmap.s_heightmaps);
        private readonly List<global::TerrainModifier> _modifiers = new(global::TerrainModifier.s_instances);
        private readonly List<Player> _players = new(Player.s_players);
        private bool _disposed;

        public ValheimWorldScope WithWorld(WorldGenerator world) { _statics.And(() => WorldGenerator.instance, world); return this; }
        public ValheimWorldScope WithTerrain(ITerrain terrain) => WithWorld(new TerrainWorld(terrain));
        /// <summary>
        /// Sets game and real time in seconds without running a frame or any behaviour's Update. The last frame length
        /// becomes zero; the frame count is unchanged. The scope restores all four clock values on dispose.
        /// </summary>
        public ValheimWorldScope WithClock(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds < 0f)
                throw new System.ArgumentOutOfRangeException(nameof(seconds), "Clock time must be a finite, non-negative number of seconds.");
            UnityEngine.Time.SetClockForTest(seconds);
            return this;
        }
        /// <summary>
        /// The order the Unity doubles call behaviours and objects where Unity promises none (restored on dispose): see
        /// <see cref="UnityOrder"/>. Run a test that depends on two objects once in each order to find an order assumption.
        /// </summary>
        public ValheimWorldScope WithUnityOrder(UnityOrder order)
        {
            if (!System.Enum.IsDefined(typeof(UnityOrder), order)) throw new System.ArgumentOutOfRangeException(nameof(order));
            UnityEngine.Object.s_unityReversedOrder = order == UnityOrder.Reversed;
            return this;
        }
        /// <summary>A new, empty ZDOMan (this session's id is 1).</summary>
        public ValheimWorldScope WithZdos() { _statics.And(() => ZDOMan.instance, new ZDOMan()); return this; }
        /// <summary>Removes the ZDO manager for a test of code that runs before world loading.</summary>
        public ValheimWorldScope WithoutZdos() { _statics.And(() => ZDOMan.instance, (ZDOMan?)null); return this; }
        public ValheimWorldScope WithZoneSystem() { _statics.And(() => ZoneSystem.instance, new ZoneSystem()); return this; }
        /// <summary>Installs or removes a prepared heightmap builder for one test.</summary>
        public ValheimWorldScope WithHeightmapBuilder(HeightmapBuilder? builder) { _statics.And(() => HeightmapBuilder.instance, builder); return this; }
        /// <summary>A scene with no prefabs yet (<see cref="ZNetScene.AddPrefab"/>), and no GameObjects, Unity components or pending destroys yet for FindObjectsByType, RunFrame and EndOfFrame.</summary>
        public ValheimWorldScope WithScene()
        {
            _statics.And(() => ZNetScene.instance, new ZNetScene());
            EmptyUnityScene();
            return this;
        }
        /// <summary>No Unity objects or pending destroys: the scene <see cref="WithScene"/> and <see cref="AtMainMenu"/> start from.</summary>
        private static void EmptyUnityScene()
        {
            UnityEngine.Object.s_unityComponents = new List<UnityEngine.Component>(); UnityEngine.Object.s_unityGameObjects = new List<UnityEngine.GameObject>();
            UnityEngine.Object.s_pendingDestroy = new List<UnityEngine.Object>();
        }
        /// <summary>A new <c>ZNet</c> with no peers, as the server or a client, a new <c>ZRoutedRpc</c> and a new Jotunn <c>NetworkManager</c>.</summary>
        public ValheimWorldScope WithNetwork(bool server = true)
        {
            _statics.And(() => ZNet.instance, new ZNet { Server = server });
            _statics.And(() => ZRoutedRpc.instance, new ZRoutedRpc());
            _statics.And(() => Jotunn.Managers.NetworkManager.Instance, new Jotunn.Managers.NetworkManager());
            return this;
        }
        /// <summary>No console commands yet: the mod's registration in this test fills a fresh table.</summary>
        public ValheimWorldScope WithCommands() { Terminal.commands = new System.Collections.Generic.Dictionary<string, Terminal.ConsoleCommand>(); return this; }
        /// <summary>
        /// Opens both cheat gates (restored on dispose), as <c>devcommands</c> and <c>confirmcheats</c> do on a server:
        /// <see cref="Terminal.m_cheat"/> and <see cref="Achievements.CheatedAtAll"/>. Cheat commands still need the server side.
        /// </summary>
        public ValheimWorldScope WithCheats() { Terminal.m_cheat = true; Achievements.CheatedAtAll = true; return this; }
        /// <summary>A local player standing at <paramref name="position"/>, as on a client or a host; without it there is none, as on a dedicated server.</summary>
        public Player WithLocalPlayer(UnityEngine.Vector3 position) { var player = new Player(); player.transform.position = position; return Player.m_localPlayer = player; }
        /// <summary>Sets the server flag on the current <c>ZNet</c> (restored on dispose).</summary>
        public ValheimWorldScope AsServer(bool server = true) { ZNet.instance.Server = server; return this; }
        /// <summary>
        /// Loads a zone heightmap (with a compiler unless <paramref name="withCompiler"/> is false) and returns it. Zones
        /// add up, so a test can load neighbours one after another; loading a zone again replaces its heightmap, as an
        /// unload and reload would.
        /// </summary>
        public global::Heightmap RegisterHeightmap(Vector2s zone, int width = 64, bool withCompiler = true)
        {
            var hm = global::Heightmap.CreateForZone(zone, width, withCompiler);
            UnloadHeightmap(zone);
            global::Heightmap.s_heightmaps.Add(hm);
            return hm;
        }
        /// <summary>Unloads the zone's heightmap, as the game does when the zone leaves the active area.</summary>
        public void UnloadHeightmap(Vector2s zone)
        {
            var centre = ZoneSystem.GetZonePos(zone);
            global::Heightmap.s_heightmaps.RemoveAll(h => h.transform.position.x == centre.x && h.transform.position.z == centre.z);
        }
        /// <summary>Captures every log line the mod writes for the rest of the scope.</summary>
        public List<string> CaptureLog() => BepInEx.Logging.ManualLogSource.Captured = new List<string>();
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Pending destroys belong to this test's frame, and so do those an OnDestroy queues while they go.
            System.Exception? frame = null;
            try { for (int i = 0; i < 100 && UnityEngine.Object.s_pendingDestroy.Count > 0; i++) UnityEngine.Object.EndOfFrame(); }
            catch (System.Exception error) { frame = error; }
            try
            {
                if (_net != null) { _net.Server = _server; _net.Dedicated = _dedicated; _net.OnlineBackend = _backend; }
                try { _statics.Dispose(); }
                finally
                {
                    RestoreList(global::Heightmap.s_heightmaps, _heightmaps);
                    RestoreList(global::TerrainModifier.s_instances, _modifiers);
                    RestoreList(Player.s_players, _players);
                }
            }
            catch (System.Exception restore) when (frame != null)
            {
                throw new System.AggregateException("Ending the test's frame failed, and so did restoring the statics.", frame, restore);
            }
            if (frame != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(frame).Throw();
        }

        private static void RestoreList<T>(List<T> current, List<T> snapshot)
        {
            current.Clear();
            current.AddRange(snapshot);
        }
    }
}
