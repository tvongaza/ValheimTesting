using System.Globalization;

namespace Valheim.Testing.Game;

/// <summary>What a host says about a process a run journalled.</summary>
internal enum ProbedState
{
    /// <summary>No process has that ID, or it has exited.</summary>
    Gone,
    /// <summary>A process has that ID but started at another time: the ID was reused, so the journalled process is gone.</summary>
    Reused,
    /// <summary>The process with that ID started at the journalled time.</summary>
    Same,
    /// <summary>The host could not tell (the start time could not be read, or the reply named nothing).</summary>
    Unreadable,
}

/// <summary>
/// One probed process: its state, the start identity the host read and the SHA-256 of its command line (lower-case hex), each
/// null when the host could not read it.
/// </summary>
internal sealed record ProbedProcess(int Pid, ProbedState State, string? StartIdentity, string? CommandLineSha256);

/// <summary>
/// Reads, without changing anything, whether processes a run journalled still run on a host: the process ID, its start
/// identity (the same rule as the stop scripts: Windows <c>StartTime</c> as a UTC file time, Linux the start in clock ticks
/// from <c>/proc/PID/stat</c>) and the SHA-256 of its command line (Windows <c>Win32_Process.CommandLine</c> as UTF-8, Linux the
/// raw bytes of <c>/proc/PID/cmdline</c>). A recovery stops a process only when all three match its journal entry (#257 Q2).
/// </summary>
internal static class HostProcessProbe
{
    /// <summary>
    /// The host's word on each journalled process, keyed by the pair asked about (a reused ID asked about twice, with two start
    /// identities, gets two answers). <paramref name="settle"/> is for a process just started: on Linux its ID may still run a
    /// launch stage (the recorder's forked shell, <c>setsid</c> or <c>env</c>) that has not yet executed the game, so the check
    /// waits up to 5 s for that before it reads the command line (#463).
    /// </summary>
    public static async Task<IReadOnlyDictionary<(int Pid, string StartIdentity), ProbedProcess>> ProbeAsync(IGameHost host,
        IReadOnlyCollection<(int Pid, string StartIdentity)> processes, TimeSpan timeout, CancellationToken cancellation = default, bool settle = false)
    {
        var found = new Dictionary<(int Pid, string StartIdentity), ProbedProcess>();
        if (processes.Count == 0) return found;
        foreach (var (pid, start) in processes)
            if (pid <= 0 || start.Any(c => !char.IsAsciiDigit(c)))
                throw new ArgumentException($"A journalled process is a positive ID and a numeric start identity, not {pid} '{start}'.", nameof(processes));
        string list = string.Join(' ', processes.Distinct().Select(process => process.Pid.ToString(CultureInfo.InvariantCulture) + ":" + process.StartIdentity));
        var variables = new Dictionary<string, string> { ["processes"] = list };
        if (settle) variables["settle"] = "1";
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? Windows : Bash, variables, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Reading journalled processes on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-PROC-END") == null)
            throw new HostOperationException($"The process check on {host.Name} did not finish", result);
        foreach (string line in result.Stdout.Split('\n'))
        {
            // VT-PROC <pid> <start asked, or -> <state> <start read, or -> <sha256, or ->
            var parts = line.Trim().Split(' ');
            if (parts.Length != 6 || parts[0] != "VT-PROC" || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) continue;
            var state = parts[3] switch { "gone" => ProbedState.Gone, "reused" => ProbedState.Reused, "same" => ProbedState.Same, _ => ProbedState.Unreadable };
            string? Read(string value) => value == "-" ? null : value;
            found[(pid, Read(parts[2]) ?? "")] = new(pid, state, Read(parts[4]), Read(parts[5]) is { Length: 64 } hash && hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant() : null);
        }
        // A process the reply did not name is one the host could not tell about.
        foreach (var process in processes) found.TryAdd(process, new(process.Pid, ProbedState.Unreadable, null, null));
        return found;
    }

    /// <summary>
    /// The command-line hash of a process just started, for its journal entry; null (with a warning) when the host could not
    /// read it, which leaves a process no recovery ever stops. Never throws: the process runs either way.
    /// </summary>
    public static async Task<string?> CommandLineAsync(IGameHost host, int pid, string startIdentity, TimeSpan timeout)
    {
        try
        {
            var probed = (await ProbeAsync(host, [(pid, startIdentity)], timeout, settle: true).ConfigureAwait(false))[(pid, startIdentity)];
            if (probed.State == ProbedState.Same && probed.CommandLineSha256 != null) return probed.CommandLineSha256;
            Console.Error.WriteLine($"Warning: the command line of process {pid} on {host.Name} could not be read ({probed.State}); no recovery will stop it.");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Warning: the command line of process {pid} on {host.Name} could not be read: {error.Message}; no recovery will stop it.");
        }
        return null;
    }

    /// <summary>
    /// The SHA-256 (lower-case hex) the probe reads for a game the launch scripts start with <paramref name="executable"/> and
    /// <paramref name="arguments"/>, journalled before the start so a launch interrupted before its process was journalled can
    /// still be proven its own from its pid file. Windows: the command line .NET's <c>Process.Start</c> passes to
    /// <c>CreateProcess</c> (the executable in quotes, then a space and the joined arguments when there are any) as UTF-8. Linux:
    /// <c>/proc/PID/cmdline</c> once <c>env</c> has executed the game: the executable as given, then each argument, each followed by a NUL.
    /// </summary>
    internal static string ExpectedCommandLineSha256(bool windows, string executable, IReadOnlyList<string> arguments)
    {
        string text;
        if (windows)
        {
            string file = executable.Trim();
            bool quoted = file.StartsWith('"') && file.EndsWith('"');
            string joined = WindowsCommandLine.Join(arguments);
            text = (quoted ? file : "\"" + file + "\"") + (joined.Length == 0 ? "" : " " + joined);
        }
        else text = string.Concat(arguments.Prepend(executable).Select(argument => argument + "\0"));
        return FileHash.Sha256(System.Text.Encoding.UTF8.GetBytes(text));
    }

    // Variables: processes (space-separated PID:START pairs; an empty START matches any), settle (optional). One line per pair:
    // VT-PROC <pid> <start asked or -> gone|reused|same|unreadable <start read or -> <sha256 or ->, then VT-PROC-END. With
    // settle, a process still running as a launch stage (the recorder's forked shell, setsid or env, busybox's too, before the game is
    // executed in its place) is given up to 5 s to exec.
    internal static readonly string Bash = """
        started() { local s; s=$(cat "/proc/$1/stat" 2> /dev/null) || return 1; s=${s##*) }; set -- $s; [ "$1" != Z ] && echo "${20}"; }
        for pair in $processes; do
          id=${pair%%:*}; start=${pair#*:}; asked=${start:--}
          if [ ! -d /proc/self ]; then echo "VT-PROC $id $asked unreadable - -"; continue; fi
          if [ ! -e "/proc/$id" ]; then echo "VT-PROC $id $asked gone - -"; continue; fi
          identity=$(started "$id") || { echo "VT-PROC $id $asked gone - -"; continue; }
          if [ -n "$start" ] && [ "$identity" != "$start" ]; then echo "VT-PROC $id $asked reused $identity -"; continue; fi
          if [ -n "${settle:-}" ]; then
            n=0
            while [ "$n" -lt 50 ]; do
              case "$(basename -- "$(readlink "/proc/$id/exe" 2> /dev/null)")" in bash|sh|dash|busybox|setsid|env) sleep 0.1; n=$((n + 1)) ;; *) break ;; esac
            done
          fi
          hash=$({ sha256sum < "/proc/$id/cmdline"; } 2> /dev/null | cut -d' ' -f1)
          echo "VT-PROC $id $asked same $identity ${hash:--}"
        done
        echo VT-PROC-END
        """;
    // One CIM query reads every command line at once: each Get-CimInstance call costs about a second on Windows PowerShell.
    internal static readonly string Windows = """
        $utf8 = New-Object Text.UTF8Encoding $false
        $sha = [Security.Cryptography.SHA256]::Create()
        $verdicts = @()
        foreach ($pair in ($processes -split ' ')) {
            if (-not $pair) { continue }
            $id, $start = $pair -split ':', 2
            $asked = if ($start) { $start } else { '-' }
            $process = $null
            try { $process = [Diagnostics.Process]::GetProcessById([int]$id) } catch { }
            if ($null -eq $process) { $verdicts += ,@($id, $asked, 'gone', '-'); continue }
            $identity = $null
            try { $identity = [string]$process.StartTime.ToFileTimeUtc() } catch { }
            if ($null -eq $identity) { if ($process.HasExited) { $verdicts += ,@($id, $asked, 'gone', '-') } else { $verdicts += ,@($id, $asked, 'unreadable', '-') }; continue }
            if ($start -and $identity -cne $start) { $verdicts += ,@($id, $asked, 'reused', $identity); continue }
            $verdicts += ,@($id, $asked, 'same', $identity)
        }
        $lines = @{}
        $same = @($verdicts | Where-Object { $_[2] -eq 'same' } | ForEach-Object { 'ProcessId=' + [int]$_[0] })
        if ($same.Count -gt 0) {
            try { foreach ($found in (Get-CimInstance Win32_Process -Filter ($same -join ' OR ') -ErrorAction Stop)) { $lines[[string]$found.ProcessId] = $found.CommandLine } } catch { }
        }
        foreach ($verdict in $verdicts) {
            $hash = '-'
            if ($verdict[2] -eq 'same' -and $null -ne $lines[$verdict[0]]) { $hash = -join ($sha.ComputeHash($utf8.GetBytes($lines[$verdict[0]])) | ForEach-Object { $_.ToString('x2') }) }
            'VT-PROC ' + $verdict[0] + ' ' + $verdict[1] + ' ' + $verdict[2] + ' ' + $verdict[3] + ' ' + $hash
        }
        'VT-PROC-END'
        """;
}
