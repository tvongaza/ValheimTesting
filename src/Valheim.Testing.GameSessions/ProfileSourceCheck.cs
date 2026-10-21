using System.Globalization;
using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The one owner of a disposable profile's source checks: the preparation snapshot, copied-file staging scope,
/// narrow before-boot scope, and the whole-source settlement after the run (#612): what the game added to the install is moved
/// out into the run's evidence, the install is listed again against the persisted baseline, and <see cref="SourceVerdict"/> says
/// what was found. A recovery settles from the journalled baseline (<see cref="FromJournalAsync"/>).
/// </summary>
internal sealed class ProfileSourceCheck
{
    internal const string HashAfterRunVariable = "VALHEIM_TESTING_SOURCE_HASH_AFTER_RUN";
    internal static AsyncLocal<bool?> HashAfterRunOverride { get; } = new();
    /// <summary>The baseline and the moved files, in the profile's parent folder on its host (beside <c>runtime</c> and <c>staging</c>).</summary>
    internal const string BaselineFile = "source-baseline.json", AddedFolder = "source-added";
    /// <summary>The most a persisted baseline may hold when a recovery reads it back.</summary>
    private const long BaselineLimit = 64L << 20;

    private readonly IReadOnlyDictionary<string, string> _beforeBoot;
    private readonly IReadOnlyDictionary<string, string> _linkedRootFiles;
    private IReadOnlyDictionary<string, string> _stagedBeforeBoot = new Dictionary<string, string>();
    private HostListing? _preparedGame, _preparedLoader;
    // A baseline read back from its journal keeps the comparison its run persisted.
    private bool? _contentMode;

    private ProfileSourceCheck(HostListing source, IReadOnlyDictionary<string, string> beforeBoot,
        IReadOnlyDictionary<string, string> linkedRootFiles)
    { Source = source; _beforeBoot = beforeBoot; _linkedRootFiles = linkedRootFiles; }

    internal HostListing Source { get; }
    internal IReadOnlyDictionary<string, string> BeforeBoot => _beforeBoot;
    internal IReadOnlyDictionary<string, string> LinkedRootFiles => _linkedRootFiles;
    internal IReadOnlyDictionary<string, string> StagedBeforeBoot => _stagedBeforeBoot;

    // steam-runtime/ is on DYLD_LIBRARY_PATH, so the whole folder is compared before boot: an extra file there could load.
    internal const string SteamRuntimeFolder = "steam-runtime";
    internal bool StagesSteamRuntime => _stagedBeforeBoot.Keys.Any(key => key.StartsWith(SteamRuntimeFolder + "/", StringComparison.Ordinal));
    internal bool InStagedFolder(string key) => StagesSteamRuntime && key.StartsWith(SteamRuntimeFolder + "/", StringComparison.Ordinal);
    internal HostListing PreparedGame => _preparedGame ?? throw new InvalidOperationException("The source-check owner has no prepared game listing.");
    internal HostListing PreparedLoader => _preparedLoader ?? throw new InvalidOperationException("The source-check owner has no prepared loader listing.");
    internal bool HashAfterRun => _contentMode ?? HashAfterRunOverride.Value ?? Environment.GetEnvironmentVariable(HashAfterRunVariable) == "1";

    /// <summary>The preparation row: retain the one complete content listing and derive every later narrow scope from it.</summary>
    internal static ProfileSourceCheck Prepare(HostListing source, IEnumerable<string> linkedFiles)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Files.Keys.ToHashSet(source.Names).SetEquals(source.States.Keys))
            throw new InvalidDataException($"The preparation listing of {source.Root} on {source.HostName} has no size and modified time for every file.");
        // Every root file selected by the Windows link plan is a write-through hard link, including the game and crash-handler
        // executables. Nested game data is covered by the managed-code pins; it is not promoted into this root-file row.
        string[] rootFiles = linkedFiles.Where(path => !path.Contains('/')).ToArray();
        string[] paths = [.. InstallPins.ManagedFolders, .. rootFiles.Select(HostInstall.LiteralPathPattern)];
        bool ignoreCase = source.Shell == HostShellKind.PowerShell;
        Dictionary<string, string> Expected(IEnumerable<string> selected) => source.Files
            .Where(file => HostInstall.MatchesAny(file.Key, selected, ignoreCase))
            .ToDictionary(file => file.Key, file => file.Value, source.Names);
        return new ProfileSourceCheck(source, Expected(paths), Expected(rootFiles.Select(HostInstall.LiteralPathPattern)));
    }

    /// <summary>The preparation row's one complete source listing. No stage or launch caller chooses a full-source rule.</summary>
    internal static Task<HostListing> InspectAsync(IGameHost host, string root, TimeSpan timeout,
        CancellationToken cancellation = default) => ListAsync(host, root, content: true, timeout, cancellation);

    // The whole-root listings this owner reads: with content (preparation, a gate run's settlement, the moved files) or metadata only.
    private static Task<HostListing> ListAsync(IGameHost host, string root, bool content, TimeSpan timeout, CancellationToken cancellation) => content
        ? HostInstall.ListProfileSourceAsync(host, root, timeout, cancellation)
        : HostInstall.ListMetadataAsync(host, root, timeout, cancellation);

    /// <summary>Retains the preparation listings which a prepared launch reuses instead of listing either source-backed root again.</summary>
    internal void RecordPreparedProfile(HostListing game, HostListing loader, IEnumerable<string> stagedBeforeBoot)
    {
        _preparedGame = game;
        _preparedLoader = loader;
        var expected = new Dictionary<string, string>(loader.Names);
        foreach (string path in stagedBeforeBoot.Distinct(loader.Names))
            if (!loader.Files.TryGetValue(path, out string? hash))
                throw new InvalidDataException($"The prepared profile listing lacks its staged before-boot file: {path}.");
            else expected.Add(path, hash);
        _stagedBeforeBoot = expected;
    }

    // Called only by the clean-stopped placement after its atomic replacement and byte verification.
    internal void ReplacePreparedPlugin(string relative, string before, string after)
    {
        HostListing Replace(HostListing listing)
        {
            if (!listing.Files.TryGetValue(relative, out var hash) || hash != before)
                throw new InvalidOperationException("The prepared plugin hash differs from the stopped swap input.");
            var files = new Dictionary<string, string>(listing.Files, listing.Names) { [relative] = after };
            return new HostListing(listing.HostName, listing.Shell, listing.Root, files, listing.Executables, listing.States);
        }
        var loader = Replace(PreparedLoader);
        var game = PreparedGame.Root == PreparedLoader.Root ? Replace(PreparedGame) : PreparedGame;
        _preparedLoader = loader; _preparedGame = game;
    }

    /// <summary>A scoped staging listing; an empty selection means there is nothing to verify, never the whole directory.</summary>
    internal static async Task<HostListing?> ListSelectedAsync(IGameHost host, string root, IEnumerable<string> selected,
        TimeSpan timeout, CancellationToken cancellation = default)
    {
        string[] paths = selected.Select(HostInstall.LiteralPathPattern).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return paths.Length == 0 ? null : await HostInstall.ListAsync(host, root, timeout, paths, cancellation).ConfigureAwait(false);
    }

    /// <summary>The staging-verification row: copied loader/BepInEx files and the game pin paths, never the whole profile.</summary>
    internal static string[] StagingVerificationPaths(IEnumerable<string>? copied = null) =>
        InstallPins.PinPaths.Concat(["BepInEx"]).Concat(InstallPins.LoaderEntries)
        .Concat((copied ?? []).Select(HostInstall.LiteralPathPattern))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The before-boot row: game/loader pin paths plus root files linked into a Windows profile.</summary>
    internal string[] ProfileBeforeBootPaths(bool includePlugins) =>
        InstallPins.PinPaths.Concat(_linkedRootFiles.Keys.Select(HostInstall.LiteralPathPattern))
        .Concat(_stagedBeforeBoot.Keys.Select(HostInstall.LiteralPathPattern))
        .Concat(StagesSteamRuntime ? [SteamRuntimeFolder] : [])
        .Concat(includePlugins ? ["BepInEx/plugins", "BepInEx/scripts"] : [])
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The owned loader-profile side of the before-boot row, including signed staged outputs.</summary>
    internal string[] LoaderBeforeBootPaths(bool includePlugins) =>
        InstallPins.PinPaths.Concat(_stagedBeforeBoot.Keys.Select(HostInstall.LiteralPathPattern))
        .Concat(StagesSteamRuntime ? [SteamRuntimeFolder] : [])
        .Concat(includePlugins ? ["BepInEx/plugins", "BepInEx/scripts"] : [])
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The source side of the before-boot row: game-code pins plus root files linked or launched directly from the source.</summary>
    internal string[] SourceBeforeBootPaths() =>
        InstallPins.ManagedFolders.Concat(_linkedRootFiles.Keys.Select(HostInstall.LiteralPathPattern))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private sealed record PersistedFile(string Path, long Size, string Modified, string Sha256);
    private sealed record PersistedBaseline(string Host, string Root, string Mode, IReadOnlyList<PersistedFile> Files, IReadOnlyList<string> Directories);
    private static readonly JsonSerializerOptions s_baselineJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Writes the preparation listing (every file's size, modified time and SHA-256, and every directory) as
    /// <c>source-baseline.json</c> in the parent folder of the profile <paramref name="runtime"/> on its host, then journals
    /// <see cref="JournalEntry.SourceBaseline"/> with its path and SHA-256, before anything launches from the profile: an
    /// interrupted run's recovery settles the install from it.
    /// </summary>
    internal async Task PersistAsync(IGameHost host, string runtime, Func<JournalEntry, Task> journal, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        if (host.Name != Source.HostName) throw new ArgumentException("The source check belongs to another host.", nameof(host));
        string path = HostPath.Join(HostPath.Parent(runtime), BaselineFile);
        var baseline = new PersistedBaseline(Source.HostName, Source.Root, HashAfterRun ? SourceVerdict.ContentMode : SourceVerdict.MetadataMode,
            Source.Files.OrderBy(file => file.Key, StringComparer.Ordinal)
                .Select(file => new PersistedFile(file.Key, Source.States[file.Key].Size, Source.States[file.Key].Modified, file.Value)).ToList(),
            Source.Directories.Order(StringComparer.Ordinal).ToList());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(baseline, s_baselineJson);
        await WriteAsync(host, path, bytes, timeout, cancellation).ConfigureAwait(false);
        await journal(JournalEntry.Of(JournalEntry.SourceBaseline, ("runtime", runtime), ("sourceRoot", Source.Root), ("baseline", path),
            ("baselineSha256", FileHash.Sha256(bytes)))).ConfigureAwait(false);
    }

    /// <summary>
    /// The check a recovery settles with, from a copy's journalled fields (<see cref="JournalEntry.SourceBaseline"/>): null when
    /// none was journalled, or when the run settled the install and its baseline is gone. A baseline whose SHA-256 or root differs
    /// from the journal's is refused, before anything is moved or removed.
    /// </summary>
    internal static async Task<ProfileSourceCheck?> FromJournalAsync(IGameHost host, IReadOnlyDictionary<string, string> fields, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        if (!fields.TryGetValue("baseline", out string? path)) return null;
        string root = fields.GetValueOrDefault("sourceRoot") ?? "", sha256 = fields.GetValueOrDefault("baselineSha256") ?? "";
        byte[]? bytes = await HostClientPreflight.Read(host, path, timeout, cancellation, BaselineLimit).ConfigureAwait(false);
        if (bytes == null)
        {
            // The run settled the install and kept what it moved: the baseline goes last.
            if (fields.ContainsKey("sourceVerdict")) return null;
            throw new InvalidDataException($"its journalled source baseline {path} is missing on {host.Name}, so the source install cannot be settled; nothing was removed");
        }
        if (!FileHash.Sha256(bytes).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"the source baseline {path} on {host.Name} differs from its journalled SHA-256, so it is refused and nothing was removed");
        var baseline = JsonSerializer.Deserialize<PersistedBaseline>(bytes, s_baselineJson)
            ?? throw new InvalidDataException($"the source baseline {path} on {host.Name} is empty");
        if (baseline.Root != root || baseline.Host != host.Name)
            throw new InvalidDataException($"the source baseline {path} names another install than its journal, so it is refused and nothing was removed");
        var names = host.Shell.Kind == HostShellKind.PowerShell ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var listing = new HostListing(host.Name, host.Shell.Kind, root, baseline.Files.ToDictionary(file => file.Path, file => file.Sha256, names), [],
            baseline.Files.ToDictionary(file => file.Path, file => new HostFileState(file.Size, file.Modified), names),
            baseline.Directories.ToHashSet(names));
        return new ProfileSourceCheck(listing, new Dictionary<string, string>(), new Dictionary<string, string>())
        { _contentMode = baseline.Mode == SourceVerdict.ContentMode };
    }

    /// <summary>
    /// The post-run row, once every process launched from the profile is proven stopped: lists the whole source (metadata; a gate
    /// run, <see cref="HashAfterRunVariable"/>=1, re-hashes) against the baseline, journals the added paths
    /// (<see cref="JournalEntry.SourceMoveIntended"/>), moves each added file into the profile's <c>source-added</c> folder and
    /// removes each added folder once empty, deepest first (<see cref="SourceSettleScripts"/>: links and paths outside the install
    /// are refused), lists the install again and requires it to equal the baseline, then keeps the moved files where
    /// <paramref name="where"/> says, writes <c>source-changes.json</c> beside them and journals <see cref="JournalEntry.SourceSettled"/>.
    /// A changed or missing file is named and left as it is. Repeating it (a recovery after an interrupted one) finds the moved files
    /// gone and carries on. It never throws for what it finds on the host: a failure is the <see cref="SourceVerdictKind.Failed"/> verdict.
    /// </summary>
    internal async Task<SourceVerdict> SettleAsync(IGameHost host, SourceSettlement where, Func<JournalEntry, Task> journal, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        if (host.Name != Source.HostName) throw new ArgumentException("The source check belongs to another host.", nameof(host));
        bool content = HashAfterRun;
        string mode = content ? SourceVerdict.ContentMode : SourceVerdict.MetadataMode;
        string keep = HostPath.Join(where.Parent, AddedFolder);
        var notMoved = new Dictionary<string, string>(Source.Names);
        var removed = new List<string>();
        SourceVerdict Verdict(SourceVerdictKind kind, IReadOnlyList<SourceAddedFile> kept, Differences left, string? reason = null) =>
            new(where.Actor, host.Name, Source.Root, mode, kind, kept, left.Changed, left.Missing,
                left.Added.Select(path => new SourceNotMoved(path, notMoved.GetValueOrDefault(path) ?? "still in the install"))
                    .Concat(left.AddedDirectories.Select(path => new SourceNotMoved(path + "/", notMoved.GetValueOrDefault(path) ?? "still in the install"))).ToList(),
                removed, reason);
        var found = Differences.None;
        var added = new List<SourceAddedFile>();
        string? evidence = null;
        SourceVerdict verdict;
        try
        {
            found = Compare(await ListAsync(host, Source.Root, content, timeout, cancellation).ConfigureAwait(false), content);
            if (found.Added.Count + found.AddedDirectories.Count != 0)
            {
                // Journalled before the move: a recovery after an interruption here finds what moved in source-added.
                await journal(JournalEntry.Of(JournalEntry.SourceMoveIntended, ("runtime", where.Runtime),
                    ("moving", string.Join('\n', found.Added.Concat(found.AddedDirectories.Select(path => path + "/")))))).ConfigureAwait(false);
                await MoveAsync(host, keep, found, notMoved, removed, timeout, cancellation).ConfigureAwait(false);
                found = Compare(await ListAsync(host, Source.Root, content, timeout, cancellation).ConfigureAwait(false), content);
            }
            (added, evidence) = await KeepAsync(host, where, keep, journal, timeout, cancellation).ConfigureAwait(false);
            verdict = Verdict(found.Clean ? added.Count + removed.Count == 0 ? SourceVerdictKind.Unchanged : SourceVerdictKind.Restored : SourceVerdictKind.Failed,
                added, found);
        }
        catch (Exception error)
        {
            // An abandoned cleanup included: the install was not settled, and nothing journals that it was, so a recovery settles it.
            verdict = Verdict(SourceVerdictKind.Failed, added, found, "the settlement did not finish: " + error.Message);
        }
        if (verdict.Verdict != SourceVerdictKind.Unchanged)
            try { await WriteReportAsync(host, where, evidence, verdict, timeout, cancellation).ConfigureAwait(false); }
            catch (Exception error)
            { verdict = verdict with { Verdict = SourceVerdictKind.Failed, Reason = "its source-changes.json was not written: " + error.Message }; }
        if (cancellation.IsCancellationRequested) return verdict;
        // After the effect a lost line is a warning: the verdict stands, and a recovery would only settle an install that is settled.
        try
        {
            await journal(JournalEntry.Of(JournalEntry.SourceSettled, ("runtime", where.Runtime), ("sourceVerdict", verdict.Verdict.ToString()),
                ("added", verdict.Added.Count.ToString(CultureInfo.InvariantCulture)), ("changed", verdict.Changed.Count.ToString(CultureInfo.InvariantCulture)),
                ("missing", verdict.Missing.Count.ToString(CultureInfo.InvariantCulture)), ("notMoved", verdict.NotMoved.Count.ToString(CultureInfo.InvariantCulture)))).ConfigureAwait(false);
        }
        catch (Exception error)
        { Console.Error.WriteLine($"Warning: could not journal {JournalEntry.SourceSettled} for {where.Actor} on {host.Name}: {error.Message}"); }
        return verdict;
    }

    // What differs from the baseline: changed and missing files (and missing folders, with a trailing /), added files and folders.
    private sealed record Differences(List<SourceChangedFile> Changed, List<string> Missing, List<string> Added, List<string> AddedDirectories)
    {
        internal static Differences None => new([], [], [], []);
        internal bool Clean => Changed.Count + Missing.Count + Added.Count + AddedDirectories.Count == 0;
    }

    private Differences Compare(HostListing after, bool content)
    {
        var names = Source.Names;
        var changed = new List<SourceChangedFile>();
        if (content)
            foreach (var (path, hash) in Source.Files)
            {
                if (after.Files.TryGetValue(path, out string? now) && !now.Equals(hash, StringComparison.OrdinalIgnoreCase)) changed.Add(new(path, hash, now));
            }
        else
            foreach (var (path, state) in Source.States)
                if (after.States.TryGetValue(path, out var now) && now != state)
                    changed.Add(new(path, $"{state.Size} bytes, modified {state.Modified}", $"{now.Size} bytes, modified {now.Modified}"));
        var listed = content ? after.Files.Keys : after.States.Keys;
        var missing = Source.Files.Keys.Where(path => !listed.Contains(path, names))
            .Concat(Source.Directories.Where(path => !after.Directories.Contains(path, names)).Select(path => path + "/")).ToList();
        var added = listed.Where(path => !Source.Files.ContainsKey(path)).ToList();
        var addedDirectories = after.Directories.Where(path => !Source.Directories.Contains(path, names)).ToList();
        return new(changed.OrderBy(file => file.Path, StringComparer.Ordinal).ToList(), missing.Order(StringComparer.Ordinal).ToList(),
            added.Order(StringComparer.Ordinal).ToList(),
            // Deepest first, so a folder is empty by the time it is reached.
            addedDirectories.OrderByDescending(path => path.Count(c => c == '/')).ThenByDescending(path => path, StringComparer.Ordinal).ToList());
    }

    // The host's one settlement script: each added file into keep, then each added folder removed once empty.
    private async Task MoveAsync(IGameHost host, string keep, Differences found, Dictionary<string, string> notMoved, List<string> removed,
        TimeSpan timeout, CancellationToken cancellation)
    {
        static string Encode(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? SourceSettleScripts.PowerShell : SourceSettleScripts.Bash,
            new Dictionary<string, string>
            {
                ["root"] = Source.Root, ["keep"] = keep,
                ["files"] = string.Join('\n', found.Added.Select(Encode)), ["folders"] = string.Join('\n', found.AddedDirectories.Select(Encode)),
            }, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Moving what the run added out of {Source.Root} on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-SETTLE-REFUSED ") is { } refused)
            throw new IOException($"The source install {Source.Root} on {host.Name} was not settled: {refused}.");
        if (InteractiveClient.Line(result.Stdout, "VT-SETTLED") == null)
            throw new HostOperationException($"Unexpected reply while settling {Source.Root} on {host.Name}", result);
        foreach (string line in result.Stdout.Split('\n'))
        {
            string[] parts = line.Split(' ');
            if (parts is ["VT-NOTMOVED", var path, var reason])
                notMoved[Encoding.UTF8.GetString(Convert.FromBase64String(path))] = reason switch
                {
                    "outside" => "outside the install",
                    "link" => "a link on the way, never followed",
                    "not-a-file" => "not a regular file",
                    "kept-already" => "the moved files already hold that name",
                    "not-empty" => "the folder is not empty",
                    _ => "the move failed",
                };
            else if (parts is ["VT-RMDIR", var folder]) removed.Add(Encoding.UTF8.GetString(Convert.FromBase64String(folder)) + "/");
        }
    }

    // What the moved files folder holds, kept where the settlement says: in a run fetched to <output>/source-changes/<actor>/added and
    // dropped on the host (a folder past the size limits stays there, journalled kept for env teardown); in a recovery moved into the
    // run's recovered-<actor> folder on the host. Either way the baseline goes last. Returns the files and the local evidence folder, if any.
    private async Task<(List<SourceAddedFile> Added, string? Evidence)> KeepAsync(IGameHost host, SourceSettlement where, string keep,
        Func<JournalEntry, Task> journal, TimeSpan timeout, CancellationToken cancellation)
    {
        HostListing? moved;
        try { moved = await ListAsync(host, keep, content: true, timeout, cancellation).ConfigureAwait(false); }
        catch (DirectoryNotFoundException) { moved = null; }
        var files = moved?.Files.OrderBy(file => file.Key, StringComparer.Ordinal).ToList() ?? [];
        long Bytes(string path) => moved!.States[path].Size;
        string? evidence = null;
        var (perFile, total) = PinnedServerRun.RetainLimits(passed: false);
        bool kept = where.RecoveredIn == null && files.Count != 0 && (files.Any(file => Bytes(file.Key) > perFile) || files.Sum(file => Bytes(file.Key)) > total);
        if (kept)
            await journal(JournalEntry.Of(JournalEntry.CopyKept, ("runtime", keep), ("stage", ""), ("parent", where.Parent), ("holds", AddedFolder),
                ("why", $"what the game added to the source install is past the size limits ({DiskSpace.Format(perFile)} per file, {DiskSpace.Format(total)} in all)"))).ConfigureAwait(false);
        else if (where.RecoveredIn == null && files.Count != 0)
        {
            evidence = EvidenceFolder(where);
            await host.FetchDirectoryAsync(keep, Path.Combine(evidence, "added"), timeout, cancellation).ConfigureAwait(false);
        }
        // A folder that holds no file (only the folders a failed move made) goes: there is nothing to keep.
        var reply = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? SourceSettleScripts.PowerShellEvidence : SourceSettleScripts.BashEvidence,
            new Dictionary<string, string>
            {
                ["parent"] = where.Parent, ["added"] = evidence != null || files.Count == 0 ? "1" : "",
                ["recovered"] = files.Count == 0 ? "" : where.RecoveredIn ?? "",
            },
            timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Settling the moved files of {where.Runtime} on {host.Name}");
        if (InteractiveClient.Line(reply.Stdout, "VT-SOURCE-REFUSED ") is { } refused)
            throw new IOException($"The files moved out of the source install were not kept: {refused}.");
        if (InteractiveClient.Line(reply.Stdout, "VT-SOURCE-SETTLED") == null)
            throw new HostOperationException($"Unexpected reply while settling the moved files of {where.Runtime} on {host.Name}", reply);
        return (files.Select(file => new SourceAddedFile(file.Key, Bytes(file.Key), file.Value,
            where.RecoveredIn != null ? HostPath.Join(where.RecoveredIn, AddedFolder, file.Key)
            : evidence != null ? "added/" + file.Key : host.Name + ":" + HostPath.Join(keep, file.Key))).ToList(), evidence);
    }

    // A run's <output>/source-changes/<actor>, numbered when the actor settled another profile before (two plans of one client).
    private static string EvidenceFolder(SourceSettlement where)
    {
        string folder = Path.Combine(where.Output!, "source-changes", where.Actor);
        for (int n = 2; Directory.Exists(folder); n++) folder = Path.Combine(where.Output!, "source-changes", where.Actor + "-" + n.ToString(CultureInfo.InvariantCulture));
        return folder;
    }

    private static async Task WriteReportAsync(IGameHost host, SourceSettlement where, string? evidence, SourceVerdict verdict, TimeSpan timeout,
        CancellationToken cancellation)
    {
        if (where.RecoveredIn != null)
        {
            await WriteAsync(host, HostPath.Join(where.RecoveredIn, "source-changes.json"), Encoding.UTF8.GetBytes(verdict.ToJson()), timeout, cancellation).ConfigureAwait(false);
            return;
        }
        string folder = evidence ?? EvidenceFolder(where);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "source-changes.json"), verdict.ToJson());
    }

    private static async Task WriteAsync(IGameHost host, string path, byte[] bytes, TimeSpan timeout, CancellationToken cancellation)
    {
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? SourceSettleScripts.PowerShellWrite : SourceSettleScripts.BashWrite,
            new Dictionary<string, string> { ["path"] = path, ["content"] = Convert.ToBase64String(bytes) }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Writing {path} on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-SOURCE-WRITTEN") == null)
            throw new HostOperationException($"Unexpected reply while writing {path} on {host.Name}", result);
    }
}
