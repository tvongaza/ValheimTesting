using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The one macOS process identity and stop (#665). A Mac has no <c>/proc</c>: a process's start identity is the checksum of its
/// C-locale <c>ps lstart</c> value, which tells it from a later process with the same ID. Every macOS script that reads a
/// process's identity (the probe, the server's and client's starts, launchers and waits, and every stop, recovery's included)
/// holds <see cref="Functions"/>, and every macOS stop is <c>vt_mac_stop</c>. Nothing else reads <c>lstart</c>.
/// </summary>
internal static class MacProcess
{
    /// <summary>
    /// Bash functions for a macOS host script, in front of the script that uses them.
    /// <list type="bullet">
    /// <item><c>vt_mac_observe PID [START]</c> prints <c>gone -</c> (no such process, or a zombie), <c>unreadable -</c> (a process has the ID but
    /// <c>ps</c> cannot read it, which is never taken for gone), <c>same IDENTITY</c> (it started at START, or any live process when START is
    /// empty) or <c>reused IDENTITY</c> (it started at another time, so the ID was reused).</item>
    /// <item><c>vt_mac_state PID [START]</c> prints only that word.</item>
    /// <item><c>vt_mac_started PID</c> prints the start identity <c>ps</c> reports for PID, a zombie's included (the server's
    /// supervisor reads its own child's, which stays a zombie until it is reaped), and fails when there is none. It is the one
    /// read of <c>lstart</c>.</item>
    /// <item><c>vt_mac_identity PID</c> prints a live process's start identity, and fails when it is gone or unreadable.</item>
    /// <item><c>vt_mac_command PID</c> prints the full <c>ps command</c> value, and fails when it cannot be read.</item>
    /// <item><c>vt_mac_stop</c> stops the process the variables <c>game</c> and <c>start</c> name. With <c>quit</c> over 0 it first sends
    /// SIGINT (the game saves and quits) and waits up to <c>quit</c> seconds; only then does it send SIGKILL and wait up to <c>seconds</c>.
    /// Both waits are measured in elapsed time, not in rounds, however long each <c>ps</c> takes: bash's <c>$SECONDS</c> counts whole
    /// seconds, so each deadline is one second further on, and a wait is never shorter than its budget.
    /// With a non-empty <c>commandLine</c> (recovery) the process's command line must also hash to it before anything is signalled.
    /// It prints exactly one verdict, <c>VT-STOP gone|quit|stopped|running|unproven</c>, and leaves the word in <c>vt_stopped</c>: a process
    /// that cannot be read, or whose command line differs, is <c>unproven</c> and is never signalled.</item>
    /// </list>
    /// </summary>
    internal static readonly string Functions = """
        vt_mac_observe() {
          local state begin identity
          state=$(LC_ALL=C ps -p "$1" -o stat= 2>/dev/null) || state=
          if [ -z "$state" ]; then
            if kill -0 "$1" 2>/dev/null; then echo 'unreadable -'; else echo 'gone -'; fi
            return 0
          fi
          case "$state" in Z*) echo 'gone -'; return 0 ;; esac
          identity=$(vt_mac_started "$1") || { echo 'unreadable -'; return 0; }
          if [ -z "${2:-}" ] || [ "$identity" = "$2" ]; then echo "same $identity"; else echo "reused $identity"; fi
        }
        vt_mac_started() {
          local begin
          begin=$(LC_ALL=C ps -ww -p "$1" -o lstart= 2>/dev/null) || return 1
          [ -n "$begin" ] || return 1
          printf '%s' "$begin" | cksum | awk '{print $1}'
        }
        vt_mac_state() { local observed; observed=$(vt_mac_observe "$1" "${2:-}"); echo "${observed%% *}"; }
        vt_mac_identity() {
          local observed; observed=$(vt_mac_observe "$1" "")
          case "$observed" in "same "*) echo "${observed#same }" ;; *) return 1 ;; esac
        }
        vt_mac_command() {
          local command; command=$(LC_ALL=C ps -ww -p "$1" -o command= 2>/dev/null) || return 1
          [ -n "$command" ] || return 1
          printf '%s\n' "$command"
        }
        vt_mac_stop() {
          local state command hash deadline
          vt_stopped=unproven
          state=$(vt_mac_state "$game" "$start")
          case "$state" in
            gone|reused) vt_stopped=gone ;;
            same)
              if [ -n "${commandLine:-}" ]; then
                command=$(vt_mac_command "$game") || command=
                hash=$(printf '%s' "$command" | shasum -a 256 | awk '{print $1}')
                [ -n "$command" ] && [ "$hash" = "$commandLine" ] || state=unreadable
              fi
              if [ "$state" = same ] && [ "$quit" -gt 0 ] && kill -INT "$game" 2>/dev/null; then
                deadline=$((SECONDS + quit + 1))
                while [ "$(vt_mac_state "$game" "$start")" = same ] && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.1; done
                state=$(vt_mac_state "$game" "$start")
                case "$state" in gone|reused) vt_stopped=quit ;; esac
              fi
              if [ "$state" = same ]; then
                kill -KILL "$game" 2>/dev/null || true
                deadline=$((SECONDS + seconds + 1))
                while [ "$(vt_mac_state "$game" "$start")" = same ] && [ "$SECONDS" -lt "$deadline" ]; do sleep 0.1; done
                state=$(vt_mac_state "$game" "$start")
                case "$state" in gone|reused) vt_stopped=stopped ;; same) vt_stopped=running ;; esac
              fi ;;
          esac
          echo "VT-STOP $vt_stopped"
        }

        """.ReplaceLineEndings("\n");

    /// <summary>The one macOS stop, for a server and for recovery. Variables: game, start, quit, seconds, and optionally commandLine.</summary>
    internal static readonly string Stop = "set -u\n" + Functions + "vt_mac_stop\n";

    /// <summary>
    /// Waits for the process (variables game, start, seconds, dir) to exit, then reports the exit code its launcher wrote to
    /// <c>dir/exit</c>: <c>VT-WAIT exited CODE|?</c>, or after <c>seconds</c> <c>VT-WAIT running</c>, or <c>VT-WAIT unproven</c> when
    /// its last read failed. A process that cannot be read is never taken for one that exited: the caller waits on.
    /// </summary>
    internal static readonly string Wait = "set -u\n" + Functions + """
        deadline=$((SECONDS + seconds))
        state=$(vt_mac_state "$game" "$start")
        while [ "$state" = same ] || [ "$state" = unreadable ]; do
          if [ "$SECONDS" -ge "$deadline" ]; then
            if [ "$state" = same ]; then echo 'VT-WAIT running'; else echo 'VT-WAIT unproven'; fi
            exit 0
          fi
          sleep 1
          state=$(vt_mac_state "$game" "$start")
        done
        for _ in 1 2 3 4 5 6 7 8 9 10; do [ ! -s "$dir/exit" ] || break; sleep 0.2; done
        if [ -s "$dir/exit" ]; then echo "VT-WAIT exited $(cat "$dir/exit")"; else echo 'VT-WAIT exited ?'; fi
        """.ReplaceLineEndings("\n");

    /// <summary>
    /// Runs the one stop (<see cref="Stop"/>) on a Mac host, for a server's own stop and for recovery's of a server or a client:
    /// <paramref name="what"/> names the process in errors, and a recovery passes the journalled <paramref name="commandLine"/>,
    /// which the process must still run. Returns <c>gone</c>, <c>quit</c> or <c>stopped</c>. A process still there after the kill
    /// throws <see cref="InvalidOperationException"/>; one that could not be read, or runs another command line, was never
    /// signalled and throws <see cref="HostOperationException"/>.
    /// </summary>
    internal static async Task<string> StopAsync(IGameHost host, string what, int pid, string start, TimeSpan quit, TimeSpan timeout, string? commandLine,
        CancellationToken cancellation)
    {
        WaitText.RequireTimeout(timeout);
        if (quit < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(quit));
        var variables = new Dictionary<string, string>
        {
            ["game"] = pid.ToString(System.Globalization.CultureInfo.InvariantCulture), ["start"] = start, ["commandLine"] = commandLine ?? "",
            ["quit"] = WaitText.WholeSeconds(quit), ["seconds"] = WaitText.WholeSeconds(timeout, minimum: 1),
        };
        var result = (await host.RunAsync(Stop, variables, quit + timeout + TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Stopping {what}");
        string? verdict = InteractiveClient.Line(result.Stdout, "VT-STOP ");
        return verdict switch
        {
            "gone" or "quit" or "stopped" => verdict,
            "running" => throw new InvalidOperationException($"{what} was still running {WaitText.Seconds(timeout)} after it was killed."),
            "unproven" => throw new HostOperationException($"{what} could not be read" + (commandLine is { Length: > 0 } ? ", or no longer runs its journalled command line" : "") + "; it was not stopped", result),
            _ => throw new HostOperationException($"Unexpected reply while stopping {what}", result),
        };
    }
}
