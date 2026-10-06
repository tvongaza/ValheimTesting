using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
namespace Valheim.Testing.GameSessions.Fakes;

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

    /// <summary>
    /// A <see cref="GameSession"/> whose world a scripted hosting client hosts (<see cref="GameSession.Host"/>, named <c>host</c>),
    /// with no dedicated server: <paramref name="openClient"/> opens the host from <paramref name="host"/> (a plan with its
    /// <c>hostWorld</c> section; its fixture is placed in the client data directory a <see cref="FakeClientDataDirectory"/> scope
    /// gives), each of <paramref name="peers"/> (named clients that start with the session and join the host,
    /// <see cref="GameSession.Join"/>; their plans set <see cref="ClientRunPlan.JoinsHost"/>) and every client the scenario opens
    /// (<see cref="GameSession.OpenClient"/>), each as <c>(plan, name, output)</c>. With peers, each actor's <c>output</c> is its own
    /// folder <c>&lt;output&gt;/&lt;name&gt;</c> (the host's is <c>host</c>), as in a campaign; a host alone gets the output itself.
    /// <paramref name="mod"/>, when given, is checked on the host as the runner does. Start it before the scenario and dispose it
    /// after, as the runner does.
    /// </summary>
    /// <param name="hostLog">The host's live log a scenario reads (<see cref="HostingClientActor.LiveLog"/>), or null.</param>
    /// <param name="interval">How often the scenario's observation waits re-read (<see cref="GameSession.Interval"/>); a few milliseconds.</param>
    public static GameSession Hosted(ScenarioReport report, string output, ClientRunPlan host, Func<ClientRunPlan, string, string, ClientSession> openClient,
        IReadOnlyDictionary<string, ClientRunPlan>? peers = null, string? hostLog = null, ModDeclaration? mod = null, TimeSpan? interval = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(host); ArgumentNullException.ThrowIfNull(openClient);
        var placement = new Placement(openClient);
        // With peers, every actor writes into its own folder (GameSession.ActorOutput), as a hosted campaign's do; a host alone
        // writes into the output, as the runner's host mode does. The session refuses any other layout when it starts.
        bool campaign = peers is { Count: > 0 };
        string Evidence(string actor) => campaign ? GameSession.ActorOutput(output, actor) : output;
        var declared = (peers ?? new Dictionary<string, ClientRunPlan>()).Select(peer =>
            (peer.Key, (Func<CancellationToken, ClientActor>)(token => new ClientActor(peer.Key, peer.Value, Evidence(peer.Key), placement, token))));
        return new GameSession(report, output, null, null, declared, cancellation,
            token => new HostingClientActor("host", host, Evidence("host"), placement, token) { LiveLogSource = () => hostLog })
        {
            ResolveClient = (_, name) => (name ?? "client", placement),
            LiveClientLog = _ => null,
            Interval = interval ?? TimeSpan.FromMilliseconds(10),
            Mod = mod, ServerPinsMod = mod != null && (!host.Pinned || mod.PinnedIn(host.Pins)),
        };
    }

    private sealed class Placement(Func<ClientRunPlan, string, string, ClientSession> open) : IClientPlacement
    {
        public ClientSession Open(string name, ClientRunPlan plan, string output, CancellationToken cancellation) => open(plan, name, output);
    }
}
