using System.Globalization;

namespace Valheim.Testing.Game;

public sealed record HeightExpectation(float X, float Z, float Height);
public sealed record HeightMeasurement(float X, float Z, float Expected, float Actual, double Delta, bool Passed);
public sealed record TerrainComparison(string Layer, string ExpectedFrom, float Tolerance, IReadOnlyList<HeightMeasurement> Samples)
{
    public bool Passed => Samples.Count > 0 && Samples.All(x => x.Passed);
}

// Compares a declared independent expectation with a specific observation layer.
// Capturing an input and replaying it is useful, but is not model calibration.
public static class TerrainProbe
{
    public const int MaximumSamples = 256;
    public static void Validate(string layer, string expectedFrom, IReadOnlyList<HeightExpectation> samples, float tolerance)
    {
        if (layer != "generator" && layer != "loaded-ground") throw new ArgumentException("Choose generator or loaded-ground explicitly.");
        if (string.IsNullOrWhiteSpace(expectedFrom)) throw new ArgumentException("Describe the independent source of expected heights.");
        if (!float.IsFinite(tolerance) || tolerance < 0) throw new ArgumentException("Use a finite, nonnegative tolerance in metres.");
        if (samples.Count == 0 || samples.Count > MaximumSamples) throw new ArgumentException("Supply 1..256 expected samples.");
        if (samples.Any(x => !float.IsFinite(x.X) || !float.IsFinite(x.Z) || !float.IsFinite(x.Height) || Math.Abs(x.X) > 20000 || Math.Abs(x.Z) > 20000))
            throw new ArgumentException("Invalid height or coordinates outside the observation range.");
        if (samples.Select(x => (x.X, x.Z)).Distinct().Count() != samples.Count) throw new ArgumentException("Duplicate coordinates do not add coverage.");
    }
    public static float Height(Observation observation, float x, float z, string layer)
    {
        if (layer != "generator" && layer != "loaded-ground") throw new ArgumentException("Unknown terrain layer.");
        observation.RequireComplete(layer);
        var data = observation.Data;
        if (data.GetProperty("units").GetString() != "metres" || data.GetProperty("x").GetSingle() != x || data.GetProperty("z").GetSingle() != z)
            throw new InvalidOperationException("Terrain response has the wrong units or coordinates.");
        float height = data.GetProperty("height").GetSingle();
        if (!float.IsFinite(height)) throw new InvalidOperationException("Terrain returned a non-finite height.");
        return height;
    }
    public static TerrainComparison Compare(GameActor actor, string layer, string expectedFrom, IReadOnlyList<HeightExpectation> samples, float tolerance)
    {
        Validate(layer, expectedFrom, samples, tolerance);
        var capability = actor.RequireCapability("valheim.world/terrain");
        return Compare(layer, expectedFrom, samples, tolerance, (x, z) => actor.Observe(capability,
            x.ToString("R", CultureInfo.InvariantCulture), z.ToString("R", CultureInfo.InvariantCulture), layer));
    }
    public static TerrainComparison Compare(string layer, string expectedFrom, IReadOnlyList<HeightExpectation> samples, float tolerance, Func<float, float, Observation> observe)
    {
        Validate(layer, expectedFrom, samples, tolerance);
        var measurements = new List<HeightMeasurement>(samples.Count);
        foreach (var sample in samples)
        {
            float actual = Height(observe(sample.X, sample.Z), sample.X, sample.Z, layer);
            double delta = (double)actual - sample.Height;
            measurements.Add(new(sample.X, sample.Z, sample.Height, actual, delta, double.IsFinite(delta) && Math.Abs(delta) <= tolerance));
        }
        return new(layer, expectedFrom, tolerance, measurements);
    }
}
