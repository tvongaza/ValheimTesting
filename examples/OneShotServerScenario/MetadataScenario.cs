using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

namespace ExampleOneShotServerScenario;

/// <summary>Small server-only example; replace the observation with your mod's own command or state check.</summary>
public sealed class MetadataScenario : IOneShotServerScenario
{
    public string Name => "example-world-identity";
    public bool RequiresClient => false;

    public Task RunAsync(GameSession session, OneShotServerContext context)
    {
        session.Report.Step("server still reports the prepared world's UID", () =>
        {
            var state = new SessionControl(session.Server!.Game).Read();
            if (!state.WorldReady || state.WorldUid != context.WorldUid)
                throw new InvalidDataException("The server stopped reporting the prepared fixture world.");
        });
        return Task.CompletedTask;
    }
}
