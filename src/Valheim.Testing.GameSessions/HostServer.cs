using System.Globalization;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// Starts an owned dedicated server on a Linux/bash, macOS/bash or Windows/pwsh host and returns it identified by
/// process ID and start time, the identity an owned session checks against its adapter's reported process ID. Linux uses
/// a <c>setsid</c> recorder, macOS a detached supervisor, and Windows a short-lived headless task independent of SSH.
/// Each keeps the server's stdout and stderr in the boot directory.
/// </summary>
public static class HostServer
{
    /// <summary>
    /// On a Windows host: how a server task would log on there (<c>s4u</c>, an elevated token's session-0 task, or
    /// <c>interactive</c>, the user's one desktop session), or a refusal naming why neither can be registered, before anything
    /// is copied. Other hosts need no task and return null.
    /// </summary>
    internal static async Task<string?> RequireTaskLogonAsync(IGameHost host, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (host.Shell.Kind != HostShellKind.PowerShell) return null;
        var result = (await host.RunAsync(HostServerScripts.WindowsServerLogonCheck, new Dictionary<string, string>(), timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Checking how a server task logs on at {host.Name}");
        string verdict = InteractiveClient.Line(result.Stdout, "VT-LOGON ") ?? throw new HostOperationException($"Unexpected reply while checking the server task's logon on {host.Name}", result);
        if (verdict.StartsWith("unsupported ", StringComparison.Ordinal))
            throw new InvalidOperationException($"A dedicated server cannot be started on {host.Name}: {verdict["unsupported ".Length..]}.");
        if (verdict is not ("s4u" or "interactive")) throw new HostOperationException($"Unexpected reply while checking the server task's logon on {host.Name}", result);
        return verdict;
    }

    /// <summary>
    /// Starts <paramref name="launch"/> on <paramref name="host"/>. <paramref name="bootDirectory"/> is a new absolute directory on
    /// the host for this boot's evidence. <paramref name="logs"/> are the game's logs inside the runtime (for example
    /// <c>BepInEx/LogOutput.log</c>): a copy of any of them left by an earlier boot or by the install moves into the boot directory
    /// first (<c>previous-N.log</c>), so a wait on the log from offset 0 sees this boot's lines only, and they move there again
    /// (<c>game-N.log</c>) when the server stops. With <paramref name="evidence"/>, a new local directory, stopping then fetches the
    /// boot directory there. Returns once the server process exists, not when it is ready. A reply lost past
    /// <paramref name="timeout"/> is an unknown outcome: a server may be running, and the exception names its boot directory.
    /// </summary>
    internal static readonly TimeSpan WindowsHandoffStartLimit = TimeSpan.FromMinutes(3);

    public static Task<HostServerProcess> StartAsync(IGameHost host, GameLaunch launch, string bootDirectory, TimeSpan timeout,
        IReadOnlyList<string>? logs = null, string? evidence = null, CancellationToken cancellation = default)
        => StartAsync(host, launch, bootDirectory, timeout, logs, evidence, logonSeams: null, cancellation);

    // logonSeams: the test seams of Get-VtServerLogon (elevated, session, desktops), so a test forces the interactive-token task.
    internal static async Task<HostServerProcess> StartAsync(IGameHost host, GameLaunch launch, string bootDirectory, TimeSpan timeout,
        IReadOnlyList<string>? logs, string? evidence, IReadOnlyDictionary<string, string>? logonSeams, CancellationToken cancellation,
        HostServerLaunchOwnership? ownership = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(launch);
        if (timeout < TimeSpan.FromSeconds(15)) throw new ArgumentOutOfRangeException(nameof(timeout), "Allow a server start at least 15 s.");
        // The Windows launcher keeps its game for 4 minutes awaiting the controller's handoff acknowledgement; a longer start
        // would let it stop a game the controller still accepts.
        if (launch.Platform == ClientPlatform.Windows && timeout > WindowsHandoffStartLimit)
            throw new ArgumentOutOfRangeException(nameof(timeout), "A Windows server start waits at most 3 minutes (its launcher awaits the handoff for 4).");
        if (!launch.IsServer || !launch.ForHost)
            throw new ArgumentException("A host starts a dedicated-server launch built for it: GameLaunch.ForServer with the host's platform.", nameof(launch));
        bool windows = launch.Platform == ClientPlatform.Windows;
        if ((host.Shell.Kind == HostShellKind.PowerShell) != windows)
            throw new PlatformNotSupportedException($"The dedicated server launch and {host.Name}'s shell must use the same operating system.");
        HostInstall.RequireHostPath(host, bootDirectory, nameof(bootDirectory));
        string directory = bootDirectory.TrimEnd('/', '\\');
        if (directory.Length == 0) throw new ArgumentException("The boot directory cannot be a root.", nameof(bootDirectory));
        List<string> kept = logs?.ToList() ?? [];
        foreach (string log in kept)
            if (string.IsNullOrWhiteSpace(log) || log.Any(char.IsControl) || log.StartsWith('/') || log.StartsWith('\\') || log.Contains(':') || log.Split('/', '\\').Contains(".."))
                throw new ArgumentException($"'{log}' is not a path inside the runtime.", nameof(logs));
        if (evidence != null && (Directory.Exists(evidence) || File.Exists(evidence))) throw new ArgumentException("The evidence directory must be new: " + evidence, nameof(evidence));

        ownership ??= HostServerLaunchOwnership.Create(launch.Platform);
        if (!HostServerLaunchOwnership.ValidFileName(ownership.FileName) ||
            (windows ? !HostServerLaunchOwnership.ValidTask(ownership.TaskName) : ownership.TaskName.Length != 0))
            throw new ArgumentException("The launcher ownership identity is malformed.", nameof(ownership));
        var variables = new Dictionary<string, string>
        {
            ["runtime"] = launch.GameDirectory, ["logroot"] = launch.WorkingDirectory,
            ["workdir"] = launch.WorkingDirectory,
            ["exe"] = launch.Platform switch { ClientPlatform.Windows => GameLaunch.ServerWindowsExecutable,
                ClientPlatform.MacOS => GameLaunch.ServerMacExecutable, _ => GameLaunch.ServerLinuxExecutable },
            ["files"] = string.Join('\n', launch.RequiredFiles), ["dir"] = directory,
            ["spec"] = launch.Spec(), ["logs"] = string.Join('\n', kept),
            ["crossplay"] = launch.Crossplay ? "1" : "", ["libraries"] = string.Join('\n', CrossplayLibraries.PartyLibraries),
            ["seconds"] = Math.Max(5, (int)Math.Floor(timeout.TotalSeconds) - 10).ToString(CultureInfo.InvariantCulture),
            ["task"] = ownership.TaskName, ["owner"] = ownership.FileName, ["launcher"] = HostServerScripts.WindowsLauncher,
        };
        foreach (var (name, value) in logonSeams ?? new Dictionary<string, string>())
            variables.Add(name is "elevated" or "session" or "desktops" ? name : throw new ArgumentException("Not a logon seam: " + name, nameof(logonSeams)), value);
        var startScript = launch.Platform switch { ClientPlatform.Windows => HostServerScripts.WindowsStart,
            ClientPlatform.MacOS => HostServerScripts.MacStart, _ => HostServerScripts.Start };
        var result = await host.RunAsync(startScript, variables, timeout, cancellation).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new HostOperationException($"Starting the dedicated server on {host.Name} (boot directory {directory}); a server may have started, see {directory}/pid", result);
        string? verdict = InteractiveClient.Line(result.Stdout, "VT-SERVER ");
        if (verdict == null) throw new HostOperationException($"Unexpected reply while starting the dedicated server on {host.Name}; see {directory}/pid", result);
        int space = verdict.IndexOf(' ');
        string word = space < 0 ? verdict : verdict[..space], detail = space < 0 ? "" : verdict[(space + 1)..];
        if (word == "started")
        {
            var parts = detail.Split(' ');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || parts[1].Length == 0 || !parts[1].All(char.IsAsciiDigit))
                throw new HostOperationException($"Unexpected process identity from {host.Name}; see {directory}/pid", result);
            return new HostServerProcess(host, id, parts[1], directory, launch.GameDirectory, kept, evidence,
                launch.WorkingDirectory, launch.Platform)
                { TaskLogon = InteractiveClient.Line(result.Stdout, "VT-LOGON ") };
        }
        throw word switch
        {
            "unsupported" => (Exception)new PlatformNotSupportedException($"Cannot start the dedicated server on {host.Name}: {detail}. Nothing was started."),
            "missing" => new FileNotFoundException($"The runtime on {host.Name} is incomplete: {detail}. Nothing was started."),
            // Verdict throws the check's own refusal (a missing library, no libparty.so, no ldd).
            "libraries" => CrossplayLibraries.Verdict(host.Name, result.Stdout) == null
                ? new HostOperationException($"Unexpected reply while checking crossplay's libraries on {host.Name}", result)
                : new HostOperationException($"The crossplay library check on {host.Name} passed but the start refused it", result),
            "exists" => new InvalidOperationException($"{directory} already exists on {host.Name}; give each boot a new directory. Nothing was started."),
            "failed" => new InvalidOperationException($"The dedicated server did not start on {host.Name}: {detail}. Evidence is in {directory}."),
            _ => new HostOperationException($"Unexpected reply while starting the dedicated server on {host.Name}", result),
        };
    }
}

/// <summary>What stopping a server on a host found: it was killed, had gone already, or quit by itself when asked (SIGINT).</summary>
[ResultShape]
public enum HostServerStop { Stopped, AlreadyGone, Quit }

/// <summary>
/// A dedicated server <see cref="HostServer"/> started, identified by process ID and start time on its host. It is an
/// <see cref="IOwnedProcess"/>, so an <see cref="OwnedServerSession"/> launches, checks and stops it like a local one. Stopping
/// touches only that process, and only while its start time still matches (a process ID reused by another program is never
/// touched): a clean stop (<see cref="StopCleanly"/>) sends it SIGINT, on which the game saves, retires its PlayFab lobby and
/// quits, and kills it only if it has not exited in time. Then it keeps the boot's logs in its boot directory and, when an
/// evidence directory was given, fetches that directory here. Disposing kills it if it was not stopped yet.
/// </summary>
public sealed class HostServerProcess : IOwnedProcess, IAsyncDisposable
{
    private readonly IGameHost _host;
    private readonly IReadOnlyList<string> _logs;
    private readonly SemaphoreSlim _stopping = new(1, 1);
    private bool _killed, _kept;
    private volatile bool _exited;
    private readonly ClientPlatform _platform;

    /// <summary>On Windows, how the task that started this server logged on: <c>s4u</c> (session 0) or <c>interactive</c> (the user's desktop session).</summary>
    public string? TaskLogon { get; internal init; }

    internal HostServerProcess(IGameHost host, int id, string start, string bootDirectory, string runtime, IReadOnlyList<string> logs, string? evidence,
        string? logRoot = null, ClientPlatform? platform = null)
    {
        _host = host; Id = id; StartIdentity = start; BootDirectory = bootDirectory; Runtime = runtime;
        LogRoot = logRoot ?? runtime; _logs = logs; EvidenceDirectory = evidence;
        _platform = platform ?? (host.Shell.Kind == HostShellKind.PowerShell ? ClientPlatform.Windows : ClientPlatform.Linux);
    }

    public string HostName => _host.Name;
    /// <summary>The server's process ID on its host.</summary>
    public int Id { get; }
    /// <summary>The host-specific start identity, which tells this process from a later process with the same ID.</summary>
    public string StartIdentity { get; }
    /// <summary>This boot's evidence directory on the host.</summary>
    public string BootDirectory { get; }
    public string Runtime { get; }
    internal string LogRoot { get; }
    /// <summary>Where stopping fetches the boot directory, or null.</summary>
    public string? EvidenceDirectory { get; }
    // Published only after the hash-checked fetch completed; a destination directory alone proves nothing.
    internal bool EvidenceFetched { get; private set; }
    /// <summary>How long fetching the evidence may take.</summary>
    public TimeSpan EvidenceTimeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>True once a wait or a stop saw the process gone. Not a live check: use <see cref="WaitForExitAsync"/> for that.</summary>
    public bool HasExited => _exited;

    /// <summary>
    /// Completes when the process has exited, with the exit code the launch's recorder wrote, otherwise -1. Waits on the host in
    /// rounds of at most 10 minutes until cancelled; a round whose reply is lost throws <see cref="HostOperationException"/>.
    /// </summary>
    public async Task<int> WaitForExitAsync(CancellationToken cancellation)
    {
        while (true)
        {
            var waitScript = _platform switch { ClientPlatform.Windows => InteractiveScripts.WindowsWait,
                ClientPlatform.MacOS => MacProcess.Wait, _ => InteractiveScripts.LinuxWait };
            var result = (await _host.RunAsync(waitScript,
                Variables(("seconds", "600")), TimeSpan.FromSeconds(660), cancellation).ConfigureAwait(false))
                .EnsureSuccess($"Waiting for server process {Id} on {HostName}");
            string? verdict = InteractiveClient.Line(result.Stdout, "VT-WAIT ");
            // A process that could not be read (macOS) is not known to have exited: wait on.
            if (verdict is "running" or "unproven") continue;
            if (verdict != null && verdict.StartsWith("exited ", StringComparison.Ordinal))
            {
                _exited = true;
                return int.TryParse(verdict["exited ".Length..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code) ? code : -1;
            }
            throw new HostOperationException($"Unexpected reply while waiting for server process {Id} on {HostName}", result);
        }
    }

    /// <summary>
    /// Kills the process if it is still the one this boot started and waits up to <paramref name="timeout"/> for it to exit, then
    /// keeps the logs and fetches the evidence. Each part is done once; a call after a failure repeats only what is left, which
    /// the identity check makes safe. A process still there after the kill, or an unproven stop or fetch, throws.
    /// </summary>
    public Task<HostServerStop> StopAsync(TimeSpan timeout, CancellationToken cancellation = default) => StopAsync(TimeSpan.Zero, timeout, cancellation);

    /// <summary>
    /// <see cref="StopAsync(TimeSpan, CancellationToken)"/>, but first sends the server SIGINT and gives it <paramref name="quit"/>
    /// to save and exit by itself, which it reports as <see cref="HostServerStop.Quit"/>; only then is it killed.
    /// </summary>
    public async Task<HostServerStop> StopAsync(TimeSpan quit, TimeSpan timeout, CancellationToken cancellation = default)
    {
        WaitText.RequireTimeout(timeout);
        if (quit < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(quit));
        await _stopping.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            var outcome = HostServerStop.AlreadyGone;
            if (!_killed)
            {
                outcome = await StopOnHostAsync(_host, _platform, Id, StartIdentity, BootDirectory, quit, timeout, cancellation).ConfigureAwait(false);
                _killed = true; _exited = true;
            }
            if (!_kept)
            {
                var kept = (await _host.RunAsync(_host.Shell.Kind == HostShellKind.PowerShell ? HostServerScripts.WindowsKeep : HostServerScripts.Keep,
                    new Dictionary<string, string> { ["runtime"] = LogRoot, ["dir"] = BootDirectory, ["logs"] = string.Join('\n', _logs) },
                    TimeSpan.FromSeconds(60), cancellation).ConfigureAwait(false)).EnsureSuccess($"Keeping the logs of server process {Id} on {HostName}");
                if (InteractiveClient.Line(kept.Stdout, "VT-KEPT") == null) throw new HostOperationException($"Unexpected reply while keeping the logs of server process {Id} on {HostName}", kept);
                if (EvidenceDirectory != null)
                {
                    await _host.FetchDirectoryAsync(BootDirectory, EvidenceDirectory, EvidenceTimeout, cancellation).ConfigureAwait(false);
                    EvidenceFetched = true;
                }
                _kept = true;
            }
            return outcome;
        }
        finally { _stopping.Release(); }
    }

    /// <summary>
    /// The one stop of a server process on its host, for this boot and for <see cref="RunRecovery"/>: asks it to quit (SIGINT, or
    /// Ctrl+Break on Windows) and gives it <paramref name="quit"/> (none when zero), then kills it and waits up to
    /// <paramref name="timeout"/>. Only the process with that ID and start identity is touched (on macOS by
    /// <see cref="MacProcess"/>'s one stop). A process still running after the kill, or one that could not be read, throws.
    /// </summary>
    internal static async Task<HostServerStop> StopOnHostAsync(IGameHost host, ClientPlatform platform, int pid, string start, string directory,
        TimeSpan quit, TimeSpan timeout, CancellationToken cancellation)
    {
        WaitText.RequireTimeout(timeout);
        if (quit < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(quit));
        string what = $"Server process {pid} on {host.Name}";
        string? verdict;
        HostResult? result = null;
        if (platform == ClientPlatform.MacOS) verdict = await MacProcess.StopAsync(host, what, pid, start, quit, timeout, null, cancellation).ConfigureAwait(false);
        else
        {
            var variables = new Dictionary<string, string>
            {
                ["game"] = pid.ToString(CultureInfo.InvariantCulture), ["start"] = start, ["dir"] = directory, ["seconds"] = WaitText.WholeSeconds(timeout, minimum: 1),
                ["quit"] = WaitText.WholeSeconds(quit), ["signal"] = "INT", ["quitHelper"] = ProcessQuit.WindowsConsoleControlScript,
            };
            result = (await host.RunAsync(platform == ClientPlatform.Windows ? HostServerScripts.WindowsStop : InteractiveScripts.LinuxStop, variables,
                quit + timeout + TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false)).EnsureSuccess($"Stopping server process {pid} on {host.Name}");
            verdict = InteractiveClient.Line(result.Stdout, "VT-STOP ");
        }
        return verdict switch
        {
            "quit" => HostServerStop.Quit,
            "stopped" => HostServerStop.Stopped,
            "gone" => HostServerStop.AlreadyGone,
            "running" => throw new InvalidOperationException($"{what} was still running {WaitText.Seconds(timeout)} after it was killed."),
            _ => throw new HostOperationException($"Unexpected reply while stopping {what}", result!),
        };
    }

    /// <summary>The <see cref="IOwnedProcess"/> stop: <see cref="StopAsync(TimeSpan, CancellationToken)"/>, waited for.</summary>
    public void Stop(TimeSpan timeout) => StopAsync(timeout).GetAwaiter().GetResult();

    /// <summary>The <see cref="IOwnedProcess"/> clean stop: <see cref="StopAsync(TimeSpan, TimeSpan, CancellationToken)"/>, waited for.</summary>
    public ProcessStop StopCleanly(TimeSpan quit, TimeSpan kill)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        return StopAsync(quit, kill).GetAwaiter().GetResult() switch
        {
            HostServerStop.Quit => new(StopOutcome.Clean, null, clock.Elapsed, _host.Shell.Kind == HostShellKind.PowerShell ? "Ctrl+Break" : "SIGINT"),
            HostServerStop.Stopped => new(StopOutcome.Killed, null, clock.Elapsed, quit > TimeSpan.Zero ? $"{(_host.Shell.Kind == HostShellKind.PowerShell ? "Ctrl+Break" : "SIGINT")}; no exit within {WaitText.Seconds(quit)}" : "not asked to quit"),
            _ => new(StopOutcome.AlreadyExited, null, clock.Elapsed, "not asked: it had exited"),
        };
    }

    /// <summary>Stops the process unless it was stopped already; an unproven stop throws.</summary>
    public void Dispose()
    {
        if (!_killed || !_kept) Stop(TimeSpan.FromSeconds(30));
    }

    public async ValueTask DisposeAsync()
    {
        if (!_killed || !_kept) await StopAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    public override string ToString() => $"server process {Id} (started {StartIdentity}) on {HostName}";

    private Dictionary<string, string> Variables(params (string Name, string Value)[] extra)
    {
        var variables = new Dictionary<string, string> { ["game"] = Id.ToString(CultureInfo.InvariantCulture), ["start"] = StartIdentity, ["dir"] = BootDirectory };
        foreach (var (name, value) in extra) variables[name] = value;
        return variables;
    }
}

// The fixed scripts of a dedicated server on a Linux host. Values arrive as variables (ScriptedGameHost.Compose); each ends
// with one verdict line. The wait and stop are InteractiveScripts' Linux ones (same recorder files, same identity rule).
internal static partial class HostServerScripts
{
    // Variables: game, start, quit, seconds, quitHelper. Identity is checked before either the console event or kill.
    // The Ctrl+Break helper attaches only to the target console. A failed event falls through to the bounded kill.
    public static readonly string WindowsStop = """
        $process = $null
        try { $process = [Diagnostics.Process]::GetProcessById([int]$game) } catch { }
        if ($null -eq $process) { 'VT-STOP gone'; exit 0 }
        try { $identity = [string]$process.StartTime.ToFileTimeUtc() } catch { if ($process.HasExited) { 'VT-STOP gone'; exit 0 }; throw }
        if ($identity -cne $start) { 'VT-STOP gone'; exit 0 }
        if ([int]$quit -gt 0) {
            $env:VT_QUIT_PID = $game
            $env:VT_QUIT_EVENT = '1'
            $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($quitHelper))
            & ([Environment]::ProcessPath) -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded | Out-Null
            if ($LASTEXITCODE -eq 0 -and $process.WaitForExit([int]$quit * 1000)) { 'VT-STOP quit'; exit 0 }
        }
        try { $process.Kill() } catch { if (-not $process.HasExited) { throw } }
        if ($process.WaitForExit([int]$seconds * 1000)) { 'VT-STOP stopped' } else { 'VT-STOP running' }
        """.ReplaceLineEndings("\n");

    // How the server's task logs on, decided on the host (Get-VtServerLogon). An elevated token registers an S4U task, which
    // starts the server in session 0 with no desktop (an SSH session as an administrator); Windows refuses S4U to any other
    // token (E_ACCESSDENIED). A user's ordinary, UAC-filtered token registers an interactive-token task at the limited run
    // level, as the client's task does, which starts the server in the user's desktop session: only when the asking process
    // is itself in that one desktop session, so the stop, run from the same session, reaches the server's console. Otherwise
    // the reply names why. Test seams replace the facts: elevated ('true'/'false'), session (this process's session id) and
    // desktops (how many desktop sessions the user has, this one among them).
    internal const string WindowsServerLogon = InteractiveScripts.WindowsSessions + "\n" + """
        function Get-VtServerLogon {
            $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
            $system = [Environment]::SystemDirectory
            $admin = if ($elevated) { $elevated -eq 'true' } else {
                (New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
            if ($admin) { return 's4u' }
            $here = if ($session) { [int]$session } else { (Get-Process -Id $PID).SessionId }
            if ($here -eq 0) { return 'unsupported ' + $me + ' is not elevated, which a session-0 server task needs, and this is not a desktop session (an SSH session, for example); run from your own desktop session, or over SSH as an administrator' }
            $count = if ($desktops) { [int]$desktops } else { (Get-VtSessions '').Count }
            if ($count -gt 1) { return 'unsupported ' + $me + ' is not elevated and has ' + $count + ' desktop sessions, so a server task could start in another one; sign out of all but this one, or run as an administrator' }
            return 'interactive'
        }
        """;

    // The check preflight runs before anything is copied: which logon the server's task would use on this host, or why none.
    public static readonly string WindowsServerLogonCheck = (WindowsServerLogon + "\n'VT-LOGON ' + (Get-VtServerLogon)\n").ReplaceLineEndings("\n");

    // A task starts the server independent of the session that asked (Get-VtServerLogon decides its logon). The task and
    // launch specification are removed after the child reports its PID, which the start acknowledges (handoff.ack). Before the
    // game starts, the launcher records itself and then the game in the ownership file (owner), which a recovery reads when the
    // pid file never appeared (#800). A start that gives up marks itself abandoned (handoff.abandon), which the launcher checks
    // after its first record and before it spawns the game, then kills the recorded launcher's tree (#814). No Steam client is required.
    // Variables: runtime, logroot, files, dir, spec, logs, seconds, task, owner, launcher.
    public static readonly string WindowsStart = (WindowsServerLogon + "\n" + HostServerLauncher.WindowsStopFunction + "\n" + """
        $utf8 = New-Object Text.UTF8Encoding $false
        foreach ($file in ($files -split "`n")) {
            if ($file -and -not [IO.File]::Exists((Join-Path $runtime $file))) { 'VT-SERVER missing ' + $file; exit 0 }
        }
        if ([IO.Directory]::Exists($dir) -or [IO.File]::Exists($dir)) { 'VT-SERVER exists'; exit 0 }
        $logon = Get-VtServerLogon
        if ($logon -like 'unsupported *') { 'VT-SERVER ' + $logon; exit 0 }
        $logonType = if ($logon -eq 's4u') { 2 } else { 3 } # 2: S4U, no desktop or password; 3: this desktop session's token
        'VT-LOGON ' + $logon
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dir))
        [void][IO.Directory]::CreateDirectory($dir)
        $index = 0
        foreach ($log in ($logs -split "`n")) {
            if ($log) {
                $old = Join-Path $logroot $log
                if ([IO.File]::Exists($old)) { [IO.File]::Move($old, (Join-Path $dir ('previous-' + $index + '.log'))) }
            }
            $index++
        }
        $specFile = Join-Path $dir 'spec.txt'
        $launcherFile = Join-Path $dir 'launcher.ps1'
        $pidFile = Join-Path $dir 'pid'
        $errorFile = Join-Path $dir 'launcher-error.txt'
        $ownerFile = Join-Path $dir $owner
        $taskFile = Join-Path $dir 'launcher-task'
        $ownerNameFile = Join-Path $dir 'launcher-owner-name'
        $ackFile = Join-Path $dir 'handoff.ack'
        $abandonFile = Join-Path $dir 'handoff.abandon'
        $verdict = $null
        try {
            [IO.File]::WriteAllText($specFile, $spec, $utf8)
            [IO.File]::WriteAllText($taskFile, $task, $utf8)
            [IO.File]::WriteAllText($ownerNameFile, $owner, $utf8)
            [IO.File]::WriteAllText($launcherFile, $launcher, (New-Object Text.UTF8Encoding $true))
            $service = New-Object -ComObject Schedule.Service
            $service.Connect()
            $folder = $service.GetFolder('\')
            $definition = $service.NewTask(0)
            $definition.RegistrationInfo.Description = 'ValheimTesting: starts one owned headless dedicated server; removed after launch.'
            $definition.Principal.UserId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
            $definition.Principal.LogonType = $logonType
            $definition.Principal.RunLevel = 0 # limited: an interactive token is the user's filtered one
            $definition.Settings.Hidden = $true
            $definition.Settings.Enabled = $true
            $definition.Settings.AllowDemandStart = $true
            $definition.Settings.DisallowStartIfOnBatteries = $false
            $definition.Settings.StopIfGoingOnBatteries = $false
            $definition.Settings.ExecutionTimeLimit = 'PT5M'
            $action = $definition.Actions.Create(0)
            $action.Path = [Environment]::ProcessPath
            # The launcher finds its directory itself ($PSScriptRoot): the only argument is the quoted script path.
            $action.Arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $launcherFile + '"'
            $action.WorkingDirectory = $dir
            $registered = $folder.RegisterTaskDefinition($task, $definition, 2, $definition.Principal.UserId, $null, $logonType)
            try {
                [void]$registered.Run($null)
                $deadline = [DateTime]::UtcNow.AddSeconds([double]$seconds)
                $watcher = New-Object IO.FileSystemWatcher -ArgumentList $dir
                $running = $false
                $unread = $null
                try {
                    while ($null -eq $verdict) {
                        # Both files are moved into place complete, but another process (an antivirus scan of the new file) can
                        # hold one for a moment: a file that cannot be read yet is read again on the next look.
                        $started = $null; $reported = $null
                        try { if ([IO.File]::Exists($pidFile)) { $started = [IO.File]::ReadAllText($pidFile, $utf8) } } catch { $unread = $_.Exception.Message }
                        if ($null -ne $started) {
                            $handoff = $started.Trim()
                            if ($handoff -notmatch '^\d+ \d+$') { $verdict = 'VT-SERVER failed the launcher wrote a malformed process identity'; break }
                            [IO.File]::WriteAllText($ackFile + '.tmp', 'ack', $utf8)
                            [IO.File]::Move($ackFile + '.tmp', $ackFile)
                            $verdict = 'VT-SERVER started ' + $handoff
                            break
                        }
                        try { if ([IO.File]::Exists($errorFile)) { $reported = [IO.File]::ReadAllText($errorFile, $utf8) } } catch { $unread = $_.Exception.Message }
                        if ($null -ne $reported) { $verdict = 'VT-SERVER failed the launcher reported: ' + ($reported -replace '\s+', ' ').Trim(); break }
                        # The task's own account of itself: a task that ran and ended without the launcher's file never reached
                        # the launcher, or the launcher died before its catch, and the result code says how.
                        $state = $registered.State
                        $result = '0x{0:X8}' -f $registered.LastTaskResult
                        if ($state -eq 4) { $running = $true }
                        elseif (($running -or (Test-VtTaskEnded $state $registered.LastTaskResult)) -and -not [IO.File]::Exists($pidFile) -and -not [IO.File]::Exists($errorFile)) {
                            $verdict = 'VT-SERVER failed the task ended without the launcher starting the server (task result ' + $result + ')'
                            if (-not [IO.File]::Exists((Join-Path $dir 'launcher.started'))) { $verdict += '; the launcher never ran' }
                            break
                        }
                        if ([DateTime]::UtcNow -ge $deadline) {
                            $verdict = 'VT-SERVER failed no process within ' + $seconds + ' s (task state ' + $state + ', result ' + $result + ')'
                            if ($unread) { $verdict += '; the launcher''s file could not be read: ' + ($unread -replace '\s+', ' ').Trim() }
                            break
                        }
                        [void]$watcher.WaitForChanged([IO.WatcherChangeTypes]::All, 500)
                    }
                } finally { $watcher.Dispose() }
            } finally {
                # Until the handoff is acknowledged, this uniquely named task and the launcher's ownership record own what the
                # launch started. On any other verdict (a deadline, an unreadable pid file, a launcher that already exited) kill
                # the launcher's process tree by its recorded identity, stop the task's instance, then the game the launcher
                # recorded, by its exact start time, and prove each gone.
                try {
                    if ($verdict -like 'VT-SERVER started *') {
                        # The launcher ends once it sees the acknowledgement: remove its task after that, as before the handoff wait.
                        $until = [DateTime]::UtcNow.AddSeconds(15)
                        while ($registered.State -eq 4 -and [DateTime]::UtcNow -lt $until) { [Threading.Thread]::Sleep(100) }
                    } else {
                        $left = @()
                        # A launcher that has not yet spawned its game sees this mark before it would, and never does. It records
                        # itself before it looks, so a launcher this read misses cannot start a game (#814).
                        try { [IO.File]::WriteAllText($abandonFile, 'abandoned', $utf8) } catch { $left += 'marking the start abandoned failed: ' + $_.Exception.Message }
                        # The launcher's whole process tree first, while it is the recorded launcher: the task stop below ends only the
                        # launcher, which would leave a game it spawned but has not recorded yet running with no record of it (#814).
                        $words = @()
                        try { if ([IO.File]::Exists($ownerFile)) { $words = @([IO.File]::ReadAllText($ownerFile, $utf8).Trim() -split '\s+') } } catch { $left += 'its ownership record could not be read: ' + $_.Exception.Message }
                        if ($words.Count -in @(5, 7) -and $words[0] -ceq 'windows' -and $words[1] -ceq $task) {
                            try {
                                $stopped = Stop-VtLauncher $words[2] $words[3] $words[4] 30
                                if ($stopped -eq 'running') { $left += 'its launcher ' + $words[2] + ' still runs after it was killed' }
                                elseif ($stopped -in @('mismatch', 'unreadable')) { $left += 'its launcher ' + $words[2] + ' could not be proven the recorded one (' + $stopped + ') and was not stopped' }
                            } catch { $left += 'stopping its launcher ' + $words[2] + ' failed: ' + $_.Exception.Message }
                        }
                        try { if ($registered.State -in @(2, 4)) { $registered.Stop(0) } } catch { $left += 'stopping its task failed: ' + $_.Exception.Message }
                        # Then the game the launcher recorded, read again: a launcher that had already exited left it with no parent.
                        $words = @()
                        try { if ([IO.File]::Exists($ownerFile)) { $words = @([IO.File]::ReadAllText($ownerFile, $utf8).Trim() -split '\s+') } } catch { $left += 'its ownership record could not be read: ' + $_.Exception.Message }
                        if ($words.Count -eq 7 -and $words[0] -ceq 'windows' -and $words[1] -ceq $task) {
                            $ownedGame = $null
                            try { $ownedGame = [Diagnostics.Process]::GetProcessById([int]$words[5]) } catch [ArgumentException] { }
                            try {
                                if ($null -ne $ownedGame -and [string]$ownedGame.StartTime.ToFileTimeUtc() -ceq $words[6]) {
                                    if (-not $ownedGame.HasExited) { $ownedGame.Kill($true) }
                                    if (-not $ownedGame.WaitForExit(30000)) { $left += 'its game process ' + $words[5] + ' still runs after it was killed' }
                                }
                            } catch [InvalidOperationException] { } # it exited before its start time was read
                            catch { $left += 'stopping its game process ' + $words[5] + ' failed: ' + $_.Exception.Message }
                        }
                        foreach ($why in $left) { $verdict = [string]$verdict + '; ' + ($why -replace '\s+', ' ').Trim() }
                    }
                } finally { try { $folder.DeleteTask($task, 0) } catch { } }
            }
        } finally {
            if ([IO.File]::Exists($specFile)) { [IO.File]::Delete($specFile) }
        }
        $verdict
        """).ReplaceLineEndings("\n");

    // Runs in the task's session (pwsh, the same PowerShell 7 that registered the task). Its directory is its own ($PSScriptRoot), never an argument: a path
    // with spaces, as a user's profile has, is then only ever the quoted -File path.
    public static readonly string WindowsLauncher = """
        $ErrorActionPreference = 'Stop'
        Add-Type -TypeDefinition @'
        using System;
        using System.ComponentModel;
        using System.Diagnostics;
        using System.Runtime.InteropServices;
        using System.Text;

        public static class VtLiteralServerProcess
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct SecurityAttributes
            {
                public int Length;
                public IntPtr Descriptor;
                [MarshalAs(UnmanagedType.Bool)] public bool Inherit;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct StartupInfo
            {
                public int Size;
                public string Reserved;
                public string Desktop;
                public string Title;
                public int X, Y, Width, Height, XCountChars, YCountChars, FillAttribute, Flags;
                public short ShowWindow, Reserved2;
                public IntPtr Reserved2Pointer, StandardInput, StandardOutput, StandardError;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct ProcessInformation
            {
                public IntPtr Process, Thread;
                public int ProcessId, ThreadId;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
            private static extern IntPtr CreateFile(string path, uint access, uint share, ref SecurityAttributes attributes,
                uint creation, uint flags, IntPtr template);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes,
                IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
                IntPtr environment, string workingDirectory, ref StartupInfo startup, out ProcessInformation process);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CloseHandle(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern uint ResumeThread(IntPtr thread);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool TerminateProcess(IntPtr process, uint exitCode);

            private static IntPtr Open(string path, uint access, uint creation)
            {
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = true };
                var handle = CreateFile(path, access, 1, ref attributes, creation, 0x80, IntPtr.Zero);
                if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Opening server standard stream " + path);
                return handle;
            }

            public static Process Start(string executable, string arguments, string workingDirectory, string stdout, string stderr)
            {
                IntPtr input = IntPtr.Zero, output = IntPtr.Zero, error = IntPtr.Zero;
                try
                {
                    input = Open("NUL", 0x80000000, 3);
                    output = Open(stdout, 0x40000000, 2);
                    error = Open(stderr, 0x40000000, 2);
                    var startup = new StartupInfo {
                        Size = Marshal.SizeOf<StartupInfo>(), Flags = 0x100 | 0x1, ShowWindow = 0,
                        StandardInput = input, StandardOutput = output, StandardError = error
                    };
                    var command = new StringBuilder("\"" + executable + "\"" + (string.IsNullOrEmpty(arguments) ? "" : " " + arguments));
                    // A suspended child cannot exit and have its PID reused before the managed owner acquires a handle.
                    if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x14,
                            IntPtr.Zero, workingDirectory, ref startup, out var process))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Starting the dedicated server");
                    Process managed = null;
                    try
                    {
                        managed = Process.GetProcessById(process.ProcessId);
                        var ownedHandle = managed.Handle;
                        if (ResumeThread(process.Thread) == uint.MaxValue)
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Resuming the dedicated server");
                        return managed;
                    }
                    catch
                    {
                        TerminateProcess(process.Process, 1);
                        managed?.Dispose();
                        throw;
                    }
                    finally { CloseHandle(process.Thread); CloseHandle(process.Process); }
                }
                finally
                {
                    if (error != IntPtr.Zero) CloseHandle(error);
                    if (output != IntPtr.Zero) CloseHandle(output);
                    if (input != IntPtr.Zero) CloseHandle(input);
                }
            }
        }
        '@
        $dir = $PSScriptRoot
        $utf8 = New-Object Text.UTF8Encoding $false
        $game = $null
        try {
            $self = Get-Process -Id $PID
            $task = [IO.File]::ReadAllText((Join-Path $dir 'launcher-task'), $utf8).Trim()
            $ownerName = [IO.File]::ReadAllText((Join-Path $dir 'launcher-owner-name'), $utf8).Trim()
            if ($ownerName -notmatch '^launcher-[0-9a-f]{32}\.owner$' -or $task -notmatch '^VT-Server-[0-9a-f]{32}$') { throw 'The launcher ownership identity is missing or malformed.' }
            $ownerPath = Join-Path $dir $ownerName
            function Write-VtOwner([Diagnostics.Process]$ownedGame) {
                $text = 'windows ' + $task + ' ' + $PID + ' ' + $self.StartTime.ToFileTimeUtc() + ' ' + $self.SessionId
                if ($null -ne $ownedGame) { $text += ' ' + $ownedGame.Id + ' ' + $ownedGame.StartTime.ToFileTimeUtc() }
                [IO.File]::WriteAllText($ownerPath + '.tmp', $text, $utf8)
                [IO.File]::Move($ownerPath + '.tmp', $ownerPath, $true)
            }
            # Before the game exists, publish the exact task action and launcher process that own any later child.
            Write-VtOwner $null
            [IO.File]::WriteAllText((Join-Path $dir 'launcher.started'), [string]$PID + ' session ' + $self.SessionId, $utf8)
            $start = New-Object Diagnostics.ProcessStartInfo
            foreach ($line in [IO.File]::ReadAllLines((Join-Path $dir 'spec.txt'), $utf8)) {
                if (-not $line) { continue }
                $kind, $value = $line.Split([char[]]@(' '), 2)
                $text = $utf8.GetString([Convert]::FromBase64String($value))
                if ($kind -ceq 'exe') { $start.FileName = $text }
                elseif ($kind -ceq 'dir') { $start.WorkingDirectory = $text }
                elseif ($kind -ceq 'args') { $start.Arguments = $text }
                elseif ($kind -ceq 'env') {
                    $at = $text.IndexOf('=')
                    [Environment]::SetEnvironmentVariable($text.Substring(0, $at), $text.Substring($at + 1), 'Process')
                }
            }
            [IO.File]::Delete((Join-Path $dir 'spec.txt'))
            foreach ($key in @([Environment]::GetEnvironmentVariables('Process').Keys)) {
                if ($key -like 'DOORSTOP_*') { [Environment]::SetEnvironmentVariable($key, $null, 'Process') }
            }
            # After the ownership record above: a controller that gave up either finds that record, and kills this launcher's
            # tree, or marked the start abandoned before it looked, which this sees.
            if ([IO.File]::Exists((Join-Path $dir 'handoff.abandon'))) { throw 'The controller gave up on the start before the server was started.' }
            # Literal CreateProcess paths accept bracketed folder names. Inherited file handles let the short-lived task
            # exit without truncating output or blocking the game when its pipe buffer fills.
            $game = [VtLiteralServerProcess]::Start($start.FileName, $start.Arguments, $start.WorkingDirectory,
                (Join-Path $dir 'stdout.log'), (Join-Path $dir 'stderr.log'))
            # The owner update precedes the ordinary pid publication, so recovery still has an exact game identity in that gap.
            Write-VtOwner $game
            $temporary = Join-Path $dir 'pid.tmp'
            [IO.File]::WriteAllText($temporary, [string]$game.Id + ' ' + $game.StartTime.ToFileTimeUtc(), $utf8)
            [IO.File]::Move($temporary, (Join-Path $dir 'pid'))
            # The task, and this launcher with it, owns the game until the controller acknowledges the pid. A controller that
            # never does (killed, or its reply lost) leaves no game behind: the catch stops it, inside the task's 5-minute limit.
            $deadline = [DateTime]::UtcNow.AddMinutes(4)
            while (-not [IO.File]::Exists((Join-Path $dir 'handoff.ack'))) {
                if ([DateTime]::UtcNow -ge $deadline) { throw 'The controller did not acknowledge the process handoff.' }
                [Threading.Thread]::Sleep(100)
            }
        } catch {
            $launchError = $_.Exception.Message
            if ($null -ne $game) {
                try { $game.Kill($true); $game.WaitForExit() }
                catch { $launchError += '; stopping the process tree also failed: ' + $_.Exception.Message }
            }
            $temporary = Join-Path $dir 'launcher-error.tmp'
            [IO.File]::WriteAllText($temporary, $launchError, $utf8)
            [IO.File]::Move($temporary, (Join-Path $dir 'launcher-error.txt'))
            exit 1
        }
        """.ReplaceLineEndings("\n");

    public static readonly string WindowsKeep = """
        if (-not [IO.Directory]::Exists($dir)) { exit 3 }
        $index = 0
        foreach ($log in ($logs -split "`n")) {
            if ($log) {
                $source = Join-Path $runtime $log
                $target = Join-Path $dir ('game-' + $index + '.log')
                if ([IO.File]::Exists($source)) { [IO.File]::Move($source, $target) }
                elseif (-not [IO.File]::Exists($target)) { [IO.File]::WriteAllText($target + '.absent', 'Game did not create this log: ' + $source) }
            }
            $index++
        }
        'VT-KEPT'
        """.ReplaceLineEndings("\n");

    // A process's start time in clock ticks after boot (field 22 of /proc/PID/stat, counted after the name); empty for a
    // zombie or a missing process.
    private const string Started = """
        started() { local s; s=$(cat "/proc/$1/stat" 2> /dev/null) || return 1; s=${s##*) }; set -- $s; [ "$1" != Z ] && echo "${20}"; }
        """;

    // Variables: runtime, workdir, exe, files, dir, spec, logs, seconds, crossplay, libraries, owner. A crossplay launch first proves the game's
    // libparty.so loads (CrossplayLibraryScripts), and otherwise replies with that check's lines and "libraries". The recorder writes the server's PID and start identity to
    // the boot's pid file (what an interrupted run's recovery reads, #257) and hands the PID back through a FIFO the script already holds open, so the start waits for that event (bounded by seconds) rather than looking for a file; the
    // FIFO is opened read-write at both ends, so neither side can block on a missing partner. The server's own descriptors
    // are only the logs and /dev/null. Nothing here is written to disk but the evidence files: arguments may hold a password.
    public static readonly string Start = ("set -u\n" + Started + "\n" + CrossplayLibraryScripts.Body + "\n" + """
        if [ "$(uname -s)" != Linux ]; then echo "VT-SERVER unsupported this host runs $(uname -s); the Linux dedicated server needs a Linux host"; exit 0; fi
        while IFS= read -r f; do
            if [ -n "$f" ]; then
                case "$f" in /*) needed="$f" ;; *) needed="$runtime/$f" ;; esac
                if [ ! -f "$needed" ]; then echo "VT-SERVER missing $f"; exit 0; fi
            fi
        done <<< "$files"
        if [ ! -x "$runtime/$exe" ]; then echo "VT-SERVER missing $exe is not executable"; exit 0; fi
        if [ -n "$crossplay" ]; then
            party=$(vt_party_check)
            if printf '%s\n' "$party" | grep -q -e ' => not found' || ! printf '%s\n' "$party" | grep -q -e '^VT-PARTY checked .* 0$'; then
                printf '%s\n' "$party"; echo "VT-SERVER libraries"; exit 0
            fi
        fi
        if [ -e "$dir" ]; then echo "VT-SERVER exists"; exit 0; fi
        mkdir -p -- "$(dirname -- "$dir")" && mkdir -- "$dir" || exit 3
        i=0
        while IFS= read -r log; do
            if [ -n "$log" ] && [ -e "$logroot/$log" ]; then mv -f -- "$logroot/$log" "$dir/previous-$i.log" || exit 3; fi
            i=$((i + 1))
        done <<< "$logs"
        decode() { printf '%s' "$1" | base64 -d && printf x; }
        unsets=(); removed=(); sets=(); args=()
        while IFS=' ' read -r kind value; do
            [ -n "$kind" ] || continue
            text=$(decode "$value") || exit 3
            text=${text%x}
            case "$kind" in
                unset) unsets+=(-u "$text"); removed+=("$text") ;;
                env) sets+=("$text") ;;
                prepend) name=${text%%=*}; entry=${text#*=}; current=${!name:-}
                    for dropped in ${removed[@]+"${removed[@]}"}; do
                        if [ "$dropped" = "$name" ]; then current=; fi
                    done
                    for pair in ${sets[@]+"${sets[@]}"}; do
                        case "$pair" in "$name="*) current=${pair#*=} ;; esac
                    done
                    if [ -n "$current" ]; then sets+=("$name=$entry:$current"); else sets+=("$name=$entry"); fi ;;
                arg) args+=("$text") ;;
            esac
        done <<< "$spec"
        # A background job of a non-interactive shell starts with SIGINT ignored, and the clean stop sends SIGINT: env gives the
        # server the default disposition back where it can (coreutils 8.31 and later).
        signals=()
        if env --default-signal=INT true 2> /dev/null; then signals=(--default-signal=INT); fi
        mkfifo -m 600 -- "$dir/started" || exit 3
        exec 3<> "$dir/started"
        cd -- "$workdir" || exit 3
        setsid bash -c 'f=$1; p=$2; x=$3; o=$4; shift 4; self=$$; s=$(cat "/proc/$self/stat" 2> /dev/null) || exit 3; read -r -a me <<< "${s##*) }"; [ "${me[2]:-}" = "$self" ] && [ "${me[3]:-}" = "$self" ] || exit 3; launcherStart=${me[19]:-}; [ -n "$launcherStart" ] || exit 3; printf "unix %s %s %s %s\n" "$self" "$launcherStart" "$self" "$self" > "$o.tmp" && mv -f -- "$o.tmp" "$o" || exit 3; exec 4<> "$f"; "$@" 4>&- & g=$!; s=$(cat "/proc/$g/stat" 2> /dev/null); s=${s##*) }; set -- $s; gameStart=${20:-}; if [ -n "$gameStart" ]; then printf "unix %s %s %s %s %s %s\n" "$self" "$launcherStart" "$self" "$self" "$g" "$gameStart" > "$o.tmp" && mv -f -- "$o.tmp" "$o"; fi; printf "%s %s\n" "$g" "$gameStart" > "$p.tmp" && mv -f -- "$p.tmp" "$p"; printf "%s\n" "$g" >&4; exec 4>&-; wait "$g"; printf "%s\n" "$?" > "$x.tmp" && mv -f -- "$x.tmp" "$x"' \
            vt-server "$dir/started" "$dir/pid" "$dir/exit" "$dir/$owner" env ${signals[@]+"${signals[@]}"} ${unsets[@]+"${unsets[@]}"} ${sets[@]+"${sets[@]}"} "$runtime/$exe" ${args[@]+"${args[@]}"} \
            > "$dir/stdout.log" 2> "$dir/stderr.log" < /dev/null 3<&- &
        launcher=$!
        launcherStart=$(started "$launcher")
        abandon() {
            # The session/process-group leader just created above, signalled only while it is still that start: bash reaps an
            # exited background job, so its PID (and its emptied group) can be reused. Once it has exited, its game has too.
            if [ -n "$launcherStart" ] && [ "$(started "$launcher")" = "$launcherStart" ]; then kill -KILL -- "-$launcher" 2> /dev/null || true; fi
            wait "$launcher" 2> /dev/null || true
        }
        game=
        read -r -t "$seconds" -u 3 game
        exec 3<&-
        rm -f -- "$dir/started"
        if [ -z "$game" ]; then abandon; echo "VT-SERVER failed no server process within $seconds s"; exit 0; fi
        start=$(started "$game")
        if [ -z "$start" ]; then abandon; echo "VT-SERVER failed the server exited at once: $(tail -c 300 "$dir/stderr.log" 2> /dev/null | tr '\n' ' ')"; exit 0; fi
        echo "VT-SERVER started $game $start"
        """).ReplaceLineEndings("\n");

    // Variables: runtime, dir, logs. After the stop: each game log moves into the boot directory as game-N.log, or an .absent
    // note says the game never wrote it.
    public static readonly string Keep = """
        set -u
        [ -d "$dir" ] || exit 3
        i=0
        while IFS= read -r log; do
            if [ -n "$log" ]; then
                if [ -e "$runtime/$log" ]; then mv -f -- "$runtime/$log" "$dir/game-$i.log" || exit 3
                elif [ ! -e "$dir/game-$i.log" ]; then printf 'Game did not create this log: %s\n' "$runtime/$log" > "$dir/game-$i.log.absent" || exit 3; fi
            fi
            i=$((i + 1))
        done <<< "$logs"
        echo "VT-KEPT"
        """.ReplaceLineEndings("\n");
}
