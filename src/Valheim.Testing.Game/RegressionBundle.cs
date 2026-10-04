using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Valheim.Testing.Game;

/// <summary>
/// What a public bundle of a targeted native regression is made from, kept beside the private evidence: the runner's
/// source files, an optional probe project, how the toolkit is pinned, the plugin IDs relevant to the issue, and each arm's
/// private evidence directory with the result the operator declares. The environment comes from the run's own
/// <see cref="Environment"/> manifest, or, for a run made before such manifests, from a <see cref="Template"/> written with
/// placeholders. A runner ported from a hand-written native one names it in <see cref="Native"/>, with the lines that must
/// appear verbatim. <see cref="Read"/> resolves relative paths against the spec's own directory and refuses unknown fields.
/// </summary>
public sealed class BundleSpec
{
    public string Title { get; set; } = "";
    /// <summary>The public issue or pull request the regression is for.</summary>
    public string Issue { get; set; } = "";
    /// <summary>A short description of what the scenario does, for the README.</summary>
    public string Summary { get; set; } = "";
    /// <summary>The run's <see cref="RegressionEnvironment"/> manifest (private; only read). Its template is generated.</summary>
    public string? Environment { get; set; }
    /// <summary>Instead of <see cref="Environment"/>: a manifest whose machine-specific values are all <c>&lt;...&gt;</c> placeholders, bundled as is.</summary>
    public string? Template { get; set; }
    /// <summary>The runner project's name in the bundle: letters, digits, <c>.</c> and <c>_</c>.</summary>
    public string Runner { get; set; } = "";
    /// <summary>The runner's C# source files, copied exactly; they must hold no machine path, account data or unrelated plugin name.</summary>
    public List<string> Sources { get; set; } = [];
    /// <summary>An optional game-side probe project, copied exactly into <c>Probe/</c>.</summary>
    public BundleProbe? Probe { get; set; }
    public BundleToolkit Toolkit { get; set; } = new();
    /// <summary>The mod under test's plugin GUID, needed only when an arm's evidence has no <c>run-manifest.json</c>.</summary>
    public string? ModPlugin { get; set; }
    /// <summary>The plugin IDs the issue is about (the mod, its dependencies, the probe): the only plugin names the bundle may hold.</summary>
    public List<string> Plugins { get; set; } = [];
    /// <summary>Further names that must not appear anywhere in the bundle (a character, a host name).</summary>
    public List<string> Deny { get; set; } = [];
    public Dictionary<string, BundleArm> Arms { get; set; } = [];
    /// <summary>For a runner ported from a hand-written native one: that runner and what differs.</summary>
    public BundleNative? Native { get; set; }
    /// <summary>What the result does not establish, for the README.</summary>
    public List<string> Limitations { get; set; } = [];

    private static readonly Regex RunnerName = new(@"^[A-Za-z][A-Za-z0-9_.]*\z", RegexOptions.CultureInvariant);

    public static BundleSpec Read(string path)
    {
        path = Path.GetFullPath(path);
        BundleSpec spec;
        try { spec = ClientPlanFile.Read<BundleSpec>(path); }
        catch (JsonException error) { throw new ArgumentException($"{path} is not a bundle spec: {error.Message}", error); }
        string directory = Path.GetDirectoryName(path)!;
        string Full(string value) => value.Length == 0 || Path.IsPathFullyQualified(value) ? value : Path.GetFullPath(Path.Combine(directory, value));
        if (spec.Environment != null) spec.Environment = Full(spec.Environment);
        if (spec.Template != null) spec.Template = Full(spec.Template);
        spec.Sources = spec.Sources.Select(Full).ToList();
        if (spec.Probe != null) spec.Probe.Directory = Full(spec.Probe.Directory);
        foreach (var arm in spec.Arms.Values) arm.Evidence = Full(arm.Evidence);
        if (spec.Native != null) spec.Native.Runner = Full(spec.Native.Runner);
        spec.Validate();
        return spec;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Title) || Title.Contains('\n')) throw new ArgumentException("title: give the bundle a one-line title.");
        if (Issue.Length != 0 && !Issue.StartsWith("https://", StringComparison.Ordinal)) throw new ArgumentException("issue: give the public issue's https URL, or leave it out.");
        if ((Environment == null) == (Template == null))
            throw new ArgumentException("environment or template: give the run's environment manifest, or (for a run made before one existed) a template with placeholders; not both.");
        if (!Path.IsPathFullyQualified(Environment ?? Template!)) throw new ArgumentException("environment or template: give the file's path.");
        if (!RunnerName.IsMatch(Runner)) throw new ArgumentException("runner: name the runner project with letters, digits, . and _.");
        if (Sources.Count == 0) throw new ArgumentException("sources: list the runner's C# source files.");
        foreach (string source in Sources)
            if (!Path.IsPathFullyQualified(source) || !source.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"sources: {source} is not a C# source file's path.");
        if (Sources.GroupBy(source => Path.GetFileName(source), StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } twice)
            throw new ArgumentException($"sources: two files are named {twice.Key}; the bundle keeps the runner flat.");
        if (Probe != null && (!Path.IsPathFullyQualified(Probe.Directory) || Probe.Project.Length == 0 || Probe.Project != Path.GetFileName(Probe.Project) || !Probe.Project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("probe: give the probe's project directory and its .csproj file name.");
        Toolkit.Validate();
        if (Plugins.Count == 0 || Plugins.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("plugins: list the plugin IDs the issue is about; every other plugin name is refused.");
        if (Arms.Count == 0) throw new ArgumentException("arms: name each arm's evidence directory and declared result.");
        foreach (var (name, arm) in Arms) { RegressionEnvironment.RequireToken(name, "arms"); arm.Validate("arms." + name); }
        Native?.Validate(Sources.Select(source => Path.GetFileName(source)).ToList());
        if (Limitations.Count == 0 || Limitations.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("limitations: state what the result does not establish.");
    }
}

/// <summary>A probe project to copy into the bundle: its directory and project file name.</summary>
public sealed class BundleProbe
{
    public string Directory { get; set; } = "";
    public string Project { get; set; } = "";
}

/// <summary>How the bundled runner gets the toolkit: a published <see cref="Package"/> version, or an exact source <see cref="Commit"/>.</summary>
public sealed class BundleToolkit
{
    public string? Package { get; set; }
    public string? Commit { get; set; }
    public string Repository { get; set; } = "https://github.com/tvongaza/ValheimTesting";
    public void Validate()
    {
        if ((Package == null) == (Commit == null)) throw new ArgumentException("toolkit: pin either a published package version or an exact source commit, not both.");
        if (Package != null && !Regex.IsMatch(Package, @"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?\z", RegexOptions.CultureInvariant)) throw new ArgumentException($"toolkit.package: \"{Package}\" is not a package version.");
        if (Commit != null && !Regex.IsMatch(Commit, "^[0-9a-f]{40}\\z", RegexOptions.CultureInvariant)) throw new ArgumentException("toolkit.commit: give the full 40-character commit.");
        if (!Regex.IsMatch(Repository, @"^https://github\.com/[\w.-]+/[\w.-]+\z", RegexOptions.CultureInvariant)) throw new ArgumentException("toolkit.repository: give the toolkit's GitHub repository URL.");
    }
}

/// <summary>
/// One arm's private evidence directory and the result the operator declares for it. The build's commit, MD5 and SHA256 are
/// read from the evidence's <c>run-manifest.json</c> and the environment manifest; a value given here too must agree, and
/// evidence without a run manifest needs <see cref="Commit"/> and <see cref="Md5"/>.
/// </summary>
public sealed class BundleArm
{
    public string Evidence { get; set; } = "";
    /// <summary><c>pass</c> or <c>fail</c>: checked against the evidence, never taken from an exit code.</summary>
    public string Expect { get; set; } = "";
    /// <summary>For an arm expected to fail: the step that must be its first failure.</summary>
    public string? FailingStep { get; set; }
    public string? Commit { get; set; }
    /// <summary>The mod build's MD5: every strict pin of the mod in the command trace must be this.</summary>
    public string? Md5 { get; set; }
    /// <summary>The mod build's SHA256, when the evidence does not record it; shown as declared, not checked.</summary>
    public string? Sha256 { get; set; }
    public void Validate(string field)
    {
        if (!Path.IsPathFullyQualified(Evidence)) throw new ArgumentException($"{field}.evidence: give the arm's evidence directory.");
        if (Expect is not ("pass" or "fail")) throw new ArgumentException($"{field}.expect: declare pass or fail.");
        if (FailingStep != null && Expect != "fail") throw new ArgumentException($"{field}.failingStep is for an arm expected to fail.");
        if (Commit != null && (Commit.Length == 0 || Commit.Any(char.IsWhiteSpace))) throw new ArgumentException($"{field}.commit: give the source commit the arm was built from.");
        if (Md5 != null && (Md5.Length != 32 || !Md5.All(Uri.IsHexDigit))) throw new ArgumentException($"{field}.md5: give the mod build's MD5.");
        if (Sha256 != null && (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit))) throw new ArgumentException($"{field}.sha256: give the mod build's SHA256.");
    }
}

/// <summary>
/// The hand-written native runner a bundled runner was ported from: the file (private; only read and hashed), what it ran
/// with, the lines of it that must appear verbatim in a bundled source, and what differs, for the README and manifest.
/// </summary>
public sealed class BundleNative
{
    public string Runner { get; set; } = "";
    /// <summary>The toolkit and harness the native run used, as a reader should know it (a commit or package version).</summary>
    public string RanWith { get; set; } = "";
    public List<BundleExcerpt> Excerpts { get; set; } = [];
    /// <summary>Every difference between the native runner and the bundle, which must be environment-only.</summary>
    public List<string> Differences { get; set; } = [];
    public void Validate(IReadOnlyList<string> sources)
    {
        if (!Path.IsPathFullyQualified(Runner)) throw new ArgumentException("native.runner: give the native runner's source file.");
        if (string.IsNullOrWhiteSpace(RanWith) || RanWith.Contains('\n')) throw new ArgumentException("native.ranWith: say in one line which toolkit the native run used.");
        if (Excerpts.Count == 0) throw new ArgumentException("native.excerpts: name the native lines (the assertions) that the bundle holds verbatim.");
        foreach (var excerpt in Excerpts)
            if (excerpt.From < 1 || excerpt.To < excerpt.From || !sources.Contains(excerpt.In, StringComparer.Ordinal))
                throw new ArgumentException($"native.excerpts: an excerpt needs lines from..to of the native runner and the bundled source it appears in ({excerpt.In} is not one).");
        if (Differences.Count == 0 || Differences.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("native.differences: list what differs from the native runner.");
    }
}

/// <summary>Lines <see cref="From"/> to <see cref="To"/> of the native runner, which must appear verbatim in the bundled file <see cref="In"/>.</summary>
public sealed class BundleExcerpt
{
    public int From { get; set; }
    public int To { get; set; }
    public string In { get; set; } = "";
}

/// <summary>Where the bundle checks a toolkit pin is available: published package versions and source commits.</summary>
public interface IBundleSources
{
    /// <summary>Every published version of the package, or null when the feed does not know it.</summary>
    IReadOnlyList<string>? PackageVersions(string id);
    /// <summary>Whether the repository has the commit.</summary>
    bool HasCommit(string repository, string commit);
}

/// <summary>NuGet.org's flat container and GitHub's commit API. Needs the network; an unanswered query throws rather than passes.</summary>
public sealed class PublicBundleSources : IBundleSources
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static PublicBundleSources() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Valheim.Testing.Game-RegressionBundle");

    public IReadOnlyList<string>? PackageVersions(string id)
    {
        using var reply = Http.GetAsync($"https://api.nuget.org/v3-flatcontainer/{id.ToLowerInvariant()}/index.json").GetAwaiter().GetResult();
        if (reply.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        reply.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(reply.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return document.RootElement.GetProperty("versions").EnumerateArray().Select(version => version.GetString()!).ToList();
    }

    public bool HasCommit(string repository, string commit)
    {
        string path = new Uri(repository).AbsolutePath.Trim('/');
        using var reply = Http.GetAsync($"https://api.github.com/repos/{path}/commits/{commit}").GetAwaiter().GetResult();
        if (reply.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.UnprocessableEntity) return false;
        reply.EnsureSuccessStatusCode();
        return true;
    }
}

/// <summary>One file of a bundle: its path, SHA256 and where it came from.</summary>
public sealed record BundleFileEntry(string Path, string Sha256, string Origin);
/// <summary>Something the bundle leaves out on purpose, and why.</summary>
public sealed record BundleOmission(string What, string Why);
/// <summary>A private evidence file the bundle was checked against, by SHA256 only.</summary>
public sealed record BundleEvidence(string Arm, string File, string Sha256);
/// <summary>One arm's checked result, as the README's table states it; <paramref name="Sha256Source"/> says where the SHA256 came from.</summary>
public sealed record BundleArmResult(string Arm, string Commit, string Md5, string? Sha256, string? Sha256Source, string Expect, bool Passed, int StepsPassed, int Steps, string? FirstFailure);
/// <summary>The native runner a ported bundle was checked against (by SHA256), what it ran with, the checked excerpts and the differences.</summary>
public sealed record BundleNativeRecord(string RunnerSha256, string RanWith, IReadOnlyList<string> Excerpts, IReadOnlyList<string> Differences);
/// <summary>
/// <c>BUNDLE-MANIFEST.json</c>: every file with its origin, what was left out and why, the environment-only fields replaced
/// by placeholders, the native runner of a port, the private evidence checked (by hash), the plugin allowlist and the checks made.
/// </summary>
public sealed record BundleManifest(int Schema, string Title, string Runner, BundleToolkit Toolkit, IReadOnlyList<BundleFileEntry> Files,
    IReadOnlyList<BundleOmission> Omitted, IReadOnlyList<string> EnvironmentOnly, BundleNativeRecord? Native, IReadOnlyList<BundleEvidence> Evidence,
    IReadOnlyList<BundleArmResult> Results, IReadOnlyList<string> Plugins, IReadOnlyList<string> Checks);

/// <summary>
/// Turns a targeted native regression into a directory a person reviews before sharing it, and never publishes anything. The
/// runner and probe sources are copied exactly; machine-specific configuration stays in the environment manifest, of which
/// only a template with placeholders is bundled. Before writing anything it checks the toolkit pin is available (and not
/// older than the native run's), each arm's <c>result.json</c> against its <c>junit.xml</c>, the strict pins in its command
/// trace, its <c>run-manifest.json</c> and the operator's declared result and hashes, that the arms' traces differ only in
/// the mod's pin, and that a ported runner holds the native assertions verbatim. The README's A/B table is generated from
/// those checked results. Every bundled file is then scrubbed (<see cref="Verify"/>): raw logs, saves, traces and binaries,
/// machine paths, account data, the environment's private names and any plugin name outside <see cref="BundleSpec.Plugins"/>
/// are refused with file:line. A refused bundle is removed.
/// </summary>
public static class RegressionBundle
{
    public const string ManifestFile = "BUNDLE-MANIFEST.json", ReadmeFile = "README.md", TemplateFile = "regression.template.json", ProbeDirectory = "Probe";
    public const string Package = "Valheim.Testing.Game";
    private static readonly string[] AllowedExtensions = [".cs", ".csproj", ".props", ".targets", ".json", ".md"];
    // Readable files: a person reviews them before sharing, so <placeholders> and apostrophes stay as written.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions TemplateJson = new(Json) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Writes the bundle of <paramref name="spec"/> to <paramref name="output"/>, which must not exist, after every check; a
    /// failed check writes nothing and throws <see cref="InvalidOperationException"/> naming each problem.
    /// </summary>
    public static BundleManifest Create(BundleSpec spec, string output, IBundleSources sources)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(sources);
        spec.Validate();
        output = Path.GetFullPath(output);
        if (Path.Exists(output)) throw new IOException($"{output} already exists; give the bundle a new directory.");
        var environment = spec.Environment == null ? null : RegressionEnvironment.Read(spec.Environment);
        var (template, environmentOnly) = environment != null ? Template(environment) : ReadTemplate(spec.Template!);
        var evidence = CheckEvidence(spec, environment, template);
        var checks = evidence.Checks.ToList();
        checks.Add(CheckToolkit(spec, evidence.Toolkits, sources));
        var native = spec.Native == null ? null : CheckNative(spec);
        if (native != null) checks.AddRange(native.Excerpts);

        // A unique sibling is ours to clean up. A predictable ".incomplete" directory may belong to someone else.
        string staging = output + ".incomplete-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            var ported = spec.Native?.Excerpts.Select(excerpt => excerpt.In).ToHashSet(StringComparer.Ordinal) ?? [];
            var files = new List<BundleFileEntry>();
            foreach (string source in spec.Sources)
            {
                string name = Path.GetFileName(source);
                File.Copy(source, Path.Combine(staging, name));
                files.Add(new(name, "", spec.Native == null ? "copied exactly from the runner that ran"
                    : ported.Contains(name) ? "the port: holds the native runner's assertion lines verbatim (checked; see README.md)"
                    : "the harness the port runs with; the native run used its own (see README.md)"));
            }
            if (spec.Probe != null) files.AddRange(CopyProbe(spec.Probe, staging));
            string project = spec.Runner + ".csproj";
            File.WriteAllText(Path.Combine(staging, project), RunnerProject(spec));
            files.Add(new(project, "", spec.Toolkit.Package != null ? $"generated: {Package} {spec.Toolkit.Package} from NuGet.org" : $"generated: {Package} from source commit {spec.Toolkit.Commit}"));
            if (environment != null)
            {
                File.WriteAllText(Path.Combine(staging, TemplateFile), JsonSerializer.Serialize(template, TemplateJson) + "\n");
                files.Add(new(TemplateFile, "", "generated from the environment manifest: every machine-specific value replaced by a placeholder; hashes kept"));
            }
            else
            {
                File.Copy(spec.Template!, Path.Combine(staging, TemplateFile));
                files.Add(new(TemplateFile, "", "written for this bundle (the native run had no environment manifest); every machine-specific value is a placeholder (checked)"));
            }
            var omitted = new List<BundleOmission>
            {
                new("the environment manifest", "it holds machine paths, the disposable character and the fixture; regression.template.json has its shape with placeholders"),
                new("each arm's result.json, junit.xml, run-manifest.json and client-commands.jsonl", "private evidence with machine context; checked here and summarised in README.md, listed by SHA256 below"),
                new("logs, saves, the fixture world and the staged DLLs", "never bundled"),
            };
            if (spec.Native != null) omitted.Add(new("the native runner", "it holds environment detail; its assertions are in the port verbatim and it is listed by SHA256 under native"));
            if (evidence.Unrelated.Count != 0) omitted.Add(new($"{evidence.Unrelated.Count} plugin pin(s) of the native run outside the allowlist", "environment detail, not part of the issue; their names are refused anywhere in the bundle"));
            files.Add(new(ReadmeFile, "", "generated from the checked results"));
            int count = files.Count + 1; // And the manifest itself.
            File.WriteAllText(Path.Combine(staging, ReadmeFile), Readme(spec, template, evidence, native, files, count));
            files = files.Select(file => file with { Sha256 = WorldFixture.Hash(Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar))) }).ToList();
            var manifest = new BundleManifest(1, spec.Title, spec.Runner, spec.Toolkit, files, omitted, environmentOnly, native, evidence.Files, evidence.Results, spec.Plugins, checks);
            File.WriteAllText(Path.Combine(staging, ManifestFile), JsonSerializer.Serialize(manifest, Json) + "\n");
            Verify(staging, spec, environment, evidence);
            Directory.Move(staging, output);
            return manifest;
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    /// <summary>
    /// The operator's check before sharing, also the last step of <see cref="Create"/>: the arms' evidence still gives the
    /// manifest's results, the directory holds exactly the files its manifest lists, each with its SHA256, the README states
    /// that count, the template holds only placeholders, and every file passes the scrub. Needs no network. Throws
    /// <see cref="InvalidOperationException"/> listing every problem with file:line.
    /// </summary>
    public static void Verify(string bundle, BundleSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        spec.Validate();
        var environment = spec.Environment == null ? null : RegressionEnvironment.Read(spec.Environment);
        string template = Path.Combine(Path.GetFullPath(bundle), TemplateFile);
        RegressionEnvironment? bundled = null;
        try { bundled = ClientPlanFile.Read<RegressionEnvironment>(template); }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException) { }
        Verify(bundle, spec, environment, CheckEvidence(spec, environment, bundled));
    }

    private static void Verify(string bundle, BundleSpec spec, RegressionEnvironment? environment, EvidenceCheck evidence)
    {
        bundle = Path.GetFullPath(bundle);
        var problems = new List<string>();
        BundleManifest? manifest = null;
        try { manifest = JsonSerializer.Deserialize<BundleManifest>(File.ReadAllText(Path.Combine(bundle, ManifestFile)), Json); }
        catch (Exception error) when (error is IOException or JsonException) { problems.Add($"{ManifestFile}: missing or unreadable ({error.Message})"); }
        var actual = Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(bundle, path).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        if (manifest != null)
        {
            var listed = manifest.Files.Select(file => file.Path).Append(ManifestFile).ToHashSet(StringComparer.Ordinal);
            foreach (string extra in actual.Where(path => !listed.Contains(path))) problems.Add($"{extra}: not in {ManifestFile}; every bundled file is listed with its origin");
            foreach (var file in manifest.Files)
            {
                string path = Path.Combine(bundle, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) problems.Add($"{file.Path}: listed in {ManifestFile} but missing");
                else if (!WorldFixture.Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) problems.Add($"{file.Path}: changed after the bundle was generated");
            }
            string readme = Path.Combine(bundle, ReadmeFile);
            var stated = File.Exists(readme) ? Regex.Match(File.ReadAllText(readme), @"This bundle holds (\d+) files") : Match.Empty;
            if (!stated.Success) problems.Add($"{ReadmeFile}: does not state how many files the bundle holds");
            else if (int.Parse(stated.Groups[1].Value, CultureInfo.InvariantCulture) != actual.Count)
                problems.Add($"{ReadmeFile}: says the bundle holds {stated.Groups[1].Value} files, and it holds {actual.Count}");
            if (!manifest.Results.SequenceEqual(evidence.Results))
                problems.Add($"{ManifestFile}: its results are not what the arms' evidence gives now ({string.Join("; ", evidence.Results.Select(Describe))})");
        }
        try
        {
            foreach (string problem in TemplateProblems(ClientPlanFile.Read<RegressionEnvironment>(Path.Combine(bundle, TemplateFile)))) problems.Add($"{TemplateFile}: {problem}");
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException) { problems.Add($"{TemplateFile}: missing or not an environment manifest ({error.Message})"); }
        problems.AddRange(Scrub(bundle, actual, RulesFor(spec, environment, evidence)));
        if (problems.Count != 0)
            throw new InvalidOperationException($"The bundle in {bundle} is not ready to share:\n  " + string.Join("\n  ", problems) +
                "\nFix the sources or the spec and generate it again; nothing was published.");
    }

    /// <summary>
    /// Builds the bundled runner as a stranger would: a fresh copy, NuGet.org as the only package source, an isolated
    /// <c>NUGET_PACKAGES</c> and HTTP cache, and for a commit-pinned bundle a fresh clone of the toolkit at that commit.
    /// Throws naming the failure: an unavailable package or commit, or source that does not compile. Needs the network.
    /// The probe needs game assemblies and is not built.
    /// </summary>
    public static void Build(string bundle, TimeSpan? timeout = null)
    {
        bundle = Path.GetFullPath(bundle);
        var manifest = JsonSerializer.Deserialize<BundleManifest>(File.ReadAllText(Path.Combine(bundle, ManifestFile)), Json)
            ?? throw new InvalidDataException($"{ManifestFile} is empty.");
        manifest.Toolkit.Validate();
        var deadline = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromMinutes(10);
        string root = Directory.CreateTempSubdirectory("bundle-build-").FullName;
        try
        {
            string copy = Path.Combine(root, "bundle");
            foreach (string file in Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(copy, Path.GetRelativePath(bundle, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            const string NuGetOrg = "https://api.nuget.org/v3/index.json";
            File.WriteAllText(Path.Combine(root, "nuget.config"),
                $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration><packageSources><clear/><add key=\"nuget.org\" value=\"{NuGetOrg}\"/></packageSources></configuration>\n");
            var arguments = new List<string> { "build", manifest.Runner + ".csproj", "-c", "Release", "-nologo", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-p:RestoreSources=" + NuGetOrg };
            if (manifest.Toolkit.Commit is { } commit)
            {
                // A stranger's checkout: the commit must be reachable from the public repository.
                string toolkit = Path.Combine(root, "ValheimTesting");
                Run("git", ["clone", "--quiet", "--no-checkout", manifest.Toolkit.Repository + ".git", toolkit], root, Remaining(deadline, limit),
                    $"{manifest.Toolkit.Repository} could not be cloned");
                Run("git", ["-C", toolkit, "checkout", "--quiet", commit], root, Remaining(deadline, limit),
                    $"{manifest.Toolkit.Repository} has no commit {commit} on any branch a clone fetches");
                arguments.Add("-p:ValheimTestingRoot=" + toolkit);
            }
            Run(DotNet(), arguments, copy, Remaining(deadline, limit),
                "The bundle's runner does not build in a clean directory with only NuGet.org: the pinned package or commit is unavailable, or the source does not compile",
                new Dictionary<string, string> { ["NUGET_PACKAGES"] = Path.Combine(root, "packages"), ["NUGET_HTTP_CACHE_PATH"] = Path.Combine(root, "http-cache") });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static TimeSpan Remaining(Stopwatch clock, TimeSpan limit)
    {
        var left = limit - clock.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private static string DotNet() => System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

    private static void Run(string program, IEnumerable<string> arguments, string directory, TimeSpan timeout, string failure, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(program) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        // A caller inside an SDK command (a test host) must not steer this build with its MSBuild variables.
        foreach (string name in new[] { "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildLoadMicrosoftTargetsReadOnly" }) start.Environment.Remove(name);
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var (name, value) in environment ?? new Dictionary<string, string>()) start.Environment[name] = value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{failure}: {program} did not finish in time.");
        }
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var lines = (stdout.Result + "\n" + stderr.Result).Split('\n').Select(line => line.Trim()).Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.StartsWith("fatal", StringComparison.Ordinal)).Distinct().Take(12);
            throw new InvalidOperationException($"{failure} ({program} exit {process.ExitCode}).\n  " + string.Join("\n  ", lines));
        }
    }

    // ---- the arms' evidence ----

    private sealed record ResultFile(string Name, Dictionary<string, string> Provenance, List<StepResult> Steps, bool Passed, string? Pinning);

    // Every strict pin in a command trace: the mod's builds and how often they were pinned, the world UIDs, and every other plugin's values.
    private sealed record TracePins(int ModPins, IReadOnlySet<string> ModBuilds, IReadOnlySet<string> Worlds, IReadOnlyDictionary<string, SortedSet<string>> Others);

    private sealed record ArmCheck(BundleArmResult Result, TracePins Pins, IReadOnlyList<BundleEvidence> Files, string Check, string? Toolkit, IReadOnlyList<string> Private, IReadOnlyList<string> ModPlugins);

    private sealed record EvidenceCheck(IReadOnlyList<BundleArmResult> Results, IReadOnlyList<BundleEvidence> Files, IReadOnlyList<string> Checks, IReadOnlyList<string> Toolkits,
        IReadOnlySet<string> Unrelated, IReadOnlyList<string> Private, IReadOnlyList<string> ModPlugins);

    private static EvidenceCheck CheckEvidence(BundleSpec spec, RegressionEnvironment? environment, RegressionEnvironment? template)
    {
        if (environment != null && !environment.Mod.Arms.Keys.Order(StringComparer.Ordinal).SequenceEqual(spec.Arms.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidOperationException($"The spec's arms ({string.Join(", ", spec.Arms.Keys)}) are not the environment's ({string.Join(", ", environment.Mod.Arms.Keys)}).");
        var arms = spec.Arms.Select(entry => CheckArm(spec, environment, entry.Key, entry.Value)).ToList();
        var checks = arms.Select(arm => arm.Check).ToList();

        // A/B evidence: the arms ran in the same world with the same plugins; only the mod's build differs.
        var worlds = arms.SelectMany(arm => arm.Pins.Worlds).Distinct(StringComparer.Ordinal).ToList();
        if (worlds.Count != 1) throw new InvalidOperationException($"The arms' command traces pinned the worlds {string.Join(", ", worlds)}: an A/B comparison runs every arm on the same fixture.");
        if (environment != null && worlds[0] != environment.Fixture.WorldUid)
            throw new InvalidOperationException($"The command traces pinned world UID {worlds[0]}, not the environment's {environment.Fixture.WorldUid}.");
        var differences = new List<string>();
        var first = arms[0];
        foreach (var other in arms.Skip(1))
            foreach (string key in first.Pins.Others.Keys.Union(other.Pins.Others.Keys).Order(StringComparer.Ordinal))
            {
                string Values(ArmCheck arm) => arm.Pins.Others.TryGetValue(key, out var values) ? string.Join("|", values) : "unpinned";
                if (Values(first) != Values(other)) differences.Add($"{key} is {Values(first)} in {first.Result.Arm} and {Values(other)} in {other.Result.Arm}");
            }
        if (differences.Count != 0)
            throw new InvalidOperationException($"More than the mod under test differs between the arms' strict pins: {string.Join("; ", differences)}. Rerun the arms with only the mod's DLL changed.");
        if (arms.Count > 1)
            checks.Add($"the arms' command traces pin the same world and the same build of every other plugin; only the mod's build differs");
        var results = arms.Select(arm => arm.Result).ToList();
        if (results.GroupBy(result => result.Md5).FirstOrDefault(group => group.Count() > 1) is { } same && !(environment?.Mod.Repeatability ?? false))
            throw new InvalidOperationException($"The arms {string.Join(" and ", same.Select(result => result.Arm))} ran the same build (md5 {same.Key}): comparing a build with itself proves nothing.");

        if (template != null)
        {
            if (!template.Mod.Arms.Keys.Order(StringComparer.Ordinal).SequenceEqual(spec.Arms.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw new InvalidOperationException($"{TemplateFile}: its arms ({string.Join(", ", template.Mod.Arms.Keys)}) are not the spec's ({string.Join(", ", spec.Arms.Keys)}).");
            foreach (var result in results)
            {
                var arm = template.Mod.Arms[result.Arm];
                if (arm.Commit.Length != 0 && !IsPlaceholder(arm.Commit) && arm.Commit != result.Commit)
                    throw new InvalidOperationException($"{TemplateFile}: arm {result.Arm} names commit {arm.Commit}, and the evidence ran {result.Commit}.");
                if (arm.Sha256.Length != 0 && !IsPlaceholder(arm.Sha256) && result.Sha256 != null && !arm.Sha256.Equals(result.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{TemplateFile}: arm {result.Arm} pins sha256 {arm.Sha256}, and the evidence ran {result.Sha256}.");
            }
        }
        var unrelated = arms.SelectMany(arm => arm.Pins.Others.Keys).Where(key => !Allowed(spec, key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(results, arms.SelectMany(arm => arm.Files).ToList(), checks, arms.Select(arm => arm.Toolkit).OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
            unrelated, arms.SelectMany(arm => arm.Private).Append(worlds[0]).Distinct(StringComparer.Ordinal).ToList(),
            arms.SelectMany(arm => arm.ModPlugins).Distinct(StringComparer.Ordinal).ToList());
    }

    private static ArmCheck CheckArm(BundleSpec spec, RegressionEnvironment? environment, string name, BundleArm arm)
    {
        string where = $"arm {name} ({arm.Evidence})";
        string resultPath = Path.Combine(arm.Evidence, "result.json"), junitPath = Path.Combine(arm.Evidence, "junit.xml"), tracePath = Path.Combine(arm.Evidence, "client-commands.jsonl");
        foreach (string required in new[] { resultPath, junitPath, tracePath })
            if (!File.Exists(required)) throw new InvalidOperationException($"{where}: {Path.GetFileName(required)} is missing; a result is never taken from an exit code.");
        ResultFile result;
        try { result = JsonSerializer.Deserialize<ResultFile>(File.ReadAllText(resultPath)) ?? throw new JsonException("empty"); }
        catch (JsonException error) { throw new InvalidOperationException($"{where}: result.json is not a scenario report ({error.Message}).", error); }
        if (result.Steps == null || result.Provenance == null) throw new InvalidOperationException($"{where}: result.json has no steps or provenance; it is not a scenario report.");
        if (result.Pinning != EnvironmentPinning.Strict)
            throw new InvalidOperationException($"{where}: result.json records pinning {result.Pinning ?? "(none)"}: only a strictly pinned run is evidence of which builds ran.");
        bool stepsPassed = result.Steps.Count > 0 && result.Steps.All(step => step.Passed);
        if (result.Passed != stepsPassed)
            throw new InvalidOperationException($"{where}: result.json says Passed={result.Passed}, but its steps say {(stepsPassed ? "every step passed" : "a step failed")}: the file was edited after the run.");
        var suite = XDocument.Load(junitPath).Root ?? throw new InvalidDataException($"{where}: junit.xml is empty.");
        // junit.xml holds one suite per phase, in phase order (a schema-1 report has one suite and every step in Scenario).
        var cases = suite.Descendants("testcase").Where(test => test.Attribute("name")?.Value != EnvironmentPinning.NotPinned).ToList();
        var disagreements = new List<string>();
        if (cases.Count != result.Steps.Count) disagreements.Add($"{cases.Count} test cases for {result.Steps.Count} steps");
        foreach (var (step, test) in result.Steps.OrderBy(step => step.Phase).Zip(cases))
        {
            if (test.Attribute("name")?.Value != step.Name) disagreements.Add($"step \"{step.Name}\" is test case \"{test.Attribute("name")?.Value}\"");
            else if ((test.Element("failure") == null) != step.Passed) disagreements.Add($"\"{step.Name}\" is {(step.Passed ? "passed" : "failed")} in result.json and {(step.Passed ? "failed" : "passed")} in junit.xml");
        }
        if (int.TryParse(suite.Attribute("failures")?.Value, out int failures) && failures != result.Steps.Count(step => !step.Passed))
            disagreements.Add($"junit.xml counts {failures} failures, result.json {result.Steps.Count(step => !step.Passed)}");
        if (disagreements.Count != 0)
            throw new InvalidOperationException($"{where}: result.json and junit.xml disagree ({string.Join("; ", disagreements)}): one was edited after the run.");

        // Which build ran: every source that records it must agree, and the declared values must be among them.
        string manifestPath = Path.Combine(arm.Evidence, "run-manifest.json");
        RunManifest? run = File.Exists(manifestPath) ? JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(manifestPath), TargetedRegression.ManifestJson) : null;
        if (run != null && run.Arm != name) throw new InvalidOperationException($"{where}: run-manifest.json is arm {run.Arm}'s.");
        var declared = environment?.Mod.Arms[name];
        // The first source that records a value, after every source that records it agrees (case aside).
        (string? Value, string? Source) Agree(string field, params (string Source, string? Value)[] values)
        {
            var given = values.Where(value => value.Value != null).ToList();
            if (given.Select(value => value.Value!.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Count() > 1)
                throw new InvalidOperationException($"{where}: the declared build is not the one that ran: {field} is {string.Join(", ", given.Select(value => $"{value.Value} in {value.Source}"))}.");
            return given.Count == 0 ? (null, null) : (given[0].Value, given[0].Source);
        }
        string commit = Agree("commit", ("run-manifest.json", run?.ModCommit), ("the environment manifest", declared?.Commit), ("the spec", arm.Commit)).Value
            ?? throw new InvalidOperationException($"{where}: no run-manifest.json records the build's source commit; set the arm's commit in the spec.");
        string md5 = Agree("md5", ("run-manifest.json", run?.Arms.FirstOrDefault(entry => entry.Arm == name)?.Md5), ("the spec", arm.Md5)).Value?.ToLowerInvariant()
            ?? throw new InvalidOperationException($"{where}: no run-manifest.json records the build's MD5; set the arm's md5 in the spec (the value its strict pins name).");
        var (sha256, sha256Source) = Agree("sha256", ("run-manifest.json", run?.ModSha256), ("the environment manifest", declared?.Sha256),
            ("result.json", result.Provenance.GetValueOrDefault("modSha256")), ("the spec", arm.Sha256));
        sha256 = sha256?.ToLowerInvariant();
        if (sha256Source == "the spec") sha256Source = "declared in the spec; not in the evidence";
        if (declared != null && File.Exists(declared.File) && (!WorldFixture.Hash(declared.File).Equals(declared.Sha256, StringComparison.OrdinalIgnoreCase) || PluginPins.Md5(declared.File) != md5))
            throw new InvalidOperationException($"{where}: {declared.File} is not the declared build (sha256 {declared.Sha256.ToLowerInvariant()}, md5 {md5}).");
        var guids = (run?.ModPlugin ?? spec.ModPlugin ?? throw new InvalidOperationException($"{where}: no run-manifest.json; set modPlugin in the spec to the mod's plugin GUID."))
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // The trace's strict pins say which build every command ran against.
        var pins = ReadTrace(tracePath, where, guids);
        string plugin = string.Join(", ", guids);
        if (pins.ModPins == 0) throw new InvalidOperationException($"{where}: the command trace never pinned {plugin}; it is not a strict run of the mod under test.");
        if (pins.ModBuilds.Any(build => build != md5))
            throw new InvalidOperationException($"{where}: the command trace pinned {plugin}={string.Join(", ", pins.ModBuilds.Where(build => build != md5))}, not the arm's {md5}: the trace is another arm's or another build's.");
        if (pins.Worlds.Count == 0) throw new InvalidOperationException($"{where}: the command trace never pinned the world UID; no command ran in the fixture world.");
        if (result.Provenance.TryGetValue("worldUid", out string? uid) && !pins.Worlds.Contains(uid))
            throw new InvalidOperationException($"{where}: result.json records world UID {uid}, and the command trace pinned {string.Join(", ", pins.Worlds)}.");

        var failed = result.Steps.FirstOrDefault(step => !step.Passed);
        if (arm.Expect == "pass" && failed != null)
            throw new InvalidOperationException($"{where}: declared pass, but result.json fails at \"{failed.Name}\": {failed.Error}");
        if (arm.Expect == "fail" && failed == null) throw new InvalidOperationException($"{where}: declared fail, but every step in result.json passed.");
        if (arm.FailingStep != null && failed!.Name != arm.FailingStep)
            throw new InvalidOperationException($"{where}: declared to fail at \"{arm.FailingStep}\", but result.json first fails at \"{failed.Name}\": {failed.Error}");
        var files = new[] { resultPath, junitPath, tracePath, manifestPath }.Where(File.Exists).Select(path => new BundleEvidence(name, Path.GetFileName(path), WorldFixture.Hash(path))).ToList();
        // What the evidence shows of the private environment: the world's name and any machine path a provenance value holds.
        var privateNames = result.Provenance.Where(entry => entry.Key == "hostWorld" || LooksLikePath(entry.Value)).Select(entry => entry.Value).Concat(pins.Worlds).ToList();
        string check = $"arm {name}: result.json agrees with junit.xml ({result.Steps.Count} steps, strict pinning) and with the declared {arm.Expect}" +
            (arm.FailingStep != null ? $" at \"{arm.FailingStep}\"" : "") + $"; {pins.ModPins} strict pins of {plugin} in the command trace, all {md5}" + (run != null ? "; run-manifest.json agrees" : "");
        return new(new(name, commit, md5, sha256, sha256Source, arm.Expect, failed == null, result.Steps.Count(step => step.Passed), result.Steps.Count,
            failed == null ? null : $"{failed.Name}: {failed.Error}"), pins, files, check, result.Provenance.GetValueOrDefault("toolkit"), privateNames, guids);
    }

    private static bool LooksLikePath(string value) => Regex.IsMatch(value, @"^([A-Za-z]:[\\/]|\\\\|/)", RegexOptions.CultureInvariant);

    private static TracePins ReadTrace(string path, string where, IReadOnlyCollection<string> mod)
    {
        int modPins = 0, number = 0;
        var builds = new HashSet<string>(StringComparer.Ordinal);
        var worlds = new HashSet<string>(StringComparer.Ordinal);
        var others = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(path))
        {
            number++;
            if (line.Trim().Length == 0) continue;
            string command;
            try { using var document = JsonDocument.Parse(line); command = document.RootElement.GetProperty("command").GetString() ?? ""; }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            { throw new InvalidOperationException($"{where}: client-commands.jsonl line {number} is not a command record: the trace was edited or truncated.", error); }
            const string Strict = "cli_expect --strict ";
            if (!command.StartsWith(Strict, StringComparison.Ordinal)) continue;
            foreach (string token in command[Strict.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = token.IndexOf('=');
                if (equals <= 0) continue;
                string key = token[..equals], value = token[(equals + 1)..].ToLowerInvariant();
                if (mod.Contains(key, StringComparer.Ordinal)) { modPins++; builds.Add(value); }
                else if (key == "worlduid") worlds.Add(token[(equals + 1)..]);
                else if (key != "world") (others.TryGetValue(key, out var values) ? values : others[key] = new(StringComparer.Ordinal)).Add(value);
            }
        }
        return new(modPins, builds, worlds, others);
    }

    private static string CheckToolkit(BundleSpec spec, IReadOnlyList<string> native, IBundleSources sources)
    {
        var toolkit = spec.Toolkit;
        if (toolkit.Package is { } version)
        {
            var published = sources.PackageVersions(Package);
            if (published == null || !published.Contains(version, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"toolkit.package: {Package} {version} is not published on NuGet.org" +
                    (published == null ? "" : $" (newest {published.LastOrDefault()})") + ": pin a published version, or the exact source commit the run used.");
            foreach (string ran in native)
            {
                string ranVersion = ran.Split(' ').Last();
                if (CompareVersions(version, ranVersion) < 0)
                    throw new InvalidOperationException($"toolkit.package: {version} is older than the {ranVersion} the native run used, so it may lack what the runner calls. " +
                        $"Pin {ranVersion} or newer once published, or the exact source commit the run used.");
            }
            return $"toolkit: {Package} {version} is published on NuGet.org" + (native.Count == 0 ? "; the native run recorded no toolkit version" : $" and not older than the native run's {string.Join(", ", native)}");
        }
        if (!sources.HasCommit(toolkit.Repository, toolkit.Commit!))
            throw new InvalidOperationException($"toolkit.commit: {toolkit.Repository} has no commit {toolkit.Commit}: push it, or pin a commit or package version that exists.");
        return $"toolkit: {toolkit.Repository} has commit {toolkit.Commit}";
    }

    // NuGet's order for the versions this toolkit publishes: x.y.z, then a pre-release's dot-separated parts (numbers numerically).
    internal static int CompareVersions(string a, string b)
    {
        static (Version Core, string[] Pre) Split(string text)
        {
            int dash = text.IndexOf('-');
            return (Version.Parse(dash < 0 ? text : text[..dash]), dash < 0 ? [] : text[(dash + 1)..].Split('.'));
        }
        var (left, right) = (Split(a.Split('+')[0]), Split(b.Split('+')[0]));
        int core = left.Core.CompareTo(right.Core);
        if (core != 0) return core;
        if (left.Pre.Length == 0 || right.Pre.Length == 0) return left.Pre.Length == right.Pre.Length ? 0 : left.Pre.Length == 0 ? 1 : -1; // A release follows its pre-releases.
        for (int i = 0; i < Math.Min(left.Pre.Length, right.Pre.Length); i++)
        {
            bool ln = int.TryParse(left.Pre[i], out int l), rn = int.TryParse(right.Pre[i], out int r);
            int part = ln && rn ? l.CompareTo(r) : ln ? -1 : rn ? 1 : string.CompareOrdinal(left.Pre[i], right.Pre[i]);
            if (part != 0) return part;
        }
        return left.Pre.Length.CompareTo(right.Pre.Length);
    }

    private static BundleNativeRecord CheckNative(BundleSpec spec)
    {
        static List<string> Normal(IEnumerable<string> lines) => lines.Select(line => Regex.Replace(line.Trim(), @"\s+", " ")).Where(line => line.Length != 0).ToList();
        var native = spec.Native!;
        var lines = File.ReadAllLines(native.Runner);
        var checks = new List<string>();
        foreach (var excerpt in native.Excerpts)
        {
            if (excerpt.To > lines.Length) throw new InvalidOperationException($"native: the native runner has {lines.Length} lines, not {excerpt.To}.");
            var wanted = Normal(lines[(excerpt.From - 1)..excerpt.To]);
            if (wanted.Count == 0) throw new InvalidOperationException($"native: lines {excerpt.From}-{excerpt.To} of the native runner are blank.");
            var bundled = Normal(File.ReadAllLines(spec.Sources.First(source => Path.GetFileName(source) == excerpt.In)));
            bool found = Enumerable.Range(0, Math.Max(0, bundled.Count - wanted.Count + 1)).Any(start => bundled.Skip(start).Take(wanted.Count).SequenceEqual(wanted, StringComparer.Ordinal));
            if (!found)
            {
                int best = Enumerable.Range(0, Math.Max(1, bundled.Count)).Max(start => bundled.Skip(start).Zip(wanted).TakeWhile(pair => pair.First == pair.Second).Count());
                throw new InvalidOperationException($"native: {excerpt.In} does not hold lines {excerpt.From}-{excerpt.To} of the native runner verbatim; the first that differs is \"{wanted[Math.Min(best, wanted.Count - 1)]}\". The public assertions must be the ones that ran.");
            }
            checks.Add($"native: lines {excerpt.From}-{excerpt.To} of the native runner ({wanted.Count} non-blank lines) appear verbatim in {excerpt.In}, whitespace aside");
        }
        return new(WorldFixture.Hash(native.Runner), native.RanWith.Trim(), checks, native.Differences.Select(difference => difference.Trim()).ToList());
    }

    // ---- what the bundle holds ----

    private static List<BundleFileEntry> CopyProbe(BundleProbe probe, string staging)
    {
        if (!File.Exists(Path.Combine(probe.Directory, probe.Project))) throw new FileNotFoundException($"probe: {probe.Project} is not in {probe.Directory}.");
        var files = new List<BundleFileEntry>();
        foreach (string path in Directory.EnumerateFiles(probe.Directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(probe.Directory, path).Replace('\\', '/');
            if (relative.Split('/') is [("bin" or "obj"), ..] || InstallPins.IsMacMetadata(path)) continue;
            if (!AllowedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"probe: {relative} is not source ({string.Join(", ", AllowedExtensions)}); a bundle carries no binaries, logs or saves.");
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(File.ReadAllText(path), @"(Include|Project)=""\.\.[\\/]"))
                throw new InvalidOperationException($"probe: {relative} imports or references files outside its directory; a bundled probe project must build on its own.");
            string target = Path.Combine(staging, ProbeDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
            files.Add(new($"{ProbeDirectory}/{relative}", "", "copied exactly from the probe that ran"));
        }
        return files;
    }

    private static string RunnerProject(BundleSpec spec)
    {
        var text = new StringBuilder();
        text.Append("<Project Sdk=\"Microsoft.NET.Sdk\">\n");
        text.Append("  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><RollForward>Major</RollForward><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>\n");
        if (spec.Probe != null) text.Append($"  <ItemGroup><Compile Remove=\"{ProbeDirectory}/**\" /></ItemGroup>\n");
        if (spec.Toolkit.Package != null)
            text.Append($"  <ItemGroup><PackageReference Include=\"{Package}\" Version=\"[{spec.Toolkit.Package}]\" /></ItemGroup>\n");
        else
        {
            text.Append($"  <!-- The toolkit from source: pass -p:ValheimTestingRoot=<a checkout of {spec.Toolkit.Repository} at {spec.Toolkit.Commit}>. -->\n");
            text.Append($"  <PropertyGroup><ValheimTestingCommit>{spec.Toolkit.Commit}</ValheimTestingCommit></PropertyGroup>\n");
            text.Append("  <ItemGroup><ProjectReference Include=\"$(ValheimTestingRoot)/src/Valheim.Testing.Game/Valheim.Testing.Game.csproj\" /></ItemGroup>\n");
            text.Append("  <Target Name=\"CheckValheimTestingCommit\" BeforeTargets=\"Restore;CollectPackageReferences;Build\">\n");
            text.Append("    <Error Condition=\"'$(ValheimTestingRoot)' == ''\" Text=\"Pass -p:ValheimTestingRoot=&lt;a ValheimTesting checkout at $(ValheimTestingCommit)&gt;.\" />\n");
            text.Append("    <Exec Command=\"git -C &quot;$(ValheimTestingRoot)&quot; rev-parse HEAD\" ConsoleToMSBuild=\"true\" StandardOutputImportance=\"low\"><Output TaskParameter=\"ConsoleOutput\" PropertyName=\"ValheimTestingHead\" /></Exec>\n");
            text.Append("    <Error Condition=\"'$(ValheimTestingHead)' != '$(ValheimTestingCommit)'\" Text=\"$(ValheimTestingRoot) is at $(ValheimTestingHead), not $(ValheimTestingCommit), the commit this bundle pins.\" />\n");
            text.Append("  </Target>\n");
        }
        text.Append("</Project>\n");
        return text.ToString();
    }

    // The environment manifest's shape with every machine-specific value replaced; returns it and the fields replaced.
    private static (RegressionEnvironment Template, List<string> Replaced) Template(RegressionEnvironment environment)
    {
        var replaced = new List<string>();
        string Placeholder(string field, string text) { replaced.Add(field); return $"<{text}>"; }
        string FileOf(string field, string file) => Placeholder(field, "path of " + Path.GetFileNameWithoutExtension(file)) + "/" + Path.GetFileName(file);
        RegressionFile Copy(RegressionFile file, string field) => new() { File = FileOf(field + ".file", file.File), Sha256 = file.Sha256.ToLowerInvariant() };
        var template = new RegressionEnvironment
        {
            Name = environment.Name,
            Game = Placeholder("game", "full path of a prepared Valheim install with BepInEx; only read"),
            Install = Placeholder("install", "full path of a new directory for the disposable install"),
            Client = new()
            {
                Port = environment.Client.Port, LaunchArguments = environment.Client.LaunchArguments, StartSeconds = environment.Client.StartSeconds, JoinSeconds = environment.Client.JoinSeconds,
                Character = Placeholder("client.character", "a disposable local character's file name"),
            },
            Fixture = new() { Root = Placeholder("fixture.root", "directory holding exactly one world folder"), WorldUid = Placeholder("fixture.worldUid", "that world's UID") },
            Cli = new()
            {
                Core = Copy(environment.Cli.Core, "cli.core"), Packs = environment.Cli.Packs.Select((pack, i) => Copy(pack, $"cli.packs[{i}]")).ToList(),
                Manifest = environment.Cli.Manifest == null ? null : Placeholder("cli.manifest", "that ValheimCLI build's capability manifest"),
            },
            Plugins = environment.Plugins.Select((plugin, i) => Copy(plugin, $"plugins[{i}]")).ToList(),
            Probe = environment.Probe == null ? null : Copy(environment.Probe, "probe"),
            Patchers = environment.Patchers.Select((patcher, i) => Copy(patcher, $"patchers[{i}]")).ToList(),
            Configs = environment.Configs.ToDictionary(entry => entry.Key, entry => Placeholder($"configs.{entry.Key}", "path of " + entry.Key)),
            OptionalReferences = environment.OptionalReferences,
            GamePins = environment.GamePins,
            Mod = new()
            {
                InstallAs = environment.Mod.InstallAs, Repeatability = environment.Mod.Repeatability,
                Arms = environment.Mod.Arms.ToDictionary(entry => entry.Key, entry => new RegressionArm
                {
                    File = FileOf($"mod.arms.{entry.Key}.file", entry.Value.File), Sha256 = entry.Value.Sha256.ToLowerInvariant(), Commit = entry.Value.Commit,
                }),
            },
        };
        if (environment.Client.SaveDirectory != null) replaced.Add("client.saveDirectory (left out)");
        replaced.Add("client.port (kept as a default)");
        if (TemplateProblems(template).FirstOrDefault() is { } problem) throw new InvalidOperationException("The generated template is not all placeholders: " + problem);
        return (template, replaced);
    }

    private static (RegressionEnvironment Template, List<string> Replaced) ReadTemplate(string path)
    {
        RegressionEnvironment template;
        try { template = ClientPlanFile.Read<RegressionEnvironment>(path); }
        catch (JsonException error) { throw new ArgumentException($"template: {path} is not an environment manifest's shape: {error.Message}", error); }
        var problems = TemplateProblems(template).ToList();
        if (problems.Count != 0) throw new InvalidOperationException($"template: {path} holds machine-specific values; make each a <...> placeholder: " + string.Join("; ", problems));
        var replaced = new List<string>();
        foreach (var (field, value) in Fields(template)) if (value != null && (IsPlaceholder(value) || IsPlaceholderFile(value))) replaced.Add(field);
        replaced.Add("client.port (kept as a default)");
        return (template, replaced);
    }

    private static bool IsPlaceholder(string? value) => value != null && Regex.IsMatch(value, @"^<[^<>\r\n]+>\z", RegexOptions.CultureInvariant);
    private static bool IsPlaceholderFile(string value) => Regex.IsMatch(value, @"^<[^<>\r\n]+>/[^/\\<>\r\n]+\z", RegexOptions.CultureInvariant);

    // Every machine-specific field of a manifest, by name.
    private static IEnumerable<(string Field, string? Value)> Fields(RegressionEnvironment manifest)
    {
        yield return ("game", manifest.Game);
        yield return ("install", manifest.Install);
        yield return ("client.character", manifest.Client.Character);
        yield return ("client.saveDirectory", manifest.Client.SaveDirectory);
        yield return ("fixture.root", manifest.Fixture.Root);
        yield return ("fixture.worldUid", manifest.Fixture.WorldUid);
        yield return ("cli.manifest", manifest.Cli.Manifest);
        yield return ("cli.core.file", manifest.Cli.Core.File);
        foreach (var (pack, i) in manifest.Cli.Packs.Select((pack, i) => (pack, i))) yield return ($"cli.packs[{i}].file", pack.File);
        foreach (var (plugin, i) in manifest.Plugins.Select((plugin, i) => (plugin, i))) yield return ($"plugins[{i}].file", plugin.File);
        if (manifest.Probe != null) yield return ("probe.file", manifest.Probe.File);
        foreach (var (patcher, i) in manifest.Patchers.Select((patcher, i) => (patcher, i))) yield return ($"patchers[{i}].file", patcher.File);
        foreach (var (name, source) in manifest.Configs) yield return ($"configs.{name}", source);
        foreach (var (name, arm) in manifest.Mod.Arms) yield return ($"mod.arms.{name}.file", arm.File);
    }

    private static IEnumerable<string> TemplateProblems(RegressionEnvironment template)
    {
        foreach (var (field, value) in Fields(template))
        {
            bool file = field.EndsWith(".file", StringComparison.Ordinal) || field.StartsWith("configs.", StringComparison.Ordinal) || field == "cli.manifest";
            bool optional = field is "client.saveDirectory" or "cli.manifest";
            if (value == null ? !optional : !(IsPlaceholder(value) || (file && IsPlaceholderFile(value))))
                yield return $"{field} is \"{value}\", not a <...> placeholder" + (file ? " (or <...>/file name)" : "");
        }
    }

    private static string Readme(BundleSpec spec, RegressionEnvironment template, EvidenceCheck evidence, BundleNativeRecord? native, IReadOnlyList<BundleFileEntry> files, int count)
    {
        var text = new StringBuilder();
        text.Append($"# {spec.Title}\n\n");
        text.Append("_Generated by ValheimTesting's regression bundler for human review. Nothing in this directory has been published. Review every file before sharing it (see the checklist at the end)._\n\n");
        if (spec.Issue.Length != 0) text.Append($"Issue: {spec.Issue}\n\n");
        if (spec.Summary.Length != 0) text.Append(spec.Summary.Trim()).Append("\n\n");
        text.Append("## Native A/B result\n\n");
        text.Append("| Arm | Source commit | Build MD5 | Build SHA256 | Declared | Result | Steps passed | First failure |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var result in evidence.Results)
            text.Append($"| {result.Arm} | `{result.Commit}` | `{result.Md5}` | {(result.Sha256 == null ? "not recorded" : $"`{result.Sha256}`" + (result.Sha256Source!.StartsWith("declared", StringComparison.Ordinal) ? " (declared)" : ""))} | {result.Expect} | **{(result.Passed ? "pass" : "fail")}** | {result.StepsPassed}/{result.Steps} | {Cell(result.FirstFailure)} |\n");
        text.Append($"\nEach row comes from that arm's `result.json`, checked against its `junit.xml`, every strict pin of `{string.Join(", ", evidence.ModPlugins.DefaultIfEmpty(template.Mod.InstallAs))}` in its command trace (each pinned the arm's MD5) and the declared result.");
        if (evidence.Results.Count > 1) text.Append(" The arms' traces pin the same world and the same build of every other plugin: only the mod under test differs.");
        text.Append(" The raw evidence stays private; `BUNDLE-MANIFEST.json` lists it by SHA256.\n\n");
        if (native != null)
        {
            text.Append("## The native run and this bundle\n\n");
            text.Append($"The native run used a hand-written runner with {native.RanWith}. The rounds and assertions of that runner appear verbatim (whitespace aside) in {string.Join(", ", spec.Native!.Excerpts.Select(excerpt => $"`{excerpt.In}`").Distinct())}; this was checked when the bundle was made. The other sources are the harness this bundle runs them with, **which has not itself been run in the game**. What differs from the native runner:\n\n");
            foreach (string difference in native.Differences) text.Append($"- {difference}\n");
            text.Append('\n');
        }
        text.Append("## What the run loads\n\n");
        text.Append("A clean install of Valheim with BepInEx and only these files in `BepInEx/plugins` (see `regression.template.json` for their hashes):\n\n");
        text.Append($"- ValheimCLI: `{FileName(template.Cli.Core.File)}`" + string.Concat(template.Cli.Packs.Select(pack => $", `{FileName(pack.File)}`")) + ", from one build\n");
        foreach (var plugin in template.Plugins) text.Append($"- `{FileName(plugin.File)}`\n");
        if (template.Probe != null) text.Append($"- the test probe `{FileName(template.Probe.File)}`" + (spec.Probe != null ? $", built from `{ProbeDirectory}/`" : "") + "\n");
        text.Append($"- the mod under test as `{template.Mod.InstallAs}`, one arm at a time\n\n");
        text.Append($"Plugin IDs relevant to this issue: {string.Join(", ", spec.Plugins.Select(id => $"`{id}`"))}.\n\n");
        text.Append("## Build and run\n\n");
        string toolkit = spec.Toolkit.Package != null ? $"`{Package}` `{spec.Toolkit.Package}` from NuGet.org" : $"{spec.Toolkit.Repository} at commit `{spec.Toolkit.Commit}`";
        text.Append($"Toolkit: {toolkit}. Requires the .NET 10 SDK, a Valheim install with BepInEx in a desktop session with Steam, a disposable fixture world and a disposable **local** character.\n\n```sh\n");
        if (spec.Toolkit.Commit != null)
            text.Append($"git clone {spec.Toolkit.Repository}.git ValheimTesting\ngit -C ValheimTesting checkout {spec.Toolkit.Commit}\n");
        string root = spec.Toolkit.Commit != null ? " -p:ValheimTestingRoot=\"$PWD/ValheimTesting\"" : "";
        string run = $"dotnet run --project {spec.Runner}.csproj -c Release{root} --";
        text.Append($"cp {TemplateFile} regression.json   # then replace every <...> with a value of your own machine\n");
        text.Append($"dotnet build {spec.Runner}.csproj -c Release{root}\n{run} preflight regression.json\n");
        foreach (string arm in spec.Arms.Keys) text.Append($"{run} run regression.json {arm} <new-evidence-directory-for-{arm}>\n");
        text.Append("```\n\n");
        text.Append("`preflight` stages and checks every arm without starting the game. Use a new evidence directory for every run and compare the arms by their `result.json`, never by an exit code.");
        if (spec.Probe != null) text.Append($" Build the probe in `{ProbeDirectory}/` against your own game and BepInEx assemblies, as its project file describes; the commands above do not build it.");
        text.Append("\n\n## Limitations\n\n");
        foreach (string limitation in spec.Limitations) text.Append($"- {limitation.Trim()}\n");
        text.Append("- Machine-specific values (paths, the character, the fixture and its UID) live only in your copy of the environment manifest; `BUNDLE-MANIFEST.json` lists the fields.\n\n");
        text.Append($"## Files\n\nThis bundle holds {count} files:\n\n");
        foreach (var file in files) text.Append($"- `{file.Path}`: {file.Origin}\n");
        text.Append($"- `{ManifestFile}`: every file's origin and SHA256, what was left out and why, the checks made\n\n");
        text.Append("## Before sharing\n\n- [ ] Read every file, including this one.\n- [ ] Run the bundle verification again after any edit.\n- [ ] Confirm the result table matches the private evidence.\n- [ ] Confirm no name, path or identifier beyond the issue's appears.\n");
        return text.ToString();
    }

    private static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    private static string Cell(string? text) => text == null ? "" : text.Replace("|", "\\|").Replace("\n", " ").Trim();

    private static string Describe(BundleArmResult result) => $"{result.Arm} {(result.Passed ? "pass" : "fail")} {result.StepsPassed}/{result.Steps} md5 {result.Md5}";

    // ---- the scrub ----

    private sealed record ScrubRules(IReadOnlyList<(Regex Pattern, string Kind)> Private, IReadOnlySet<string> Denied, BundleSpec Spec);

    private static bool Allowed(BundleSpec spec, string id) =>
        spec.Plugins.Contains(id, StringComparer.OrdinalIgnoreCase) || id.StartsWith("valheimCLI.", StringComparison.OrdinalIgnoreCase) || id is "world" or "worlduid";

    private static readonly (Regex Pattern, string Kind)[] Leaks =
    [
        (new(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/]", RegexOptions.CultureInvariant), "a Windows machine path"),
        (new(@"\\\\[A-Za-z0-9._$-]+\\", RegexOptions.CultureInvariant), "a network share path"),
        (new(@"(?<![\w.~])/(Users|home|private|var/folders|tmp|mnt|Volumes|root)/", RegexOptions.CultureInvariant), "a machine path"),
        (new(@"(?<![\w])~/|%USERPROFILE%|%APPDATA%|AppData[\\/]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "a user directory"),
        (new(@"\b7656119\d{10}\b", RegexOptions.CultureInvariant), "a Steam account ID"),
        (new(@"\b(Steam|PlayFab)_\d{6,}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), "a platform account ID"),
        (new(@"\b[\w.+-]+@[\w-]+(\.[\w-]+)+\b", RegexOptions.CultureInvariant), "an email address"),
    ];
    private static readonly Regex[] PluginMentions =
    [
        new(@"(?<![\w.\-])([A-Za-z][\w.\-]*)=(?:[0-9a-fA-F]{32}|absent)\b", RegexOptions.CultureInvariant),
        new(@"\[\s*""([^""]+)""\s*\]\s*=\s*(?:""absent""|""[0-9a-fA-F]{32}""|\w*Md5\b)", RegexOptions.CultureInvariant),
        new(@"""([^""]+)""\s*:\s*""absent""", RegexOptions.CultureInvariant),
        new(@"BepIn(?:Plugin|Dependency|Incompatibility)\(\s*""([^""]+)""", RegexOptions.CultureInvariant),
    ];

    private static IEnumerable<string> Scrub(string bundle, IReadOnlyList<string> files, ScrubRules rules)
    {
        foreach (string relative in files)
        {
            if (!AllowedExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase))
            {
                yield return $"{relative}: not source or text this bundle carries ({Path.GetExtension(relative)}); raw logs, saves, traces and binaries are never bundled";
                continue;
            }
            int number = 0;
            foreach (string line in File.ReadLines(Path.Combine(bundle, relative.Replace('/', Path.DirectorySeparatorChar))))
            {
                number++;
                foreach (var (pattern, kind) in Leaks)
                    if (pattern.Match(line) is { Success: true } match) yield return $"{relative}:{number}: {kind} ({match.Value.Trim()})";
                foreach (var (pattern, kind) in rules.Private)
                    if (pattern.IsMatch(line)) yield return $"{relative}:{number}: {kind} from the private environment";
                foreach (var pattern in PluginMentions)
                    foreach (Match match in pattern.Matches(line))
                        if (!Allowed(rules.Spec, match.Groups[1].Value))
                            yield return $"{relative}:{number}: names plugin {match.Groups[1].Value}, which is not in the plugin allowlist; remove it (an unrelated pin is environment detail) or add it to plugins if the issue is about it";
                foreach (string denied in rules.Denied)
                    if (Regex.IsMatch(line, $@"(?<![\w]){Regex.Escape(denied)}(?![\w])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        yield return $"{relative}:{number}: names {denied}, which is outside the plugin allowlist";
            }
        }
    }

    private static ScrubRules RulesFor(BundleSpec spec, RegressionEnvironment? environment, EvidenceCheck evidence)
    {
        var secrets = new List<(Regex, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Secret(string? text, string kind)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < 3) return;
            text = Path.TrimEndingDirectorySeparator(text.Trim());
            foreach (string form in new[] { text, text.Replace('\\', '/') }.Distinct())
                if (seen.Add(form)) secrets.Add((new Regex($@"(?<![\w]){Regex.Escape(form)}(?![\w])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), kind));
        }
        var denied = new HashSet<string>(evidence.Unrelated, StringComparer.OrdinalIgnoreCase);
        denied.UnionWith(spec.Deny);
        // The directories of every private input.
        foreach (string? path in new[] { spec.Environment, spec.Template, spec.Native?.Runner, spec.Probe?.Directory }.Concat(spec.Sources).Select(path => path == null ? null : Path.GetDirectoryName(path))
            .Concat(spec.Arms.Values.Select(arm => arm.Evidence)))
            Secret(path, "a path");
        foreach (string text in evidence.Private) Secret(text, LooksLikePath(text) ? "a path" : "a name or ID of the fixture world");
        if (environment != null)
        {
            foreach (string? path in new[] { environment.Game, environment.Install, environment.Fixture.Root, environment.Client.SaveDirectory, environment.Cli.Manifest }
                .Concat(environment.Configs.Values.Select(path => Path.GetDirectoryName(path)))
                .Concat(new[] { environment.Cli.Core }.Concat(environment.Cli.Packs).Concat(environment.Plugins).Concat(environment.Patchers).Concat(environment.Probe == null ? [] : [environment.Probe]).Select(file => Path.GetDirectoryName(file.File)))
                .Concat(environment.Mod.Arms.Values.Select(arm => Path.GetDirectoryName(arm.File))))
                Secret(path, "a path");
            Secret(environment.Client.Character, "the disposable character's name");
            Secret(environment.Fixture.WorldUid, "the fixture world's UID");
            // Plugins of the prepared game that the run does not load: environment detail, never named in the bundle.
            string plugins = Path.Combine(environment.Game, "BepInEx", "plugins");
            if (Directory.Exists(plugins))
            {
                var staged = new[] { environment.Cli.Core }.Concat(environment.Cli.Packs).Concat(environment.Plugins).Concat(environment.Probe == null ? [] : [environment.Probe])
                    .Select(file => Path.GetFileNameWithoutExtension(file.File)).Append(Path.GetFileNameWithoutExtension(environment.Mod.InstallAs)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string dll in Directory.EnumerateFiles(plugins, "*.dll", SearchOption.AllDirectories))
                {
                    if (staged.Contains(Path.GetFileNameWithoutExtension(dll))) continue;
                    denied.Add(Path.GetFileNameWithoutExtension(dll));
                    try { foreach (var plugin in PluginMetadata.Read(dll).Plugins) denied.Add(plugin.Guid); }
                    catch (Exception error) when (error is InvalidDataException or BadImageFormatException or IOException) { }
                }
            }
        }
        denied.RemoveWhere(name => Allowed(spec, name) || name.Length < 3);
        return new(secrets, denied, spec);
    }
}
