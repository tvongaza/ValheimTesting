using Valheim.Testing;
using Valheim.Testing.Doubles;

// A slope with a declared terrace. Last matching region wins for every fact.
var terrain = new CompositeTerrain(new PlaneTerrain(40,.125f),
    TerrainRegion.Rectangle(28,-4,36,4,new PlaneTerrain(46)));

// Two neighbouring zones as the game holds them: a Heightmap and its TerrainComp per zone, generated from the terrain
// above. Zone (0,0) spans x -32..32 and zone (1,0) x 32..96, so the terrace straddles their shared column at x = 32.
using (var scope = new ValheimWorldScope().WithTerrain(terrain).WithZdos())
{
    var west = scope.RegisterHeightmap(new Vector2s(0,0));
    var east = scope.RegisterHeightmap(new Vector2s(1,0));
    west.RebuildTerrain(); east.RebuildTerrain();
    TerrainAssert.SeamAgrees(west, east);
    var eastBefore = TerrainSnapshot.Of(east.m_terrainComp!);
    // Stand-in for a mod's terrain writer: raise the shared column by 1 m, but only in the west zone's compiler.
    // The game applies and saves a delta only where its modified flag is set, so a writer sets both.
    for (int z=0; z<=64; z++) Raise(west.m_terrainComp!, z*65+64);
    west.RebuildTerrain();
    if (!Fails(() => TerrainAssert.SeamAgrees(west, east))) throw new Exception("A write to one side of a seam went unnoticed.");
    TerrainAssert.Unchanged(eastBefore, east.m_terrainComp!, "east zone");
    // The writer's fix: the neighbour's copy of the shared vertices gets the same write.
    for (int z=0; z<=64; z++) Raise(east.m_terrainComp!, z*65);
    east.RebuildTerrain();
    TerrainAssert.SeamAgrees(west, east);
}

// The same ground as a grid dump (cli_world_dump's CSV layout, 4 m nodes), checked against its source at the nodes.
// Heights are written at full precision, so tolerance 0 holds; a real cli_world_dump rounds height to 0.1 m and
// river weight to 0.01, so compare one with its generator at >= 0.05 m (0.005 river weight).
// Negative control: the dump placed one node east must fail. Pass a path to also write a review PNG.
var dump = GridDumpTerrain.Read(new StringReader(Csv(0)), "terrace.csv");
var shifted = GridDumpTerrain.Read(new StringReader(Csv(4)), "terrace-shifted.csv");
var area = new TerrainArea(4,-8,64,8);
var parity = TerrainParity.Compare(terrain, dump, area, 4, 0);
if (!parity.Passed) throw new Exception(parity.ToString());
if (TerrainParity.Compare(terrain, shifted, area, 4, 0).Passed) throw new Exception("A one-node shift went unnoticed.");
var picture = new TerrainRenderer(new TerrainArea(0,-8,64,8), .5f) { LowHeight = 20, HighHeight = 60 }
    .Polyline(new[] { (28f,-4f), (36f,-4f), (36f,4f), (28f,4f), (28f,-4f) }, new RenderColor(255,0,0))
    .Render(dump);
if (args.Length > 0) picture.WritePng(args[0]);
Console.WriteLine("PASS: composed terrain, a one-sided seam write caught by the terrain doubles and then fixed, grid dump parity with a shifted-grid control. No game used.");

string Csv(int offsetX)
{
    var text = new StringWriter();
    text.WriteLine("x,z,height");
    for (int z=-8; z<=8; z+=4) for (int x=0; x<=64; x+=4) text.WriteLine(FormattableString.Invariant($"{x+offsetX},{z},{terrain.GetHeight(x,z)}"));
    return text.ToString();
}

static void Raise(TerrainComp compiler, int vertex)
{
    compiler.m_levelDelta[vertex] = 1;
    compiler.m_modifiedHeight[vertex] = true;
}

static bool Fails(Action check)
{
    try { check(); return false; }
    catch (TerrainAssertException) { return true; }
}
