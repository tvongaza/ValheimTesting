using System.Globalization;

namespace Valheim.Testing.Game;

/// <summary>
/// Starts an owned dedicated server on a Linux/bash or Windows/PowerShell host and returns it identified by process ID
/// and start time, the identity an owned session checks against its adapter's reported process ID. Linux uses a
/// <c>setsid</c> recorder and keeps stdout and stderr in the boot directory. Windows uses a short-lived headless task
/// to start the server independently of SSH, then removes that task.
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
    public static Task<HostServerProcess> StartAsync(IGameHost host, GameLaunch launch, string bootDirectory, TimeSpan timeout,
        IReadOnlyList<string>? logs = null, string? evidence = null, CancellationToken cancellation = default)
        => StartAsync(host, launch, bootDirectory, timeout, logs, evidence, logonSeams: null, cancellation);

    // logonSeams: the test seams of Get-VtServerLogon (elevated, session, desktops), so a test forces the interactive-token task.
    internal static async Task<HostServerProcess> StartAsync(IGameHost host, GameLaunch launch, string bootDirectory, TimeSpan timeout,
        IReadOnlyList<string>? logs, string? evidence, IReadOnlyDictionary<string, string>? logonSeams, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(launch);
        if (timeout < TimeSpan.FromSeconds(15)) throw new ArgumentOutOfRangeException(nameof(timeout), "Allow a server start at least 15 s.");
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

        var variables = new Dictionary<string, string>
        {
            ["runtime"] = launch.WorkingDirectory, ["exe"] = windows ? GameLaunch.ServerWindowsExecutable : GameLaunch.ServerLinuxExecutable, ["files"] = string.Join('\n', launch.RequiredFiles), ["dir"] = directory,
            ["spec"] = launch.Spec(), ["logs"] = string.Join('\n', kept),
            ["crossplay"] = launch.Crossplay ? "1" : "", ["libraries"] = string.Join('\n', CrossplayLibraries.PartyLibraries),
            ["seconds"] = Math.Max(5, (int)Math.Floor(timeout.TotalSeconds) - 10).ToString(CultureInfo.InvariantCulture),
            ["task"] = "VT-Server-" + Guid.NewGuid().ToString("N"), ["launcher"] = HostServerScripts.WindowsLauncher,
        };
        foreach (var (name, value) in logonSeams ?? new Dictionary<string, string>())
            variables.Add(name is "elevated" or "session" or "desktops" ? name : throw new ArgumentException("Not a logon seam: " + name, nameof(logonSeams)), value);
        var result = await host.RunAsync(windows ? HostServerScripts.WindowsStart : HostServerScripts.Start, variables, timeout, cancellation).ConfigureAwait(false);
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
            return new HostServerProcess(host, id, parts[1], directory, launch.WorkingDirectory, kept, evidence)
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

    /// <summary>On Windows, how the task that started this server logged on: <c>s4u</c> (session 0) or <c>interactive</c> (the user's desktop session).</summary>
    public string? TaskLogon { get; internal init; }

    internal HostServerProcess(IGameHost host, int id, string start, string bootDirectory, string runtime, IReadOnlyList<string> logs, string? evidence)
    {
        _host = host; Id = id; StartIdentity = start; BootDirectory = bootDirectory; Runtime = runtime; _logs = logs; EvidenceDirectory = evidence;
    }

    public string HostName => _host.Name;
    /// <summary>The server's process ID on its host.</summary>
    public int Id { get; }
    /// <summary>The process's start time in clock ticks after the host's boot, which tells it from a later process with the same ID.</summary>
    public string StartIdentity { get; }
    /// <summary>This boot's evidence directory on the host.</summary>
    public string BootDirectory { get; }
    public string Runtime { get; }
    /// <summary>Where stopping fetches the boot directory, or null.</summary>
    public string? EvidenceDirectory { get; }
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
            var result = (await _host.RunAsync(_host.Shell.Kind == HostShellKind.PowerShell ? InteractiveScripts.WindowsWait : InteractiveScripts.LinuxWait,
                Variables(("seconds", "600")), TimeSpan.FromSeconds(660), cancellation).ConfigureAwait(false))
                .EnsureSuccess($"Waiting for server process {Id} on {HostName}");
            string? verdict = InteractiveClient.Line(result.Stdout, "VT-WAIT ");
            if (verdict == "running") continue;
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
                string seconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                string quitSeconds = ((int)Math.Ceiling(quit.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                var result = (await _host.RunAsync(_host.Shell.Kind == HostShellKind.PowerShell ? HostServerScripts.WindowsStop : InteractiveScripts.LinuxStop,
                    Variables(("seconds", seconds), ("quit", quitSeconds), ("signal", "INT"), ("quitHelper", ProcessQuit.WindowsConsoleControlScript)),
                    quit + timeout + TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false)).EnsureSuccess($"Stopping server process {Id} on {HostName}");
                outcome = InteractiveClient.Line(result.Stdout, "VT-STOP ") switch
                {
                    "quit" => HostServerStop.Quit,
                    "stopped" => HostServerStop.Stopped,
                    "gone" => HostServerStop.AlreadyGone,
                    "running" => throw new InvalidOperationException($"Server process {Id} on {HostName} was still running {WaitText.Seconds(timeout)} after it was killed."),
                    _ => throw new HostOperationException($"Unexpected reply while stopping server process {Id} on {HostName}", result),
                };
                _killed = true; _exited = true;
            }
            if (!_kept)
            {
                var kept = (await _host.RunAsync(_host.Shell.Kind == HostShellKind.PowerShell ? HostServerScripts.WindowsKeep : HostServerScripts.Keep,
                    new Dictionary<string, string> { ["runtime"] = Runtime, ["dir"] = BootDirectory, ["logs"] = string.Join('\n', _logs) },
                    TimeSpan.FromSeconds(60), cancellation).ConfigureAwait(false)).EnsureSuccess($"Keeping the logs of server process {Id} on {HostName}");
                if (InteractiveClient.Line(kept.Stdout, "VT-KEPT") == null) throw new HostOperationException($"Unexpected reply while keeping the logs of server process {Id} on {HostName}", kept);
                if (EvidenceDirectory != null) await _host.FetchDirectoryAsync(BootDirectory, EvidenceDirectory, EvidenceTimeout, cancellation).ConfigureAwait(false);
                _kept = true;
            }
            return outcome;
        }
        finally { _stopping.Release(); }
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
internal static class HostServerScripts
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
            & (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe') -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded | Out-Null
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
    // launch specification are removed after the child reports its PID. No Steam client is required.
    // Variables: runtime, files, dir, spec, logs, seconds, task, launcher.
    public static readonly string WindowsStart = (WindowsServerLogon + "\n" + """
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
                $old = Join-Path $runtime $log
                if ([IO.File]::Exists($old)) { [IO.File]::Move($old, (Join-Path $dir ('previous-' + $index + '.log'))) }
            }
            $index++
        }
        $specFile = Join-Path $dir 'spec.txt'
        $launcherFile = Join-Path $dir 'launcher.ps1'
        $pidFile = Join-Path $dir 'pid'
        $errorFile = Join-Path $dir 'launcher-error.txt'
        $verdict = $null
        try {
            [IO.File]::WriteAllText($specFile, $spec, $utf8)
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
            $action.Path = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
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
                        if ($null -ne $started) { $verdict = 'VT-SERVER started ' + $started.Trim(); break }
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
                try { $folder.DeleteTask($task, 0) } catch { }
            }
        } finally {
            if ([IO.File]::Exists($specFile)) { [IO.File]::Delete($specFile) }
        }
        $verdict
        """).ReplaceLineEndings("\n");

    // Runs in the task's session (Windows PowerShell 5.1). Its directory is its own ($PSScriptRoot), never an argument: a path
    // with spaces, as a user's profile has, is then only ever the quoted -File path.
    public static readonly string WindowsLauncher = """
        $ErrorActionPreference = 'Stop'
        $dir = $PSScriptRoot
        $utf8 = New-Object Text.UTF8Encoding $false
        try {
            # Evidence that the task reached this script, and where: its process and session.
            [IO.File]::WriteAllText((Join-Path $dir 'launcher.started'), [string]$PID + ' session ' + (Get-Process -Id $PID).SessionId, $utf8)
            $start = New-Object Diagnostics.ProcessStartInfo
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $false
            foreach ($line in [IO.File]::ReadAllLines((Join-Path $dir 'spec.txt'), $utf8)) {
                if (-not $line) { continue }
                $kind, $value = $line.Split([char[]]@(' '), 2)
                $text = $utf8.GetString([Convert]::FromBase64String($value))
                if ($kind -ceq 'exe') { $start.FileName = $text }
                elseif ($kind -ceq 'dir') { $start.WorkingDirectory = $text }
                elseif ($kind -ceq 'args') { $start.Arguments = $text }
                elseif ($kind -ceq 'env') { $at = $text.IndexOf('='); $start.Environment[$text.Substring(0, $at)] = $text.Substring($at + 1) }
            }
            [IO.File]::Delete((Join-Path $dir 'spec.txt'))
            foreach ($key in @($start.Environment.Keys)) { if ($key -like 'DOORSTOP_*') { [void]$start.Environment.Remove($key) } }
            $game = [Diagnostics.Process]::Start($start)
            $temporary = Join-Path $dir 'pid.tmp'
            [IO.File]::WriteAllText($temporary, [string]$game.Id + ' ' + $game.StartTime.ToFileTimeUtc(), $utf8)
            [IO.File]::Move($temporary, (Join-Path $dir 'pid'))
        } catch {
            $temporary = Join-Path $dir 'launcher-error.tmp'
            [IO.File]::WriteAllText($temporary, $_.Exception.Message, $utf8)
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

    // Variables: runtime, exe, files, dir, spec, logs, seconds, crossplay, libraries. A crossplay launch first proves the game's
    // libparty.so loads (CrossplayLibraryScripts), and otherwise replies with that check's lines and "libraries". The recorder writes the server's PID and start identity to
    // the boot's pid file (what an interrupted run's recovery reads, #257) and hands the PID back through a FIFO the script already holds open, so the start waits for that event (bounded by seconds) rather than looking for a file; the
    // FIFO is opened read-write at both ends, so neither side can block on a missing partner. The server's own descriptors
    // are only the logs and /dev/null. Nothing here is written to disk but the evidence files: arguments may hold a password.
    public static readonly string Start = ("set -u\n" + Started + "\n" + CrossplayLibraryScripts.Body + "\n" + """
        if [ "$(uname -s)" != Linux ]; then echo "VT-SERVER unsupported this host runs $(uname -s); the Linux dedicated server needs a Linux host"; exit 0; fi
        while IFS= read -r f; do
            if [ -n "$f" ] && [ ! -f "$runtime/$f" ]; then echo "VT-SERVER missing $f"; exit 0; fi
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
            if [ -n "$log" ] && [ -e "$runtime/$log" ]; then mv -f -- "$runtime/$log" "$dir/previous-$i.log" || exit 3; fi
            i=$((i + 1))
        done <<< "$logs"
        decode() { printf '%s' "$1" | base64 -d && printf x; }
        unsets=(); sets=(); args=()
        while IFS=' ' read -r kind value; do
            [ -n "$kind" ] || continue
            text=$(decode "$value") || exit 3
            text=${text%x}
            case "$kind" in
                unset) unsets+=(-u "$text") ;;
                env) sets+=("$text") ;;
                prepend) name=${text%%=*}; entry=${text#*=}; current=${!name:-}
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
        cd -- "$runtime" || exit 3
        setsid bash -c 'f=$1; p=$2; x=$3; shift 3; exec 4<> "$f"; "$@" 4>&- & g=$!; s=$(cat "/proc/$g/stat" 2> /dev/null); s=${s##*) }; set -- $s; printf "%s %s\n" "$g" "${20:-}" > "$p.tmp" && mv -f -- "$p.tmp" "$p"; printf "%s\n" "$g" >&4; exec 4>&-; wait "$g"; printf "%s\n" "$?" > "$x.tmp" && mv -f -- "$x.tmp" "$x"' \
            vt-server "$dir/started" "$dir/pid" "$dir/exit" env ${signals[@]+"${signals[@]}"} ${unsets[@]+"${unsets[@]}"} ${sets[@]+"${sets[@]}"} "$runtime/$exe" ${args[@]+"${args[@]}"} \
            > "$dir/stdout.log" 2> "$dir/stderr.log" < /dev/null 3<&- &
        game=
        read -r -t "$seconds" -u 3 game
        exec 3<&-
        rm -f -- "$dir/started"
        if [ -z "$game" ]; then echo "VT-SERVER failed no server process within $seconds s"; exit 0; fi
        start=$(started "$game")
        if [ -z "$start" ]; then echo "VT-SERVER failed the server exited at once: $(tail -c 300 "$dir/stderr.log" 2> /dev/null | tr '\n' ' ')"; exit 0; fi
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
