using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

namespace ExampleMod.Tests;

/// <summary>
/// The example's plan: the toolkit's pinned server plan (runtime, world, launch, pins, port) and client section, plus what
/// this mod's scenario needs: a dry site, a wet site and where the player stands. The expectations are declared here by
/// whoever prepared the fixture, from the world's generator heights; the scenario never derives them from the mod's replies.
/// </summary>
public sealed class MarkerPlan : ServerRunPlan
{
    public const string ScenarioName = "dry-site-lifecycle";
    public const string ModPlugin = "example.examplemod", AdapterPlugin = "example.examplemod.testadapter";
    public const string SessionTokenVariable = "EXAMPLEMOD_TEST_SESSION_TOKEN";
    /// <summary>ExampleMod as the runner sees it: its adapter's session, the Observe pack's Harmony capability, and the patch it declares.</summary>
    public static readonly ModDeclaration Mod = new("examplemod.testing/session", SessionTokenVariable)
    {
        HarmonyCapability = "valheim.observe/harmony", Owner = ModPlugin, Patches = MarkerScenario.Patches,
    };
    // The mod's rule inputs (see ModWithTests' DrySiteRule): the sea at 30 m, 1.5 m of clearance.
    public const float WaterLevel = 30f, Clearance = 1.5f;

    /// <summary>A site whose generator ground is at least 31.5 m: the mod must place its marker there.</summary>
    public Site DrySite { get; set; } = new();
    /// <summary>A site whose generator ground is below 31.5 m: the mod must refuse it.</summary>
    public Site WetSite { get; set; } = new();
    /// <summary>Dry ground a few metres from the dry site, where the player stands to look at the marker.</summary>
    public Site Arrival { get; set; } = new();
    /// <summary>The client that joins and looks: a client without the mod, which must still see the marker.</summary>
    public ClientRunPlan? Client { get; set; }
    public string WorldUid => Pins["worlduid"];

    /// <summary>The plan's rules, on a read plan or on a session's plan once bound to its prepared actors.</summary>
    public static MarkerPlan Validated(MarkerPlan plan)
    {
        // The scenario joins and checks by the pinned world uid, so this example has no unpinned mode.
        if (!plan.Pinned || plan.Client is { Pinned: false })
            throw new ArgumentException("This example runs with strict pins only: remove \"pinning\".");
        plan.ValidateServerPlan([ModPlugin, AdapterPlugin, "valheimCLI.valheimCLI"], SessionTokenVariable);
        plan.RequireScenario(ScenarioName);
        var client = plan.Client ?? throw new ArgumentException("The scenario looks from a client: add the client section.");
        client.Validate(ModPlugin); // The claim is what a client without the mod sees.
        plan.CheckSites();
        return plan;
    }

    /// <summary>
    /// The sites' own rules. The plan must agree with itself before any game starts: a "dry" site declared under the rule's
    /// threshold would make a correct mod fail, and the reverse would let a broken one pass.
    /// </summary>
    public void CheckSites()
    {
        DrySite.Validate("dry site"); WetSite.Validate("wet site"); Arrival.Validate("arrival point");
        if (DrySite.Ground < WaterLevel + Clearance) throw new ArgumentException($"The dry site's declared ground {DrySite.Ground} m is below {WaterLevel + Clearance} m.");
        if (WetSite.Ground >= WaterLevel + Clearance) throw new ArgumentException($"The wet site's declared ground {WetSite.Ground} m is not below {WaterLevel + Clearance} m.");
        if (MathF.Abs(DrySite.X - WetSite.X) < 50 && MathF.Abs(DrySite.Z - WetSite.Z) < 50)
            throw new ArgumentException("Keep the sites at least 50 m apart so one site's marker can never be counted at the other.");
        float fromMarker = MathF.Sqrt(MathF.Pow(Arrival.X - DrySite.X, 2) + MathF.Pow(Arrival.Z - DrySite.Z, 2));
        if (fromMarker is < 3 or > 20) throw new ArgumentException("Put the arrival point 3 to 20 m from the dry site: beside the marker, not on it.");
        if (Arrival.Ground < WaterLevel + Clearance) throw new ArgumentException("The arrival point must be dry ground; a swimming player is not supported.");
    }
}

public sealed class Site
{
    public float X { get; set; }
    public float Z { get; set; }
    /// <summary>The generator's ground height there, as the fixture's author read it.</summary>
    public float Ground { get; set; } = float.NaN;
    public void Validate(string what)
    {
        if (!float.IsFinite(X) || !float.IsFinite(Z) || MathF.Abs(X) > 10000 || MathF.Abs(Z) > 10000) throw new ArgumentException($"The {what} needs finite coordinates inside the world.");
        if (!float.IsFinite(Ground) || Ground < -100 || Ground > 1000) throw new ArgumentException($"Declare the {what}'s ground height.");
    }
}
