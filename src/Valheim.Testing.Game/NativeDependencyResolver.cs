using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valheim.Testing.Game;

/// <summary>Inputs for discovering the files a native mod smoke needs. Search inputs are explicit local directories or DLLs; nothing is downloaded.</summary>
public sealed class NativeDependencyRequest
{
    public List<string> Mods { get; set; } = [];
    /// <summary>Local directories to inspect, or exact DLL paths that take precedence over other discovered builds of the same dependency.</summary>
    public List<string> SearchRoots { get; set; } = [];
    public string GameManaged { get; set; } = "";
    public string BepInExCore { get; set; } = "";
    public string CliManifest { get; set; } = "";
    public string CliFiles { get; set; } = "";
    public List<string> Capabilities { get; set; } = [];
    /// <summary>Require every <see cref="CliCapabilities.Toolkit"/> command and stage every file in the pinned ValheimCLI
    /// manifest, including packs this scenario does not use. One-shot runners set this; general requests may select by capability.</summary>
    public bool StageAllCliPacks { get; set; }
    /// <summary>Assembly names the developer explicitly confirms are only used behind an absent soft integration.</summary>
    public List<string> OptionalReferences { get; set; } = [];

    /// <summary>Reads the private request, resolving relative paths against the request file rather than the shell's directory.</summary>
    public static NativeDependencyRequest Read(string path)
    {
        path = Path.GetFullPath(path);
        var request = JsonSerializer.Deserialize<NativeDependencyRequest>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException($"{path} is empty.");
        string directory = Path.GetDirectoryName(path)!;
        string Full(string value) => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{path} has an empty dependency path; supply the file or directory explicitly.")
            : Path.GetFullPath(Path.Combine(directory, value));
        request.Mods = request.Mods.Select(Full).ToList();
        request.SearchRoots = request.SearchRoots.Select(Full).ToList();
        request.GameManaged = Full(request.GameManaged);
        request.BepInExCore = Full(request.BepInExCore);
        request.CliManifest = Full(request.CliManifest);
        request.CliFiles = Full(request.CliFiles);
        return request;
    }
}

/// <summary>One selected DLL and the reason it belongs in the disposable game.</summary>
public sealed record NativeDependencyFile(string File, string Sha256, string Reason)
{
    /// <summary>All discovered paths with the same assembly/plugin identity and bytes, including <see cref="File"/>. Only File is staged.</summary>
    public List<string> SourcePaths { get; init; } = [];
}

/// <summary>A choice the resolver cannot make safely. Candidate paths are suggestions, never staged automatically.</summary>
public sealed record NativeDependencyGap(string Kind, string Name, string Reason, IReadOnlyList<string> Candidates);

/// <summary>
/// An editable, pinned result of <see cref="NativeDependencyResolver.Resolve"/>. Save a plan with gaps, supply or confirm
/// the missing files in the request, and resolve again; once ready, <see cref="ReadReady"/> checks the exact files without
/// rediscovering them. Keep this lock private: it contains machine paths.
/// </summary>
public sealed class NativeDependencyLock
{
    public List<NativeDependencyFile> Mods { get; set; } = [];
    public List<NativeDependencyFile> Plugins { get; set; } = [];
    public List<NativeDependencyFile> CliFiles { get; set; } = [];
    public CliCapabilityManifest CliManifest { get; set; } = new();
    public List<string> OptionalReferences { get; set; } = [];
    public List<string> OptionalCandidates { get; set; } = [];
    public List<NativeDependencyGap> Gaps { get; set; } = [];
    [JsonIgnore] public bool Ready => Gaps.Count == 0;

    /// <summary>
    /// Requires an A/B comparison to change only the first selected mod. Companions, their resolved dependencies,
    /// optional-reference decisions and ValheimCLI must remain byte-identical; otherwise a difference in the native
    /// result could be caused by the environment rather than that mod build.
    /// </summary>
    public void RequireSameFixedInputs(NativeDependencyLock other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!Ready || !other.Ready || Mods.Count == 0 || other.Mods.Count == 0)
            throw new InvalidDataException("Both A/B dependency locks must be ready and name a selected mod.");
        static string[] Identity(IEnumerable<NativeDependencyFile> files) => files
            .Select(file => Path.GetFileName(file.File).ToLowerInvariant() + ":" + file.Sha256.ToLowerInvariant())
            .Order(StringComparer.Ordinal).ToArray();
        static void Same(string label, string[] left, string[] right)
        {
            if (!left.SequenceEqual(right, StringComparer.Ordinal))
                throw new InvalidDataException($"A/B {label} differ; keep every input except the first selected mod identical. Before: {string.Join(", ", left)}; after: {string.Join(", ", right)}.");
        }
        Same("companion mods", Identity(Mods.Skip(1)), Identity(other.Mods.Skip(1)));
        Same("plugin dependencies", Identity(Plugins), Identity(other.Plugins));
        Same("ValheimCLI files", Identity(CliFiles), Identity(other.CliFiles));
        Same("optional references", OptionalReferences.Order(StringComparer.Ordinal).ToArray(),
            other.OptionalReferences.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Checks an add/remove-mod comparison before either arm starts. The second arm must contain the same selected
    /// files in the same order except for <paramref name="removedMod"/>. Its resolved dependency closure may shrink,
    /// but it may not acquire a new file; ValheimCLI and explicit optional-reference decisions stay fixed.
    /// </summary>
    public void RequireSameExceptRemovedMod(NativeDependencyLock other, string removedMod)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!Ready || !other.Ready || Mods.Count < 2)
            throw new InvalidDataException("Both A/B dependency locks must be ready and the first arm needs at least two selected mods.");
        string removed = Path.GetFullPath(removedMod);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var matches = Mods.Where(file => Path.GetFullPath(file.File).Equals(removed, pathComparison)).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("The removed mod must identify exactly one selected DLL in the first arm.");
        static string Identity(NativeDependencyFile file) =>
            Path.GetFullPath(file.File) + ":" + file.Sha256.ToLowerInvariant();
        var expected = Mods.Where(file => !Path.GetFullPath(file.File).Equals(removed, pathComparison))
            .Select(Identity).ToArray();
        if (!expected.SequenceEqual(other.Mods.Select(Identity), StringComparer.Ordinal))
            throw new InvalidDataException("The remaining selected mods changed between A/B arms.");
        var originalDependencies = Plugins.Select(Identity).ToHashSet(StringComparer.Ordinal);
        if (other.Plugins.Any(file => !originalDependencies.Contains(Identity(file))))
            throw new InvalidDataException("Removing a mod introduced a new plugin dependency; review both arms instead of treating this as an isolated removal.");
        if (!CliFiles.Select(Identity).Order(StringComparer.Ordinal).SequenceEqual(
                other.CliFiles.Select(Identity).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("ValheimCLI files changed between A/B arms.");
        if (!OptionalReferences.Order(StringComparer.Ordinal).SequenceEqual(
                other.OptionalReferences.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Optional-reference decisions changed between A/B arms.");
    }

    /// <summary>Fills a targeted run's dependency and ValheimCLI fields from this lock, leaving its fixture and mod arms to the caller.</summary>
    public void ApplyTo(RegressionInputs inputs, string cliManifestPath)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (!Ready) throw new InvalidOperationException($"Resolve the {Gaps.Count} dependency choice(s) before applying this lock.");
        RequireExactCliSet();
        var core = CliFiles.Where(file => CliManifest.Files.Any(entry => entry.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)
            && entry.Plugins.Contains("valheimCLI.valheimCLI", StringComparer.Ordinal))).ToList();
        if (core.Count != 1) throw new InvalidDataException("The dependency lock needs exactly one ValheimCLI core.");
        CliManifest.Write(cliManifestPath);
        inputs.Cli = new RegressionCli
        {
            Core = new RegressionFile { File = core[0].File, Sha256 = core[0].Sha256 },
            Packs = CliFiles.Where(file => file != core[0]).Select(file => new RegressionFile { File = file.File, Sha256 = file.Sha256 }).ToList(),
            Manifest = Path.GetFullPath(cliManifestPath),
        };
        inputs.Plugins = Plugins.Select(file => new RegressionFile { File = file.File, Sha256 = file.Sha256 }).ToList();
        inputs.OptionalReferences = [.. OptionalReferences];
    }

    public void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + "\n");

    /// <summary>Loads a previously resolved set, refusing changed files, missing dependencies and a mixed CLI build.</summary>
    public static NativeDependencyLock ReadReady(string path)
    {
        var plan = JsonSerializer.Deserialize<NativeDependencyLock>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"The native dependency lock {path} is empty.");
        if (!plan.Ready) throw new InvalidDataException($"The native dependency lock {path} still has {plan.Gaps.Count} unresolved choice(s): {string.Join("; ", plan.Gaps.Select(gap => gap.Name))}.");
        plan.CliManifest.Validate();
        foreach (var file in plan.Mods.Concat(plan.Plugins).Concat(plan.CliFiles))
            if (!File.Exists(file.File) || !FileHash.Sha256(file.File).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The pinned file {file.File} ({file.Reason}) is missing or changed; resolve from explicit roots again.");
        plan.RequireExactCliSet();
        return plan;
    }

    /// <summary>The lock's ValheimCLI files are exactly its manifest's set (<see cref="CliCapabilityManifest.Locate"/>), with one core.</summary>
    internal void RequireExactCliSet()
    {
        CliManifest.Validate();
        if (CliFiles.Count == 0 || CliFiles.Count != CliManifest.Files.Count)
            throw new InvalidDataException("The dependency lock must contain exactly the ValheimCLI core and packs selected by its capability manifest.");
        if (CliManifest.Files.Count(file => file.Plugins.Contains("valheimCLI.valheimCLI", StringComparer.Ordinal)) != 1)
            throw new InvalidDataException("The dependency lock needs exactly one ValheimCLI core.");
        foreach (var file in CliFiles)
            if (!File.Exists(file.File) || !FileHash.Sha256(file.File).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The pinned ValheimCLI file {file.File} is missing or changed; resolve from explicit roots again.");
        var location = CliManifest.Locate(CliFiles.Select(file => file.File), "in the dependency lock");
        if (location.Problems.Count != 0)
            throw new InvalidDataException($"The dependency lock's ValheimCLI files are not the build {CliManifest.Build} its manifest describes: {string.Join("; ", location.Problems.Select(problem => problem.Message))}.");
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true,
    };
}

/// <summary>
/// Discovers a mod's hard BepInEx dependencies and directly referenced managed libraries from explicit local roots, plus
/// one coherent ValheimCLI core and the packs providing requested commands. It never guesses among candidates, omits a
/// direct reference merely because a soft dependency exists, or downloads code. No game is started or install changed.
/// </summary>
public static class NativeDependencyResolver
{
    public static NativeDependencyLock Resolve(NativeDependencyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mods.Count == 0) throw new ArgumentException("mods: give at least one plugin DLL.");
        foreach (string source in request.SearchRoots)
            if (!Path.IsPathFullyQualified(source) || !(Directory.Exists(source) || File.Exists(source) &&
                Path.GetExtension(source).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
                throw new FileNotFoundException($"Dependency search input {source} must be an existing local directory or DLL.", source);
        foreach (string folder in new[] { request.GameManaged, request.BepInExCore, request.CliFiles })
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) throw new DirectoryNotFoundException($"Dependency search directory {folder} does not exist; supply an explicit local root.");
        foreach (string file in request.Mods.Append(request.CliManifest))
            if (!Path.IsPathFullyQualified(file) || !File.Exists(file)) throw new FileNotFoundException($"Dependency input {file} does not exist; supply an absolute local file.", file);

        var result = new NativeDependencyLock { OptionalReferences = request.OptionalReferences.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList() };
        var bundle = CliCapabilityManifest.Read(request.CliManifest);
        // The one-shot contract needs every toolkit command in the bundle, then stages every declared file.
        // General resolver callers can still request a smaller set explicitly.
        var requiredCli = request.StageAllCliPacks
            ? request.Capabilities.Concat(CliCapabilities.Toolkit)
            : request.Capabilities;
        var needed = bundle.ForCapabilities(requiredCli);
        var cli = request.StageAllCliPacks ? bundle : needed;
        result.CliManifest = cli;
        var exactSources = request.SearchRoots.Where(File.Exists).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var inventory = request.SearchRoots.Concat(request.Mods.Select(path => Path.GetDirectoryName(path)!)).Distinct(StringComparer.Ordinal)
            .SelectMany(root => File.Exists(root) ? new[] { root } : Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
            .Concat(request.Mods).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(path =>
            {
                try { return (Path: path, Metadata: PluginMetadata.Read(path)); }
                catch (Exception error) when (error is BadImageFormatException or InvalidDataException) { return (Path: path, Metadata: (PluginAssembly?)null); }
            }).Where(item => item.Metadata != null).Select(item => (item.Path, Metadata: item.Metadata!)).ToList();
        var known = inventory.ToDictionary(item => item.Path, item => item.Metadata, StringComparer.Ordinal);
        var equivalentSources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // A second path to the same bytes is provenance, not another build to choose from.
        // Keep the choice explicit whenever either the metadata identity or bytes differ.
        List<string> DistinctCandidates(IEnumerable<string> paths)
        {
            var groups = paths.GroupBy(candidate =>
            {
                var metadata = known[candidate];
                string plugins = string.Join(";", metadata.Plugins
                    .Select(plugin => plugin.Guid + "@" + plugin.Version).Order(StringComparer.Ordinal));
                return metadata.AssemblyName + "|" + plugins + "|" + FileHash.Sha256(candidate);
            }, StringComparer.OrdinalIgnoreCase);
            var choices = new List<string>();
            foreach (var group in groups)
            {
                var sources = group.Order(StringComparer.Ordinal).ToList();
                string expectedName = known[sources[0]].AssemblyName + ".dll";
                string chosen = sources.OrderBy(source => Path.GetFileName(source).Equals(expectedName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(source => source, StringComparer.Ordinal).First();
                choices.Add(chosen);
                equivalentSources[chosen] = sources;
            }
            // A file named explicitly in SearchRoots wins over directory-discovered builds.
            // If two different explicit builds match, keep the ambiguity visible.
            var named = choices.Where(choice => equivalentSources[choice].Any(exactSources.Contains)).ToArray();
            if (named.Length == 1)
            {
                string exact = equivalentSources[named[0]].First(exactSources.Contains);
                equivalentSources[exact] = equivalentSources[named[0]];
                return [exact];
            }
            return choices;
        }
        IEnumerable<string> AllSources(IEnumerable<string> choices) => choices.SelectMany(choice => equivalentSources[choice]);
        var selected = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        void Add(string path, string reason)
        {
            if (selected.TryAdd(path, reason)) queue.Enqueue(path);
        }
        foreach (string path in request.Mods)
        {
            var metadata = PluginMetadata.Read(path);
            if (metadata.Plugins.Count == 0) throw new InvalidDataException($"{path} declares no [BepInPlugin]; give a mod plugin, not a library.");
            known[path] = metadata;
            result.Mods.Add(Pinned(path, "selected mod"));
            Add(path, "selected mod");
        }
        // The ValheimCLI set: the one static check (CliCapabilityManifest.Locate) over the folder's DLLs of the manifest's file
        // names. Only the located files are copied, so another build left in the folder is not judged.
        var names = cli.Files.Select(file => file.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var location = cli.Locate(Directory.EnumerateFiles(request.CliFiles, "*.dll", SearchOption.AllDirectories).Where(path => names.Contains(Path.GetFileName(path))),
            "in " + request.CliFiles, othersLoad: false);
        foreach (var problem in location.Problems)
            result.Gaps.Add(new("cli", problem.File.File, $"The pinned ValheimCLI build {cli.Build}: {problem.Message}.", problem.Candidates.Order(StringComparer.Ordinal).ToList()));
        foreach (var (file, path) in location.Located)
        {
            var actual = PluginMetadata.Read(path);
            result.CliFiles.Add(Pinned(path, file.Plugins.Contains("valheimCLI.valheimCLI", StringComparer.Ordinal) ? "ValheimCLI core" :
                request.StageAllCliPacks ? "pinned ValheimCLI bundle pack" : "provides " + string.Join(", ", request.Capabilities.Where(capability => file.Commands().Any(command => command.Path == capability)))));
            known[path] = actual;
            Add(path, "ValheimCLI selected build");
        }

        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in new[] { request.GameManaged, request.BepInExCore })
            foreach (string path in Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly)) provided.Add(Path.GetFileNameWithoutExtension(path));
        var gaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Gap(string kind, string name, string reason, IEnumerable<string> candidates)
        {
            if (gaps.Add(kind + "/" + name)) result.Gaps.Add(new(kind, name, reason, candidates.Order(StringComparer.Ordinal).ToList()));
        }
        while (queue.TryDequeue(out string? path))
        {
            var assembly = known[path];
            foreach (var plugin in assembly.Plugins)
            {
                foreach (var dependency in plugin.Dependencies.Where(item => item.Hard))
                {
                    var already = selected.Keys.SelectMany(candidate => known[candidate].Plugins.Where(p => p.Guid == dependency.Guid).Select(p => (candidate, p))).ToList();
                    if (already.Count != 0)
                    {
                        if (!already.Any(item => DependencyRule.AtLeast(item.p.Version, dependency.MinimumVersion)))
                            Gap("plugin", dependency.Guid, $"{Path.GetFileName(path)} needs {dependency.Guid} >= {dependency.MinimumVersion}, but the selected {Path.GetFileName(already[0].candidate)} declares {already[0].p.Version}; select a compatible build instead of staging both.", []);
                        continue;
                    }
                    var exactMatches = DistinctCandidates(inventory.Where(item => exactSources.Contains(item.Path) &&
                        item.Metadata.Plugins.Any(p => p.Guid == dependency.Guid)).Select(item => item.Path));
                    if (exactMatches.Count == 1)
                    {
                        string exact = exactMatches[0];
                        string version = known[exact].Plugins.First(p => p.Guid == dependency.Guid).Version;
                        if (!DependencyRule.AtLeast(version, dependency.MinimumVersion))
                            Gap("plugin", dependency.Guid, $"{Path.GetFileName(path)} requires {dependency.Guid} >= {dependency.MinimumVersion}, but the explicitly selected {Path.GetFileName(exact)} declares {version}; choose a compatible DLL.", AllSources(exactMatches));
                        else Add(exact, $"hard [BepInDependency] {dependency.Guid} of {Path.GetFileName(path)}");
                        continue;
                    }
                    if (exactMatches.Count > 1)
                    {
                        Gap("plugin", dependency.Guid, $"More than one explicitly selected DLL provides {dependency.Guid}; choose one build.", AllSources(exactMatches));
                        continue;
                    }
                    var matches = DistinctCandidates(inventory.Where(item => item.Metadata.Plugins.Any(p => p.Guid == dependency.Guid && DependencyRule.AtLeast(p.Version, dependency.MinimumVersion)))
                        .Select(item => item.Path).Where(candidate => !selected.ContainsKey(candidate)));
                    if (matches.Count == 1) Add(matches[0], $"hard [BepInDependency] {dependency.Guid} of {Path.GetFileName(path)}");
                    else Gap("plugin", dependency.Guid, $"{Path.GetFileName(path)} requires {dependency.Guid}{(dependency.MinimumVersion == null ? "" : " >= " + dependency.MinimumVersion)}; supply its plugin DLL in an explicit search root or choose among the candidates.", AllSources(matches));
                }
                foreach (var dependency in plugin.Dependencies.Where(item => !item.Hard))
                    if (!result.OptionalCandidates.Contains(dependency.Guid, StringComparer.Ordinal)) result.OptionalCandidates.Add(dependency.Guid);
            }
            foreach (string reference in assembly.References)
            {
                if (provided.Contains(reference) || result.OptionalReferences.Contains(reference, StringComparer.OrdinalIgnoreCase)
                    || selected.Keys.Any(candidate => known[candidate].AssemblyName.Equals(reference, StringComparison.OrdinalIgnoreCase))) continue;
                var matches = DistinctCandidates(inventory.Where(item => item.Metadata.AssemblyName.Equals(reference, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Path).Where(candidate => !selected.ContainsKey(candidate)));
                bool soft = assembly.Plugins.SelectMany(plugin => plugin.Dependencies.Where(dep => !dep.Hard))
                    .Any(dep => matches.Any(candidate => known[candidate].Plugins.Any(p => p.Guid == dep.Guid)));
                if (soft) Gap("optional-reference", reference, $"{Path.GetFileName(path)} references {reference}, which may be behind a soft dependency. Confirm optionalReferences explicitly or select its DLL; metadata alone cannot prove it is safe to omit.", AllSources(matches));
                else if (matches.Count == 1) Add(matches[0], $"assembly reference {reference} of {Path.GetFileName(path)}");
                else Gap("assembly", reference, $"{Path.GetFileName(path)} references {reference}; supply its DLL in an explicit search root, or explicitly confirm optionalReferences if it is guarded.", AllSources(matches));
            }
        }
        // The one declared-dependency rule over the selected set (TargetedRegression applies it to what it stages). A name the
        // search above already reported keeps that gap. No process is named: one resolver serves client and server mods.
        foreach (var problem in DependencyRule.Check(selected.Keys.Select(path => (path, known[path])).ToList(), provided, result.OptionalReferences, process: null,
            show: path => Path.GetFileName(path)))
            if (!result.Gaps.Any(gap => gap.Name.Equals(problem.Name, StringComparison.OrdinalIgnoreCase)))
                Gap(problem.Kind, problem.Name, problem.Message, problem.Paths);
        foreach (var (path, reason) in selected)
            if (!request.Mods.Contains(path, StringComparer.Ordinal) && !result.CliFiles.Any(file => file.File == path))
                result.Plugins.Add(Pinned(path, reason) with { SourcePaths = equivalentSources.GetValueOrDefault(path) ?? [path] });
        result.Plugins.Sort((a, b) => StringComparer.Ordinal.Compare(a.File, b.File));
        result.OptionalCandidates.Sort(StringComparer.Ordinal);
        return result;
    }

    private static NativeDependencyFile Pinned(string path, string reason) => new(path, FileHash.Sha256(path), reason);
}
