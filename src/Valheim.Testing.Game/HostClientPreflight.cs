using System.Text;

namespace Valheim.Testing.Game;

// Reads only the few loader and CLI settings needed before a campaign client starts. The remote host remains the authority
// for its files; no game install or host-global expectation file is copied into the runner's workspace.
internal static class HostClientPreflight
{
    internal static async Task CheckAsync(IGameHost host, string install, ClientPlatform platform, ClientRunPlan plan,
        TimeSpan timeout, CancellationToken cancellation)
    {
        if (platform == ClientPlatform.Windows)
        {
            byte[] proxy = await Required(host, HostInstall.Join(install, BepInExLoader.WindowsProxy), timeout, cancellation).ConfigureAwait(false);
            byte[] config = await Required(host, HostInstall.Join(install, BepInExLoader.WindowsConfig), timeout, cancellation).ConfigureAwait(false);
            BepInExLoader.RequireWindowsLoader(proxy, Encoding.UTF8.GetString(config), install, $"client install on {host.Name}");
        }

        string cliConfig = HostInstall.Join(install, "BepInEx/config/" + OwnedClientPreflight.CliConfig);
        byte[]? configBytes = await Read(host, cliConfig, timeout, cancellation).ConfigureAwait(false);
        if (configBytes == null) return;
        string configText = Encoding.UTF8.GetString(configBytes);
        string configured = OwnedClientPreflight.IniValue(configText.Split('\n'), "Expectations", "File")?.Trim() ?? "";
        if (configured.Length == 0) return;
        if (configured.StartsWith('/') || configured.StartsWith('\\') || configured.Contains(':') ||
            configured.Split('/', '\\').Any(part => part is ".." or "." or ""))
            throw new InvalidOperationException($"ValheimCLI on {host.Name} points at '{configured}' outside its disposable client config. " +
                "Use a per-run standing file under BepInEx/config derived from the staged plugins and world UID; do not inherit a host-global standing file.");
        string standingPath = HostInstall.Join(install, "BepInEx/config/" + configured.Replace('\\', '/'));
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

    private static async Task<byte[]> Required(IGameHost host, string path, TimeSpan timeout, CancellationToken cancellation) =>
        await Read(host, path, timeout, cancellation).ConfigureAwait(false) ??
        throw new FileNotFoundException($"The client on {host.Name} lacks {path}; install a coherent BepInExPack before launch.", path);

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
