using ExampleMod.Tests;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

if (args is not [var campaignFile, var resultDirectory])
{
    Console.Error.WriteLine("Usage: dotnet run -- RUN_OUTPUT/campaign.json NEW_RESULT_DIRECTORY");
    return 2;
}
string directory = Path.GetDirectoryName(Path.GetFullPath(campaignFile))!;
var plan = ServerRunPlan.Read<MarkerPlan>(Path.Combine(directory, "marker-plan.json"));
plan.Client = ReadClient("client-plan.json");
if (plan.Scenario == MarkerPlan.TwoClientScenarioName)
    plan.SecondClient = ReadClient("second-client-plan.json");
int passwordIndex = plan.Arguments.ToList().IndexOf("-password");
if (passwordIndex < 0 || passwordIndex + 1 >= plan.Arguments.Length)
    throw new InvalidDataException("The server plan has no join password argument.");
foreach (var client in MarkerPlan.Clients(plan).Values)
{
    string passwordVariable = client.PasswordVariable ?? throw new InvalidDataException("The client plan has no password variable.");
    if (string.IsNullOrWhiteSpace(passwordVariable)) throw new InvalidDataException("The client plan has no password variable.");
    Environment.SetEnvironmentVariable(passwordVariable, plan.Arguments[passwordIndex + 1]);
}
return await PinnedServerRun.RunCampaignAsync(campaignFile, plan,
    MarkerPlan.Clients, resultDirectory,
    MarkerScenario.RunnerOptions((session, bound) =>
    {
        MarkerScenario.Run(session, bound);
        return Task.CompletedTask;
    }));

ClientRunPlan ReadClient(string name)
{
    string file = Path.Combine(directory, name);
    if (!File.Exists(file)) throw new InvalidDataException("The scenario needs " + name + " beside its campaign.");
    return System.Text.Json.JsonSerializer.Deserialize<ClientRunPlan>(File.ReadAllText(file))
        ?? throw new InvalidDataException("The client plan is empty: " + name);
}
