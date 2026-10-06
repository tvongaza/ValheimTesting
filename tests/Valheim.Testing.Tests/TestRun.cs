using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>
/// The runner tests' view of a launched run, over the <see cref="GameSession"/> and plan a scenario gets (#258 step 4), with
/// the names the tests were written against: the server's current boot, the owned server, the copies and the client opens.
/// </summary>
internal sealed class TestRun<TPlan>(GameSession session, TPlan plan) where TPlan : ServerRunPlan
{
    public GameSession GameSession => session;
    public TPlan Plan => plan;
    public ScenarioReport Report => session.Report;
    public string Output => session.Output;
    public CancellationToken Cancellation => session.Cancellation;
    public GameActor Server => session.Server!.Game;
    public ServerActor Session => session.Server!;
    public string RuntimeDirectory => session.Server!.RuntimeDirectory;
    public string WorldDirectory => session.Server!.WorldDirectory;
    public IGameHost? ServerHost => session.Server!.Host;
    public IReadOnlyList<string> CampaignClients => session.CampaignClients;
    public ClientSession OpenClient(ClientRunPlan client, string? name = null) => session.OpenClient(client, name);
}

internal static class TestRun
{
    /// <summary>A scenario written against <see cref="TestRun{TPlan}"/>, as the runner calls it.</summary>
    public static Func<GameSession, TPlan, Task> Scenario<TPlan>(Func<TestRun<TPlan>, Task> scenario) where TPlan : ServerRunPlan =>
        (session, plan) => scenario(new TestRun<TPlan>(session, plan));
}
