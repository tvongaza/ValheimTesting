using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// A mod-owned assertion that runs inside one strictly pinned <c>valheim-test server-load</c> campaign.
/// The toolkit prepares the server, optional clean client, fixture, dependencies, plan and evidence before calling this.
/// Implement this interface in a .NET 10 test assembly and pass that assembly with <c>--scenario</c>.
/// </summary>
public interface IOneShotServerScenario
{
    /// <summary>A short name recorded with the private run evidence.</summary>
    string Name { get; }

    /// <summary>Whether this assertion opens the prepared clean client. When true, omit <c>--server-only</c>.</summary>
    bool RequiresClient { get; }

    /// <summary>
    /// Exercise and assert using the same owned actors and report as the one-shot preparation.
    /// A client scenario can call <see cref="GameSession.OpenClient(ClientRunPlan, string?, string?)"/>
    /// with <see cref="OneShotServerContext.ClientPlan"/>; teardown still owns that client if the scenario fails.
    /// </summary>
    Task RunAsync(GameSession session, OneShotServerContext context);
}

/// <summary>The strict plans and fixture identity prepared for a mod-owned one-shot server scenario.</summary>
public sealed class OneShotServerContext
{
    /// <summary>The owned server's validated plan.</summary>
    public required ServerRunPlan ServerPlan { get; init; }
    /// <summary>The prepared clean client plan, or null for <c>--server-only</c>.</summary>
    public ClientRunPlan? ClientPlan { get; init; }
    /// <summary>The fixture world UID checked by the runner before invoking the scenario.</summary>
    public required string WorldUid { get; init; }
}

/// <summary>
/// A mod-owned assertion for <c>valheim-test start</c>, where the owned client hosts the fixture world.
/// The toolkit prepares the client, character, world, pins, report, and cleanup. The scenario receives
/// the joined client and its hosting actor for one round; it should record each assertion with
/// <see cref="ClientRound.Step(string, Action)"/>.
/// </summary>
public interface IOneShotHostedScenario
{
    /// <summary>A short name recorded with the private run evidence.</summary>
    string Name { get; }

    /// <summary>Exercise and assert within the prepared hosted world.</summary>
    void Run(ClientRound round);
}
