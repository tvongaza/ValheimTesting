using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>Why a host refused to start a client in its desktop session.</summary>
[ResultShape]
public enum InteractiveRefusal
{
    /// <summary>The host user has no desktop session there (Windows), or the display does not exist or refuses the user (Linux).</summary>
    NoSession,
    /// <summary>The session has no Steam client running as the host user (on the display, for Linux).</summary>
    NoSteam,
}

/// <summary>A host could not start a client: there is no desktop session for the host user, or no Steam client in it. Nothing was started.</summary>
public sealed class InteractiveSessionException(InteractiveRefusal reason, string hostName, string detail)
    : InvalidOperationException($"Cannot start a game client on {hostName}: {detail}")
{
    public InteractiveRefusal Reason { get; } = reason;
    public string HostName { get; } = hostName;
}

/// <summary>
/// The existing desktop session of a Linux host: an X display (<c>:N</c>, a socket in <c>/tmp/.X11-unix</c>), optionally a Wayland
/// socket in the runtime directory, and the X authority file. The host's user must be the one logged in to it. Valheim draws
/// through X, so a Wayland session needs its XWayland display too.
/// </summary>
public sealed class LinuxDisplay
{
    private static readonly Regex DisplayName = new(@"^:[0-9]{1,4}(\.[0-9]{1,2})?$", RegexOptions.CultureInvariant);
    private static readonly Regex SocketName = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant);

    /// <param name="display">The local X display, <c>:0</c> by default. Remote (TCP) displays are refused.</param>
    /// <param name="waylandDisplay">The Wayland socket's name (for example <c>wayland-0</c>), passed on as WAYLAND_DISPLAY when given.</param>
    /// <param name="runtimeDirectory">XDG_RUNTIME_DIR; <c>/run/user/&lt;uid&gt;</c> is used when it exists and this is null.</param>
    /// <param name="xAuthority">XAUTHORITY; otherwise <c>$XDG_RUNTIME_DIR/gdm/Xauthority</c> or <c>~/.Xauthority</c>, whichever exists.</param>
    public LinuxDisplay(string display = ":0", string? waylandDisplay = null, string? runtimeDirectory = null, string? xAuthority = null)
    {
        if (!DisplayName.IsMatch(display ?? "")) throw new ArgumentException("A display is a local X display such as :0 or :1.0; remote displays are not supported.", nameof(display));
        if (waylandDisplay != null && !SocketName.IsMatch(waylandDisplay)) throw new ArgumentException("A Wayland display is a socket name such as wayland-0.", nameof(waylandDisplay));
        CheckPath(runtimeDirectory, nameof(runtimeDirectory));
        CheckPath(xAuthority, nameof(xAuthority));
        Display = display!; WaylandDisplay = waylandDisplay; RuntimeDirectory = runtimeDirectory; XAuthority = xAuthority;
    }

    public string Display { get; }
    public string? WaylandDisplay { get; }
    public string? RuntimeDirectory { get; }
    public string? XAuthority { get; }

    private static void CheckPath(string? path, string name)
    {
        if (path != null && (!path.StartsWith('/') || path.Any(char.IsControl))) throw new ArgumentException(name + " must be an absolute path on the host.", name);
    }
}

/// <summary>What stopping an interactive client found: it was killed, had gone already, or quit by itself when asked.</summary>
[ResultShape]
public enum InteractiveStop { Stopped, AlreadyGone, Quit }

/// <summary>
/// Starts a game client inside a host's existing desktop session, where its display, GPU and signed-in Steam client are. A
/// process started over SSH or from a service has none of these, so <see cref="ClientSession"/> cannot start a client on another machine.
/// <list type="bullet">
/// <item>Windows (a PowerShell host): a scheduled task registered for the host user with "Run only when user is logged on"
/// (an interactive token, never a stored password), under a unique name for this launch. Starting it runs a small launcher in the
/// user's desktop session, which starts the game and records its process ID and start time. The task is always removed again,
/// whatever happened.</item>
/// <item>Linux (a bash host): the game starts in its own session (setsid) with the <see cref="LinuxDisplay"/>'s DISPLAY,
/// WAYLAND_DISPLAY, XDG_RUNTIME_DIR, XAUTHORITY and session bus. The host user must be the display's user.</item>
/// <item>macOS: not supported from another machine (<see cref="GameLaunch.ForClient"/> refuses it).</item>
/// </list>
/// The host refuses before anything starts when the user has no desktop session there (on Windows, also when it has more than one),
/// when no Steam client runs as the user in it, when a required install file is missing or when the launch directory exists.
/// </summary>
public static class InteractiveClient
{
    internal const string TaskPrefix = "ValheimTesting-client-";
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>
    /// Checks, without creating a task or a launch directory, that this Windows host has exactly one desktop session for its
    /// user and Steam is running in that session. Call this before staging a large disposable install for a one-shot run;
    /// <see cref="StartAsync(IGameHost, GameLaunch, string, TimeSpan, LinuxDisplay, CancellationToken)"/> checks again at launch.
    /// </summary>
    internal static async Task RequireWindowsDesktopAsync(IGameHost host, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.Shell.Kind != HostShellKind.PowerShell)
            throw new ArgumentException("The Windows desktop check needs a PowerShell host.", nameof(host));
        var result = (await host.RunAsync(InteractiveScripts.WindowsDesktopCheck, new Dictionary<string, string>(),
            TimeSpan.FromSeconds(15), cancellation).ConfigureAwait(false)).EnsureSuccess($"Checking the desktop session on {host.Name}");
        string? verdict = Line(result.Stdout, "VT-INTERACTIVE ");
        if (verdict == "ready") return;
        if (verdict?.StartsWith("no-session ", StringComparison.Ordinal) == true)
            throw new InteractiveSessionException(InteractiveRefusal.NoSession, host.Name, verdict["no-session ".Length..]);
        if (verdict?.StartsWith("no-steam ", StringComparison.Ordinal) == true)
            throw new InteractiveSessionException(InteractiveRefusal.NoSteam, host.Name, verdict["no-steam ".Length..]);
        throw new HostOperationException($"Unexpected desktop check reply from {host.Name}", result);
    }

    /// <summary>
    /// Starts <paramref name="launch"/> in <paramref name="host"/>'s desktop session and returns the game's process, identified by
    /// process ID and start time. <paramref name="launchDirectory"/> is a new absolute directory on the host for this launch's evidence:
    /// the recorded process identity (its <c>pid</c> file), and the game's standard output (Linux) or the launcher's error (Windows);
    /// the launch spec is not kept there. Secret variables are read here first; a missing one is refused before anything runs. Their values never enter the
    /// composed script (which the host's wrapper writes to a temporary file): they follow it on standard input, the wrapper keeps
    /// them in memory, and they reach only the game's environment (on Windows through a file in the user's temporary directory
    /// that the launcher deletes as it reads it). They are redacted from every reply. It returns when the game process exists, not
    /// when it is ready: wait for its log line or state through the host and a <see cref="CliTunnel"/>.
    /// </summary>
    /// <param name="timeout">How long the start may take, at least 15 s. A reply lost past it is an unknown outcome: a client (and, on
    /// Windows, its task) may exist, named in the exception.</param>
    public static async Task<InteractiveClientProcess> StartAsync(IGameHost host, GameLaunch launch, string launchDirectory, TimeSpan timeout,
        LinuxDisplay? display = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(launch);
        if (timeout < TimeSpan.FromSeconds(15)) throw new ArgumentOutOfRangeException(nameof(timeout), "Allow a client start at least 15 s.");
        if (launch.IsServer || !launch.ForHost)
            throw new ArgumentException("A host starts a client launch built for it: GameLaunch.ForClient with the host's platform.", nameof(launch));
        bool windows = launch.Platform == ClientPlatform.Windows;
        if (windows != (host.Shell.Kind == HostShellKind.PowerShell))
            throw new ArgumentException(windows ? $"A Windows client starts through a PowerShell host; {host.Name} runs {host.Shell}."
                : $"A Linux client starts through a bash host; {host.Name} runs {host.Shell}.", nameof(host));
        if (windows && display != null) throw new ArgumentException("A display belongs to a Linux client.", nameof(display));
        launchDirectory = CheckDirectory(host, launchDirectory, windows);

        var secretValues = new List<string>();
        var secretTokens = new List<string>();
        foreach (string name in launch.SecretVariables)
        {
            string value = System.Environment.GetEnvironmentVariable(name)
                ?? throw new InvalidOperationException($"Set {name} in this runner's environment (from a secret store); the client on {host.Name} receives it at launch.");
            if (value.Contains('\0')) throw new InvalidOperationException(name + " contains a NUL character.");
            string token = Base64(name + "=" + value);
            secretTokens.Add(token);
            secretValues.Add(value); secretValues.Add(token);
        }
        if (secretTokens.Count > 1) secretValues.Add(string.Join(' ', secretTokens));
        // Secrets must not enter the composed script, which the host's wrapper keeps in a temporary file while it runs.
        if (secretTokens.Count > 0 && host is not ScriptedGameHost)
            throw new NotSupportedException($"Secret variables need a local, SSH or container host; {host.Name} is a {host.GetType().Name}.");

        string seconds = Math.Max(5, (int)Math.Floor(timeout.TotalSeconds) - 10).ToString(CultureInfo.InvariantCulture);
        string? task = windows ? TaskPrefix + Guid.NewGuid().ToString("N") : null;
        var variables = new Dictionary<string, string>
        {
            ["install"] = launch.WorkingDirectory, ["dir"] = launchDirectory, ["spec"] = launch.Spec(), ["seconds"] = seconds,
            ["files"] = string.Join('\n', launch.RequiredFiles),
        };
        if (windows) { variables["task"] = task!; variables["launcher"] = InteractiveScripts.WindowsLauncher; }
        else
        {
            display ??= new LinuxDisplay();
            variables["exe"] = GameLaunch.ClientLinuxExecutable;
            variables["display"] = display.Display; variables["wayland"] = display.WaylandDisplay ?? "";
            variables["runtime"] = display.RuntimeDirectory ?? ""; variables["xauthority"] = display.XAuthority ?? "";
        }
        string script = windows ? InteractiveScripts.WindowsStart : InteractiveScripts.LinuxStart;
        var result = Redact(await (host is ScriptedGameHost scripted
            ? scripted.RunWithSecretsAsync(script, variables, secretTokens, timeout, cancellation)
            : host.RunAsync(script, variables, timeout, cancellation)).ConfigureAwait(false), secretValues);
        return await ReadStartAsync(host, launch.Platform, launchDirectory, task, result).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="StartAsync(IGameHost, GameLaunch, string, TimeSpan, LinuxDisplay, CancellationToken)"/> on the leased Steam
    /// account <paramref name="account"/>: refused before anything runs on the host unless its lease is live, was taken for a client
    /// on this host, and passed the signed-in check when the profile asks for one.
    /// </summary>
    public static Task<InteractiveClientProcess> StartAsync(SteamAccountHold account, IGameHost host, GameLaunch launch, string launchDirectory, TimeSpan timeout,
        LinuxDisplay? display = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(host);
        account.RequireReady(host.Name);
        return StartAsync(host, launch, launchDirectory, timeout, display, cancellation);
    }

    internal static async Task<InteractiveClientProcess> ReadStartAsync(IGameHost host, ClientPlatform platform, string launchDirectory, string? task, HostResult result)
    {
        string taskNote = task == null ? "" : $" The scheduled task {task} may still exist; check with: schtasks /Query /TN {task}";
        if (!result.Succeeded) throw new HostOperationException($"Starting a client in the desktop session of {host.Name}; its launch directory is {launchDirectory}.{taskNote}", result);
        string? verdict = Line(result.Stdout, "VT-INTERACTIVE ");
        string? removal = task == null ? "removed" : Line(result.Stdout, "VT-TASK ");
        if (verdict == null) throw new HostOperationException($"Unexpected reply while starting a client on {host.Name}.{(removal == "removed" ? "" : taskNote)}", result);
        (string word, string detail) = Split(verdict);
        if (word == "started")
        {
            var parts = detail.Split(' ');
            if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || parts[1].Length == 0 || !parts[1].All(char.IsAsciiDigit))
                throw new HostOperationException($"Unexpected process identity from {host.Name}.{(removal == "removed" ? "" : taskNote)}", result);
            var process = new InteractiveClientProcess(host, platform, id, parts[1], launchDirectory, task);
            if (removal != "removed")
            {
                // The task must not outlive the launch; the game it started is stopped rather than left behind.
                try { await process.StopAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
                catch (Exception error) { throw new InvalidOperationException($"The scheduled task {task} on {host.Name} could not be removed, and the client it started (process {id}) could not be stopped: {error.Message} Remove the task by hand: schtasks /Delete /TN {task} /F", error); }
                throw new InvalidOperationException($"The scheduled task {task} on {host.Name} could not be removed; the client it started was stopped. Remove the task by hand: schtasks /Delete /TN {task} /F");
            }
            return process;
        }
        string removed = removal == "removed" || task == null ? "" : taskNote;
        throw word switch
        {
            "no-session" => (Exception)new InteractiveSessionException(InteractiveRefusal.NoSession, host.Name, detail),
            "no-steam" => new InteractiveSessionException(InteractiveRefusal.NoSteam, host.Name, detail),
            "unsupported" => new PlatformNotSupportedException($"Cannot start a {platform} client on {host.Name}: {detail}"),
            "missing" => new FileNotFoundException($"The client install on {host.Name} is incomplete: {detail}. Nothing was started."),
            "exists" => new InvalidOperationException($"{launchDirectory} already exists on {host.Name}; give each launch a new directory. Nothing was started."),
            "failed" => new InvalidOperationException($"The client did not start on {host.Name}: {detail}. Evidence is in {launchDirectory}.{removed}"),
            _ => new HostOperationException($"Unexpected reply while starting a client on {host.Name}.{removed}", result),
        };
    }

    internal static string Base64(string value) => Convert.ToBase64String(Utf8.GetBytes(value));

    /// <summary>The rest of the first stdout line that starts with <paramref name="prefix"/>.</summary>
    internal static string? Line(string stdout, string prefix)
    {
        string? line = stdout.Split('\n').FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));
        return line?.Substring(prefix.Length).TrimEnd();
    }

    private static (string Word, string Detail) Split(string verdict)
    {
        int space = verdict.IndexOf(' ');
        return space < 0 ? (verdict, "") : (verdict[..space], verdict[(space + 1)..]);
    }

    /// <summary>Replaces every secret (longest first) in the reply's text, so no exception, report or log can carry one.</summary>
    internal static HostResult Redact(HostResult result, IReadOnlyCollection<string> secrets) =>
        secrets.Count == 0 ? result : result with { Stdout = Redact(result.Stdout, secrets), Stderr = Redact(result.Stderr, secrets) };
    internal static string Redact(string text, IEnumerable<string> secrets)
    {
        foreach (string secret in secrets.Where(secret => secret.Length > 0).OrderByDescending(secret => secret.Length))
            text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return text;
    }

    private static string CheckDirectory(IGameHost host, string directory, bool windows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (directory.Any(char.IsControl) || !ScriptedGameHost.IsAbsolute(host.Shell.Kind, directory) || (windows && directory.Contains('"')))
            throw new ArgumentException($"The launch directory must be an absolute path on {host.Name}.", nameof(directory));
        string trimmed = directory.TrimEnd('/', '\\');
        if (trimmed.Length == 0 || (windows && trimmed.EndsWith(':'))) throw new ArgumentException("The launch directory cannot be a root.", nameof(directory));
        return trimmed;
    }
}

/// <summary>
/// A game client <see cref="InteractiveClient"/> started, identified by process ID and start time on its host: the identity an
/// owned session checks against its adapter's reported process ID. It implements <see cref="IOwnedProcess"/>, so it can be given
/// to the <see cref="ClientSession"/> launch that takes its process, connection and readiness as functions.
/// Stopping it stops only that process, and only while its start time still matches: a process ID reused by another program
/// is never touched. Disposing stops it if it was not stopped yet.
/// </summary>
public sealed class InteractiveClientProcess : IOwnedProcess, IAsyncDisposable
{
    private readonly IGameHost _host;
    private readonly ClientPlatform _platform;
    private int _stopped;
    private volatile bool _exited;

    internal InteractiveClientProcess(IGameHost host, ClientPlatform platform, int id, string start, string launchDirectory, string? taskName)
    {
        _host = host; _platform = platform; Id = id; StartIdentity = start; LaunchDirectory = launchDirectory; TaskName = taskName;
    }

    public string HostName => _host.Name;
    /// <summary>The game's process ID on its host.</summary>
    public int Id { get; }
    /// <summary>The process's start time as the host reports it (Windows: UTC file time; Linux: clock ticks after boot), which tells it from a later process with the same ID.</summary>
    public string StartIdentity { get; }
    /// <summary>The launch's evidence directory on the host.</summary>
    public string LaunchDirectory { get; }
    /// <summary>Windows: the scheduled task's name. It was removed before the start returned.</summary>
    public string? TaskName { get; }
    /// <summary>True once a wait or a stop saw the process gone. Not a live check: use <see cref="WaitForExitAsync"/> for that.</summary>
    public bool HasExited => _exited;

    /// <summary>
    /// Completes when the process has exited, with its exit code where the host can tell it (the launch's recorder on Linux, the
    /// process handle on Windows), otherwise -1. Waits on the host in rounds of at most 10 minutes until cancelled; a round whose
    /// reply is lost throws <see cref="HostOperationException"/>.
    /// </summary>
    public async Task<int> WaitForExitAsync(CancellationToken cancellation)
    {
        while (true)
        {
            var result = (await _host.RunAsync(_platform == ClientPlatform.Windows ? InteractiveScripts.WindowsWait : InteractiveScripts.LinuxWait,
                Variables(("seconds", "600")), TimeSpan.FromSeconds(660), cancellation).ConfigureAwait(false)).EnsureSuccess($"Waiting for client process {Id} on {HostName}");
            string? verdict = InteractiveClient.Line(result.Stdout, "VT-WAIT ");
            if (verdict == "running") continue;
            if (verdict != null && verdict.StartsWith("exited ", StringComparison.Ordinal))
            {
                _exited = true;
                return int.TryParse(verdict["exited ".Length..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code) ? code : -1;
            }
            throw new HostOperationException($"Unexpected reply while waiting for client process {Id} on {HostName}", result);
        }
    }

    /// <summary>
    /// Kills the process if it is still the one this launch started and waits up to <paramref name="timeout"/> for it to exit.
    /// Only the first successful call acts. A process that is still there afterwards, or an unproven stop, throws; a later call
    /// may try again, because the identity check makes it safe to repeat.
    /// </summary>
    public Task<InteractiveStop> StopAsync(TimeSpan timeout, CancellationToken cancellation = default) => StopAsync(TimeSpan.Zero, timeout, cancellation);

    /// <summary>
    /// <see cref="StopAsync(TimeSpan, CancellationToken)"/>, but first asks the game to quit (Windows: closes its main window;
    /// Linux: SIGTERM) and gives it <paramref name="quit"/> to exit by itself, which it reports as <see cref="InteractiveStop.Quit"/>.
    /// </summary>
    public async Task<InteractiveStop> StopAsync(TimeSpan quit, TimeSpan timeout, CancellationToken cancellation = default)
    {
        WaitText.RequireTimeout(timeout);
        if (quit < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(quit));
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return InteractiveStop.AlreadyGone;
        try
        {
            string seconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            string quitSeconds = ((int)Math.Ceiling(quit.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            var result = (await _host.RunAsync(_platform == ClientPlatform.Windows ? InteractiveScripts.WindowsStop : InteractiveScripts.LinuxStop,
                Variables(("seconds", seconds), ("quit", quitSeconds), ("signal", "TERM")), quit + timeout + TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false))
                .EnsureSuccess($"Stopping client process {Id} on {HostName}");
            string? verdict = InteractiveClient.Line(result.Stdout, "VT-STOP ");
            LastQuitRequest = _platform == ClientPlatform.Windows
                ? (InteractiveClient.Line(result.Stdout, "VT-QUIT ") == "no-window" ? "window close: no main window in this session" : "window closed")
                : "SIGTERM";
            InteractiveStop outcome = verdict switch
            {
                "quit" => InteractiveStop.Quit,
                "stopped" => InteractiveStop.Stopped,
                "gone" => InteractiveStop.AlreadyGone,
                "running" => throw new InvalidOperationException($"Client process {Id} on {HostName} was still running {WaitText.Seconds(timeout)} after it was killed."),
                _ => throw new HostOperationException($"Unexpected reply while stopping client process {Id} on {HostName}", result),
            };
            _exited = true;
            return outcome;
        }
        catch
        {
            Volatile.Write(ref _stopped, 0);
            throw;
        }
    }

    /// <summary>The <see cref="IOwnedProcess"/> stop: <see cref="StopAsync(TimeSpan, CancellationToken)"/>, waited for.</summary>
    public void Stop(TimeSpan timeout) => StopAsync(timeout).GetAwaiter().GetResult();

    /// <summary>How the last stop asked the game to quit.</summary>
    public string LastQuitRequest { get; private set; } = "not asked";

    /// <summary>The <see cref="IOwnedProcess"/> clean stop: <see cref="StopAsync(TimeSpan, TimeSpan, CancellationToken)"/>, waited for.</summary>
    public ProcessStop StopCleanly(TimeSpan quit, TimeSpan kill)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var outcome = StopAsync(quit, kill).GetAwaiter().GetResult();
        return outcome switch
        {
            InteractiveStop.Quit => new(StopOutcome.Clean, null, clock.Elapsed, LastQuitRequest),
            InteractiveStop.Stopped => new(StopOutcome.Killed, null, clock.Elapsed, LastQuitRequest + $"; no exit within {WaitText.Seconds(quit)}"),
            _ => new(StopOutcome.AlreadyExited, null, clock.Elapsed, "not asked: it had exited"),
        };
    }

    /// <summary>Stops the process unless it was stopped already; an unproven stop throws.</summary>
    public void Dispose()
    {
        if (Volatile.Read(ref _stopped) == 0) Stop(TimeSpan.FromSeconds(30));
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _stopped) == 0) await StopAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    public override string ToString() => $"client process {Id} (started {StartIdentity}) on {HostName}";

    private Dictionary<string, string> Variables(params (string Name, string Value)[] extra)
    {
        var variables = new Dictionary<string, string> { ["game"] = Id.ToString(CultureInfo.InvariantCulture), ["start"] = StartIdentity, ["dir"] = LaunchDirectory };
        foreach (var (name, value) in extra) variables[name] = value;
        return variables;
    }
}

/// <summary>Windows command-line quoting, as the C runtime and CommandLineToArgvW read it back.</summary>
internal static class WindowsCommandLine
{
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return argument;
        var text = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char ch in argument)
        {
            if (ch == '\\') { backslashes++; continue; }
            // Backslashes are literal unless they precede a quote; then each is doubled and the quote escaped.
            text.Append('\\', ch == '"' ? backslashes * 2 + 1 : backslashes).Append(ch);
            backslashes = 0;
        }
        return text.Append('\\', backslashes * 2).Append('"').ToString();
    }
}

// The fixed scripts of an interactive client start. Values arrive as variables (ScriptedGameHost.Compose); every script ends
// with one verdict line the C# side requires. Line endings are normalised because a checkout may have converted this file to CRLF.
internal static class InteractiveScripts
{
    // The user's desktop sessions (sessions other than 0, where services and SSH run) in which $me runs processes, optionally
    // only those running $image; tasklist reports them without administrator rights. Needs $me and $system. The client's and
    // the server's tasks both count sessions with it. Test-VtTaskEnded: whether a task is Ready (3) again after a run of its own,
    // which a task that ended between two looks shows only by its result: anything but "has not run" (0x41303), "running"
    // (0x41301) or "queued" (0x41325).
    internal const string WindowsSessions = """
        function Test-VtTaskEnded($state, $code) {
            ($state -eq 3) -and (@(0x41303, 0x41301, 0x41325) -notcontains [int64]$code)
        }
        function Get-VtSessions([string]$image) {
            $arguments = @('/FI', ('USERNAME eq ' + $me), '/FI', 'SESSION ne 0', '/FO', 'CSV', '/NH')
            if ($image) { $arguments += @('/FI', ('IMAGENAME eq ' + $image)) }
            $lines = & (Join-Path $system 'tasklist.exe') @arguments
            if ($LASTEXITCODE -ne 0) { throw ('tasklist exited with ' + $LASTEXITCODE) }
            $found = @()
            foreach ($line in @($lines)) { if ($line -match '^"[^"]*","[0-9]+","[^"]*","([0-9]+)"') { $found += [int]$Matches[1] } }
            ,@($found | Sort-Object -Unique)
        }
        """;

    // Shared by the read-only desktop check and the actual launch: the verdict cannot drift between them.
    internal const string WindowsDesktopGuard = """
        $desktops = Get-VtSessions ''
        if ($desktops.Count -eq 0) { 'VT-INTERACTIVE no-session ' + $me + ' has no desktop session here; sign in at the console or over Remote Desktop and leave the session running'; exit 0 }
        if ($desktops.Count -gt 1) { 'VT-INTERACTIVE no-session ' + $me + ' has ' + $desktops.Count + ' desktop sessions (' + ($desktops -join ', ') + ') and a task could start in any of them; sign out of all but one'; exit 0 }
        $steam = Get-VtSessions 'steam.exe'
        if ($steam.Count -eq 0) { 'VT-INTERACTIVE no-steam no Steam client (steam.exe) runs in the desktop session ' + $desktops[0] + ' of ' + $me; exit 0 }
        """;

    // The read-only part of WindowsStart, available before a one-shot copies the game. Start performs the same checks again
    // so a desktop logout or Steam exit between preflight and launch is still refused.
    internal static readonly string WindowsDesktopCheck = ("""
        $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $system = [Environment]::SystemDirectory
""" + "\n" + WindowsSessions + "\n" + WindowsDesktopGuard + "\n" + """
        'VT-INTERACTIVE ready'
        """).ReplaceLineEndings("\n");

    // Variables: install, files, dir, spec, task, launcher, seconds; secrets arrive in VT_SECRETS (base64 NAME=value tokens,
    // space separated), which the wrapper keeps in memory and this script clears at once. Runs as the SSH user (or locally). The user's
    // desktop sessions are the sessions other than 0 (services, and SSH) in which it runs processes; tasklist reports them
    // without administrator rights. The task runs launcher.ps1 in that session with the user's interactive token.
    public static readonly string WindowsStart = ("""
        $secrets = [Environment]::GetEnvironmentVariable('VT_SECRETS')
        [Environment]::SetEnvironmentVariable('VT_SECRETS', $null)
        $utf8 = New-Object Text.UTF8Encoding $false
        $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        foreach ($file in ($files -split "`n")) {
            if ($file -and -not [IO.File]::Exists((Join-Path $install $file))) { 'VT-INTERACTIVE missing ' + $file; exit 0 }
        }
        if ([IO.Directory]::Exists($dir) -or [IO.File]::Exists($dir)) { 'VT-INTERACTIVE exists'; exit 0 }
        $system = [Environment]::SystemDirectory
""" + "\n" + WindowsSessions + "\n" + WindowsDesktopGuard + "\n" + """
        [void][IO.Directory]::CreateDirectory($dir)
        $specFile = Join-Path $dir 'spec.txt'
        [IO.File]::WriteAllText($specFile, $spec, $utf8)
        $launcherFile = Join-Path $dir 'launcher.ps1'
        [IO.File]::WriteAllText($launcherFile, $launcher, (New-Object Text.UTF8Encoding $true))
        $pidFile = Join-Path $dir 'pid'
        $errorFile = Join-Path $dir 'launcher-error.txt'
        $secretFile = $null
        $verdict = $null
        try {
            if ($secrets) {
                # The hand-off to the desktop session: a file in the user's own temporary directory (not the launch directory,
                # which is evidence) that the launcher deletes as it reads it.
                $secretFile = Join-Path ([IO.Path]::GetTempPath()) ('vt-secrets-' + [Guid]::NewGuid().ToString('N'))
                [IO.File]::WriteAllText($secretFile, (($secrets -split ' ') -join "`n") + "`n", $utf8)
                [IO.File]::AppendAllText($specFile, 'secrets ' + [Convert]::ToBase64String($utf8.GetBytes($secretFile)) + "`n", $utf8)
            }
            $service = New-Object -ComObject Schedule.Service
            $service.Connect()
            $folder = $service.GetFolder('\')
            $definition = $service.NewTask(0)
            $definition.RegistrationInfo.Description = 'ValheimTesting: starts one game client in this desktop session; removed once the client has started.'
            $definition.Principal.UserId = $me
            $definition.Principal.LogonType = 3
            $definition.Principal.RunLevel = 0
            $settings = $definition.Settings
            $settings.Enabled = $true
            $settings.AllowDemandStart = $true
            $settings.DisallowStartIfOnBatteries = $false
            $settings.StopIfGoingOnBatteries = $false
            $settings.ExecutionTimeLimit = 'PT5M'
            $settings.MultipleInstances = 2
            $settings.Hidden = $true
            $action = $definition.Actions.Create(0)
            $action.Path = Join-Path $system 'WindowsPowerShell\v1.0\powershell.exe'
            $action.Arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $launcherFile + '"'
            $action.WorkingDirectory = $dir
            # 2: create only (never replace a task); 3: interactive token, so no password is given or stored.
            $registered = $folder.RegisterTaskDefinition($task, $definition, 2, $me, $null, 3)
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
                        if ($null -ne $started) { $verdict = 'VT-INTERACTIVE started ' + $started.Trim(); break }
                        try { if ([IO.File]::Exists($errorFile)) { $reported = [IO.File]::ReadAllText($errorFile, $utf8) } } catch { $unread = $_.Exception.Message }
                        if ($null -ne $reported) { $verdict = 'VT-INTERACTIVE failed the launcher reported: ' + ($reported -replace '\s+', ' ').Trim(); break }
                        $state = $registered.State
                        $result = '0x{0:X8}' -f $registered.LastTaskResult
                        if ($state -eq 4) { $running = $true }
                        elseif (($running -or (Test-VtTaskEnded $state $registered.LastTaskResult)) -and -not [IO.File]::Exists($pidFile) -and -not [IO.File]::Exists($errorFile)) {
                            # The launcher moves its file into place before it ends, so an ended task without one never started the game.
                            $verdict = 'VT-INTERACTIVE failed the launcher ended without starting the game (task result ' + $result + ')'; break
                        }
                        if ([DateTime]::UtcNow -ge $deadline) {
                            $verdict = 'VT-INTERACTIVE failed no game started within ' + $seconds + ' s (task state ' + $state + ', result ' + $result + ')'
                            if ($unread) { $verdict += '; the launcher''s file could not be read: ' + ($unread -replace '\s+', ' ').Trim() }
                            break
                        }
                        [void]$watcher.WaitForChanged([IO.WatcherChangeTypes]::All, 500)
                    }
                } finally { $watcher.Dispose() }
            } finally {
                try { $folder.DeleteTask($task, 0) } catch { }
                $gone = $false
                try { [void]$folder.GetTask($task) } catch { $gone = $true }
                if ($gone) { [Console]::Out.WriteLine('VT-TASK removed') } else { [Console]::Out.WriteLine('VT-TASK kept') }
            }
        } finally {
            if ($secretFile -and [IO.File]::Exists($secretFile)) { [IO.File]::Delete($secretFile) }
            if ([IO.File]::Exists($specFile)) { [IO.File]::Delete($specFile) }
        }
        $verdict
        """).ReplaceLineEndings("\n");

    // Runs in the desktop session (Windows PowerShell 5.1, started by the task). It reads the spec, takes the secrets from their
    // file and deletes it, starts the game and records its ID and start time, or the error (each moved into place, so the file is complete).
    // Its directory is its own ($PSScriptRoot), never an argument: the task's command line carries only the quoted -File path.
    public static readonly string WindowsLauncher = """
        $ErrorActionPreference = 'Stop'
        $dir = $PSScriptRoot
        $utf8 = New-Object Text.UTF8Encoding $false
        try {
            $start = New-Object Diagnostics.ProcessStartInfo
            $start.UseShellExecute = $false
            $secretFile = $null
            foreach ($line in [IO.File]::ReadAllLines((Join-Path $dir 'spec.txt'), $utf8)) {
                if (-not $line) { continue }
                $kind, $value = $line.Split([char[]]@(' '), 2)
                $text = $utf8.GetString([Convert]::FromBase64String($value))
                if ($kind -ceq 'exe') { $start.FileName = $text }
                elseif ($kind -ceq 'dir') { $start.WorkingDirectory = $text }
                elseif ($kind -ceq 'args') { $start.Arguments = $text }
                elseif ($kind -ceq 'unset') { [void]$start.Environment.Remove($text) }
                elseif ($kind -ceq 'env') { $at = $text.IndexOf('='); $start.Environment[$text.Substring(0, $at)] = $text.Substring($at + 1) }
                elseif ($kind -ceq 'secrets') { $secretFile = $text }
            }
            [IO.File]::Delete((Join-Path $dir 'spec.txt'))
            if ($secretFile) {
                $lines = [IO.File]::ReadAllLines($secretFile, $utf8)
                [IO.File]::Delete($secretFile)
                foreach ($line in $lines) {
                    if (-not $line) { continue }
                    $text = $utf8.GetString([Convert]::FromBase64String($line))
                    $at = $text.IndexOf('=')
                    $start.Environment[$text.Substring(0, $at)] = $text.Substring($at + 1)
                }
            }
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

    // Variables: game, start, seconds, quit. The start time must still match, so a reused process ID is never touched. With
    // quit > 0 the game's main window is closed first and it gets quit seconds to exit by itself ("quit"); a window in another
    // session than this script's cannot be closed from here, and the game is then killed ("stopped", after "VT-QUIT no-window").
    public static readonly string WindowsStop = """
        $process = $null
        try { $process = [Diagnostics.Process]::GetProcessById([int]$game) } catch { }
        if ($null -eq $process) { 'VT-STOP gone'; exit 0 }
        try { $identity = [string]$process.StartTime.ToFileTimeUtc() } catch { if ($process.HasExited) { 'VT-STOP gone'; exit 0 }; throw }
        if ($identity -cne $start) { 'VT-STOP gone'; exit 0 }
        if ([int]$quit -gt 0) {
            $asked = $false
            try { $asked = $process.CloseMainWindow() } catch { }
            if (-not $asked) { 'VT-QUIT no-window' }
            elseif ($process.WaitForExit([int]$quit * 1000)) { 'VT-STOP quit'; exit 0 }
        }
        try { $process.Kill() } catch { if (-not $process.HasExited) { throw } }
        if ($process.WaitForExit([int]$seconds * 1000)) { 'VT-STOP stopped' } else { 'VT-STOP running' }
        """.ReplaceLineEndings("\n");

    // Variables: game, start, seconds. Waits on the process handle; its exit code is readable once the handle was opened before the exit.
    public static readonly string WindowsWait = """
        $process = $null
        try { $process = [Diagnostics.Process]::GetProcessById([int]$game) } catch { }
        if ($null -eq $process) { 'VT-WAIT exited ?'; exit 0 }
        $same = $false
        try { $same = [string]$process.StartTime.ToFileTimeUtc() -ceq $start } catch { }
        if (-not $same) { 'VT-WAIT exited ?'; exit 0 }
        if (-not $process.WaitForExit([int]$seconds * 1000)) { 'VT-WAIT running'; exit 0 }
        $code = '?'
        try { $code = [string]$process.ExitCode } catch { }
        'VT-WAIT exited ' + $code
        """.ReplaceLineEndings("\n");

    // A process's start time in clock ticks after boot (field 22 of /proc/PID/stat, counted after the name, which may contain
    // spaces); empty for a zombie or a missing process.
    private const string LinuxStarted = """
        started() { local s; s=$(cat "/proc/$1/stat" 2> /dev/null) || return 1; s=${s##*) }; set -- $s; [ "$1" != Z ] && echo "${20}"; }
        """;

    // Variables: install, files, exe, dir, spec, display, wayland, runtime, xauthority, seconds; secrets arrive in VT_SECRETS
    // (base64 NAME=value tokens, space separated), which the wrapper keeps in memory and this script clears at once. Steam is a process named
    // steam running as this user, on the display when its environment says. The game runs under a small recorder in its own
    // session (setsid), so the SSH session's end does not reach it; the recorder writes the game's PID (env execs the game in
    // place, so it is the game's) and start identity to the pid file, which is all an interrupted run's recovery has to go on
    // (#257), then its exit code.
    public static readonly string LinuxStart = ("set -u\nsecrets=${VT_SECRETS:-}\nunset VT_SECRETS\n" + LinuxStarted + "\n" + """
        if [ "$(uname -s)" != Linux ]; then echo "VT-INTERACTIVE unsupported this host runs $(uname -s); a Linux client needs a Linux host"; exit 0; fi
        while IFS= read -r f; do
            if [ -n "$f" ] && [ ! -f "$install/$f" ]; then echo "VT-INTERACTIVE missing $f"; exit 0; fi
        done <<< "$files"
        if [ ! -x "$install/$exe" ]; then echo "VT-INTERACTIVE missing $exe is not executable"; exit 0; fi
        if [ -e "$dir" ]; then echo "VT-INTERACTIVE exists"; exit 0; fi
        uid=$(id -u) || exit 3
        user=$(id -un 2> /dev/null || echo "uid $uid")
        screen=${display#:}; screen=${screen%%.*}
        socket="/tmp/.X11-unix/X$screen"
        if [ ! -S "$socket" ]; then echo "VT-INTERACTIVE no-session no X server on display $display here ($socket does not exist); start the desktop session first"; exit 0; fi
        if [ -z "$runtime" ] && [ -d "/run/user/$uid" ]; then runtime="/run/user/$uid"; fi
        if [ -n "$wayland" ] && { [ -z "$runtime" ] || [ ! -S "$runtime/$wayland" ]; }; then echo "VT-INTERACTIVE no-session no Wayland socket $wayland in ${runtime:-XDG_RUNTIME_DIR}"; exit 0; fi
        if [ -z "$xauthority" ]; then
            if [ -n "$runtime" ] && [ -f "$runtime/gdm/Xauthority" ]; then xauthority="$runtime/gdm/Xauthority"
            elif [ -n "${HOME:-}" ] && [ -f "$HOME/.Xauthority" ]; then xauthority="$HOME/.Xauthority"; fi
        fi
        if command -v xdpyinfo > /dev/null 2>&1; then
            if ! ( export DISPLAY="$display"; if [ -n "$xauthority" ]; then export XAUTHORITY="$xauthority"; fi; timeout 10 xdpyinfo ) > /dev/null 2>&1; then
                echo "VT-INTERACTIVE no-session display $display refuses $user (xdpyinfo failed; check XAUTHORITY or xhost)"; exit 0
            fi
        fi
        want=${display%.*}
        steam=
        for p in /proc/[0-9]*; do
            [ "$(cat "$p/comm" 2> /dev/null)" = steam ] || continue
            [ "$(stat -c %u "$p" 2> /dev/null)" = "$uid" ] || continue
            d=$(tr '\0' '\n' < "$p/environ" 2> /dev/null | sed -n 's/^DISPLAY=//p' | head -n 1)
            if [ -n "$d" ] && [ "${d%.*}" != "$want" ]; then continue; fi
            steam=${p#/proc/}; break
        done
        if [ -z "$steam" ]; then echo "VT-INTERACTIVE no-steam no Steam client (a process named steam) runs as $user on display $display"; exit 0; fi

        mkdir -p -- "$(dirname -- "$dir")" && mkdir -- "$dir" || exit 3
        decode() { printf '%s' "$1" | base64 -d && printf x; }
        export DISPLAY="$display"
        if [ -n "$wayland" ]; then export WAYLAND_DISPLAY="$wayland"; fi
        if [ -n "$runtime" ]; then export XDG_RUNTIME_DIR="$runtime"; fi
        if [ -n "$xauthority" ]; then export XAUTHORITY="$xauthority"; fi
        if [ -n "$runtime" ] && [ -S "$runtime/bus" ]; then export DBUS_SESSION_BUS_ADDRESS="unix:path=$runtime/bus"; fi
        # The launch's own variables (the loader's among them) reach only the game, through env as it execs the game: LD_PRELOAD
        # must not load Doorstop into the shells in between. They are evidence, not secrets.
        unsets=(); sets=(); args=()
        while IFS=' ' read -r kind value; do
            [ -n "$kind" ] || continue
            text=$(decode "$value") || exit 3
            text=${text%x}
            case "$kind" in
                unset) unsets+=(-u "$text") ;;
                env) sets+=("$text") ;;
                prepend) name=${text%%=*}; entry=${text#*=}; current=${!name:-}
                    for pair in ${sets[@]+"${sets[@]}"}; do case "$pair" in "$name="*) current=${pair#*=} ;; esac; done
                    if [ -n "$current" ]; then sets+=("$name=$entry:$current"); else sets+=("$name=$entry"); fi ;;
                arg) args+=("$text") ;;
            esac
        done <<< "$spec"
        # Secrets are exported here from memory, never written to a file or put on a command line; every process from here to
        # the game inherits them.
        for value in $secrets; do
            text=$(decode "$value") || exit 3
            export "${text%x}"
        done
        cd -- "$install" || exit 3
        setsid bash -c 'p=$1; x=$2; shift 2; "$@" & g=$!; s=$(cat "/proc/$g/stat" 2> /dev/null); s=${s##*) }; set -- $s; printf "%s %s\n" "$g" "${20:-}" > "$p.tmp" && mv -f -- "$p.tmp" "$p"; wait "$g"; printf "%s\n" "$?" > "$x.tmp" && mv -f -- "$x.tmp" "$x"' \
            vt-client "$dir/pid" "$dir/exit" env ${unsets[@]+"${unsets[@]}"} ${sets[@]+"${sets[@]}"} "$install/$exe" ${args[@]+"${args[@]}"} \
            > "$dir/game.stdout.log" 2> "$dir/game.stderr.log" < /dev/null &
        i=0
        while [ ! -s "$dir/pid" ] && [ "$i" -lt $((seconds * 10)) ]; do sleep 0.1; i=$((i + 1)); done
        if [ ! -s "$dir/pid" ]; then echo "VT-INTERACTIVE failed no game process within $seconds s"; exit 0; fi
        read -r game _ < "$dir/pid"
        start=$(started "$game")
        if [ -z "$start" ]; then echo "VT-INTERACTIVE failed the game exited at once: $(tail -c 300 "$dir/game.stderr.log" 2> /dev/null | tr '\n' ' ')"; exit 0; fi
        echo "VT-INTERACTIVE started $game $start"
        """).ReplaceLineEndings("\n");

    // Variables: game, start, seconds, quit, signal. With quit > 0 the game is first sent the signal (INT or TERM) and given
    // quit seconds to exit by itself ("quit"); only then, or with quit 0, is it killed ("stopped").
    public static readonly string LinuxStop = ("set -u\n" + LinuxStarted + "\n" + """
        if [ "$(started "$game")" != "$start" ]; then echo "VT-STOP gone"; exit 0; fi
        if [ "$quit" -gt 0 ] && kill -s "$signal" "$game" 2> /dev/null; then
            i=0
            while [ "$(started "$game")" = "$start" ] && [ "$i" -lt $((quit * 10)) ]; do sleep 0.1; i=$((i + 1)); done
            if [ "$(started "$game")" != "$start" ]; then echo "VT-STOP quit"; exit 0; fi
        fi
        kill -KILL "$game" 2> /dev/null
        i=0
        while [ "$(started "$game")" = "$start" ]; do
            if [ "$i" -ge $((seconds * 10)) ]; then echo "VT-STOP running"; exit 0; fi
            sleep 0.1; i=$((i + 1))
        done
        echo "VT-STOP stopped"
        """).ReplaceLineEndings("\n");

    // Variables: game, start, seconds, dir. The game is not this script's child, so its exit is looked for once a second; the
    // launch's recorder writes the exit code a moment after the exit.
    public static readonly string LinuxWait = ("set -u\n" + LinuxStarted + "\n" + """
        i=0
        while [ "$(started "$game")" = "$start" ]; do
            if [ "$i" -ge "$seconds" ]; then echo "VT-WAIT running"; exit 0; fi
            sleep 1; i=$((i + 1))
        done
        for _ in 1 2 3 4 5 6 7 8 9 10; do if [ -s "$dir/exit" ]; then break; fi; sleep 0.2; done
        if [ -s "$dir/exit" ]; then echo "VT-WAIT exited $(cat -- "$dir/exit")"; else echo "VT-WAIT exited ?"; fi
        """).ReplaceLineEndings("\n");
}
