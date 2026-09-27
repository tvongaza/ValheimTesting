using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;
public class WalkingReviewTests
{
    private static WalkCheckpoint[] Route=>new[]{new WalkCheckpoint(0,10,0,1),new WalkCheckpoint(10,10,0,1)};
    private static WalkSample[] Walk=>Enumerable.Range(0,11).Select(i=>new WalkSample(i*.5,i,10,0,2,true,false,false,false,false)).ToArray();
    [Fact] public void GoodTelemetryStillRequiresAHumanVerdict()
    {
        var report=new WalkingReview(WalkingProbe.Assess(Route,Walk));
        Assert.True(report.Evidence.Sufficient);Assert.Equal("not-reviewed",report.HumanVerdict);
    }
    [Fact] public void StationaryCharacterCannotPass()
    { var samples=Walk.Select(s=>s with { X=0,Speed=0 }).ToArray(); Assert.Contains("checkpoints_not_reached_in_order",WalkingProbe.Assess(Route,samples).Issues); }
    [Fact] public void EndpointTeleportWithoutFlagCannotPass()
    { Assert.Contains("position_jump",WalkingProbe.Assess(Route,new[]{Walk[0],Walk[10] with { Seconds=.5 }}).Issues); }
    [Theory][InlineData("Flying")][InlineData("Attached")][InlineData("Dead")][InlineData("Teleporting")]
    public void InvalidPlayerStateCannotPass(string field)
    {
        var samples=Walk;samples[4]=field switch { "Flying"=>samples[4] with { Flying=true },"Attached"=>samples[4] with { Attached=true },"Dead"=>samples[4] with { Dead=true },_=>samples[4] with { Teleporting=true }};
        Assert.Contains("invalid_player_state",WalkingProbe.Assess(Route,samples).Issues);
    }
    [Fact] public void GapIsNotEvidenceOfWalking() => Assert.Contains("observation_gap",WalkingProbe.Assess(Route,Walk.Select(s=>s with { Seconds=s.Seconds*5 }).ToArray()).Issues);
    [Fact] public void WrongHeightCannotReachCheckpoints() => Assert.False(WalkingProbe.Assess(Route,Walk.Select(s=>s with { Y=30 }).ToArray()).Sufficient);
    [Fact] public void WrongOrderCannotPass() => Assert.False(WalkingProbe.Assess(Route.Reverse().ToArray(),Walk).Sufficient);
    [Fact] public void AirborneTraversalNeedsReview() => Assert.Contains("insufficient_grounded_samples",WalkingProbe.Assess(Route,Walk.Select(s=>s with { Grounded=false }).ToArray()).Issues);
    [Fact] public void EmptyTraceIsNotSuccessful() => Assert.False(WalkingProbe.Assess(Route,Array.Empty<WalkSample>()).Sufficient);
    [Fact] public void TimeMustIncrease() => Assert.Throws<ArgumentException>(()=>WalkingProbe.Assess(Route,new[]{Walk[0],Walk[0]}));
    [Fact] public void OverlappingCheckpointsRefused()=>Assert.Throws<ArgumentException>(()=>WalkingProbe.Validate(new[]{Route[0],Route[0]}));
    [Fact] public void NonFiniteSampleRefused()=>Assert.Throws<ArgumentException>(()=>WalkingProbe.Assess(Route,new[]{Walk[0] with { X=float.NaN }}));
    [Fact] public void IncompleteObservationRefused()=>Assert.Throws<InvalidOperationException>(()=>WalkingProbe.Read(new("local-player-support",false,JsonSerializer.SerializeToElement(new{})),0));
}
