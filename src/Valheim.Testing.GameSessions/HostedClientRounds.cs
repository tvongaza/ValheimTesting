using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// A hosting client's <see cref="ClientRounds"/> on this machine, driven by the caller (#829). The fixture world is placed through
/// the hosting layer's one journalled site, as <c>start</c>'s, a session's and a campaign's are: journalled in this machine's run
/// journal before anything reaches the client's <c>worlds_local</c>, and moved out after into <c>&lt;Output&gt;/&lt;runId&gt;/host-world</c>
/// (fetched into <c>host-world</c> beside it), so after a killed run <c>valheim-test env status</c> lists the world and
/// <c>valheim-test env recover --run</c> moves it out.
/// </summary>
public static class HostedClientRounds
{
    /// <summary>
    /// Runs a hosting client's rounds (the plan's <see cref="ClientRunPlan.HostWorld"/> section), opening the client with
    /// <paramref name="openClient"/> (for example <see cref="ClientSession.Open"/>). In <paramref name="measure"/>,
    /// <see cref="ClientRound.Server"/> and <see cref="ClientRound.Client"/> are the same host. The placement's journal lines go to
    /// the run this flow journals (its own run's, or one run for the whole process), under the actor <c>client</c>.
    /// </summary>
    public static void Run(this ClientRounds rounds, Func<ClientSession> openClient, Action<ClientRound> measure)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        rounds.RunHosted(HostedWorldOnHost.ThisMachine(rounds.Output, "client"), openClient, measure);
    }
}
