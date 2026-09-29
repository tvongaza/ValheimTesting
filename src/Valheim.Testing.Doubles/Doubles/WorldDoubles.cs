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
    /// <summary>A <see cref="WorldGenerator"/> over the toolkit's composable terrain (<see cref="ITerrain"/>).</summary>
    public partial class TerrainWorld : WorldGenerator
    {
        public ITerrain Terrain { get; }
        public TerrainWorld(ITerrain terrain) => Terrain = terrain;
        public override float GetHeight(float x, float z) => Terrain.GetHeight(x, z);
        public override void GetRiverWeight(float x, float z, out float weight, out float width) => Terrain.GetRiverWeight(x, z, out weight, out width);
        public override Heightmap.Biome GetBiome(float x, float z) => Terrain.GetBiome(x, z) switch
        {
            TerrainBiome.Meadows => Heightmap.Biome.Meadows,
            TerrainBiome.BlackForest => Heightmap.Biome.BlackForest,
            TerrainBiome.Swamp => Heightmap.Biome.Swamp,
            TerrainBiome.Mountain => Heightmap.Biome.Mountain,
            TerrainBiome.Plains => Heightmap.Biome.Plains,
            TerrainBiome.Mistlands => Heightmap.Biome.Mistlands,
            TerrainBiome.Ocean => Heightmap.Biome.Ocean,
            TerrainBiome.Ashlands => Heightmap.Biome.AshLands,
            TerrainBiome.DeepNorth => Heightmap.Biome.DeepNorth,
            _ => throw new System.NotSupportedException("Fixture biome is unknown."),
        };
    }

    /// <summary>
    /// The game's process-wide singletons for one test. Created, it records which objects the singletons refer to
    /// (<c>WorldGenerator</c>, <c>ZDOMan</c>, <c>ZoneSystem</c>, <c>ZNetScene</c>, <c>ZNet</c>, <c>ZRoutedRpc</c>, Jotunn's <c>NetworkManager</c>, the
    /// loaded heightmaps, the log capture, the console commands and the local player) plus the server flag and the clock; disposed, it puts those references
    /// and values back. It restores references, not contents: nothing is deep-copied, so a test that mutates an object it
    /// did not install (adds a peer to the existing <c>ZNet</c>, a ZDO to the existing <c>ZDOMan</c>) leaves that change
    /// behind. Use the builder methods to install fresh, isolated objects instead (<see cref="WithZdos"/>,
    /// <see cref="WithScene"/>, <see cref="WithNetwork"/>, <see cref="WithCommands"/>, ...). A mod's own statics are the mod's to reset. The doubles
    /// are process-wide like the game's, so tests using them must not run in parallel (disable xUnit parallelization
    /// for the assembly).
    /// </summary>
    public sealed partial class ValheimWorldScope : System.IDisposable
    {
        private readonly WorldGenerator? _world = WorldGenerator.instance;
        private readonly ZDOMan? _zdos = ZDOMan.instance;
        private readonly ZoneSystem? _zones = ZoneSystem.instance;
        private readonly ZNetScene? _scene = ZNetScene.instance;
        private readonly System.Collections.Generic.List<Heightmap> _heightmaps = Heightmap.s_heightmaps;
        private bool _ownsHeightmaps;
        private readonly List<string>? _captured = BepInEx.Logging.ManualLogSource.Captured;
        private readonly ZNet _net = ZNet.instance;
        private readonly ZRoutedRpc _rpc = ZRoutedRpc.instance;
        private readonly Jotunn.Managers.NetworkManager _jotunn = Jotunn.Managers.NetworkManager.Instance;
        private readonly System.Collections.Generic.Dictionary<string, Terminal.ConsoleCommand> _commands = Terminal.commands;
        private readonly Player? _localPlayer = Player.m_localPlayer;
        private readonly bool _server = ZNet.instance?.Server ?? false;
        private readonly float _time = UnityEngine.Time.realtimeSinceStartup;

        public ValheimWorldScope WithWorld(WorldGenerator world) { WorldGenerator.instance = world; return this; }
        public ValheimWorldScope WithTerrain(ITerrain terrain) => WithWorld(new TerrainWorld(terrain));
        /// <summary>A new, empty ZDOMan (this session's id is 1).</summary>
        public ValheimWorldScope WithZdos() { ZDOMan.instance = new ZDOMan(); return this; }
        public ValheimWorldScope WithZoneSystem() { ZoneSystem.instance = new ZoneSystem(); return this; }
        /// <summary>A scene with no prefabs yet (<see cref="ZNetScene.AddPrefab"/>), and no Unity components yet for FindObjectsByType and RunFrame.</summary>
        public ValheimWorldScope WithScene() { ZNetScene.instance = new ZNetScene(); UnityEngine.Object.s_unityComponents = new List<UnityEngine.Component>(); return this; }
        /// <summary>A new <c>ZNet</c> with no peers, as the server or a client, a new <c>ZRoutedRpc</c> and a new Jotunn <c>NetworkManager</c>.</summary>
        public ValheimWorldScope WithNetwork(bool server = true)
        {
            ZNet.instance = new ZNet { Server = server }; ZRoutedRpc.instance = new ZRoutedRpc(); Jotunn.Managers.NetworkManager.Instance = new Jotunn.Managers.NetworkManager();
            return this;
        }
        /// <summary>No console commands yet: the mod's registration in this test fills a fresh table.</summary>
        public ValheimWorldScope WithCommands() { Terminal.commands = new System.Collections.Generic.Dictionary<string, Terminal.ConsoleCommand>(); return this; }
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
            if (!_ownsHeightmaps) { global::Heightmap.s_heightmaps = new(global::Heightmap.s_heightmaps); _ownsHeightmaps = true; }
            var hm = global::Heightmap.CreateForZone(zone, width, withCompiler);
            UnloadHeightmap(zone);
            global::Heightmap.s_heightmaps.Add(hm);
            return hm;
        }
        /// <summary>Unloads the zone's heightmap, as the game does when the zone leaves the active area.</summary>
        public void UnloadHeightmap(Vector2s zone)
        {
            if (!_ownsHeightmaps) { global::Heightmap.s_heightmaps = new(global::Heightmap.s_heightmaps); _ownsHeightmaps = true; }
            var centre = ZoneSystem.GetZonePos(zone);
            global::Heightmap.s_heightmaps.RemoveAll(h => h.transform.position.x == centre.x && h.transform.position.z == centre.z);
        }
        /// <summary>Captures every log line the mod writes for the rest of the scope.</summary>
        public List<string> CaptureLog() => BepInEx.Logging.ManualLogSource.Captured = new List<string>();
        public void Dispose()
        {
            UnityEngine.Object.EndOfFrame(); // pending destroys belong to this test's frame
            RestorePresetState();
            WorldGenerator.instance = _world; ZDOMan.instance = _zdos; ZoneSystem.instance = _zones; ZNetScene.instance = _scene;
            global::Heightmap.s_heightmaps = _heightmaps; BepInEx.Logging.ManualLogSource.Captured = _captured;
            Terminal.commands = _commands; Player.m_localPlayer = _localPlayer;
            ZNet.instance = _net; ZRoutedRpc.instance = _rpc; Jotunn.Managers.NetworkManager.Instance = _jotunn; if (_net != null) _net.Server = _server; UnityEngine.Time.realtimeSinceStartup = _time;
            RestoreTerrainModifiers();
        }
        /// <summary>Puts back what the role presets, registries, config and Unity doubles changed (WorldScopePresets.cs).</summary>
        partial void RestorePresetState();
    }
}
