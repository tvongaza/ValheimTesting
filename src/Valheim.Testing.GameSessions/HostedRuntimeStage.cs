using System.Globalization;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>A reviewed local file copied to one path in a disposable host runtime.</summary>
[ResultShape]
public sealed record HostedRuntimeFile(string Source, string RelativePath);

/// <summary>The game process whose executable must be present in the source install.</summary>
internal enum HostedRuntimeKind { Server, Client }

/// <summary>
/// Builds a disposable modded runtime (an owned loader profile) on a game host. The source install is read only: the
/// profile keeps the executable in place on Unix or uses an owned hard-link launch folder on Windows, and selects the
/// BepInEx plugin, script, config, patcher and loader content here. The caller owns the host lock and the resulting
/// runtime's cleanup.
/// </summary>
internal static partial class HostedRuntimeStage
{
    private static readonly StringComparer HostNames = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex SafePath = new(@"^[A-Za-z0-9_. -]+(?:/[A-Za-z0-9_. -]+)*$", RegexOptions.CultureInvariant);

    // The profile's selection and loader-package policy.
    private static (Dictionary<string, (string Source, string Sha)> Files, bool ExplicitSettings) SelectPayload(
        IReadOnlyList<HostedRuntimeFile> files, BepInExLoaderPackage? loaderPackage, HostShellKind shell)
    {
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
        bool explicitlySelectedSettings = selected.ContainsKey(BepInExSettings.RelativePath);
        loaderPackage?.Validate();
        if (loaderPackage != null)
        {
            bool windowsPackage = loaderPackage.Files.ContainsKey(BepInExLoader.WindowsProxy);
            if (windowsPackage != (shell == HostShellKind.PowerShell))
                throw new InvalidDataException("The reviewed loader package does not match the host platform.");
            // A BepInEx.cfg the plan selects replaces the package's default (#711); any other overlap is refused.
            foreach (var (relative, sha) in loaderPackage.Files.Where(file => !(explicitlySelectedSettings && selected.Comparer.Equals(file.Key, BepInExSettings.RelativePath))))
                if (!selected.TryAdd(relative, (Path.Combine(loaderPackage.Root, relative.Replace('/', Path.DirectorySeparatorChar)), sha)))
                    throw new InvalidDataException("A selected runtime file overrides a reviewed loader file: " + relative);
        }
        return (selected, explicitlySelectedSettings);
    }

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
    // per line. The folder a game's executable was started from and each runtime are compared as written and once resolved on
    // the host (ProtectedPaths' vt_physical), so a game started under /tmp is found running from a /private/tmp runtime and the
    // reverse (#665). A game whose path ps does not give is unknown, never idle.
    internal static readonly string BashProcessCheck = ProtectedPaths.BashInside + """
        processes=$(ps -axo pid=,comm=) || exit 4
        # One line per game process: its ID, 1 when it is a client in this run's desktop session (else 0), and its executable.
        games=$(printf '%s\n' "$processes" | awk -v client="$clientSession" '
          { line=$0; sub(/^[[:space:]]+/, "", line); pid=line; sub(/[[:space:]].*$/, "", pid); path=line; sub(/^[0-9]+[[:space:]]+/, "", path);
            name=path; sub(/^.*\//, "", name);
            macServer=(name=="Valheim" && path ~ /\/valheim_server\/Valheim$/);
            if (name!="Valheim" && index(name,"valheim_server")!=1) next;
            print pid, ((client=="true" && name=="Valheim" && !macServer) ? 1 : 0), path }') || exit 4
        busy=; unknown=
        while read -r pid hit path; do
          [ -n "$pid" ] || continue
          while IFS= read -r root; do
            [ -n "$root" ] || continue
            case "$path" in /*) ;; *) unknown=1; continue ;; esac
            # The folder it was started from (an executable that is itself a link still runs from where it was started), and the
            # runtime, each resolved when this user can: a folder this user cannot enter (another user's) is compared as written.
            folder=${path%/*}; [ -n "$folder" ] || folder=/
            physical=$(vt_physical "$root") || physical=$root
            resolved=$(vt_physical "$folder") || resolved=$folder
            for candidate in "$folder" "$resolved"; do
              case "$candidate" in "$root"|"$root"/*|"$physical"|"$physical"/*) hit=1 ;; esac
            done
          done <<< "$runtime"
          [ "$hit" != 1 ] || busy="${busy:+$busy,}$pid"
        done <<< "$games"
        if [ -n "$unknown" ]; then echo 'VT-GAME unknown'; elif [ -n "$busy" ]; then echo "VT-GAME busy $busy"; else echo 'VT-GAME idle'; fi
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

    /// <summary>Read-only source-install eligibility, shared by campaign preflight and the later profile staging.</summary>
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
            bool mac = sourceListing.Files.ContainsKey("Valheim.app/Contents/MacOS/Valheim");
            if (sourceListing.Files.ContainsKey(GameLaunch.ClientLinuxExecutable)) GameLaunch.RequireClientHost(ClientPlatform.Linux);
            if ((windows ? 1 : 0) + (mac ? 1 : 0) != 1 || sourceListing.Files.ContainsKey(GameLaunch.ServerWindowsExecutable) ||
                sourceListing.Files.ContainsKey(GameLaunch.ServerLinuxExecutable))
                throw new InvalidOperationException($"The source install on {host.Name} must contain exactly one Windows or macOS client executable and no server executable.");
            if (windows != (host.Shell.Kind == HostShellKind.PowerShell))
                throw new InvalidOperationException($"The client install on {host.Name} does not match its host platform.");
        }
        var platform = kind == HostedRuntimeKind.Server
            ? HostInstall.DetectServer(sourceListing) switch
            {
                ServerPlatform.Windows => ClientPlatform.Windows,
                ServerPlatform.MacOS => ClientPlatform.MacOS,
                _ => ClientPlatform.Linux,
            }
            : host.Shell.Kind == HostShellKind.PowerShell ? ClientPlatform.Windows
            : ClientPlatform.MacOS;
        // A reviewed package replaces the source loader, so it, not the source, must be this platform's complete loader.
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

    private static async Task RequireStagePathsAsync(IGameHost host, HostedRuntimeKind kind, string source, string destination,
        string staging, IReadOnlyList<HostedRuntimeFile> files, TimeSpan timeout, CancellationToken cancellation)
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
        RequireStaging(host, HostPath.Parent(destination), staging);
        // Whether the disposable runtime and its staging lie outside the install that runs never change is decided on the host,
        // with links resolved there: a runtime directory reached through a link into the install is inside it.
        await HostInstall.RequireApartAsync(host, source, [destination, staging], "the source install", timeout, cancellation).ConfigureAwait(false);
    }

    // For a toolkit-created copy (RequirePrepared) and its own staging folder (RequireStaging), whether the caller still holds it in memory or
    // a recovery read it from a journal. The caller must hold the host lock and must have stopped all game processes first.
    internal static async Task RetireAsync(IGameHost host, string destination, string staging, TimeSpan timeout, CancellationToken cancellation = default, string? runId = null)
    {
        string parent = RequirePrepared(host, destination, runId);
        if (staging.Length != 0) RequireStaging(host, parent, staging);
        var result = await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsCleanup : BashCleanup,
            new Dictionary<string, string> { ["runtime"] = destination, ["stage"] = staging, ["parent"] = parent }, timeout, cancellation).ConfigureAwait(false);
        result.EnsureSuccess($"Retiring prepared runtime on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-STAGE-CLEANED") == null)
            throw new HostOperationException($"Unexpected cleanup reply from {host.Name}", result);
    }

    /// <summary>
    /// The one rule for a copy's staging folder: <c>&lt;parent&gt;/staging</c> beside the runtime (case-insensitive on a Windows host).
    /// Preparation applies it to the folder it is given, and retirement to the one it is given, which on recovery comes from a journal
    /// that a hand edit or corruption could aim anywhere. A world copy has no staging: retirement skips the check for an empty one.
    /// </summary>
    internal static void RequireStaging(IGameHost host, string parent, string staging)
    {
        bool windows = host.Shell.Kind == HostShellKind.PowerShell;
        string Same(string path) => windows ? path.Replace('/', '\\') : path;
        // Windows ignores case; a bash host's folders may differ by it (the host script resolves what is really there).
        if (!string.Equals(Same(staging), Same(parent) + (windows ? @"\" : "/") + "staging", windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("The staging directory must be a sibling named staging beside the disposable runtime.", nameof(staging));
    }

    /// <summary>
    /// Refuses anything but a toolkit-created <c>&lt;runs&gt;/vt-prep-*/runtime</c>, or, with <paramref name="runId"/>, that
    /// standalone hosted run's own <c>&lt;runs&gt;/&lt;runId&gt;/runtime</c> or <c>&lt;runs&gt;/&lt;runId&gt;/world</c>
    /// (<see cref="HostedServerRun"/>); returns the copy's parent.
    /// </summary>
    internal static string RequirePrepared(IGameHost host, string destination, string? runId = null)
    {
        int at = destination.LastIndexOfAny(['/', '\\']);
        string parent = at > 0 ? destination[..at] : "";
        string separator = host.Shell.Kind == HostShellKind.PowerShell ? @"\" : "/", parentName = parent.Replace('\\', '/').Split('/').Last();
        if (destination.EndsWith(separator + "runtime", StringComparison.OrdinalIgnoreCase) && parentName.StartsWith("vt-prep-", StringComparison.Ordinal))
            return parent;
        if (runId != null && RunJournal.SafeName(runId) && parentName == runId &&
            (destination.EndsWith(separator + "runtime", StringComparison.OrdinalIgnoreCase) || destination.EndsWith(separator + "world", StringComparison.OrdinalIgnoreCase)))
            return parent;
        throw new ArgumentException("Only a toolkit-created vt-prep runtime, or a hosted run's own runtime or world copy, can be retired.", nameof(destination));
    }

    /// <summary>Whether a relative path can be staged into a runtime (letters, digits, '.', '_', '-' and spaces, '/' between parts).</summary>
    internal static bool IsStageable(string relativePath) =>
        SafePath.IsMatch(relativePath) && !relativePath.Split('/').Any(part => part is "." or "..") && !relativePath.StartsWith(' ');

    // Variables: runtime, stage, parent (the folder holding both), files, loader, preserveConfig. Every delete is below its root: the
    // runtime's own BepInEx folders and loader entries below the runtime, the staging folder below the parent.
    internal static readonly string WindowsApply = ProtectedPaths.PowerShellInside + ProtectedPaths.PowerShellRemove + """
        $utf8 = New-Object Text.UTF8Encoding $false
        if ($preserveConfig -eq 'true') {
            [IO.File]::Copy((Join-Path $runtime 'BepInEx\config\BepInEx.cfg'), (Join-Path $stage 'source-BepInEx.cfg'))
        }
        foreach ($name in @('plugins', 'scripts', 'config', 'patchers')) {
            $dir = Join-Path $runtime ('BepInEx\' + $name)
            Remove-VtTree $dir $runtime
            [void][IO.Directory]::CreateDirectory($dir)
        }
        # loader: the loader's files and folders (InstallPins), one per line, when a reviewed package replaces them; else empty.
        foreach ($entry in ($loader -split "`n")) {
            if (-not $entry) { continue }
            $path = Join-Path $runtime $entry
            Remove-VtTree $path $runtime
        }
        foreach ($line in ($files -split "`n")) {
            if (-not $line) { continue }
            $relative = $utf8.GetString([Convert]::FromBase64String($line))
            $from = Join-Path $stage $relative
            $to = Join-Path $runtime $relative
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
            [IO.File]::Copy($from, $to, $true)
        }
        if ($preserveConfig -eq 'true') {
            [IO.File]::Copy((Join-Path $stage 'source-BepInEx.cfg'), (Join-Path $runtime 'BepInEx\config\BepInEx.cfg'))
        }
        Remove-VtTree $stage $parent
        'VT-STAGED selected files only'
        """.ReplaceLineEndings("\n");

    internal static readonly string BashApply = "set -eu\n" + ProtectedPaths.BashInside + ProtectedPaths.BashRemove + """
        if [ "$preserveConfig" = true ]; then
          cp "$runtime/BepInEx/config/BepInEx.cfg" "$stage/source-BepInEx.cfg"
        fi
        for name in plugins scripts config patchers; do
          vt_remove "$runtime/BepInEx/$name" "$runtime" || { echo "vt: refused to remove $runtime/BepInEx/$name" >&2; exit 3; }
          mkdir -p -- "$runtime/BepInEx/$name"
        done
        # loader: the loader's files and folders (InstallPins), one per line, when a reviewed package replaces them; else empty.
        while IFS= read -r entry; do
          [ -n "$entry" ] || continue
          vt_remove "$runtime/$entry" "$runtime" || { echo "vt: refused to remove $runtime/$entry" >&2; exit 3; }
        done <<< "$loader"
        while IFS= read -r line; do
          [ -n "$line" ] || continue
          relative=$(printf '%s' "$line" | base64 -D 2>/dev/null || printf '%s' "$line" | base64 -d)
          mkdir -p "$(dirname "$runtime/$relative")"
          cp "$stage/$relative" "$runtime/$relative"
        done <<< "$files"
        if [ "$preserveConfig" = true ]; then
          cp "$stage/source-BepInEx.cfg" "$runtime/BepInEx/config/BepInEx.cfg"
        fi
        vt_remove "$stage" "$parent" || { echo "vt: refused to remove $stage" >&2; exit 3; }
        echo 'VT-STAGED selected files only'
        """.ReplaceLineEndings("\n");

    // Only the runtime and its staging folder, each below the run's own parent, through the one remover (ProtectedPaths): a link is
    // unlinked, never emptied, and a path that resolves outside the parent is refused with nothing deleted. Variables: runtime, stage, parent.
    internal static readonly string WindowsCleanup = ProtectedPaths.PowerShellInside + ProtectedPaths.PowerShellRemove + """
        foreach ($path in @($runtime, $stage)) {
            if ($path) { Remove-VtTree $path $parent -Check }
        }
        foreach ($path in @($runtime, $stage)) {
            if ($path) { Remove-VtTree $path $parent }
        }
        if ($parent -and [IO.Directory]::Exists($parent) -and [IO.Directory]::GetFileSystemEntries($parent).Length -eq 0) {
            [IO.Directory]::Delete($parent)
        }
        'VT-STAGE-CLEANED'
        """.ReplaceLineEndings("\n");

    internal static readonly string BashCleanup = "set -eu\n" + ProtectedPaths.BashInside + ProtectedPaths.BashRemove + """
        if [ -z "$parent" ]; then echo 'vt: no parent folder to remove below' >&2; exit 3; fi
        # Every path is checked before any is deleted: a refusal leaves the whole copy for a later recovery.
        for path in "$runtime" "$stage"; do
          if [ -n "$path" ]; then vt_remove "$path" "$parent" check || { echo "vt: refused to remove $path" >&2; exit 3; }; fi
        done
        for path in "$runtime" "$stage"; do
          if [ -n "$path" ]; then vt_remove "$path" "$parent" || { echo "vt: could not remove $path" >&2; exit 3; }; fi
        done
        if [ -d "$parent" ]; then rmdir "$parent" 2>/dev/null || true; fi
        echo 'VT-STAGE-CLEANED'
        """.ReplaceLineEndings("\n");
}
