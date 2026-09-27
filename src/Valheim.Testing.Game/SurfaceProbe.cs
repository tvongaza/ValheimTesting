using System.Globalization;
namespace Valheim.Testing.Game;

public sealed record SurfaceMeasurement(float X,float Z,float Expected,float Height,float ColliderHeight,bool Passed);
public static class SurfaceProbe
{
    public static SurfaceMeasurement Read(Observation observation, HeightExpectation expected,float tolerance)
    {
        TerrainProbe.Validate("loaded-ground","declared surface",new[]{expected},tolerance);
        observation.RequireComplete("loaded-terrain-surface");
        var d=observation.Data;
        if(d.GetProperty("units").GetString()!="metres" || d.GetProperty("x").GetSingle()!=expected.X || d.GetProperty("z").GetSingle()!=expected.Z)
            throw new InvalidOperationException("Wrong surface coordinates or units.");
        float h=d.GetProperty("height").GetSingle(),c=d.GetProperty("colliderHeight").GetSingle();
        if(!float.IsFinite(h)||!float.IsFinite(c)) throw new InvalidOperationException("Non-finite surface.");
        return new(expected.X,expected.Z,expected.Height,h,c,Math.Abs(h-expected.Height)<=tolerance && Math.Abs(c-expected.Height)<=tolerance);
    }
    public static IReadOnlyList<SurfaceMeasurement> Compare(GameActor actor,string expectedFrom,IReadOnlyList<HeightExpectation> samples,float tolerance)
    {
        TerrainProbe.Validate("loaded-ground",expectedFrom,samples,tolerance);
        if(samples.Any(s=>s.X!=MathF.Round(s.X)||s.Z!=MathF.Round(s.Z))) throw new ArgumentException("Native surface probe requires integer grid vertices.");
        var cap=actor.RequireCapability("valheim.world/terrain-surface");
        return samples.Select(s=>Read(actor.Observe(cap,s.X.ToString("R",CultureInfo.InvariantCulture),s.Z.ToString("R",CultureInfo.InvariantCulture)),s,tolerance)).ToArray();
    }
    public static bool Supported(Observation observation,HeightExpectation point,float horizontalTolerance=2,float verticalTolerance=.3f,float maximumSpeed=.15f)
    {
        TerrainProbe.Validate("loaded-ground","support point",new[]{point},verticalTolerance);
        if(!float.IsFinite(horizontalTolerance)||horizontalTolerance<0||!float.IsFinite(maximumSpeed)||maximumSpeed<0) throw new ArgumentException("Invalid support tolerances.");
        observation.RequireComplete("local-player-support"); var d=observation.Data;
        if(d.GetProperty("units").GetString()!="metres") throw new InvalidOperationException("Wrong support units.");
        float x=d.GetProperty("x").GetSingle(),y=d.GetProperty("y").GetSingle(),z=d.GetProperty("z").GetSingle(),speed=d.GetProperty("speed").GetSingle();
        return float.IsFinite(x)&&float.IsFinite(y)&&float.IsFinite(z)&&float.IsFinite(speed)&&speed>=0 &&
            Math.Sqrt(Math.Pow(x-point.X,2)+Math.Pow(z-point.Z,2))<=horizontalTolerance && Math.Abs(y-point.Height)<=verticalTolerance && speed<=maximumSpeed &&
            d.GetProperty("grounded").GetBoolean() && !d.GetProperty("flying").GetBoolean() && !d.GetProperty("attached").GetBoolean() &&
            !d.GetProperty("dead").GetBoolean() && !d.GetProperty("teleporting").GetBoolean();
    }
}
