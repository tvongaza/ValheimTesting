using System.Text;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>A reviewed local file copied to one path in a disposable host runtime.</summary>
public sealed record HostedRuntimeFile(string Source, string RelativePath);

/// <summary>The game process whose executable must be present in the source install.</summary>
public enum HostedRuntimeKind { Server, Client }

/// <summary>
/// Builds a disposable modded runtime on a remote game host. The source install is read only: the host makes a fresh
/// copy, then replaces only that copy's BepInEx plugin, script, config and patcher content with explicitly selected
/// files. The caller owns the host lock and the resulting copy's cleanup.
/// </summary>
public static class HostedRuntimeStage
{
    private static readonly StringComparer HostNames = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex SafePath = new(@"^[A-Za-z0-9_. -]+(?:/[A-Za-z0-9_. -]+)*$", RegexOptions.CultureInvariant);

    /// <summary>Refuse conflicting client use or a process executing from the owned runtime; unrelated servers may stay up.</summary>
    internal static async Task RequireStoppedAsync(IGameHost host, TimeSpan timeout, CancellationToken cancellation = default,
        string runtime = "", bool clientSession = true)
    {
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsProcessCheck : BashProcessCheck,
            new Dictionary<string, string> { ["runtime"] = runtime, ["clientSession"] = clientSession ? "true" : "false" },
            timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Checking conflicting game processes on {host.Name}");
        string state = InteractiveClient.Line(result.Stdout, "VT-GAME ") ??
            throw new HostOperationException($"No game-process verdict from {host.Name}", result);
        if (state == "busy") throw new InvalidOperationException($"A conflicting Valheim client or owned-runtime process is running on {host.Name}; no files were changed.");
        if (state != "idle") throw new HostOperationException($"Unexpected game-process verdict from {host.Name}", result);
    }

    internal static readonly string WindowsProcessCheck = """
        $busy = $false
        foreach ($p in @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object { $_.Name -in @('Valheim.exe', 'valheim_server.exe') })) {
            if ($clientSession -eq 'true' -and $p.Name -eq 'Valheim.exe') { $busy = $true }
            if ($runtime) {
                if (-not $p.ExecutablePath) { throw 'Cannot establish the running game executable path' }
                if ($p.ExecutablePath.StartsWith($runtime.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { $busy = $true }
            }
        }
        if ($busy) { 'VT-GAME busy' } else { 'VT-GAME idle' }
        """;
    // ps errors remain errors. A dedicated server elsewhere does not use the client's character files.
    internal static readonly string BashProcessCheck = """
        processes=$(ps -axo comm=) || exit 4
        if printf '%s\n' "$processes" | awk -v root="$runtime" -v client="$clientSession" '
          { path=$0; sub(/^[[:space:]]+/, "", path); name=path; sub(/^.*\//, "", name);
            game=(name=="Valheim" || name=="valheim.x86_64" || index(name,"valheim_server")==1);
            if (client=="true" && (name=="Valheim" || name=="valheim.x86_64")) busy=1;
            if (game && root!="" && index(path,root "/")==1) busy=1;
            if (game && root!="" && index(path,"/")!=1) unknown=1 }
          END { if (unknown) print "VT-GAME unknown"; else if (busy) print "VT-GAME busy"; else print "VT-GAME idle" }'; then :; else exit 4; fi
        """;

    /// <summary>
    /// Selects the exact mod, dependency and coherent ValheimCLI DLLs of one ready lock for one process. Resolve
    /// separate locks for the server and each client: this deliberately does not copy server-only mods to clients.
    /// Extra config, script or asset files remain explicit <see cref="HostedRuntimeFile"/> entries in the campaign.
    /// </summary>
    public static IReadOnlyList<HostedRuntimeFile> FromDependencies(string dependencyLockFile)
    {
        var dependencies = NativeDependencyLock.ReadReady(dependencyLockFile);
        var selected = new Dictionary<string, HostedRuntimeFile>(HostNames);
        foreach (var file in dependencies.Mods.Concat(dependencies.Plugins).Concat(dependencies.CliFiles))
        {
            if (!File.Exists(file.File) || !FileHash.Sha256(file.File).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A pinned dependency changed before staging: " + file.File);
            string name = "BepInEx/plugins/" + Path.GetFileName(file.File);
            if (!selected.TryAdd(name, new HostedRuntimeFile(file.File, name)))
                throw new InvalidDataException("Two selected dependencies would install as " + name + "; rename or resolve the conflict before launch.");
        }
        return selected.Values.ToArray();
    }

    /// <summary>Read-only source-install eligibility, shared by campaign preflight and the later copy.</summary>
    internal static async Task<HostListing> InspectSourceAsync(IGameHost host, HostedRuntimeKind kind, string source,
        BepInExLoaderPackage? loaderPackage, TimeSpan timeout, CancellationToken cancellation = default)
    {
        loaderPackage?.Validate();
        if (loaderPackage != null && loaderPackage.Files.ContainsKey(BepInExLoader.WindowsProxy) !=
            (host.Shell.Kind == HostShellKind.PowerShell))
            throw new InvalidDataException("The reviewed loader package does not match the host platform.");
        var sourceListing = await HostInstall.ListAsync(host, source, timeout, cancellation: cancellation).ConfigureAwait(false);
        if (kind == HostedRuntimeKind.Server) _ = HostInstall.DetectServer(sourceListing);
        else
        {
            bool windows = sourceListing.Files.ContainsKey(ClientLaunch.WindowsExecutable);
            bool linux = sourceListing.Files.ContainsKey(ClientLaunch.LinuxExecutable);
            bool mac = sourceListing.Files.ContainsKey("Valheim.app/Contents/MacOS/Valheim");
            if ((windows ? 1 : 0) + (linux ? 1 : 0) + (mac ? 1 : 0) != 1 || sourceListing.Files.ContainsKey(ServerLaunch.WindowsExecutable) ||
                sourceListing.Files.ContainsKey(ServerLaunch.LinuxExecutable))
                throw new InvalidOperationException($"The source install on {host.Name} must contain exactly one Windows, Linux or macOS client executable and no server executable.");
            if (windows != (host.Shell.Kind == HostShellKind.PowerShell))
                throw new InvalidOperationException($"The client install on {host.Name} does not match its host platform.");
        }
        if (loaderPackage == null)
        {
            _ = HostInstall.Pins(sourceListing); // Also refuses a nested BepInEx/core/core.
            // Every platform's loader files from the listing; a Windows proxy and configuration also as one Doorstop version.
            var platform = host.Shell.Kind == HostShellKind.PowerShell ? ClientPlatform.Windows
                : sourceListing.Files.ContainsKey("Valheim.app/Contents/MacOS/Valheim") ? ClientPlatform.MacOS : ClientPlatform.Linux;
            BepInExLoader.RequireLoaderFiles(platform, sourceListing.Files.ContainsKey, $"source install on {host.Name}");
            if (platform == ClientPlatform.Windows)
                await HostClientPreflight.RequireWindowsLoaderAsync(host, source, $"source install on {host.Name}", timeout, cancellation).ConfigureAwait(false);
        }
        return sourceListing;
    }

    public static Task<HostListing> PrepareAsync(IGameHost host, HostedRuntimeKind kind, string source, string destination, string staging,
        IReadOnlyList<HostedRuntimeFile> files, TimeSpan timeout, CancellationToken cancellation = default,
        BepInExLoaderPackage? loaderPackage = null) =>
        PrepareWithInspectedSourceAsync(host, kind, source, destination, staging, files, timeout, cancellation, loaderPackage, null);

    internal static async Task<HostListing> PrepareWithInspectedSourceAsync(IGameHost host, HostedRuntimeKind kind,
        string source, string destination, string staging, IReadOnlyList<HostedRuntimeFile> files, TimeSpan timeout,
        CancellationToken cancellation, BepInExLoaderPackage? loaderPackage, HostListing? inspectedSource)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(files);
        HostInstall.RequireHostPath(host, source, nameof(source));
        HostInstall.RequireHostPath(host, destination, nameof(destination));
        HostInstall.RequireHostPath(host, staging, nameof(staging));
        foreach (string path in new[] { source, destination, staging })
            if (path.Split('/', '\\').Any(part => part is "." or ".."))
                throw new ArgumentException("Runtime paths cannot contain . or .. segments.");
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (host.Shell.Kind is not (HostShellKind.Bash or HostShellKind.PowerShell))
            throw new PlatformNotSupportedException("Runtime preparation needs a bash or PowerShell host.");
        if (files.Count == 0) throw new ArgumentException("Select at least one pinned plugin file.", nameof(files));
        // The two paths must be distinct new siblings. Never stage into the source or an existing runtime.
        string stageSuffix = host.Shell.Kind == HostShellKind.PowerShell ? @"\staging" : "/staging";
        if (!staging.EndsWith(stageSuffix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(staging[..^stageSuffix.Length], destination[..destination.LastIndexOfAny(['/', '\\'])],
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The staging directory must be a new sibling named staging beside the disposable runtime.", nameof(staging));
        string Canonical(string path) => path.Replace('\\', '/').TrimEnd('/');
        string from = Canonical(source), to = Canonical(destination), stage = Canonical(staging);
        if (from.Equals(to, StringComparison.OrdinalIgnoreCase) || to.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase) ||
            from.StartsWith(to + "/", StringComparison.OrdinalIgnoreCase) ||
            from.Equals(stage, StringComparison.OrdinalIgnoreCase) || stage.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The source install cannot be a staging or destination directory.");

        var selected = new Dictionary<string, (string Source, string Sha)>(HostNames);
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.RelativePath) || !SafePath.IsMatch(file.RelativePath) ||
                file.RelativePath.Split('/').Any(part => part is "." or "..") || file.RelativePath.StartsWith(' ') ||
                !(file.RelativePath.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) ||
                  file.RelativePath.StartsWith("BepInEx/scripts/", StringComparison.OrdinalIgnoreCase) ||
                  file.RelativePath.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase) ||
                  file.RelativePath.StartsWith("BepInEx/patchers/", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Staged paths must be regular files under BepInEx/plugins, scripts, config or patchers. Use loaderPackage to replace the copied loader; never pass loader files as plugins.", nameof(files));
            string local = Path.GetFullPath(file.Source);
            if (!File.Exists(local) || (File.GetAttributes(local) & FileAttributes.ReparsePoint) != 0)
                throw new FileNotFoundException("A selected runtime file is missing or linked: " + local, local);
            if (!selected.TryAdd(file.RelativePath, (local, FileHash.Sha256(local))))
                throw new ArgumentException("Two selected runtime files have the same target path: " + file.RelativePath, nameof(files));
        }
        // Use the existing reviewed package contract, not a hand-repaired source install.
        loaderPackage?.Validate();
        if (loaderPackage != null)
        {
            bool windowsPackage = loaderPackage.Files.ContainsKey(BepInExLoader.WindowsProxy);
            if (windowsPackage != (host.Shell.Kind == HostShellKind.PowerShell))
                throw new InvalidDataException("The reviewed loader package does not match the host platform.");
            foreach (var (relative, sha) in loaderPackage.Files)
                if (!selected.TryAdd(relative, (Path.Combine(loaderPackage.Root, relative.Replace('/', Path.DirectorySeparatorChar)), sha)))
                    throw new InvalidDataException("A selected runtime file overrides a reviewed loader file: " + relative);
        }
        // Check this before shipping anything; a reviewed loader package fixes only the disposable copy.
        if (inspectedSource != null && (inspectedSource.HostName != host.Name || inspectedSource.Root != source))
            throw new ArgumentException("The inspected source belongs to a different host or install.", nameof(inspectedSource));
        // A shared campaign preflight already hashed and inspected this source. Reuse that exact listing, then compare
        // the copied runtime against it: if the source changed before or during the copy, the mismatch is refused.
        var sourceListing = inspectedSource ?? await InspectSourceAsync(host, kind, source, loaderPackage, timeout, cancellation).ConfigureAwait(false);
        string payload = Path.Combine(Path.GetTempPath(), "valheim-host-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(payload);
        bool shipped = false, copied = false;
        try
        {
            foreach (var (relative, value) in selected)
            {
                string target = Path.Combine(payload, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(value.Source, target);
                if (!FileHash.Sha256(target).Equals(value.Sha, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The local staging copy changed: " + relative);
            }
            shipped = true;
            await host.ShipFilesAsync(payload, staging, timeout, cancellation).ConfigureAwait(false);
            var shippedListing = await HostInstall.ListAsync(host, staging, timeout, cancellation: cancellation).ConfigureAwait(false);
            foreach (var (relative, value) in selected)
                if (!shippedListing.Files.TryGetValue(relative, out string? hash) || !hash.Equals(value.Sha, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"The staged file {relative} on {host.Name} differs from the reviewed local file.");
            copied = true;
            await HostInstall.CopyAsync(host, source, destination, timeout, cancellation).ConfigureAwait(false);
            var copy = await HostInstall.ListAsync(host, destination, timeout, cancellation: cancellation).ConfigureAwait(false);
            if (sourceListing.Files.Count != copy.Files.Count || sourceListing.Files.Any(file =>
                !copy.Files.TryGetValue(file.Key, out string? hash) || !hash.Equals(file.Value, StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"The disposable copy on {host.Name} differs from the pinned source install.");
            string names = string.Join('\n', selected.Keys.Select(name => Convert.ToBase64String(Encoding.UTF8.GetBytes(name))));
            var result = await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsApply : BashApply,
                new Dictionary<string, string> { ["runtime"] = destination, ["stage"] = staging, ["files"] = names,
                    ["loader"] = loaderPackage == null ? "" : string.Join('\n', InstallPins.LoaderEntries) }, timeout, cancellation).ConfigureAwait(false);
            result.EnsureSuccess($"Staging selected plugins on {host.Name}");
            if (InteractiveClient.Line(result.Stdout, "VT-STAGED") == null)
                throw new HostOperationException($"Unexpected reply while staging plugins on {host.Name}", result);
            var runtime = await HostInstall.ListAsync(host, destination, timeout, cancellation: cancellation).ConfigureAwait(false);
            foreach (var (relative, value) in selected)
                if (!runtime.Files.TryGetValue(relative, out string? hash) || !hash.Equals(value.Sha, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"The prepared runtime file {relative} on {host.Name} differs from its pinned source.");
            foreach (string directory in new[] { "plugins", "scripts", "config", "patchers" })
            {
                string prefix = "BepInEx/" + directory + "/";
                var expected = selected.Keys.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToHashSet(HostNames);
                if (runtime.Files.Keys.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Any(name => !expected.Contains(name)))
                    throw new IOException($"The prepared runtime on {host.Name} retained an unselected {directory} file.");
            }
            var pins = HostInstall.Pins(runtime);
            // The one loader identity: the prepared runtime's loader is the package's, every loader file and no other.
            if (loaderPackage != null && !pins.Loader.Equals(loaderPackage.Loader, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"The prepared runtime's loader on {host.Name} ({pins.Loader}) is not the reviewed package's ({loaderPackage.Loader}): " +
                    "a loader file outside the package survived preparation, or a package file is missing.");
            return runtime;
        }
        catch (Exception original)
        {
            if (shipped || copied)
            {
                // These are intended owned paths, recorded before each effect. A ship/copy can partly succeed before
                // losing its reply. Cleanup is uncancelled and its failure must stay alongside the original failure.
                try
                {
                    var cleanup = await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsCleanup : BashCleanup,
                        new Dictionary<string, string> { ["runtime"] = copied ? destination : "", ["stage"] = shipped ? staging : "",
                            ["parent"] = destination[..destination.LastIndexOfAny(['/', '\\'])] },
                        timeout, CancellationToken.None).ConfigureAwait(false);
                    cleanup.EnsureSuccess($"Cleaning failed preparation on {host.Name}");
                    if (InteractiveClient.Line(cleanup.Stdout, "VT-STAGE-CLEANED") == null)
                        throw new HostOperationException($"Cleanup of failed preparation on {host.Name} was not proven", cleanup);
                }
                catch (Exception cleanup) { throw new AggregateException("Preparation failed and cleanup of its owned paths was not proven.", original, cleanup); }
            }
            throw;
        }
        finally
        {
            Directory.Delete(payload, recursive: true);
        }
    }

    // Only HostedCampaignPreparation calls this for a unique directory it created and retained in memory. Its caller
    // must hold the host lock and must have stopped all game processes before retiring the prepared install.
    internal static async Task RetireAsync(IGameHost host, string destination, string staging, TimeSpan timeout)
    {
        string parent = destination[..destination.LastIndexOfAny(['/', '\\'])];
        if (!destination.EndsWith(host.Shell.Kind == HostShellKind.PowerShell ? @"\runtime" : "/runtime", StringComparison.OrdinalIgnoreCase) ||
            !parent.Replace('\\', '/').Split('/').Last().StartsWith("vt-prep-", StringComparison.Ordinal))
            throw new ArgumentException("Only a toolkit-created vt-prep runtime can be retired.", nameof(destination));
        var result = await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsCleanup : BashCleanup,
            new Dictionary<string, string> { ["runtime"] = destination, ["stage"] = staging, ["parent"] = parent }, timeout).ConfigureAwait(false);
        result.EnsureSuccess($"Retiring prepared runtime on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-STAGE-CLEANED") == null)
            throw new HostOperationException($"Unexpected cleanup reply from {host.Name}", result);
    }

    internal static readonly string WindowsApply = """
        $utf8 = New-Object Text.UTF8Encoding $false
        foreach ($name in @('plugins', 'scripts', 'config', 'patchers')) {
            $dir = Join-Path $runtime ('BepInEx\' + $name)
            if ([IO.Directory]::Exists($dir)) { [IO.Directory]::Delete($dir, $true) }
            [void][IO.Directory]::CreateDirectory($dir)
        }
        # loader: the loader's files and folders (InstallPins), one per line, when a reviewed package replaces them; else empty.
        foreach ($entry in ($loader -split "`n")) {
            if (-not $entry) { continue }
            $path = Join-Path $runtime $entry
            if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
            elseif ([IO.Directory]::Exists($path)) { [IO.Directory]::Delete($path, $true) }
        }
        foreach ($line in ($files -split "`n")) {
            if (-not $line) { continue }
            $relative = $utf8.GetString([Convert]::FromBase64String($line))
            $from = Join-Path $stage $relative
            $to = Join-Path $runtime $relative
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
            [IO.File]::Copy($from, $to, $true)
        }
        [IO.Directory]::Delete($stage, $true)
        'VT-STAGED selected files only'
        """.ReplaceLineEndings("\n");

    internal static readonly string BashApply = """
        set -eu
        for name in plugins scripts config patchers; do
          rm -rf -- "$runtime/BepInEx/$name"
          mkdir -p -- "$runtime/BepInEx/$name"
        done
        # loader: the loader's files and folders (InstallPins), one per line, when a reviewed package replaces them; else empty.
        while IFS= read -r entry; do
          [ -n "$entry" ] || continue
          rm -rf -- "$runtime/$entry"
        done <<< "$loader"
        while IFS= read -r line; do
          [ -n "$line" ] || continue
          relative=$(printf '%s' "$line" | base64 -D 2>/dev/null || printf '%s' "$line" | base64 -d)
          mkdir -p "$(dirname "$runtime/$relative")"
          cp "$stage/$relative" "$runtime/$relative"
        done <<< "$files"
        rm -rf -- "$stage"
        echo 'VT-STAGED selected files only'
        """.ReplaceLineEndings("\n");

    internal static readonly string WindowsCleanup = """
        foreach ($path in @($runtime, $stage)) {
            if ($path -and [IO.Directory]::Exists($path)) { [IO.Directory]::Delete($path, $true) }
        }
        if ($parent -and [IO.Directory]::Exists($parent) -and [IO.Directory]::GetFileSystemEntries($parent).Length -eq 0) {
            [IO.Directory]::Delete($parent)
        }
        'VT-STAGE-CLEANED'
        """.ReplaceLineEndings("\n");

    internal static readonly string BashCleanup = """
        set -eu
        if [ -n "$runtime" ]; then rm -rf -- "$runtime"; fi
        if [ -n "$stage" ]; then rm -rf -- "$stage"; fi
        if [ -n "$parent" ] && [ -d "$parent" ]; then rmdir "$parent" 2>/dev/null || true; fi
        echo 'VT-STAGE-CLEANED'
        """.ReplaceLineEndings("\n");
}
