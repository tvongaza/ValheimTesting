using System.Globalization;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>The durable identity of a HostServer launcher, chosen before the launch is journalled.</summary>
internal sealed record HostServerLaunchOwnership(string FileName, string TaskName)
{
    private const string Prefix = "launcher-", Suffix = ".owner";

    internal static HostServerLaunchOwnership Create(ClientPlatform platform) =>
        new(Prefix + Guid.NewGuid().ToString("N") + Suffix,
            platform == ClientPlatform.Windows ? "VT-Server-" + Guid.NewGuid().ToString("N") : "");

    internal static bool ValidFileName(string value) => value.Length == Prefix.Length + 32 + Suffix.Length &&
        value.StartsWith(Prefix, StringComparison.Ordinal) && value.EndsWith(Suffix, StringComparison.Ordinal) &&
        value.AsSpan(Prefix.Length, 32).IndexOfAnyExcept("0123456789abcdef") < 0;

    internal static HostServerLauncherRecord Parse(string text)
    {
        var words = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        static int Pid(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed : throw new InvalidDataException("a process ID is not a positive integer");
        static string Start(string value) => value.Length is > 0 and <= 20 && value.All(char.IsAsciiDigit)
            ? value : throw new InvalidDataException("a process start identity is not numeric");

        if (words.Length is 5 or 7 && words[0] == "unix")
        {
            int launcherPid = Pid(words[1]), processGroup = Pid(words[3]), sessionId = Pid(words[4]);
            int? gamePid = words.Length == 7 ? Pid(words[5]) : null;
            string? gameStart = words.Length == 7 ? Start(words[6]) : null;
            return new(ClientPlatform.Linux, launcherPid, Start(words[2]), processGroup, sessionId, "", gamePid, gameStart);
        }
        if (words.Length is 5 or 7 && words[0] == "windows")
        {
            if (!ValidTask(words[1])) throw new InvalidDataException("the scheduled-task name is malformed");
            int? gamePid = words.Length == 7 ? Pid(words[5]) : null;
            string? gameStart = words.Length == 7 ? Start(words[6]) : null;
            // A Windows session ID is 0 for a session-0 (S4U) task.
            int session = int.TryParse(words[4], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                ? parsed : throw new InvalidDataException("a session ID is not a non-negative integer");
            return new(ClientPlatform.Windows, Pid(words[2]), Start(words[3]), 0, session, words[1], gamePid, gameStart);
        }
        throw new InvalidDataException("it does not have a supported launcher-ownership record");
    }

    internal static bool ValidTask(string value) => value.Length == "VT-Server-".Length + 32 &&
        value.StartsWith("VT-Server-", StringComparison.Ordinal) && value.AsSpan("VT-Server-".Length).IndexOfAnyExcept("0123456789abcdef") < 0;
}

internal sealed record HostServerLauncherRecord(ClientPlatform Platform, int LauncherPid, string LauncherStartIdentity,
    int ProcessGroup, int SessionId, string TaskName, int? GamePid, string? GameStartIdentity)
{
    internal IReadOnlyDictionary<string, string> Fields(string directory, string ownerFile,
        string expectedCommandLine, string quitSeconds)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bootDirectory"] = directory, ["launcherOwnershipFile"] = ownerFile,
            ["launcherPlatform"] = Platform == ClientPlatform.Windows ? "windows" : "unix",
            ["launcherPid"] = LauncherPid.ToString(CultureInfo.InvariantCulture),
            ["launcherStartIdentity"] = LauncherStartIdentity,
            ["launcherProcessGroup"] = ProcessGroup.ToString(CultureInfo.InvariantCulture),
            ["launcherSessionId"] = SessionId.ToString(CultureInfo.InvariantCulture),
            ["launcherTask"] = TaskName, ["expectedCommandLineSha256"] = expectedCommandLine,
            ["quitSeconds"] = quitSeconds,
        };
        if (GamePid is { } game && GameStartIdentity is { } start)
        {
            fields["pid"] = game.ToString(CultureInfo.InvariantCulture);
            fields["startIdentity"] = start;
        }
        return fields;
    }

    /// <summary>The record <see cref="Fields"/> wrote, as recovery reads it back from the status item.</summary>
    internal static HostServerLauncherRecord FromFields(IReadOnlyDictionary<string, string> fields)
    {
        static int Number(string text) => int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        return new(fields["launcherPlatform"] == "windows" ? ClientPlatform.Windows : ClientPlatform.Linux,
            Number(fields["launcherPid"]), fields["launcherStartIdentity"], Number(fields["launcherProcessGroup"]),
            Number(fields["launcherSessionId"]), fields["launcherTask"],
            fields.TryGetValue("pid", out string? game) ? Number(game) : null, fields.GetValueOrDefault("startIdentity"));
    }
}

internal enum HostServerLauncherState { Owned, Gone, Reused, Mismatch, Unreadable }

/// <summary>Checks and retires the launcher owner without ever signalling an unverified PID, group or scheduled task.</summary>
internal static class HostServerLauncher
{
    internal static async Task<HostServerLauncherState> InspectAsync(IGameHost host, HostServerLauncherRecord owner,
        string directory, TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync(owner.Platform == ClientPlatform.Windows ? WindowsInspect : UnixInspect,
            Variables(owner, directory, timeout), timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Checking server launcher {owner.LauncherPid} on {host.Name}");
        return InteractiveClient.Line(result.Stdout, "VT-LAUNCHER ") switch
        {
            "owned" => HostServerLauncherState.Owned, "gone" => HostServerLauncherState.Gone,
            "reused" => HostServerLauncherState.Reused, "mismatch" => HostServerLauncherState.Mismatch,
            "unreadable" => HostServerLauncherState.Unreadable,
            _ => throw new HostOperationException($"Unexpected reply while checking server launcher {owner.LauncherPid} on {host.Name}", result),
        };
    }

    /// <summary>
    /// Kills the launcher's whole process group (Unix) or process tree (Windows) only while it is still the recorded launcher, and
    /// on Windows removes the task registration an interrupted start left. Returns <c>stopped</c>, or <c>gone</c> when the launcher
    /// had exited or its ID now names another start (nothing of it is left to stop). A launcher that cannot be read, or whose
    /// group, session or task no longer matches the record, was never signalled and throws.
    /// </summary>
    internal static async Task<string> RetireAsync(IGameHost host, HostServerLauncherRecord owner, string directory,
        TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync(owner.Platform == ClientPlatform.Windows ? WindowsRetire : UnixRetire,
            Variables(owner, directory, timeout), timeout + TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Stopping server launcher {owner.LauncherPid} on {host.Name}");
        return InteractiveClient.Line(result.Stdout, "VT-LAUNCHER ") switch
        {
            "stopped" => "stopped",
            "gone" or "reused" => "gone",
            "running" => throw new InvalidOperationException($"Server launcher {owner.LauncherPid} on {host.Name} was still running after it was killed."),
            "mismatch" or "unreadable" => throw new HostOperationException(
                $"Server launcher {owner.LauncherPid} on {host.Name} could not be proven its recorded launcher; it was not stopped", result),
            _ => throw new HostOperationException($"Unexpected reply while stopping server launcher {owner.LauncherPid} on {host.Name}", result),
        };
    }

    private static Dictionary<string, string> Variables(HostServerLauncherRecord owner, string directory, TimeSpan timeout) => new()
    {
        ["launcherPid"] = owner.LauncherPid.ToString(CultureInfo.InvariantCulture),
        ["launcherStart"] = owner.LauncherStartIdentity,
        ["launcherGroup"] = owner.ProcessGroup.ToString(CultureInfo.InvariantCulture),
        ["launcherSession"] = owner.SessionId.ToString(CultureInfo.InvariantCulture),
        ["task"] = owner.TaskName, ["dir"] = directory,
        ["seconds"] = WaitText.WholeSeconds(timeout, minimum: 1),
    };

    internal static readonly string UnixInspect = ("set -u\n" + MacProcess.Functions + UnixIdentity + "\nvt_launcher_state\necho \"VT-LAUNCHER $vt_launcher\"\n").ReplaceLineEndings("\n");

    internal static readonly string UnixRetire = ("set -u\n" + MacProcess.Functions + UnixIdentity + "\n" + """
        vt_launcher_state
        case "$vt_launcher" in
          gone|reused|mismatch|unreadable) echo "VT-LAUNCHER $vt_launcher"; exit 0 ;;
        esac
        kill -KILL -- "-$launcherGroup" 2>/dev/null || true
        deadline=$((SECONDS + seconds + 1))
        while [ "$SECONDS" -lt "$deadline" ]; do vt_launcher_state; [ "$vt_launcher" = owned ] || break; sleep 0.1; done
        case "$vt_launcher" in gone|reused) echo 'VT-LAUNCHER stopped' ;; owned) echo 'VT-LAUNCHER running' ;; *) echo "VT-LAUNCHER $vt_launcher" ;; esac
        """).ReplaceLineEndings("\n");

    private const string UnixIdentity = """
        vt_launcher_state() {
          vt_launcher=unreadable
          if [ "$(uname -s)" = Darwin ]; then
            observed=$(vt_mac_observe "$launcherPid" "$launcherStart"); state=${observed%% *}
            case "$state" in gone) vt_launcher=gone; return ;; reused) vt_launcher=reused; return ;; same) ;; *) return ;; esac
            group=$(LC_ALL=C ps -p "$launcherPid" -o pgid= 2>/dev/null | tr -d ' ') || return
            if [ "$launcherPid" != "$launcherGroup" ] || [ "$launcherGroup" != "$launcherSession" ] || [ "$group" != "$launcherGroup" ]; then vt_launcher=mismatch; else vt_launcher=owned; fi
            return
          fi
          [ -e "/proc/$launcherPid/stat" ] || { vt_launcher=gone; return; }
          stat=$(cat "/proc/$launcherPid/stat" 2>/dev/null) || return
          stat=${stat##*) }; set -- $stat
          [ "$1" != Z ] || { vt_launcher=gone; return; }
          [ "${20:-}" = "$launcherStart" ] || { vt_launcher=reused; return; }
          if [ "$launcherPid" != "$launcherGroup" ] || [ "$launcherGroup" != "$launcherSession" ] ||
             [ "${3:-}" != "$launcherGroup" ] || [ "${4:-}" != "$launcherSession" ]; then vt_launcher=mismatch; else vt_launcher=owned; fi
        }
        """;

    // The launcher's identity as the Windows launcher recorded it: the same process ID, start time and session, or why not.
    private const string WindowsIdentity = """
        $vtLauncher = 'unreadable'; $process = $null
        try {
            $process = [Diagnostics.Process]::GetProcessById([int]$launcherPid)
            if ($process.HasExited) { $vtLauncher = 'gone' }
            elseif ([string]$process.StartTime.ToFileTimeUtc() -cne $launcherStart) { $vtLauncher = 'reused' }
            elseif ($process.SessionId -ne [int]$launcherSession) { $vtLauncher = 'mismatch' }
            else { $vtLauncher = 'owned' }
        } catch [ArgumentException] { $vtLauncher = 'gone' }
          catch [InvalidOperationException] { $vtLauncher = 'gone' }
          catch { $vtLauncher = 'unreadable' }
        """;

    internal static readonly string WindowsInspect = ("$ErrorActionPreference = 'Stop'\n" + WindowsIdentity + "\n'VT-LAUNCHER ' + $vtLauncher\n").ReplaceLineEndings("\n");

    // Kills the launcher's process tree only while it is the recorded launcher, then removes the launch's task registration
    // when the interrupted start left it: the exact task name the launch journalled, starting this boot directory's launcher.
    internal static readonly string WindowsRetire = ("$ErrorActionPreference = 'Stop'\n" + WindowsIdentity + "\n" + """
        if ($vtLauncher -in @('mismatch', 'unreadable')) { 'VT-LAUNCHER ' + $vtLauncher; exit 0 }
        if ($vtLauncher -eq 'owned') {
            $process.Kill($true)
            if (-not $process.WaitForExit([int]$seconds * 1000)) { 'VT-LAUNCHER running'; exit 0 }
            $vtLauncher = 'stopped'
        }
        $service = New-Object -ComObject Schedule.Service
        $service.Connect()
        $folder = $service.GetFolder('\')
        $found = @($folder.GetTasks(1) | Where-Object { $_.Path -ceq ('\' + $task) })
        if ($found.Count -eq 1) {
            $definition = $found[0].Definition
            if ($definition.RegistrationInfo.Description -ceq 'ValheimTesting: starts one owned headless dedicated server; removed after launch.' -and
                [int]$definition.Actions.Count -eq 1 -and $definition.Actions.Item(1).WorkingDirectory.TrimEnd('\', '/') -ceq $dir.TrimEnd('\', '/')) {
                if ($found[0].State -in @(2, 4)) { $found[0].Stop(0) }
                $folder.DeleteTask($found[0].Path, 0)
            }
        }
        'VT-LAUNCHER ' + $vtLauncher
        """).ReplaceLineEndings("\n");
}
