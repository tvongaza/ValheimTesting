using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>Runs the <see cref="MacSteamRuntimePackage"/> host contract on a local or SSH Mac.</summary>
internal static class MacSteamRuntimeOnHost
{
    internal static async Task VerifyAsync(IGameHost host, MacSteamRuntimePackage package, TimeSpan timeout,
        CancellationToken cancellation)
    {
        var result = (await host.RunAsync(MacSteamRuntimePackage.VerificationScript, package.VerificationVariables(),
            timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Checking pinned SteamCMD libraries on {host.Name}");
        try { package.RequireVerificationReply(result.Stdout, host.Name); }
        catch (InvalidDataException error) { throw new HostOperationException(error.Message, result); }
    }

    internal static async Task<IReadOnlyDictionary<string, string>> StageAsync(IGameHost host, MacSteamRuntimePackage package,
        string profile, TimeSpan timeout, CancellationToken cancellation)
    {
        var variables = package.VerificationVariables().ToDictionary(pair => pair.Key, pair => pair.Value);
        variables["runtime"] = profile;
        var result = (await host.RunAsync(Stage, variables, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Signing pinned SteamCMD libraries in the disposable profile on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-STEAM-STAGED") == null)
            throw new HostOperationException($"The SteamCMD profile stage on {host.Name} did not report VT-STEAM-STAGED", result);
        var listing = await HostInstall.ListAsync(host, HostPath.Join(profile, "steam-runtime"), timeout, cancellation: cancellation).ConfigureAwait(false);
        if (!listing.Files.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(package.Files.Keys))
            throw new IOException($"The signed Steam runtime on {host.Name} does not contain exactly the pinned dependency closure.");
        return listing.Files;
    }

    // Copy, thin and sign only. The caller executes the package's one verifier before this script and again after it.
    internal static readonly string Stage = """
        set -eu
        target="$runtime/steam-runtime"
        [ -d "$runtime" ] && [ ! -e "$target" ] || { echo 'VT-STEAM-RUNTIME exists'; exit 4; }
        mkdir -p -- "$target"
        while read -r expected encoded; do
          [ -n "$expected" ] || continue
          relative=$(printf '%s' "$encoded" | base64 -D)
          sourceFile="$source/$relative"
          output="$target/$relative"
          mkdir -p -- "$(dirname "$output")"
          archs=$(/usr/bin/lipo -archs "$sourceFile")
          if [ "$archs" = arm64 ]; then cp "$sourceFile" "$output"
          else /usr/bin/lipo -thin arm64 "$sourceFile" -output "$output"; fi
          /usr/bin/codesign --force --sign - --timestamp=none "$output"
          /usr/bin/codesign --verify "$output"
        done <<< "$files"
        echo 'VT-STEAM-STAGED'
        """.ReplaceLineEndings("\n");
}
