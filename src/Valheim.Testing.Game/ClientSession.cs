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
/// attached client's process is never touched. A <see cref="ClientRunPlan.DirectStart"/> owned client is pinned to its
/// world before the session is returned; ordinary clients are pinned at the menu. Given a <see cref="SteamAccountHold"/>, a session starts or attaches only while
/// that lease is live (and signed-in checked when the profile asks), and a lost lease stops the owned client at once (an attached
/// client is detached); the hold's owner releases it after disposing the session.
/// </summary>
public sealed class ClientSession : IDisposable
{
    private readonly IServerProcess? _process;
    private readonly object _stopping = new();
    private bool _disposed, _detachedForAccount, _stoppedForAccount;
    private CancellationTokenRegistration _accountLost;
    public GameActor Actor { get; }
    /// <summary>The Steam account lease this client runs on, or null when the run leases none.</summary>
    public SteamAccountHold? Account { get; private set; }
    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    public bool Closed => _disposed;
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

    /// <summary><see cref="Launch(ClientRunPlan, string, CancellationToken)"/> or <see cref="Attach(ClientRunPlan, string, IGameTransport)"/>, as the plan's mode says.</summary>
    public static ClientSession Open(ClientRunPlan plan, string output, CancellationToken cancellation = default) =>
        plan.Owned ? Launch(plan, output, cancellation) : Attach(plan, output);

    /// <summary><see cref="Open(ClientRunPlan, string, CancellationToken)"/> on the leased Steam account <paramref name="account"/> (none when null).</summary>
    public static ClientSession Open(ClientRunPlan plan, string output, SteamAccountHold? account, CancellationToken cancellation = default) =>
        plan.Owned ? Launch(plan, output, account, cancellation) : Attach(plan, output, account);
    /// <summary>
    /// <see cref="Open(ClientRunPlan, string, CancellationToken)"/>, adding the logs the owned client keeps beside the evidence
    /// (<see cref="Logs"/>) to <paramref name="logs"/> for the teardown scan: when the session opens, and also when its startup
    /// fails after the process started, so a client that never reached its menu is still scanned and listed in the result.
    /// </summary>
    public static ClientSession Open(ClientRunPlan plan, string output, ICollection<RunLog> logs, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ClientSession session;
        try { session = Open(plan, output, cancellation); }
        catch (Exception error)
        {
            foreach (var log in KeptLogs(error)) logs.Add(log);
            throw;
        }
        foreach (var log in session.Logs) logs.Add(log);
        return session;
    }

    // A failed owned startup carries the logs its stopped process kept, for Open's log list (and the profile client's path).
    private const string KeptLogsKey = "Valheim.Testing.Game.ClientSession.KeptLogs";
    /// <summary>The logs a failed owned startup kept beside the evidence (its process had started); empty for any other failure.</summary>
    internal static IReadOnlyList<RunLog> KeptLogs(Exception error) => error.Data[KeptLogsKey] as IReadOnlyList<RunLog> ?? [];

    /// <summary>Connects to an operator's client and verifies its menu pins. The operator launched it and still owns it.</summary>
    public static ClientSession Attach(ClientRunPlan plan, string output, IGameTransport? transport = null) => Attach(plan, output, null, transport);

    /// <summary>
    /// <see cref="Attach(ClientRunPlan, string, IGameTransport)"/> once <paramref name="account"/>'s lease is live (and signed-in
    /// checked when the profile asks). Losing the lease detaches the session; the operator's client is never touched.
    /// </summary>
    public static ClientSession Attach(ClientRunPlan plan, string output, SteamAccountHold? account, IGameTransport? transport = null)
    {
        if (plan.Owned) throw new ArgumentException("This plan's client is owned: launch it instead.");
        account?.RequireReady(null); // Before the session assumes the client.
        transport ??= new CliTransport(plan.Host, plan.Port);
        GameActor actor;
        try { actor = new GameActor("client", new RecordingTransport(transport, CommandLog(output), plan.Pinned ? null : EnvironmentPinning.NotPinned)); }
        catch { transport.Dispose(); throw; }
        try
        {
            actor.VerifyEnvironment(plan.MenuExpectations);
            if (plan.Capabilities.Length != 0) CliCapabilities.Require(actor, plan.Capabilities); // The only capability check an attached client gets.
            return new ClientSession(actor, null).Using(account);
        }
        catch { actor.Dispose(); throw; }
    }

    /// <summary>
    /// Launches the plan's client install with <see cref="ClientLaunch"/> and waits, on events, until it reaches its main
    /// menu, or the pinned world for <see cref="ClientRunPlan.DirectStart"/>: ValheimCLI's listening line in this launch's
    /// BepInEx log, then the corresponding state push. A direct start writes a password-free request into the run output
    /// and verifies player readiness and the world UID before returning. Every wait races the process exit, which ends
    /// startup at once with its exit code. Refuses before launching
    /// when something already listens on the client's CLI port (a command could reach a client this session does not own),
    /// when no Steam client is running here, when the plan's password variable is not set in this process (the client
    /// inherits it), when the install's <c>BepInEx/patchers</c> holds anything the plan's <see cref="ClientRunPlan.Patchers"/>
    /// does not name, when its game build, BepInEx core or patchers are not the plan's <see cref="ClientRunPlan.InstallPins"/>,
    /// when <see cref="ClientLaunch"/> refuses the install for the plan's <see cref="ClientRunPlan.Architecture"/> (an
    /// arm64 request without an arm64 Doorstop library or a native BepInEx core is refused, never run under Rosetta) or its
    /// Doorstop proxy and configuration are from different versions, or when the rest of
    /// <see cref="ClientRunPlan.Preflight()"/>'s install checks fail (a pinned plugin build that is not installed, a script
    /// ScriptEngine will not load at start, a standing expectations file that would refuse the run). Once started, BepInEx must
    /// write this launch's first log line within <see cref="ClientRunPlan.BepInExSeconds"/>, or startup fails then, naming the
    /// loader, instead of at the start deadline. A failed startup stops the process it started. The process's output goes to
    /// <c>client-boot.stdout.log</c>/<c>.stderr.log</c>, and its BepInEx log and Unity's Player.log are copied beside them
    /// (<c>client-boot.game-0.log</c>, <c>client-boot.game-1.log</c>) when it stops; <see cref="Logs"/> lists them.
    /// </summary>
    public static ClientSession Launch(ClientRunPlan plan, string output, CancellationToken cancellation = default) => Launch(plan, output, null, cancellation);

    /// <summary>
    /// <see cref="Launch(ClientRunPlan, string, CancellationToken)"/> on the leased Steam account <paramref name="account"/>: refused
    /// before anything starts unless its lease is live (and signed-in checked on this machine's host when the profile asks); losing
    /// the lease later stops the client.
    /// </summary>
    public static ClientSession Launch(ClientRunPlan plan, string output, SteamAccountHold? account, CancellationToken cancellation = default)
    {
        if (!plan.Owned) throw new ArgumentException("This plan's client is attached: its operator launches it.");
        account?.RequireReady(null);
        var start = plan.CheckOwnedInstall(); // Patchers, install pins, loader, plugin builds, ScriptEngine and standing pins, before any port or Steam check.
        var reservation = new TcpListener(IPAddress.Loopback, plan.Port);
        try { reservation.Start(); }
        catch (SocketException error) { throw new InvalidOperationException($"Something already listens on the client's CLI port {plan.Port}; stop it first, this session only drives a client it launched.", error); }
        finally { reservation.Stop(); }
        if (!SteamRunning()) throw new InvalidOperationException("No Steam client is running in this session. An owned client needs Steam running and signed in, in the desktop session this runner runs in.");
        if (plan.DirectStart)
        {
            string request = DirectWorldStart.Write(plan, output);
            start.Environment[DirectWorldStart.FileVariable] = request;
            // A prior client launch may have left this marker in the runner's environment. The new owned process must
            // always consume its own request once; pack reloads inside that process set the marker themselves.
            start.Environment[DirectWorldStart.ClaimedVariable] = "0";
        }
        // The Standard pack still starts with the mode off. This marker only permits an owned test to opt in
        // after the character joins; an operator's attached client never receives it.
        if (plan.FastTestTeleports) start.Environment["VALHEIMCLI_TEST_FAST_TELEPORT"] = "1";
        string log = Path.Combine(plan.Install, "BepInEx", "LogOutput.log");
        var platform = ClientLaunch.Detect(plan.Install);
        string playerLog = PlayerLog(platform);
        string prefix = Path.Combine(output, "client-boot");
        LogWait? cliLog = null;
        try
        {
            var session = Launch(plan, output,
                () =>
                {
                    cliLog = new LogWait(log); // Opened before the launch: an earlier run's lines never count.
                    return new DirectServerProcess(start, prefix, log, playerLog) { Quit = QuitRequest.CloseWindow };
                },
                () => new CliTransport(plan.Host, plan.Port),
                async (left, token) =>
                {
                    var clock = Stopwatch.StartNew();
                    var bepInEx = TimeSpan.FromSeconds(plan.BepInExSeconds);
                    await StartupEvents.WaitForBepInExLog(cliLog!, bepInEx < left ? bepInEx : left, playerLog, token).ConfigureAwait(false);
                    await cliLog!.WaitAsync(StartupEvents.CliListening, left - clock.Elapsed, StartupEvents.StartupFailures, token).ConfigureAwait(false);
                    using var states = StateWait.Connect(plan.Host, plan.Port);
                    await states.WaitAsync(plan.DirectStart ? [StateWait.InWorld] : [StateWait.MainMenu], left - clock.Elapsed, cancellation: token).ConfigureAwait(false);
                }, cancellation, () => cliLog != null && !cliLog.HasOutput() ? StartupEvents.NoBepInExLog(log, playerLog) : null,
                [new RunLog("client BepInEx log", prefix + ".game-0.log", Required: true), new RunLog("client Player.log", prefix + ".game-1.log")], account);
            if (plan.DirectStart)
            {
                try
                {
                    string worldUid = plan.HostWorld?.WorldUid ?? plan.DirectStartWorldUid;
                    // The scenario decides whether the player should be protected. A client-only probe may need to
                    // observe combat or aggro, and ClientRounds/HostRounds protect once when their policy asks for it.
                    new SessionControl(session.Actor).WaitForWorld(worldUid, TimeSpan.FromSeconds(plan.JoinSeconds), cancellation, protectPlayer: false);
                    session.Actor.VerifyEnvironment(plan.WorldExpectations(worldUid));
                    CliCapabilities.RequireDirectStartClaimed(session.Actor);
                }
                catch { session.Dispose(); throw; }
            }
            return session;
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
        Func<TimeSpan, CancellationToken, Task> ready, CancellationToken cancellation, Func<string?>? exitHint, IReadOnlyList<RunLog>? logs, SteamAccountHold? account = null)
    {
        if (plan.PasswordVariable is { } variable && Environment.GetEnvironmentVariable(variable) == null)
            throw new InvalidOperationException($"Set {variable} in this runner's environment; the launched client inherits it for the join.");
        account?.RequireReady(null);
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
                // An exit counts even when readiness completed too: a client that is gone is not ready.
                if (exited.IsCompletedSuccessfully)
                    throw new WaitFailedException(plan.DirectStart ? "client in its world" : "client at its main menu", "the owned client exited with code " + exited.Result + exitHint?.Invoke(), clock.Elapsed, null);
                cancellation.ThrowIfCancellationRequested();
                if (first != waiting) throw new WaitTimeoutException(plan.DirectStart ? "client in its world within the start deadline" : "client at its main menu within the start deadline", clock.Elapsed, null);
                waiting.GetAwaiter().GetResult(); // A readiness failure (a plugin-load error, a closed state connection) ends startup.
            }
            var transport = connect();
            try { actor = new GameActor("client", new RecordingTransport(transport, CommandLog(output), plan.Pinned ? null : EnvironmentPinning.NotPinned)); }
            catch { transport.Dispose(); throw; }
            actor.VerifyEnvironment(plan.DirectStart ? plan.WorldExpectations(plan.HostWorld?.WorldUid ?? plan.DirectStartWorldUid) : plan.MenuExpectations);
            if (plan.Capabilities.Length != 0 || plan.DirectStart)
                CliCapabilities.Require(actor, plan.Capabilities.Concat(plan.DirectStart ? [CliCapabilities.DirectStart] : [])); // Live, after any static manifest check.
            // A lease lost during startup: this client must not run on the account.
            account?.ThrowIfLost();
            return new ClientSession(actor, process, logs, architecture).Using(account);
        }
        catch (Exception error)
        {
            // Stopping keeps the logs beside the evidence; the caller lists them for the scan even though no session opened.
            if (logs is { Count: > 0 }) error.Data[KeptLogsKey] = logs;
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
        lock (_stopping)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _accountLost.Dispose(); // Waits for a loss's stop that is running.
        try { if (!_detachedForAccount) Actor.Dispose(); }
        finally
        {
            if (_process != null)
                try { if (!_stoppedForAccount) Stopped = _process.StopCleanly(QuitTimeout, TimeSpan.FromSeconds(15)); }
                finally { _process.Dispose(); }
        }
    }

    private ClientSession Using(SteamAccountHold? account)
    {
        if (account == null) return this;
        Account = account;
        _accountLost = account.Lost.Register(StopForLostAccount);
        return this;
    }

    // The account's lease is gone: another run may take the account, so an owned client is killed now rather than asked to quit; an
    // attached one is only disconnected. Dispose then keeps what this did.
    private void StopForLostAccount()
    {
        lock (_stopping)
        {
            if (_disposed) return;
            try { Actor.Dispose(); } catch (Exception) { } // The scenario's next command fails on the closed connection.
            _detachedForAccount = true;
            if (_process == null) return;
            var clock = Stopwatch.StartNew();
            bool exited = _process.HasExited;
            _process.Stop(TimeSpan.FromSeconds(15));
            Stopped = new ProcessStop(exited ? StopOutcome.AlreadyExited : StopOutcome.Killed, null, clock.Elapsed, "killed: its Steam account lease was lost");
            _stoppedForAccount = true;
        }
    }
}
