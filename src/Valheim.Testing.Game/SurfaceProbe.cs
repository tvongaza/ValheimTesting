using System.Globalization;
namespace Valheim.Testing.Game;

public sealed record SurfaceMeasurement(float X,float Z,float Expected,float Height,float ColliderHeight,bool Passed);
public static class SurfaceProbe
{
    public static SurfaceMeasurement Read(Observation observation, HeightExpectation expected,float tolerance)
    {
        TerrainProbe.Validate("loaded-ground","declared surface",new[]{expected},tolerance);
        var (h,c)=Observed(observation,expected.X,expected.Z);
        return new(expected.X,expected.Z,expected.Height,h,c,Math.Abs(h-expected.Height)<=tolerance && Math.Abs(c-expected.Height)<=tolerance);
    }
    /// <summary>The one reader of <c>valheim.world/terrain-surface</c>: the loaded vertex height and its own collider's height.</summary>
    internal static (float Height,float ColliderHeight) Observed(Observation observation,float x,float z)
    {
        observation.RequireComplete("loaded-terrain-surface");
        var d=observation.Data;
        if(d.GetProperty("units").GetString()!="metres" || d.GetProperty("x").GetSingle()!=x || d.GetProperty("z").GetSingle()!=z)
            throw new InvalidOperationException("Wrong surface coordinates or units.");
        float h=d.GetProperty("height").GetSingle(),c=d.GetProperty("colliderHeight").GetSingle();
        if(!float.IsFinite(h)||!float.IsFinite(c)) throw new InvalidOperationException("Non-finite surface.");
        return (h,c);
    }
    public static IReadOnlyList<SurfaceMeasurement> Compare(GameActor actor,string expectedFrom,IReadOnlyList<HeightExpectation> samples,float tolerance)
    {
        TerrainProbe.Validate("loaded-ground",expectedFrom,samples,tolerance);
        if(samples.Any(s=>s.X!=MathF.Round(s.X)||s.Z!=MathF.Round(s.Z))) throw new ArgumentException("Native surface probe requires integer grid vertices.");
        var cap=actor.RequireCapability("valheim.world/terrain-surface");
        return samples.Select(s=>Read(actor.Observe(cap,s.X.ToString("R",CultureInfo.InvariantCulture),s.Z.ToString("R",CultureInfo.InvariantCulture)),s,tolerance)).ToArray();
    }
    /// <summary>
    /// Whether the player in a <c>valheim.world/player-support</c> observation stands settled at <paramref name="point"/>.
    /// Parses it as <see cref="WalkingProbe.Read"/> does, the one parser of that observation; a non-finite reading is not support.
    /// </summary>
    public static bool Supported(Observation observation,HeightExpectation point,float horizontalTolerance=2,float verticalTolerance=.3f,float maximumSpeed=.15f) =>
        WalkingProbe.Parse(observation,0).SupportedAt(point,horizontalTolerance,verticalTolerance,maximumSpeed);
}
