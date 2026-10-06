using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Game;

/// <summary>
/// The files of a directory on a host as <see cref="HostInstall.ListAsync"/> found them: every regular file's SHA256 by its
/// relative path with <c>/</c> separators (the same manifest <see cref="WorldFixture.Manifest"/> records for a local
/// directory) and, on a bash host, which game executables at the root carry
/// the user-execute bit.
/// </summary>
public sealed class HostListing
{
    internal HostListing(string hostName, HostShellKind shell, string root, IReadOnlyDictionary<string, string> files, IReadOnlyList<string> executables)
    {
        HostName = hostName; Shell = shell; Root = root; Files = files; Executables = executables;
    }
    public string HostName { get; }
    public HostShellKind Shell { get; }
    /// <summary>The listed directory on the host.</summary>
    public string Root { get; }
    /// <summary>Relative path (<c>/</c> separators) to lower-case SHA256.</summary>
    public IReadOnlyDictionary<string, string> Files { get; }
    /// <summary>Bash hosts: which of <c>valheim_server.x86_64</c> and <c>valheim.x86_64</c> at the root the user may execute.</summary>
    public IReadOnlyList<string> Executables { get; }
    /// <summary>Paths compare ignoring case on a Windows (PowerShell) host.</summary>
    internal StringComparer Names => Shell == HostShellKind.PowerShell ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

/// <summary>
/// Reads and copies game installs and runtimes on a host, so a run on another machine keeps the same pins as one here: the
/// runtime manifest, the game build, loader and patchers (<see cref="InstallPins"/>) and the server's
/// execute bit are all checked against what the host holds, not against a local copy.
/// </summary>
public static class HostInstall
{
    private static readonly Regex Sha256Line = new(@"^(\\?)([0-9a-f]{64}) [ *]\./(.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex FileLine = new(@"^VT-FILE ([0-9a-f]{64}) ([A-Za-z0-9+/=]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex GameAssembly = new(@"^(?<managed>[^/]+_Data/Managed|[^/]+\.app/Contents/Resources/Data/Managed)/assembly_valheim\.dll$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Hashes every regular file under <paramref name="root"/> on the host (links are refused, as <see cref="WorldFixture"/>
    /// refuses them). <paramref name="paths"/> limits the listing to those subdirectories and files (relative, <c>*</c> allowed
    /// in a directory, for example <c>*_Data/Managed</c>; a file only by its exact name, for example <c>winhttp.dll</c>); null
    /// lists everything. The patcher entries and executables are always read.
    /// </summary>
    public static async Task<HostListing> ListAsync(IGameHost host, string root, TimeSpan timeout, IReadOnlyList<string>? paths = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        RequireHostPath(host, root, nameof(root));
        foreach (string path in paths ?? Array.Empty<string>())
            if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || path.StartsWith('/') || path.Split('/', '\\').Contains(".."))
                throw new ArgumentException($"'{path}' is not a relative path inside the root.", nameof(paths));
        var result = (await host.RunAsync(HostInstallScripts.List(host.Shell.Kind), new Dictionary<string, string>
        {
            ["root"] = root, ["dirs"] = string.Join('\n', paths ?? Array.Empty<string>()),
        }, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Listing {root} on {host.Name}");
        return ReadListing(host.Name, host.Shell.Kind, root, result);
    }

    internal static HostListing ReadListing(string hostName, HostShellKind shell, string root, HostResult result)
    {
        var lines = result.Stdout.Split('\n');
        if (lines.Length > 0 && lines[0] == "VT-LIST missing") throw new DirectoryNotFoundException($"{root} does not exist on {hostName}.");
        if (lines.Length > 0 && lines[0] == "VT-LIST links")
            throw new IOException($"{root} on {hostName} holds links, which are unsupported in a fixture: {string.Join(", ", lines.Skip(1).Where(line => line.Length > 0).Take(5))}.");
        if (!lines.Contains("VT-LIST done")) throw new HostOperationException($"Unexpected reply while listing {root} on {hostName}", result);
        var names = shell == HostShellKind.PowerShell ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var files = new Dictionary<string, string>(names);
        var executables = new List<string>();
        foreach (string line in lines)
        {
            if (line.Length == 0 || line == "VT-LIST done") continue;
            string relative, sha256;
            if (FileLine.Match(line) is { Success: true } file) { sha256 = file.Groups[1].Value; relative = Decode(file.Groups[2].Value); }
            else if (Sha256Line.Match(line) is { Success: true } sum)
            {
                sha256 = sum.Groups[2].Value; relative = sum.Groups[3].Value;
                // sha256sum escapes a name holding a backslash or a line break, and marks the line with a leading backslash.
                if (sum.Groups[1].Value.Length != 0) relative = Unescape(relative);
            }
            else if (line.StartsWith("VT-EXEC ", StringComparison.Ordinal)) { executables.Add(line["VT-EXEC ".Length..]); continue; }
            else throw new HostOperationException($"Unexpected line while listing {root} on {hostName}: {line}", result);
            relative = relative.Replace('\\', '/');
            // Overlapping directories list a file twice; its hash is the same both times.
            if (files.TryGetValue(relative, out string? seen) && seen != sha256) throw new IOException($"{relative} changed while {root} on {hostName} was listed.");
            files[relative] = sha256;
        }
        return new HostListing(hostName, shell, root, files, executables);
    }

    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destination"/> on the host,
    /// which must not exist yet; a directory this call created is removed again if the copy fails. Verify the copy with
    /// <see cref="ListAsync"/> before using it.
    /// </summary>
    public static Task CopyAsync(IGameHost host, string source, string destination, TimeSpan timeout, CancellationToken cancellation = default) =>
        CopyAsync(host, source, destination, timeout, [], cancellation);

    /// <summary>
    /// The top-level directories a dedicated server's runtime copy leaves out: <c>logs</c>, where Steam writes the server's own
    /// runtime output (<c>connection_log_*.txt</c>, <c>stats_log.txt</c>), none of it the game's or the loader's files (#257).
    /// </summary>
    internal static readonly string[] ServerRuntimeSkips = ["logs"];

    /// <summary><paramref name="files"/> (a manifest or listing) without what lies under a skipped top-level directory.</summary>
    internal static Dictionary<string, string> WithoutSkipped(IReadOnlyDictionary<string, string> files, IReadOnlyCollection<string> skip, StringComparer names) =>
        files.Where(file => !Skipped(file.Key, skip, names)).ToDictionary(file => file.Key, file => file.Value, names);

    // Whatever the top-level entry is (a folder, a file or a link), as the copy scripts skip it by name.
    internal static bool Skipped(string relative, IReadOnlyCollection<string> skip, StringComparer names)
    {
        int slash = relative.IndexOfAny(['/', '\\']);
        return skip.Contains(slash < 0 ? relative : relative[..slash], names);
    }

    // skip: names of the source's top-level entries not copied (the install keeps them).
    internal static async Task CopyAsync(IGameHost host, string source, string destination, TimeSpan timeout, IReadOnlyCollection<string> skip,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(host);
        RequireHostPath(host, source, nameof(source));
        RequireHostPath(host, destination, nameof(destination));
        foreach (string name in skip)
            if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', '\n', '\r']) >= 0) throw new ArgumentException($"'{name}' is not a directory name.", nameof(skip));
        var result = (await host.RunAsync(HostInstallScripts.CopyFor(host.Shell.Kind), new Dictionary<string, string>
            { ["source"] = source, ["dest"] = destination, ["skip"] = string.Join('\n', skip) }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Copying {source} to {destination} on {host.Name}");
        switch (InteractiveClient.Line(result.Stdout, "VT-COPY "))
        {
            case "copied": return;
            case "missing": throw new DirectoryNotFoundException($"{source} does not exist on {host.Name}.");
            case "exists": throw new InvalidOperationException($"{destination} already exists on {host.Name}; each run copies into a new directory.");
            default: throw new HostOperationException($"Unexpected reply while copying {source} on {host.Name}", result);
        }
    }

    /// <summary>
    /// Refuses when anything on the host listens on TCP <paramref name="port"/> (any address), so a command can never reach a
    /// process this run does not own. Bash reads <c>/proc/net/tcp</c> (Linux); PowerShell asks .NET for the active listeners.
    /// </summary>
    public static async Task RequirePortFreeAsync(IGameHost host, int port, TimeSpan timeout, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        GameHostPorts.Check(port, nameof(port));
        var result = (await host.RunAsync(HostInstallScripts.Port(host.Shell.Kind), new Dictionary<string, string> { ["port"] = port.ToString(CultureInfo.InvariantCulture) }, timeout, cancellation)
            .ConfigureAwait(false)).EnsureSuccess($"Checking port {port} on {host.Name}");
        switch (InteractiveClient.Line(result.Stdout, "VT-PORT "))
        {
            case "free": return;
            case "busy": throw new InvalidOperationException($"Something already listens on port {port} on {host.Name}; stop it first, this run only drives processes it started.");
            case "unknown": throw new PlatformNotSupportedException($"{host.Name} cannot tell which ports are in use (no /proc/net/tcp); a server host must be Linux.");
            default: throw new HostOperationException($"Unexpected reply while checking port {port} on {host.Name}", result);
        }
    }

    /// <summary>
    /// Refuses a listing that is not exactly <paramref name="expected"/> (a manifest, <c>/</c> or <c>\</c> separators): a missing
    /// file, an extra one or a different hash. <paramref name="ignore"/> names files the listing may hold beyond it.
    /// </summary>
    public static void RequireSame(IReadOnlyDictionary<string, string> expected, HostListing listing, string what, IEnumerable<string>? ignore = null)
    {
        var want = expected.ToDictionary(pair => pair.Key.Replace('\\', '/'), pair => pair.Value.ToLowerInvariant(), listing.Names);
        var skip = new HashSet<string>(ignore ?? Enumerable.Empty<string>(), listing.Names);
        var missing = want.Keys.Where(key => !listing.Files.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();
        var extra = listing.Files.Keys.Where(key => !want.ContainsKey(key) && !skip.Contains(key)).Order(StringComparer.Ordinal).ToList();
        var changed = want.Where(pair => listing.Files.TryGetValue(pair.Key, out string? found) && found != pair.Value).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToList();
        if (missing.Count + extra.Count + changed.Count == 0) return;
        static string Few(List<string> names) => string.Join(", ", names.Take(5)) + (names.Count > 5 ? $" and {names.Count - 5} more" : "");
        var problems = new List<string>();
        if (changed.Count != 0) problems.Add("different hash: " + Few(changed));
        if (missing.Count != 0) problems.Add("missing: " + Few(missing));
        if (extra.Count != 0) problems.Add("not in the manifest: " + Few(extra));
        throw new InvalidOperationException($"The {what} at {listing.Root} on {listing.HostName} is not the pinned one ({string.Join("; ", problems)}).");
    }

    /// <summary>What a listing for <see cref="Pins"/> needs to cover: the game's Managed folder, the loader files and <c>BepInEx/patchers</c>.</summary>
    internal static readonly string[] PinPaths =
        ["*_Data/Managed", "*.app/Contents/Resources/Data/Managed", .. InstallPins.LoaderRootFiles, .. InstallPins.LoaderFolders, "BepInEx/patchers"];

    /// <summary>The <see cref="InstallPins"/> of a listing that covers <see cref="PinPaths"/>.</summary>
    public static InstallPins Pins(HostListing listing) => Pins(listing, out _);

    private static InstallPins Pins(HostListing listing, out string managed)
    {
        var assemblies = listing.Files.Keys.Select(key => GameAssembly.Match(key)).Where(match => match.Success).Select(match => match.Groups["managed"].Value).Order(StringComparer.Ordinal).ToList();
        if (assemblies.Count == 0)
            throw new FileNotFoundException($"No game assembly ({InstallPins.GameAssemblyName} in a *_Data/Managed folder) under {listing.Root} on {listing.HostName}.");
        if (assemblies.Count > 1) throw new InvalidOperationException($"More than one game assembly under {listing.Root} on {listing.HostName}: {string.Join(", ", assemblies)}; refusing to guess which runs.");
        managed = assemblies[0];
        string prefix = managed + "/";
        if (!listing.Files.Keys.Any(key => key.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase)))
            throw new DirectoryNotFoundException($"BepInEx is not installed in {listing.Root} on {listing.HostName} (no BepInEx/core).");
        if (listing.Files.Keys.Any(key => key.StartsWith("BepInEx/core/core/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"The install on {listing.HostName} has a nested BepInEx/core/core. Replace the loader as one coherent tree before launch.");
        return new InstallPins
        {
            Game = FileHash.Listing(Under(listing, prefix).Where(file => !file.Relative.Contains('/') &&
                file.Relative.StartsWith("assembly_", StringComparison.Ordinal) && file.Relative.EndsWith(".dll", StringComparison.Ordinal))),
            Loader = FileHash.Listing(listing.Files.Select(pair => (Relative: InstallPins.LoaderPath(pair.Key, Comparison(listing)), Sha256: pair.Value))
                .Where(file => file.Relative != null).Select(file => (file.Relative!, file.Sha256))),
            Patchers = FileHash.Listing(Under(listing, "BepInEx/patchers/")),
        };
    }

    // The files under a folder, relative to it, as the folder's listing names them.
    private static IEnumerable<(string Relative, string Sha256)> Under(HostListing listing, string prefix) =>
        listing.Files.Where(pair => pair.Key.StartsWith(prefix, Comparison(listing))).Select(pair => (pair.Key[prefix.Length..], pair.Value));
    private static StringComparison Comparison(HostListing listing) =>
        listing.Shell == HostShellKind.PowerShell ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Pinned: refuses a listing whose game build, loader or patchers are not <paramref name="pinned"/>, naming each that
    /// differs. Returns the pins found, for the report.
    /// </summary>
    public static InstallPins CheckPins(InstallPins pinned, HostListing listing, string kind)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        var found = Pins(listing, out string managed);
        return pinned.Compare(found, kind + " on " + listing.HostName, managed,
            () => Under(listing, "BepInEx/patchers/").Select(file => file.Relative.Split('/')[0]).Distinct(listing.Names).ToList());
    }

    /// <summary>The dedicated server's platform from the files at the root, as <see cref="GameLaunch.DetectServer"/> decides it for a local runtime.</summary>
    public static ServerPlatform DetectServer(HostListing listing)
    {
        bool windows = listing.Files.ContainsKey(GameLaunch.ServerWindowsExecutable), linux = listing.Files.ContainsKey(GameLaunch.ServerLinuxExecutable);
        if (windows && linux) throw new InvalidOperationException($"The runtime on {listing.HostName} contains both {GameLaunch.ServerWindowsExecutable} and {GameLaunch.ServerLinuxExecutable}; refusing to guess its platform.");
        if (windows || linux) return windows ? ServerPlatform.Windows : ServerPlatform.Linux;
        throw new FileNotFoundException($"The runtime at {listing.Root} on {listing.HostName} contains neither {GameLaunch.ServerWindowsExecutable} nor {GameLaunch.ServerLinuxExecutable}.");
    }

    internal static void RequireHostPath(IGameHost host, string path, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(path, name);
        if (path.Any(char.IsControl) || !ScriptedGameHost.IsAbsolute(host.Shell.Kind, path))
            throw new ArgumentException($"{name} must be an absolute path on {host.Name}; '{path}' would depend on the user's home or working directory.", name);
    }

    /// <summary>A path on a host under <paramref name="root"/>: joined with <c>\</c> when the root is a Windows path, else <c>/</c>.</summary>
    internal static string Join(string root, params string[] parts)
    {
        bool windows = root.Contains('\\') || Regex.IsMatch(root, "^[A-Za-z]:");
        char separator = windows ? '\\' : '/';
        string path = root.Length > 1 ? root.TrimEnd('/', '\\') : root;
        foreach (string part in parts) path += (path.EndsWith(separator) ? "" : separator.ToString()) + (windows ? part.Replace('/', '\\') : part);
        return path;
    }

    private static string Decode(string base64) => Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    private static string Unescape(string name)
    {
        var text = new StringBuilder(name.Length);
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] != '\\' || i + 1 == name.Length) { text.Append(name[i]); continue; }
            char next = name[++i];
            text.Append(next switch { 'n' => '\n', 'r' => '\r', _ => next });
        }
        return text.ToString();
    }
}

// The fixed scripts of HostInstall. Values arrive as variables (ScriptedGameHost.Compose); each script ends with its verdict.
internal static class HostInstallScripts
{
    public static string CopyFor(HostShellKind kind) => kind == HostShellKind.Bash ? Copy : PowerShellCopy;
    public static string List(HostShellKind kind) => kind == HostShellKind.Bash ? BashList : PowerShellList;
    public static string Port(HostShellKind kind) => kind == HostShellKind.Bash ? BashPort : PowerShellPort;

    // Variables: root, dirs (relative directory patterns and file names, one per line; none lists everything). sha256sum prints each file as
    // "<hash>  ./<path>", escaping a name with a backslash or line break. Links are refused before anything is hashed.
    public static readonly string BashList = """
        set -u
        if [ ! -d "$root" ]; then echo "VT-LIST missing"; exit 0; fi
        cd -- "$root" || exit 3
        shopt -s nullglob dotglob
        if command -v sha256sum > /dev/null 2>&1; then hasher=(sha256sum); else hasher=(shasum -a 256); fi
        roots=()
        if [ -z "$dirs" ]; then roots=(.); else
            while IFS= read -r pattern; do
                [ -n "$pattern" ] || continue
                IFS=$'\n'
                # A named path that is a link is kept, so the link check below refuses it rather than leave it out.
                for d in $pattern; do if [ -e "$d" ] || [ -L "$d" ]; then roots+=("./$d"); fi; done
                unset IFS
            done <<< "$dirs"
        fi
        if [ "${#roots[@]}" -gt 0 ]; then
            links=$(find "${roots[@]}" -type l -print | head -n 5) || exit 3
            if [ -n "$links" ]; then echo "VT-LIST links"; printf '%s\n' "$links"; exit 0; fi
            find "${roots[@]}" -type f -exec "${hasher[@]}" {} + || exit 3
        fi
        for e in valheim_server.x86_64 valheim.x86_64; do if [ -f "$e" ] && [ -x "$e" ]; then echo "VT-EXEC $e"; fi; done
        echo "VT-LIST done"
        """.ReplaceLineEndings("\n");

    // Variables: root, dirs. Hashes with .NET (see HostScripts' note on Get-FileHash); names travel as base64.
    public static readonly string PowerShellList = """
        $utf8 = New-Object Text.UTF8Encoding $false
        if (-not [IO.Directory]::Exists($root)) { 'VT-LIST missing'; exit 0 }
        $full = [IO.Path]::GetFullPath($root).TrimEnd('\', '/')
        # Patterns resolve segment by segment with .NET, so every path found keeps the root's own spelling (a short 8.3 name
        # included) and the relative paths below are exact.
        $roots = @()
        $links = New-Object 'Collections.Generic.List[string]'
        if (-not $dirs) { $roots = @($full) } else {
            foreach ($pattern in ($dirs -split "`n")) {
                if (-not $pattern) { continue }
                $current = @($full)
                $segments = @($pattern -split '[\\/]' | Where-Object { $_ })
                for ($i = 0; $i -lt $segments.Count; $i++) {
                    $segment = $segments[$i]
                    $next = @()
                    foreach ($dir in $current) {
                        if ($segment.IndexOfAny([char[]]'*?') -ge 0) { $next += [IO.Directory]::GetDirectories($dir, $segment) }
                        elseif ([IO.Directory]::Exists((Join-Path $dir $segment))) { $next += (Join-Path $dir $segment) }
                        elseif ($i -eq $segments.Count - 1 -and [IO.File]::Exists((Join-Path $dir $segment))) { $next += (Join-Path $dir $segment) }
                    }
                    $current = $next
                }
                foreach ($dir in $current) {
                    $attributes = [IO.File]::GetAttributes($dir)
                    if (-not ($attributes -band [IO.FileAttributes]::ReparsePoint)) { $roots += $dir }
                    else { $links.Add($dir) } # a named path that is a link is refused below, never left out
                }
            }
        }
        $files = New-Object 'Collections.Generic.List[string]'
        $pending = New-Object 'Collections.Generic.Stack[string]'
        foreach ($r in $roots) { if ([IO.File]::GetAttributes($r) -band [IO.FileAttributes]::Directory) { $pending.Push($r) } else { $files.Add($r) } }
        while ($pending.Count -gt 0) {
            foreach ($entry in [IO.Directory]::GetFileSystemEntries($pending.Pop())) {
                $attributes = [IO.File]::GetAttributes($entry)
                if ($attributes -band [IO.FileAttributes]::ReparsePoint) { $links.Add($entry) }
                elseif ($attributes -band [IO.FileAttributes]::Directory) { $pending.Push($entry) }
                else { $files.Add($entry) }
            }
        }
        if ($links.Count -gt 0) { 'VT-LIST links'; $links | Select-Object -First 5; exit 0 }
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            foreach ($file in $files) {
                $stream = [IO.File]::OpenRead($file)
                try { $hash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() } finally { $stream.Dispose() }
                $relative = $file.Substring($full.Length + 1).Replace('\', '/')
                'VT-FILE ' + $hash + ' ' + [Convert]::ToBase64String($utf8.GetBytes($relative))
            }
        } finally { $sha.Dispose() }
        'VT-LIST done'
        """.ReplaceLineEndings("\n");

    // Variables: source, dest, skip (names of top-level entries left out, one per line; none copies everything).
    public static readonly string Copy = """
        set -u
        if [ ! -d "$source" ]; then echo "VT-COPY missing"; exit 0; fi
        if [ -e "$dest" ]; then echo "VT-COPY exists"; exit 0; fi
        mkdir -p -- "$(dirname -- "$dest")" && mkdir -- "$dest" || exit 3
        if [ -z "${skip:-}" ]; then
            if ! cp -a -- "$source/." "$dest/"; then rm -rf -- "$dest"; exit 3; fi
        else
            shopt -s nullglob dotglob
            for entry in "$source"/*; do
                if printf '%s\n' "$skip" | grep -Fqx -- "${entry##*/}"; then continue; fi
                if ! cp -a -- "$entry" "$dest/"; then rm -rf -- "$dest"; exit 3; fi
            done
        fi
        echo "VT-COPY copied"
        """.ReplaceLineEndings("\n");

    // Variables: source, dest, skip (as the bash copy's; names compare ignoring case). The source may be a game install; only a
    // new destination can be written.
    public static readonly string PowerShellCopy = """
        if (-not [IO.Directory]::Exists($source)) { 'VT-COPY missing'; exit 0 }
        if ([IO.Directory]::Exists($dest) -or [IO.File]::Exists($dest)) { 'VT-COPY exists'; exit 0 }
        $skipped = @($skip -split "`n" | Where-Object { $_ })
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest))
        [void][IO.Directory]::CreateDirectory($dest)
        try {
            $pending = New-Object 'Collections.Generic.Stack[string]'
            $pending.Push($source)
            while ($pending.Count -gt 0) {
                $from = $pending.Pop()
                $relative = $from.Substring($source.TrimEnd('\', '/').Length).TrimStart('\', '/')
                $to = if ($relative) { Join-Path $dest $relative } else { $dest }
                [void][IO.Directory]::CreateDirectory($to)
                foreach ($entry in [IO.Directory]::GetFileSystemEntries($from)) {
                    if (-not $relative -and $skipped -contains [IO.Path]::GetFileName($entry)) { continue }
                    $attributes = [IO.File]::GetAttributes($entry)
                    if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Refusing link in runtime: ' + $entry) }
                    if ($attributes -band [IO.FileAttributes]::Directory) { $pending.Push($entry) }
                    else { [IO.File]::Copy($entry, (Join-Path $to ([IO.Path]::GetFileName($entry)))) }
                }
            }
        } catch { [IO.Directory]::Delete($dest, $true); throw }
        'VT-COPY copied'
        """.ReplaceLineEndings("\n");

    // Variables: port. A listening socket is state 0A; the local address's port is the hex after its last colon.
    public static readonly string BashPort = """
        set -u
        if [ ! -r /proc/net/tcp ]; then echo "VT-PORT unknown"; exit 0; fi
        hex=$(printf '%04X' "$port") || exit 3
        for f in /proc/net/tcp /proc/net/tcp6; do
            [ -r "$f" ] || continue
            while read -r _ local _ state _; do
                if [ "$state" = 0A ] && [ "${local##*:}" = "$hex" ]; then echo "VT-PORT busy"; exit 0; fi
            done < "$f"
        done
        echo "VT-PORT free"
        """.ReplaceLineEndings("\n");

    // Variables: port.
    public static readonly string PowerShellPort = """
        $listeners = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
        if (@($listeners | Where-Object { $_.Port -eq [int]$port }).Count -gt 0) { 'VT-PORT busy' } else { 'VT-PORT free' }
        """.ReplaceLineEndings("\n");
}
