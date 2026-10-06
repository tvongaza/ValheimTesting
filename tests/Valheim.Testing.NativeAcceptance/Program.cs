using Valheim.Testing.Game;
using Valheim.Testing.NativeAcceptance;
using Valheim.Testing.GameSessions;

// The native acceptance suite's console runner: the toolkit's pinned dedicated-server runner (PinnedServerRun) with the suite's
// plan (AcceptancePlan) and every scenario by name (ScenarioTable), on the FullLifecycle example's MyMod. validate and run take
// a plan whose actors are on this machine; campaign check and campaign run take a session manifest whose inventory places them.
// A hosted run has no dedicated server to pin: validate-host and host are the toolkit runner's hosted modes, where the
// session's host is a client that hosts the fixture world (HostedScenario). A fresh world and its server plan come from the
// example's prepare-server.
// --in-place (before the mode) runs each owned client from its install as it is instead of a disposable copy.
string? mode = args.SkipWhile(arg => arg == PinnedServerRun.InPlaceOption).FirstOrDefault();
if (mode is PinnedServerRun.HostMode or PinnedServerRun.ValidateHostMode) return await PinnedServerRun.MainAsync(args, HostedScenario.RunnerOptions());
// The runner's options: the suite's plan rules, MyMod's declaration, and every scenario by name (ScenarioTable).
var options = ScenarioTable.RunnerOptions();
if (args.Length > 0 && args[0] == "campaign") return await Campaign(args, options);
return await PinnedServerRun.MainAsync(args, options);

// The suite's campaign command around the toolkit's campaign runner (PinnedServerRun.RunCampaignAsync): the manifest declares
// the actors (a dedicated server and named clients, or, for the hosted scenario, no server: a host and its peer) and the private
// inventory they are placed from; the scenario's table entry names which of the plan's client sections are client-a and
// client-b (the hosted plan's: host and peer). It never modifies the source game installs.
static async Task<int> Campaign(string[] args, PinnedServerRunOptions<AcceptancePlan> options)
{
    if (args is not ["campaign", "check" or "run", var manifestFile, var templateFile, .. var rest] || (args[1] == "check" ? rest.Length != 0 : rest.Length != 1))
    {
        Console.Error.WriteLine("Usage: Valheim.Testing.NativeAcceptance campaign check <campaign.json> <scenario-template.json> | campaign run <campaign.json> <scenario-template.json> <new-output-directory>");
        return 2;
    }
    AcceptancePlan? template = null;
    HostedPlan? hosted = null;
    try
    {
        // A hosted template (one client hosts, its peer joins it) runs on a campaign without a dedicated server.
        if (ScenarioOf(templateFile) == HostedPlan.HostedScenarioName)
        {
            hosted = HostedPlan.CheckTemplate(HostedPlan.Read(templateFile)); // Before any host is written.
            if (args[1] == "check")
            {
                HostedCampaignPreparation.CheckHostedPlan(manifestFile, HostedScenario.CampaignClients(hosted));
                Console.WriteLine("ELIGIBLE: reviewed mod and ValheimCLI locks, fixture, independent disposable characters, actor assignment and plan. No host was contacted.");
                return 0;
            }
        }
        else
        {
            template = ServerRunPlan.Read<AcceptancePlan>(templateFile);
            var named = ScenarioTable.CampaignClientsFor(template); // Refuses a scenario that does not run as a campaign.
            if (args[1] == "check")
            {
                // The same Preflight the run starts with: the campaign's inputs and actor assignment, then the plan's agreement with it.
                HostedCampaignPreparation.CheckPlan(manifestFile, template, named);
                Console.WriteLine("ELIGIBLE: reviewed mod and ValheimCLI locks, fixture, independent disposable characters, actor assignment and plan. No host was contacted.");
                return 0;
            }
        }
    }
    catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine("Campaign setup: " + error.Message);
        return 2;
    }
    return hosted != null
        ? await PinnedServerRun.RunCampaignAsync(manifestFile, hosted, HostedScenario.CampaignClients, rest[0], HostedScenario.RunnerOptions()).ConfigureAwait(false)
        : await PinnedServerRun.RunCampaignAsync(manifestFile, template!, ScenarioTable.CampaignClientsFor, rest[0], options).ConfigureAwait(false);
}

// The template's scenario name, read before choosing its plan type.
static string? ScenarioOf(string templateFile)
{
    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(templateFile));
    return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && document.RootElement.TryGetProperty("scenario", out var scenario)
        && scenario.ValueKind == System.Text.Json.JsonValueKind.String ? scenario.GetString() : null;
}
