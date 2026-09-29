#!/bin/bash
# Game-free tests of the image's helpers. Run inside the image as root, with an init process:
#   docker run --rm --init -v "$PWD/docker/linux-client/tests:/tests:ro" <image> bash /tests/helpers.sh
# An inert shell script stands in for the game: nothing is downloaded, no Steam login, no display. Exit 1 on any failure.
set -uo pipefail
n=0; fails=0
t() { local name=$1; shift; n=$((n + 1)); if eval "$*"; then echo "ok   $n $name"; else fails=$((fails + 1)); echo "FAIL $n $name"; echo "     rc=$RC output: ${OUT//$'\n'/ | }"; fi; }
run() { OUT=$("$@" 2>&1); RC=$?; }
starttime() { local s; s=$(cat "/proc/$1/stat" 2>/dev/null) || return 1; s=${s##*) }; set -- $s; echo "${20}"; }
gone() { local s; s=$(cat "/proc/$1/stat" 2>/dev/null) || return 0; s=${s##*) }; [ "${s%% *}" = Z ]; }

echo "# vt-stage-character"
C=/home/steam/.config/unity3d/IronGate/Valheim/characters_local
run bash -c 'printf one | vt-stage-character roadtester'
t "stages a new character, owned by steam" '[ $RC = 0 ] && [ "$(cat $C/roadtester.fch)" = one ] && [ "$(stat -c %U $C/roadtester.fch)" = steam ]'
run bash -c 'printf two | vt-stage-character roadtester'
t "keeps an existing character without --replace" '[ $RC = 1 ] && [ "$(cat $C/roadtester.fch)" = one ]'
run vt-stage-character roadtester < /dev/null
t "empty input keeps the existing character" '[ $RC = 1 ] && [ "$(cat $C/roadtester.fch)" = one ]'
run vt-stage-character --replace roadtester < /dev/null
t "empty input with --replace keeps the existing character" '[ $RC = 1 ] && [ "$(cat $C/roadtester.fch)" = one ]'
run vt-stage-character --replace roadtester < /
t "failed input with --replace keeps the existing character" '[ $RC = 1 ] && [ "$(cat $C/roadtester.fch)" = one ]'
run bash -c 'printf two | vt-stage-character --replace roadtester'
t "--replace replaces the character" '[ $RC = 0 ] && [ "$(cat $C/roadtester.fch)" = two ]'
run vt-stage-character fresh < /dev/null
t "empty input stages nothing" '[ $RC = 1 ] && [ ! -e $C/fresh.fch ]'
for bad in ../escape a/b '' .hidden 'x y'; do
    run bash -c 'printf data | vt-stage-character "$1"' _ "$bad"
    t "refuses the name '$bad'" '[ $RC = 2 ] && [ ! -e $C/../escape.fch ] && [ ! -e "$C/$bad.fch" ]'
done
run bash -c 'printf data | vt-stage-character one two'
t "refuses two names" '[ $RC = 2 ]'
t "leaves no temporary files" '[ -z "$(find $C -name ".*" -type f)" ]'

echo "# vt-launch-valheim, vt-stop-game, vt-pids, vt-stop-all (inert game)"
G=/tmp/vt-game; L=/home/steam/.config/unity3d/IronGate/Valheim/Player.log
mkdir -p "$G/decoy" "$G/long" /home/steam/.local/share/Steam/logs /tmp/vt-empty
echo "[2026-01-01 00:00:00] [Logged On] test" > /home/steam/.local/share/Steam/logs/connection_log.txt
cat > "$G/valheim.x86_64" <<'GAME'
#!/bin/bash
# Inert game: records its environment, renames itself (a name with a space, like a renamed Unity thread) and acts out $G/mode.
G=$(pwd); L=/home/steam/.config/unity3d/IronGate/Valheim/Player.log
env > "$G/env.$$"
printf 'Unity Main' > /proc/self/comm
mode=$(cat "$G/mode")
if [ "$mode" = steamworks-once ] && [ ! -e "$G/attempted" ]; then
    : > "$G/attempted"
    # A process with the game's name that the launch did not start: the retry must leave it running.
    "$G/decoy/valheim.x86_64" 600 & echo $! > "$G/decoy.pid"
    echo "Steamworks is not initialized" >> "$L"
    while :; do sleep 1; done
fi
# Exits after the launcher has recorded it (an instant exit would read as "did not start").
[ "$mode" = exit ] && { sleep 1; exit 1; }
echo "Starting music menu" >> "$L"
while :; do sleep 1; done
GAME
cp /bin/sleep "$G/decoy/valheim.x86_64"; cp /bin/bash "$G/long/valheim.x86_64"
chown -R steam:steam "$G" /home/steam/.local; chmod +x "$G/valheim.x86_64"
export VT_GAME_DIR=$G VT_STEAM_SETTLE=0 VT_RETRY_DELAY=0
reset() { rm -f "$G"/env.* "$G/attempted" "$L"; echo "$1" > "$G/mode"; }
lastpid() { printf '%s\n' "$OUT" | tail -1; }

reset menu
VT_GAME_DIR=/tmp/vt-empty run vt-launch-valheim
t "refuses without a client" '[ $RC = 2 ] && [[ $OUT == *"no client"* ]]'
run vt-launch-valheim
t "refuses without BepInEx and starts nothing" '[ $RC = 2 ] && [[ $OUT == *BepInEx.Preloader.dll* ]] && [[ $OUT == *libdoorstop_x64.so* ]] && [ -z "$(ls $G/env.* 2>/dev/null)" ]'
mkdir -p "$G/BepInEx/core" "$G/doorstop_libs"; : > "$G/BepInEx/core/BepInEx.Preloader.dll"; chown -R steam:steam "$G"
run vt-launch-valheim
t "refuses without the Doorstop library" '[ $RC = 2 ] && [[ $OUT == *libdoorstop_x64.so* ]] && [[ $OUT != *Preloader* ]] && [ -z "$(ls $G/env.* 2>/dev/null)" ]'
rm "$G/BepInEx/core/BepInEx.Preloader.dll"; : > "$G/doorstop_libs/libdoorstop_x64.so"; chown -R steam:steam "$G"
run vt-launch-valheim
t "refuses without the BepInEx preloader" '[ $RC = 2 ] && [[ $OUT == *BepInEx.Preloader.dll* ]] && [[ $OUT != *doorstop_x64* ]] && [ -z "$(ls $G/env.* 2>/dev/null)" ]'
run vt-launch-valheim --env 'not a name'
t "refuses an invalid --env name" '[ $RC = 2 ]'
run vt-launch-valheim --env VT_TEST_UNSET
t "refuses an unset --env variable" '[ $RC = 2 ]'

reset menu
VT_TEST_SECRET=s3 run vt-launch-valheim --vanilla --env VT_TEST_SECRET; P=$(lastpid)
t "--vanilla launches without BepInEx" '[ $RC = 0 ] && [ -f "$G/env.$P" ] && ! grep -q DOORSTOP "$G/env.$P" && ! grep -q LD_PRELOAD "$G/env.$P"'
t "--env passes the named variable" 'grep -qx VT_TEST_SECRET=s3 "$G/env.$P"'
t "records the identity of the process it started" '[ "$(cat /home/steam/valheim.identity)" = "$P $(starttime $P)" ]'
run vt-launch-valheim --vanilla
t "refuses a second client" '[ $RC = 3 ]'
run vt-stop-game 5
t "vt-stop-game stops the launched client" '[ $RC = 0 ] && gone $P && [ ! -e /home/steam/valheim.identity ]'

reset menu
: > "$G/BepInEx/core/BepInEx.Preloader.dll"; chown -R steam:steam "$G"
run vt-launch-valheim; P=$(lastpid)
t "launches with the BepInEx loader variables" '[ $RC = 0 ] && grep -qx DOORSTOP_ENABLED=1 "$G/env.$P" && grep -qx "DOORSTOP_TARGET_ASSEMBLY=$G/BepInEx/core/BepInEx.Preloader.dll" "$G/env.$P" && grep -qx LD_PRELOAD=libdoorstop_x64.so "$G/env.$P"'
run vt-stop-game 5
t "stops it again" '[ $RC = 0 ] && gone $P'

reset exit
run vt-launch-valheim
t "reports a game that exits" '[ $RC = 1 ] && [[ $OUT == *"game exited"* ]]'

reset steamworks-once
run vt-launch-valheim; P=$(lastpid)
first=$(ls "$G"/env.* | grep -v "/env\.$P\$"); first=${first##*.}; D=$(cat "$G/decoy.pid" 2>/dev/null)
t "retries an early failure and reaches the menu" '[ $RC = 0 ] && [[ $OUT == *"early failure (Steamworks is not initialized); attempt 2"* ]] && [ "$first" != "$P" ]'
t "the retry stopped only the failed attempt" 'gone $first && [ -n "$D" ] && ! gone $D'
run vt-stop-game 5
t "stops the retried client" '[ $RC = 0 ] && gone $P'
echo "$D 1" > /home/steam/valheim.identity
run vt-stop-game 5
t "vt-stop-game leaves a PID whose start time differs" '[ $RC = 0 ] && ! gone $D'
reset menu
run vt-launch-valheim --vanilla
t "refuses to launch next to a same-name process" '[ $RC = 3 ] && [ -z "$(ls $G/env.* 2>/dev/null)" ]'

# argv[0] identifies a renamed process even behind a long command line.
"$G/long/valheim.x86_64" -c 'printf "Unity Main" > /proc/self/comm; sleep 600; :' x "$(head -c 100000 /dev/zero | tr '\0' a)" & LONG=$!
for _ in 1 2 3 4 5 6 7 8 9 10; do [ "$(cat /proc/$LONG/comm)" = "Unity Main" ] && break; sleep 0.1; done
run vt-pids valheim.x86_64
t "vt-pids finds renamed processes by argv[0], also behind a long command line" '[ $RC = 0 ] && grep -qx "$D" <<<"$OUT" && grep -qx "$LONG" <<<"$OUT"'
run vt-stop-all valheim.x86_64 5
t "vt-stop-all stops every process of that name" '[ $RC = 0 ] && gone $D && gone $LONG'
run vt-pids valheim.x86_64
t "vt-pids exits 1 when none is left" '[ $RC = 1 ] && [ -z "$OUT" ]'

echo "# $((n - fails))/$n passed"
[ $fails = 0 ]
