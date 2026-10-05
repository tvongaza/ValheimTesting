// Build the preview public-surface reference after bootstrap-cli.cs and validate.cs.
// No game process or game assemblies are needed.
//
//   dotnet tool restore
//   dotnet run scripts/bootstrap-cli.cs
//   dotnet run scripts/validate.cs
//   dotnet run scripts/api-docs.cs                       the DocFX reference (docs/reference/_site, not committed)
//   dotnet run scripts/api-docs.cs -- surface            write docs/reference/public-api/<Package>.txt from the release builds
//   dotnet run scripts/api-docs.cs -- check [--base REV] CI: the committed lists match the builds; with --base, a change to
//                                                        them since REV needs a line starting "public-api:" in PR_BODY
//
// The public-api lists are the reviewed public surface (#292, #135): every public or protected type and member of each
// package, one per line. A pull request that changes one says why in its description, on a line starting "public-api:".
#:package Mono.Cecil@0.11.6
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

string root = FindRoot();
// Each package's release build. Every public type in it is the package's surface (Doubles' game stand-ins included), except
// in the Adapter's compile check, which also holds the stubs it compiles against: only its own namespace counts there.
(string Package, string Assembly, string? Namespace)[] packages =
[
    ("Valheim.Testing", "src/Valheim.Testing/bin/Release/netstandard2.0/Valheim.Testing.dll", null),
    ("Valheim.Testing.Doubles", "src/Valheim.Testing.Doubles/bin/Release/netstandard2.0/Valheim.Testing.Doubles.dll", null),
    ("Valheim.Testing.Game", "src/Valheim.Testing.Game/bin/Release/net10.0/Valheim.Testing.Game.dll", null),
    ("Valheim.Testing.Bindings", "src/Valheim.Testing.Bindings/bin/Release/netstandard2.0/Valheim.Testing.Bindings.dll", null),
    ("Valheim.Testing.Bindings.Tool", "src/Valheim.Testing.Bindings.Tool/bin/Release/net10.0/Valheim.Testing.Bindings.Tool.dll", null),
    ("Valheim.Testing.Adapter", "tests/Valheim.Testing.Adapter.CompileCheck/bin/Release/net48/Valheim.Testing.Adapter.CompileCheck.dll", "Valheim.Testing.Adapter"),
];
foreach (var package in packages)
    if (!File.Exists(Path.Combine(root, package.Assembly)))
        throw new FileNotFoundException("Run validate.cs first; a release build is missing.", package.Assembly);
string listDirectory = Path.Combine(root, "docs", "reference", "public-api");
if (args is ["surface"])
{
    Directory.CreateDirectory(listDirectory);
    foreach (var package in packages)
        File.WriteAllText(Path.Combine(listDirectory, package.Package + ".txt"), ApiList(package.Package, Path.Combine(root, package.Assembly), package.Namespace));
    Console.WriteLine($"Public API lists written: {Path.GetRelativePath(root, listDirectory)}");
    return 0;
}
if (args.Length > 0 && args[0] == "check")
{
    string? baseRevision = args is ["check", "--base", var revision] ? revision : args is ["check"] ? null
        : throw new ArgumentException("usage: dotnet run scripts/api-docs.cs -- check [--base REV]");
    return CheckLists(baseRevision);
}
if (args.Length != 0) throw new ArgumentException("usage: dotnet run scripts/api-docs.cs [-- surface | -- check [--base REV]]");

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
    "index.html",
    "api/Valheim.Testing.ITerrain.html",
    "api/Valheim.Testing.Doubles.TerrainWorld.html",
    "api/Valheim.Testing.Game.html",
    "api/Valheim.Testing.Adapter.html",
    "api/Valheim.Testing.Bindings.html"
})
    if (!File.Exists(Path.Combine(site, page)))
        throw new InvalidOperationException($"DocFX omitted a package's reference page: {page}");

// These selected examples explain the order of operations. A successful metadata build alone does not prove that
// DocFX rendered an XML <example> on the page a mod author will read.
foreach (var (page, excerpt) in new (string Page, string Excerpt)[]
{
    ("api/Valheim.Testing.CompositeTerrain.html", "float terraceHeight = terrain.GetHeight"),
    ("api/Valheim.Testing.Game.WorldFixture.html", "WorldFixture.Verify(fixtureSource, reviewedHashes)"),
    ("api/Valheim.Testing.Game.DisposableCharacterStore.html", "store.Register(\"tester\", localCharacterFile)"),
    ("api/Valheim.Testing.Game.GameActor.html", "actor.RequireCapability(\"mymod.testing/session\")"),
    ("api/Valheim.Testing.Game.PinnedServerRun.html", "PinnedServerRun.MainAsync(args"),
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

Console.WriteLine("Preview API reference ready: docs/reference/_site/index.html");
return 0;

// The committed lists against the builds; then, given the base revision a pull request starts from, its description.
int CheckLists(string? baseRevision)
{
    bool differs = false;
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
    if (differs)
    {
        Console.Error.WriteLine("The public API changed: run `dotnet run scripts/api-docs.cs -- surface`, review the diff, commit it, and add a line " +
            "starting \"public-api:\" to the pull request description saying why.");
        return 1;
    }
    Console.WriteLine($"Public API lists match the builds ({packages.Length} packages).");
    if (baseRevision == null) return 0;
    string changed = Git("diff", "--name-only", baseRevision, "--", "docs/reference/public-api");
    if (changed.Length == 0) { Console.WriteLine($"No public API change since {baseRevision}."); return 0; }
    string body = Environment.GetEnvironmentVariable("PR_BODY") ?? "";
    if (Regex.IsMatch(body, @"(?m)^\s*public-api:\s*\S"))
    {
        Console.WriteLine($"The public API changed since {baseRevision} ({changed.Replace('\n', ' ')}); the description says why.");
        return 0;
    }
    Console.Error.WriteLine($"This change alters the public API ({changed.Replace('\n', ' ')}) and its pull request description has no line " +
        "starting \"public-api:\". Add one that says why each type or member is public (or leaves), then re-run this check.");
    return 1;
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

    static bool Visible(Mono.Cecil.TypeDefinition type) => type.IsNested
        ? (type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamilyOrAssembly) && Visible(type.DeclaringType)
        : type.IsPublic;
    static bool InNamespace(Mono.Cecil.TypeDefinition type, string? name)
    {
        if (name == null) return true;
        while (type.DeclaringType != null) type = type.DeclaringType;
        return type.Namespace == name || type.Namespace.StartsWith(name + ".", StringComparison.Ordinal);
    }
    static bool VisibleMember(Mono.Cecil.MethodDefinition method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;
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
        return $"{(Visible(type) && type.IsNested && !type.IsNestedPublic ? "protected " : "")}{kind} {type.FullName}" +
            (bases.Count > 0 ? " : " + string.Join(", ", bases) : "");
    }
}

static string SourcePath([CallerFilePath] string path = "") => path;

static string FindRoot()
{
    foreach (string start in new[] { Environment.CurrentDirectory, Path.GetDirectoryName(SourcePath()) ?? "" })
        for (DirectoryInfo? directory = new DirectoryInfo(start); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "cli-dependency.json"))) return directory.FullName;
    throw new InvalidOperationException("Run from inside the ValheimTesting repository.");
}
