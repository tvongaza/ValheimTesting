using System.Text;

namespace Valheim.Testing.Game;

// Reads only the few loader and CLI settings needed before a campaign client starts. The remote host remains the authority
// for its files; no game install or host-global expectation file is copied into the runner's workspace.
internal static class HostClientPreflight
{
    internal static async Task CheckAsync(IGameHost host, string install, ClientPlatform platform, ClientRunPlan plan,
        TimeSpan timeout, CancellationToken cancellation)
    {
        // A remote client is Windows or Linux (a macOS client runs locally). Every loader file in one existence check, so a
        // refusal names all that are missing; then, on Windows, the proxy and configuration as one Doorstop version.
        var present = await ExistingAsync(host, install, BepInExLoader.LoaderFiles(platform), timeout, cancellation).ConfigureAwait(false);
        BepInExLoader.RequireLoaderFiles(platform, present.Contains, $"client install on {host.Name}");
        if (platform == ClientPlatform.Windows)
            await RequireWindowsLoaderAsync(host, install, $"client install on {host.Name}", timeout, cancellation).ConfigureAwait(false);

        string cliConfig = HostPath.Join(install, "BepInEx/config/" + OwnedClientPreflight.CliConfig);
        byte[]? configBytes = await Read(host, cliConfig, timeout, cancellation).ConfigureAwait(false);
        if (configBytes == null) return;
        string configText = Encoding.UTF8.GetString(configBytes);
        string configured = OwnedClientPreflight.IniValue(configText.Split('\n'), "Expectations", "File")?.Trim() ?? "";
        if (configured.Length == 0) return;
        if (configured.StartsWith('/') || configured.StartsWith('\\') || configured.Contains(':') ||
            configured.Split('/', '\\').Any(part => part is ".." or "." or ""))
            throw new InvalidOperationException($"ValheimCLI on {host.Name} points at '{configured}' outside its disposable client config. " +
                "Use a per-run standing file under BepInEx/config derived from the staged plugins and world UID; do not inherit a host-global standing file.");
        string standingPath = HostPath.Join(install, "BepInEx/config/" + configured.Replace('\\', '/'));
        byte[]? standingBytes = await Read(host, standingPath, timeout, cancellation).ConfigureAwait(false);
        if (standingBytes == null)
            throw new InvalidOperationException($"ValheimCLI's standing file {standingPath} does not exist on {host.Name}; it would refuse every command after launch.");
        IReadOnlyList<KeyValuePair<string, string>> standing;
        try { standing = StandingPins.Parse(Encoding.UTF8.GetString(standingBytes).Split('\n'), standingPath); }
        catch (ArgumentException error) { throw new InvalidOperationException($"ValheimCLI's standing file on {host.Name} is malformed: {error.Message}", error); }
        bool strict = string.Equals(OwnedClientPreflight.IniValue(configText.Split('\n'), "Expectations", "Strict"), "true", StringComparison.OrdinalIgnoreCase);
        OwnedClientPreflight.RequireStandingPins(standing, plan.Pins, plan.HostWorld?.WorldUid, null,
            strict, $"ValheimCLI's standing file on {host.Name}");
    }

    /// <summary>
    /// Reads a Windows install's Doorstop proxy and configuration on its host and requires them to be one coherent Doorstop
    /// version (<see cref="BepInExLoader.RequireWindowsLoader(byte[], string, string, string)"/>): the one remote Windows loader
    /// check, for a campaign's source install, a remote client before launch and a copied server runtime.
    /// </summary>
    internal static async Task RequireWindowsLoaderAsync(IGameHost host, string root, string kind, TimeSpan timeout, CancellationToken cancellation)
    {
        string proxyPath = HostPath.Join(root, BepInExLoader.WindowsProxy), configPath = HostPath.Join(root, BepInExLoader.WindowsConfig);
        byte[] proxy = await Read(host, proxyPath, timeout, cancellation).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"The {kind} has no {BepInExLoader.WindowsProxy}; install a coherent BepInExPack.", proxyPath);
        byte[] config = await Read(host, configPath, timeout, cancellation).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"The {kind} has no {BepInExLoader.WindowsConfig}; install a coherent BepInExPack.", configPath);
        BepInExLoader.RequireWindowsLoader(proxy, Encoding.UTF8.GetString(config), root, kind);
    }

    /// <summary>Which of <paramref name="relative"/> (paths with <c>/</c> under <paramref name="root"/>) exist as files on the host, in one round trip.</summary>
    internal static async Task<HashSet<string>> ExistingAsync(IGameHost host, string root, IEnumerable<string> relative, TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? PowerShellExists : BashExists,
            new Dictionary<string, string> { ["root"] = root, ["paths"] = string.Join('\n', relative) }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Checking client files on {host.Name}");
        var lines = result.Stdout.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (!lines.Contains("VT-EXISTS done")) throw new HostOperationException($"Unexpected file check reply from {host.Name}", result);
        return lines.Where(line => line.StartsWith("VT-EXISTS file ", StringComparison.Ordinal)).Select(line => line["VT-EXISTS file ".Length..]).ToHashSet(StringComparer.Ordinal);
    }

    internal static readonly string BashExists = """
        while IFS= read -r relative; do
          [ -n "$relative" ] && [ -f "$root/$relative" ] && echo "VT-EXISTS file $relative"
        done <<< "$paths"
        echo 'VT-EXISTS done'
        """.ReplaceLineEndings("\n");
    internal static readonly string PowerShellExists = """
        foreach ($relative in ($paths -split "`n")) {
            if ($relative -and [IO.File]::Exists([IO.Path]::Combine($root, $relative))) { "VT-EXISTS file $relative" }
        }
        'VT-EXISTS done'
        """.ReplaceLineEndings("\n");

    internal static async Task<byte[]?> Read(IGameHost host, string path, TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? PowerShellRead : BashRead,
            new Dictionary<string, string> { ["path"] = path }, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Reading client preflight file on {host.Name}");
        string line = result.Stdout.Trim();
        if (line == "VT-PREFLIGHT missing") return null;
        if (line == "VT-PREFLIGHT too-large") throw new IOException($"Client preflight file {path} on {host.Name} exceeds the 4 MiB safety limit.");
        if (!line.StartsWith("VT-PREFLIGHT ", StringComparison.Ordinal)) throw new HostOperationException($"Unexpected client preflight reply from {host.Name}", result);
        try { return Convert.FromBase64String(line["VT-PREFLIGHT ".Length..]); }
        catch (FormatException) { throw new HostOperationException($"Malformed client preflight reply from {host.Name}", result); }
    }

    internal static readonly string BashRead = """
        if [ ! -f "$path" ]; then echo 'VT-PREFLIGHT missing'; exit 0; fi
        size=$(wc -c < "$path") || exit 3
        if [ "$size" -gt 4194304 ]; then echo 'VT-PREFLIGHT too-large'; exit 0; fi
        printf 'VT-PREFLIGHT '; base64 < "$path" | tr -d '\n'; printf '\n'
        """.ReplaceLineEndings("\n");
    internal static readonly string PowerShellRead = """
        if (-not [IO.File]::Exists($path)) { 'VT-PREFLIGHT missing'; exit 0 }
        $file = New-Object IO.FileInfo $path
        if ($file.Length -gt 4194304) { 'VT-PREFLIGHT too-large'; exit 0 }
        'VT-PREFLIGHT ' + [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))
        """.ReplaceLineEndings("\n");
}
