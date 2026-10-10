using System.Text.Json;
using Valheim.Testing.Game;
using Xunit;

public sealed class ModBuildTests : IDisposable
{
    private readonly ClientLaunchTests.Install _game = ClientLaunchTests.Install.Windows();
    private readonly string _work = Directory.CreateTempSubdirectory("mod-build-test-").FullName;
    public void Dispose() { _game.Dispose(); Directory.Delete(_work, recursive: true); }

    private string Project(string references = "")
    {
        string directory = Path.Combine(_work, "mod"); Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, "Sample.csproj");
        File.WriteAllText(project, "<Project><ItemGroup>" + references + "</ItemGroup></Project>");
        return project;
    }

    private void Inputs()
    {
        _game.Add("valheim_Data/Managed/assembly_valheim.dll");
        _game.Add("BepInEx/core/BepInEx.dll");
        _game.Add("BepInEx/core/0Harmony.dll");
    }

    [Fact] public void PlanSelectsPrivateReferencesWithoutChangingGame()
    {
        Inputs();
        _game.Add("valheim_Data/Managed/assembly_utils.dll");
        string jotunn = _game.Add("BepInEx/plugins/Jotunn/Jotunn.dll");
        string project = Project("<Reference Include=\"Jotunn\" />");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(project)!, "environment.props"),
            "<HintPath>$(PUBLICIZED_PATH)/assembly_utils_publicized.dll</HintPath>");
        var plan = ModBuild.Plan(project, _game.Root, new Dictionary<string, string>(), Path.Combine(_work, "evidence"));
        Assert.Equal(new[] { "assembly_utils" }, plan.Publicize);
        Assert.Equal(jotunn, plan.Dependencies["Jotunn"]);
        Assert.False(Directory.Exists(plan.Output));
    }

    [Fact] public void MissingJotunnIsActionableAndDoesNotMakeAnOutput()
    {
        Inputs();
        string output = Path.Combine(_work, "evidence");
        string message = Assert.Throws<InvalidDataException>(() => ModBuild.Plan(Project("<Reference Include=\"Jotunn\" />"),
            _game.Root, new Dictionary<string, string>(), output)).Message;
        Assert.Contains("Jotunn", message);
        Assert.Contains("--dependency", message);
        Assert.False(Path.Exists(output));
    }

    [Fact] public void BuildOverridesAProjectsLivePluginDeployment()
    {
        Inputs();
        string live = Path.Combine(_game.Root, "BepInEx", "plugins");
        string project = Project();
        File.WriteAllText(project, "<Project><PropertyGroup><CopyOutputDLLPath>" + live +
            "</CopyOutputDLLPath></PropertyGroup></Project>");
        var input = ModBuild.Plan(project, _game.Root, new Dictionary<string, string>(), Path.Combine(_work, "evidence"));
        string privateDeployment = Path.Combine(input.Output, "private-deployment");
        string[] arguments = ModBuild.BuildArguments(input, Path.Combine(input.Output, "references", "core"),
            Path.Combine(input.Output, "references", "publicized_assemblies"), privateDeployment);
        Assert.Contains("-p:CopyOutputDLLPath=" + privateDeployment, arguments);
        Assert.Contains("-p:OutputPath=" + Path.Combine(input.Output, "private-build"), arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains(live, StringComparison.Ordinal));
        Assert.False(Directory.Exists(live));
    }

    [Fact] public void IdenticalInstalledJotunnCopiesAreOneCoherentChoice()
    {
        Inputs();
        _game.Add("BepInEx/core/Jotunn.dll", "one copy");
        _game.Add("BepInEx/plugins/Jotunn/Jotunn.dll", "one copy");
        var plan = ModBuild.Plan(Project("<Reference Include=\"Jotunn\" />"), _game.Root,
            new Dictionary<string, string>(), Path.Combine(_work, "out"));
        Assert.EndsWith("Jotunn.dll", plan.Dependencies["Jotunn"]);
    }

    [Fact] public void WrongGameAndLiveDeploymentOutputAreRefused()
    {
        Inputs();
        string project = Project();
        Assert.Throws<DirectoryNotFoundException>(() => ModBuild.Plan(project, Path.Combine(_work, "missing"),
            new Dictionary<string, string>(), Path.Combine(_work, "out")));
        Assert.Contains("outside", Assert.Throws<ArgumentException>(() => ModBuild.Plan(project, _game.Root,
            new Dictionary<string, string>(), Path.Combine(_game.Root, "BepInEx", "plugins", "out"))).Message);
    }

    [Fact] public void DedicatedServerIsAValidCompileSource()
    {
        string server = Path.Combine(_work, "server");
        Directory.CreateDirectory(server);
        File.WriteAllText(Path.Combine(server, "valheim_server.exe"), "fake");
        string managed = Path.Combine(server, "valheim_server_Data", "Managed");
        Directory.CreateDirectory(managed);
        File.WriteAllText(Path.Combine(managed, "assembly_valheim.dll"), "fake");
        string core = Path.Combine(server, "BepInEx", "core");
        Directory.CreateDirectory(core);
        File.WriteAllText(Path.Combine(core, "BepInEx.dll"), "fake");
        File.WriteAllText(Path.Combine(core, "0Harmony.dll"), "fake");
        Assert.Equal(managed, ModBuild.Plan(Project(), server, new Dictionary<string, string>(), Path.Combine(_work, "out")).Managed);
    }

    [Fact] public void RecordedSourcesAndPublicizedFilesMustStillMatch()
    {
        Inputs();
        string source = Path.Combine(_game.Root, "valheim_Data", "Managed", "assembly_valheim.dll");
        string output = Path.Combine(_work, "out"); Directory.CreateDirectory(output);
        string publicized = Path.Combine(output, "assembly_valheim_publicized.dll");
        File.Copy(source, publicized);
        string mod = Path.Combine(output, "mod.dll"); File.Copy(source, mod);
        var record = new ModBuild.BuildRecord(_game.Root, InstallPins.GameHash(_game.Root), Path.GetDirectoryName(source)!, mod, FileHash.Sha256(mod),
            "BepInEx.AssemblyPublicizer/0.4.3", [new(source, FileHash.Sha256(source))],
            [new(publicized, FileHash.Sha256(publicized))], []);
        string manifest = Path.Combine(output, "build-inputs.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(record));
        Assert.Equal(mod, ModBuild.Verify(manifest, _game.Root, mod).Mod);
        File.AppendAllText(publicized, "changed");
        Assert.Contains("changed", Assert.Throws<InvalidDataException>(() => ModBuild.Verify(manifest, _game.Root, mod)).Message);
        File.WriteAllText(publicized, File.ReadAllText(source));
        File.AppendAllText(source, "new game build");
        Assert.Contains("game build differs", Assert.Throws<InvalidDataException>(() => ModBuild.Verify(manifest, _game.Root, mod)).Message);
    }

    [Fact] public void ServerInstallCanUseTheBuildOnlyWhenGameAssembliesMatch()
    {
        Inputs();
        string source = Path.Combine(_game.Root, "valheim_Data", "Managed", "assembly_valheim.dll");
        string server = Path.Combine(_work, "server"), managed = Path.Combine(server, "valheim_server_Data", "Managed");
        Directory.CreateDirectory(managed);
        string counterpart = Path.Combine(managed, "assembly_valheim.dll"); File.Copy(source, counterpart);
        string mod = Path.Combine(_work, "mod.dll"); File.Copy(source, mod);
        string publicized = Path.Combine(_work, "assembly_valheim_publicized.dll"); File.Copy(source, publicized);
        string manifest = Path.Combine(_work, "build-inputs.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new ModBuild.BuildRecord(_game.Root, InstallPins.GameHash(_game.Root), Path.GetDirectoryName(source)!, mod,
            FileHash.Sha256(mod), "BepInEx.AssemblyPublicizer/0.4.3", [new(source, FileHash.Sha256(source))],
            [new(publicized, FileHash.Sha256(publicized))], [])));
        Assert.Equal(mod, ModBuild.Verify(manifest, server, mod).Mod);
        File.AppendAllText(counterpart, "changed");
        Assert.Contains("game build differs", Assert.Throws<InvalidDataException>(() => ModBuild.Verify(manifest, server, mod)).Message);
    }
    [Fact] public void AChangedModGetsANewRunPinWithoutEditingThePreviousRun()
    {
        Inputs();
        string source = Path.Combine(_game.Root, "valheim_Data", "Managed", "assembly_valheim.dll");
        string publicized = Path.Combine(_work, "assembly_valheim_publicized.dll");
        File.Copy(source, publicized);
        string first = Path.Combine(_work, "first.dll"), second = Path.Combine(_work, "second.dll");
        File.WriteAllText(first, "first build"); File.WriteAllText(second, "edited build");
        string Write(string mod, string name)
        {
            string manifest = Path.Combine(_work, name + ".json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new ModBuild.BuildRecord(_game.Root,
                InstallPins.GameHash(_game.Root), Path.GetDirectoryName(source)!, mod, FileHash.Sha256(mod),
                "BepInEx.AssemblyPublicizer/0.4.3", [new(source, FileHash.Sha256(source))],
                [new(publicized, FileHash.Sha256(publicized))], [])));
            return manifest;
        }
        string oldPin = Write(first, "first-run"), newPin = Write(second, "second-run");
        Assert.Equal(first, ModBuild.Verify(oldPin, _game.Root, first).Mod);
        Assert.Throws<InvalidDataException>(() => ModBuild.Verify(oldPin, _game.Root, second));
        Assert.Equal(second, ModBuild.Verify(newPin, _game.Root, second).Mod);
    }
}
