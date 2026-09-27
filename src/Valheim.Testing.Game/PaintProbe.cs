using System.Globalization;
namespace Valheim.Testing.Game;

// Raw channels deliberately preserve vegetation alpha and mixed paint. They
// do not infer dirt/stone from appearance or confuse compiler data with a loaded mask.
public sealed record PaintExpectation(float X, float Z, float R, float G, float B, float A);
public sealed record PaintMeasurement(PaintExpectation Expected, PaintExpectation Actual, float MaxError, bool Passed);
public static class PaintProbe
{
    public static void Validate(string expectedFrom, IReadOnlyList<PaintExpectation> samples, float tolerance)
    {
        if (string.IsNullOrWhiteSpace(expectedFrom) || samples == null || samples.Count == 0 || samples.Count > 4096 ||
            !float.IsFinite(tolerance) || tolerance < 0 || tolerance > 1)
            throw new ArgumentException("Declare paint expectations, 1..4096 texels and a tolerance in [0,1].");
        foreach (var s in samples)
        {
            if (s == null || !float.IsFinite(s.X) || !float.IsFinite(s.Z) || Math.Abs(s.X) > 20000 || Math.Abs(s.Z) > 20000 ||
                s.X != MathF.Round(s.X) || s.Z != MathF.Round(s.Z) || !Channels(s).All(v => float.IsFinite(v) && v >= 0 && v <= 1))
                throw new ArgumentException("Paint requires integer world coordinates and finite RGBA channels in [0,1].");
        }
        if (samples.Select(s => (s.X, s.Z)).Distinct().Count() != samples.Count)
            throw new ArgumentException("Duplicate paint coordinates do not add coverage.");
    }
    private static float[] Channels(PaintExpectation s) => new[] { s.R, s.G, s.B, s.A };
    public static PaintMeasurement Read(Observation observation, PaintExpectation expected, float tolerance)
    {
        Validate("declared paint", new[] { expected }, tolerance);
        observation.RequireComplete("loaded-terrain-paint");
        var d = observation.Data;
        if (d.GetProperty("units").GetString() != "rgba01" || d.GetProperty("x").GetSingle() != expected.X || d.GetProperty("z").GetSingle() != expected.Z)
            throw new InvalidOperationException("Wrong paint coordinates or units.");
        var actual = new PaintExpectation(expected.X, expected.Z, d.GetProperty("r").GetSingle(), d.GetProperty("g").GetSingle(),
            d.GetProperty("b").GetSingle(), d.GetProperty("a").GetSingle());
        Validate("observed paint", new[] { actual }, 0);
        float error = Channels(actual).Zip(Channels(expected), (a, b) => Math.Abs(a - b)).Max();
        return new(expected, actual, error, error <= tolerance);
    }
    public static IReadOnlyList<PaintMeasurement> Compare(GameActor actor, string expectedFrom, IReadOnlyList<PaintExpectation> samples, float tolerance)
    {
        Validate(expectedFrom, samples, tolerance);
        var cap = actor.RequireCapability("valheim.world/terrain-paint");
        return samples.Select(s => Read(actor.Observe(cap, s.X.ToString("R", CultureInfo.InvariantCulture),
            s.Z.ToString("R", CultureInfo.InvariantCulture)), s, tolerance)).ToArray();
    }
}
