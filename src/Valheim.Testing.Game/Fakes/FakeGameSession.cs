namespace Valheim.Testing.Game.Fakes;

/// <summary>
/// A <see cref="GameSession"/> over a scripted world, for testing a mod's scenarios <c>(GameSession, TPlan) =&gt; Task</c>
/// without a game: the owned server is <paramref name="server"/> (its restarts and joinable wait), its first boot's actor
/// <c>firstBoot()</c>, and <see cref="GameSession.OpenClient"/> opens clients with <c>openClient(plan, name, output)</c>, for
/// example <see cref="ClientSession.Attach(ClientRunPlan, string, IGameTransport)"/> over a <see cref="ScriptedTransport"/>.
/// Start it (<see cref="GameSession.StartAsync"/>) before the scenario and dispose it after, as the runner does.
/// </summary>
public static class FakeGameSession
{
    /// <param name="worldUid">The world UID joins verify.</param>
    /// <param name="serverLog">The server's live log a scenario reads (<see cref="ServerActor.LiveLog"/>), or null.</param>
    /// <param name="clientLog">An owned client's live log (<see cref="GameSession.ClientLog"/>), or null for none.</param>
    /// <param name="lobby">The crossplay server's lobby (<see cref="ServerActor.Lobby"/>).</param>
    /// <param name="interval">How often the scenario's observation waits re-read (<see cref="GameSession.Interval"/>); a few milliseconds.</param>
    /// <param name="campaignClients">The campaign's client names (<see cref="GameSession.CampaignClients"/>), for a scenario that opens named clients.</param>
    public static GameSession Create(ScenarioReport report, string output, string worldUid, IOwnedServer server, Func<GameActor> firstBoot,
        Func<ClientRunPlan, string, string, ClientSession> openClient, string? serverLog = null, Func<ClientRunPlan, string?>? clientLog = null,
        Func<GameActor, CrossplayLobby>? lobby = null, TimeSpan? interval = null, IReadOnlyList<string>? campaignClients = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(openClient);
        var placement = new Placement(openClient);
        return new GameSession(report, output, worldUid, _ => new ServerActor(server, firstBoot, serverLog, lobby), [], cancellation)
        {
            ResolveClient = (_, name) => (name ?? "client", placement),
            LiveClientLog = clientLog ?? (_ => null),
            Interval = interval ?? TimeSpan.FromMilliseconds(10),
            CampaignClients = campaignClients ?? [],
        };
    }

    private sealed class Placement(Func<ClientRunPlan, string, string, ClientSession> open) : IClientPlacement
    {
        public ClientSession Open(string name, ClientRunPlan plan, string output, CancellationToken cancellation) => open(plan, name, output);
    }
}
