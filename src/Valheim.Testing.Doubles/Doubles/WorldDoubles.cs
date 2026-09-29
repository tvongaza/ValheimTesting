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
    /// The game's process-wide singletons for one test: snapshots them when created and restores them on dispose, so a
    /// test's world never leaks into the next. Builder methods set up what the test needs. The doubles are process-wide
    /// like the game's, so tests using them must not run in parallel (disable xUnit parallelization for the assembly).
    /// </summary>
    public sealed partial class ValheimWorldScope : System.IDisposable
    {
        private readonly WorldGenerator? _world = WorldGenerator.instance;
        private readonly ZDOMan? _zdos = ZDOMan.instance;
        private readonly ZoneSystem? _zones = ZoneSystem.instance;
        private readonly ZNetScene? _scene = ZNetScene.instance;
        private readonly Heightmap? _heightmap = Heightmap.Registered;
        private readonly List<string>? _captured = BepInEx.Logging.ManualLogSource.Captured;
        private readonly bool _server = ZNet.instance.Server;
        private readonly float _time = UnityEngine.Time.realtimeSinceStartup;

        public ValheimWorldScope WithWorld(WorldGenerator world) { WorldGenerator.instance = world; return this; }
        public ValheimWorldScope WithTerrain(ITerrain terrain) => WithWorld(new TerrainWorld(terrain));
        /// <summary>A new, empty ZDOMan (this session's id is 1).</summary>
        public ValheimWorldScope WithZdos() { ZDOMan.instance = new ZDOMan(); return this; }
        public ValheimWorldScope WithZoneSystem() { ZoneSystem.instance = new ZoneSystem(); return this; }
        /// <summary>A scene with no prefabs yet (<see cref="ZNetScene.AddPrefab"/>).</summary>
        public ValheimWorldScope WithScene() { ZNetScene.instance = new ZNetScene(); return this; }
        public ValheimWorldScope AsServer(bool server = true) { ZNet.instance.Server = server; return this; }
        /// <summary>Registers a zone heightmap (with a compiler unless <paramref name="withCompiler"/> is false) and returns it.</summary>
        public global::Heightmap RegisterHeightmap(Vector2s zone, int width = 64, bool withCompiler = true) => global::Heightmap.Registered = global::Heightmap.CreateForZone(zone, width, withCompiler);
        /// <summary>Captures every log line the mod writes for the rest of the scope.</summary>
        public List<string> CaptureLog() => BepInEx.Logging.ManualLogSource.Captured = new List<string>();
        public void Dispose()
        {
            WorldGenerator.instance = _world; ZDOMan.instance = _zdos; ZoneSystem.instance = _zones; ZNetScene.instance = _scene;
            global::Heightmap.Registered = _heightmap; BepInEx.Logging.ManualLogSource.Captured = _captured;
            ZNet.instance.Server = _server; UnityEngine.Time.realtimeSinceStartup = _time;
        }
    }
}
