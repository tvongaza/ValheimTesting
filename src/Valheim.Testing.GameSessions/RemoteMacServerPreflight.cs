using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>Read-only readiness for the profile stage's SSH Mac server backend.</summary>
internal static class RemoteMacServerPreflight
{
    internal static async Task InspectAsync(IGameHost host, GameRole role, TimeSpan timeout, CancellationToken cancellation)
    {
        string saveRoot = await HostedWorldOnHost.MacSaveRootAsync(host, timeout, cancellation).ConfigureAwait(false);
        var result = (await host.RunAsync(Script, new Dictionary<string, string>
        {
            ["source"] = role.Install, ["runtime"] = role.Runtime, ["saveRoot"] = saveRoot,
        }, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Checking the remote Apple Silicon server on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-MAC-SERVER-READY") == null)
            throw new HostOperationException($"The remote Mac server on {host.Name} did not report readiness", result);
    }

    internal static readonly string Script = """
        set -eu
        [ "$(uname -s)" = Darwin ] && [ "$(uname -m)" = arm64 ] || { echo 'VT-MAC-SERVER unsupported architecture'; exit 4; }
        for tool in /usr/bin/arch /usr/bin/lipo /usr/bin/codesign /usr/bin/otool /usr/bin/perl /usr/bin/shasum; do
          [ -x "$tool" ] || { echo "VT-MAC-SERVER missing $tool"; exit 4; }
        done
        game="$source/valheim_server/Valheim"
        [ -x "$game" ] || { echo 'VT-MAC-SERVER missing executable'; exit 4; }
        archs=$(/usr/bin/lipo -archs "$game")
        case " $archs " in *' arm64 '*) ;; *) echo 'VT-MAC-SERVER executable lacks arm64'; exit 4 ;; esac
        writable_ancestor() {
          path=$1
          while [ ! -e "$path" ]; do
            parent=$(dirname "$path")
            [ "$parent" != "$path" ] || return 1
            path=$parent
          done
          [ -d "$path" ] && [ -w "$path" ] && [ ! -L "$path" ]
        }
        writable_ancestor "$runtime" || { echo 'VT-MAC-SERVER profile is not writable'; exit 4; }
        writable_ancestor "$saveRoot" || { echo 'VT-MAC-SERVER save root is not writable'; exit 4; }
        parent=$runtime
        while [ ! -e "$parent" ]; do parent=$(dirname "$parent"); done
        free=$(df -Pk "$parent" | awk 'NR==2 {print $4}')
        [ "${free:-0}" -ge 524288 ] || { echo 'VT-MAC-SERVER profile needs 512 MiB free'; exit 4; }
        echo VT-MAC-SERVER-READY
        """.ReplaceLineEndings("\n");
}
