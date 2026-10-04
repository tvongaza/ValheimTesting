using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// The client half of a persistence scenario, in rounds: the same entry into the world and measurement before and after a
/// restart, with the evidence written in the same shape each time. One loop serves both kinds of client:
/// <list type="bullet">
/// <item>a client that joins an owned dedicated server (<see cref="Run(GameActor, Func{ClientSession}, Action{ClientRound}, Action{ClientRound})"/>,
/// with <see cref="OwnedServer"/> and <see cref="WorldUid"/>);</item>
/// <item>a client that hosts a fixture world itself, as a listen server or locally (<see cref="Run(Func{ClientSession}, Action{ClientRound})"/>,
/// with the plan's <see cref="ClientRunPlan.HostWorld"/> section): it is both the server and the client of each round.</item>
/// </list>
/// <see cref="Run(GameActor, Func{ClientSession}, Action{ClientRound}, Action{ClientRound})"/> opens the client, then for each of <see cref="Rounds"/>:
/// <list type="number">
/// <item>waits until the server accepts game connections (<see cref="IOwnedServer.WaitUntilJoinable"/>);</item>
/// <item>joins with the plan's disposable character (devcommands first, exactly once), verifies the client's world pins and
/// waits for the world; a <see cref="ClientRunPlan.Crossplay"/> client first reads the server's lobby (<see cref="Lobby"/>)
/// and joins it (<see cref="SessionControl.JoinCrossplay"/>);</item>
/// <item>joins, which protects the player once the world is ready (<see cref="SessionControl.WaitForWorld"/>), and, with an <see cref="Arrival"/>, has it arrive there with <see cref="PlayerPlacement.Arrive"/> (<c>{round}-arrival.json</c> and <c>{round}-teleport-trace.json</c>);</item>
/// <item>runs the mod's measurement, which adds its own steps and evidence through <see cref="ClientRound"/>;</item>
/// <item>between rounds: a confirmed world save, the client leaves to its menu, only the owned server restarts
/// (<see cref="IOwnedServer.Restart"/>), and the optional after-restart check runs; after the last round the client leaves.</item>
/// </list>
/// A hosting client's rounds first run the plan's static preflight (<see cref="ClientRunPlan.Preflight(IEnumerable{string})"/>:
/// the fixture's pinned files and own world UID, and an owned client's install, whose ValheimCLI set must also provide
/// <see cref="CliCapabilities.HostedRounds"/> when the plan names a <see cref="ClientRunPlan.CliManifest"/>), check the native
/// client's save directory, and place the fixture world (<see cref="HostedWorld"/>), so a wrong fixture, install or
/// ValheimCLI set stops the run before the fixture is copied or the game started. After opening the client they require the
/// session commands the rounds use (<see cref="CliCapabilities.HostedRounds"/>); each round then hosts the world
/// (<see cref="HostWorlds.Start"/>; from the second round on, this is the hosted world's restart), measures, saves with
/// confirmation between rounds (<see cref="SessionControl.Save"/>) and leaves to the menu, which saves again.
/// <para>
/// Every step is recorded in <see cref="Report"/>, those of a round prefixed with its name (<c>first: join ...</c>). The
/// first failure stops the rounds and is rethrown: nothing after a failed save, restart or measurement runs. The teardown is
/// the same for both kinds: the client is closed in every outcome (an owned client's process is stopped, an attached client
/// is detached and left running), then a hosted world is moved into the evidence (<c>hostWorldEvidence</c>) once no client
/// can still host it; after a failure with an attached client, or an owned client that did not stop, the world is left in
/// place and named in <c>hostWorldLeftInPlace</c>. A failed teardown fails a passing run and never hides an earlier failure.
/// </para>
/// </summary>
public sealed class ClientRounds
{
    private static readonly Regex RoundName = new(@"^[A-Za-z0-9][A-Za-z0-9_-]*\z", RegexOptions.CultureInvariant);

    /// <summary>The client plan: join address or <see cref="ClientRunPlan.HostWorld"/> section, character, password variable, pins and timeouts.</summary>
    public required ClientRunPlan Client { get; init; }
    /// <summary>
    /// The owned server's world UID, which the joined client must report; required for a joining client. A hosting client's
    /// world UID is its plan's <see cref="HostWorldPlan.WorldUid"/>: leave this out or give the same.
    /// </summary>
    public string? WorldUid { get; init; }
    public required ScenarioReport Report { get; init; }
    /// <summary>The run's output directory, where each round's evidence is written.</summary>
    public required string Output { get; init; }
    /// <summary>
    /// For a joining client, required: the owned server the rounds wait on before each join and restart between rounds, for
    /// example the runner's <see cref="OwnedServerSession"/>. A hosting client is its own server: leave this out.
    /// </summary>
    public IOwnedServer? OwnedServer { get; init; }
    /// <summary>Where the player stands to measure; null leaves the player where it joined. A joining client only.</summary>
    public HeightExpectation? Arrival { get; init; }
    /// <summary>
    /// Whether to enable god, ghost and debug protection once in the world. The default protects a character before terrain
    /// or movement checks. Set false only for a short load/join smoke or a scenario that needs ordinary gameplay and
    /// intentionally avoids those modes; server admin privileges may be unavailable to a clean client.
    /// </summary>
    public bool ProtectPlayer { get; init; } = true;
    /// <summary>The arrival step's name within a round, saying where the player goes. A joining client only.</summary>
    public string ArriveStep { get; init; } = DefaultArriveStep;
    private const string DefaultArriveStep = "arrive at the measurement point";
    /// <summary>The client's opening step's name; the default says whether it is launched or attached, with plugins pinned.</summary>
    public string? OpenStep { get; init; }
    /// <summary>The rounds' names, which prefix their steps and evidence files: letters, digits, <c>-</c> and <c>_</c>, all different.</summary>
    public IReadOnlyList<string> Rounds { get; init; } = ["first", "after-restart"];
    /// <summary>
    /// For a crossplay client (<see cref="ClientRunPlan.Crossplay"/>), required: the server's lobby, read before each
    /// round's join because a restarted server opens a new one, for example
    /// <c>server =&gt; CrossplayServer.WaitForLobby(server, CrossplayServer.BepInExLog(runtime), timeout)</c>.
    /// </summary>
    public Func<GameActor, CrossplayLobby>? Lobby { get; init; }
    public CancellationToken Cancellation { get; init; }

    /// <summary>
    /// Runs a joining client's rounds against <paramref name="server"/>, the owned server's current actor, opening the client
    /// with <paramref name="openClient"/> (for example <see cref="ClientSession.Open"/>). <paramref name="measure"/> runs in
    /// each round once the player is in place; <paramref name="afterRestart"/>, if given, runs after each restart before the
    /// next round's join, for server-side checks of what survived. Returns the server's actor after the last restart.
    /// </summary>
    public GameActor Run(GameActor server, Func<ClientSession> openClient, Action<ClientRound> measure, Action<ClientRound>? afterRestart = null)
    {
        CheckJoined();
        return RunRounds(new JoinedWorld(this, server), openClient, measure, afterRestart);
    }

    /// <summary>
    /// Runs a hosting client's rounds (the plan's <see cref="ClientRunPlan.HostWorld"/> section), opening the client with
    /// <paramref name="openClient"/> (for example <see cref="ClientSession.Open"/>). In <paramref name="measure"/>,
    /// <see cref="ClientRound.Server"/> and <see cref="ClientRound.Client"/> are the same host.
    /// </summary>
    public void Run(Func<ClientSession> openClient, Action<ClientRound> measure)
    {
        var plan = CheckHosted();
        RunRounds(new HostedWorldRounds(this, plan), openClient, measure, afterRestart: null);
    }

    // The one loop: open, then per round enter the world, measure, save between rounds, leave, restart; then the one teardown.
    private GameActor RunRounds(IRoundsWorld world, Func<ClientSession> openClient, Action<ClientRound> measure, Action<ClientRound>? afterRestart)
    {
        world.Record();
        // What an owned client is launched as (never another slice); an attached client's is its operator's.
        Report.Provenance["clientArchitecture"] = Client.Owned ? ClientLaunch.PlanName(Client.LaunchArchitecture) : "attached";
        Report.Provenance["cliPreflight"] = Client.CliPreflight;
        var completed = new List<string>();
        ClientSession? session = null;
        bool passed = false;
        try
        {
            world.Prepare();
            Report.Step(OpenStep ?? (Client.Owned ? "launch the owned client to its menu, plugins pinned" : "attach to the operator's client at its menu, plugins pinned"),
                () =>
                {
                    session = openClient();
                    // Arrival's in-game waits, checked once at the menu so a missing pack fails before any join.
                    if (Arrival != null) CliCapabilities.Require(session.Actor, PlayerPlacement.ArrivalCapabilities);
                });
            var client = session!.Actor;
            world.Opened(client);
            for (int i = 0; i < Rounds.Count; i++)
            {
                var round = new ClientRound(Rounds[i], i, i == Rounds.Count - 1, world.ServerFor(client), client, Report, Output);
                if (i > 0) afterRestart?.Invoke(round);
                world.Enter(round);
                measure(round);
                if (!round.Last) Report.Step(Between("confirmed world save", i), () => world.Save(round));
                round.Step(world.LeaveStep, () =>
                {
                    new SessionControl(client).Leave();
                    client.VerifyEnvironment(Client.MenuExpectations); // A transition always needs fresh pins.
                });
                completed.Add(round.Name);
                Report.Provenance[world.CompletedKey] = string.Join(",", completed);
                if (!round.Last) world.Restart(i);
            }
            passed = true;
            return world.ServerFor(client);
        }
        finally { Close(session, world, passed); }
    }

    // The one teardown: close the client, then release what the world strategy placed. A failure already on its way out is
    // the one to rethrow; a failed teardown fails a passing run. Each failed part is recorded as its own failed step.
    private void Close(ClientSession? session, IRoundsWorld world, bool passed)
    {
        // A placed world may be moved once no client can still host it: none was opened, the owned one stopped, or every
        // round ended with the client at its menu.
        bool released = session == null || passed;
        Exception? teardown = null;
        if (session != null)
            try { Report.Step(session.Owned ? "stop only the owned client" : "detach from the operator's client", session.Dispose); released |= session.Owned; }
            catch (Exception error) { teardown = error; }
            finally { if (session.Stopped is { } stopped) Report.Provenance["clientStop"] = stopped.ToString(); }
        try { world.Release(released); }
        catch (Exception error) { teardown ??= error; }
        if (passed && teardown != null) ExceptionDispatchInfo.Capture(teardown).Throw();
    }

    // What differs between a joining and a hosting client: how the client enters the world, saves, restarts, and what is
    // placed before and released after.
    private interface IRoundsWorld
    {
        string LeaveStep { get; }
        string CompletedKey { get; }
        void Record();
        void Prepare();
        void Opened(GameActor client);
        GameActor ServerFor(GameActor client);
        void Enter(ClientRound round);
        void Save(ClientRound round);
        void Restart(int round);
        void Release(bool released);
    }

    // Joins an owned dedicated server by address or crossplay lobby; the server restarts between rounds.
    private sealed class JoinedWorld(ClientRounds rounds, GameActor server) : IRoundsWorld
    {
        private GameActor _server = server;
        public string LeaveStep => "the client leaves to its menu";
        public string CompletedKey => "clientRoundsCompleted";
        public void Record()
        {
            rounds.Report.Provenance["clientRounds"] = string.Join(",", rounds.Rounds);
            rounds.Report.Provenance["clientJoin"] = rounds.Client.Crossplay ? "crossplay" : "address";
        }
        public void Prepare()
        {
            // An owned client's staged ValheimCLI set, read from its manifest before the launch, when the plan names one.
            if (rounds.Arrival != null && rounds.Client.Owned && rounds.Client.CliManifest != null)
                rounds.Report.Step("the owned client's ValheimCLI manifest offers the arrival waits, before launch",
                    () => rounds.Client.CheckCliManifest(PlayerPlacement.ArrivalCapabilities));
        }
        public void Opened(GameActor client) { }
        public GameActor ServerFor(GameActor client) => _server;
        public void Enter(ClientRound round) => rounds.Join(round);
        public void Save(ClientRound round) => round.Server.SaveConfirmed();
        public void Restart(int round) => rounds.Report.Step(rounds.Between("restart only the owned server", round), () => _server = rounds.OwnedServer!.Restart());
        public void Release(bool released) { }
    }

    // Hosts the plan's fixture world on the client itself; the next round's start is the hosted world's restart.
    private sealed class HostedWorldRounds(ClientRounds rounds, HostWorldPlan plan) : IRoundsWorld
    {
        private HostedWorld? _world;
        private ClientRunPlan Client => rounds.Client;
        private ScenarioReport Report => rounds.Report;
        public string LeaveStep => "the host leaves to its menu";
        public string CompletedKey => "hostRoundsCompleted";
        public void Record()
        {
            Report.Provenance["role"] = "host";
            Report.Provenance["hostMode"] = plan.Local ? "local" : "listen";
            Report.Provenance["hostCrossplay"] = plan.Crossplay ? "true" : "false";
            Report.Provenance["hostRounds"] = string.Join(",", rounds.Rounds);
        }
        public void Prepare()
        {
            Report.Step(Client.Owned ? "preflight the fixture world and the owned client's install, before anything is copied or started" : "preflight the fixture world, before it is copied",
                () => Client.Preflight(CliCapabilities.HostedRounds));
            string? saveDirectory = null;
            Report.Step("preflight the native client's hosted-world save directory", () =>
            {
                var platform = Client.Owned ? ClientLaunch.Detect(Client.Install) : HostedWorld.CurrentPlatform;
                string defaultSaveDirectory = HostedWorld.DefaultSaveDirectory(platform);
                HostedWorld.RequireNativeSaveDirectory(platform, plan.SaveDirectory, Client.LaunchArguments, defaultSaveDirectory);
                saveDirectory = plan.SaveDirectory ?? defaultSaveDirectory;
            });
            Report.Step("place the disposable fixture world in the client's local worlds", () => _world = HostedWorld.Place(plan, saveDirectory!, rounds.Output, Client.Pinned));
            Report.Provenance["hostWorld"] = _world!.Name;
        }
        public void Opened(GameActor client) =>
            Report.Step("the client's ValheimCLI offers the session commands the rounds use", () => CliCapabilities.Require(client, CliCapabilities.HostedRounds));
        public GameActor ServerFor(GameActor client) => client; // The host is both.
        public void Enter(ClientRound round)
        {
            string protect = rounds.ProtectPlayer ? ", protected" : "";
            round.Step(round.Index == 0 ? "host the fixture world with the disposable character" + protect : "restart the hosted world" + protect,
                () => HostWorlds.Start(round.Client, Client, _world!.Name, TimeSpan.FromSeconds(Client.JoinSeconds), rounds.Cancellation, rounds.ProtectPlayer));
            // The owned host's disposable character and fixture acknowledge cheats; an operator's client keeps devcommands only.
            if (Client.Owned)
                round.Step("establish test access on the owned host", () => TestAccess.Ensure(round.Client, TestActorRole.ClientInWorld));
        }
        public void Save(ClientRound round) => new SessionControl(round.Client).Save(plan.WorldUid, TimeSpan.FromSeconds(plan.SaveSeconds));
        public void Restart(int round) { } // The next round's start restarts the hosted world.
        public void Release(bool released)
        {
            if (_world == null) return;
            if (!released) Report.Provenance["hostWorldLeftInPlace"] = _world.WorldsDirectory + " (" + _world.Name + ")";
            else
            {
                Report.Step("move the hosted world from the client's local worlds into the evidence", _world.Collect);
                Report.Provenance["hostWorldEvidence"] = _world.CollectedTo!;
            }
        }
    }

    private void Join(ClientRound round)
    {
        var session = new SessionControl(round.Client);
        string worldUid = WorldUid!; // CheckJoined requires it.
        round.Step("the server accepts game connections", () => OwnedServer!.WaitUntilJoinable(round.Server));
        if (Client.Crossplay)
        {
            CrossplayLobby? lobby = null;
            round.Step("the server's crossplay lobby is open", () => lobby = Lobby!(round.Server));
            round.Step("join the owned server's crossplay lobby with the disposable character" + (ProtectPlayer ? ", protected" : ""), () =>
            {
                // Devcommands first; the join command exactly once, then the connection is awaited on the session state.
                session.JoinCrossplay(lobby!.RemotePlayerId, Client.Character, worldUid, Client.MenuExpectations, TimeSpan.FromSeconds(Client.JoinSeconds), cancellation: Cancellation,
                    worldExpectations: Client.WorldExpectations(worldUid));
                round.Client.VerifyEnvironment(Client.WorldExpectations(worldUid));
                session.WaitForWorld(worldUid, TimeSpan.FromSeconds(Client.JoinSeconds), Cancellation, ProtectPlayer);
            });
        }
        else round.Step("join the owned server with the disposable character" + (ProtectPlayer ? ", protected" : ""), () =>
        {
            session.Join(Client.Join, Client.Character, Client.PasswordVariable); // Join exactly once.
            round.Client.VerifyEnvironment(Client.WorldExpectations(worldUid));
            // When requested, protects the player once the world is ready (god, ghost, debug mode, read back); fly stays off.
            session.WaitForWorld(worldUid, TimeSpan.FromSeconds(Client.JoinSeconds), Cancellation, ProtectPlayer);
        });
        // An owned client's disposable character acknowledges cheats once it is in the world; an operator's client keeps
        // devcommands only (set at its menu by the join).
        if (Client.Owned)
            round.Step("establish test access on the owned client", () => TestAccess.Ensure(round.Client, TestActorRole.ClientInWorld));
        if (Arrival is { } point)
        {
            round.Step(ArriveStep, () =>
            {
                var arrival = PlayerPlacement.Arrive(round.Server, round.Client, point, TimeSpan.FromSeconds(Client.ArrivalSeconds), Cancellation);
                round.Write("arrival", arrival.Support);
                round.Write("teleport-trace", arrival.Timing);
            });
        }
    }

    // Two rounds have one save and one restart between them; with more, each names the round it leads to.
    private string Between(string step, int round) => Between(step, round, Rounds);
    internal static string Between(string step, int round, IReadOnlyList<string> rounds) => rounds.Count == 2 ? step : $"{step} before {rounds[round + 1]}";

    private void CheckJoined()
    {
        CheckRoundNames(Rounds);
        if (string.IsNullOrWhiteSpace(ArriveStep)) throw new ArgumentException("ArriveStep: name the arrival step.");
        if (Client.HostWorld != null) throw new ArgumentException("Client: this client hosts its own world (hostWorld); run it with Run(openClient, measure), without a server.");
        if (OwnedServer == null) throw new ArgumentException("OwnedServer: a joining client's rounds wait on and restart the owned server; supply it, for example the runner's OwnedServerSession.");
        if (string.IsNullOrWhiteSpace(WorldUid)) throw new ArgumentException("WorldUid: name the owned server's world UID, which the joined client must report.");
        if (Client.Crossplay && Lobby == null) throw new ArgumentException("Lobby: a crossplay client joins the server's PlayFab lobby; supply Lobby, for example with CrossplayServer.WaitForLobby.");
    }

    private HostWorldPlan CheckHosted()
    {
        CheckRoundNames(Rounds);
        var plan = Client.HostWorld ?? throw new ArgumentException("Client: the plan has no hostWorld section; a client that joins a server runs with Run(server, openClient, measure).");
        // A hosting client is its own server and has no arrival step: what only a joining client uses is refused, not ignored.
        if (OwnedServer != null) throw new ArgumentException("OwnedServer: a hosting client is its own server; leave OwnedServer out.");
        if (Lobby != null) throw new ArgumentException("Lobby: a hosting client joins no lobby; leave Lobby out.");
        if (Arrival != null) throw new ArgumentException("Arrival: a hosting client's rounds have no arrival step; place the player in the measurement.");
        if (ArriveStep != DefaultArriveStep) throw new ArgumentException("ArriveStep: a hosting client's rounds have no arrival step to name; leave ArriveStep out.");
        if (WorldUid != null && WorldUid != plan.WorldUid)
            throw new ArgumentException($"WorldUid: {WorldUid} is not the hosted fixture's world UID {plan.WorldUid}; leave WorldUid out for a hosting client.");
        return plan;
    }

    internal static void CheckRoundNames(IReadOnlyList<string> rounds)
    {
        if (rounds.Count == 0) throw new ArgumentException("Rounds: name at least one round.");
        foreach (string? name in rounds)
            if (name == null || !RoundName.IsMatch(name))
                throw new ArgumentException($"Rounds: \"{name}\" is not a round name; use letters, digits, - and _ (it names the round's steps and files).");
        if (rounds.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } repeated)
            throw new ArgumentException($"Rounds: \"{repeated.Key}\" is named twice; give every round its own name, so no evidence is overwritten.");
    }
}

/// <summary>One round of <see cref="ClientRounds"/>, handed to the mod's measurement and after-restart check.</summary>
public sealed class ClientRound
{
    internal ClientRound(string name, int index, bool last, GameActor server, GameActor client, ScenarioReport report, string output)
    { Name = name; Index = index; Last = last; Server = server; Client = client; Report = report; Output = output; }

    /// <summary>The round's name, which prefixes its steps and evidence files.</summary>
    public string Name { get; }
    /// <summary>The round's position, from 0.</summary>
    public int Index { get; }
    /// <summary>Whether this is the last round: the client leaves after it and the server is not restarted.</summary>
    public bool Last { get; }
    /// <summary>The server's actor in this round (a new one after each restart).</summary>
    public GameActor Server { get; }
    /// <summary>The joined client's actor, strictly pinned to the server's world.</summary>
    public GameActor Client { get; }
    public ScenarioReport Report { get; }
    public string Output { get; }

    /// <summary>Records a step named <c>{round}: {name}</c> and rethrows its failure.</summary>
    public void Step(string name, Action action) => Report.Step(Name + ": " + name, action);

    /// <summary>
    /// Writes <paramref name="value"/> as indented JSON to <c>{round}-{name}.json</c> in the output directory and returns
    /// the path. Refuses a file that already exists: evidence is never overwritten.
    /// </summary>
    public string Write(string name, object value)
    {
        string path = Path.Combine(Output, $"{Name}-{name}.json");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(file, value, value.GetType(), new JsonSerializerOptions { WriteIndented = true });
        return path;
    }
}
