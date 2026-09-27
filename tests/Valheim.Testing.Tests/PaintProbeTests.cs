using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;
public class PaintProbeTests
{
    private static readonly PaintExpectation Expected = new(32, 0, 1, 0, .5f, .25f);
    private static Observation Paint(float r=1,float g=0,float b=.5f,float a=.25f,bool complete=true,float x=32,string units="rgba01") =>
        new("loaded-terrain-paint",complete,JsonSerializer.SerializeToElement(new { x,z=0,r,g,b,a,units }));
    [Fact] public void MixedMaskIsNotCollapsedToOneMaterial() => Assert.True(PaintProbe.Read(Paint(),Expected,.01f).Passed);
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public void EveryChannelMatters(int channel)
    {
        var actual = channel switch { 0=>Paint(r:0), 1=>Paint(g:1), 2=>Paint(b:1), _=>Paint(a:1) };
        Assert.False(PaintProbe.Read(actual,Expected,.01f).Passed);
    }
    [Fact] public void MissingMaskCannotPassAsBlack() => Assert.Throws<InvalidOperationException>(()=>PaintProbe.Read(Paint(complete:false),Expected,.01f));
    [Fact] public void WrongCoordinateRejected() => Assert.Throws<InvalidOperationException>(()=>PaintProbe.Read(Paint(x:33),Expected,.01f));
    [Fact] public void WrongUnitsRejected() => Assert.Throws<InvalidOperationException>(()=>PaintProbe.Read(Paint(units:"metres"),Expected,.01f));
    [Fact] public void WrongLayerRejected() => Assert.Throws<InvalidOperationException>(()=>PaintProbe.Read(Paint() with { Source="compiler-paint" },Expected,.01f));
    [Fact] public void InvalidChannelRejected() => Assert.Throws<ArgumentException>(()=>PaintProbe.Read(Paint(a:2),Expected,.01f));
    [Theory][InlineData(-1)][InlineData(float.NaN)][InlineData(2)]
    public void InvalidToleranceRejected(float tolerance) => Assert.Throws<ArgumentException>(()=>PaintProbe.Read(Paint(),Expected,tolerance));
    [Fact] public void DuplicateTexelsRejected() => Assert.Throws<ArgumentException>(()=>PaintProbe.Validate("declared",new[]{Expected,Expected},.01f));
    [Fact] public void FractionalCoordinateRejected() => Assert.Throws<ArgumentException>(()=>PaintProbe.Validate("declared",new[]{Expected with { X=32.5f }},.01f));
}
