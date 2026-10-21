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
    public const string TwoClientScenarioName = "dry-site-two-clients";
    public const string FirstClientName = "client", SecondClientName = "client-b";
    public const string ModPlugin = "example.examplemod", AdapterPlugin = "example.examplemod.testadapter";
    public const string SessionTokenVariable = "EXAMPLEMOD_TEST_SESSION_TOKEN";
    /// <summary>ExampleMod as the runner sees it: its adapter's session, the default ValheimCLI plugin's Harmony capability, and the patch it declares.</summary>
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
    /// <summary>Optional second clean client for simultaneous visibility and rejoin, with its own registered test character.</summary>
    public ClientRunPlan? SecondClient { get; set; }
    /// <summary>Measured dry arrival for the second client, separate from the first arrival.</summary>
    public Site? SecondArrival { get; set; }
    /// <summary>Optional human-review still on the first joined round. Omit to keep normal runs fast and small.</summary>
    public ReviewStillSettings? ReviewStill { get; set; }
    public string WorldUid => Pins["worlduid"];

    /// <summary>The plan's rules, on a read plan or on a session's plan once bound to its prepared actors.</summary>
    public static MarkerPlan Validated(MarkerPlan plan)
    {
        plan.ValidateServerPlan([ModPlugin, AdapterPlugin, "valheimCLI.valheimCLI"], SessionTokenVariable);
        plan.RequireScenario(plan.SecondClient == null ? ScenarioName : TwoClientScenarioName);
        var client = plan.Client ?? throw new ArgumentException("The scenario looks from a client: add the client section.");
        client.Validate(ModPlugin); // The claim is what a client without the mod sees.
        plan.SecondClient?.Validate(ModPlugin);
        _ = Clients(plan);
        plan.CheckSites();
        plan.ReviewStill?.Validate();
        return plan;
    }

    /// <summary>
    /// The sites' own rules. The plan must agree with itself before any game starts: a "dry" site declared under the rule's
    /// threshold would make a correct mod fail, and the reverse would let a broken one pass.
    /// </summary>
    public void CheckSites()
    {
        RequireScenario(SecondClient == null ? ScenarioName : TwoClientScenarioName);
        CheckSites(DrySite, WetSite, Arrival);
        if ((SecondClient == null) != (SecondArrival == null))
            throw new ArgumentException("Supply secondClient and secondArrival together.");
        if (SecondClient == null) return;
        CheckSites(DrySite, WetSite, SecondArrival!);
        float apart = MathF.Sqrt(MathF.Pow(Arrival.X - SecondArrival!.X, 2) + MathF.Pow(Arrival.Z - SecondArrival.Z, 2));
        if (apart < 3) throw new ArgumentException("Keep the two client arrivals at least 3 m apart.");
        var first = Client ?? throw new ArgumentException("The session's plan needs its client section.");
        first.Validate(ModPlugin);
        SecondClient.Validate(ModPlugin);
        if (!first.Owned || !SecondClient.Owned || first.HostWorld != null || SecondClient.HostWorld != null || first.Crossplay || SecondClient.Crossplay)
            throw new ArgumentException("The two-client example uses two owned clients joining the dedicated server by address.");
        if (!string.Equals(first.Join, SecondClient.Join, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Both clients must join the same dedicated server address.");
        if (string.Equals(first.Character, SecondClient.Character, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Give the two clients distinct disposable character filenames.");
        if (ReviewStill != null) throw new ArgumentException("Omit reviewStill for the two-client visibility scenario.");
    }

    /// <summary>The same named plan objects used by campaign binding, the scenario and the native test fixture.</summary>
    public static IReadOnlyDictionary<string, ClientRunPlan> Clients(MarkerPlan plan)
    {
        var first = plan.Client ?? throw new ArgumentException("The session's plan needs its client section.");
        var clients = new Dictionary<string, ClientRunPlan> { [FirstClientName] = first };
        if (plan.SecondClient != null)
        {
            if (ReferenceEquals(first, plan.SecondClient)) throw new ArgumentException("The two named clients need separate plan objects.");
            clients.Add(SecondClientName, plan.SecondClient);
        }
        return clients;
    }

    /// <summary>The same site contract for dedicated and local-host runs.</summary>
    public static void CheckSites(Site drySite, Site wetSite, Site arrival)
    {
        drySite.Validate("dry site"); wetSite.Validate("wet site"); arrival.Validate("arrival point");
        if (drySite.Ground < WaterLevel + Clearance) throw new ArgumentException($"The dry site's declared ground {drySite.Ground} m is below {WaterLevel + Clearance} m.");
        if (wetSite.Ground >= WaterLevel + Clearance) throw new ArgumentException($"The wet site's declared ground {wetSite.Ground} m is not below {WaterLevel + Clearance} m.");
        if (MathF.Abs(drySite.X - wetSite.X) < 50 && MathF.Abs(drySite.Z - wetSite.Z) < 50)
            throw new ArgumentException("Keep the sites at least 50 m apart so one site's marker can never be counted at the other.");
        float fromMarker = MathF.Sqrt(MathF.Pow(arrival.X - drySite.X, 2) + MathF.Pow(arrival.Z - drySite.Z, 2));
        if (fromMarker is < 3 or > 20) throw new ArgumentException("Put the arrival point 3 to 20 m from the dry site: beside the marker, not on it.");
        if (arrival.Ground < WaterLevel + Clearance) throw new ArgumentException("The arrival point must be dry ground; a swimming player is not supported.");
    }
}

/// <summary>One optional screenshot, with a private, empty directory on the client's host. The image is evidence, not a visual assertion.</summary>
public sealed class ReviewStillSettings
{
    /// <summary>Absolute no-space path on the client host, unique to this run; no personal screenshot directory.</summary>
    public string HostDirectory { get; set; } = "";
    /// <summary>The exact game build shown in the image sidecar.</summary>
    public string GameBuild { get; set; } = "";
    public string Weather { get; set; } = "Clear";
    public float TimeOfDay { get; set; } = 0.45f;
    public float CameraDistance { get; set; } = 24f;
    public float CameraHeight { get; set; } = 12f;
    public float CameraAzimuthDegrees { get; set; } = 225f;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(HostDirectory) || string.IsNullOrWhiteSpace(GameBuild))
            throw new ArgumentException("A review still needs a private no-space hostDirectory and the exact gameBuild.");
    }

    public ReviewCapturePlan For(MarkerPlan plan, string output) => new("example-marker",
        new HeightExpectation(plan.Arrival.X, plan.Arrival.Z, plan.Arrival.Ground), Weather, TimeOfDay,
        CameraDistance, CameraHeight, HostDirectory, Path.Combine(output, "review-marker"), plan.WorldUid,
        GameBuild, plan.Client!.Pins, CameraAzimuthDegrees: CameraAzimuthDegrees);
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
