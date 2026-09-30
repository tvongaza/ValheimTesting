using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>
/// A game client for a system test: one this session launched and owns, or one an operator runs that it only attaches
/// to. Either way <see cref="Actor"/> is strictly pinned to the plan's menu expectations when the session is returned,
/// and every command it sends is recorded to <c>client-commands.jsonl</c> in the output directory (<c>client-commands-2.jsonl</c>
/// and so on for later sessions: evidence is never overwritten). Disposing closes the
/// connection and, for an owned client, stops only the process this session started (never one found by name): it asks the
/// client to quit and kills it only if it does not (<see cref="Stopped"/>), then keeps its logs beside the evidence. An
/// attached client's process is never touched.
/// </summary>
public sealed class ClientSession : IDisposable
{
    private readonly IServerProcess? _process;
    private bool _disposed;
    public GameActor Actor { get; }
    public bool Owned => _process != null;
    /// <summary>The owned client's process ID, or null for an attached client.</summary>
    public int? ProcessId => _process?.Id;
    /// <summary>
    /// The slice the owned client was launched as (<see cref="ClientRunPlan.Architecture"/>; x64 unless the plan asked for arm64),
    /// or null for an attached client, whose operator chose it.
    /// </summary>
    public ClientArchitecture? Architecture { get; }
    /// <summary>
    /// The owned client's logs as kept beside the evidence once it is disposed (its BepInEx log and Unity's Player.log),
    /// for a teardown <see cref="ScenarioReport.ScanLogs"/>; empty for an attached client, whose logs are its operator's.
    /// </summary>
    public IReadOnlyList<RunLog> Logs { get; }

    /// <summary>How long disposing waits for the owned client to quit (its profile save) after asking it, before it kills it. Default one minute.</summary>
    public TimeSpan QuitTimeout { get; set; } = TimeSpan.FromMinutes(1);
    /// <summary>How the owned client ended once disposed; null before that, and for an attached client.</summary>
    public ProcessStop? Stopped { get; private set; }

    private ClientSession(GameActor actor, IServerProcess? process, IReadOnlyList<RunLog>? logs = null, ClientArchitecture? architecture = null)
    {
        Actor = actor; _process = process; Logs = logs ?? []; Architecture = architecture;
    }

    /// <summary><see cref="Launch(ClientRunPlan, string, CancellationToken)"/> or <see cref="Attach"/>, as the plan's mode says.</summary>
    public static ClientSession Open(ClientRunPlan plan, string output, CancellationToken cancellation = default) =>
        plan.Owned ? Launch(plan, output, cancellation) : Attach(plan, output);

    /// <summary>Connects to an operator's client and verifies its menu pins. The operator launched it and still owns it.</summary>
    public static ClientSession Attach(ClientRunPlan plan, string output, IGameTransport? transport = null)
    {
        if (plan.Owned) throw new ArgumentException("This plan's client is owned: launch it instead.");
        transport ??= new CliTransport(plan.Host, plan.Port);
        GameActor actor;
        try { actor = new GameActor("client", new RecordingTransport(transport, CommandLog(output), plan.Pinned ? null : EnvironmentPinning.NotPinned)); }
        catch { transport.Dispose(); throw; }
        try { actor.VerifyEnvironment(plan.MenuExpectations); return new ClientSession(actor, null); }
        catch { actor.Dispose(); throw; }
    }

    /// <summary>
    /// Launches the plan's client install with <see cref="ClientLaunch"/> and waits, on events, until it is at its main
    /// menu with the pinned plugins: ValheimCLI's listening line in this launch's BepInEx log, then the MainMenu state
    /// push. Every wait races the process exit, which ends startup at once with its exit code. Refuses before launching
    /// when something already listens on the client's CLI port (a command could reach a client this session does not own),
    /// when no Steam client is running here, when the plan's password variable is not set in this process (the client
    /// inherits it), when the install's <c>BepInEx/patchers</c> holds anything the plan's <see cref="ClientRunPlan.Patchers"/>
    /// does not name, when its game build, BepInEx core or patchers are not the plan's <see cref="ClientRunPlan.InstallPins"/>,
    /// or when <see cref="ClientLaunch"/> refuses the install for the plan's <see cref="ClientRunPlan.Architecture"/> (an
    /// arm64 request without an arm64 Doorstop library or a native BepInEx core is refused, never run under Rosetta).
    /// A failed startup stops the process it started. The process's output goes to
    /// <c>client-boot.stdout.log</c>/<c>.stderr.log</c>, and its BepInEx log and Unity's Player.log are copied beside them
    /// (<c>client-boot.game-0.log</c>, <c>client-boot.game-1.log</c>) when it stops; <see cref="Logs"/> lists them.
    /// </summary>
    public static ClientSession Launch(ClientRunPlan plan, string output, CancellationToken cancellation = default)
    {
        if (!plan.Owned) throw new ArgumentException("This plan's client is attached: its operator launches it.");
        BepInExLoader.RequirePatchers(plan.Install, plan.Patchers, "client install");
        plan.CheckInstallPins();
        var start = StartInfo(plan, ClientLaunch.CurrentHost); // The install's loader and slices, before any port or Steam check.
        var reservation = new TcpListener(IPAddress.Loopback, plan.Port);
        try { reservation.Start(); }
        catch (SocketException error) { throw new InvalidOperationException($"Something already listens on the client's CLI port {plan.Port}; stop it first, this session only drives a client it launched.", error); }
        finally { reservation.Stop(); }
        if (!SteamRunning()) throw new InvalidOperationException("No Steam client is running in this session. An owned client needs Steam running and signed in, in the desktop session this runner runs in.");
        string log = Path.Combine(plan.Install, "BepInEx", "LogOutput.log");
        var platform = ClientLaunch.Detect(plan.Install);
        string playerLog = PlayerLog(platform);
        string prefix = Path.Combine(output, "client-boot");
        LogWait? cliLog = null;
        try
        {
            return Launch(plan, output,
                () =>
                {
                    cliLog = new LogWait(log); // Opened before the launch: an earlier run's lines never count.
                    return new DirectServerProcess(start, prefix, log, playerLog) { Quit = QuitRequest.CloseWindow };
                },
                () => new CliTransport(plan.Host, plan.Port),
                async (left, token) =>
                {
                    var clock = Stopwatch.StartNew();
                    await cliLog!.WaitAsync(StartupEvents.CliListening, left, StartupEvents.StartupFailures, token).ConfigureAwait(false);
                    using var states = StateWait.Connect(plan.Host, plan.Port);
                    await states.WaitAsync([StateWait.MainMenu], left - clock.Elapsed, cancellation: token).ConfigureAwait(false);
                }, cancellation, () => cliLog != null && !cliLog.HasOutput() ? StartupEvents.NoBepInExLog(log, playerLog) : null,
                [new RunLog("client BepInEx log", prefix + ".game-0.log", Required: true), new RunLog("client Player.log", prefix + ".game-1.log")]);
        }
        finally { cliLog?.Dispose(); }
    }

    /// <summary>The owned launch of the plan's install as the plan's architecture, built for <paramref name="host"/> (injectable for tests).</summary>
    internal static ProcessStartInfo StartInfo(ClientRunPlan plan, ClientPlatform host) =>
        ClientLaunch.CreateStartInfo(plan.Install, plan.LaunchArguments, null, plan.LaunchArchitecture, true, host);

    /// <summary>Where Unity writes the game client's Player.log on <paramref name="platform"/> (company IronGate, product Valheim).</summary>
    internal static string PlayerLog(ClientPlatform platform)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return platform switch
        {
            ClientPlatform.Windows => Path.Combine(home, "AppData", "LocalLow", "IronGate", "Valheim", "Player.log"),
            ClientPlatform.MacOS => Path.Combine(home, "Library", "Logs", "IronGate", "Valheim", "Player.log"),
            _ => Path.Combine(home, ".config", "unity3d", "IronGate", "Valheim", "Player.log"),
        };
    }

    /// <summary>
    /// The owned launch with its process, connection and readiness supplied, so a test can drive startup failures and
    /// cleanup without a game. <paramref name="ready"/> gets the time left and a token that is cancelled when the process
    /// exits first.
    /// </summary>
    public static ClientSession Launch(ClientRunPlan plan, string output, Func<IServerProcess> start, Func<IGameTransport> connect,
        Func<TimeSpan, CancellationToken, Task> ready, CancellationToken cancellation = default) =>
        Launch(plan, output, start, connect, ready, cancellation, null, null);

    // exitHint adds to an early exit's reason, for example that BepInEx never wrote its log.
    internal static ClientSession Launch(ClientRunPlan plan, string output, Func<IServerProcess> start, Func<IGameTransport> connect,
        Func<TimeSpan, CancellationToken, Task> ready, CancellationToken cancellation, Func<string?>? exitHint, IReadOnlyList<RunLog>? logs)
    {
        if (plan.PasswordVariable is { } variable && Environment.GetEnvironmentVariable(variable) == null)
            throw new InvalidOperationException($"Set {variable} in this runner's environment; the launched client inherits it for the join.");
        var architecture = plan.LaunchArchitecture;
        var clock = Stopwatch.StartNew();
        var deadline = TimeSpan.FromSeconds(plan.StartSeconds);
        var process = start();
        GameActor? actor = null;
        try
        {
            File.WriteAllText(Path.Combine(output, "client-process.json"), JsonSerializer.Serialize(EnvironmentPinning.Stamp(new() { ["pid"] = process.Id, ["startedUtc"] = DateTime.UtcNow, ["install"] = plan.Install, ["architecture"] = ClientLaunch.PlanName(architecture) }, plan.Pinned)));
            using (var abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                var exited = process.WaitForExitAsync(abandon.Token);
                var waiting = ready(deadline, abandon.Token);
                var first = Task.WhenAny(waiting, exited, Elapsed(clock, deadline, abandon.Token)).GetAwaiter().GetResult();
                abandon.Cancel();
                // An exit counts even when readiness completed too: a client that is gone is not at its menu.
                if (exited.IsCompletedSuccessfully)
                    throw new WaitFailedException("client at its main menu", "the owned client exited with code " + exited.Result + exitHint?.Invoke(), clock.Elapsed, null);
                cancellation.ThrowIfCancellationRequested();
                if (first != waiting) throw new WaitTimeoutException("client at its main menu within the start deadline", clock.Elapsed, null);
                waiting.GetAwaiter().GetResult(); // A readiness failure (a plugin-load error, a closed state connection) ends startup.
            }
            var transport = connect();
            try { actor = new GameActor("client", new RecordingTransport(transport, CommandLog(output), plan.Pinned ? null : EnvironmentPinning.NotPinned)); }
            catch { transport.Dispose(); throw; }
            actor.VerifyEnvironment(plan.MenuExpectations);
            return new ClientSession(actor, process, logs, architecture);
        }
        catch
        {
            actor?.Dispose();
            try { process.Stop(TimeSpan.FromSeconds(15)); } finally { process.Dispose(); }
            throw;
        }
    }

    /// <summary>
    /// Completes once <paramref name="clock"/> has passed <paramref name="deadline"/>. Timers can fire slightly before a
    /// Stopwatch agrees (Windows' coarse timer: a 1 s delay measured 0.9999 s), so an early wake-up waits out the rest
    /// instead of reporting a timeout before the deadline. No polling: at most one more short delay.
    /// </summary>
    private static async Task Elapsed(Stopwatch clock, TimeSpan deadline, CancellationToken cancellation)
    {
        for (var left = deadline - clock.Elapsed; left > TimeSpan.Zero; left = deadline - clock.Elapsed)
            await Task.Delay(left < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : left, cancellation).ConfigureAwait(false);
    }

    private static string CommandLog(string output)
    {
        string path = Path.Combine(output, "client-commands.jsonl");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(output, $"client-commands-{n}.jsonl");
        return path;
    }

    private static bool SteamRunning()
    {
        string name = OperatingSystem.IsMacOS() ? "steam_osx" : "steam";
        var found = Process.GetProcessesByName(name);
        foreach (var process in found) process.Dispose();
        return found.Length != 0;
    }

    /// <summary>
    /// Closes the connection and, for an owned client, stops the process this session started: asks it to quit (it saves
    /// its profile, as a player's quit does), waits up to <see cref="QuitTimeout"/>, and kills it only then. <see cref="Stopped"/> says which.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Actor.Dispose(); }
        finally
        {
            if (_process != null)
                try { Stopped = _process.StopCleanly(QuitTimeout, TimeSpan.FromSeconds(15)); } finally { _process.Dispose(); }
        }
    }
}
