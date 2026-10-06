using MyMod.SystemTests;
using Valheim.Testing.Game;

// The toolkit's pinned dedicated-server runner (PinnedServerRun) with this mod's plan and scenario. The toolkit owns the
// lifecycle: plan checks, fixture copies, provenance, the owned server and its startup events, teardown, the report and
// the result banner. The mod supplies the plan fields, the session capability its test adapter serves, and the scenario.
// prepare-server creates a fresh world and writes a dry-site-server plan for it (see ServerFixture); it runs before any
// plan exists, so it is outside the pinned runner. A hosted run has no dedicated server to pin, so validate-host and host
// have their own entry point (HostedRun).
if (args.Length > 0 && args[0] == ServerFixture.Mode) return ServerFixture.Run(args);
if (args.Length > 0 && args[0] is HostedRun.RunMode or HostedRun.ValidateMode) return HostedRun.Run(args);
var options = new PinnedServerRunOptions<LifecyclePlan>
{
    Name = "mymod-system-test",
    ReadPlan = path =>
    {
        var plan = LifecyclePlan.ReadValidated(path);
        // Two simultaneous clients are a campaign's actors (an inventory assigns their hosts and Steam identities).
        if (plan.Scenario is LifecyclePlan.ThreeActorScenario or LifecyclePlan.OwnershipHandoffScenario)
            throw new ArgumentException($"The {plan.Scenario} scenario runs as a campaign: campaign run <campaign.json> <plan.json> <new-output-directory>.");
        return plan;
    },
    // The session capability and token variable MyMod's test adapter serves, and its Harmony patches, which the session
    // checks on the server before any scenario step.
    Mod = LifecyclePlan.Mod,
    CheckPlan = plan =>
    {
        if (plan.Client == null && !plan.ServerOnly)
            throw new ArgumentException($"A run looks from a client: add the client section, or use the {LifecyclePlan.ServerScenario} scenario for the server half alone.");
        // A campaign template is bound to its prepared actors in memory; its own rules apply to the bound plan.
        if (plan.Scenario is LifecyclePlan.ThreeActorScenario or LifecyclePlan.OwnershipHandoffScenario) LifecyclePlan.Validated(plan);
    },
    Provenance = (plan, provenance) =>
    {
        provenance["clientMode"] = plan.Client?.Mode ?? "none";
        provenance["humanReview"] = plan.Review.Enabled ? "requested" : "not requested";
        provenance["expectFailure"] = plan.ExpectFailure ?? "none";
    },
    // Every scenario by name: ScenarioTable.
    Scenario = ScenarioTable.Run,
};
if (args.Length > 0 && args[0] == "campaign") return await Campaign(args, options);
return await PinnedServerRun.MainAsync(args, options);

// The example's campaign command around the toolkit's campaign runner (PinnedServerRun.RunCampaignAsync): the manifest declares
// the actors (a dedicated server and named clients) and the private inventory they are placed from; the scenario's table entry
// names which of the plan's client sections are client-a and client-b. It never modifies the source game installs.
static async Task<int> Campaign(string[] args, PinnedServerRunOptions<LifecyclePlan> options)
{
    if (args is not ["campaign", "check" or "run", var manifestFile, var templateFile, .. var rest] || (args[1] == "check" ? rest.Length != 0 : rest.Length != 1))
    {
        Console.Error.WriteLine("Usage: MyMod.SystemTests campaign check <campaign.json> <scenario-template.json> | campaign run <campaign.json> <scenario-template.json> <new-output-directory>");
        return 2;
    }
    LifecyclePlan template;
    Func<LifecyclePlan, IReadOnlyDictionary<string, ClientRunPlan>> clients;
    try
    {
        template = ServerRunPlan.Read<LifecyclePlan>(templateFile);
        clients = ScenarioTable.Find(template.Scenario)?.CampaignClients
            ?? throw new ArgumentException($"This example campaign runs {LifecyclePlan.ThreeActorScenario} or {LifecyclePlan.OwnershipHandoffScenario}.");
        if (args[1] == "check")
        {
            // The same Preflight the run starts with: the campaign's inputs and actor assignment, then the plan's agreement with it.
            HostedCampaignPreparation.CheckPlan(manifestFile, template, clients(template));
            Console.WriteLine("ELIGIBLE: reviewed mod and ValheimCLI locks, fixture, independent disposable characters, actor assignment and plan. No host was contacted.");
            return 0;
        }
    }
    catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine("Campaign setup: " + error.Message);
        return 2;
    }
    return await PinnedServerRun.RunCampaignAsync(manifestFile, template, clients, rest[0], options).ConfigureAwait(false);
}
