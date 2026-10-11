using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Valheim.Testing.Game;
using Xunit;

// #259 step 2a: every result.json names the toolkit that ran (each package released, candidate or unreleased, the ValheimCLI
// transport and its commit, the runner) and each actor's in-game plugin pins. No game.
public sealed class ToolkitProvenanceTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("toolkit-provenance-").FullName;
    public void Dispose() => Directory.Delete(_output, recursive: true);

    private static string Built(Assembly assembly) => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

    [Fact]
    public void SourceBuiltRunnerIsDistinctFromReusedCandidatePackage()
    {
        // The candidate set was built first. A later source edit changes the runner without rebuilding that set.
        // Its package coordinate must not masquerade as the code that the entry assembly actually executed.
        const string oldCommit = "230157f6", newCommit = "abcdef12";
        var context = new AssemblyLoadContext("provenance-test", isCollectible: true);
        var (candidate, candidateHash) = Compile("Valheim.Testing.Game", oldCommit, "old behavior", candidate: true, context);
        var (runner, runnerHash) = Compile("Valheim.Testing.NativeSmoke", newCommit, "new behavior", candidate: false, context);
        var result = ToolkitProvenance.Of([candidate], runner, assembly => assembly == candidate ? candidateHash : runnerHash);
        Assert.Equal(oldCommit, result.Packages.Single(package => package.Id == "Valheim.Testing.Game").Commit);
        Assert.Equal(oldCommit, result.LoadedAssemblies.Single(assembly => assembly.Name == "Valheim.Testing.Game").Commit);
        var actualRunner = result.LoadedAssemblies.Single(assembly => assembly.Name == "Valheim.Testing.NativeSmoke");
        Assert.Equal(newCommit, actualRunner.Commit);
        Assert.Equal(ToolkitProvenance.Unreleased, actualRunner.State);
        Assert.Equal(result.RunnerSha256, actualRunner.Sha256);
        Assert.NotEqual(result.Packages.Single(package => package.Id == "Valheim.Testing.Game").Sha256, actualRunner.Sha256);
        Assert.NotEqual(candidate.ManifestModule.ModuleVersionId, runner.ManifestModule.ModuleVersionId);
        using var written = JsonSerializer.SerializeToDocument(result);
        Assert.Equal(newCommit, written.RootElement.GetProperty("LoadedAssemblies").EnumerateArray()
            .Single(assembly => assembly.GetProperty("Name").GetString() == "Valheim.Testing.NativeSmoke")
            .GetProperty("Commit").GetString());
        Assert.Equal(oldCommit, written.RootElement.GetProperty("Packages").EnumerateArray()
            .Single(package => package.GetProperty("Id").GetString() == "Valheim.Testing.Game")
            .GetProperty("Commit").GetString());
        context.Unload();
    }

    private static (Assembly Assembly, string Sha256) Compile(string name, string commit, string behavior, bool candidate, AssemblyLoadContext context)
    {
        string version = candidate ? $"0.1.0-preview.1-candidate.{commit}+{commit}" : $"0.1.0-preview.1+{commit}";
        string source = $$"""
            using System.Reflection;
            [assembly: AssemblyInformationalVersion("{{version}}")]
            public static class TestCode { public static string Behavior() => "{{behavior}}"; }
            """;
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)],
            platform.Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        byte[] bytes = stream.ToArray();
        return (context.LoadFromStream(new MemoryStream(bytes)), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    [Fact]
    public void OnlyAReleaseBuildIsReleasedAndACandidateNeverIs()
    {
        Assert.Equal(ToolkitProvenance.Released, ToolkitProvenance.StateOf("0.1.0-preview.42", releaseBuild: true));
        Assert.Equal(ToolkitProvenance.Unreleased, ToolkitProvenance.StateOf("0.1.0-preview.42", releaseBuild: false));
        Assert.Equal(ToolkitProvenance.Candidate, ToolkitProvenance.StateOf("0.1.0-preview.42-candidate.0123456789ab", releaseBuild: true));
        Assert.Equal(ToolkitProvenance.Candidate, ToolkitProvenance.StateOf("0.1.0-preview.8-CANDIDATE.80fb6ce", releaseBuild: false));
    }

    [Fact]
    public void OnlyTheToolkitsOwnPackagesAreListedEachWithItsStateCommitAndHash()
    {
        var game = typeof(GameActor).Assembly;
        var cli = typeof(valheim_cli.Testing.ValheimClient).Assembly;
        var built = ToolkitProvenance.Of([game, cli, typeof(ToolkitProvenanceTests).Assembly, typeof(Assert).Assembly], typeof(ToolkitProvenanceTests).Assembly);
        Assert.Equal(["Valheim.Testing.Cli", "Valheim.Testing.Game"], built.Packages.Select(package => package.Id));
        var gamePackage = built.Packages.Single(package => package.Id == "Valheim.Testing.Game");
        // A test build is a source build: the release workflow's metadata is absent.
        Assert.Equal((Built(game), ToolkitProvenance.Unreleased, FileHash.Sha256(game.Location)), (gamePackage.Version, gamePackage.State, gamePackage.Sha256));
        string informational = game.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(informational.Contains('+') ? informational[(informational.IndexOf('+') + 1)..] : "", gamePackage.Commit);
        var cliPackage = built.Packages.Single(package => package.Id == "Valheim.Testing.Cli");
        Assert.Equal((Built(cli), FileHash.Sha256(cli.Location)), (cliPackage.Version, cliPackage.Sha256));
        Assert.Equal(Built(cli).Contains("-candidate", StringComparison.Ordinal) ? ToolkitProvenance.Candidate : ToolkitProvenance.Released, cliPackage.State);
        Assert.Equal(("Valheim.Testing.Tests", FileHash.Sha256(typeof(ToolkitProvenanceTests).Assembly.Location), null), (built.Runner, built.RunnerSha256, built.Error));
        Assert.Equal(("unknown", ""), (ToolkitProvenance.Of([game], null).Runner, ToolkitProvenance.Of([game], null).RunnerSha256));
    }

    // This process's own: built from this checkout, so the Game is the source version, and the embedded files are this checkout's.
    [Fact]
    public void EveryResultNamesTheToolkitAndEachActorsPluginPins()
    {
        var report = new ScenarioReport("provenance");
        report.RecordPlugins("server", new Dictionary<string, string> { ["worlduid"] = "4242", ["my.mod"] = new string('c', 32), ["valheimCLI.valheimCLI"] = new string('d', 32) });
        report.RecordPlugins("client-a", new Dictionary<string, string> { ["my.mod"] = "absent", ["valheimCLI.valheimCLI"] = new string('d', 32) });
        report.RecordPlugins("client-b", new Dictionary<string, string> { ["worlduid"] = "4242" }); // nothing pinned: no entry
        report.Step("a step", () => { });
        report.Write(_output);
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "result.json")));
        var root = result.RootElement;
        Assert.Equal(4, root.GetProperty("Schema").GetInt32());
        var toolkit = root.GetProperty("Toolkit");
        var packages = toolkit.GetProperty("Packages").EnumerateArray().ToDictionary(package => package.GetProperty("Id").GetString()!);
        // Game's own references are listed even before anything used them: Valheim.Testing and the transport.
        Assert.Superset(new HashSet<string> { "Valheim.Testing", "Valheim.Testing.Cli", "Valheim.Testing.Game" }, packages.Keys.ToHashSet());
        Assert.Equal(Built(typeof(GameActor).Assembly), packages["Valheim.Testing.Game"].GetProperty("Version").GetString());
        Assert.Equal(ToolkitProvenance.Unreleased, packages["Valheim.Testing.Game"].GetProperty("State").GetString());
        var loaded = toolkit.GetProperty("LoadedAssemblies").EnumerateArray()
            .ToDictionary(assembly => assembly.GetProperty("Name").GetString()!);
        Assert.Equal(packages["Valheim.Testing.Game"].GetProperty("Sha256").GetString(),
            loaded["Valheim.Testing.Game"].GetProperty("Sha256").GetString());
        Assert.Equal(typeof(GameActor).Assembly.ManifestModule.ModuleVersionId.ToString("D"),
            loaded["Valheim.Testing.Game"].GetProperty("Mvid").GetString());
        Assert.True(loaded.ContainsKey(toolkit.GetProperty("Runner").GetString()!));
        Assert.Equal(toolkit.GetProperty("RunnerSha256").GetString(),
            loaded[toolkit.GetProperty("Runner").GetString()!].GetProperty("Sha256").GetString());
        // The transport this checkout pins (cli-dependency.json's packageVersion) is the one that ran.
        using var pin = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureProjects.RepositoryRoot(), "cli-dependency.json")));
        Assert.Equal(pin.RootElement.GetProperty("packageVersion").GetString(), packages["Valheim.Testing.Cli"].GetProperty("Version").GetString());
        Assert.Equal(JsonValueKind.Null, toolkit.GetProperty("Error").ValueKind);
        var plugins = root.GetProperty("Plugins");
        Assert.Equal(["client-a", "server"], plugins.EnumerateObject().Select(actor => actor.Name));
        Assert.Equal(new string('c', 32), plugins.GetProperty("server").GetProperty("my.mod").GetString());
        Assert.False(plugins.GetProperty("server").TryGetProperty("worlduid", out _));
        Assert.Equal("absent", plugins.GetProperty("client-a").GetProperty("my.mod").GetString());
    }
}
