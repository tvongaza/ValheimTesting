using System.Text;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>The host-side execution of one <see cref="MacSteamRuntimePackage"/> contract for an SSH Mac.</summary>
internal static class MacSteamRuntimeOnHost
{
    private static Dictionary<string, string> Variables(MacSteamRuntimePackage package) => new()
    {
        ["source"] = package.Root,
        ["manifestSha"] = package.SourceManifestSha256.ToLowerInvariant(),
        ["files"] = string.Join('\n', package.Files.OrderBy(file => file.Key, StringComparer.Ordinal)
            .Select(file => file.Value.ToLowerInvariant() + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Key)))),
    };

    internal static async Task VerifyAsync(IGameHost host, MacSteamRuntimePackage package, TimeSpan timeout,
        CancellationToken cancellation)
    {
        var result = (await host.RunAsync(Verify, Variables(package), timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Checking pinned SteamCMD libraries on {host.Name}");
        RequireReply(result, package, host.Name, "VT-STEAM-VERIFIED");
    }

    internal static async Task<IReadOnlyDictionary<string, string>> StageAsync(IGameHost host, MacSteamRuntimePackage package,
        string profile, TimeSpan timeout, CancellationToken cancellation)
    {
        var variables = Variables(package);
        variables["runtime"] = profile;
        var result = (await host.RunAsync(Stage, variables, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Signing pinned SteamCMD libraries in the disposable profile on {host.Name}");
        RequireReply(result, package, host.Name, "VT-STEAM-STAGED");
        var listing = await HostInstall.ListAsync(host, HostPath.Join(profile, "steam-runtime"), timeout, cancellation: cancellation).ConfigureAwait(false);
        if (!listing.Files.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(package.Files.Keys))
            throw new IOException($"The signed Steam runtime on {host.Name} does not contain exactly the pinned dependency closure.");
        return listing.Files;
    }

    private static void RequireReply(HostResult result, MacSteamRuntimePackage package, string host, string verdict)
    {
        if (InteractiveClient.Line(result.Stdout, verdict) == null)
            throw new HostOperationException($"The SteamCMD dependency closure on {host} did not report {verdict}", result);
        var listings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in result.Stdout.Split('\n').Where(line => line.StartsWith("VT-STEAM-OTOOL ", StringComparison.Ordinal)))
        {
            string[] pair = line.TrimEnd('\r').Split(' ', 3);
            if (pair.Length != 3) throw new HostOperationException($"The SteamCMD dependency reply from {host} is incomplete", result);
            string relative, output;
            try
            {
                relative = Encoding.UTF8.GetString(Convert.FromBase64String(pair[1]));
                output = Encoding.UTF8.GetString(Convert.FromBase64String(pair[2]));
            }
            catch (FormatException error) { throw new InvalidDataException($"The SteamCMD dependency reply from {host} is malformed.", error); }
            if (!listings.TryAdd(relative, output))
                throw new InvalidDataException($"The SteamCMD dependency reply from {host} repeats {relative}.");
        }
        package.RequireRemoteClosure(listings);
    }

    // Verify every source byte and arm64 slice before the stage creates its first file. Repeat in Stage immediately
    // before copying, so a changed SteamCMD install cannot cross the gap between preflight and preparation.
    internal static readonly string Verify = """
        set -eu
        [ "$(uname -s)" = Darwin ] || { echo 'VT-STEAM-RUNTIME unsupported'; exit 4; }
        [ -d "$source" ] && [ ! -L "$source" ] || { echo 'VT-STEAM-RUNTIME source'; exit 4; }
        manifest="$source/package/steam_cmd_osx.manifest"
        [ -f "$manifest" ] && [ ! -L "$manifest" ] || { echo 'VT-STEAM-RUNTIME manifest'; exit 4; }
        actual=$(shasum -a 256 "$manifest" | awk '{print $1}')
        [ "$actual" = "$manifestSha" ] || { echo 'VT-STEAM-RUNTIME manifest-changed'; exit 4; }
        while read -r expected encoded; do
          [ -n "$expected" ] || continue
          relative=$(printf '%s' "$encoded" | base64 -D)
          part="$source"
          IFS=/ read -ra components <<< "$relative"
          for component in "${components[@]}"; do
            part="$part/$component"
            [ ! -L "$part" ] || { echo 'VT-STEAM-RUNTIME linked'; exit 4; }
          done
          [ -f "$part" ] || { echo 'VT-STEAM-RUNTIME missing'; exit 4; }
          actual=$(shasum -a 256 "$part" | awk '{print $1}')
          [ "$actual" = "$expected" ] || { echo 'VT-STEAM-RUNTIME changed'; exit 4; }
          archs=$(/usr/bin/lipo -archs "$part")
          case " $archs " in *' arm64 '*) ;; *) echo 'VT-STEAM-RUNTIME architecture'; exit 4 ;; esac
          dependencies=$(/usr/bin/otool -L "$part")
          printf 'VT-STEAM-OTOOL %s %s\n' "$encoded" "$(printf '%s' "$dependencies" | base64 | tr -d '\n')"
        done <<< "$files"
        echo 'VT-STEAM-VERIFIED'
        """.ReplaceLineEndings("\n");

    internal static readonly string Stage = Verify + "\n" + """
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
