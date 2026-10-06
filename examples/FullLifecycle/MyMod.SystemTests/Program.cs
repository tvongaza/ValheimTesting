using MyMod.SystemTests;
using Valheim.Testing.Game;

// The toolkit's pinned dedicated-server runner (PinnedServerRun) with this mod's plan and scenario. The toolkit owns the
// lifecycle: plan checks, fixture copies, provenance, the owned server and its startup events, teardown, the report and
// the result banner. The mod supplies the plan fields, the session capability its test adapter serves, and the scenario.
// prepare-server creates a fresh world and writes a dry-site-server plan for it (see ServerFixture); it runs before any
// plan exists, so it is outside the pinned runner. A hosted run has no dedicated server to pin: validate-host and host are
// the toolkit runner's hosted modes, where the session's host is a client that hosts the fixture world (HostedScenario).
if (args.Length > 0 && args[0] == ServerFixture.Mode) return ServerFixture.Run(args);
if (args.Length > 0 && args[0] is PinnedServerRun.HostMode or PinnedServerRun.ValidateHostMode) return await PinnedServerRun.MainAsync(args, HostedScenario.RunnerOptions());
// The runner's options: MyMod's plan rules and declaration, and every scenario by name (ScenarioTable).
var options = ScenarioTable.RunnerOptions();
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
    try
    {
        template = ServerRunPlan.Read<LifecyclePlan>(templateFile);
        var named = ScenarioTable.CampaignClientsFor(template); // Refuses a scenario that does not run as a campaign.
        if (args[1] == "check")
        {
            // The same Preflight the run starts with: the campaign's inputs and actor assignment, then the plan's agreement with it.
            HostedCampaignPreparation.CheckPlan(manifestFile, template, named);
            Console.WriteLine("ELIGIBLE: reviewed mod and ValheimCLI locks, fixture, independent disposable characters, actor assignment and plan. No host was contacted.");
            return 0;
        }
    }
    catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine("Campaign setup: " + error.Message);
        return 2;
    }
    return await PinnedServerRun.RunCampaignAsync(manifestFile, template, ScenarioTable.CampaignClientsFor, rest[0], options).ConfigureAwait(false);
}
