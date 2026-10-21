using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// One owned dedicated server, from its plan, through every boot it starts, to its stop. It is the one place a run's
/// owned server is wired (#258): each boot gets the plan's environment with the session token, its kept logs
/// (<see cref="Logs"/>, <c>boot-N</c>), a <c>boot-N.process.json</c> record and recorded commands (<c>connection-N.jsonl</c>)
/// in the output, waits on the plan's startup events, is strictly pinned (<see cref="ServerRunPlan.ExpectCommand"/>),
/// and has test access established before it is handed over (<see cref="OwnedServerSession.EnsureTestAccess"/>), the
/// first boot and each <see cref="Restart"/> alike. A stop asks the server to quit and kills it only after the plan's
/// <see cref="ServerRunPlan.QuitSeconds"/>. Where the boots run is its placement: this machine or a host of the environment,
/// from the disposable profile <see cref="PinnedServerRun"/> stages there. <see cref="Game"/> is the current boot's in-game
/// handle. Disposing it stops only the server it started.
/// </summary>
public sealed class ServerActor : IOwnedServer, IDisposable
{
    private readonly OwnedServerSession? _session;
    // A scripted server instead (Fakes.FakeGameSession): a no-game world that answers as an owned server.
    private readonly (IOwnedServer Server, Func<GameActor> FirstBoot, string? LiveLog, Func<GameActor, CrossplayLobby>? Lobby)? _scripted;
    private readonly ServerRunPlan? _plan;
    private readonly IServerPlacement? _placement;
    private readonly string? _output;
    /// <summary>The plan this server runs, when it was given one (its pins are the server's plugin pins).</summary>
    internal ServerRunPlan? Plan => _plan;
    private readonly CancellationToken _cancellation;
    private readonly List<RunLog> _logs = [];
    private GameActor? _game;
    internal (string Path, IReadOnlyDictionary<string, string> Hashes)? OriginalFixture { get; }

    // The one owned-server wiring: every boot's launch, logs, process record and recorded connection.
    internal ServerActor(IServerPlacement placement, ServerRunPlan plan, string output, string sessionCapability, string sessionTokenVariable, CancellationToken cancellation, string? checkpointFixture = null)
    {
        ArgumentNullException.ThrowIfNull(placement); ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(output); ArgumentException.ThrowIfNullOrEmpty(sessionTokenVariable);
        if (checkpointFixture != null) OriginalFixture = (checkpointFixture, WorldFixture.Manifest(checkpointFixture));
        string runtime = RuntimeDirectory = placement.RuntimeDirectory, world = WorldDirectory = placement.WorldDirectory;
        Host = placement.Host; _plan = plan; _placement = placement; _output = output; _cancellation = cancellation;
        int boot = 0, connection = 0;
        _session = new OwnedServerSession((token, launchCancellation) =>
        {
            // The launch adds SteamAppId and the Doorstop loader variables BepInEx needs; a Unix profile starts in its owned loader root.
            var environment = plan.Environment.ToDictionary(entry => entry.Key, entry => plan.Expand(entry.Value, runtime, world));
            environment[sessionTokenVariable] = token;
            var launch = GameLaunch.ForServerWithLoader(placement.GameDirectory, placement.LoaderDirectory,
                plan.LaunchArguments(runtime, world), environment, placement.Platform, placement.MacSteamRuntimeDirectory);
            int n = ++boot;
            var started = placement.Start(n, launch, output, TimeSpan.FromSeconds(plan.QuitSeconds), launchCancellation);
            // What the stop keeps: BepInEx's log, Unity's log when the plan passes -logFile {runtime}/toolkit-unity.log, and the
            // process output (Unity's log on Linux without -logFile).
            // Registered at once, so a boot that fails from here is still scanned.
            lock (_logs) _logs.AddRange(started.Logs);
            try
            {
                File.WriteAllText(Path.Combine(output, "boot-" + n + ".process.json"), JsonSerializer.Serialize(started.Facts));
                started.Started?.Invoke();
            }
            catch
            {
                // The session never owned this process, so only this stop ends it; one that fails leaves a server that may still run.
                try { started.Process.Stop(TimeSpan.FromSeconds(15)); started.Process.Dispose(); }
                catch (Exception stop)
                {
                    MayStillRun = true;
                    Console.Error.WriteLine($"Warning: boot-{n} of the owned server could not be stopped after its start failed: {stop.Message}");
                }
                throw;
            }
            return started.Process;
        }, () => new RecordingTransport(placement.Connect(), Path.Combine(output, "connection-" + ++connection + ".jsonl")),
            world, plan.ExpectCommand, sessionCapability, TimeSpan.FromSeconds(plan.StartupSeconds), TimeSpan.FromSeconds(plan.CommandSeconds), cancellation: cancellation)
        {
            QuitTimeout = TimeSpan.FromSeconds(plan.QuitSeconds), Events = placement.Events(plan),
            // Every boot this actor owns gets test access, so a scenario's restart comes back with it too.
            EnsureTestAccess = true,
        };
    }

    // Test seam: an actor over a scripted session (Fakes.FakeOwnedServer), whose boots log nothing.
    internal ServerActor(OwnedServerSession session, string runtimeDirectory = "", string worldDirectory = "", IGameHost? host = null, ServerRunPlan? plan = null,
        CancellationToken cancellation = default)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        RuntimeDirectory = runtimeDirectory; WorldDirectory = worldDirectory; Host = host; _plan = plan; _cancellation = cancellation;
        _session.EnsureTestAccess = true;
    }

    // Test seam: a scripted world as the owned server (its restarts and joinable wait its own), for scenario tests without a game.
    internal ServerActor(IOwnedServer server, Func<GameActor> firstBoot, string? liveLog, Func<GameActor, CrossplayLobby>? lobby)
    {
        ArgumentNullException.ThrowIfNull(server); ArgumentNullException.ThrowIfNull(firstBoot);
        _scripted = (server, firstBoot, liveLog, lobby);
        RuntimeDirectory = ""; WorldDirectory = "";
    }

    /// <summary>The server's profile, where BepInEx writes: a path on <see cref="Host"/> when it has one.</summary>
    public string RuntimeDirectory { get; }
    /// <summary>The world copy the server saves to: a path on <see cref="Host"/> when it has one.</summary>
    public string WorldDirectory { get; }
    /// <summary>The host the server runs on: this machine's own (a local host) or another (<c>--inventory</c> or a campaign); null only for a scripted server.</summary>
    public IGameHost? Host { get; }
    /// <summary>The current boot's in-game handle; the server must have started.</summary>
    public GameActor Game => _game ?? throw new InvalidOperationException("The owned server has not started.");
    /// <summary>Each boot's kept logs, in boot order, for the teardown scan (<see cref="ScenarioReport.ScanLogs"/>) once the server stopped.</summary>
    public IReadOnlyList<RunLog> Logs { get { lock (_logs) return _logs.ToArray(); } }
    /// <summary>The process ID of every boot this actor started.</summary>
    public IReadOnlyList<int> StartedProcesses => _session?.StartedProcesses ?? [];
    /// <summary>How each boot ended, in boot order, restarts included.</summary>
    public IReadOnlyList<ProcessStop> Stops => _session?.Stops ?? [];
    internal IOwnedProcess? CurrentProcess => _session?.CurrentProcess;

    // Intermediate checkpoint operations retain the placement, its host lock, profile and original fixture. Only the
    // encompassing run disposes that ownership. A forced or pre-existing exit is never a clean checkpoint.
    internal void StopCheckpoint()
    {
        if (_session?.CurrentProcess is not { HasExited: false })
            throw new InvalidOperationException("A checkpoint requires the current live owned server.");
        _game = null;
        _session.Stop();
        if (_session.Stops[^1] is not { Outcome: StopOutcome.Clean, ExitCode: null or 0 })
            throw new InvalidOperationException("The checkpoint server did not stop cleanly: " + _session.Stops[^1]);
        if (_output != null) OwnedClientCommandLease.RetireServer(_output);
    }

    internal void ArchiveCheckpoint(string archive, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellation, cancellation);
        cancellation = linked.Token;
        RequireStoppedCheckpoint();
        (_placement ?? throw new InvalidOperationException("No checkpoint placement.")).ArchiveCheckpoint(archive, cancellation);
        var logs = Logs;
        if (logs.Any(log => log.Required && !File.Exists(log.Path)))
            throw new InvalidDataException("A required stopped boot log is missing.");
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var log in logs.Where(log => File.Exists(log.Path)))
        {
            cancellation.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(_output!, log.Path);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new InvalidOperationException("A checkpoint log is outside its owned evidence.");
            string destination = Path.Combine(archive, "logs", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string hash = FileHash.Sha256(log.Path);
            File.Copy(log.Path, destination);
            if (FileHash.Sha256(destination) != hash || FileHash.Sha256(log.Path) != hash)
                throw new IOException("A stopped checkpoint log changed during its copy.");
            hashes.Add(relative, hash);
        }
        if (hashes.Count == 0) throw new InvalidDataException("A checkpoint has no stopped boot logs.");
        File.WriteAllText(Path.Combine(archive, "logs.sha256.json"), JsonSerializer.Serialize(hashes));
    }

    internal void RestoreCheckpoint(string original, IReadOnlyDictionary<string, string> hashes, string archive, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellation, cancellation);
        RequireStoppedCheckpoint();
        (_placement ?? throw new InvalidOperationException("No checkpoint placement.")).RestoreCheckpoint(original, hashes, archive, linked.Token);
    }

    internal string CheckpointGameDirectory => _placement?.GameDirectory ?? throw new InvalidOperationException("No checkpoint placement.");
    internal string CheckpointLoaderDirectory => _placement?.LoaderDirectory ?? throw new InvalidOperationException("No checkpoint placement.");

    internal void SwapCheckpointPlugin(string operation, string source, string oldSha256, string newSha256,
        string guid, string oldMd5, string newMd5, Action admission, CancellationToken cancellation)
    {
        RequireStoppedCheckpoint();
        if (_plan == null || !_plan.Pins.TryGetValue(guid, out var pin) || pin != oldMd5)
            throw new InvalidOperationException("The stopped plugin does not match the actor's original pin.");
        (_placement ?? throw new InvalidOperationException("No checkpoint placement.")).SwapCheckpointPlugin(
            operation, source, oldSha256, newSha256, admission, cancellation);
        _session!.ReplaceStoppedPluginExpectation(guid, oldMd5, newMd5);
        _plan.Pins[guid] = newMd5;
    }

    internal void StartCheckpoint(CancellationToken cancellation)
    {
        RequireStoppedCheckpoint();
        _game = _session!.StartAsync(cancellation).GetAwaiter().GetResult();
        // The new actor has already verified its own session token, strict pins and test access.
        _session.WaitUntilJoinableCheckpoint(_game, cancellation);
        PublishCommandCapability();
    }

    private void RequireStoppedCheckpoint()
    {
        if (_session == null || _session.CurrentProcess != null || _session.Stops.LastOrDefault() is not { Outcome: StopOutcome.Clean, ExitCode: null or 0 })
            throw new InvalidOperationException("The owned server must have a proven clean checkpoint stop.");
    }
    /// <summary>
    /// The current boot's live BepInEx log, for a scenario's in-run log reads: on this machine only (null when the server runs
    /// on another host, where <see cref="Lobby"/> reads its log there).
    /// </summary>
    public string? LiveLog => _scripted is { } scripted ? scripted.LiveLog
        : (Host == null || Host.Kind == GameHostKind.Local) && RuntimeDirectory.Length != 0 ? Path.Combine(RuntimeDirectory, "BepInEx", "LogOutput.log") : null;

    /// <summary>
    /// The current boot's line where the game refused a peer on network version <paramref name="network"/> (#860), from the log the
    /// game writes to (the plan's <c>-logFile</c> file, otherwise BepInEx's), awaited within <paramref name="within"/> (BepInEx flushes
    /// its disk log every few seconds): on this machine in the log's last <see cref="RefusalLogTail"/> bytes, on the server's host from
    /// the log's start. Never retried after <paramref name="within"/>. Null when there is none, or the server is scripted or has no
    /// plan. For a refused join's reason.
    /// </summary>
    internal string? IncompatibleVersionLine(uint network, TimeSpan within, CancellationToken cancellation)
    {
        if (_scripted != null || _plan is not { } plan || RuntimeDirectory.Length == 0) return null;
        var pattern = JoinRefusalText.IncompatibleVersionOf(network);
        if (Host == null || Host.Kind == GameHostKind.Local)
        {
            string log = plan.GameLogFile(RuntimeDirectory, WorldDirectory) ?? CrossplayServer.BepInExLog(RuntimeDirectory);
            var deadline = DateTime.UtcNow + within;
            for (;;)
            {
                if (LastRefusal(log, pattern, cancellation) is { } line) return line;
                if (DateTime.UtcNow >= deadline) return null;
                cancellation.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250));
                cancellation.ThrowIfCancellationRequested();
            }
        }
        var result = Host.WaitForLogAsync(plan.GameLogFile(RuntimeDirectory, WorldDirectory) ?? CrossplayServer.HostBepInExLog(RuntimeDirectory), 0,
            pattern, [], within, cancellation).GetAwaiter().GetResult();
        return result.Outcome == HostLogOutcome.Matched ? result.Line : null;
    }

    /// <summary>How much of a local server log's end the refusal read looks at: the refusal is the latest event of a join just refused.</summary>
    internal const long RefusalLogTail = 4 << 20;

    /// <summary>The last line of <paramref name="log"/>'s last <see cref="RefusalLogTail"/> bytes that <paramref name="pattern"/> matches; the game may still write it.</summary>
    internal static string? LastRefusal(string log, Regex pattern, CancellationToken cancellation, long tail = RefusalLogTail)
    {
        if (!File.Exists(log)) return null;
        using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > tail) stream.Seek(-tail, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        string? last = null;
        for (string? line; (line = reader.ReadLine()) != null;)
        {
            cancellation.ThrowIfCancellationRequested();
            if (pattern.IsMatch(line)) last = line;
        }
        return last;
    }

    /// <summary>
    /// The crossplay server's lobby for its current boot <paramref name="server"/>, read from the log the game writes to (the plan's
    /// <c>-logFile</c> file, otherwise BepInEx's), on this machine or on the server's host, within the plan's startup time
    /// (<see cref="CrossplayServer.WaitForLobby(GameActor, string, TimeSpan, CancellationToken)"/>). A restarted server opens a new one.
    /// </summary>
    public CrossplayLobby Lobby(GameActor server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (_scripted is { Lobby: { } scripted }) return scripted(server);
        var plan = _plan ?? throw new InvalidOperationException("This server has no plan to read its lobby's log from.");
        var within = TimeSpan.FromSeconds(plan.StartupSeconds);
        return Host == null
            ? CrossplayServer.WaitForLobby(server, plan.GameLogFile(RuntimeDirectory, WorldDirectory) ?? CrossplayServer.BepInExLog(RuntimeDirectory), within, _cancellation)
            : WaitForLobby(server, Host, plan.GameLogFile(RuntimeDirectory, WorldDirectory) ?? CrossplayServer.HostBepInExLog(RuntimeDirectory), within, _cancellation);
    }
    /// <summary>
    /// <see cref="CrossplayServer.WaitForLobby(GameActor, string, TimeSpan, CancellationToken)"/> for a server on another machine
    /// (<c>PinnedServerRun --inventory</c> or a campaign): the lobby line is awaited on <paramref name="host"/> in <paramref name="serverLog"/>,
    /// the host's path of this boot's log (<see cref="CrossplayServer.HostBepInExLog"/> of the run's host runtime), from its start, with
    /// the host's event-driven <see cref="IGameHost.WaitForLogAsync"/>.
    /// </summary>
    internal static CrossplayLobby WaitForLobby(GameActor server, IGameHost host, string serverLog, TimeSpan timeout, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        WaitText.RequireTimeout(timeout);
        var identity = CrossplayServer.RequirePlayFabServer(server);
        var result = host.WaitForLogAsync(serverLog, 0, CrossplayServer.LobbyCreated, CrossplayServer.LobbyFailures, timeout, cancellation).GetAwaiter().GetResult();
        return CrossplayServer.Lobby(identity, CrossplayServer.LobbyCreated.Match(result.EnsureMatched()), host.Name + ":" + serverLog);
    }

    /// <summary>A boot whose start failed after its process started could not be stopped: the server may still run, so its runtime is kept.</summary>
    internal bool MayStillRun { get; private set; }

    /// <summary>Starts the first boot and returns its strictly pinned, test-access-ready actor (<see cref="OwnedServerSession.StartAsync"/>).</summary>
    public GameActor Start()
    {
        _game = _scripted is { } scripted ? scripted.FirstBoot() : _session!.Start();
        PublishCommandCapability();
        return _game;
    }
    /// <summary>Stops only this server, then starts it again with test access; returns the new boot's actor.</summary>
    public GameActor Restart()
    {
        _game = null; // A failed stop never leaves the stopped boot's handle current.
        _game = _scripted is { } scripted ? scripted.Server.Restart() : _session!.Restart();
        PublishCommandCapability();
        return _game;
    }
    // The boot's local command capability (valheim-test cli --role server), published only once Start or Restart returned:
    // after the read-only bootstrap and strict verification, so a separate command never reaches a boot still being
    // verified. It is optional: a failure warns and publishes less (no lease, or no world pins), never fails the boot.
    private void PublishCommandCapability()
    {
        if (_placement?.PublishesCommandLease != true) return;
        _placement.PublishCommandLease(_output!);
        string worldUid;
        if (!_plan!.Pins.TryGetValue("worlduid", out worldUid!))
        {
            // Startup owns world readiness and strict verification. Placement receives only the resulting identity; it
            // neither discovers capabilities nor performs another game observation.
            SessionState state;
            try { state = new SessionControl(_game!).Read(); }
            catch (Exception error) when (error is InvalidOperationException or IOException or InvalidDataException or TimeoutException or
                JsonException or FormatException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("Warning: the owned server's world could not be read for its command pins: " + error.Message +
                    "; world-phase commands stay refused.");
                return;
            }
            if (!state.Dedicated || !state.WorldReady || state.WorldUid == null)
            {
                Console.Error.WriteLine("Warning: the owned server's exact world is not verified; its world command pins were not published.");
                return;
            }
            worldUid = state.WorldUid;
        }
        _placement.PublishVerifiedWorld(worldUid, _output!);
    }
    /// <summary>Waits until <paramref name="server"/>, this server's current actor, accepts game connections, within the plan's startup deadline.</summary>
    public void WaitUntilJoinable(GameActor server)
    {
        if (_scripted is { } scripted) scripted.Server.WaitUntilJoinable(server);
        else _session!.WaitUntilJoinable(server);
    }
    /// <summary>Stops only the server this actor started (asked to quit first, killed after the plan's quit time).</summary>
    public void Dispose()
    {
        _game = null;
        _session?.Dispose(); // A scripted world is its test's to dispose.
    }
}

/// <summary>Where an owned server's boots run: what differs between this machine and a host of the environment.</summary>
internal interface IServerPlacement
{
    void SwapCheckpointPlugin(string operation, string source, string oldSha256, string newSha256, Action admission, CancellationToken cancellation) =>
        throw new NotSupportedException("This placement has no detached build swap.");
    void ArchiveCheckpoint(string archive, CancellationToken cancellation) => throw new NotSupportedException("This placement has no detached checkpoints.");
    void RestoreCheckpoint(string original, IReadOnlyDictionary<string, string> hashes, string archive, CancellationToken cancellation) =>
        throw new NotSupportedException("This placement has no detached checkpoints.");
    string RuntimeDirectory { get; }
    string GameDirectory => RuntimeDirectory;
    string LoaderDirectory => RuntimeDirectory;
    string? MacSteamRuntimeDirectory => null;
    string WorldDirectory { get; }
    /// <summary>The host the boots run on, or null for this machine.</summary>
    IGameHost? Host { get; }
    /// <summary>The launch's platform on a host, or null for this machine's own.</summary>
    ServerPlatform? Platform { get; }
    /// <summary>
    /// Starts boot <paramref name="boot"/> (from 1), which its stop will give <paramref name="quit"/> to quit when asked (a journalling
    /// placement records it, so env recover gives it the same). A failure after the returned process started is the actor's to stop.
    /// </summary>
    ServerBoot Start(int boot, GameLaunch launch, string output, TimeSpan quit, CancellationToken cancellation);
    /// <summary>A new connection to the server's ValheimCLI.</summary>
    IGameTransport Connect();
    StartupEvents? Events(ServerRunPlan plan);
    /// <summary>Whether this placement publishes a host-local command capability once a boot passed startup verification.</summary>
    bool PublishesCommandLease => false;
    /// <summary>Publishes the current boot's command lease after it completed strict verification; never throws (it warns).</summary>
    void PublishCommandLease(string output) { }
    /// <summary>Publishes the exact world UID supplied by startup after this boot completed strict verification; remote placements do nothing.</summary>
    void PublishVerifiedWorld(string worldUid, string output) { }
}

/// <summary>A started boot: its process, the logs its stop keeps, its <c>boot-N.process.json</c> facts, and what the placement records once that file is written.</summary>
internal sealed record ServerBoot(IOwnedProcess Process, IReadOnlyList<RunLog> Logs, Dictionary<string, object?> Facts, Action? Started = null);
