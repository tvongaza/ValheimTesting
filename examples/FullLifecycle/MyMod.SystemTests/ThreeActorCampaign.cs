using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>
/// The example's campaign command around the toolkit's campaign runner (<see cref="PinnedServerRun.RunCampaignAsync{TPlan}"/>).
/// The manifest declares the actors (a dedicated server and named clients) and names the private inventory they are placed
/// from; this example's scenarios read client-a and client-b. It never modifies the source game installs.
/// </summary>
public static class ThreeActorCampaign
{
    public static bool Handles(string[] args) => args.Length > 0 && args[0] == "campaign";

    // The example's two client sections, by the campaign's client names.
    private static IReadOnlyDictionary<string, ClientRunPlan> Clients(LifecyclePlan plan) => new Dictionary<string, ClientRunPlan>
    {
        ["client-a"] = plan.Client ?? throw new ArgumentException("The campaign template needs a client section."),
        ["client-b"] = plan.SecondClient ?? throw new ArgumentException("The campaign template needs a secondClient section."),
    };

    public static async Task<int> RunAsync(string[] args, PinnedServerRunOptions<LifecyclePlan> options)
    {
        if (args is not ["campaign", "check" or "run", var manifestFile, var templateFile, .. var rest] ||
            (args[1] == "check" ? rest.Length != 0 : rest.Length != 1))
        {
            Console.Error.WriteLine("Usage: MyMod.SystemTests campaign check <campaign.json> <scenario-template.json> | campaign run <campaign.json> <scenario-template.json> <new-output-directory>");
            return 2;
        }
        LifecyclePlan template;
        try
        {
            template = ServerRunPlan.Read<LifecyclePlan>(templateFile);
            if (template.Scenario is not (LifecyclePlan.ThreeActorScenario or LifecyclePlan.OwnershipHandoffScenario))
                throw new ArgumentException("This example campaign runs three-actor-smoke or ownership-handoff.");
            if (args[1] == "check")
            {
                // The same Preflight the run starts with: the campaign's inputs and actor assignment, then the plan's agreement with it.
                HostedCampaignPreparation.CheckPlan(manifestFile, template, Clients(template));
                Console.WriteLine("ELIGIBLE: reviewed mod and ValheimCLI locks, fixture, independent disposable characters, actor assignment and plan. No host was contacted.");
                return 0;
            }
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine("Campaign setup: " + error.Message);
            return 2;
        }
        return await PinnedServerRun.RunCampaignAsync(manifestFile, template, Clients, rest[0], options).ConfigureAwait(false);
    }
}
