using System.Globalization;
using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The one owner of a disposable profile's source checks: the preparation snapshot, copied-file staging scope,
/// narrow before-boot scope, and the whole-source settlement after the run (#612). It decides what the game left in the install
/// (moved into the run's evidence, left in place, or a failure), where that evidence goes, and whether the profile may be retired
/// (<see cref="SourceVerdict"/>). A recovery settles from the journalled baseline (<see cref="RecoverAsync"/>).
/// </summary>
internal sealed class ProfileSourceCheck
{
    internal const string HashAfterRunVariable = "VALHEIM_TESTING_SOURCE_HASH_AFTER_RUN";
    internal static AsyncLocal<bool?> HashAfterRunOverride { get; } = new();
    /// <summary>
    /// The baseline and the moved files, in the profile's parent folder on its host (beside <c>runtime</c> and <c>staging</c>): the
    /// files go to <c>source-added-&lt;id&gt;</c>, one folder per prepared profile, so a later profile in the same folder (the next
    /// regression arm) never takes an earlier one's as its own.
    /// </summary>
    internal const string BaselineFile = "source-baseline.json", AddedFolder = "source-added";
    /// <summary>The most a persisted baseline may hold when a recovery reads it back.</summary>
    private const long BaselineLimit = 64L << 20;

    private readonly IReadOnlyDictionary<string, string> _beforeBoot;
    private readonly IReadOnlyDictionary<string, string> _linkedNativeFiles;
    private IReadOnlyDictionary<string, string> _stagedBeforeBoot = new Dictionary<string, string>();
    private HostListing? _preparedGame, _preparedLoader;
    // A baseline read back from its journal keeps the comparison its run persisted.
    private bool? _contentMode;

    private ProfileSourceCheck(HostListing source, IReadOnlyDictionary<string, string> beforeBoot,
        IReadOnlyDictionary<string, string> linkedRootFiles, bool linked, string id)
    { Source = source; _beforeBoot = beforeBoot; _linkedNativeFiles = linkedRootFiles; Linked = linked; Id = id; }

    /// <summary>Whether the game launches from a linked profile (it hard-links game files), so it cannot add a file to the install.</summary>
    internal bool Linked { get; }
    // This prepared profile's own name for its moved files' folder (AddedFolder-<id>).
    private string Id { get; }

    internal HostListing Source { get; }
    internal IReadOnlyDictionary<string, string> BeforeBoot => _beforeBoot;
    internal IReadOnlyDictionary<string, string> LinkedNativeFiles => _linkedNativeFiles;
    internal IReadOnlyDictionary<string, string> StagedBeforeBoot => _stagedBeforeBoot;

    // steam-runtime/ is on DYLD_LIBRARY_PATH, so the whole folder is compared before boot: an extra file there could load.
    internal const string SteamRuntimeFolder = "steam-runtime";
    internal bool StagesSteamRuntime => _stagedBeforeBoot.Keys.Any(key => key.StartsWith(SteamRuntimeFolder + "/", StringComparison.Ordinal));
    internal bool InStagedFolder(string key) => StagesSteamRuntime && key.StartsWith(SteamRuntimeFolder + "/", StringComparison.Ordinal);
    internal HostListing PreparedGame => _preparedGame ?? throw new InvalidOperationException("The source-check owner has no prepared game listing.");
    internal HostListing PreparedLoader => _preparedLoader ?? throw new InvalidOperationException("The source-check owner has no prepared loader listing.");
    internal bool HashAfterRun => _contentMode ?? HashAfterRunOverride.Value ?? Environment.GetEnvironmentVariable(HashAfterRunVariable) == "1";

    /// <summary>The preparation row: retain the one complete content listing and derive every later narrow scope from it.</summary>
    internal static ProfileSourceCheck Prepare(HostListing source, IEnumerable<string> checkedFiles, ProfileLayout layout)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Files.Keys.ToHashSet(source.Names).SetEquals(source.States.Keys))
            throw new InvalidDataException($"The preparation listing of {source.Root} on {source.HostName} has no size and modified time for every file.");
        // The link table's native code (LinkedProfileFiles.Checked: the executable and the libraries beside it, at the root on Windows
        // and Linux, in valheim_server/ on the Mac server) is write-through hard links, every entry re-checked before each boot. Other
        // game data is covered by the managed-code pins.
        string[] native = checkedFiles.ToArray();
        string[] paths = [.. InstallPins.ManagedFolders, .. native.Select(HostInstall.LiteralPathPattern)];
        bool ignoreCase = source.Shell == HostShellKind.PowerShell;
        Dictionary<string, string> Expected(IEnumerable<string> selected) => source.Files
            .Where(file => HostInstall.MatchesAny(file.Key, selected, ignoreCase))
            .ToDictionary(file => file.Key, file => file.Value, source.Names);
        return new ProfileSourceCheck(source, Expected(paths), Expected(native.Select(HostInstall.LiteralPathPattern)), layout == ProfileLayout.Linked,
            Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>The before-boot row: game/loader pin paths plus the native files linked into a linked profile.</summary>
    internal string[] ProfileBeforeBootPaths(bool includePlugins) =>
        InstallPins.PinPaths.Concat(_linkedNativeFiles.Keys.Select(HostInstall.LiteralPathPattern))
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

    /// <summary>The source side of the before-boot row: game-code pins plus the native files linked from the source.</summary>
    internal string[] SourceBeforeBootPaths() =>
        InstallPins.ManagedFolders.Concat(_linkedNativeFiles.Keys.Select(HostInstall.LiteralPathPattern))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private sealed record PersistedFile(string Path, long Size, string Modified, string Sha256);
    private sealed record PersistedBaseline(string Host, string Root, string Mode, bool Linked, string Id, IReadOnlyList<PersistedFile> Files,
        IReadOnlyList<string> Directories);
    private static readonly JsonSerializerOptions s_baselineJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Writes the preparation listing (every file's size, modified time and SHA-256, and every directory) as
    /// <c>source-baseline.json</c> in the parent folder of the profile <paramref name="runtime"/> on its host, then journals
    /// <see cref="JournalEntry.SourceBaseline"/> with its path, SHA-256, moved files' folder and <paramref name="actor"/> (the
    /// one name of the actor's verdict and evidence, in the run and in a recovery). Once, at preparation, before anything launches
    /// from the profile: an interrupted run's recovery settles the install from it.
    /// </summary>
    internal async Task PersistAsync(IGameHost host, string runtime, string actor, Func<JournalEntry, Task> journal, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        if (host.Name != Source.HostName) throw new ArgumentException("The source check belongs to another host.", nameof(host));
        string parent = HostPath.Parent(runtime), path = HostPath.Join(parent, BaselineFile);
        var baseline = new PersistedBaseline(Source.HostName, Source.Root, HashAfterRun ? SourceVerdict.ContentMode : SourceVerdict.MetadataMode, Linked, Id,
            Source.Files.OrderBy(file => file.Key, StringComparer.Ordinal)
                .Select(file => new PersistedFile(file.Key, Source.States[file.Key].Size, Source.States[file.Key].Modified, file.Value)).ToList(),
            Source.Directories.Order(StringComparer.Ordinal).ToList());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(baseline, s_baselineJson);
        await WriteAsync(host, path, bytes, timeout, cancellation).ConfigureAwait(false);
        await journal(JournalEntry.Of(JournalEntry.SourceBaseline, ("runtime", runtime), ("sourceRoot", Source.Root), ("baseline", path),
            ("baselineSha256", FileHash.Sha256(bytes)), ("sourceAdded", KeepFolder(parent)), ("sourceActor", actor))).ConfigureAwait(false);
    }

    // The moved files' folder of this prepared profile, beside its runtime.
    private string KeepFolder(string parent) => HostPath.Join(parent, AddedFolder + "-" + Id);
    /// <summary>The moved files' folder of this prepared profile beside <paramref name="runtime"/>.</summary>
    internal string MovedFilesFolder(string runtime) => KeepFolder(HostPath.Parent(runtime));

    /// <summary>
    /// A recovery's settlement of the profile <paramref name="runtime"/> of a copy whose journalled <paramref name="fields"/> name a
    /// persisted baseline (<see cref="JournalEntry.SourceBaseline"/>): what the game added goes to this profile's own
    /// <c>source-changes-&lt;id&gt;</c> folder in <paramref name="recoveredIn"/> (the copy's <c>recovered-&lt;actor&gt;</c>, beside its logs),
    /// with its <c>source-changes.json</c>; a moved-files folder <paramref name="keptApart"/> names (journalled kept on its own) is left
    /// to its owner, <c>env teardown</c>. Null when none was journalled, or when the run settled the install itself (its
    /// <see cref="JournalEntry.SourceSettled"/>) and kept everything it moved. A baseline whose SHA-256 or root differs from the
    /// journal's is refused before anything is moved or removed. A baseline that is gone although no settlement was journalled is
    /// its own verdict, never a reason to keep the copy: what was moved is kept, and the install is named as not compared.
    /// </summary>
    internal static async Task<SourceVerdict?> RecoverAsync(IGameHost host, string runtime, string actor, IReadOnlyDictionary<string, string> fields,
        string recoveredIn, Func<string, bool> keptApart, Func<JournalEntry, Task> journal, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (!fields.TryGetValue("baseline", out string? path)) return null;
        actor = fields.GetValueOrDefault("sourceActor") ?? actor;
        string root = fields.GetValueOrDefault("sourceRoot") ?? "", sha256 = fields.GetValueOrDefault("baselineSha256") ?? "";
        string id = Leaf(fields.GetValueOrDefault("sourceAdded") ?? AddedFolder)[AddedFolder.Length..].TrimStart('-');
        var where = SourceSettlement.Recovery(actor, runtime, HostPath.Join(recoveredIn, "source-changes-" + (id.Length == 0 ? "profile" : id))) with { KeptApart = keptApart };
        byte[]? bytes = await HostClientPreflight.Read(host, path, timeout, cancellation, BaselineLimit).ConfigureAwait(false);
        if (bytes == null)
        {
            // The baseline goes only after its settlement was journalled, so without one it was removed by hand or by a run before
            // that order: keep whatever was moved, and name the install as not compared.
            var orphan = new ProfileSourceCheck(new HostListing(host.Name, host.Shell.Kind, root, new Dictionary<string, string>(), []),
                new Dictionary<string, string>(), new Dictionary<string, string>(), linked: false, id);
            var (kept, _) = await orphan.KeepAsync(host, where, journal, timeout, cancellation).ConfigureAwait(false);
            await orphan.DropAsync(host, where, kept.Count == 0, timeout, cancellation).ConfigureAwait(false);
            if (kept.Count == 0 && fields.ContainsKey("sourceVerdict")) return null;
            var found = new SourceVerdict(actor, host.Name, root, "", SourceVerdictKind.Restored, kept, [], [], [], [], [],
                "the run settled the install; the files it had moved were not kept yet, and are now");
            return fields.ContainsKey("sourceVerdict") ? found : SourceVerdict.NotCompared(actor, $"its source baseline {path} is gone, so the install was not compared" +
                (kept.Count == 0 ? "" : "; the files it had moved are kept in " + where.RecoveredIn)) with { Host = host.Name, Root = root, Added = kept };
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
        var check = new ProfileSourceCheck(listing, new Dictionary<string, string>(), new Dictionary<string, string>(), baseline.Linked, baseline.Id)
        { _contentMode = baseline.Mode == SourceVerdict.ContentMode };
        return await check.SettleAsync(host, where, journal, timeout, cancellation).ConfigureAwait(false);
    }

    private static string Leaf(string path) => path.Replace('\\', '/').Split('/').Last();

    /// <summary>
    /// The post-run row, once every process launched from the profile is proven stopped. It lists the whole source (metadata; a gate
    /// run, <see cref="HashAfterRunVariable"/>=1, re-hashes) against the baseline and decides, one rule for every launch path:
    /// <list type="bullet">
    /// <item>A changed or missing file fails, named and left as it is.</item>
    /// <item>On a linked profile the game cannot add a file to the install, so an added path came from something else (Steam, a mod
    /// manager, a person): it is named with a warning and left in place, and fails nothing.</item>
    /// <item>On an install the game runs in place, each added file is moved into this profile's moved-files folder and each added
    /// folder removed once empty, deepest first (<see cref="SourceSettleScripts"/>: links, paths outside the install and paths that
    /// name an install file are refused), and the install is listed again, which must equal the baseline. Nothing is moved when a
    /// file also changed or went missing: the verdict fails, naming every path.</item>
    /// </list>
    /// The moved files are kept where <paramref name="where"/> says, <c>source-changes.json</c> is written beside them,
    /// <see cref="JournalEntry.SourceSettled"/> is journalled, and only then the baseline goes. A settlement that did not finish
    /// (a host that failed, an abandoned cleanup, a journal that refused its line) keeps the profile, its baseline and its moved files,
    /// journalled kept, so <c>env recover</c> settles it again (<see cref="SourceVerdict.KeepsProfile"/>). Repeating it finds the moved
    /// files gone and carries on. It never throws for what it finds on the host.
    /// </summary>
    internal async Task<SourceVerdict> SettleAsync(IGameHost host, SourceSettlement where, Func<JournalEntry, Task> journal, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        if (host.Name != Source.HostName) throw new ArgumentException("The source check belongs to another host.", nameof(host));
        bool content = HashAfterRun;
        string mode = content ? SourceVerdict.ContentMode : SourceVerdict.MetadataMode;
        var notMoved = new Dictionary<string, string>(Source.Names);
        var removed = new List<string>();
        var found = Differences.None;
        var kept = new List<SourceAddedFile>();
        SourceVerdict Verdict(SourceVerdictKind kind, string? reason = null, bool settled = true) =>
            new(where.Actor, host.Name, Source.Root, mode, kind, kept, found.Changed, found.Missing, found.MissingDirectories,
                found.Added.Select(path => new SourceNotMoved(path, notMoved.GetValueOrDefault(path) ?? "still in the install"))
                    .Concat(found.AddedDirectories.Select(path => new SourceNotMoved(path + "/", notMoved.GetValueOrDefault(path) ?? "still in the install"))).ToList(),
                removed, reason) { Settled = settled, Profile = where.Runtime };
        SourceVerdict verdict;
        string? evidence = null;
        try
        {
            found = Compare(await ListAsync(host, Source.Root, content, timeout, cancellation).ConfigureAwait(false), content);
            bool damaged = found.Changed.Count + found.Missing.Count + found.MissingDirectories.Count != 0;
            if (Linked)
                foreach (string path in found.Added.Concat(found.AddedDirectories))
                    notMoved[path] = "added during the run by something other than the game's profile; left in place";
            else if (damaged)
                foreach (string path in found.Added.Concat(found.AddedDirectories))
                    notMoved[path] = "not moved: the install also changed, so nothing was moved";
            else if (found.Added.Count + found.AddedDirectories.Count != 0)
            {
                // Journalled before the move: a recovery after an interruption here finds what moved in the moved-files folder.
                await journal(JournalEntry.Of(JournalEntry.SourceMoveIntended, ("runtime", where.Runtime),
                    ("moving", string.Join('\n', found.Added.Concat(found.AddedDirectories.Select(path => path + "/")))))).ConfigureAwait(false);
                await MoveAsync(host, KeepFolder(where.Parent), found, notMoved, removed, timeout, cancellation).ConfigureAwait(false);
                found = Compare(await ListAsync(host, Source.Root, content, timeout, cancellation).ConfigureAwait(false), content);
            }
            (kept, evidence) = await KeepAsync(host, where, journal, timeout, cancellation).ConfigureAwait(false);
            verdict = Verdict(
                found.Changed.Count + found.Missing.Count + found.MissingDirectories.Count != 0 || !Linked && found.Added.Count + found.AddedDirectories.Count != 0
                    ? SourceVerdictKind.Failed
                : Linked && found.Added.Count + found.AddedDirectories.Count != 0 ? SourceVerdictKind.AddedElsewhere
                : kept.Count + removed.Count != 0 ? SourceVerdictKind.Restored : SourceVerdictKind.Unchanged);
            if (verdict.Verdict != SourceVerdictKind.Unchanged) await WriteReportAsync(host, where, evidence, verdict, timeout, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            // Journalled before the baseline goes: without this line a recovery finds the baseline and settles again.
            await journal(JournalEntry.Of(JournalEntry.SourceSettled, ("runtime", where.Runtime), ("sourceVerdict", verdict.Verdict.ToString()),
                ("added", verdict.Added.Count.ToString(CultureInfo.InvariantCulture)), ("changed", verdict.Changed.Count.ToString(CultureInfo.InvariantCulture)),
                ("missing", verdict.Missing.Count.ToString(CultureInfo.InvariantCulture)), ("notMoved", verdict.NotMoved.Count.ToString(CultureInfo.InvariantCulture)))).ConfigureAwait(false);
            await DropAsync(host, where, evidence != null || kept.Count == 0, timeout, cancellation).ConfigureAwait(false);
        }
        catch (SourceRefusedException refused)
        {
            // A refusal that cannot change is a finished failure, never a retry: the profile retires as usual, and what was moved stays
            // on the host, named and journalled kept for env teardown.
            verdict = Verdict(SourceVerdictKind.Failed, refused.Message + ". Resolve it by hand; env teardown removes the moved files it kept");
            try
            {
                await journal(JournalEntry.Of(JournalEntry.CopyKept, ("runtime", KeepFolder(where.Parent)), ("stage", ""), ("parent", where.Parent),
                    ("holds", AddedFolder), ("why", "its source settlement was refused: " + refused.Message))).ConfigureAwait(false);
                await journal(JournalEntry.Of(JournalEntry.SourceSettled, ("runtime", where.Runtime), ("sourceVerdict", verdict.Verdict.ToString()),
                    ("refused", refused.Message))).ConfigureAwait(false);
            }
            catch (Exception lost) { Console.Error.WriteLine($"Warning: could not journal the refused settlement of {where.Actor} on {host.Name}: {lost.Message}"); }
        }
        catch (Exception error)
        {
            // Anything else may pass (a host that failed or timed out, an abandoned cleanup, an I/O error on a move, a journal that
            // refused its line). The profile, its baseline and its moved files stay, journalled kept, so a recovery settles them again;
            // the evidence fetched so far is in the run's folder too.
            verdict = Verdict(SourceVerdictKind.Failed, "the settlement did not finish: " + error.Message, settled: false);
            if (where.RecoveredIn == null)
                try
                {
                    await journal(JournalEntry.Of(JournalEntry.CopyKept, ("runtime", where.Runtime),
                        ("why", "its source settlement did not finish; env teardown settles the install again and removes it"))).ConfigureAwait(false);
                }
                catch (Exception lost) { Console.Error.WriteLine($"Warning: could not journal {JournalEntry.CopyKept} for {where.Actor} on {host.Name}: {lost.Message}"); }
        }
        return verdict;
    }

    // What differs from the baseline: changed and missing files, missing folders, added files and added folders (deepest first).
    private sealed record Differences(List<SourceChangedFile> Changed, List<string> Missing, List<string> MissingDirectories, List<string> Added,
        List<string> AddedDirectories)
    {
        internal static Differences None => new([], [], [], [], []);
    }

    private Differences Compare(HostListing after, bool content)
    {
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
        var listed = content ? after.Files.Keys.ToHashSet(Source.Names) : after.States.Keys.ToHashSet(Source.Names);
        var folders = after.Directories.ToHashSet(Source.Names);
        var baselineFolders = Source.Directories.ToHashSet(Source.Names);
        return new(changed.OrderBy(file => file.Path, StringComparer.Ordinal).ToList(),
            Source.Files.Keys.Where(path => !listed.Contains(path)).Order(StringComparer.Ordinal).ToList(),
            baselineFolders.Where(path => !folders.Contains(path)).Order(StringComparer.Ordinal).ToList(),
            listed.Where(path => !Source.Files.ContainsKey(path)).Order(StringComparer.Ordinal).ToList(),
            // Deepest first, so a folder is empty by the time it is reached.
            folders.Where(path => !baselineFolders.Contains(path))
                .OrderByDescending(path => path.Count(c => c == '/')).ThenByDescending(path => path, StringComparer.Ordinal).ToList());
    }

    // The host's one settlement script: each added file into keep, then each added folder removed once empty. A path a Windows host
    // would read as a file of the install (it trims a trailing dot or space) is never sent: no baseline file is ever moved.
    private async Task MoveAsync(IGameHost host, string keep, Differences found, Dictionary<string, string> notMoved, List<string> removed,
        TimeSpan timeout, CancellationToken cancellation)
    {
        static string Encode(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        bool windows = host.Shell.Kind == HostShellKind.PowerShell;
        string Resolved(string path) => windows ? string.Join('/', path.Split('/').Select(part => part.TrimEnd('.', ' '))) : path;
        bool NamesInstall(string path) => !Resolved(path).Equals(path, StringComparison.Ordinal) &&
            (Source.Files.ContainsKey(Resolved(path)) || Source.Directories.Contains(Resolved(path), Source.Names));
        foreach (string path in found.Added.Concat(found.AddedDirectories).Where(NamesInstall))
            notMoved[path] = "it would name a file or folder of the install on this host";
        var result = (await host.RunAsync(windows ? SourceSettleScripts.PowerShell : SourceSettleScripts.Bash,
            new Dictionary<string, string>
            {
                ["root"] = Source.Root, ["keep"] = keep,
                ["files"] = string.Join('\n', found.Added.Where(path => !NamesInstall(path)).Select(Encode)),
                ["folders"] = string.Join('\n', found.AddedDirectories.Where(path => !NamesInstall(path)).Select(Encode)),
            }, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Moving what the run added out of {Source.Root} on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-SETTLE-REFUSED ") is { } refused)
            throw new SourceRefusedException($"the host refused to settle {Source.Root} on {host.Name}: {refused}");
        if (InteractiveClient.Line(result.Stdout, "VT-SETTLED") == null)
            throw new HostOperationException($"Unexpected reply while settling {Source.Root} on {host.Name}", result);
        foreach (string line in result.Stdout.Split('\n'))
        {
            string[] parts = line.Split(' ');
            if (parts is ["VT-NOTMOVED", var path, var reason])
                notMoved[Encoding.UTF8.GetString(Convert.FromBase64String(path))] = SourceSettleScripts.Reason(reason);
            else if (parts is ["VT-RMDIR", var folder]) removed.Add(Encoding.UTF8.GetString(Convert.FromBase64String(folder)) + "/");
        }
    }

    // What the moved-files folder holds, and where it is kept: in a run, fetched to <output>/source-changes/<actor>/added (a run
    // with no evidence folder, or files past the size limits, leave it on the host, journalled kept for env teardown); in a recovery,
    // moved into the run's recovered-<actor> folder on the host by DropAsync. Returns the files and the local evidence folder, if any.
    private async Task<(List<SourceAddedFile> Kept, string? Evidence)> KeepAsync(IGameHost host, SourceSettlement where,
        Func<JournalEntry, Task> journal, TimeSpan timeout, CancellationToken cancellation)
    {
        string keep = KeepFolder(where.Parent);
        // A folder the run kept on purpose (past the size limits, or no evidence folder) has its own kept entry: env teardown's, not ours.
        if (where.KeptApart?.Invoke(keep) == true) return ([], null);
        HostListing? moved;
        try { moved = await ListAsync(host, keep, content: true, timeout, cancellation).ConfigureAwait(false); }
        catch (DirectoryNotFoundException) { moved = null; }
        var files = moved?.Files.OrderBy(file => file.Key, StringComparer.Ordinal).ToList() ?? [];
        long Bytes(string path) => moved!.States[path].Size;
        string? evidence = null;
        var (perFile, total) = PinnedServerRun.RetainLimits(passed: false);
        string? why = where.RecoveredIn != null || files.Count == 0 ? null
            : where.Output == null ? "the run kept no evidence folder for what the game added to the source install"
            : files.Any(file => Bytes(file.Key) > perFile) || files.Sum(file => Bytes(file.Key)) > total
                ? $"what the game added to the source install is past the size limits ({DiskSpace.Format(perFile)} per file, {DiskSpace.Format(total)} in all)"
            : null;
        if (why != null)
            await journal(JournalEntry.Of(JournalEntry.CopyKept, ("runtime", keep), ("stage", ""), ("parent", where.Parent), ("holds", AddedFolder),
                ("why", why))).ConfigureAwait(false);
        else if (where.RecoveredIn == null && files.Count != 0)
        {
            evidence = EvidenceFolder(where);
            await host.FetchDirectoryAsync(keep, Path.Combine(evidence, "added"), timeout, cancellation).ConfigureAwait(false);
        }
        return (files.Select(file => new SourceAddedFile(file.Key, Bytes(file.Key), file.Value,
            where.RecoveredIn != null ? HostPath.Join(where.RecoveredIn, "added", file.Key)
            : evidence != null ? "added/" + file.Key : host.Name + ":" + HostPath.Join(keep, file.Key))).ToList(), evidence);
    }

    // The host's one evidence step, once the settlement is journalled: the moved-files folder is removed once fetched (or when it holds
    // no file), or in a recovery moved into recovered-<actor>; the baseline goes last.
    private async Task DropAsync(IGameHost host, SourceSettlement where, bool dropMoved, TimeSpan timeout, CancellationToken cancellation)
    {
        bool apart = where.KeptApart?.Invoke(KeepFolder(where.Parent)) == true;
        var reply = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? SourceSettleScripts.PowerShellEvidence : SourceSettleScripts.BashEvidence,
            new Dictionary<string, string>
            {
                ["parent"] = where.Parent, ["keep"] = KeepFolder(where.Parent), ["added"] = dropMoved && where.RecoveredIn == null && !apart ? "1" : "",
                ["recovered"] = apart ? "" : where.RecoveredIn ?? "",
            }, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Settling the moved files of {where.Runtime} on {host.Name}");
        if (InteractiveClient.Line(reply.Stdout, "VT-SOURCE-REFUSED ") is { } refused)
            throw new SourceRefusedException($"the host refused to keep the files moved out of {Source.Root} on {host.Name}: {refused}");
        if (InteractiveClient.Line(reply.Stdout, "VT-SOURCE-SETTLED") == null)
            throw new HostOperationException($"Unexpected reply while settling the moved files of {where.Runtime} on {host.Name}", reply);
    }

    // The one evidence location: <run output>/source-changes/<actor>, numbered when the actor settled another profile before (two
    // plans of one client).
    private static string EvidenceFolder(SourceSettlement where)
    {
        string folder = Path.Combine(where.Output!, "source-changes", where.Actor);
        for (int n = 2; Directory.Exists(folder); n++) folder = Path.Combine(where.Output!, "source-changes", where.Actor + "-" + n.ToString(CultureInfo.InvariantCulture));
        return folder;
    }

    // source-changes.json beside the kept files: in the run's evidence, or on the host in a recovery. A run without an evidence folder
    // has only its verdict.
    private static async Task WriteReportAsync(IGameHost host, SourceSettlement where, string? evidence, SourceVerdict verdict, TimeSpan timeout,
        CancellationToken cancellation)
    {
        if (where.RecoveredIn != null)
        {
            await WriteAsync(host, HostPath.Join(where.RecoveredIn, "source-changes.json"), Encoding.UTF8.GetBytes(verdict.ToJson()), timeout, cancellation).ConfigureAwait(false);
            return;
        }
        if (where.Output == null) return;
        string local = evidence ?? EvidenceFolder(where);
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, "source-changes.json"), verdict.ToJson());
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
