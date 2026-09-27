using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;
public class SurfaceProbeTests
{
    private static Observation Surface(float h=65,float collider=65,bool complete=true)=>new("loaded-terrain-surface",complete,JsonSerializer.SerializeToElement(new{x=32,z=0,height=h,colliderHeight=collider,units="metres"}));
    [Fact] public void IndependentLayers()=>Assert.True(SurfaceProbe.Read(Surface(),new(32,0,65),.05f).Passed);
    [Fact] public void ColliderMismatchFails()=>Assert.False(SurfaceProbe.Read(Surface(collider:64),new(32,0,65),.05f).Passed);
    [Fact] public void HeightMismatchFails()=>Assert.False(SurfaceProbe.Read(Surface(h:64),new(32,0,65),.05f).Passed);
    [Fact] public void MissingGroundFails()=>Assert.Throws<InvalidOperationException>(()=>SurfaceProbe.Read(Surface(complete:false),new(32,0,65),.05f));
    [Fact] public void OtherCoordinatesFail()=>Assert.Throws<InvalidOperationException>(()=>SurfaceProbe.Read(Surface(),new(33,0,65),.05f));
    private static Observation Player(string? wrong=null)
    {
        var fields=new Dictionary<string,object>{["x"]=32f,["y"]=65.1f,["z"]=0f,["speed"]=0f,["grounded"]=true,["flying"]=false,["attached"]=false,["dead"]=false,["teleporting"]=false,["units"]="metres"};
        if(wrong=="speed") fields[wrong]=1f; else if(wrong=="x") fields[wrong]=200f; else if(wrong=="y")fields[wrong]=67f; else if(wrong!=null) fields[wrong]=wrong!="grounded";
        return new("local-player-support",true,JsonSerializer.SerializeToElement(fields));
    }
    [Fact] public void StationaryPlayerIsSupported()=>Assert.True(SurfaceProbe.Supported(Player(),new(32,0,65)));
    [Theory][InlineData("speed")][InlineData("x")][InlineData("y")][InlineData("grounded")][InlineData("flying")][InlineData("attached")][InlineData("dead")][InlineData("teleporting")]
    public void PositionAloneDoesNotProveSupport(string state)=>Assert.False(SurfaceProbe.Supported(Player(state),new(32,0,65)));
}
