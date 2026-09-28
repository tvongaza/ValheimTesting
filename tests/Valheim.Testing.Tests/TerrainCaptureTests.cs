using System.Text.Json;
using System.Text.Json.Nodes;
using Valheim.Testing;
using Valheim.Testing.Game;
using Xunit;
public class TerrainCaptureTests
{
    private static readonly TerrainGridRequest Grid=new(-4,8,2,2,2,"generator");
    private static JsonObject Payload()=>JsonSerializer.SerializeToNode(new {
        formatVersion=1,source="terrain-grid",complete=true,units="metres",consistency="per-sample",layer="generator",
        originX=-4,originZ=8,spacing=2,countX=2,countZ=2,worldUid="123",gameVersion="fixture",gameAssemblyId=Guid.Empty.ToString(),worldGenVersion=2,
        startedUtc="2026-09-27T00:00:00Z",finishedUtc="2026-09-27T00:00:01Z",
        samples=new[]{new{x=-4,z=8,height=28,complete=true,biome="Meadows",riverWeight=0f,riverWidth=0f},
            new{x=-2,z=8,height=30,complete=true,biome="Meadows",riverWeight=0f,riverWidth=0f},
            new{x=-4,z=10,height=26,complete=true,biome="Meadows",riverWeight=0f,riverWidth=0f},
            new{x=-2,z=10,height=28,complete=true,biome="Meadows",riverWeight=0f,riverWidth=0f}}
    })!.AsObject();
    private static TerrainCapture Read(JsonObject d,TerrainGridRequest? grid=null)=>TerrainCapture.FromObservation(
        new Observation("terrain-grid",true,JsonSerializer.SerializeToElement(d)),grid??Grid,"declared capture fixture");
    [Fact] public void ReplayIsExactAndFileRoundTripPreservesProvenance()
    {
        var capture=Read(Payload());Assert.Equal(28,capture.Terrain.GetHeight(-4,8));Assert.Equal(TerrainBiome.Meadows,capture.Terrain.GetBiome(-4,8));
        Assert.Throws<InvalidOperationException>(()=>capture.Terrain.GetHeight(-3,8));
        string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
        try
        {
            capture.Save(path);Assert.Throws<IOException>(()=>capture.Save(path));
            var loaded=TerrainCapture.Load(path);Assert.Equal(capture.Provenance,loaded.Provenance);Assert.Equal("123",loaded.WorldUid);Assert.Equal(26,loaded.Terrain.GetHeight(-4,10));
            File.WriteAllText(path,File.ReadAllText(path).Replace("\"height\": 28","\"height\": 99"));
            Assert.Throws<InvalidDataException>(()=>TerrainCapture.Load(path));
        }
        finally{File.Delete(path);}
    }
    [Theory] [InlineData("complete")] [InlineData("coordinate")] [InlineData("missing")] [InlineData("duplicate")] [InlineData("layer")] [InlineData("units")] [InlineData("version")]
    public void MalformedCaptureCannotBecomeReplay(string change)
    {
        var d=Payload();var rows=d["samples"]!.AsArray();
        switch(change){case "complete":rows[0]!["complete"]=false;break;case "coordinate":rows[0]!["x"]=8;break;
            case "missing":rows.RemoveAt(0);break;case "duplicate":rows[1]=rows[0]!.DeepClone();break;
            case "layer":d["layer"]="loaded-ground";break;case "units":d["units"]="feet";break;case "version":d["formatVersion"]=2;break;}
        Assert.Throws<InvalidDataException>(()=>Read(d));
    }
    [Fact] public void LoadedGroundDoesNotInventBiomeOrRiverSamples()
    {
        var d=Payload();d["layer"]="loaded-ground";foreach(var row in d["samples"]!.AsArray()){row!["biome"]=null;row["riverWeight"]=null;row["riverWidth"]=null;}
        var capture=Read(d,Grid with{Layer="loaded-ground"});Assert.Equal(28,capture.Terrain.GetHeight(-4,8));
        Assert.Throws<NotSupportedException>(()=>capture.Terrain.GetBiome(-4,8));
        Assert.Throws<NotSupportedException>(()=>capture.Terrain.GetRiverWeight(-4,8,out _,out _));
    }
}
