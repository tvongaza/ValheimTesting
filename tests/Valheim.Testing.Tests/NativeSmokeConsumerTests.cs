using Valheim.Testing.Game;
using Xunit;

public sealed class NativeSmokeConsumerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("native-smoke-consumer-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ASelectedCliBundleYieldsItsSingleManifestWithoutASeparateManifestFlag()
    {
        string bundle = Path.Combine(_root, "cli");
        Directory.CreateDirectory(bundle);
        string manifest = Path.Combine(bundle, "cli-manifest.json");
        File.WriteAllText(manifest, Manifest());
        var resolved = SmokeInputs.Cli(new Dictionary<string, string> { ["--cli-files"] = bundle });
        Assert.Equal(manifest, resolved.Manifest);
        Assert.Equal(bundle, resolved.Files);
    }

    [Fact]
    public void AmbiguousCliBuildsAreRefusedBeforeAClientCanStart()
    {
        string bundle = Path.Combine(_root, "cli");
        Directory.CreateDirectory(Path.Combine(bundle, "older"));
        Directory.CreateDirectory(Path.Combine(bundle, "newer"));
        File.WriteAllText(Path.Combine(bundle, "older", "cli-manifest.json"), Manifest());
        File.WriteAllText(Path.Combine(bundle, "newer", "cli-manifest.json"), Manifest());
        var error = Assert.Throws<InvalidDataException>(() =>
            SmokeInputs.Cli(new Dictionary<string, string> { ["--cli-files"] = bundle }));
        Assert.Contains("Several ValheimCLI capability manifests", error.Message);
        Assert.Contains("never mix packs", error.Message);
    }

    [Fact]
    public void MissingCliManifestNamesTheRequiredChoice()
    {
        string bundle = Path.Combine(_root, "cli");
        Directory.CreateDirectory(bundle);
        var error = Assert.Throws<InvalidDataException>(() =>
            SmokeInputs.Cli(new Dictionary<string, string> { ["--cli-files"] = bundle }));
        Assert.Contains("No ValheimCLI capability manifest", error.Message);
        Assert.Contains("VALHEIMCLI_BUNDLE", error.Message);
    }

    [Fact]
    public void ExplicitSteamRootRemainsTheOnlyAccountRootChosen()
    {
        string expected = Path.Combine(_root, "steam", "userdata");
        Assert.Equal(expected, SmokeInputs.SteamUserdata(new Dictionary<string, string> { ["--steam-userdata"] = expected }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditableConsumerUsesOnlyThePublishedGamePackage(bool server)
    {
        SmokeProject.Write(_root, server);
        string project = File.ReadAllText(Path.Combine(_root, "SmokeCheck.csproj"));
        Assert.Contains("Valheim.Testing.Game", project);
        Assert.Contains(SmokeProject.GameVersion, project);
        Assert.DoesNotContain("ProjectReference", project);
        string config = File.ReadAllText(Path.Combine(_root, "NuGet.Config"));
        Assert.Contains("<clear/>", config);
        Assert.Contains("https://api.nuget.org/v3/index.json", config);
        string source = File.ReadAllText(Path.Combine(_root, "Program.cs"));
        Assert.Contains(server ? "PinnedServerRun.MainAsync" : "TargetedRegression.Read", source);
        if (server) Assert.Contains("PinnedServerRun.RunCampaignAsync", source); // a server-load run off a Mac is a campaign
    }

    // The tests exercise the tool's own build through InternalsVisibleTo, not a second compile of its sources into this
    // assembly; nothing is public only for the tests.
    [Fact]
    public void TestsReachTheToolsOwnBuildAndItExposesNoPublicType()
    {
        System.Reflection.Assembly tool = typeof(SmokeProject).Assembly;
        Assert.Equal("Valheim.Testing.NativeSmoke", tool.GetName().Name);
        // Compiler-generated helpers ("<PrivateImplementationDetails>", inline arrays) exist in both and are not sources.
        static IEnumerable<string> Declared(System.Reflection.Assembly assembly) =>
            assembly.GetTypes().Select(type => type.FullName!).Where(name => !name.StartsWith('<'));
        Assert.Empty(Declared(typeof(NativeSmokeConsumerTests).Assembly).Intersect(Declared(tool)));
        Assert.Empty(tool.GetExportedTypes());
    }

    [Fact]
    public void ToolCarriesTheServerAdapterSourceNeededOutsideThisCheckout()
    {
        string[] names = typeof(SmokeProject).Assembly.GetManifestResourceNames();
        foreach (string file in new[] { "Plugin.cs", "TestExtension.cs", "Members.cs",
                     "Valheim.GameReferences.props", "Valheim.GameReferences.targets" })
            Assert.Contains("NativeSmoke.Adapter." + file, names);
    }

    // One source (#297): the consumer pins the Valheim.Testing.Game this tool was built with and runs, the version of
    // the Game project it references, so the files the tool writes and the consumer's reader are one version. The tool no
    // longer carries toolkit-versions.json, the released version that differed from what it ran.
    [Fact]
    public void EditableConsumerPinsTheGameThisToolRuns()
    {
        string project = File.ReadAllText(Path.Combine(FixtureProjects.RepositoryRoot(), "src", "Valheim.Testing.Game", "Valheim.Testing.Game.csproj"));
        string built = System.Text.RegularExpressions.Regex.Match(project, "<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.NotEmpty(built);
        Assert.Equal(built, SmokeProject.GameVersion);
        Assert.Equal("Valheim.Testing.Game " + built, TargetedRegression.ToolkitVersion); // the run's provenance names the same version
        Assert.Null(typeof(SmokeProject).Assembly.GetManifestResourceStream("toolkit-versions.json"));
    }

    [Theory]
    [InlineData("0.1.0-preview.41", false)]
    [InlineData("0.1.0-preview.42-candidate.1a2b3c4", true)]
    [InlineData("unknown", true)]
    public void InitRefusesOnlyAGameVersionNuGetOrgNeverServes(string version, bool refused)
    {
        Assert.Equal(refused, SmokeProject.Unpublishable(version) != null);
    }

    // start and server-load make no network call: neither the tool nor the toolkit assemblies it runs in process has an
    // HTTP client (the NuGet.org check before every run is gone), and the tool's only dotnet processes are init's consumer
    // build and the server adapter build that --adapter skips.
    [Fact]
    public void NeitherTheToolNorTheToolkitItRunsHasAnHttpClient()
    {
        var tool = typeof(SmokeProject).Assembly;
        var toolkit = tool.GetReferencedAssemblies().Where(name => name.Name!.StartsWith("Valheim.", StringComparison.Ordinal))
            .Select(System.Reflection.Assembly.Load).Prepend(tool).ToList();
        Assert.Contains(toolkit, assembly => assembly.GetName().Name == "Valheim.Testing.Game");
        foreach (var assembly in toolkit)
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name => name.Name == "System.Net.Http");
    }

    private static string Manifest() =>
        "{\"schema\":1,\"build\":\"fixture\",\"files\":[{\"file\":\"valheimCLI.dll\",\"sha256\":\"" +
        new string('a', 64) + "\",\"plugins\":[\"valheimCLI.valheimCLI\"],\"extensions\":{}}]}";
}
