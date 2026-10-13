using System.Security;

namespace Valheim.Testing.GameSessions;

// The macOS branch of InteractiveClient. SSH commands run in a Background audit session; launchctl
// bootstraps one run-owned agent into the already unlocked console user's Aqua domain. The agent and
// game are identified by the same launch directory and process identity as the other host backends.
internal static partial class InteractiveScripts
{
    internal const string MacLabelPrefix = "org.valheimtesting.client.";

    internal static string MacPlist(string label, string directory)
    {
        if (!label.StartsWith(MacLabelPrefix, StringComparison.Ordinal) ||
            label.Length != MacLabelPrefix.Length + 32 || !label[MacLabelPrefix.Length..].All(Uri.IsHexDigit))
            throw new ArgumentException("A Mac client agent needs a unique ValheimTesting label.", nameof(label));
        string escaped = SecurityElement.Escape(directory) ?? throw new ArgumentException("The launch directory is invalid.", nameof(directory));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>Label</key><string>{label}</string>
              <key>ProgramArguments</key><array><string>/bin/bash</string><string>{escaped}/launcher.sh</string></array>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key><false/>
            </dict></plist>
            """ + "\n";
    }

    // The GUI agent reads only a named FIFO, never a secret-bearing file. Its script and plist hold
    // the fixed launch mechanics and the public launch specification, not the password values.
    internal static readonly string MacLauncher = ("set -u\n" + MacProcess.Functions + """
        dir=$(cd -- "$(dirname -- "$0")" && pwd -P) || exit 3
        # This marker precedes every possible game spawn. A failed bootstrap can only be
        # settled as never started when neither this marker nor a pid exists.
        printf 'entered\n' > "$dir/launcher-entered" || exit 3
        IFS= read -r secrets < "$dir/secrets" || exit 3
        rm -f -- "$dir/secrets"
        spec=$(cat -- "$dir/spec") || exit 3
        install=$(cat -- "$dir/install") || exit 3
        exe=$(cat -- "$dir/exe") || exit 3
        architecture=$(cat -- "$dir/arch") || exit 3
        case "$architecture" in arm64|x86_64) ;; *) exit 3 ;; esac
        decode() { printf '%s' "$1" | base64 -D && printf x; }
        unsets=(); removed=(); sets=(); archOptions=("-$architecture"); args=()
        while IFS=' ' read -r kind value; do
          [ -n "$kind" ] || continue
          text=$(decode "$value") || exit 3
          text=${text%x}
          case "$kind" in
            unset) unsets+=(-u "$text"); removed+=("$text") ;;
            env) sets+=("$text") ;;
            prepend)
              name=${text%%=*}; entry=${text#*=}; current=${!name:-}
              for dropped in ${removed[@]+"${removed[@]}"}; do [ "$dropped" != "$name" ] || current=; done
              for pair in ${sets[@]+"${sets[@]}"}; do case "$pair" in "$name="*) current=${pair#*=} ;; esac; done
              [ -z "$current" ] || entry="$entry:$current"
              case "$name" in DYLD_*) archOptions+=(-e "$name=$entry") ;; *) sets+=("$name=$entry") ;; esac ;;
            arg) args+=("$text") ;;
          esac
        done <<< "$spec"
        for value in $secrets; do
          text=$(decode "$value") || exit 3
          export "${text%x}"
        done
        cd -- "$install" || exit 3
        # Bash ignores SIGINT in an asynchronous child. Restore the default disposition before
        # exec so a clean-quit request reaches Valheim instead of forcing a kill at the deadline.
        /usr/bin/perl -e '$SIG{INT} = "DEFAULT"; exec @ARGV or die "exec: $!"' \
          /usr/bin/env ${unsets[@]+"${unsets[@]}"} ${sets[@]+"${sets[@]}"} \
          /usr/bin/arch ${archOptions[@]+"${archOptions[@]}"} "$install/$exe" ${args[@]+"${args[@]}"} \
          > "$dir/game.stdout.log" 2> "$dir/game.stderr.log" < /dev/null &
        game=$!
        i=0
        while ! vt_mac_identity "$game" > /dev/null && [ "$i" -lt 50 ]; do sleep 0.1; i=$((i + 1)); done
        start=$(vt_mac_identity "$game") || start=
        [ -n "$start" ] || { printf 'no process identity\n' > "$dir/launcher-error.txt"; exit 3; }
        printf '%s %s\n' "$game" "$start" > "$dir/pid.tmp" && mv -f -- "$dir/pid.tmp" "$dir/pid"
        wait "$game"; code=$?
        printf '%s\n' "$code" > "$dir/exit.tmp" && mv -f -- "$dir/exit.tmp" "$dir/exit"
        """).ReplaceLineEndings("\n");

    // The SSH side. launchctl bootstrap (unlike launchctl asuser) can cross into this user's GUI
    // domain from SSH. The only secret transfer is the FIFO write after the agent is bootstrapped.
    internal static readonly string MacStart = ("set -u\nsecrets=${VT_SECRETS:-}\nunset VT_SECRETS\n" + MacProcess.Functions + """
        [ "$(uname -s)" = Darwin ] || { echo 'VT-INTERACTIVE unsupported this host is not macOS'; exit 0; }
        user=$(id -un); uid=$(id -u)
        [ "$(/usr/bin/stat -f %Su /dev/console)" = "$user" ] &&
          /bin/launchctl print "gui/$uid" >/dev/null 2>&1 || {
            echo 'VT-INTERACTIVE no-session the SSH user has no console GUI session'; exit 0;
          }
        while IFS= read -r f; do
          [ -n "$f" ] || continue
          case "$f" in /*) needed="$f" ;; *) needed="$install/$f" ;; esac
          [ -f "$needed" ] || { echo "VT-INTERACTIVE missing $f"; exit 0; }
        done <<< "$files"
        [ -x "$install/$exe" ] || { echo "VT-INTERACTIVE missing $exe is not executable"; exit 0; }
        [ ! -e "$dir" ] || { echo 'VT-INTERACTIVE exists'; exit 0; }
        mkdir -p -- "$(dirname -- "$dir")" && mkdir -- "$dir" || exit 3
        chmod 700 "$dir"
        printf '%s' "$spec" > "$dir/spec"
        printf '%s' "$install" > "$dir/install"
        printf '%s' "$exe" > "$dir/exe"
        printf '%s' "$arch" > "$dir/arch"
        printf '%s' "$label" > "$dir/label"
        printf '%s' "$launcher" > "$dir/launcher.sh" && chmod 700 "$dir/launcher.sh"
        printf '%s' "$plist" > "$dir/agent.plist"
        mkfifo "$dir/secrets" && chmod 600 "$dir/secrets" || exit 3
        if ! /bin/launchctl bootstrap "gui/$uid" "$dir/agent.plist" 2> "$dir/launchctl-error.txt"; then
          reason=$(tail -c 300 "$dir/launchctl-error.txt" | tr '\n' ' ')
          if ! /bin/launchctl print "gui/$uid/$label" >/dev/null 2>&1 &&
             [ ! -e "$dir/launcher-entered" ] && [ ! -e "$dir/pid" ]; then
            echo "VT-INTERACTIVE not-started launchctl could not start the GUI agent: $reason"
          else
            echo "VT-INTERACTIVE failed launchctl could not prove the GUI agent never ran: $reason"
          fi
          exit 0
        fi
        # The writer times out if the agent never opens the pipe. No password is an argument or file.
        if ! printf '%s\n' "$secrets" | /usr/bin/perl -e '
          alarm 15;
          open my $pipe, ">", $ARGV[0] or die "secret pipe: $!";
          while (my $line = <STDIN>) { print {$pipe} $line or die "secret pipe write: $!"; }
        ' "$dir/secrets"; then
          /bin/launchctl bootout "gui/$uid/$label" >/dev/null 2>&1 || true
          echo 'VT-INTERACTIVE failed GUI agent did not accept its launch input'; exit 0
        fi
        i=0
        while [ ! -s "$dir/pid" ] && [ "$i" -lt $((seconds * 10)) ]; do
          [ ! -s "$dir/launcher-error.txt" ] || break
          sleep 0.1; i=$((i + 1))
        done
        if [ ! -s "$dir/pid" ]; then
          /bin/launchctl bootout "gui/$uid/$label" >/dev/null 2>&1 || true
          echo "VT-INTERACTIVE failed no game process within $seconds s; see $dir/launcher-error.txt"; exit 0
        fi
        read -r game recorded < "$dir/pid"
        start=$(vt_mac_identity "$game") || start=
        if [ -z "$start" ] || [ "$start" != "$recorded" ]; then
          /bin/launchctl bootout "gui/$uid/$label" >/dev/null 2>&1 || true
          echo 'VT-INTERACTIVE failed the game exited before its process identity could be verified'; exit 0
        fi
        echo "VT-INTERACTIVE started $game $start"
        """).ReplaceLineEndings("\n");

    // A GUI agent is stopped only after the exact game process is gone. A lost bootout reply is verified through launchctl
    // before the run releases its host lock. vt_mac_agent_retire (variable dir) prints exactly one verdict, VT-AGENT
    // removed|kept|unproven: a launch directory that is gone left no agent; one without a ValheimTesting label is unproven.
    private static readonly string MacAgentFunction = """
        vt_mac_agent_retire() {
          local label suffix domain
          [ -d "$dir" ] || { echo 'VT-AGENT removed'; return 0; }
          [ -f "$dir/label" ] || { echo 'VT-AGENT unproven'; return 0; }
          label=$(cat -- "$dir/label")
          case "$label" in org.valheimtesting.client.*) ;; *) echo 'VT-AGENT unproven'; return 0 ;; esac
          suffix=${label#org.valheimtesting.client.}
          case "$suffix" in *[!0-9a-f]*|'') echo 'VT-AGENT unproven'; return 0 ;; esac
          [ "${#suffix}" -eq 32 ] || { echo 'VT-AGENT unproven'; return 0; }
          domain="gui/$(id -u)/$label"
          if /bin/launchctl print "$domain" >/dev/null 2>&1; then
            /bin/launchctl bootout "$domain" >/dev/null 2>&1 || true
          fi
          if /bin/launchctl print "$domain" >/dev/null 2>&1; then echo 'VT-AGENT kept'; else echo 'VT-AGENT removed'; fi
        }

        """.ReplaceLineEndings("\n");

    internal static readonly string MacAgentRetire = "set -u\n" + MacAgentFunction + "vt_mac_agent_retire\n";

    // The client's stop is MacProcess's one stop. Its GUI agent is retired only after that verdict is printed, and only once the
    // game is proven gone: a game still running, or unreadable, keeps its agent and the run's ownership.
    internal static readonly string MacStop = "set -u\n" + MacProcess.Functions + MacAgentFunction + """
        vt_mac_stop
        case "$vt_stopped" in gone|quit|stopped) vt_mac_agent_retire ;; esac
        """.ReplaceLineEndings("\n");
}
