using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// One BepInEx dedicated-server launch for a Linux runtime on a host, as data: what <see cref="ServerLaunch"/> builds for a
/// local runtime. <c>SteamAppId</c> is the dedicated server's unless the caller sets it; Doorstop is enabled for BepInEx's
/// preloader, the runtime's <c>linux64</c> and <c>doorstop_libs</c> are put in front of LD_LIBRARY_PATH and
/// <c>libdoorstop_x64.so</c> in front of LD_PRELOAD (for the server only, never the shells that start it), and inherited
/// Doorstop variables are removed. Doorstop variables and <c>--doorstop-*</c> arguments from the caller are refused. The
/// runtime's files are checked on the host when the server starts (<see cref="RequiredFiles"/>).
/// </summary>
public sealed class HostServerLaunch
{
    private static readonly Regex VariableName = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    private HostServerLaunch(string runtime, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, IReadOnlyDictionary<string, string> prepended)
    {
        Runtime = runtime; Arguments = arguments; Environment = environment; Prepended = prepended;
    }

    /// <summary>The runtime directory on the host; also the server's working directory.</summary>
    public string Runtime { get; }
    /// <summary>The server executable's full path on the host.</summary>
    public string Executable => Runtime + "/" + ServerLaunch.LinuxExecutable;
    public IReadOnlyList<string> Arguments { get; }
    /// <summary>Variables set for the server, the caller's first.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }
    /// <summary>Entries put in front of the host session's own value (<c>LD_LIBRARY_PATH</c>, <c>LD_PRELOAD</c>).</summary>
    public IReadOnlyDictionary<string, string> Prepended { get; }
    /// <summary>Inherited variables removed before the launch: Doorstop's, apart from the ones set here.</summary>
    public IReadOnlyList<string> Unset { get; } = ["DOORSTOP_DISABLE"];
    /// <summary>Files, relative to <see cref="Runtime"/>, the host must have before anything starts.</summary>
    public IReadOnlyList<string> RequiredFiles { get; } =
        [ServerLaunch.LinuxExecutable, "BepInEx/core/BepInEx.Preloader.dll", "BepInEx/core/BepInEx.dll", "doorstop_libs/libdoorstop_x64.so"];

    /// <summary>
    /// A launch of the Linux dedicated server in <paramref name="runtime"/>, an absolute path on a Linux host (without ':', ';'
    /// or '=', which the loader's search lists and <c>env</c> cannot hold). A Windows runtime is refused: on a Windows host,
    /// run the runner there (<see cref="ServerLaunch"/>).
    /// </summary>
    public static HostServerLaunch Create(string runtime, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtime);
        if (Regex.IsMatch(runtime, @"^([A-Za-z]:|\\\\)"))
            throw new PlatformNotSupportedException("A dedicated server on another host runs on Linux (over SSH, in a container or on this Linux machine). " +
                "For a Windows server, run the runner on that Windows machine, which launches it with ServerLaunch.");
        if (!runtime.StartsWith('/') || runtime.Any(char.IsControl)) throw new ArgumentException("The runtime must be an absolute Linux path on the host.", nameof(runtime));
        if (runtime.IndexOfAny([':', ';', '=']) >= 0) throw new ArgumentException("A Linux runtime path cannot contain ':', ';' or '='.", nameof(runtime));
        string root = runtime.Length > 1 ? runtime.TrimEnd('/') : runtime;
        environment ??= new Dictionary<string, string>();
        var passed = BepInExLoader.RefuseOverrides(environment, arguments, StringComparer.Ordinal, nameof(HostServerLaunch));
        foreach (string argument in passed)
            if (argument.Contains('\0') || argument.Any(ch => ch is '\n' or '\r')) throw new ArgumentException("A launch argument cannot contain NUL or a line break.", nameof(arguments));
        var set = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in environment)
        {
            if (!VariableName.IsMatch(name ?? "")) throw new ArgumentException($"'{name}' is not a variable name.", nameof(environment));
            ArgumentNullException.ThrowIfNull(value, name);
            if (value.Contains('\0')) throw new ArgumentException($"{name} cannot contain a NUL character.", nameof(environment));
            if (name is "LD_LIBRARY_PATH" or "LD_PRELOAD") throw new ArgumentException($"{name} is set by the launch for BepInEx's loader; leave it out of the environment.", nameof(environment));
            set[name!] = value;
        }
        if (!set.ContainsKey("SteamAppId")) set["SteamAppId"] = ServerLaunch.DedicatedServerSteamAppId;
        set["DOORSTOP_ENABLED"] = "1";
        set["DOORSTOP_TARGET_ASSEMBLY"] = root + "/BepInEx/core/BepInEx.Preloader.dll";
        // Same effective order as the pack's script: linux64, then doorstop_libs, then the existing value.
        var prepended = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LD_LIBRARY_PATH"] = root + "/linux64:" + root + "/doorstop_libs",
            ["LD_PRELOAD"] = "libdoorstop_x64.so",
        };
        return new HostServerLaunch(root, passed, set, prepended);
    }

    /// <summary>The launch as the start script reads it: one line per item, <c>kind base64(UTF-8)</c>. Never written to disk: arguments may hold a server password.</summary>
    internal string Spec()
    {
        var text = new StringBuilder();
        void Line(string kind, string value) => text.Append(kind).Append(' ').Append(InteractiveClient.Base64(value)).Append('\n');
        foreach (string name in Unset) Line("unset", name);
        foreach (var (name, value) in Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal)) Line("env", name + "=" + value);
        foreach (var (name, value) in Prepended.OrderBy(pair => pair.Key, StringComparer.Ordinal)) Line("prepend", name + "=" + value);
        foreach (string argument in Arguments) Line("arg", argument);
        return text.ToString();
    }
}

/// <summary>
/// Starts an owned dedicated server on a Linux host (a <see cref="SshGameHost"/>, a <see cref="ContainerGameHost"/> or a local
/// bash host) and returns it identified by process ID and start time, the identity an owned session checks against its
/// adapter's reported process ID. The server runs in its own session (<c>setsid</c>) under a small recorder that writes its
/// process ID and later its exit code, so the end of the SSH session does not reach it; its standard output and error go to
/// <c>stdout.log</c> and <c>stderr.log</c> in the boot directory.
/// </summary>
public static class HostServer
{
    /// <summary>
    /// Starts <paramref name="launch"/> on <paramref name="host"/>. <paramref name="bootDirectory"/> is a new absolute directory on
    /// the host for this boot's evidence. <paramref name="logs"/> are the game's logs inside the runtime (for example
    /// <c>BepInEx/LogOutput.log</c>): a copy of any of them left by an earlier boot or by the install moves into the boot directory
    /// first (<c>previous-N.log</c>), so a wait on the log from offset 0 sees this boot's lines only, and they move there again
    /// (<c>game-N.log</c>) when the server stops. With <paramref name="evidence"/>, a new local directory, stopping then fetches the
    /// boot directory there. Returns once the server process exists, not when it is ready. A reply lost past
    /// <paramref name="timeout"/> is an unknown outcome: a server may be running, and the exception names its boot directory.
    /// </summary>
    public static async Task<HostServerProcess> StartAsync(IGameHost host, HostServerLaunch launch, string bootDirectory, TimeSpan timeout,
        IReadOnlyList<string>? logs = null, string? evidence = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(launch);
        if (timeout < TimeSpan.FromSeconds(15)) throw new ArgumentOutOfRangeException(nameof(timeout), "Allow a server start at least 15 s.");
        if (host.Shell.Kind != HostShellKind.Bash)
            throw new PlatformNotSupportedException($"A dedicated server on a host starts through a bash host on Linux; {host.Name} runs {host.Shell}.");
        HostInstall.RequireHostPath(host, bootDirectory, nameof(bootDirectory));
        string directory = bootDirectory.TrimEnd('/');
        if (directory.Length == 0) throw new ArgumentException("The boot directory cannot be a root.", nameof(bootDirectory));
        List<string> kept = logs?.ToList() ?? [];
        foreach (string log in kept)
            if (string.IsNullOrWhiteSpace(log) || log.Any(char.IsControl) || log.StartsWith('/') || log.Split('/').Contains(".."))
                throw new ArgumentException($"'{log}' is not a path inside the runtime.", nameof(logs));
        if (evidence != null && (Directory.Exists(evidence) || File.Exists(evidence))) throw new ArgumentException("The evidence directory must be new: " + evidence, nameof(evidence));

        var result = await host.RunAsync(HostServerScripts.Start, new Dictionary<string, string>
        {
            ["runtime"] = launch.Runtime, ["exe"] = ServerLaunch.LinuxExecutable, ["files"] = string.Join('\n', launch.RequiredFiles), ["dir"] = directory,
            ["spec"] = launch.Spec(), ["logs"] = string.Join('\n', kept),
            ["seconds"] = Math.Max(5, (int)Math.Floor(timeout.TotalSeconds) - 10).ToString(CultureInfo.InvariantCulture),
        }, timeout, cancellation).ConfigureAwait(false);
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
            return new HostServerProcess(host, id, parts[1], directory, launch.Runtime, kept, evidence);
        }
        throw word switch
        {
            "unsupported" => (Exception)new PlatformNotSupportedException($"Cannot start the Linux dedicated server on {host.Name}: {detail}"),
            "missing" => new FileNotFoundException($"The runtime on {host.Name} is incomplete: {detail}. Nothing was started."),
            "exists" => new InvalidOperationException($"{directory} already exists on {host.Name}; give each boot a new directory. Nothing was started."),
            "failed" => new InvalidOperationException($"The dedicated server did not start on {host.Name}: {detail}. Evidence is in {directory}."),
            _ => new HostOperationException($"Unexpected reply while starting the dedicated server on {host.Name}", result),
        };
    }
}

/// <summary>What stopping a server on a host found: it was killed, had gone already, or quit by itself when asked (SIGINT).</summary>
public enum HostServerStop { Stopped, AlreadyGone, Quit }

/// <summary>
/// A dedicated server <see cref="HostServer"/> started, identified by process ID and start time on its host. It is an
/// <see cref="IServerProcess"/>, so an <see cref="OwnedServerSession"/> launches, checks and stops it like a local one. Stopping
/// touches only that process, and only while its start time still matches (a process ID reused by another program is never
/// touched): a clean stop (<see cref="StopCleanly"/>) sends it SIGINT, on which the game saves, retires its PlayFab lobby and
/// quits, and kills it only if it has not exited in time. Then it keeps the boot's logs in its boot directory and, when an
/// evidence directory was given, fetches that directory here. Disposing kills it if it was not stopped yet.
/// </summary>
public sealed class HostServerProcess : IServerProcess, IAsyncDisposable
{
    private readonly IGameHost _host;
    private readonly IReadOnlyList<string> _logs;
    private readonly SemaphoreSlim _stopping = new(1, 1);
    private bool _killed, _kept;
    private volatile bool _exited;

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
            var result = (await _host.RunAsync(InteractiveScripts.LinuxWait, Variables(("seconds", "600")), TimeSpan.FromSeconds(660), cancellation).ConfigureAwait(false))
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
                var result = (await _host.RunAsync(InteractiveScripts.LinuxStop, Variables(("seconds", seconds), ("quit", quitSeconds), ("signal", "INT")),
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
                var kept = (await _host.RunAsync(HostServerScripts.Keep, new Dictionary<string, string> { ["runtime"] = Runtime, ["dir"] = BootDirectory, ["logs"] = string.Join('\n', _logs) },
                    TimeSpan.FromSeconds(60), cancellation).ConfigureAwait(false)).EnsureSuccess($"Keeping the logs of server process {Id} on {HostName}");
                if (InteractiveClient.Line(kept.Stdout, "VT-KEPT") == null) throw new HostOperationException($"Unexpected reply while keeping the logs of server process {Id} on {HostName}", kept);
                if (EvidenceDirectory != null) await _host.FetchDirectoryAsync(BootDirectory, EvidenceDirectory, EvidenceTimeout, cancellation).ConfigureAwait(false);
                _kept = true;
            }
            return outcome;
        }
        finally { _stopping.Release(); }
    }

    /// <summary>The <see cref="IServerProcess"/> stop: <see cref="StopAsync(TimeSpan, CancellationToken)"/>, waited for.</summary>
    public void Stop(TimeSpan timeout) => StopAsync(timeout).GetAwaiter().GetResult();

    /// <summary>The <see cref="IServerProcess"/> clean stop: <see cref="StopAsync(TimeSpan, TimeSpan, CancellationToken)"/>, waited for.</summary>
    public ProcessStop StopCleanly(TimeSpan quit, TimeSpan kill)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        return StopAsync(quit, kill).GetAwaiter().GetResult() switch
        {
            HostServerStop.Quit => new(StopOutcome.Clean, null, clock.Elapsed, "SIGINT"),
            HostServerStop.Stopped => new(StopOutcome.Killed, null, clock.Elapsed, quit > TimeSpan.Zero ? $"SIGINT; no exit within {WaitText.Seconds(quit)}" : "not asked to quit"),
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
    // A process's start time in clock ticks after boot (field 22 of /proc/PID/stat, counted after the name); empty for a
    // zombie or a missing process.
    private const string Started = """
        started() { local s; s=$(cat "/proc/$1/stat" 2> /dev/null) || return 1; s=${s##*) }; set -- $s; [ "$1" != Z ] && echo "${20}"; }
        """;

    // Variables: runtime, exe, files, dir, spec, logs, seconds. The recorder hands the server's PID back through a FIFO the
    // script already holds open, so the start waits for that event (bounded by seconds) rather than looking for a file; the
    // FIFO is opened read-write at both ends, so neither side can block on a missing partner. The server's own descriptors
    // are only the logs and /dev/null. Nothing here is written to disk but the evidence files: arguments may hold a password.
    public static readonly string Start = ("set -u\n" + Started + "\n" + """
        if [ "$(uname -s)" != Linux ]; then echo "VT-SERVER unsupported this host runs $(uname -s); the Linux dedicated server needs a Linux host"; exit 0; fi
        while IFS= read -r f; do
            if [ -n "$f" ] && [ ! -f "$runtime/$f" ]; then echo "VT-SERVER missing $f"; exit 0; fi
        done <<< "$files"
        if [ ! -x "$runtime/$exe" ]; then echo "VT-SERVER missing $exe is not executable"; exit 0; fi
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
        setsid bash -c 'f=$1; p=$2; x=$3; shift 3; exec 4<> "$f"; "$@" 4>&- & g=$!; printf "%s\n" "$g" > "$p.tmp" && mv -f -- "$p.tmp" "$p"; printf "%s\n" "$g" >&4; exec 4>&-; wait "$g"; printf "%s\n" "$?" > "$x.tmp" && mv -f -- "$x.tmp" "$x"' \
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
