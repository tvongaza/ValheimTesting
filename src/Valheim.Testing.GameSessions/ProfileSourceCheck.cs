using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The one owner of a disposable profile's source checks: the preparation snapshot, copied-file staging scope,
/// narrow before-boot scope, and whole-source post-run verdict.
/// </summary>
internal sealed class ProfileSourceCheck
{
    internal const string HashAfterRunVariable = "VALHEIM_TESTING_SOURCE_HASH_AFTER_RUN";
    internal static AsyncLocal<bool?> HashAfterRunOverride { get; } = new();

    private readonly IReadOnlyDictionary<string, string> _beforeBoot;
    private readonly IReadOnlyDictionary<string, string> _linkedRootFiles;
    private HostListing? _preparedGame, _preparedLoader;

    private ProfileSourceCheck(HostListing source, IReadOnlyDictionary<string, string> beforeBoot,
        IReadOnlyDictionary<string, string> linkedRootFiles)
    { Source = source; _beforeBoot = beforeBoot; _linkedRootFiles = linkedRootFiles; }

    internal HostListing Source { get; }
    internal IReadOnlyDictionary<string, string> BeforeBoot => _beforeBoot;
    internal IReadOnlyDictionary<string, string> LinkedRootFiles => _linkedRootFiles;
    internal HostListing PreparedGame => _preparedGame ?? throw new InvalidOperationException("The source-check owner has no prepared game listing.");
    internal HostListing PreparedLoader => _preparedLoader ?? throw new InvalidOperationException("The source-check owner has no prepared loader listing.");
    internal bool HashAfterRun => HashAfterRunOverride.Value ?? Environment.GetEnvironmentVariable(HashAfterRunVariable) == "1";
    internal string VerdictName(string actor) => $"verify {actor}'s source install after the run ({(HashAfterRun ? "content hashes" : "size and modified time")})";

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
        CancellationToken cancellation = default) => HostInstall.ListProfileSourceAsync(host, root, timeout, cancellation);

    /// <summary>Retains the preparation listings which a prepared launch reuses instead of listing either source-backed root again.</summary>
    internal void RecordPreparedProfile(HostListing game, HostListing loader)
    {
        _preparedGame = game;
        _preparedLoader = loader;
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
        .Concat(includePlugins ? ["BepInEx/plugins", "BepInEx/scripts"] : [])
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The source side of the before-boot row: game-code pins plus root files linked or launched directly from the source.</summary>
    internal string[] SourceBeforeBootPaths() =>
        InstallPins.ManagedFolders.Concat(_linkedRootFiles.Keys.Select(HostInstall.LiteralPathPattern))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The post-run row: compare every source file, including logs; ordinary runs use metadata and gate runs re-hash.</summary>
    internal async Task VerifyAfterRunAsync(IGameHost host, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (host.Name != Source.HostName)
            throw new ArgumentException("The source check belongs to another host.", nameof(host));
        var after = HashAfterRun
            ? await HostInstall.ListAsync(host, Source.Root, timeout, cancellation: cancellation).ConfigureAwait(false)
            : await HostInstall.ListMetadataAsync(host, Source.Root, timeout, cancellation).ConfigureAwait(false);
        if (HashAfterRun) RequireIntegrity(Source.Files, after.Files, after.Names, "different content");
        else RequireIntegrity(Source.States, after.States, after.Names, "different size or modified time");
    }

    private static void RequireIntegrity<T>(IReadOnlyDictionary<string, T> expected, IReadOnlyDictionary<string, T> actual,
        StringComparer names, string changedLabel) where T : notnull
    {
        var want = expected.ToDictionary(file => file.Key, file => file.Value, names);
        var found = actual.ToDictionary(file => file.Key, file => file.Value, names);
        var changed = want.Where(file => found.TryGetValue(file.Key, out var value) && !EqualityComparer<T>.Default.Equals(file.Value, value))
            .Select(file => file.Key).Order(StringComparer.Ordinal).ToList();
        var missing = want.Keys.Where(path => !found.ContainsKey(path)).Order(StringComparer.Ordinal).ToList();
        var added = found.Keys.Where(path => !want.ContainsKey(path)).Order(StringComparer.Ordinal).ToList();
        if (changed.Count + missing.Count + added.Count == 0) return;
        static string Few(List<string> paths) => string.Join(", ", paths.Take(10)) + (paths.Count > 10 ? $" and {paths.Count - 10} more" : "");
        var differences = new List<string>();
        if (changed.Count != 0) differences.Add(changedLabel + ": " + Few(changed));
        if (missing.Count != 0) differences.Add("missing: " + Few(missing));
        if (added.Count != 0) differences.Add("added: " + Few(added));
        throw new IOException("Source integrity failed after the run (" + string.Join("; ", differences) + "). The source is not repaired.");
    }
}
