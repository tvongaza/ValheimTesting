using Valheim.Testing.Game;
using Xunit;

// #298 removed direct start and prepared-character start. A plan that still names one of their fields gets what to do
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
        Assert.Contains("launches to its menu and joins", message);
    }

    [Fact] public void AWrittenPlanCarriesNoRemovedField()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new ClientRunPlan { Mode = "owned" });
        foreach (string field in new[] { "directStart", "startAtCharacterSave", "characterStart" })
            Assert.DoesNotContain(field, json, StringComparison.OrdinalIgnoreCase);
    }
}
