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
string clientFile = Path.Combine(directory, "client-plan.json");
if (!File.Exists(clientFile)) throw new InvalidDataException("The persistence scenario needs the emitted client plan.");
plan.Client = System.Text.Json.JsonSerializer.Deserialize<ClientRunPlan>(File.ReadAllText(clientFile))
    ?? throw new InvalidDataException("The client plan is empty.");
int passwordIndex = plan.Arguments.ToList().IndexOf("-password");
if (passwordIndex < 0 || passwordIndex + 1 >= plan.Arguments.Length)
    throw new InvalidDataException("The server plan has no join password argument.");
string passwordVariable = plan.Client.PasswordVariable ?? throw new InvalidDataException("The client plan has no password variable.");
if (string.IsNullOrWhiteSpace(passwordVariable)) throw new InvalidDataException("The client plan has no password variable.");
Environment.SetEnvironmentVariable(passwordVariable, plan.Arguments[passwordIndex + 1]);
return await PinnedServerRun.RunCampaignAsync(campaignFile, plan,
    p => new Dictionary<string, ClientRunPlan> { ["client"] = p.Client! }, resultDirectory,
    MarkerScenario.RunnerOptions((session, bound) =>
    {
        MarkerScenario.Run(session, bound);
        return Task.CompletedTask;
    }));
