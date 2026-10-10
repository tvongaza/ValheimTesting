using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

namespace ExampleOneShotServerScenario;

/// <summary>Small client-hosted example; replace the observation with your mod's own command or state check.</summary>
public sealed class HostedWorldScenario : IOneShotHostedScenario
{
    public string Name => "example-hosted-world-identity";

    public void Run(ClientRound round) => round.Step("hosting client remains in the prepared world", () =>
    {
        var state = new SessionControl(round.Client).Read();
        if (!state.WorldReady || state.WorldUid != round.WorldUid)
            throw new InvalidDataException("The hosting client stopped reporting the prepared fixture world.");
    });
}
