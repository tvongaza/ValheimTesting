namespace Valheim.Testing.GameSessions;

// The macOS backend of the same HostServer owner used on Linux and Windows. A short-lived Perl launcher provides
// a detached supervisor with default SIGINT disposition; a backgrounded bash child would inherit SIGINT ignored,
// making a clean Valheim save impossible. Perl ships with macOS and is checked before staging.
internal static partial class HostServerScripts
{
    internal static readonly string MacStart = ("set -eu\n" + MacProcess.Functions + """
        [ "$(uname -s)" = Darwin ] || { echo 'VT-SERVER unsupported this host is not macOS'; exit 0; }
        [ -x /usr/bin/perl ] && [ -x /usr/bin/arch ] || { echo 'VT-SERVER unsupported macOS needs its system perl and arch'; exit 0; }
        while IFS= read -r f; do
          [ -n "$f" ] || continue
          case "$f" in /*) needed="$f" ;; *) needed="$runtime/$f" ;; esac
          [ -f "$needed" ] || { echo "VT-SERVER missing $f"; exit 0; }
        done <<< "$files"
        [ -x "$runtime/$exe" ] || { echo "VT-SERVER missing $exe is not executable"; exit 0; }
        [ ! -e "$dir" ] || { echo 'VT-SERVER exists'; exit 0; }
        mkdir -p -- "$dir"
        i=0
        while IFS= read -r log; do
          [ -n "$log" ] || { i=$((i + 1)); continue; }
          [ ! -e "$logroot/$log" ] || mv "$logroot/$log" "$dir/previous-$i.log"
          i=$((i + 1))
        done <<< "$logs"
        decode() { printf '%s' "$1" | base64 -D; }
        unsets=(); removed=(); sets=(); archOptions=(-arm64); args=()
        while IFS=' ' read -r kind value; do
          [ -n "$kind" ] || continue
          text=$(decode "$value") || exit 3
          case "$kind" in
            unset) unsets+=(-u "$text"); removed+=("$text") ;;
            env) sets+=("$text") ;;
            prepend)
              name=${text%%=*}; entry=${text#*=}; current=${!name:-}
              for dropped in ${removed[@]+"${removed[@]}"}; do [ "$dropped" != "$name" ] || current=; done
              for pair in ${sets[@]+"${sets[@]}"}; do case "$pair" in "$name="*) current=${pair#*=} ;; esac; done
              if [ -n "$current" ]; then entry="$entry:$current"; fi
              case "$name" in DYLD_*) archOptions+=(-e "$name=$entry") ;; *) sets+=("$name=$entry") ;; esac ;;
            arg) args+=("$text") ;;
          esac
        done <<< "$spec"
        cd -- "$workdir"
        /usr/bin/perl -MPOSIX -e '
          use strict; use warnings;
          my ($directory, $identify, @command) = @ARGV;
          pipe(my $reader, my $writer) or die "pipe: $!";
          my $supervisor = fork(); die "fork: $!" unless defined $supervisor;
          if ($supervisor) {
            close $writer;
            my $reply = <$reader>;
            print $reply if defined $reply;
            exit(defined $reply ? 0 : 3);
          }
          close $reader;
          POSIX::setsid() or die "setsid: $!";
          $SIG{HUP} = "IGNORE";
          open STDOUT, ">", "$directory/stdout.log" or die "stdout: $!";
          open STDERR, ">", "$directory/stderr.log" or die "stderr: $!";
          my $game = fork(); die "game fork: $!" unless defined $game;
          if (!$game) {
            close $writer;
            $SIG{INT} = "DEFAULT";
            exec @command or die "exec: $!";
          }
          # The start identity is read by vt_mac_started (MacProcess), handed over as text: never computed here. It reads a game
          # that already exited too (a zombie until the waitpid below), so a game that dies at once still reports its exit code.
          my $identity = "";
          for (1..50) {
            open(my $read, "-|", "/bin/bash", "-c", "$identify\nvt_mac_started \"\$1\"", "vt", $game) or die "identity: $!";
            $identity = <$read>; close $read;
            $identity = "" unless defined $identity;
            chomp $identity;
            last if $identity =~ /^\d+$/;
            select undef, undef, undef, 0.1;
          }
          if ($identity !~ /^\d+$/) { print $writer "VT-SERVER failed no process identity\n"; close $writer; exit 3; }
          open my $pid, ">", "$directory/pid.tmp" or die "pid: $!";
          print $pid "$game $identity\n"; close $pid;
          rename "$directory/pid.tmp", "$directory/pid" or die "pid rename: $!";
          print $writer "VT-SERVER started $game $identity\n"; close $writer;
          my $waited = waitpid($game, 0);
          my $status = $?;
          # Shell-compatible nonzero status for a signal, rather than treating it as exit 0.
          my $signal = $status & 127;
          my $code = $waited == $game ? ($signal ? 128 + $signal : $status >> 8) : -1;
          open my $exit, ">", "$directory/exit.tmp" or die "exit: $!";
          print $exit "$code\n"; close $exit;
          rename "$directory/exit.tmp", "$directory/exit" or die "exit rename: $!";
          exit 0;
        ' "$dir" "$(declare -f vt_mac_started)" /usr/bin/env ${unsets[@]+"${unsets[@]}"} ${sets[@]+"${sets[@]}"} \
          /usr/bin/arch ${archOptions[@]+"${archOptions[@]}"} "$runtime/$exe" ${args[@]+"${args[@]}"}
        """).ReplaceLineEndings("\n");

    // The server's stop and wait are MacProcess's (Stop, Wait): the same identity and verdicts as the client's and recovery's (#665).
}
