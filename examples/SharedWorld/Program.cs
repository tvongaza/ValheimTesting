using Valheim.Testing;

// A slope with a declared terrace. Last matching region wins for every fact.
var terrain = new CompositeTerrain(new PlaneTerrain(40,.125f),
    TerrainRegion.Rectangle(28,-4,36,4,new PlaneTerrain(46)));
var world = new TerrainWorldState();
var originalPaint = new PaintRgba(.2f,.4f,.6f,.3f);
for (int zone=0; zone<2; zone++)
    world.Add(zone,0,new TerrainZoneState(
        new TerrainGrid<float>(65,65,zone*64-32,-32,1,terrain.GetHeight),
        // 32x32 texel centres, deliberately different from the height grid.
        new TerrainGrid<PaintRgba>(32,32,zone*64-31,-31,2,(x,z)=>originalPaint)));
var before = world.Clone();
// Stand-in for a mod-owned writer. The library does not emulate TerrainComp.
world[0,0].Heights[64,32] = 47;
Equal(before[0,0].Heights[64,32],46);
Equal(world[1,0].Heights[0,32],46); // missed neighbour writes remain observable
world[1,0].Heights[0,32] = 47;
Equal(world[0,0].Heights[64,32],world[1,0].Heights[0,32]);
if (!world[1,0].Paint[0,0].Equals(originalPaint)) throw new Exception("Paint changed.");

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
Console.WriteLine("PASS: composed terrain, independent height/paint grids, explicit seam write and isolated snapshot, grid dump parity with a shifted-grid control. No game used.");

string Csv(int offsetX)
{
    var text = new StringWriter();
    text.WriteLine("x,z,height");
    for (int z=-8; z<=8; z+=4) for (int x=0; x<=64; x+=4) text.WriteLine(FormattableString.Invariant($"{x+offsetX},{z},{terrain.GetHeight(x,z)}"));
    return text.ToString();
}

static void Equal(float actual,float expected)
{
    if (actual != expected) throw new Exception($"Expected {expected}, observed {actual}.");
}
