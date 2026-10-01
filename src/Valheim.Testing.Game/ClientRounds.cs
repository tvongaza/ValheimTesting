using System.Text.Json;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// The client half of a persistence scenario on an owned server, in rounds: the same join and measurement before and
/// after a restart, with the evidence written in the same shape each time. <see cref="Run"/> opens the client, then for
/// each of <see cref="Rounds"/>:
/// <list type="number">
/// <item>waits until the server accepts game connections (only now, so the steps before overlap a first boot's late socket);</item>
/// <item>joins with the plan's disposable character (devcommands first, exactly once), verifies the client's world pins and
/// waits for the world; a <see cref="ClientRunPlan.Crossplay"/> client first reads the server's lobby (<see cref="Lobby"/>)
/// and joins it (<see cref="SessionControl.JoinCrossplay"/>);</item>
/// <item>joins, which protects the player once the world is ready (<see cref="SessionControl.WaitForWorld"/>), and, with an <see cref="Arrival"/>, has it arrive there (<c>{round}-arrival.json</c>);</item>
/// <item>runs the mod's measurement, which adds its own steps and evidence through <see cref="ClientRound"/>;</item>
/// <item>between rounds: a confirmed world save, the client leaves to its menu, only the owned server restarts, and the
/// optional after-restart check runs; after the last round the client leaves.</item>
/// </list>
/// Every step is recorded in <see cref="Report"/>, those of a round prefixed with its name (<c>first: join ...</c>). The
/// first failure stops the rounds and is rethrown: nothing after a failed save, restart or measurement runs. The client
/// is closed in every outcome: an owned client's process is stopped, an attached client is detached and left running.
/// </summary>
public sealed class ClientRounds
{
    private static readonly Regex RoundName = new(@"^[A-Za-z0-9][A-Za-z0-9_-]*\z", RegexOptions.CultureInvariant);

    /// <summary>The client plan: join address, character, password variable, pins and timeouts.</summary>
    public required ClientRunPlan Client { get; init; }
    /// <summary>The owned server's world UID, which the joined client must report.</summary>
    public required string WorldUid { get; init; }
    public required ScenarioReport Report { get; init; }
    /// <summary>The run's output directory, where each round's evidence is written.</summary>
    public required string Output { get; init; }
    /// <summary>Waits until the given server accepts game connections, for example <see cref="OwnedServerSession.WaitUntilJoinable"/>.</summary>
    public required Action<GameActor> WaitUntilJoinable { get; init; }
    /// <summary>Restarts only the owned server and returns its new actor, for example <see cref="OwnedServerSession.Restart"/>.</summary>
    public required Func<GameActor> RestartServer { get; init; }
    /// <summary>Where the player stands to measure; null leaves the player where it joined.</summary>
    public HeightExpectation? Arrival { get; init; }
    /// <summary>The arrival step's name within a round, saying where the player goes.</summary>
    public string ArriveStep { get; init; } = "arrive at the measurement point";
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
    /// <summary>How long the player must stand still before the arrival teleport (<see cref="PlayerPlacement.Arrive"/>); tests pass zero.</summary>
    public TimeSpan? SettleFor { get; init; }
    public CancellationToken Cancellation { get; init; }

    /// <summary>
    /// Runs the rounds against <paramref name="server"/>, opening the client with <paramref name="openClient"/> (for
    /// example <see cref="ClientSession.Open"/>). <paramref name="measure"/> runs in each round once the player is in
    /// place; <paramref name="afterRestart"/>, if given, runs after each restart before the next round's join, for
    /// server-side checks of what survived. Returns the server's actor after the last restart.
    /// </summary>
    public GameActor Run(GameActor server, Func<ClientSession> openClient, Action<ClientRound> measure, Action<ClientRound>? afterRestart = null)
    {
        Check();
        Report.Provenance["clientRounds"] = string.Join(",", Rounds);
        Report.Provenance["clientJoin"] = Client.Crossplay ? "crossplay" : "address";
        Report.Provenance["clientStart"] = Client.StartAtCharacterSave ? "characterSave" : "teleport";
        // What an owned client is launched as (never another slice); an attached client's is its operator's.
        Report.Provenance["clientArchitecture"] = Client.Owned ? ClientLaunch.PlanName(Client.LaunchArchitecture) : "attached";
        if (Client.StartAtCharacterSave) Report.Provenance["clientStartSha256"] = Client.CharacterStart!.Sha256;
        var completed = new List<string>();
        ClientSession? session = null;
        CharacterStartStage? stage = null;
        bool passed = false;
        try
        {
            if (Client.StartAtCharacterSave)
                Report.Step("stage the pinned disposable local character", () => stage = CharacterStartStage.Install(
                    Client.CharacterStart!, Client.Character, long.Parse(WorldUid, System.Globalization.CultureInfo.InvariantCulture), Arrival!));
            Report.Step(OpenStep ?? (Client.Owned ? "launch the owned client to its menu, plugins pinned" : "attach to the operator's client at its menu, plugins pinned"),
                () => session = openClient());
            for (int i = 0; i < Rounds.Count; i++)
            {
                var round = new ClientRound(Rounds[i], i, i == Rounds.Count - 1, server, session!.Actor, Report, Output);
                if (i > 0) afterRestart?.Invoke(round);
                Join(round);
                measure(round);
                if (!round.Last) Report.Step(Between("confirmed world save", i), () => server.SaveConfirmed());
                round.Step("the client leaves to its menu", () =>
                {
                    new SessionControl(round.Client).Leave();
                    round.Client.VerifyEnvironment(Client.MenuExpectations); // A transition always needs fresh pins.
                });
                completed.Add(round.Name);
                Report.Provenance["clientRoundsCompleted"] = string.Join(",", completed);
                if (!round.Last) Report.Step(Between("restart only the owned server", i), () => server = RestartServer());
            }
            passed = true;
            return server;
        }
        finally
        {
            try
            {
                if (session != null)
                    try { Report.Step(session.Owned ? "stop only the owned client" : "detach from the operator's client", session.Dispose); }
                    catch when (!passed) { } // Keep the original failure.
                    finally { if (session.Stopped is { } stopped) Report.Provenance["clientStop"] = stopped.ToString(); }
            }
            finally
            {
                if (stage != null)
                    try { Report.Step("remove only the staged character and its game-made backups", stage.Dispose); }
                    catch when (!passed) { } // Cleanup's failure is recorded without masking the original one.
            }
        }
    }

    private void Join(ClientRound round)
    {
        var session = new SessionControl(round.Client);
        round.Step("the server accepts game connections", () => WaitUntilJoinable(round.Server));
        bool preparedStart = Client.StartAtCharacterSave && round.Index == 0;
        if (Client.Crossplay)
        {
            CrossplayLobby? lobby = null;
            round.Step("the server's crossplay lobby is open", () => lobby = Lobby!(round.Server));
            round.Step("join the owned server's crossplay lobby with the disposable character, protected", () =>
            {
                // Devcommands first; the join command exactly once, then the connection is awaited on the session state.
                session.JoinCrossplay(lobby!.RemotePlayerId, Client.Character, WorldUid, Client.MenuExpectations, TimeSpan.FromSeconds(Client.JoinSeconds), cancellation: Cancellation,
                    worldExpectations: Client.WorldExpectations(WorldUid), requiredLocalFilename: preparedStart ? Client.Character : null);
                round.Client.VerifyEnvironment(Client.WorldExpectations(WorldUid));
                session.WaitForWorld(WorldUid, TimeSpan.FromSeconds(Client.JoinSeconds), Cancellation);
            });
        }
        else round.Step("join the owned server with the disposable character, protected", () =>
        {
            if (preparedStart)
            {
                session.EnableDevcommands();
                session.RequireLocalCharacter(Client.Character);
            }
            session.Join(Client.Join, Client.Character, Client.PasswordVariable, enableDevcommands: !preparedStart); // Join exactly once.
            round.Client.VerifyEnvironment(Client.WorldExpectations(WorldUid));
            // Protects the player once the world is ready (god, ghost, debug mode, read back); fly stays off.
            session.WaitForWorld(WorldUid, TimeSpan.FromSeconds(Client.JoinSeconds), Cancellation);
        });
        if (Arrival is { } point)
        {
            round.Step(preparedStart ? "verify prepared character start at the measurement point" : ArriveStep,
                () => round.Write("arrival", preparedStart
                    ? PlayerPlacement.ObserveArrival(round.Client, point, TimeSpan.FromSeconds(Client.ArrivalSeconds), Cancellation)
                    : PlayerPlacement.Arrive(round.Server, round.Client, point, TimeSpan.FromSeconds(Client.ArrivalSeconds), Cancellation, SettleFor)));
        }
    }

    // Two rounds have one save and one restart between them; with more, each names the round it leads to.
    private string Between(string step, int round) => Between(step, round, Rounds);
    internal static string Between(string step, int round, IReadOnlyList<string> rounds) => rounds.Count == 2 ? step : $"{step} before {rounds[round + 1]}";

    private void Check()
    {
        CheckRoundNames(Rounds);
        if (Client.StartAtCharacterSave && Arrival == null)
            throw new ArgumentException("startAtCharacterSave needs an arrival point to verify on the client.");
        if (Client.StartAtCharacterSave && (!Client.Owned || Client.CharacterStart == null))
            throw new ArgumentException("startAtCharacterSave needs an owned client and pinned characterStart staging details.");
        if (string.IsNullOrWhiteSpace(ArriveStep)) throw new ArgumentException("ArriveStep: name the arrival step.");
        if (Client.HostWorld != null) throw new ArgumentException("Client: this client hosts its own world (hostWorld); run it with HostRounds.");
        if (Client.Crossplay && Lobby == null) throw new ArgumentException("Lobby: a crossplay client joins the server's PlayFab lobby; supply Lobby, for example with CrossplayServer.WaitForLobby.");
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
