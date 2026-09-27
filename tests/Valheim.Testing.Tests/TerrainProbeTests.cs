using System.Text.Json;
using Valheim.Testing;
using Valheim.Testing.Game;
using Xunit;

public class TerrainProbeTests
{
    private static Observation Observed(float x, float z, float? h, string layer = "loaded-ground", bool complete = true, string units = "metres") =>
        new(layer, complete, JsonSerializer.SerializeToElement(new { x, z, height = h, units }));
    [Fact] public void DeclaredPlaneUsesHorizontalXZAndKeepsEveryResidual()
    {
        var plane = new PlaneTerrain(40, .25f, -.5f);
        var report = TerrainProbe.Compare("loaded-ground", "hand-derived plane: 40 + x/4 - z/2", [new(8, 10, 37), new(-4, 6, 36)], .001f,
            (x,z) => Observed(x,z,plane.GetHeight(x,z)));
        Assert.True(report.Passed); Assert.Equal(2, report.Samples.Count); Assert.All(report.Samples, x => Assert.Equal(0, x.Delta));
    }
    [Fact] public void WrongGroundFailsWithoutDroppingOtherSamples()
    {
        var report = TerrainProbe.Compare("loaded-ground", "fixture design", [new(0,0,40), new(1,0,40)], .05f,
            (x,z) => Observed(x,z,x == 0 ? 42 : 40));
        Assert.False(report.Passed); Assert.Equal(2,report.Samples.Count); Assert.Equal(2,report.Samples[0].Delta);
    }
    [Fact] public void UnloadedGroundCannotBecomeZeroOrGeneratorHeight() =>
        Assert.Throws<InvalidOperationException>(() => TerrainProbe.Height(Observed(0,0,null,complete:false),0,0,"loaded-ground"));
    [Fact] public void RawGeneratorCannotSatisfyGroundExpectation() =>
        Assert.Throws<InvalidOperationException>(() => TerrainProbe.Height(Observed(0,0,40,"generator"),0,0,"loaded-ground"));
    [Theory] [InlineData(1,2,"feet")] [InlineData(2,1,"metres")]
    public void WrongUnitsOrSwappedCoordinatesAreRefused(float x,float z,string units) =>
        Assert.Throws<InvalidOperationException>(() => TerrainProbe.Height(Observed(x,z,40,units:units),1,2,"loaded-ground"));
    [Fact] public void InvalidPlanFailsBeforeAnyObservation()
    {
        int reads=0;
        Assert.Throws<ArgumentException>(() => TerrainProbe.Compare("loaded-ground","",[new(0,0,40)],.1f,(x,z)=>{ reads++;return Observed(x,z,40); }));
        Assert.Equal(0,reads);
        Assert.Throws<ArgumentException>(()=>TerrainProbe.Validate("loaded-ground","source",[new(0,0,40),new(0,0,40)],.1f));
        Assert.Throws<ArgumentException>(()=>TerrainProbe.Validate("loaded-ground","source",[],.1f));
        Assert.Throws<ArgumentException>(()=>TerrainProbe.Validate("loaded-ground","source",[new(0,0,40)],float.NaN));
    }
}
