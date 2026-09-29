using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>
/// The example's plan: the toolkit's pinned server plan (runtime, world, launch, pins, port) plus what this mod's
/// scenario needs. The expectations are declared here by whoever prepared the fixture, from the world's generator
/// heights; the runner never derives them from the mod's own replies.
/// </summary>
public sealed class LifecyclePlan : ServerRunPlan
{
    public const string SessionTokenVariable = "MYMOD_TEST_SESSION_TOKEN";
    public const string ModPlugin = "example.mymod";
    public const string AdapterPlugin = "example.mymod.testadapter";
    // The mod's rule inputs (see ModWithTests' DrySiteRule): the sea at 30 m, 1.5 m of clearance.
    public const float WaterLevel = 30f, Clearance = 1.5f;

    /// <summary>A site whose generator ground is at least 31.5 m: the mod must place its marker there.</summary>
    public Site DrySite { get; set; } = new();
    /// <summary>A site whose generator ground is below 31.5 m: the mod must refuse it.</summary>
    public Site WetSite { get; set; } = new();
    /// <summary>Dry ground a few metres from the dry site, where the player stands to look at the marker.</summary>
    public Site Arrival { get; set; } = new();
    /// <summary>The client that joins and looks; required for <c>run</c>.</summary>
    public ClientRunPlan? Client { get; set; }
    /// <summary>Optional human checkpoint; never part of pass or fail.</summary>
    public ReviewSettings Review { get; set; } = new();

    public static LifecyclePlan ReadValidated(string path)
    {
        var plan = Read<LifecyclePlan>(path);
        // The scenario joins and checks by the pinned world uid, so this example has no unpinned mode.
        if (!plan.Pinned || plan.Client is { Pinned: false }) throw new ArgumentException("This example runs with strict pins only: remove \"pinning\".");
        plan.ValidateServerPlan([ModPlugin, AdapterPlugin, "valheimCLI.valheimCLI"], SessionTokenVariable);
        if (plan.Scenario != "dry-site-lifecycle") throw new ArgumentException("This runner only runs the dry-site-lifecycle scenario.");
        plan.DrySite.Validate("dry site", requireGround: true); plan.WetSite.Validate("wet site", requireGround: true); plan.Arrival.Validate("arrival point", requireGround: true);
        // The plan must agree with itself before any game starts: a "dry" site declared under the rule's threshold would
        // make a correct mod fail, and the reverse would let a broken one pass.
        if (plan.DrySite.Ground < WaterLevel + Clearance) throw new ArgumentException($"The dry site's declared ground {plan.DrySite.Ground} m is below {WaterLevel + Clearance} m.");
        if (plan.WetSite.Ground >= WaterLevel + Clearance) throw new ArgumentException($"The wet site's declared ground {plan.WetSite.Ground} m is not below {WaterLevel + Clearance} m.");
        if (MathF.Abs(plan.DrySite.X - plan.WetSite.X) < 50 && MathF.Abs(plan.DrySite.Z - plan.WetSite.Z) < 50)
            throw new ArgumentException("Keep the sites at least 50 m apart so one site's marker can never be counted at the other.");
        float fromMarker = MathF.Sqrt(MathF.Pow(plan.Arrival.X - plan.DrySite.X, 2) + MathF.Pow(plan.Arrival.Z - plan.DrySite.Z, 2));
        if (fromMarker is < 3 or > 20) throw new ArgumentException("Put the arrival point 3 to 20 m from the dry site: beside the marker, not on it.");
        if (plan.Arrival.Ground < WaterLevel + Clearance) throw new ArgumentException("The arrival point must be dry ground; a swimming player is not supported.");
        plan.Client?.Validate(ModPlugin); // A server-only mod: the claim is what a client without it sees.
        plan.Review.Validate();
        return plan;
    }
    public string WorldUid => Pins["worlduid"];
}

public sealed class Site
{
    public float X { get; set; }
    public float Z { get; set; }
    /// <summary>The generator's ground height there, as the fixture's author read it.</summary>
    public float Ground { get; set; } = float.NaN;
    public void Validate(string what, bool requireGround)
    {
        if (!float.IsFinite(X) || !float.IsFinite(Z) || MathF.Abs(X) > 10000 || MathF.Abs(Z) > 10000) throw new ArgumentException($"The {what} needs finite coordinates inside the world.");
        if (requireGround && (!float.IsFinite(Ground) || Ground < -100 || Ground > 1000)) throw new ArgumentException($"Declare the {what}'s ground height.");
    }
}

/// <summary>
/// A person's look at the result, after the automated checks and never instead of them: the runner leaves the player at
/// the dry site and waits up to <see cref="Seconds"/> for <c>review.json</c> in the output directory. The verdict is
/// recorded beside the automated result and does not change it.
/// </summary>
public sealed class ReviewSettings
{
    public bool Enabled { get; set; }
    public int Seconds { get; set; } = 600;
    public void Validate() { if (Enabled && Seconds is < 10 or > 7200) throw new ArgumentException("Review wait is 10 s to 2 h."); }
}
