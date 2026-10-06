using System.Globalization;
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

    /// <summary>
    /// Refuse conflicting client use or a process executing from any of the owned <paramref name="runtimes"/>; unrelated
    /// servers may stay up. One check covers every runtime on the host.
    /// </summary>
    /// <summary>
    /// The conflicting-use check (#257): refuses when a Valheim client runs where this run needs the desktop session
    /// (<paramref name="clientSession"/>: one client per session), or any game runs from one of <paramref name="runtimes"/>.
    /// Other games on the host (a server elsewhere, a client when only a server is needed) are not a conflict. The refusal names
    /// each conflicting process, and the run that journalled it when <paramref name="owners"/> (process ID to run) knows it; a
    /// client no run owns is the user's own, to stop or to describe in the inventory.
    /// </summary>
    internal static async Task RequireStoppedAsync(IGameHost host, TimeSpan timeout, CancellationToken cancellation = default,
        IReadOnlyCollection<string>? runtimes = null, bool clientSession = true, IReadOnlyDictionary<int, string>? owners = null)
    {
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsProcessCheck : BashProcessCheck,
            new Dictionary<string, string> { ["runtime"] = string.Join('\n', runtimes ?? []), ["clientSession"] = clientSession ? "true" : "false" },
            timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Checking conflicting game processes on {host.Name}");
        string verdict = InteractiveClient.Line(result.Stdout, "VT-GAME ") ??
            throw new HostOperationException($"No game-process verdict from {host.Name}", result);
        string[] parts = verdict.Split(' ', 2);
        if (parts[0] == "busy")
        {
            var pids = parts.Length == 2 ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(pid => int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : 0).Where(id => id > 0).ToList() : [];
            string Name(int pid) => owners != null && owners.TryGetValue(pid, out string? run)
                ? $"process {pid} of run {run} (wait for it to end, or see valheim-test env status)"
                : owners != null ? $"process {pid}, which no run journalled: a game of your own, so stop it, or describe its machine in the inventory"
                : $"process {pid}";
            throw new InvalidOperationException((pids.Count == 0 ? "A conflicting Valheim client or owned-runtime process" : "Conflicting Valheim " + string.Join("; ", pids.Select(Name))) +
                $" is running on {host.Name}" + (clientSession ? ", where this run needs the desktop session's one client" : "") + "; no files were changed.");
        }
        if (parts[0] != "idle") throw new HostOperationException($"Unexpected game-process verdict from {host.Name}", result);
    }

    internal static readonly string WindowsProcessCheck = """
        $busy = @()
        $roots = @($runtime -split "`n" | Where-Object { $_ })
        foreach ($p in @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object { $_.Name -in @('Valheim.exe', 'valheim_server.exe') })) {
            if ($clientSession -eq 'true' -and $p.Name -eq 'Valheim.exe') { $busy += [string]$p.ProcessId }
            if ($roots.Count -ne 0 -and -not $p.ExecutablePath) { throw 'Cannot establish the running game executable path' }
            foreach ($root in $roots) {
                if ($p.ExecutablePath.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { $busy += [string]$p.ProcessId }
            }
        }
        if ($busy.Count -ne 0) { 'VT-GAME busy ' + ((@($busy) | Select-Object -Unique) -join ',') } else { 'VT-GAME idle' }
        """;
    // ps errors remain errors. A dedicated server elsewhere does not use the client's character files. The runtimes are one
    // per line; awk takes them as one value, so the lines are joined with a byte no path holds (\034) and split again.
    internal static readonly string BashProcessCheck = """
        processes=$(ps -axo pid=,comm=) || exit 4
        roots=$(printf '%s' "$runtime" | tr '\n' '\034')
        if printf '%s\n' "$processes" | awk -v roots="$roots" -v client="$clientSession" '
          BEGIN { n=split(roots, root, "\034") }
          { line=$0; sub(/^[[:space:]]+/, "", line); pid=line; sub(/[[:space:]].*$/, "", pid); path=line; sub(/^[0-9]+[[:space:]]+/, "", path);
            name=path; sub(/^.*\//, "", name); hit=0;
            game=(name=="Valheim" || name=="valheim.x86_64" || index(name,"valheim_server")==1);
            if (client=="true" && (name=="Valheim" || name=="valheim.x86_64")) hit=1;
            for (i=1; i<=n; i++) if (game && root[i]!="") { if (index(path,root[i] "/")==1) hit=1; if (index(path,"/")!=1) unknown=1 }
            if (hit) busy=busy (busy=="" ? "" : ",") pid }
          END { if (unknown) print "VT-GAME unknown"; else if (busy!="") print "VT-GAME busy " busy; else print "VT-GAME idle" }'; then :; else exit 4; fi
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
            bool windows = sourceListing.Files.ContainsKey(GameLaunch.ClientWindowsExecutable);
            bool linux = sourceListing.Files.ContainsKey(GameLaunch.ClientLinuxExecutable);
            bool mac = sourceListing.Files.ContainsKey("Valheim.app/Contents/MacOS/Valheim");
            if ((windows ? 1 : 0) + (linux ? 1 : 0) + (mac ? 1 : 0) != 1 || sourceListing.Files.ContainsKey(GameLaunch.ServerWindowsExecutable) ||
                sourceListing.Files.ContainsKey(GameLaunch.ServerLinuxExecutable))
                throw new InvalidOperationException($"The source install on {host.Name} must contain exactly one Windows, Linux or macOS client executable and no server executable.");
            if (windows != (host.Shell.Kind == HostShellKind.PowerShell))
                throw new InvalidOperationException($"The client install on {host.Name} does not match its host platform.");
        }
        var platform = host.Shell.Kind == HostShellKind.PowerShell ? ClientPlatform.Windows
            : sourceListing.Files.ContainsKey("Valheim.app/Contents/MacOS/Valheim") ? ClientPlatform.MacOS : ClientPlatform.Linux;
        // A reviewed package replaces the copied loader, so it, not the source, must be this platform's complete loader.
        if (loaderPackage != null) loaderPackage.RequireFor(platform, $"reviewed loader package for {host.Name}");
        else
        {
            _ = HostInstall.Pins(sourceListing); // Also refuses a nested BepInEx/core/core.
            // Every platform's loader files from the listing; a Windows proxy and configuration also as one Doorstop version.
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
            // A server's copy leaves out Steam's own runtime output in the install (logs/), which is not the game's or the loader's.
            var skip = kind == HostedRuntimeKind.Server ? HostInstall.ServerRuntimeSkips : [];
            await HostInstall.CopyAsync(host, source, destination, timeout, skip, cancellation).ConfigureAwait(false);
            var copy = await HostInstall.ListAsync(host, destination, timeout, cancellation: cancellation).ConfigureAwait(false);
            var sourceFiles = HostInstall.WithoutSkipped(sourceListing.Files, skip, sourceListing.Names);
            if (sourceFiles.Count != copy.Files.Count || sourceFiles.Any(file =>
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
    internal static async Task RetireAsync(IGameHost host, string destination, string staging, TimeSpan timeout, CancellationToken cancellation = default)
    {
        string parent = RequirePrepared(host, destination);
        var result = await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsCleanup : BashCleanup,
            new Dictionary<string, string> { ["runtime"] = destination, ["stage"] = staging, ["parent"] = parent }, timeout, cancellation).ConfigureAwait(false);
        result.EnsureSuccess($"Retiring prepared runtime on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-STAGE-CLEANED") == null)
            throw new HostOperationException($"Unexpected cleanup reply from {host.Name}", result);
    }

    /// <summary>Refuses anything but a toolkit-created <c>&lt;runs&gt;/vt-prep-*/runtime</c>; returns its <c>vt-prep-*</c> parent.</summary>
    internal static string RequirePrepared(IGameHost host, string destination)
    {
        int at = destination.LastIndexOfAny(['/', '\\']);
        string parent = at > 0 ? destination[..at] : "";
        if (!destination.EndsWith(host.Shell.Kind == HostShellKind.PowerShell ? @"\runtime" : "/runtime", StringComparison.OrdinalIgnoreCase) ||
            !parent.Replace('\\', '/').Split('/').Last().StartsWith("vt-prep-", StringComparison.Ordinal))
            throw new ArgumentException("Only a toolkit-created vt-prep runtime can be retired.", nameof(destination));
        return parent;
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
