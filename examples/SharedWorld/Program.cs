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
Console.WriteLine("PASS: composed terrain, independent height/paint grids, explicit seam write and isolated snapshot. No game used.");

static void Equal(float actual,float expected)
{
    if (actual != expected) throw new Exception($"Expected {expected}, observed {actual}.");
}
