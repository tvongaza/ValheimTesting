using System.Text.Json.Serialization;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

namespace Valheim.Testing.NativeAcceptance;

/// <summary>
/// The dry-site plan: the toolkit's pinned server plan (runtime, world, launch, pins, port) plus what AcceptanceMod's
/// dry-site scenarios need. The expectations are declared here by whoever prepared the fixture, from the world's generator
/// heights; the runner never derives them from the mod's own replies. <see cref="AcceptancePlan"/> extends it with the
/// suite's further scenarios, so its rules have four extension points.
/// </summary>
public class LifecyclePlan : ServerRunPlan
{
    public const string SessionTokenVariable = "ACCEPTANCEMOD_TEST_SESSION_TOKEN";
    /// <summary>AcceptanceMod as the runner sees it: its adapter's session and the Observe pack's Harmony capability and the patches it declares.</summary>
    public static readonly ModDeclaration Mod = new("acceptancemod.testing/session", SessionTokenVariable)
    {
        HarmonyCapability = HarmonyCapability, Owner = ModPlugin, Patches = DrySiteScenario.Patches,
    };
    /// <summary>The full scenario (<see cref="DrySiteScenario"/>) and its server half alone (<see cref="DrySiteServerScenario"/>).</summary>
    public const string LifecycleScenario = "dry-site-lifecycle", ServerScenario = "dry-site-server";
    public const string ModPlugin = "valheimtesting.acceptancemod";
    public const string AdapterPlugin = "valheimtesting.acceptancemod.adapter";
    /// <summary>ValheimCLI's Observe-pack census of applied Harmony patches.</summary>
    public const string HarmonyCapability = "valheim.observe/harmony";
    // The mod's rule inputs (see ModWithTests' DrySiteRule): the sea at 30 m, 1.5 m of clearance.
    public const float WaterLevel = 30f, Clearance = 1.5f;

    /// <summary>A site whose generator ground is at least 31.5 m: the mod must place its marker there.</summary>
    public Site DrySite { get; set; } = new();
    /// <summary>A site whose generator ground is below 31.5 m: the mod must refuse it.</summary>
    public Site WetSite { get; set; } = new();
    /// <summary>Dry ground a few metres from the dry site, where the player stands to look at the marker. Unused by the server scenario.</summary>
    public Site Arrival { get; set; } = new();
    /// <summary>The client that joins and looks; required for <c>run</c> of the lifecycle scenario, refused by the server scenario.</summary>
    public ClientRunPlan? Client { get; set; }
    /// <summary>Optional human checkpoint; never part of pass or fail.</summary>
    public ReviewSettings Review { get; set; } = new();

    public static LifecyclePlan ReadValidated(string path) => Validated(Read<LifecyclePlan>(path));

    /// <summary>The plan's rules on a plan in memory: a read plan, or a session's plan once bound to its prepared actors.</summary>
    public static TPlan Validated<TPlan>(TPlan plan) where TPlan : LifecyclePlan { plan.Validate(); return plan; }

    /// <summary>The scenarios this plan runs.</summary>
    [JsonIgnore] public virtual IReadOnlyList<string> Scenarios => [LifecycleScenario, ServerScenario];
    /// <summary>Whether the scenario marks the dry site and refuses the wet one (both dry-site scenarios do).</summary>
    [JsonIgnore] public virtual bool MarksSites => true;
    /// <summary>Every client section, each of which runs with strict pins.</summary>
    [JsonIgnore] protected virtual IEnumerable<ClientRunPlan?> ClientSections => [Client];
    /// <summary>A scenario's own rules, checked after the server plan's and before the sites'.</summary>
    protected virtual void ValidateScenario() { }

    private void Validate()
    {
        // The scenario joins and checks by the pinned world uid, so this suite has no unpinned mode.
        if (!Pinned || ClientSections.Any(client => client is { Pinned: false }))
            throw new ArgumentException("This suite runs with strict pins only: remove \"pinning\".");
        ValidateServerPlan([ModPlugin, AdapterPlugin, "valheimCLI.valheimCLI"], SessionTokenVariable);
        RequireScenario([.. Scenarios]);
        ValidateScenario();
        if (!MarksSites) return;
        CheckSites(DrySite, WetSite);
        if (ServerOnly)
        {
            // Nothing looks from a client here: a client or review section would suggest a check this scenario never makes.
            if (Client != null || Review.Enabled) throw new ArgumentException($"The {ServerScenario} scenario has no client and no review; remove those sections or use {LifecycleScenario}.");
            return;
        }
        Arrival.Validate("arrival point", requireGround: true);
        float fromMarker = MathF.Sqrt(MathF.Pow(Arrival.X - DrySite.X, 2) + MathF.Pow(Arrival.Z - DrySite.Z, 2));
        if (fromMarker is < 3 or > 20) throw new ArgumentException("Put the arrival point 3 to 20 m from the dry site: beside the marker, not on it.");
        if (Arrival.Ground < WaterLevel + Clearance) throw new ArgumentException("The arrival point must be dry ground; a swimming player is not supported.");
        if (Scenario == LifecycleScenario) Client?.Validate(ModPlugin); // A server-only mod: the claim is what a client without it sees.
        Review.Validate();
    }

    /// <summary>
    /// The sites' own rules. The plan must agree with itself before any game starts: a "dry" site declared under the rule's
    /// threshold would make a correct mod fail, and the reverse would let a broken one pass.
    /// </summary>
    public static void CheckSites(Site dry, Site wet)
    {
        dry.Validate("dry site", requireGround: true); wet.Validate("wet site", requireGround: true);
        if (dry.Ground < WaterLevel + Clearance) throw new ArgumentException($"The dry site's declared ground {dry.Ground} m is below {WaterLevel + Clearance} m.");
        if (wet.Ground >= WaterLevel + Clearance) throw new ArgumentException($"The wet site's declared ground {wet.Ground} m is not below {WaterLevel + Clearance} m.");
        if (MathF.Abs(dry.X - wet.X) < 50 && MathF.Abs(dry.Z - wet.Z) < 50)
            throw new ArgumentException("Keep the sites at least 50 m apart so one site's marker can never be counted at the other.");
    }
    public string WorldUid => Pins["worlduid"];
    public bool ServerOnly => Scenario == ServerScenario;
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
