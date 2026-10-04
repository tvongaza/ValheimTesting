using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valheim.Testing.Game;

/// <summary>
/// What a client surface check measures: loaded-ground heights at grid vertices and, optionally, a support point where a
/// stationary player must stand grounded (on dry ground: a swimming player is not supported). Fixture preparation writes
/// it; the ObserveCheck example's <c>surface</c> probe reads it. Expected values come from the fixture's declared inputs, never from the check.
/// </summary>
public sealed class SurfacePlan
{
    public string ExpectedFrom { get; set; } = "";
    public float Tolerance { get; set; } = .05f;
    public List<HeightExpectation> Samples { get; set; } = [];
    /// <summary>Null when the fixture declares no dry support point: grounding is then not checked, and reports say so.</summary>
    public HeightExpectation? Support { get; set; }
    public void Validate()
    {
        TerrainProbe.Validate("loaded-ground", ExpectedFrom, Samples, Tolerance);
        if (Samples.Any(s => s.X != MathF.Round(s.X) || s.Z != MathF.Round(s.Z))) throw new ArgumentException("Surface samples must be grid vertices.");
        if (Support != null) TerrainProbe.Validate("loaded-ground", ExpectedFrom, [Support], .3f);
    }
    public static SurfacePlan Read(string path) { var plan = ClientPlanFile.Read<SurfacePlan>(path); plan.Validate(); return plan; }
    public void Write(string path) { Validate(); ClientPlanFile.Write(path, this); }
}

/// <summary>What a client paint check measures: loaded paint RGBA at declared points. Fixture preparation writes it; the ObserveCheck example's <c>paint</c> probe reads it.</summary>
public sealed class PaintPlan
{
    public string ExpectedFrom { get; set; } = "";
    public float Tolerance { get; set; } = .01f;
    public List<PaintExpectation> Samples { get; set; } = [];
    public void Validate() => PaintProbe.Validate(ExpectedFrom, Samples, Tolerance);
    public static PaintPlan Read(string path) { var plan = ClientPlanFile.Read<PaintPlan>(path); plan.Validate(); return plan; }
    public void Write(string path) { Validate(); ClientPlanFile.Write(path, this); }
}

internal static class ClientPlanFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new ArgumentException("Empty plan.");
    public static void Write<T>(string path, T plan) => File.WriteAllText(path, JsonSerializer.Serialize(plan, Options));
}
