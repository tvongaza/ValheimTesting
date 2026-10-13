// Build the preview public-surface reference after bootstrap-cli.cs and a Release build of
// docs/reference/ValheimTesting.Api.sln (full validate.cs also supplies that build).
// No game process or game assemblies are needed.
//
//   dotnet tool restore
//   dotnet run scripts/bootstrap-cli.cs
//   dotnet run scripts/validate.cs
//   dotnet run scripts/api-docs.cs                       the documentation site: guides, packages and the API (docs/reference/_site, not committed)
//   dotnet run scripts/api-docs.cs -- surface            write docs/reference/public-api/<Package>.txt from the release builds
//   dotnet run scripts/api-docs.cs -- check              release.yml: the committed lists match the builds
//   dotnet run scripts/api-docs.cs -- check-docs          CI: selected 0.1 entrypoint pages and members have summaries
//
// The public-api lists are the reviewed public surface (#292, #135): every public or protected type and member of each
// package, one per line. Pull requests do not regenerate them (#683): cutting a release does, so the release preparation's
// diff reviews the surface since the last release, and release.yml refuses lists that do not match its build.
// In Valheim.Testing.Game every public type is an operation, a result shape or an input an operation takes. A result shape
// declares no operation, carries the internal [ResultShape] attribute, is listed as "[result shape]" and is versioned with
// any JSON it is written to. Both commands refuse a type that breaks this (Classify below).
#:package Mono.Cecil@0.11.6
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

string root = RepositoryPaths.FindRoot();
// The site publishes the landing page, getting started, the package table and every page in these folders; a page
// elsewhere under docs/ (maintainers/, agent pages) stays on GitHub. A published page also needs an entry in docs/toc.yml.
string[] publishedRootPages = ["index.md", "getting-started.md", "package-versions.md"];
string[] publishedFolders = ["guides", "concepts", "tutorials", "packages", "reference"];
string[] packagePages = ["Valheim.Testing", "Valheim.Testing.Doubles", "Valheim.Testing.Cli", "Valheim.Testing.Game",
    "Valheim.Testing.GameSessions", "Valheim.Testing.NativeSmoke", "Valheim.Testing.Adapter",
    "Valheim.Testing.Bindings", "Valheim.Testing.Bindings.Tool"];
// Each package's release build. Every public type in it is the package's surface (Doubles' game stand-ins included), except
// in the Adapter's compile check, which also holds the stubs it compiles against: only its own namespace counts there.
// Classified: each public type must be an operation, a labelled result shape or an operation's input (#292).
(string Package, string Assembly, string? Namespace, bool Classified)[] packages =
[
    ("Valheim.Testing", "src/Valheim.Testing/bin/Release/netstandard2.0/Valheim.Testing.dll", null, false),
    ("Valheim.Testing.Doubles", "src/Valheim.Testing.Doubles/bin/Release/netstandard2.0/Valheim.Testing.Doubles.dll", null, false),
    ("Valheim.Testing.Game", "src/Valheim.Testing.Game/bin/Release/net10.0/Valheim.Testing.Game.dll", null, true),
    ("Valheim.Testing.GameSessions", "src/Valheim.Testing.GameSessions/bin/Release/net10.0/Valheim.Testing.GameSessions.dll", null, true),
    ("Valheim.Testing.Bindings", "src/Valheim.Testing.Bindings/bin/Release/netstandard2.0/Valheim.Testing.Bindings.dll", null, false),
    ("Valheim.Testing.Bindings.Tool", "src/Valheim.Testing.Bindings.Tool/bin/Release/net10.0/Valheim.Testing.Bindings.Tool.dll", null, false),
    ("Valheim.Testing.Adapter", "tests/Valheim.Testing.Adapter.CompileCheck/bin/Release/net48/Valheim.Testing.Adapter.CompileCheck.dll", "Valheim.Testing.Adapter", false),
];
foreach (var package in packages)
    if (!File.Exists(Path.Combine(root, package.Assembly)))
        throw new FileNotFoundException("Run validate.cs first; a release build is missing.", package.Assembly);
string listDirectory = Path.Combine(root, "docs", "reference", "public-api");
string siteSource = Path.Combine(root, "artifacts", "docs-site");
if (args is ["surface"])
{
    Directory.CreateDirectory(listDirectory);
    foreach (var package in packages)
        File.WriteAllText(Path.Combine(listDirectory, package.Package + ".txt"), ApiList(package.Package, Path.Combine(root, package.Assembly), package.Namespace));
    Console.WriteLine($"Public API lists written: {Path.GetRelativePath(root, listDirectory)}");
    if (Classify()) return 0;
    Console.Error.WriteLine("Label or internalize the types named above, rebuild (validate.cs), then run this again.");
    return 1;
}
if (args is ["check"]) return CheckLists();
if (args is ["check-docs"]) return CheckDocumentedEntrypoints();
if (args.Length != 0) throw new ArgumentException("usage: dotnet run scripts/api-docs.cs [-- surface | -- check | -- check-docs]");

PreparePages();
var info = new ProcessStartInfo("dotnet")
{
    UseShellExecute = false,
    WorkingDirectory = root,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};
foreach (string argument in new[] { "tool", "run", "docfx", "docs/reference/docfx.json" }) info.ArgumentList.Add(argument);
using Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start DocFX.");
Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
Task<string> standardError = process.StandardError.ReadToEndAsync();
process.WaitForExit();
string output = standardOutput.GetAwaiter().GetResult();
string errors = standardError.GetAwaiter().GetResult();
Console.Write(output);
Console.Error.Write(errors);
if (process.ExitCode != 0) throw new InvalidOperationException($"DocFX failed with exit code {process.ExitCode}.");
// DocFX colours its summary on some CI hosts even when output is redirected; inspect the same text without ANSI SGR.
string transcript = Regex.Replace(output + "\n" + errors, "\u001b\\[[0-9;]*m", "");
if (!Regex.IsMatch(transcript, @"(?m)^\s*0 warning\(s\)\s*$") ||
    Regex.IsMatch(transcript, @"(?im)^\s*warning:"))
    throw new InvalidOperationException("DocFX reported a warning or omitted its zero-warning summary.");
if (output.Contains("InvalidFileLink", StringComparison.Ordinal) ||
    output.Contains("InvalidCrossReference", StringComparison.Ordinal) ||
    errors.Contains("InvalidFileLink", StringComparison.Ordinal) ||
    errors.Contains("InvalidCrossReference", StringComparison.Ordinal))
    throw new InvalidOperationException("DocFX reported a broken documentation link.");

string site = Path.Combine(root, "docs", "reference", "_site");
foreach (string page in new[]
{
    "api/Valheim.Testing.ITerrain.html",
    "api/Valheim.Testing.Doubles.TerrainWorld.html",
    "api/Valheim.Testing.Game.html",
    "api/Valheim.Testing.Adapter.html",
    "api/Valheim.Testing.Bindings.html"
})
    if (!File.Exists(Path.Combine(site, page)))
        throw new InvalidOperationException($"DocFX omitted a package's reference page: {page}");
foreach (string page in PublishedPages())
    if (!File.Exists(Path.Combine(site, Path.ChangeExtension(page, ".html"))))
        throw new InvalidOperationException($"DocFX omitted the site page {page}.");
if (CheckDocumentedEntrypoints() != 0)
    throw new InvalidOperationException("A selected 0.1 entrypoint has no rendered XML summary.");

// These selected examples explain the order of operations. A successful metadata build alone does not prove that
// DocFX rendered an XML <example> on the page a mod author will read.
foreach (var (page, excerpt) in new (string Page, string Excerpt)[]
{
    ("api/Valheim.Testing.CompositeTerrain.html", "float terraceHeight = terrain.GetHeight"),
    ("api/Valheim.Testing.Game.WorldFixture.html", "WorldFixture.Verify(fixtureSource, reviewedHashes)"),
    ("api/Valheim.Testing.Game.DisposableCharacterStore.html", "store.Register(\"tester\", localCharacterFile)"),
    ("api/Valheim.Testing.Game.GameActor.html", "actor.RequireCapability(\"mymod.testing/session\")"),
    ("api/Valheim.Testing.GameSessions.PinnedServerRun.html", "PinnedServerRun.MainAsync(args"),
})
{
    string file = Path.Combine(site, page);
    if (!File.Exists(file) || !File.ReadAllText(file).Contains(excerpt, StringComparison.Ordinal))
        throw new InvalidOperationException($"DocFX omitted the selected contextual example from {page}.");
}

// The solution input keeps project references in DocFX's metadata graph. Check the rendered links too: a warning-free
// build alone does not prove that cross-project links survived a DocFX or solution change.
foreach (var (page, link) in new (string Page, string Link)[]
{
    ("api/Valheim.Testing.Doubles.TerrainWorld.html", "href=\"Valheim.Testing.ITerrain.html\""),
    ("api/Valheim.Testing.Game.TerrainCapture.html", "href=\"Valheim.Testing.ReplayTerrain.html\""),
})
{
    string file = Path.Combine(site, page);
    if (!File.Exists(file) || !File.ReadAllText(file).Contains(link, StringComparison.Ordinal))
        throw new InvalidOperationException($"DocFX did not link {page} to Valheim.Testing ({link}): the project reference was lost.");
}
// DocFX's build manifest records source_base_path as an absolute local path.
// It is build metadata, not a page asset; do not publish it.
File.Delete(Path.Combine(site, "manifest.json"));
string[] privatePaths = [root, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)];
foreach (string file in Directory.EnumerateFiles(site, "*", SearchOption.AllDirectories))
{
    byte[] contents = File.ReadAllBytes(file);
    foreach (string path in privatePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        if (contents.AsSpan().IndexOf(Encoding.UTF8.GetBytes(path)) >= 0)
            throw new InvalidOperationException($"Generated reference contains a build-machine path in {Path.GetRelativePath(site, file)}.");
}

Console.WriteLine("Documentation site ready: docs/reference/_site/index.html");
return 0;

// These types are the selected 0.1 operation entrypoints. Adding a public member to one without an XML summary is a
// CI failure. A new supported entrypoint type must be added here in its PR; public-api lists separately review every
// new public type. The explicit boundaries for other generated types are in docs/reference/documentation-scope.md.
int CheckDocumentedEntrypoints()
{
    string[] entrypoints =
    [
        "Valheim.Testing.ITerrain", "Valheim.Testing.PlaneTerrain", "Valheim.Testing.CompositeTerrain",
        "Valheim.Testing.GridDumpTerrain", "Valheim.Testing.Doubles.TerrainAssert",
        "Valheim.Testing.Game.GameActor", "Valheim.Testing.Game.ScenarioReport", "Valheim.Testing.Game.SiteSearch",
        "Valheim.Testing.Game.PlayerPlacement", "Valheim.Testing.Game.WorldFixture", "Valheim.Testing.Game.DisposableCharacterStore",
        "Valheim.Testing.GameSessions.GameSession", "Valheim.Testing.GameSessions.ClientActor",
        "Valheim.Testing.GameSessions.ServerActor", "Valheim.Testing.GameSessions.PinnedServerRun",
        "Valheim.Testing.Adapter.TestExtension", "Valheim.Testing.Bindings.BindingCheck",
    ];
    bool missing = false;
    foreach (string type in entrypoints)
    {
        string page = Path.Combine(siteSource, "api", type + ".yml");
        if (!File.Exists(page)) { Console.Error.WriteLine($"Missing selected 0.1 entrypoint: {type}"); missing = true; continue; }
        string yaml = File.ReadAllText(page).ReplaceLineEndings("\n");
        string items = yaml.Split("\nreferences:", 2, StringSplitOptions.None)[0];
        string[] entries = Regex.Split(items, @"(?m)^- uid: ");
        if (entries.Length < 2 || !entries[1].StartsWith(type + "\n", StringComparison.Ordinal))
        { Console.Error.WriteLine($"Selected 0.1 entrypoint has no type item: {type}"); missing = true; continue; }
        foreach (string entry in entries.Skip(1))
        {
            string uid = entry.Split('\n', 2)[0];
            if (Regex.IsMatch(entry, @"(?m)^  summary: [^\r\n]*\S[^\r\n]*$")) continue;
            Console.Error.WriteLine($"Selected 0.1 entrypoint has no XML summary: {uid}");
            missing = true;
        }
    }
    if (!missing) Console.WriteLine($"Selected 0.1 entrypoint summaries are present ({entrypoints.Length} types).");
    return missing ? 1 : 0;
}

// The repository's pages are the single source of truth. DocFX publishes copies of the published pages (see
// publishedFolders) in the same layout under artifacts/docs-site, beside the generated types. A relative link to a
// published page stays; a link to anything else points at this exact source revision, never at a moving main branch
// or a build-machine path.
void PreparePages()
{
    string docs = Path.Combine(root, "docs"), packages = Path.Combine(docs, "packages");
    string revision = Git("rev-parse", "HEAD");
    string? releaseVersion = Environment.GetEnvironmentVariable("VALHEIM_DOCS_RELEASE_VERSION");
    if (releaseVersion != null && !Regex.IsMatch(releaseVersion, @"^0\.1\.0(?:-rc\.[1-9][0-9]*)?$"))
        throw new InvalidDataException("VALHEIM_DOCS_RELEASE_VERSION must name a coordinated 0.1 RC or final release.");
    string[] guides = Directory.GetFiles(packages, "*.md").Select(path => Path.GetFileNameWithoutExtension(path)!).Order(StringComparer.Ordinal).ToArray();
    if (!guides.SequenceEqual(packagePages.Order(StringComparer.Ordinal)))
        throw new InvalidDataException("The API site package list and docs/packages guides differ: " + string.Join(", ", guides));
    // DocFX never removes a page it no longer renders, so a rebuild starts from nothing.
    foreach (string stale in new[] { siteSource, Path.Combine(docs, "reference", "_site") })
        if (Directory.Exists(stale)) Directory.Delete(stale, recursive: true);
    Directory.CreateDirectory(siteSource);
    foreach (string page in PublishedPages())
    {
        string file = Path.Combine(docs, page);
        string content = File.ReadAllText(file);
        if (!content.StartsWith("# ", StringComparison.Ordinal))
            throw new InvalidDataException($"docs/{page} needs its title heading first.");
        bool packageGuide = Path.GetDirectoryName(file) == packages;
        if (packageGuide && releaseVersion != null)
            content = content.Replace("[Current preview API reference](https://tvongaza.github.io/ValheimTesting/reference/)",
                "[This release's API reference](https://tvongaza.github.io/ValheimTesting/versions/" + releaseVersion + "/reference/)");
        content = RewriteLinks(content, Path.GetDirectoryName(file)!, "docs/" + page, revision);
        string sourceLink = "https://github.com/tvongaza/ValheimTesting/tree/" + revision;
        int headingEnd = content.IndexOf('\n');
        if (packageGuide)
        {
            string package = Path.GetFileNameWithoutExtension(page);
            string banner = releaseVersion == null
                ? "> Reference source: [`" + revision[..12] + "`](" + sourceLink +
                    "). For the installed package version, use the [published package table](../package-versions.md)."
                : "> Release `" + releaseVersion + "` from source [`" + revision[..12] + "`](" + sourceLink +
                    "); [this package on NuGet.org](https://www.nuget.org/packages/" + package + "/" + releaseVersion + ").";
            content = content[..(headingEnd + 1)] + "\n" + banner + "\n" + content[(headingEnd + 1)..];
        }
        else if (page == "package-versions.md" && releaseVersion != null)
        {
            // The release's own versions are recorded after it is published, so this revision still lists the previous ones.
            content = content[..(headingEnd + 1)] + "\n> Release `" + releaseVersion + "` from source [`" + revision[..12] + "`](" + sourceLink +
                "). The versions below are the ones recorded at this source revision; this release's packages are on NuGet.org.\n" + content[(headingEnd + 1)..];
        }
        string target = Path.Combine(siteSource, page.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
    }
    File.Copy(Path.Combine(docs, "toc.yml"), Path.Combine(siteSource, "toc.yml"));
    CheckToc();
}

// The published pages, relative to docs/ with '/' separators.
List<string> PublishedPages()
{
    string docs = Path.Combine(root, "docs");
    var pages = new List<string>(publishedRootPages);
    foreach (string folder in publishedFolders.Where(folder => Directory.Exists(Path.Combine(docs, folder)))) // a folder exists once its first page does
        foreach (string file in Directory.EnumerateFiles(Path.Combine(docs, folder), "*.md", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(docs, file).Replace('\\', '/');
            if (!relative.StartsWith("reference/public-api/", StringComparison.Ordinal)) pages.Add(relative);
        }
    foreach (string page in pages)
        if (!File.Exists(Path.Combine(docs, page))) throw new FileNotFoundException("A published page is missing.", page);
    return pages.Order(StringComparer.Ordinal).ToList();
}

// docs/toc.yml is the site's navigation: every published page except the landing page appears in it, and every local
// page it names is published. Adding a page to the site is a new entry there.
void CheckToc()
{
    var named = Regex.Matches(File.ReadAllText(Path.Combine(root, "docs", "toc.yml")), @"(?m)^\s*(?:-\s+)?href:\s*(?<href>\S+)\s*$")
        .Select(match => match.Groups["href"].Value).Where(href => !Regex.IsMatch(href, "^[a-zA-Z][a-zA-Z0-9+.-]*:")).ToList();
    var pages = PublishedPages();
    foreach (string page in pages.Where(page => page != "index.md" && !named.Contains(page)))
        throw new InvalidDataException($"docs/{page} is published but has no entry in docs/toc.yml.");
    foreach (string href in named.Where(href => !href.EndsWith('/') && !pages.Contains(href)))
        throw new InvalidDataException($"docs/toc.yml names {href}, which is not a published page.");
}

// Links in a page that sit outside fenced code. baseDirectory is where the page lives in the repository. A target that
// is a published page stays as written (the staged tree has the repository's layout); any other file or folder becomes
// a link to the same path at this source revision.
string RewriteLinks(string content, string baseDirectory, string owner, string revision)
{
    HashSet<string> published = PublishedPages().Select(page => Path.GetFullPath(Path.Combine(root, "docs", page))).ToHashSet();
    string? fence = null;
    var lines = content.Split('\n');
    for (int i = 0; i < lines.Length; i++)
    {
        if (Regex.Match(lines[i], @"^\s{0,3}(?<fence>`{3,}|~{3,})") is { Success: true } marker)
        {
            string run = marker.Groups["fence"].Value;
            if (fence == null) fence = run;
            else if (run[0] == fence[0] && run.Length >= fence.Length && lines[i].Trim() == run) fence = null;
            continue;
        }
        if (fence != null) continue;
        lines[i] = Regex.Replace(lines[i], @"(?<=\]\()(?<target>(?![a-zA-Z][a-zA-Z0-9+.-]*:|#|/)[^)\r\n]+)(?=\))", match =>
        {
            string target = match.Groups["target"].Value;
            int fragment = target.IndexOf('#');
            string path = fragment < 0 ? target : target[..fragment];
            if (path.Length == 0) return target;
            string full = Path.GetFullPath(Path.Combine(baseDirectory, path));
            bool directory = Directory.Exists(full);
            if (!File.Exists(full) && !directory) throw new FileNotFoundException($"{owner} links to a missing file: {target}", full);
            if (published.Contains(full)) return target;
            string relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal)) throw new InvalidDataException($"{owner} links outside the repository: {target}");
            return "https://github.com/tvongaza/ValheimTesting/" + (directory ? "tree/" : "blob/") + revision + "/" + relative +
                (fragment < 0 ? "" : target[fragment..]);
        });
    }
    return string.Join('\n', lines);
}

// The committed lists against the builds.
int CheckLists()
{
    bool classified = Classify(), differs = false;
    var expected = packages.ToDictionary(package => package.Package + ".txt", package => ApiList(package.Package, Path.Combine(root, package.Assembly), package.Namespace));
    foreach (string file in Directory.Exists(listDirectory) ? Directory.GetFiles(listDirectory, "*.txt").Select(Path.GetFileName).OfType<string>() : [])
        if (!expected.ContainsKey(file)) { differs = true; Console.Error.WriteLine($"docs/reference/public-api/{file} names no package."); }
    foreach (var (file, text) in expected.OrderBy(pair => pair.Key, StringComparer.Ordinal))
    {
        string path = Path.Combine(listDirectory, file);
        string committed = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : "";
        if (committed == text) continue;
        differs = true;
        var have = committed.Split('\n').ToHashSet(StringComparer.Ordinal);
        var want = text.Split('\n').ToHashSet(StringComparer.Ordinal);
        Console.Error.WriteLine($"docs/reference/public-api/{file} does not match the build:");
        foreach (string line in want.Where(line => !have.Contains(line)).Take(40)) Console.Error.WriteLine("  +" + line.Trim());
        foreach (string line in have.Where(line => !want.Contains(line)).Take(40)) Console.Error.WriteLine("  -" + line.Trim());
        if (want.SetEquals(have)) Console.Error.WriteLine("  (same lines in another order)");
    }
    if (!classified)
        Console.Error.WriteLine("Each type named above needs [ResultShape] or internal (CONTRIBUTING.md, public API); rebuild and run this check again.");
    if (differs)
        Console.Error.WriteLine("The public API changed since the lists were written: prepare the release first " +
            "(`dotnet run scripts/api-docs.cs -- surface` after a build; CONTRIBUTING.md, release packages), review the diff and commit it.");
    if (!classified || differs) return 1;
    Console.WriteLine($"Public API lists match the builds ({packages.Length} packages).");
    return 0;
}

// #292: in a classified package, the baseline review asks one question per type: "result shape or operation". An operation
// declares a member that does something (or is an interface, a delegate or an exception). Any other type is data: a result
// shape, marked [ResultShape], when an operation gives it back (a return, an out parameter, a read-only property, or a
// property of data given back), else an input an operation takes (a parameter or settable property, or a constructor or
// setter of such an input). Refuses a labelled type that declares an operation, an unlabelled type an operation gives back,
// and a data type no operation takes or gives back. Prints each refusal; false if any.
bool Classify()
{
    bool classified = true;
    foreach (var package in packages.Where(package => package.Classified))
        foreach (string problem in Unclassified(Path.Combine(root, package.Assembly), package.Namespace))
        {
            classified = false;
            Console.Error.WriteLine($"{package.Package}: {problem}");
        }
    return classified;
}

string Git(params string[] arguments)
{
    var start = new ProcessStartInfo("git") { UseShellExecute = false, WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using Process git = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
    Task<string> output = git.StandardOutput.ReadToEndAsync(), errors = git.StandardError.ReadToEndAsync();
    git.WaitForExit();
    if (git.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {errors.GetAwaiter().GetResult().Trim()}");
    return output.GetAwaiter().GetResult().Trim();
}

// One package's public surface: each visible type (by full name), then its visible members, one per line and in ordinal
// order, from metadata alone (nothing is loaded or run). Protected members count: a consumer can derive from the type.
static string ApiList(string package, string assembly, string? ownNamespace)
{
    using var module = Mono.Cecil.ModuleDefinition.ReadModule(assembly);
    var text = new StringBuilder($"# {package}: public API. Generated by `dotnet run scripts/api-docs.cs -- surface`; never edit by hand.\n");
    foreach (var type in module.GetTypes().Where(type => Visible(type) && InNamespace(type, ownNamespace)).OrderBy(type => type.FullName, StringComparer.Ordinal))
    {
        text.Append(TypeLine(type)).Append('\n');
        var members = new List<string>();
        foreach (var field in type.Fields.Where(field => !field.IsSpecialName && (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly)))
            members.Add($"{Access(field.IsPublic)}field {(field.IsStatic && !field.HasConstant ? "static " : "")}{(field.HasConstant ? "const " : "")}{(field.IsInitOnly ? "readonly " : "")}{field.FieldType.FullName} {field.Name}" +
                (field.HasConstant ? " = " + Literal(field.Constant) : ""));
        foreach (var property in type.Properties)
        {
            var get = property.GetMethod is { } getter && VisibleMember(getter) ? getter : null;
            var set = property.SetMethod is { } setter && VisibleMember(setter) ? setter : null;
            if (get == null && set == null) continue;
            var any = get ?? set!;
            string parameters = property.HasParameters ? "[" + string.Join(", ", property.Parameters.Select(parameter => parameter.ParameterType.FullName)) + "]" : "";
            bool init = set != null && set.ReturnType is Mono.Cecil.RequiredModifierType { ModifierType.FullName: "System.Runtime.CompilerServices.IsExternalInit" };
            members.Add($"{Access(any.IsPublic)}property {Modifiers(any)}{property.PropertyType.FullName} {property.Name}{parameters} {{" +
                (get != null ? $" {Access(get.IsPublic)}get;" : "") + (set != null ? $" {Access(set.IsPublic)}{(init ? "init" : "set")};" : "") + " }");
        }
        foreach (var @event in type.Events.Where(@event => @event.AddMethod is { } add && VisibleMember(add)))
            members.Add($"{Access(@event.AddMethod.IsPublic)}event {Modifiers(@event.AddMethod)}{@event.EventType.FullName} {@event.Name}");
        foreach (var method in type.Methods.Where(method => VisibleMember(method) && !method.IsGetter && !method.IsSetter && !method.IsAddOn && !method.IsRemoveOn))
            members.Add($"{Access(method.IsPublic)}method {Modifiers(method)}{method.ReturnType.FullName} {method.Name}" +
                (method.HasGenericParameters ? "<" + string.Join(", ", method.GenericParameters.Select(parameter => parameter.Name)) + ">" : "") +
                "(" + string.Join(", ", method.Parameters.Select(Parameter)) + ")");
        foreach (string member in members.Order(StringComparer.Ordinal)) text.Append("  ").Append(member).Append('\n');
    }
    return text.ToString();

    static string Access(bool isPublic) => isPublic ? "" : "protected ";
    static string Modifiers(Mono.Cecil.MethodDefinition method) =>
        (method.IsStatic ? "static " : "") + (method.IsAbstract && !method.DeclaringType.IsInterface ? "abstract " : "") +
        (method.IsVirtual && !method.IsAbstract && !method.IsFinal ? "virtual " : "");
    static string Parameter(Mono.Cecil.ParameterDefinition parameter) =>
        (parameter.IsOut ? "out " : parameter.ParameterType.IsByReference && parameter.IsIn ? "in " : "") +
        (parameter.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "System.ParamArrayAttribute") ? "params " : "") +
        parameter.ParameterType.FullName + " " + parameter.Name + (parameter.HasDefault ? " = " + Literal(parameter.Constant) : "");
    static string Literal(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text + "\"",
        bool flag => flag ? "true" : "false",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
    };
    static string TypeLine(Mono.Cecil.TypeDefinition type)
    {
        string kind = type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct"
            : type.BaseType?.FullName == "System.MulticastDelegate" ? "delegate"
            : type.IsAbstract && type.IsSealed ? "static class" : type.IsAbstract ? "abstract class" : type.IsSealed ? "sealed class" : "class";
        var bases = new List<string>();
        if (type.BaseType != null && type.BaseType.FullName is not ("System.Object" or "System.ValueType" or "System.Enum" or "System.MulticastDelegate"))
            bases.Add(type.BaseType.FullName);
        bases.AddRange(type.Interfaces.Select(implementation => implementation.InterfaceType.FullName).Order(StringComparer.Ordinal));
        return $"{(ResultShape(type) ? "[result shape] " : "")}{(Visible(type) && type.IsNested && !type.IsNestedPublic ? "protected " : "")}{kind} {type.FullName}" +
            (bases.Count > 0 ? " : " + string.Join(", ", bases) : "");
    }
}

static bool ResultShape(Mono.Cecil.TypeDefinition type) => type.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "ResultShapeAttribute");

static IEnumerable<string> Unclassified(string assembly, string? ownNamespace)
{
    using var module = Mono.Cecil.ModuleDefinition.ReadModule(assembly);
    var types = module.GetTypes().Where(type => Visible(type) && InNamespace(type, ownNamespace)).ToDictionary(type => type.FullName, StringComparer.Ordinal);
    var operations = types.Values.Where(type => type.IsInterface || type.BaseType?.FullName == "System.MulticastDelegate" || Thrown(type) ||
        type.Methods.Any(method => VisibleMember(method) && Behaviour(method))).Select(type => type.FullName).ToHashSet(StringComparer.Ordinal);
    bool Operation(Mono.Cecil.TypeDefinition type) => operations.Contains(type.FullName);
    // What operations take (parameters, settable properties and fields, then an input's own constructor, setters and fields)
    // and what they give back (returns, out parameters, read-only properties and fields, event arguments, then every
    // property and field of a data type given back), transitively.
    var inputs = Reach(
        method => Behaviour(method) || method.IsConstructor || method.IsSetter ? method.Parameters.Where(parameter => !parameter.IsOut).Select(parameter => parameter.ParameterType) : [],
        field => !field.IsInitOnly && !field.HasConstant, method => method.IsConstructor || method.IsSetter);
    var outputs = Reach(
        method => Behaviour(method) || (method.IsGetter && ReadOnly(method)) || method.IsAddOn
            ? method.Parameters.Where(parameter => parameter.IsOut || method.IsAddOn).Select(parameter => parameter.ParameterType).Append(method.ReturnType) : [],
        field => true, method => method.IsGetter);
    // A type another public type derives from or implements cannot be internal, whatever its members.
    var bases = types.Values.SelectMany(type => type.Interfaces.Select(implementation => implementation.InterfaceType).Append(type.BaseType))
        .Where(type => type != null).SelectMany(type => Named(type!)).ToHashSet(StringComparer.Ordinal);
    foreach (var type in types.Values.OrderBy(type => type.FullName, StringComparer.Ordinal))
    {
        string name = type.FullName.Replace('/', '.');
        if (ResultShape(type) && Operation(type))
        {
            var members = type.Methods.Where(method => VisibleMember(method) && Behaviour(method)).Select(method => method.Name).Distinct().Order(StringComparer.Ordinal).ToList();
            string what = members.Count > 0 ? $"declares an operation ({string.Join(", ", members)})" : type.IsInterface ? "is an interface" : Thrown(type) ? "is an exception" : "is a delegate";
            yield return $"{name} is labelled [ResultShape] but {what}; a result shape only describes a result: move the operation out or drop the label.";
        }
        else if (ResultShape(type) || Operation(type)) continue;
        else if (outputs.Contains(type.FullName))
            yield return $"{name} is part of a result (an operation returns it, or a type it returns holds it) but is not labelled: " +
                "mark it [ResultShape], versioned with any JSON it is written to.";
        else if (!inputs.Contains(type.FullName) && !bases.Contains(type.FullName))
            yield return $"{name} is public but no operation takes or returns it, and it declares none: make it internal.";
    }

    // Seeds: each operation's visible methods and fields give `seed`'s types; a data type reached then gives the types of
    // its methods `follow` accepts and of the fields `field` accepts.
    HashSet<string> Reach(Func<Mono.Cecil.MethodDefinition, IEnumerable<Mono.Cecil.TypeReference>> seed, Func<Mono.Cecil.FieldDefinition, bool> field,
        Func<Mono.Cecil.MethodDefinition, bool> follow)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<Mono.Cecil.TypeDefinition>();
        void Add(IEnumerable<Mono.Cecil.TypeReference> found)
        {
            foreach (string name in found.SelectMany(Named))
                if (types.TryGetValue(name, out var type) && reached.Add(name) && !Operation(type)) pending.Enqueue(type);
        }
        IEnumerable<Mono.Cecil.TypeReference> Fields(Mono.Cecil.TypeDefinition type) =>
            type.Fields.Where(member => !member.IsSpecialName && (member.IsPublic || member.IsFamily || member.IsFamilyOrAssembly) && field(member)).Select(member => member.FieldType);
        foreach (var operation in types.Values.Where(Operation))
        {
            foreach (var method in operation.Methods.Where(VisibleMember)) Add(seed(method));
            Add(Fields(operation));
        }
        while (pending.TryDequeue(out var data))
        {
            foreach (var method in data.Methods.Where(method => VisibleMember(method) && follow(method)))
                Add(method.IsGetter ? [method.ReturnType] : method.Parameters.Where(parameter => !parameter.IsOut).Select(parameter => parameter.ParameterType));
            if (!data.IsEnum) Add(Fields(data));
        }
        return reached;
    }

    // A property an operation only reads out (no visible setter or init) gives a result; a settable one is an input.
    static bool ReadOnly(Mono.Cecil.MethodDefinition getter) =>
        getter.DeclaringType.Properties.FirstOrDefault(property => property.GetMethod == getter)?.SetMethod is not { } setter || !VisibleMember(setter);
    // Value semantics (equality, copying, deconstruction, operators, text) are part of a shape, not an operation.
    static bool Behaviour(Mono.Cecil.MethodDefinition method) =>
        !method.IsConstructor && !method.IsGetter && !method.IsSetter && !method.IsAddOn && !method.IsRemoveOn &&
        !method.Name.StartsWith("op_", StringComparison.Ordinal) &&
        method.Name is not ("Equals" or "GetHashCode" or "ToString" or "Deconstruct" or "<Clone>$" or "PrintMembers" or "CompareTo");
    // Derives from System.Exception. Only this module's types are resolved; a base outside it is judged by its name.
    static bool Thrown(Mono.Cecil.TypeDefinition type)
    {
        for (var baseType = type.BaseType; baseType != null; baseType = baseType.Scope == type.Module ? baseType.Resolve()?.BaseType : null)
            if (baseType.Scope != type.Module)
                return baseType.Namespace.StartsWith("System", StringComparison.Ordinal) && baseType.Name.EndsWith("Exception", StringComparison.Ordinal);
        return false;
    }
    // Every type a signature names: element types of arrays and references, and generic arguments.
    static IEnumerable<string> Named(Mono.Cecil.TypeReference type) => type switch
    {
        Mono.Cecil.GenericInstanceType generic => Named(generic.ElementType).Concat(generic.GenericArguments.SelectMany(Named)),
        Mono.Cecil.TypeSpecification specification => Named(specification.ElementType),
        _ => [type.FullName],
    };
}

static bool Visible(Mono.Cecil.TypeDefinition type) => type.IsNested
    ? (type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamilyOrAssembly) && Visible(type.DeclaringType)
    : type.IsPublic;
static bool VisibleMember(Mono.Cecil.MethodDefinition method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;
static bool InNamespace(Mono.Cecil.TypeDefinition type, string? name)
{
    if (name == null) return true;
    while (type.DeclaringType != null) type = type.DeclaringType;
    return type.Namespace == name || type.Namespace.StartsWith(name + ".", StringComparison.Ordinal);
}
