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
        var resolved = SmokeInputs.Cli(new Dictionary<string, string> { ["--cli-files"] = bundle }, _root);
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
            SmokeInputs.Cli(new Dictionary<string, string> { ["--cli-files"] = bundle }, _root));
        Assert.Contains("Several ValheimCLI capability manifests", error.Message);
        Assert.Contains("never mix packs", error.Message);
    }

    [Fact]
    public void MissingCliManifestNamesTheRequiredChoice()
    {
        string bundle = Path.Combine(_root, "cli");
        Directory.CreateDirectory(bundle);
        var error = Assert.Throws<InvalidDataException>(() =>
            SmokeInputs.Cli(new Dictionary<string, string> { ["--cli-files"] = bundle }, _root));
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
        Assert.Contains(server ? "PinnedServerRun.MainAsync" : "new TargetedRegression", source);
    }

    [Fact]
    public void ToolCarriesTheServerAdapterSourceNeededOutsideThisCheckout()
    {
        string[] names = System.Reflection.Assembly.Load("NativeSmoke").GetManifestResourceNames();
        foreach (string file in new[] { "Plugin.cs", "TestExtension.cs", "Members.cs",
                     "Valheim.GameReferences.props", "Valheim.GameReferences.targets" })
            Assert.Contains("NativeSmoke.Adapter." + file, names);
    }

    private static string Manifest() =>
        "{\"schema\":1,\"build\":\"fixture\",\"files\":[{\"file\":\"valheimCLI.dll\",\"sha256\":\"" +
        new string('a', 64) + "\",\"plugins\":[\"valheimCLI.valheimCLI\"],\"extensions\":{}}]}";
}
