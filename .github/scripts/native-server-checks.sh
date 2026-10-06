#!/usr/bin/env bash
# The scheduled native server checks, run inside a fresh container of the private docker/linux-server image by
# .github/workflows/native-server-checks.yml. /src is the read-only checkout; builds use a writable copy.
#
#   native-server-checks.sh smoke                  LinuxServerSmoke: the server boots with BepInEx and loads a new world.
#   native-server-checks.sh acceptance [plugin-pin] The native acceptance suite's server half (dry-site-server): ValheimCLI
#                                                  and AcceptanceMod built against this server's own assemblies, a new world,
#                                                  then the pinned run. plugin-pin is the negative control: the plan pins a
#                                                  wrong MD5 for the mod, so the run must fail before the scenario.
#
# Evidence goes to ~/out/<check>. On exit, only logs and reports are copied to ~/evidence/<check> for the workflow to
# take: never the server, the runtime copies, the publicized assemblies, a world or the plan (it holds the password).
set -euo pipefail

check=${1:?usage: native-server-checks.sh smoke|acceptance [plugin-pin]}
control=${2:-none}
server=/opt/valheim/server
out=$HOME/out/$check
evidence=$HOME/evidence/$check

collect() {
  local status=$?
  mkdir -p "$evidence"
  if [ -d "$out" ]; then
    (cd "$out" && find . -type f \
        \( -name '*.json' -o -name '*.jsonl' -o -name '*.xml' -o -name '*.log' -o -name '*.absent' \) \
        ! -name plan.json ! -path './*/world/*' ! -path './world/*' ! -path './*savedir/*' ! -path '*/valheim-test-*' \
        -exec cp --parents {} "$evidence/" \;)
  fi
  cp /opt/valheim/server-buildid.txt "$evidence/"
  exit "$status"
}
trap collect EXIT

cp -r /src "$HOME/repo"
cd "$HOME/repo"
echo "Dedicated server build $(cat /opt/valheim/server-buildid.txt)"

case "$check" in
  smoke)
    dotnet run --project docker/linux-server/smoke -c Release -- "$server" "$out" 600
    ;;
  acceptance)
    mkdir -p "$out"
    # The game-side ValheimCLI from the commit cli-dependency.json pins, the same source as the transport package.
    repository=$(sed -n 's/.*"repository": *"\([^"]*\)".*/\1/p' cli-dependency.json)
    commit=$(sed -n 's/.*"commit": *"\([0-9a-f]*\)".*/\1/p' cli-dependency.json)
    cli=$HOME/valheimCLI
    git init -q "$cli"
    git -C "$cli" fetch -q --depth=1 "$repository" "$commit"
    git -C "$cli" checkout -q FETCH_HEAD

    # Game references from this server install. ValheimCLI compiles against publicized game assemblies; they are made
    # here, used for the build and discarded with the container.
    managed=$(dirname "$(find "$server" -path '*/Managed/assembly_valheim.dll' | head -n 1)")
    [ -f "$managed/assembly_valheim.dll" ] || { echo "No Managed/assembly_valheim.dll in $server" >&2; exit 1; }
    core=$server/BepInEx/core
    publicized=$HOME/publicized
    mkdir -p "$publicized"
    dotnet tool install --tool-path "$HOME/tools" BepInEx.AssemblyPublicizer.Cli --version 0.4.3
    for name in assembly_valheim assembly_guiutils assembly_utils SoftReferenceableAssets; do
      DOTNET_ROLL_FORWARD=Major "$HOME/tools/assembly-publicizer" "$managed/$name.dll" -o "$publicized/${name}_publicized.dll"
    done
    cli_build=(-c Release -p:VALHEIM_MANAGED="$managed" -p:BEPINEX_CORE="$core" -p:PUBLICIZED_PATH="$publicized")
    dotnet build "$cli/valheimCLI.csproj" "${cli_build[@]}"
    dotnet build "$cli/Packs/Standard/Valheim.Cli.Standard.csproj" "${cli_build[@]}"
    dotnet build "$cli/Packs/WorldTools/Valheim.Cli.WorldTools.csproj" "${cli_build[@]}"
    only() { # Exactly one build output, or stop.
      local found
      found=$(find "$@")
      [ "$(printf '%s\n' "$found" | grep -c .)" = 1 ] || { echo "Expected one file for: $*; found: ${found:-none}" >&2; return 1; }
      printf '%s\n' "$found"
    }
    cli_dll=$(only "$cli/bin/Release" -name valheimCLI.dll)
    standard_dll=$(only "$cli/Packs/Standard/bin/Release" -name Valheim.Cli.Standard.dll)
    worldtools_dll=$(only "$cli/Packs/WorldTools/bin/Release" -name Valheim.Cli.WorldTools.dll)

    game_build=(-c Release -p:ValheimManaged="$managed" -p:BepInExCore="$core")
    dotnet build tests/Valheim.Testing.NativeAcceptance/AcceptanceMod/AcceptanceMod.csproj "${game_build[@]}"
    dotnet build tests/Valheim.Testing.NativeAcceptance/AcceptanceMod.Adapter/AcceptanceMod.Adapter.csproj "${game_build[@]}" -p:CliDll="$cli_dll"
    mod_dll=$(only tests/Valheim.Testing.NativeAcceptance/AcceptanceMod/bin/Release -name AcceptanceMod.dll)
    adapter_dll=$(only tests/Valheim.Testing.NativeAcceptance/AcceptanceMod.Adapter/bin/Release -name AcceptanceMod.Adapter.dll)

    # The test runtime is this container's server directory: the runner and the preparation copy it before launching.
    mkdir -p "$server/BepInEx/plugins" "$server/BepInEx/config"
    cp "$cli_dll" "$standard_dll" "$worldtools_dll" "$mod_dll" "$adapter_dll" "$server/BepInEx/plugins/"
    printf '[Server]\n\nEnabled = true\nPort = 5577\n' > "$server/BepInEx/config/valheimCLI.valheimCLI.cfg"

    runner=(dotnet run --project tests/Valheim.Testing.NativeAcceptance -c Release)
    "${runner[@]}" -- prepare-server "$server" "$out/prepare"
    plan=$out/prepare/plan.json
    if [ "$control" = plugin-pin ]; then
      # Negative control: pin a wrong build of the mod. The pinned run must refuse this server before the scenario.
      mod_md5=$(md5sum "$mod_dll" | cut -d' ' -f1)
      grep -q "\"$mod_md5\"" "$plan"
      sed -i "s/\"$mod_md5\"/\"00000000000000000000000000000000\"/" "$plan"
      echo "NEGATIVE CONTROL: the plan pins valheimtesting.acceptancemod=00000000000000000000000000000000 instead of $mod_md5"
    elif [ "$control" != none ]; then
      echo "Unknown negative control: $control" >&2
      exit 2
    fi
    "${runner[@]}" --no-build -- run "$plan" "$out/run"
    ;;
  *)
    echo "Unknown check: $check" >&2
    exit 2
    ;;
esac
