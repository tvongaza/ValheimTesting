using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The files of a directory on a host as <see cref="HostInstall.ListAsync"/> found them: every regular file's SHA256 by its
/// relative path with <c>/</c> separators (the same manifest <see cref="WorldFixture.Manifest"/> records for a local
/// directory) and, on a bash host, which game executables at the root carry
/// the user-execute bit.
/// </summary>
[ResultShape]
public sealed class HostListing
{
    internal HostListing(string hostName, HostShellKind shell, string root, IReadOnlyDictionary<string, string> files,
        IReadOnlyList<string> executables, IReadOnlyDictionary<string, HostFileState>? states = null)
    { HostName = hostName; Shell = shell; Root = root; Files = files; Executables = executables; States = states ?? new Dictionary<string, HostFileState>(); }
    public string HostName { get; }
    public HostShellKind Shell { get; }
    /// <summary>The listed directory on the host.</summary>
    public string Root { get; }
    /// <summary>Relative path (<c>/</c> separators) to lower-case SHA256.</summary>
    public IReadOnlyDictionary<string, string> Files { get; }
    /// <summary>Bash hosts: which dedicated-server or client executables the user may execute, including <c>valheim_server/Valheim</c> on macOS.</summary>
    public IReadOnlyList<string> Executables { get; }
    /// <summary>The size and host-native modified-time token captured beside each hash, for the profile source integrity check.</summary>
    internal IReadOnlyDictionary<string, HostFileState> States { get; }
    /// <summary>Paths compare ignoring case on a Windows (PowerShell) host.</summary>
    internal StringComparer Names => Shell == HostShellKind.PowerShell ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

/// <summary>A file's cheap identity on one host. Modified is compared only with a later listing from that same host.</summary>
internal sealed record HostFileState(long Size, string Modified);

/// <summary>
/// Reads game installs and their profiles on a host (never copying an install), so a run on another machine keeps the same pins as one here: the
/// runtime manifest, the game build, loader and patchers (<see cref="InstallPins"/>) and the server's
/// execute bit are all checked against what the host holds, not against a local copy.
/// </summary>
public static class HostInstall
{
    private static readonly Regex Sha256Line = new(@"^(\\?)([0-9a-f]{64}) [ *]\./(.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex FileLine = new(@"^VT-FILE ([0-9a-f]{64}) ([A-Za-z0-9+/=]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex StateLine = new(@"^VT-STATE ([0-9]+) ([^ ]+) ([A-Za-z0-9+/=]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex GameAssembly = new(@"^(?<managed>[^/]+_Data/Managed|[^/]+\.app/Contents/Resources/Data/Managed|valheim_server/Data/Managed)/assembly_valheim\.dll$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    internal static readonly string[] ProfilePinPaths = [.. InstallPins.PinPaths, "BepInEx/plugins", "BepInEx/scripts"];

    /// <summary>
    /// Hashes every regular file under <paramref name="root"/> on the host (links are refused, as <see cref="WorldFixture"/>
    /// refuses them; so is a pattern that passes through a linked directory on the way to a named path).
    /// <paramref name="paths"/> limits the listing to those subdirectories and files: relative patterns whose
    /// segments are separated by <c>/</c> or <c>\</c>, with <c>*</c> allowed in a directory, for example <c>*_Data/Managed</c>,
    /// or at the file name, for example <c>save_backup_auto-*</c>. A pattern that starts with a separator or a drive, or holds an
    /// empty segment, one ending in <c>.</c> or a space (<c>..</c> included) or one starting with <c>.</c> that holds a wildcard,
    /// is refused before anything runs on the host.
    /// Null lists everything. The patcher entries and executables are always read.
    /// </summary>
    public static Task<HostListing> ListAsync(IGameHost host, string root, TimeSpan timeout, IReadOnlyList<string>? paths = null,
        CancellationToken cancellation = default) => ListCoreAsync(host, root, timeout, paths, cancellation,
            ignoreCase: false, content: true, metadata: false);

    /// <summary>The preparation row's one full content listing, with metadata retained for the post-run comparison.</summary>
    internal static Task<HostListing> ListProfileSourceAsync(IGameHost host, string root, TimeSpan timeout,
        CancellationToken cancellation = default) => ListCoreAsync(host, root, timeout, null, cancellation,
            ignoreCase: false, content: true, metadata: true);

    /// <summary>Lists names, sizes and modified times without reading file contents.</summary>
    internal static Task<HostListing> ListMetadataAsync(IGameHost host, string root, TimeSpan timeout,
        CancellationToken cancellation = default) => ListCoreAsync(host, root, timeout, null, cancellation,
            ignoreCase: false, content: false, metadata: true);

    /// <summary>
    /// <see cref="ListAsync"/> with every segment of every pattern compared ignoring case (on a bash host as its locale folds
    /// letters, ASCII letters at least; on Windows as the filesystem does). The
    /// patterns are globs, used as written: <c>*</c> matches any run of characters within a segment and <c>?</c> one character
    /// (on a bash host <c>[</c> opens a character class). A caller that names a literal file passes the name through
    /// <see cref="LiteralPattern"/> first.
    /// </summary>
    internal static Task<HostListing> ListCaseInsensitiveAsync(IGameHost host, string root, TimeSpan timeout,
        IReadOnlyList<string> paths, CancellationToken cancellation = default) =>
        ListCoreAsync(host, root, timeout, paths, cancellation, ignoreCase: true, content: true, metadata: false);

    private static async Task<HostListing> ListCoreAsync(IGameHost host, string root, TimeSpan timeout, IReadOnlyList<string>? paths,
        CancellationToken cancellation, bool ignoreCase, bool content, bool metadata)
    {
        ArgumentNullException.ThrowIfNull(host);
        RequireHostPath(host, root, nameof(root));
        var patterns = ListingPatterns(paths ?? Array.Empty<string>());
        var result = (await host.RunAsync(HostInstallScripts.List(host.Shell.Kind), new Dictionary<string, string>
        {
            ["root"] = root, ["dirs"] = string.Join('\n', patterns), ["nocase"] = ignoreCase ? "1" : "0",
            ["content"] = content ? "1" : "0", ["metadata"] = metadata ? "1" : "0",
        }, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Listing {root} on {host.Name}");
        return ReadListing(host.Name, host.Shell.Kind, root, result);
    }

    private static readonly Regex Drive = new("^[A-Za-z]:", RegexOptions.CultureInvariant);

    /// <summary>
    /// The listing patterns as both listing scripts receive them, with <c>/</c> separators: refuses a pattern that is blank,
    /// holds a control character, starts with a separator or a drive (<c>C:</c>), or has an empty segment, one ending in
    /// <c>.</c> or a space (<c>.</c>, <c>..</c>, and names Windows trims) or one starting with <c>.</c> that holds a wildcard
    /// (a glob such as <c>.*</c>, which bash before 5.2 expands to <c>..</c>), each of which a bash and a PowerShell host would
    /// read differently or outside the root.
    /// </summary>
    internal static string[] ListingPatterns(IEnumerable<string> paths) => paths.Select(path =>
    {
        string pattern = (path ?? "").Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Any(char.IsControl) || pattern.StartsWith('/') || Drive.IsMatch(pattern) ||
            pattern.Split('/').Any(segment => segment.Length == 0 || segment[^1] is '.' or ' ' ||
                (segment[0] == '.' && segment.IndexOfAny(['*', '?', '[']) >= 0)))
            throw new ArgumentException($"'{path}' is not a relative path inside the root.", nameof(paths));
        return pattern;
    }).ToArray();

    /// <summary>
    /// A listing pattern for the single file or directory named <paramref name="name"/>: each character either host's glob
    /// reads as a wildcard or a separator (<c>*</c>, <c>?</c>, <c>[</c>, <c>\</c>) becomes <c>?</c>, the one wildcard both hosts
    /// share. The pattern may therefore also match names that differ only there; compare the listing's names exactly.
    /// </summary>
    internal static string LiteralPattern(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Contains('/'))
            throw new ArgumentException($"'{name}' is not a file name.", nameof(name));
        return string.Concat(name.Select(c => c is '*' or '?' or '[' or '\\' ? '?' : c));
    }

    /// <summary>A relative path as a pattern that names it literally on either host listing script.</summary>
    internal static string LiteralPathPattern(string path) => string.Join('/', path.Replace('\\', '/').Split('/').Select(LiteralPattern));

    /// <summary>Whether a listed relative path is selected by one of this type's segment-glob patterns.</summary>
    internal static bool MatchesAny(string relative, IEnumerable<string> patterns, bool ignoreCase)
    {
        string text = relative.Replace('\\', '/');
        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        return patterns.Any(pattern =>
        {
            string expression = "^" + Regex.Escape(pattern.Replace('\\', '/'))
                .Replace("\\*", "[^/]*", StringComparison.Ordinal).Replace("\\?", "[^/]", StringComparison.Ordinal) + "$";
            for (string candidate = text; ;)
            {
                if (Regex.IsMatch(candidate, expression, options)) return true;
                int slash = candidate.LastIndexOf('/');
                if (slash < 0) return false;
                candidate = candidate[..slash];
            }
        });
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
        var states = new Dictionary<string, HostFileState>(names);
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
            else if (StateLine.Match(line) is { Success: true } state)
            {
                relative = Decode(state.Groups[3].Value).Replace('\\', '/');
                var found = new HostFileState(long.Parse(state.Groups[1].Value, CultureInfo.InvariantCulture), state.Groups[2].Value);
                if (states.TryGetValue(relative, out var seenState) && seenState != found)
                    throw new IOException($"{relative} changed while {root} on {hostName} was listed.");
                states[relative] = found;
                continue;
            }
            else if (line.StartsWith("VT-STATES ", StringComparison.Ordinal))
            {
                string packed = Encoding.UTF8.GetString(Convert.FromBase64String(line["VT-STATES ".Length..]));
                foreach (string record in packed.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    int first = record.IndexOf(' '), second = first < 0 ? -1 : record.IndexOf(' ', first + 1);
                    if (first < 1 || second < first + 2)
                        throw new HostOperationException($"Unexpected packed metadata while listing {root} on {hostName}", result);
                    relative = record[(second + 1)..].Replace('\\', '/');
                    var found = new HostFileState(long.Parse(record[..first], CultureInfo.InvariantCulture), record[(first + 1)..second]);
                    if (states.TryGetValue(relative, out var seenState) && seenState != found)
                        throw new IOException($"{relative} changed while {root} on {hostName} was listed.");
                    states[relative] = found;
                }
                continue;
            }
            else if (line.StartsWith("VT-EXEC ", StringComparison.Ordinal)) { executables.Add(line["VT-EXEC ".Length..]); continue; }
            else throw new HostOperationException($"Unexpected line while listing {root} on {hostName}: {line}", result);
            relative = relative.Replace('\\', '/');
            // Overlapping directories list a file twice; its hash is the same both times.
            if (files.TryGetValue(relative, out string? seen) && seen != sha256) throw new IOException($"{relative} changed while {root} on {hostName} was listed.");
            files[relative] = sha256;
        }
        if (files.Count != 0 && states.Count != 0 && !files.Keys.ToHashSet(names).SetEquals(states.Keys))
            throw new HostOperationException($"The content and metadata listings of {root} on {hostName} named different files", result);
        return new HostListing(hostName, shell, root, files, executables, states);
    }

    /// <summary><paramref name="files"/> (a manifest or listing) without what lies under a skipped top-level directory.</summary>
    internal static Dictionary<string, string> WithoutSkipped(IReadOnlyDictionary<string, string> files, IReadOnlyCollection<string> skip, StringComparer names) =>
        files.Where(file => !Skipped(file.Key, skip, names)).ToDictionary(file => file.Key, file => file.Value, names);

    // Whatever the top-level entry is (a folder, a file or a link), by its name.
    internal static bool Skipped(string relative, IReadOnlyCollection<string> skip, StringComparer names)
    {
        int slash = relative.IndexOfAny(['/', '\\']);
        return skip.Contains(slash < 0 ? relative : relative[..slash], names);
    }

    /// <summary>
    /// Refuses, on the host, each of <paramref name="paths"/> that is <paramref name="root"/>, lies inside it or holds it, once
    /// every link on the way is resolved there (<see cref="ProtectedPaths"/>' rule, applied on the host by its script fragments):
    /// a destination reached through a link into an install is inside it. A path whose links the host cannot resolve is refused
    /// too. <paramref name="what"/> names the root in the message, for example "the source install".
    /// </summary>
    internal static async Task RequireApartAsync(IGameHost host, string root, IReadOnlyList<string> paths, string what, TimeSpan timeout,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(host);
        RequireHostPath(host, root, nameof(root));
        foreach (string path in paths) RequireHostPath(host, path, nameof(paths));
        var result = (await host.RunAsync(HostInstallScripts.Apart(host.Shell.Kind), new Dictionary<string, string>
            { ["root"] = root, ["paths"] = string.Join('\n', paths) }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Checking that paths on {host.Name} lie outside {root}");
        string verdict = InteractiveClient.Line(result.Stdout, "VT-APART ") ?? throw new HostOperationException($"No path verdict from {host.Name}", result);
        string[] parts = verdict.Split(' ', 2);
        string Named() => parts.Length == 2 ? Decode(parts[1]) : "?";
        switch (parts[0])
        {
            case "ok": return;
            case "overlap": throw new ArgumentException($"{Named()} on {host.Name} overlaps {what} {root} once links are resolved there; give a directory outside it.");
            case "unknown": throw new IOException($"{host.Name} cannot resolve the links of {Named()} or {root}, so whether it lies inside {what} is unknown; refusing.");
            default: throw new HostOperationException($"Unexpected path verdict from {host.Name}", result);
        }
    }

    /// <summary>
    /// Refuses when anything on the host listens on TCP <paramref name="port"/> (any address), so a command can never reach a
    /// process this run does not own. Bash reads <c>/proc/net/tcp</c> (Linux) or <c>netstat</c>'s listeners (macOS); PowerShell asks
    /// .NET for the active listeners.
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
            case "unknown": throw new PlatformNotSupportedException($"{host.Name} cannot tell which ports are in use (neither /proc/net/tcp nor macOS's netstat table); use bash on Linux or macOS, or PowerShell on Windows.");
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

    /// <summary>The <see cref="InstallPins"/> of a listing that covers <see cref="InstallPins.PinPaths"/>.</summary>
    public static InstallPins Pins(HostListing listing) => Pins(listing, out _);

    /// <summary>
    /// The same install pins when Doorstop and BepInEx live in an owned profile beside an unchanged game install.
    /// Game files come only from <paramref name="game"/>; loader and patcher files come only from
    /// <paramref name="profile"/>. No copied game files are needed to establish their combined identity.
    /// </summary>
    internal static InstallPins Pins(HostListing game, HostListing profile) => Pins(Combined(game, profile));

    internal static InstallPins CheckPins(InstallPins pinned, HostListing game, HostListing profile, string kind) =>
        CheckPins(pinned, Combined(game, profile), kind);

    // Hard links can retain old bytes after Steam atomically replaces a source file. The one source-check owner decides the
    // narrow paths re-read in both the launch folder and original game immediately before a profile process starts.
    internal static async Task CheckProfilePinsAsync(IGameHost host, InstallPins pinned, string gameRoot,
        string loaderRoot, ProfileSourceCheck sourceCheck, string kind, TimeSpan timeout, CancellationToken cancellation,
        IReadOnlyDictionary<string, string>? pluginFiles = null)
    {
        bool oneProfileRoot = loaderRoot == gameRoot;
        bool gameIsSource = sourceCheck.Source.Root == gameRoot;
        var game = await ListAsync(host, gameRoot, timeout,
            gameIsSource ? sourceCheck.SourceBeforeBootPaths() :
                sourceCheck.ProfileBeforeBootPaths(pluginFiles != null && oneProfileRoot), cancellation).ConfigureAwait(false);
        var loader = loaderRoot == gameRoot ? game :
            await ListAsync(host, loaderRoot, timeout, sourceCheck.LoaderBeforeBootPaths(pluginFiles != null), cancellation).ConfigureAwait(false);
        var source = gameIsSource ? game :
            await ListAsync(host, sourceCheck.Source.Root, timeout, sourceCheck.SourceBeforeBootPaths(), cancellation).ConfigureAwait(false);
        RequireSame(sourceCheck.BeforeBoot, source, "source game before profile launch");
        if (sourceCheck.StagedBeforeBoot.Count != 0)
        {
            var staged = loader.Files.Where(file => sourceCheck.StagedBeforeBoot.ContainsKey(file.Key) || sourceCheck.InStagedFolder(file.Key))
                .ToDictionary(file => file.Key, file => file.Value, loader.Names);
            RequireSame(sourceCheck.StagedBeforeBoot, new HostListing(loader.HostName, loader.Shell, loader.Root, staged, []),
                kind + " staged files");
        }
        if (sourceCheck.LinkedRootFiles.Count != 0)
        {
            var native = game.Files.Where(file => sourceCheck.LinkedRootFiles.ContainsKey(file.Key))
                .ToDictionary(file => file.Key, file => file.Value, game.Names);
            RequireSame(sourceCheck.LinkedRootFiles, new HostListing(game.HostName, game.Shell, game.Root, native, []),
                kind + " linked root files");
        }
        if (!Pins(source, loader).Game.Equals(pinned.Game, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The source game changed after its loader profile was staged for {kind}; refuse this launch and prepare a new profile.");
        CheckPins(pinned, game, loader, kind);
        if (pluginFiles != null)
        {
            var found = loader.Files.Where(file => file.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                (file.Key.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) ||
                 file.Key.StartsWith("BepInEx/scripts/", StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(file => file.Key, file => file.Value, loader.Names);
            RequireSame(pluginFiles, new HostListing(loader.HostName, loader.Shell, loader.Root, found, []), kind + " plugins");
        }
    }

    private static HostListing Combined(HostListing game, HostListing profile)
    {
        if (game.HostName != profile.HostName || game.Shell != profile.Shell)
            throw new ArgumentException("The game and profile listings must come from the same host.");
        var files = new Dictionary<string, string>(game.Names);
        foreach (var (path, hash) in game.Files)
            if (!path.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase) &&
                !InstallPins.IsLoaderFile(path, game.Shell == HostShellKind.PowerShell
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                files.Add(path, hash);
        foreach (var (path, hash) in profile.Files)
            if (path.StartsWith("BepInEx/patchers/", StringComparison.OrdinalIgnoreCase) ||
                InstallPins.IsLoaderFile(path, game.Shell == HostShellKind.PowerShell
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                files.Add(path, hash);
        return new HostListing(game.HostName, game.Shell, game.Root, files, game.Executables);
    }

    private static InstallPins Pins(HostListing listing, out string managed)
    {
        var assemblies = listing.Files.Keys.Select(key => GameAssembly.Match(key)).Where(match => match.Success).Select(match => match.Groups["managed"].Value).Order(StringComparer.Ordinal).ToList();
        if (assemblies.Count == 0)
            throw new FileNotFoundException($"No game assembly ({InstallPins.GameAssemblyName} in a *_Data/Managed, macOS client or macOS dedicated-server Managed folder) under {listing.Root} on {listing.HostName}.");
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
        bool mac = listing.Files.ContainsKey(GameLaunch.ServerMacExecutable) && listing.Files.Keys.Any(path => path.StartsWith("valheim_server/Data/", StringComparison.Ordinal));
        if ((windows ? 1 : 0) + (linux ? 1 : 0) + (mac ? 1 : 0) > 1)
            throw new InvalidOperationException($"The runtime on {listing.HostName} contains more than one dedicated-server platform; refusing to guess.");
        if (windows) return ServerPlatform.Windows;
        if (linux) return ServerPlatform.Linux;
        if (mac) return ServerPlatform.MacOS;
        throw new FileNotFoundException($"The runtime at {listing.Root} on {listing.HostName} contains no complete Windows, Linux or macOS dedicated server.");
    }

    internal static void RequireHostPath(IGameHost host, string path, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(path, name);
        if (path.Any(char.IsControl) || !ScriptedGameHost.IsAbsolute(host.Shell.Kind, path))
            throw new ArgumentException($"{name} must be an absolute path on {host.Name}; '{path}' would depend on the user's home or working directory.", name);
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
    public static string List(HostShellKind kind) => kind == HostShellKind.Bash ? BashList : PowerShellList;
    public static string Port(HostShellKind kind) => kind == HostShellKind.Bash ? BashPort : PowerShellPort;
    public static string Apart(HostShellKind kind) => kind == HostShellKind.Bash ? BashApart : PowerShellApart;

    // Variables: root, paths (one per line). The first path that is the root, lies inside it or holds it, once links are resolved
    // on the host (ProtectedPaths.BashInside), is named as "overlap"; one whose links cannot be resolved as "unknown".
    public static readonly string BashApart = "set -u\n" + ProtectedPaths.BashInside + """
        while IFS= read -r path; do
            [ -n "$path" ] || continue
            vt_inside "$root" "$path" fold; inside=$?
            vt_inside "$path" "$root" fold; holds=$?
            named=$(printf '%s' "$path" | base64 | tr -d '\n')
            if [ "$inside" = 2 ] || [ "$holds" = 2 ]; then echo "VT-APART unknown $named"; exit 0; fi
            if [ "$inside" = 0 ] || [ "$holds" = 0 ]; then echo "VT-APART overlap $named"; exit 0; fi
        done <<< "$paths"
        echo "VT-APART ok"
        """.ReplaceLineEndings("\n");

    // Variables: root, paths (as the bash check's; ProtectedPaths.PowerShellInside).
    public static readonly string PowerShellApart = ProtectedPaths.PowerShellInside + """
        foreach ($path in ($paths -split "`n")) {
            if (-not $path) { continue }
            $named = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($path))
            try { $overlap = (Test-VtInside $root $path) -or (Test-VtInside $path $root) } catch { 'VT-APART unknown ' + $named; exit 0 }
            if ($overlap) { 'VT-APART overlap ' + $named; exit 0 }
        }
        'VT-APART ok'
        """.ReplaceLineEndings("\n");

    // Variables: root, dirs (relative directory patterns and file names with "/" separators, one per line, as
    // HostInstall.ListingPatterns checked them; none lists everything), nocase (1: compare every segment ignoring case),
    // content (1 hashes contents) and metadata (1 records size and modified time for the profile source check).
    // sha256sum prints each file as "<hash>  ./<path>", escaping a name with a backslash or line break. Links are refused before
    // anything is hashed, whether a link is listed itself or lies on the way to a named path.
    public static readonly string BashList = """
        set -u
        set -o pipefail
        if [ ! -d "$root" ]; then echo "VT-LIST missing"; exit 0; fi
        cd -- "$root" || exit 3
        host_os=$(uname -s) || exit 3
        shopt -s nullglob dotglob
        if [ "${nocase:-0}" = 1 ]; then shopt -s nocasematch; fi
        if command -v sha256sum > /dev/null 2>&1; then hasher=(sha256sum); else hasher=(shasum -a 256); fi
        roots=()
        way=()
        if [ -z "$dirs" ]; then roots=(.); else
            while IFS= read -r pattern; do
                [ -n "$pattern" ] || continue
                # A glob never folds the case of a segment without a wildcard, so resolve the pattern a segment at a time. A
                # segment without a wildcard names its entry directly in the case-sensitive mode (the filesystem decides what
                # equals it); any other segment keeps each entry whose name the segment, as written, matches ("$dir"/* never yields
                # . or ..). A directory link matched before the last segment is recorded and never entered; a named entry that
                # is a link is kept for the link check below.
                found=(.)
                IFS=/ read -ra segments <<< "$pattern"
                last=$(( ${#segments[@]} - 1 )); position=0
                for segment in "${segments[@]}"; do
                    literal=0
                    if [ "${nocase:-0}" != 1 ]; then case "$segment" in *[*?[]*) ;; *) literal=1;; esac; fi
                    next=()
                    for dir in "${found[@]}"; do
                        candidates=()
                        if [ "$literal" = 1 ]; then
                            if [ -e "$dir/$segment" ] || [ -L "$dir/$segment" ]; then candidates=("$dir/$segment"); fi
                        else
                            for entry in "$dir"/*; do
                                case "${entry##*/}" in $segment) candidates+=("$entry");; esac
                            done
                        fi
                        for entry in ${candidates[@]+"${candidates[@]}"}; do
                            if [ "$position" -lt "$last" ] && [ -L "$entry" ] && [ -d "$entry" ]; then way+=("$entry"); else next+=("$entry"); fi
                        done
                    done
                    position=$((position + 1))
                    found=()
                    if [ "${#next[@]}" -eq 0 ]; then break; fi
                    found=("${next[@]}")
                done
                if [ "${#found[@]}" -gt 0 ]; then roots+=("${found[@]}"); fi
            done <<< "$dirs"
        fi
        # A link on the way to a named path is a link too; nothing is hashed before the refusal. Each is listed once.
        links=$({ for linked in ${way[@]+"${way[@]}"}; do printf '%s\n' "$linked"; done
                  if [ "${#roots[@]}" -gt 0 ]; then find "${roots[@]}" -type l -print; fi; } | awk '!seen[$0]++' | head -n 5) || exit 3
        if [ -n "$links" ]; then echo "VT-LIST links"; printf '%s\n' "$links"; exit 0; fi
        if [ "${content:-1}" = 1 ] && [ "${#roots[@]}" -gt 0 ]; then
            find "${roots[@]}" -type f -exec "${hasher[@]}" {} + || exit 3
        fi
        if [ "${metadata:-0}" = 1 ] && [ "${#roots[@]}" -gt 0 ]; then
            # Metadata callers list the whole source (roots=(.)); keep find and stat batched. GNU find supplies size,
            # fractional epoch mtime and root-relative name in one process. BSD stat receives up to 200 files per call,
            # and its floating-point %Fm retains the host's sub-second precision.
            if [ "$host_os" != Darwin ]; then
                packed=$(find . -type f -printf '%s %T@ %P\0' | base64 -w 0) || exit 3
            else
                vt_bsd_states() {
                    [ "$#" -ne 0 ] || return 0
                    local states=() state file i=0
                    while IFS= read -r state; do states+=("$state"); done < <(/usr/bin/stat -f '%z %Fm' "$@")
                    [ "${#states[@]}" -eq "$#" ] || return 3
                    for file in "$@"; do
                        printf '%s %s\0' "${states[$i]}" "${file#./}"
                        i=$((i + 1))
                    done
                }
                vt_all_bsd_states() {
                    local files=() file
                    while IFS= read -r -d '' file; do
                        files+=("$file")
                        if [ "${#files[@]}" -eq 200 ]; then vt_bsd_states "${files[@]}" || return 3; files=(); fi
                    done < <(find . -type f -print0)
                    vt_bsd_states "${files[@]}" || return 3
                }
                packed=$(vt_all_bsd_states | /usr/bin/base64 -b 0) || exit 3
            fi
            printf 'VT-STATES %s\n' "$packed"
        fi
        for e in valheim_server.x86_64 valheim.x86_64 valheim_server/Valheim; do if [ -f "$e" ] && [ -x "$e" ]; then echo "VT-EXEC $e"; fi; done
        echo "VT-LIST done"
        """.ReplaceLineEndings("\n");

    // Variables: root, dirs, content and metadata (as the bash listing's; names compare ignoring case, so nocase changes nothing). Hashes with .NET
    // (see HostScripts' note on Get-FileHash); names travel as base64.
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
                $segments = @($pattern -split '/')
                for ($i = 0; $i -lt $segments.Count; $i++) {
                    $segment = $segments[$i]
                    $next = @()
                    foreach ($dir in $current) {
                        if ($segment.IndexOfAny([char[]]'*?') -ge 0) {
                            $next += [IO.Directory]::GetDirectories($dir, $segment)
                            if ($i -eq $segments.Count - 1) { $next += [IO.Directory]::GetFiles($dir, $segment) }
                        }
                        elseif ([IO.Directory]::Exists((Join-Path $dir $segment))) { $next += (Join-Path $dir $segment) }
                        elseif ($i -eq $segments.Count - 1 -and [IO.File]::Exists((Join-Path $dir $segment))) { $next += (Join-Path $dir $segment) }
                    }
                    if ($i -lt $segments.Count - 1) {
                        # A linked directory on the way to the named path is refused below, never entered (a link with nothing behind it has nothing to enter).
                        $kept = @()
                        foreach ($candidate in $next) {
                            if (-not ([IO.File]::GetAttributes($candidate) -band [IO.FileAttributes]::ReparsePoint)) { $kept += $candidate; continue }
                            # A link on the way is refused once its target opens, whatever the target holds (an empty or unlistable
                            # one included); only a link that leads nowhere has nothing to enter and is dropped.
                            $opens = $true
                            try {
                                $entries = [IO.Directory]::EnumerateFileSystemEntries($candidate).GetEnumerator()
                                try { $null = $entries.MoveNext() } finally { $entries.Dispose() }
                            } catch [IO.DirectoryNotFoundException] { $opens = $false } catch [IO.FileNotFoundException] { $opens = $false } catch { }
                            # Any other error (access denied, a loop) leaves it refused: unlistable, never entered and never an error here.
                            if ($opens) { $links.Add($candidate) }
                        }
                        $next = $kept
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
        if ($links.Count -gt 0) { 'VT-LIST links'; $links | Select-Object -Unique | Select-Object -First 5; exit 0 }
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            foreach ($file in $files) {
                $relative = $file.Substring($full.Length + 1).Replace('\', '/')
                $named = [Convert]::ToBase64String($utf8.GetBytes($relative))
                if ($content -ne '0') {
                    $stream = [IO.File]::OpenRead($file)
                    try { $hash = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() } finally { $stream.Dispose() }
                    'VT-FILE ' + $hash + ' ' + $named
                }
                if ($metadata -eq '1') {
                    $info = New-Object IO.FileInfo $file
                    'VT-STATE ' + $info.Length + ' ' + $info.LastWriteTimeUtc.Ticks + ' ' + $named
                }
            }
        } finally { $sha.Dispose() }
        'VT-LIST done'
        """.ReplaceLineEndings("\n");

    // Variables: port. Linux: a listening socket is state 0A; the local address's port is the hex after its last colon. macOS
    // (no /proc): netstat's LISTEN lines, whose local address ends in .<port> (127.0.0.1.5688, *.5688, ::1.5688).
    public static readonly string BashPort = """
        set -u
        if [ ! -r /proc/net/tcp ]; then
            if [ "$(uname -s)" = Darwin ] && listing=$(netstat -an -p tcp 2>/dev/null); then
                # 0 busy, 1 free, anything else (no header: not netstat's table, or awk failed) unknown: never "free" by default.
                printf '%s\n' "$listing" | awk -v p="$port" '$1 == "Proto" { header = 1 } $NF == "LISTEN" { n = split($4, a, "."); if (a[n] == p) found = 1 }
                    END { if (!header) exit 2; exit found ? 0 : 1 }'
                case $? in 0) echo "VT-PORT busy";; 1) echo "VT-PORT free";; *) echo "VT-PORT unknown";; esac
                exit 0
            fi
            echo "VT-PORT unknown"; exit 0
        fi
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
