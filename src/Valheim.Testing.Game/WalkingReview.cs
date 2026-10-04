using System.Text.Json;
namespace Valheim.Testing.Game;

public sealed record WalkCheckpoint(float X, float Y, float Z, float Radius = 2, float HeightTolerance = 1);
public sealed record WalkSample(double Seconds, float X, float Y, float Z, float Speed, bool Grounded, bool Flying, bool Attached, bool Dead, bool Teleporting)
{
    /// <summary>
    /// Settled on the ground at <paramref name="point"/>: within the horizontal and vertical tolerances, no faster than
    /// <paramref name="maximumSpeed"/>, grounded, and not flying, attached, dead or teleporting.
    /// </summary>
    public bool SupportedAt(HeightExpectation point, float horizontalTolerance = 2, float verticalTolerance = .3f, float maximumSpeed = .15f)
    {
        TerrainProbe.Validate("loaded-ground", "support point", new[] { point }, verticalTolerance);
        if (!float.IsFinite(horizontalTolerance) || horizontalTolerance < 0 || !float.IsFinite(maximumSpeed) || maximumSpeed < 0)
            throw new ArgumentException("Invalid support tolerances.");
        // A non-finite position or speed is not support; every comparison below would be false for it anyway.
        return float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) && float.IsFinite(Speed) && Speed >= 0 &&
            Math.Sqrt(Math.Pow((double)X - point.X, 2) + Math.Pow((double)Z - point.Z, 2)) <= horizontalTolerance &&
            Math.Abs(Y - point.Height) <= verticalTolerance && Speed <= maximumSpeed && Grounded && !Flying && !Attached && !Dead && !Teleporting;
    }
}
public sealed record WalkEvidence(int ReachedCheckpoints, int PlannedCheckpoints, double TravelledMetres, double GroundedFraction, IReadOnlyList<string> Issues)
{
    public bool Sufficient => Issues.Count == 0;
}
// Observation quality and human usability are separate: even good telemetry
// cannot say whether a turn feels awkward or a road looks unnatural.
public sealed record WalkingReview(WalkEvidence Evidence, string HumanVerdict = "not-reviewed", string Notes = "");
public static class WalkingProbe
{
    public static void Validate(IReadOnlyList<WalkCheckpoint> route)
    {
        if (route == null || route.Count < 2 || route.Count > 256) throw new ArgumentException("Supply 2..256 ordered walk checkpoints.");
        foreach (var p in route)
            if (p == null || !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || Math.Abs(p.X)>20000 || Math.Abs(p.Z)>20000 ||
                !float.IsFinite(p.Radius) || p.Radius <= 0 || !float.IsFinite(p.HeightTolerance) || p.HeightTolerance <= 0)
                throw new ArgumentException("Checkpoints require finite coordinates and positive tolerances.");
        for (int i=1; i<route.Count; i++)
            if (Horizontal(route[i-1].X,route[i-1].Z,route[i].X,route[i].Z) <= route[i-1].Radius + route[i].Radius)
                throw new ArgumentException("Consecutive checkpoint areas must not overlap; standing still must not finish a walk.");
    }
    public static WalkSample Read(Observation observation, double seconds)
    {
        var sample=Parse(observation,seconds);
        CheckSample(sample); return sample;
    }
    /// <summary>The one parser of a <c>valheim.world/player-support</c> observation; <see cref="Read"/> also refuses non-finite values.</summary>
    internal static WalkSample Parse(Observation observation, double seconds)
    {
        observation.RequireComplete("local-player-support"); var d=observation.Data;
        if(d.GetProperty("units").GetString()!="metres") throw new InvalidOperationException("Wrong walking units.");
        return new WalkSample(seconds,d.GetProperty("x").GetSingle(),d.GetProperty("y").GetSingle(),d.GetProperty("z").GetSingle(),d.GetProperty("speed").GetSingle(),
            d.GetProperty("grounded").GetBoolean(),d.GetProperty("flying").GetBoolean(),d.GetProperty("attached").GetBoolean(),d.GetProperty("dead").GetBoolean(),d.GetProperty("teleporting").GetBoolean());
    }
    private static void CheckSample(WalkSample s)
    {
        if(s==null || !double.IsFinite(s.Seconds)||s.Seconds<0||!float.IsFinite(s.X)||!float.IsFinite(s.Y)||!float.IsFinite(s.Z)||!float.IsFinite(s.Speed)||s.Speed<0)
            throw new ArgumentException("Walking samples must be finite, with nonnegative time/speed.");
    }
    private static double Horizontal(float x,float z,float x2,float z2)=>Math.Sqrt(Math.Pow((double)x2-x,2)+Math.Pow((double)z2-z,2));
    public static WalkEvidence Assess(IReadOnlyList<WalkCheckpoint> route,IReadOnlyList<WalkSample> samples,double maxGapSeconds=1.5,double maxSpeed=10,double minimumGroundedFraction=.8)
    {
        Validate(route);
        if(!double.IsFinite(maxGapSeconds)||maxGapSeconds<=0||!double.IsFinite(maxSpeed)||maxSpeed<=0||
            !double.IsFinite(minimumGroundedFraction)||minimumGroundedFraction<0||minimumGroundedFraction>1)
            throw new ArgumentException("Invalid walking evidence limits.");
        var issues=new HashSet<string>(); int next=0; double travel=0; int grounded=0;
        if(samples.Count<2)issues.Add("insufficient_samples");
        WalkSample? previous=null;
        foreach(var s in samples)
        {
            CheckSample(s);
            if(s.Dead||s.Flying||s.Attached||s.Teleporting)issues.Add("invalid_player_state");
            if(s.Speed>maxSpeed)issues.Add("excessive_speed");
            if(s.Grounded)grounded++;
            if(previous!=null)
            {
                double dt=s.Seconds-previous.Seconds;
                if(dt<=0)throw new ArgumentException("Walking sample times must increase strictly.");
                if(dt>maxGapSeconds)issues.Add("observation_gap");
                double distance=Math.Sqrt(Math.Pow((double)s.X-previous.X,2)+Math.Pow((double)s.Y-previous.Y,2)+Math.Pow((double)s.Z-previous.Z,2));
                if(distance/dt>maxSpeed)issues.Add("position_jump");
                travel+=distance;
            }
            if(next<route.Count)
            {
                var p=route[next];
                if(Horizontal(s.X,s.Z,p.X,p.Z)<=p.Radius && Math.Abs((double)s.Y-p.Y)<=p.HeightTolerance)next++;
            }
            previous=s;
        }
        double fraction=samples.Count==0?0:(double)grounded/samples.Count;
        if(next!=route.Count)issues.Add("checkpoints_not_reached_in_order");
        if(fraction<minimumGroundedFraction)issues.Add("insufficient_grounded_samples");
        return new(next,route.Count,travel,fraction,issues.OrderBy(x=>x,StringComparer.Ordinal).ToArray());
    }
}
