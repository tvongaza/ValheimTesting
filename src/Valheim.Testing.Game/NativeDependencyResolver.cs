using System.Text.Json;
using System.Text.Json.Serialization;

namespace Valheim.Testing.Game;

/// <summary>Inputs for discovering the files a native mod smoke needs. Search roots are explicit local directories; nothing is downloaded.</summary>
public sealed class NativeDependencyRequest
{
    public List<string> Mods { get; set; } = [];
    public List<string> SearchRoots { get; set; } = [];
    public string GameManaged { get; set; } = "";
    public string BepInExCore { get; set; } = "";
    public string CliManifest { get; set; } = "";
    public string CliFiles { get; set; } = "";
    public List<string> Capabilities { get; set; } = [];
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
public sealed record NativeDependencyFile(string File, string Sha256, string Reason);

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

    /// <summary>Fills a targeted run's dependency and ValheimCLI fields from this lock, leaving its fixture and mod arms to the caller.</summary>
    public void ApplyTo(RegressionEnvironment environment, string cliManifestPath)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!Ready) throw new InvalidOperationException($"Resolve the {Gaps.Count} dependency choice(s) before applying this lock.");
        RequireExactCliSet();
        CliManifest.Write(cliManifestPath);
        var core = CliFiles.Where(file => CliManifest.Files.Any(entry => entry.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)
            && entry.Plugins.Contains("valheimCLI.valheimCLI", StringComparer.Ordinal))).ToList();
        if (core.Count != 1) throw new InvalidDataException("The dependency lock needs exactly one ValheimCLI core.");
        environment.Cli = new RegressionCli
        {
            Core = new RegressionFile { File = core[0].File, Sha256 = core[0].Sha256 },
            Packs = CliFiles.Where(file => file != core[0]).Select(file => new RegressionFile { File = file.File, Sha256 = file.Sha256 }).ToList(),
            Manifest = Path.GetFullPath(cliManifestPath),
        };
        environment.Plugins = Plugins.Select(file => new RegressionFile { File = file.File, Sha256 = file.Sha256 }).ToList();
        environment.OptionalReferences = [.. OptionalReferences];
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
            if (!File.Exists(file.File) || !WorldFixture.Hash(file.File).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The pinned file {file.File} ({file.Reason}) is missing or changed; resolve from explicit roots again.");
        plan.RequireExactCliSet();
        return plan;
    }

    private void RequireExactCliSet()
    {
        CliManifest.Validate();
        if (CliFiles.Count == 0 || CliFiles.Count != CliManifest.Files.Count)
            throw new InvalidDataException("The dependency lock must contain exactly the ValheimCLI core and packs selected by its capability manifest.");
        foreach (var declared in CliManifest.Files)
            if (CliFiles.Count(file => Path.GetFileName(file.File).Equals(declared.File, StringComparison.OrdinalIgnoreCase)
                && file.Sha256.Equals(declared.Sha256, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidDataException($"The pinned ValheimCLI file {declared.File} is not exactly the build in the lock.");
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
        foreach (string folder in request.SearchRoots.Append(request.GameManaged).Append(request.BepInExCore).Append(request.CliFiles))
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) throw new DirectoryNotFoundException($"Dependency search directory {folder} does not exist; supply an explicit local root.");
        foreach (string file in request.Mods.Append(request.CliManifest))
            if (!Path.IsPathFullyQualified(file) || !File.Exists(file)) throw new FileNotFoundException($"Dependency input {file} does not exist; supply an absolute local file.", file);

        var result = new NativeDependencyLock { OptionalReferences = request.OptionalReferences.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList() };
        var cli = CliCapabilityManifest.Read(request.CliManifest).ForCapabilities(request.Capabilities);
        result.CliManifest = cli;
        var inventory = request.SearchRoots.Concat(request.Mods.Select(path => Path.GetDirectoryName(path)!)).Distinct(StringComparer.Ordinal)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
            .Concat(request.Mods).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(path =>
            {
                try { return (Path: path, Metadata: PluginMetadata.Read(path)); }
                catch (Exception error) when (error is BadImageFormatException or InvalidDataException) { return (Path: path, Metadata: (PluginAssembly?)null); }
            }).Where(item => item.Metadata != null).Select(item => (item.Path, Metadata: item.Metadata!)).ToList();
        var known = inventory.ToDictionary(item => item.Path, item => item.Metadata, StringComparer.Ordinal);
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
        foreach (var file in cli.Files)
        {
            var matches = Directory.EnumerateFiles(request.CliFiles, file.File, SearchOption.AllDirectories)
                .Where(path => WorldFixture.Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
            if (matches.Count != 1)
            {
                result.Gaps.Add(new("cli", file.File, $"The pinned ValheimCLI build {cli.Build} needs {file.File} at SHA256 {file.Sha256}; found {matches.Count} exact copy/copies in {request.CliFiles}.", matches));
                continue;
            }
            string path = matches[0];
            var actual = PluginMetadata.Read(path);
            if (!actual.Plugins.Select(plugin => plugin.Guid).Order(StringComparer.Ordinal)
                .SequenceEqual(file.Plugins.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                result.Gaps.Add(new("cli", file.File, $"{path} has the pinned hash but declares different BepInEx plugins from the ValheimCLI manifest; regenerate the manifest from this build.", [path]));
                continue;
            }
            result.CliFiles.Add(Pinned(path, file.Plugins.Contains("valheimCLI.valheimCLI", StringComparer.Ordinal) ? "ValheimCLI core" : "provides " + string.Join(", ", request.Capabilities.Where(capability => file.Commands().Any(command => command.Path == capability)))));
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
                        if (!already.Any(item => AtLeast(item.p.Version, dependency.MinimumVersion)))
                            Gap("plugin", dependency.Guid, $"{Path.GetFileName(path)} needs {dependency.Guid} >= {dependency.MinimumVersion}, but the selected {Path.GetFileName(already[0].candidate)} declares {already[0].p.Version}; select a compatible build instead of staging both.", []);
                        continue;
                    }
                    var matches = inventory.Where(item => item.Metadata.Plugins.Any(p => p.Guid == dependency.Guid && AtLeast(p.Version, dependency.MinimumVersion)))
                        .Select(item => item.Path).Where(candidate => !selected.ContainsKey(candidate)).ToList();
                    if (matches.Count == 1) Add(matches[0], $"hard [BepInDependency] {dependency.Guid} of {Path.GetFileName(path)}");
                    else Gap("plugin", dependency.Guid, $"{Path.GetFileName(path)} requires {dependency.Guid}{(dependency.MinimumVersion == null ? "" : " >= " + dependency.MinimumVersion)}; supply its plugin DLL in an explicit search root or choose among the candidates.", matches);
                }
                foreach (var dependency in plugin.Dependencies.Where(item => !item.Hard))
                    if (!result.OptionalCandidates.Contains(dependency.Guid, StringComparer.Ordinal)) result.OptionalCandidates.Add(dependency.Guid);
            }
            foreach (string reference in assembly.References)
            {
                if (provided.Contains(reference) || result.OptionalReferences.Contains(reference, StringComparer.OrdinalIgnoreCase)
                    || selected.Keys.Any(candidate => known[candidate].AssemblyName.Equals(reference, StringComparison.OrdinalIgnoreCase))) continue;
                var matches = inventory.Where(item => item.Metadata.AssemblyName.Equals(reference, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Path).Where(candidate => !selected.ContainsKey(candidate)).ToList();
                bool soft = assembly.Plugins.SelectMany(plugin => plugin.Dependencies.Where(dep => !dep.Hard))
                    .Any(dep => matches.Any(candidate => known[candidate].Plugins.Any(p => p.Guid == dep.Guid)));
                if (soft) Gap("optional-reference", reference, $"{Path.GetFileName(path)} references {reference}, which may be behind a soft dependency. Confirm optionalReferences explicitly or select its DLL; metadata alone cannot prove it is safe to omit.", matches);
                else if (matches.Count == 1) Add(matches[0], $"assembly reference {reference} of {Path.GetFileName(path)}");
                else Gap("assembly", reference, $"{Path.GetFileName(path)} references {reference}; supply its DLL in an explicit search root, or explicitly confirm optionalReferences if it is guarded.", matches);
            }
        }
        foreach (var group in selected.Keys.SelectMany(path => known[path].Plugins.Select(plugin => (Path: path, plugin.Guid)))
            .GroupBy(entry => entry.Guid, StringComparer.Ordinal).Where(group => group.Count() > 1))
            Gap("duplicate-plugin", group.Key, $"{group.Key} is declared by multiple selected DLLs; BepInEx would skip one. Select one build.", group.Select(entry => entry.Path));
        foreach (var (path, metadata) in selected.Keys.Select(path => (path, known[path])))
            foreach (var plugin in metadata.Plugins)
                foreach (string incompatible in plugin.Incompatibilities)
                    if (selected.Keys.Any(candidate => known[candidate].Plugins.Any(p => p.Guid == incompatible)))
                        Gap("incompatible-plugin", plugin.Guid, $"{plugin.Guid} declares [BepInIncompatibility] with selected {incompatible}; remove one mod.", [path]);
        foreach (var (path, reason) in selected)
            if (!request.Mods.Contains(path, StringComparer.Ordinal) && !result.CliFiles.Any(file => file.File == path)) result.Plugins.Add(Pinned(path, reason));
        result.Plugins.Sort((a, b) => StringComparer.Ordinal.Compare(a.File, b.File));
        result.OptionalCandidates.Sort(StringComparer.Ordinal);
        return result;
    }

    private static NativeDependencyFile Pinned(string path, string reason) => new(path, WorldFixture.Hash(path), reason);
    private static bool AtLeast(string version, string? minimum) => minimum == null ||
        Version.TryParse(version, out var have) && Version.TryParse(minimum, out var need) && have >= need;
}
