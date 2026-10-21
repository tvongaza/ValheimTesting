using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>The same marker assertion as the A/B runner, on one tool-owned hosted fixture.</summary>
public sealed class MarkerOneShot : IOneShotHostedScenario
{
    public string Name => "exactly-one-marker-at-host";
    public void Run(ClientRound round) => Scenario.Measure(round);
}
