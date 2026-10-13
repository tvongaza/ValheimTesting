using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

// #735: a pull request runs the OS-aware split (scripts/validate.cs --split). A class marked [Trait("Os", "neutral")] runs on
// Linux only there, so it must behave the same on every OS. OsSplitGuard decides which classes may carry that mark; the split
// itself (which OS leaves out which value) is validate.cs's SplitFilter. Every push to main still runs every test on every OS.
public sealed class OsSplitTests
{
    [Fact]
    public void EveryOsTraitIsValidAndNoNeutralClassUsesAnOsSensitiveApi()
    {
        var sources = RepositorySources();
        var result = OsSplitGuard.Check(sources);
        Assert.True(result.Refusals.Count == 0, string.Join("\n", result.Refusals));
        // The guard saw the classes it should judge, by name (not merely some number of them).
        Assert.Equal("neutral", result.Classes["TerrainRendererTests"]);
        Assert.Equal("neutral", result.Classes["WalkingReviewTests"]);
        Assert.Equal("unix", result.Classes["HostedRetireScriptTests"]);
        Assert.Equal("macos", result.Classes["RemoteMacServerPreflightTests"]);
        Assert.False(result.Classes.ContainsKey(nameof(OsSplitTests)));
        // validate.cs --split filters the toolkit tests only (the xunit adapter matches nothing in a project whose tests carry no
        // Os trait), so a trait anywhere else would mark nothing.
        var elsewhere = OsSplitGuard.Check(sources.Where(source => !source.Key.StartsWith("tests/Valheim.Testing.Tests/", StringComparison.Ordinal))
            .ToDictionary(source => source.Key, source => source.Value, StringComparer.Ordinal));
        Assert.True(elsewhere.Classes.Count == 0, "Os traits outside the toolkit tests: " + string.Join(", ", elsewhere.Classes.Keys));
    }

    [Fact]
    public void ANeutralClassThatStartsAProcessIsRefused()
    {
        var refusals = Refusals(("NeutralTests.cs", """
            using System.Diagnostics;
            using Xunit;
            [Trait("Os", "neutral")]
            public sealed class StartsAProgramTests
            {
                [Fact] public void Runs() => Process.Start("true")!.WaitForExit();
            }
            """));
        Assert.Contains(refusals, refusal => refusal.Contains("NeutralTests.cs: StartsAProgramTests is marked neutral but uses Process (processes and host scripts)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("var path = System.IO.Path.Combine(\"a\", \"b\");", "uses Path (the file system)")]
    [InlineData("var link = new System.IO.FileInfo(\"a\").LinkTarget;", "uses FileInfo (the file system)")]
    [InlineData("bool windows = OperatingSystem.IsWindows();", "uses OperatingSystem (this machine)")]
    [InlineData("System.Threading.Thread.Sleep(10);", "uses Thread (the clock and threads)")]
    [InlineData("var when = DateTime.Now;", "uses Now (the clock and threads)")]
    [InlineData("double y = Math.Sin(0.5);", "uses Sin (platform math")]
    [InlineData("double y = double.Exp10(0.5);", "uses Exp10 (platform math")]
    [InlineData("string text = new System.Text.StringBuilder().AppendLine(\"a\").ToString();", "uses AppendLine (this machine's line ends)")]
    [InlineData("string script = \"/tmp/run.sh\";", "spells a path or a host script in \"/tmp/run.sh\"")]
    [InlineData("string drive = @\"C:\\vt\";", "spells a path or a host script in \"C:\\vt\"")]
    [InlineData("string text = @\"a\nb\";", "has a string literal over several lines")]
    public void EachKindOfOsSensitiveApiIsRefusedInANeutralClass(string statement, string refusal)
    {
        var refusals = Refusals(("Neutral.cs", $$"""
            using Xunit;
            [Trait("Os", "neutral")]
            public sealed class SomeTests
            {
                [Fact] public void Runs() { {{statement}} }
            }
            """));
        Assert.Contains(refusals, found => found.Contains(refusal, StringComparison.Ordinal));
    }

    // A helper in another file that touches the machine makes the neutral class that uses it OS-sensitive, through any chain of helpers.
    [Fact]
    public void ANeutralClassUsingAHelperThatTouchesTheMachineIsRefused()
    {
        var refusals = Refusals(
            ("Scratch.cs", "public static class Scratch { public static string Folder() => System.IO.Directory.CreateTempSubdirectory().FullName; }"),
            ("World.cs", "public sealed class SavedWorld { public string Where = Scratch.Folder(); }"),
            ("Neutral.cs", """
                using Xunit;
                [Trait("Os", "neutral")]
                public sealed class SavedWorldTests { [Fact] public void Runs() => Assert.NotNull(new SavedWorld().Where); }
                """));
        Assert.Contains(refusals, refusal => refusal.Contains("SavedWorldTests is marked neutral but uses SavedWorld (a type that touches the machine, World.cs)", StringComparison.Ordinal));
    }

    // A base class or other top-level type of a test file in the same project counts too.
    [Fact]
    public void ANeutralClassDerivedFromATestClassThatTouchesTheMachineIsRefused()
    {
        var refusals = Refusals(
            ("tests/Some.Tests/ScratchTests.cs", """
                using Xunit;
                public abstract class ScratchTests { protected string Folder = System.IO.Directory.CreateTempSubdirectory().FullName; [Fact] public void Made() => Assert.NotNull(Folder); }
                """),
            ("tests/Some.Tests/DerivedTests.cs", """
                using Xunit;
                [Trait("Os", "neutral")]
                public sealed class DerivedTests : ScratchTests { [Fact] public void Runs() { } }
                """));
        Assert.Contains(refusals, refusal => refusal.Contains("DerivedTests is marked neutral but uses ScratchTests (a type that touches the machine, tests/Some.Tests/ScratchTests.cs)", StringComparison.Ordinal));
    }

    [Fact]
    public void APureNeutralClassAndOsSpecificClassesPass()
    {
        var result = OsSplitGuard.Check(Sources(
            ("Pure.cs", """
                using Xunit;
                [Trait("Os", "neutral")]
                public sealed class SumTests { [Theory] [InlineData(1, 2, 3)] public void Adds(int a, int b, int sum) => Assert.Equal(sum, a + b); }
                """),
            // An OS-specific class may touch the machine: it runs on its OS.
            ("Unix.cs", """
                using Xunit;
                [Trait("Os", "unix")]
                public sealed class ShellTests { [Fact] public void Runs() => System.Diagnostics.Process.Start("bash", "-c true")!.WaitForExit(); }
                """)));
        Assert.Empty(result.Refusals);
        Assert.Equal("neutral", result.Classes["SumTests"]);
        Assert.Equal("unix", result.Classes["ShellTests"]);
    }

    [Theory]
    [InlineData("[Trait(\"Os\", \"linux\")] public class T { }", "T: the Os trait is one of neutral, windows, unix, macos, not \"linux\"")]
    [InlineData("[Trait(\"Os\", \"unix\")] [Trait(\"Os\", \"macos\")] public class T { }", "T: a class carries one Os trait")]
    [InlineData("public class T { [Trait(\"Os\", \"neutral\")] [Xunit.Fact] public void M() { } }", "the Os trait goes on a test class")]
    [InlineData("[Trait(\"Os\", \"neutral\")] public partial class T { }", "T: a neutral class is not partial")]
    [InlineData("[Trait(\"Os\", Values.Neutral)] public sealed class T { }", "the Os trait takes a literal value")]
    [InlineData("[Trait(Keys.Os, \"neutral\")] public sealed class T { }", "a trait's name is a literal, so the Os trait can be read")]
    [InlineData("[Trait(\"Os\", \"neutral\")] public abstract class T { }", "T: a class with an Os trait is sealed")]
    [InlineData("[Trait(\"Os\", \"unix\")] public class T { }", "T: a class with an Os trait is sealed")]
    [InlineData("[global::Xunit.TraitAttribute(\"Os\", \"neutral\")] public sealed class T { void M() => System.IO.File.Delete(\"x\"); }", "T is marked neutral but uses File")]
    [InlineData("[OsTrait(\"Os\", \"neutral\")] public sealed class T { void M() => System.IO.File.Delete(\"x\"); }", "T is marked neutral but uses File")]
    [InlineData("[Trait(\"Os\", \"neutral\")] public sealed class T {\n#if NEVER\n void M() => System.IO.File.Delete(\"x\");\n#endif\n}", "T: a neutral class's file has no #if")]
    public void AMisplacedOrUnknownOsTraitIsRefused(string source, string refusal)
    {
        var refusals = Refusals(("T.cs", "using Xunit;\nusing OsTrait = Xunit.TraitAttribute;\n" + source));
        Assert.Contains(refusals, found => found.Contains(refusal, StringComparison.Ordinal));
    }

    // Text that only looks like the trait (in a string or a comment) marks nothing.
    [Fact]
    public void ATraitInsideAStringOrCommentMarksNothing()
    {
        var result = OsSplitGuard.Check(Sources(("Probe.cs", """
            // [Trait("Os", "neutral")]
            public class ProbeTests { string source = "[Trait(\"Os\", \"neutral\")] public class X { void M() => System.Diagnostics.Process.Start(\"x\"); }"; }
            """)));
        Assert.Empty(result.Refusals);
        Assert.Empty(result.Classes);
    }

    private static IReadOnlyList<string> Refusals(params (string Path, string Text)[] files) => OsSplitGuard.Check(Sources(files)).Refusals;

    private static Dictionary<string, string> Sources(params (string Path, string Text)[] files) =>
        files.ToDictionary(file => file.Path, file => file.Text, StringComparer.Ordinal);

    // Every C# file of the test projects and examples, by its path from the repository root; build output is not walked.
    private static Dictionary<string, string> RepositorySources()
    {
        string root = FixtureProjects.RepositoryRoot();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>([Path.Combine(root, "tests"), Path.Combine(root, "examples")]);
        while (pending.Count != 0)
        {
            string directory = pending.Pop();
            foreach (string child in Directory.EnumerateDirectories(directory))
                if (Path.GetFileName(child) is not ("bin" or "obj" or "TestResults") && !Path.GetFileName(child).StartsWith('.')) pending.Push(child);
            foreach (string file in Directory.EnumerateFiles(directory, "*.cs"))
                sources[Path.GetRelativePath(root, file).Replace('\\', '/')] = File.ReadAllText(file);
        }
        // The sources the toolkit test project compiles in from elsewhere (tools, scripts, the adapter) are its helpers too.
        string project = Path.Combine(root, "tests", "Valheim.Testing.Tests");
        foreach (var compile in System.Xml.Linq.XDocument.Load(Path.Combine(project, "Valheim.Testing.Tests.csproj")).Descendants("Compile"))
            foreach (string include in ((string?)compile.Attribute("Include") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string file = Path.GetFullPath(Path.Combine(project, include));
                sources[Path.GetRelativePath(root, file).Replace('\\', '/')] = File.ReadAllText(file);
            }
        // So are the projects it references outside the packages under test (src/), such as tools/regression-bundle.
        foreach (var reference in System.Xml.Linq.XDocument.Load(Path.Combine(project, "Valheim.Testing.Tests.csproj")).Descendants("ProjectReference"))
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(project, (string?)reference.Attribute("Include") ?? "")))!;
            if (Path.GetRelativePath(root, directory).Replace('\\', '/').StartsWith("src/", StringComparison.Ordinal)) continue;
            foreach (string file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(file => !Path.GetRelativePath(directory, file).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
                sources[Path.GetRelativePath(root, file).Replace('\\', '/')] = File.ReadAllText(file);
        }
        Assert.Contains("tests/Valheim.Testing.Tests/TerrainRendererTests.cs", sources.Keys);
        Assert.Contains("tools/dev-loop/dev-loop.cs", sources.Keys);
        Assert.Contains("tools/regression-bundle/RegressionBundle.cs", sources.Keys);
        return sources;
    }
}

/// <summary>
/// The one owner of which test classes may be OS-neutral (#735). A class marked <c>[Trait("Os", "neutral")]</c> may use none of the
/// APIs below anywhere in its file, nor a type that does: one declared in a helper file (a file without tests), or a top-level type
/// of another test file of the same project, through any chain of such types. It reads syntax only: code the class calls in the
/// packages under test (and an extension method, which never names its class) is the reviewer's to judge, and every push to main
/// runs the class on every OS. It fails closed: a name that only looks like a sensitive helper refuses too.
/// </summary>
internal static class OsSplitGuard
{
    /// <summary>The Os trait's values; scripts/validate.cs's SplitFilter decides which OS runs each.</summary>
    public static readonly string[] Values = ["neutral", "windows", "unix", "macos"];

    // The one list of OS-sensitive APIs: an identifier containing any of these words (so Process also refuses ProcessStartInfo and
    // HostShell is refused by Shell). Deliberately broad: a neutral class touches nothing of the machine it runs on.
    private static readonly (string Kind, string[] Words)[] Sensitive =
    [
        ("processes and host scripts", ["Process", "Kill", "Signal", "Shell", "Ssh", "Bash", "Pwsh", "Docker", "Container"]),
        ("the file system", ["File", "Directory", "Path", "Link", "Junction", "Drive", "LastWriteTime", "LastAccessTime", "CreationTime", "Zip"]),
        ("this machine", ["OperatingSystem", "RuntimeInformation", "Environment", "ThisMachine", "CurrentPlatform", "SpecialFolder", "Win32"]),
        ("the network", ["Socket", "Tcp", "Udp", "IPAddress", "IPEndPoint", "Http", "Dns", "Loopback"]),
        ("the clock and threads", ["Thread", "Sleep", "Delay", "Stopwatch", "Timer", "TimeZone", "CancelAfter", "WaitAsync", "Parallel", "Semaphore", "Mutex"]),
        ("native code and loading", ["DllImport", "LibraryImport", "Marshal", "NativeLibrary", "AssemblyLoadContext", "LoadFrom", "LoadFile", "Harmony", "MonoMod", "Unsafe"]),
    ];

    // Identifiers refused only whole: DateTime.Now and the like.
    private static readonly (string Kind, string[] Words)[] SensitiveWhole =
    [
        ("the clock and threads", ["Now", "Today", "UtcNow"]),
        ("this machine's line ends", ["AppendLine", "WriteLine", "StringWriter", "NewLine"]),
    ];

    // Platform math, refused whole: the C library's result may differ in the last bit between OSes.
    private static readonly System.Text.RegularExpressions.Regex PlatformMath = new(
        @"^(A?(Sin|Cos|Tan)h?(Pi)?|Atan2(Pi)?|SinCos(Pi)?|Exp(2|10)?(M1)?|Log(2|10)?(P1)?|Pow|Cbrt|Hypot|RootN|Reciprocal(Sqrt)?Estimate)$");

    // A string that spells a path or names a host script: an absolute or drive-letter path, a separator of Windows, a temp folder
    // or home, a shell.
    private static readonly string[] Spellings = ["/var/", "/private/", "/tmp", "~/", "\\", ".sh", ".ps1", ".exe", ".app", "bash", "pwsh", "ssh "];

    // The toolkit test project's symbols in CI (Release, net10.0, its DefineConstants), so a helper is read as it is compiled.
    private static readonly CSharpParseOptions Options = new(LanguageVersion.Preview,
        preprocessorSymbols: ["DEV_LOOP_TESTS", "VERSIONED_API_DOCS_TESTS", "RELEASE", "TRACE", "NET", "NETCOREAPP", "NET10_0", "NET10_0_OR_GREATER"]);

    public sealed record Result(IReadOnlyList<string> Refusals, IReadOnlyDictionary<string, string> Classes);

    // One parsed file: its own uses of the list, the names it mentions and the types it lends to others.
    private sealed record Source(string Path, SyntaxNode Root, string Project, bool HasTests, List<string> Direct, HashSet<string> Names, HashSet<string> Declared, HashSet<string> Lent);

    // A type that touches the machine: where it is declared, and the project it is visible to (null: a helper file, visible to all).
    private sealed record SensitiveType(string File, string? Project);

    public static Result Check(IReadOnlyDictionary<string, string> files)
    {
        var sources = files.OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => Parse(file.Key, file.Value)).ToList();

        // The types that touch the machine, directly or through another such type, found in one pass per newly sensitive file.
        var sensitiveTypes = new Dictionary<string, List<SensitiveType>>(StringComparer.Ordinal);
        var sensitive = new HashSet<Source>(ReferenceEqualityComparer.Instance);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var source in sources.Where(source => !sensitive.Contains(source)))
            {
                if (source.Direct.Count == 0 && Helpers(source, sensitiveTypes).Count == 0) continue;
                sensitive.Add(source);
                foreach (string type in source.Lent)
                {
                    if (!sensitiveTypes.TryGetValue(type, out var declarations)) sensitiveTypes[type] = declarations = [];
                    declarations.Add(new SensitiveType(source.Path, source.HasTests ? source.Project : null));
                    grew = true;
                }
            }
        }

        var refusals = new List<string>();
        var classes = new Dictionary<string, string>(StringComparer.Ordinal);
        var marked = new HashSet<ClassDeclarationSyntax>();
        foreach (var source in sources)
            foreach (var attribute in source.Root.DescendantNodes().OfType<AttributeSyntax>().Where(attribute => IsTrait(source.Root, attribute)))
            {
                string path = source.Path;
                if (attribute.ArgumentList!.Arguments[0].Expression is not LiteralExpressionSyntax { Token.Value: string trait })
                {
                    refusals.Add($"{path}:{Line(attribute)}: a trait's name is a literal, so the Os trait can be read");
                    continue;
                }
                if (trait != "Os") continue;
                if (attribute.ArgumentList!.Arguments[1].Expression is not LiteralExpressionSyntax { Token.Value: string value })
                {
                    refusals.Add($"{path}:{Line(attribute)}: the Os trait takes a literal value ({string.Join(", ", Values)})");
                    continue;
                }
                if (attribute.Parent?.Parent is not ClassDeclarationSyntax type)
                {
                    refusals.Add($"{path}:{Line(attribute)}: the Os trait goes on a test class, not a method or another member");
                    continue;
                }
                string name = type.Identifier.ValueText;
                if (!Values.Contains(value, StringComparer.Ordinal))
                    refusals.Add($"{path}: {name}: the Os trait is one of {string.Join(", ", Values)}, not \"{value}\"");
                if (!marked.Add(type))
                {
                    refusals.Add($"{path}: {name}: a class carries one Os trait");
                    continue;
                }
                if (!classes.TryAdd(name, value)) refusals.Add($"{path}: {name}: another class marked with an Os trait has this name");
                // xunit gives a derived class its base's traits; a class without the mark must run on every OS.
                if (!type.Modifiers.Any(SyntaxKind.SealedKeyword) && !type.Modifiers.Any(SyntaxKind.StaticKeyword))
                    refusals.Add($"{path}: {name}: a class with an Os trait is sealed (a class derived from it would inherit the trait unchecked)");
                if (value != "neutral") continue;
                if (type.Modifiers.Any(SyntaxKind.PartialKeyword))
                    refusals.Add($"{path}: {name}: a neutral class is not partial (the guard reads one file)");
                if (source.Root.DescendantTrivia().Any(trivia => trivia.IsKind(SyntaxKind.IfDirectiveTrivia)))
                    refusals.Add($"{path}: {name}: a neutral class's file has no #if (the guard reads one branch)");
                refusals.AddRange(source.Direct.Concat(Helpers(source, sensitiveTypes)).Select(use => $"{path}: {name} is marked neutral but {use}"));
            }
        return new Result(refusals, classes);
    }

    private static Source Parse(string path, string text)
    {
        var root = CSharpSyntaxTree.ParseText(text, Options).GetRoot();
        bool hasTests = root.DescendantNodes().OfType<AttributeSyntax>()
            .Any(attribute => attribute.Name.ToString() is "Fact" or "Theory" or "Xunit.Fact" or "Xunit.Theory" or "FactAttribute" or "TheoryAttribute");
        var types = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().ToList();
        var declared = types.Select(type => type.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        // A helper file lends every type it declares; a test file its top-level types (a nested one is reached through its outer type).
        var lent = types.Where(type => !hasTests || type.Parent is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .Select(type => type.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        // Names used in code; a plain using directive names namespaces, not types (a using static or an alias does name a type).
        var names = root.DescendantTokens().Where(token => token.IsKind(SyntaxKind.IdentifierToken) &&
                !token.Parent!.AncestorsAndSelf().OfType<UsingDirectiveSyntax>().Any(directive => directive.StaticKeyword.IsKind(SyntaxKind.None) && directive.Alias == null))
            .Select(token => token.ValueText).ToHashSet(StringComparer.Ordinal);
        string[] parts = path.Split('/');
        return new Source(path, root, parts.Length > 2 ? parts[0] + "/" + parts[1] : "", hasTests, Direct(root), names, declared, lent);
    }

    // What in a file touches the machine by itself: an OS-sensitive identifier, a spelled path or script, a literal over several lines.
    private static List<string> Direct(SyntaxNode root)
    {
        var uses = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var token in root.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.UnsafeKeyword)) uses.Add("uses unsafe (native code and loading)");
            else if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                string identifier = token.ValueText;
                foreach (var (kind, words) in Sensitive)
                    if (words.Any(word => identifier.Contains(word, StringComparison.Ordinal))) uses.Add($"uses {identifier} ({kind})");
                foreach (var (kind, words) in SensitiveWhole)
                    if (words.Contains(identifier, StringComparer.Ordinal)) uses.Add($"uses {identifier} ({kind})");
                if (PlatformMath.IsMatch(identifier)) uses.Add($"uses {identifier} (platform math: the C library's result may differ in the last bit)");
            }
            else if (token.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.SingleLineRawStringLiteralToken
                or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.Utf8StringLiteralToken or SyntaxKind.Utf8SingleLineRawStringLiteralToken
                or SyntaxKind.Utf8MultiLineRawStringLiteralToken)
            {
                string text = token.ValueText;
                if (text.StartsWith('/') || (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':') ||
                    Spellings.Any(spelling => text.Contains(spelling, StringComparison.Ordinal)))
                    uses.Add($"spells a path or a host script in \"{(text.Length > 60 ? text[..60] + "..." : text)}\"");
                // A checkout on Windows may turn the line ends inside it into CRLF.
                if (token.Text.Contains('\n'))
                    uses.Add($"has a string literal over several lines at line {token.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (its line ends follow the checkout)");
            }
        }
        return uses.ToList();
    }

    // The types that touch the machine this file uses, other than its own.
    private static List<string> Helpers(Source source, IReadOnlyDictionary<string, List<SensitiveType>> sensitiveTypes) =>
        source.Names.Where(name => !source.Declared.Contains(name) && sensitiveTypes.ContainsKey(name)).Order(StringComparer.Ordinal)
            .SelectMany(name => sensitiveTypes[name].Where(type => type.Project == null || type.Project == source.Project)
                .Select(type => $"uses {name} (a type that touches the machine, {type.File})")).ToList();

    // xunit's trait attribute in any spelling (global::, Xunit., the Attribute suffix) or under an alias the file declares.
    private static bool IsTrait(SyntaxNode root, AttributeSyntax attribute)
    {
        if (attribute.ArgumentList is not { Arguments.Count: 2 }) return false;
        string name = attribute.Name.ToString();
        return Unqualified(name) == "Trait" || root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Any(directive => directive.Alias?.Name.Identifier.ValueText == name && Unqualified(directive.NamespaceOrType.ToString()) == "Trait");
        static string Unqualified(string name)
        {
            name = name.Replace(" ", "", StringComparison.Ordinal);
            if (name.StartsWith("global::", StringComparison.Ordinal)) name = name["global::".Length..];
            if (name.StartsWith("Xunit.", StringComparison.Ordinal)) name = name["Xunit.".Length..];
            return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
        }
    }

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
