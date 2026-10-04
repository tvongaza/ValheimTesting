using Valheim.Testing.Game;
using Xunit;

// #298 removed direct start and prepared-character start; #299 the arrival options. A plan that still names one of their fields gets what to do
// instead of the reader's generic unknown-field error.
public sealed class RemovedStartOptionsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("removed-start-").FullName;
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("directStart", "true")]
    [InlineData("directStartWorldUid", "\"77\"")]
    [InlineData("startAtCharacterSave", "false")]
    [InlineData("characterStart", "{ \"preparedFile\": \"x\" }")]
    public void APlanThatStillNamesARemovedStartOptionIsRefusedNamingTheMenuStart(string field, string value)
    {
        string path = Path.Combine(_directory, "plan.json");
        File.WriteAllText(path, $$"""{ "client": { "mode": "owned", "{{field}}": {{value}} } }""");
        var error = Assert.ThrowsAny<Exception>(() => ServerRunPlan.Read<CrossplayPlanTests.ClientPlan>(path));
        string message = error.Message + " " + error.InnerException?.Message;
        Assert.Contains($"The client plan's {field} was removed (ValheimTesting #298)", message);
        Assert.Contains($"launches to its menu and joins (or hosts), and the first arrival teleports. Delete {field} from the plan.", message);
    }

    [Theory]
    [InlineData("fastTestTeleports", "true", "arrival uses the game's ordinary teleport timing")]
    [InlineData("eventDrivenArrival", "true", "every arrival now uses the game-side signal waits")]
    [InlineData("eventDrivenArrival", "false", "every arrival now uses the game-side signal waits")]
    public void APlanThatStillNamesARemovedArrivalOptionIsRefused(string field, string value, string instead)
    {
        string path = Path.Combine(_directory, "plan.json");
        File.WriteAllText(path, $$"""{ "client": { "mode": "owned", "{{field}}": {{value}} } }""");
        var error = Assert.ThrowsAny<Exception>(() => ServerRunPlan.Read<CrossplayPlanTests.ClientPlan>(path));
        string message = error.Message + " " + error.InnerException?.Message;
        Assert.Contains($"{field} was removed (ValheimTesting #299)", message);
        Assert.Contains(instead, message);
    }

    [Fact] public void AWrittenPlanCarriesNoRemovedField()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new ClientRunPlan { Mode = "owned" });
        foreach (string field in new[] { "directStart", "startAtCharacterSave", "characterStart", "fastTestTeleports", "eventDrivenArrival" })
            Assert.DoesNotContain(field, json, StringComparison.OrdinalIgnoreCase);
    }
}
