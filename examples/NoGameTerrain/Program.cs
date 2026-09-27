using System.Text.Json;
using Valheim.Testing;
using Valheim.Testing.Game;

// Hand-derived expectations, not values read back from the model being tested.
var plane = new PlaneTerrain(40, .25f, -.5f);
HeightExpectation[] expected = [new(8,10,37), new(-4,6,36)];
var result = TerrainProbe.Compare("generator", "plane design: 40 + x/4 - z/2", expected, .001f,
    (x,z) => new Observation("generator",true,JsonSerializer.SerializeToElement(new { x,z,height=plane.GetHeight(x,z),units="metres" })));
if(!result.Passed) throw new Exception("Plane did not meet its declared contract.");
var replay = new ReplayTerrain("example capture", "generator", result.Samples.Select(s => new TerrainSample(s.X,s.Z,s.Actual,TerrainBiome.Unknown)));
Check.Near(replay.GetHeight(8,10),37,.001);
try { replay.GetBiome(8,10); throw new Exception("Missing biome was silently invented."); }
catch(NotSupportedException) { }
Console.WriteLine("PASS: independent plane expectations; exact replay; missing biome refused. No game or Steam used.");
