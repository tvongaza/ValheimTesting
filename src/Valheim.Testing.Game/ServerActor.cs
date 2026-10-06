using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>
/// One owned dedicated server, from its plan, through every boot it starts, to its stop. It is the one place a run's
/// owned server is wired (#258): each boot gets the plan's environment with the session token, its kept logs
/// (<see cref="Logs"/>, <c>boot-N</c>), a <c>boot-N.process.json</c> record and recorded commands (<c>connection-N.jsonl</c>)
/// in the output, waits on the plan's startup events, is strictly pinned (<see cref="ServerRunPlan.ExpectCommand"/>),
/// and has test access established before it is handed over (<see cref="OwnedServerSession.EnsureTestAccess"/>), the
/// first boot and each <see cref="Restart"/> alike. A stop asks the server to quit and kills it only after the plan's
/// <see cref="ServerRunPlan.QuitSeconds"/>. Where the boots run is its placement: this machine (<see cref="OnThisMachine"/>),
/// or a host of the environment, which <see cref="PinnedServerRun"/> places. <see cref="Game"/> is the current boot's
/// in-game handle. Disposing it stops only the server it started.
/// </summary>
public sealed class ServerActor : IOwnedServer, IDisposable
{
    private readonly OwnedServerSession _session;
    private readonly List<RunLog> _logs = [];
    private GameActor? _game;

    // The one owned-server wiring: every boot's launch, logs, process record and recorded connection.
    internal ServerActor(IServerPlacement placement, ServerRunPlan plan, string output, string sessionCapability, string sessionTokenVariable, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(placement); ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(output); ArgumentException.ThrowIfNullOrEmpty(sessionTokenVariable);
        string runtime = RuntimeDirectory = placement.RuntimeDirectory, world = WorldDirectory = placement.WorldDirectory;
        Host = placement.Host;
        int boot = 0, connection = 0;
        _session = new OwnedServerSession(token =>
        {
            // The launch adds SteamAppId and, for Linux, the Doorstop loader variables BepInEx needs; the working directory is the runtime.
            var environment = plan.Environment.ToDictionary(entry => entry.Key, entry => plan.Expand(entry.Value, runtime, world));
            environment[sessionTokenVariable] = token;
            var launch = GameLaunch.ForServer(runtime, plan.LaunchArguments(runtime, world), environment, placement.Platform);
            int n = ++boot;
            var started = placement.Start(n, launch, output, cancellation);
            // What the stop keeps: BepInEx's log, Unity's log when the plan passes -logFile {runtime}/toolkit-unity.log, and the
            // process output (Unity's log on Linux without -logFile).
            // Registered at once, so a boot that fails from here is still scanned.
            lock (_logs) _logs.AddRange(started.Logs);
            try
            {
                File.WriteAllText(Path.Combine(output, "boot-" + n + ".process.json"), JsonSerializer.Serialize(EnvironmentPinning.Stamp(started.Facts, plan.Pinned)));
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
        }, () => new RecordingTransport(placement.Connect(), Path.Combine(output, "connection-" + ++connection + ".jsonl"), plan.Pinned ? null : EnvironmentPinning.NotPinned),
            world, plan.ExpectCommand, sessionCapability, TimeSpan.FromSeconds(plan.StartupSeconds), TimeSpan.FromSeconds(plan.CommandSeconds), cancellation: cancellation)
        {
            QuitTimeout = TimeSpan.FromSeconds(plan.QuitSeconds), Events = placement.Events(plan),
            // Every boot this actor owns gets test access, so a scenario's restart comes back with it too.
            EnsureTestAccess = true,
        };
    }

    // Test seam: an actor over a scripted session (Fakes.FakeOwnedServer), whose boots log nothing.
    internal ServerActor(OwnedServerSession session, string runtimeDirectory = "", string worldDirectory = "", IGameHost? host = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        RuntimeDirectory = runtimeDirectory; WorldDirectory = worldDirectory; Host = host;
        _session.EnsureTestAccess = true;
    }

    /// <summary>
    /// An owned server on this machine, run from <paramref name="runtimeDirectory"/> and saving to <paramref name="worldDirectory"/>
    /// (both disposable copies the caller made), with its evidence written to <paramref name="output"/>. ValheimCLI is reached on
    /// loopback at the plan's port. <paramref name="sessionCapability"/> is the mod's read-only session capability (for example
    /// <c>my.mod.testing/session</c>) and <paramref name="sessionTokenVariable"/> the environment variable that passes the owned
    /// session token to the in-game adapter.
    /// </summary>
    public static ServerActor OnThisMachine(ServerRunPlan plan, string runtimeDirectory, string worldDirectory, string output,
        string sessionCapability, string sessionTokenVariable, CancellationToken cancellation = default) =>
        new(new LocalServerPlacement(runtimeDirectory, worldDirectory, plan.Port), plan, output, sessionCapability, sessionTokenVariable, cancellation);

    /// <summary>The runtime copy the server runs from: a path on <see cref="Host"/> when it has one.</summary>
    public string RuntimeDirectory { get; }
    /// <summary>The world copy the server saves to: a path on <see cref="Host"/> when it has one.</summary>
    public string WorldDirectory { get; }
    /// <summary>The host the server runs on (<c>--inventory</c> or a campaign), or null when it runs on this machine.</summary>
    public IGameHost? Host { get; }
    /// <summary>The current boot's in-game handle; the server must have started.</summary>
    public GameActor Game => _game ?? throw new InvalidOperationException("The owned server has not started.");
    /// <summary>Each boot's kept logs, in boot order, for the teardown scan (<see cref="ScenarioReport.ScanLogs"/>) once the server stopped.</summary>
    public IReadOnlyList<RunLog> Logs { get { lock (_logs) return _logs.ToArray(); } }
    /// <summary>The process ID of every boot this actor started.</summary>
    public IReadOnlyList<int> StartedProcesses => _session.StartedProcesses;
    /// <summary>How each boot ended, in boot order, restarts included.</summary>
    public IReadOnlyList<ProcessStop> Stops => _session.Stops;
    /// <summary>A boot whose start failed after its process started could not be stopped: the server may still run, so its runtime is kept.</summary>
    internal bool MayStillRun { get; private set; }

    /// <summary>Starts the first boot and returns its strictly pinned, test-access-ready actor (<see cref="OwnedServerSession.StartAsync"/>).</summary>
    public GameActor Start() => _game = _session.Start();
    /// <summary>Stops only this server, then starts it again with test access; returns the new boot's actor.</summary>
    public GameActor Restart()
    {
        _game = null; // A failed stop never leaves the stopped boot's handle current.
        return _game = _session.Restart();
    }
    /// <summary>Waits until <paramref name="server"/>, this server's current actor, accepts game connections, within the plan's startup deadline.</summary>
    public void WaitUntilJoinable(GameActor server) => _session.WaitUntilJoinable(server);
    /// <summary>Stops only the server this actor started (asked to quit first, killed after the plan's quit time).</summary>
    public void Dispose()
    {
        _game = null;
        _session.Dispose();
    }
}

/// <summary>Where an owned server's boots run: what differs between this machine and a host of the environment.</summary>
internal interface IServerPlacement
{
    string RuntimeDirectory { get; }
    string WorldDirectory { get; }
    /// <summary>The host the boots run on, or null for this machine.</summary>
    IGameHost? Host { get; }
    /// <summary>The launch's platform on a host, or null for this machine's own.</summary>
    ServerPlatform? Platform { get; }
    /// <summary>Starts boot <paramref name="boot"/> (from 1). A failure after the returned process started is the actor's to stop.</summary>
    ServerBoot Start(int boot, GameLaunch launch, string output, CancellationToken cancellation);
    /// <summary>A new connection to the server's ValheimCLI.</summary>
    IGameTransport Connect();
    StartupEvents? Events(ServerRunPlan plan);
}

/// <summary>A started boot: its process, the logs its stop keeps, its <c>boot-N.process.json</c> facts, and what the placement records once that file is written.</summary>
internal sealed record ServerBoot(IOwnedProcess Process, IReadOnlyList<RunLog> Logs, Dictionary<string, object?> Facts, Action? Started = null);

/// <summary>Boots on this machine: a direct child process, ValheimCLI on loopback, startup read from the runtime's own log.</summary>
internal sealed class LocalServerPlacement(string runtimeDirectory, string worldDirectory, int port) : IServerPlacement
{
    public string RuntimeDirectory { get; } = runtimeDirectory;
    public string WorldDirectory { get; } = worldDirectory;
    public ServerPlatform? Platform => null;
    public IGameHost? Host => null;

    public ServerBoot Start(int boot, GameLaunch launch, string output, CancellationToken cancellation)
    {
        string prefix = Path.Combine(output, "boot-" + boot);
        var process = new DirectServerProcess(launch.ToStartInfo(), prefix,
            Path.Combine(RuntimeDirectory, "BepInEx", "LogOutput.log"), Path.Combine(RuntimeDirectory, "toolkit-unity.log"));
        return new ServerBoot(process,
            [new RunLog($"boot-{boot} BepInEx log", prefix + ".game-0.log", Required: true), new RunLog($"boot-{boot} Unity log", prefix + ".game-1.log"),
             new RunLog($"boot-{boot} stdout", prefix + ".stdout.log")],
            new() { ["pid"] = process.Id, ["startedUtc"] = DateTime.UtcNow, ["world"] = WorldDirectory });
    }

    public IGameTransport Connect() => new CliTransport("127.0.0.1", port);
    public StartupEvents? Events(ServerRunPlan plan) => plan.DedicatedStartupEvents(RuntimeDirectory);
}
