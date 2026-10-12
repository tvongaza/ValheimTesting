using System.Diagnostics;
using System.Security;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class OneShotScenarioBuildTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("one-shot-scenario-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task SeparateScenarioLoadsItsNuGetDependencyAndSharesToolkitTransportTypes()
    {
        using var dataRoot = new FakeDataRoot(Path.Combine(_root, "machine"));
        string feed = Path.Combine(_root, "feed");
        string helper = Path.Combine(_root, "helper");
        string projectHelper = Path.Combine(_root, "project-helper");
        string directHelper = Path.Combine(_root, "direct-helper");
        string shared = Path.Combine(_root, "shared");
        string scenario = Path.Combine(_root, "scenario");
        Directory.CreateDirectory(feed);
        Directory.CreateDirectory(helper);
        Directory.CreateDirectory(projectHelper);
        Directory.CreateDirectory(directHelper);
        Directory.CreateDirectory(shared);
        Directory.CreateDirectory(scenario);
        File.WriteAllText(Path.Combine(_root, "NuGet.Config"),
            $"<configuration><packageSources><clear/><add key=\"local\" value=\"{SecurityElement.Escape(feed)}\"/></packageSources></configuration>");
        File.WriteAllText(Path.Combine(helper, "OneShotHelper.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>OneShotHelper</PackageId><Version>1.0.0</Version></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(helper, "Helper.cs"), "namespace OneShotHelper; public static class Value { public static string Name => \"helper\"; }");
        File.WriteAllText(Path.Combine(projectHelper, "ObserverHelper.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        string helperSource = Path.Combine(projectHelper, "Value.cs");
        File.WriteAllText(helperSource, "namespace ObserverHelper; public static class Value { public const string Name = \"first\"; }");
        File.WriteAllText(Path.Combine(directHelper, "DirectHelper.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        string directSource = Path.Combine(directHelper, "Value.cs");
        File.WriteAllText(directSource, "namespace DirectHelper; public static class Value { public const string Name = \"direct-first\"; }");
        string linked = Path.Combine(shared, "Linked.cs");
        File.WriteAllText(linked, "public static class LinkedValue { public const string Name = \"original\"; }");
        string firstResource = Path.Combine(shared, "A.txt");
        string secondResource = Path.Combine(shared, "B.txt");
        string generatedResource = Path.Combine(shared, "Dynamic.txt");
        File.WriteAllText(firstResource, "baseline");
        File.WriteAllText(secondResource, "candidate");
        File.WriteAllText(generatedResource, "target-populated");
        await RunDotnet("pack", Path.Combine(helper, "OneShotHelper.csproj"), "-o", feed, "-v", "quiet");
        await RunDotnet("build", Path.Combine(directHelper, "DirectHelper.csproj"), "-v", "quiet");
        string directBinary = Path.Combine(scenario, "DirectHelper.dll");
        File.Copy(Path.Combine(directHelper, "bin", "Debug", "net10.0", "DirectHelper.dll"), directBinary);

        string Reference(string name, string path) =>
            $"<Reference Include=\"{name}\"><HintPath>{SecurityElement.Escape(path)}</HintPath><Private>true</Private></Reference>";
        string cli = System.Reflection.Assembly.Load("Valheim.Cli.Testing").Location;
        File.WriteAllText(Path.Combine(scenario, "adapter.props"),
            "<Project><PropertyGroup><DefineConstants>$(DefineConstants);PINNED_ADAPTER</DefineConstants></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(scenario, "options.xml"),
            "<Project><PropertyGroup><DefineConstants>$(DefineConstants);OPTION_A</DefineConstants></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(scenario, "Scenario.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"adapter.props\"/><Import Project=\"options.xml\"/><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><DefineConstants>$(DefineConstants);$(ObserverMode)</DefineConstants><ResourceName Condition=\"'$(ResourceName)' == ''\">Scenario.Dynamic.txt</ResourceName></PropertyGroup><ItemGroup>" +
            "<PackageReference Include=\"OneShotHelper\" Version=\"1.0.0\"/>" +
            "<ProjectReference Include=\"../project-helper/ObserverHelper.csproj\"/>" +
            "<Reference Include=\"DirectHelper.dll\"/>" +
            "<Compile Include=\"../shared/Linked.cs\" Link=\"Linked.cs\"/>" +
            "<EmbeddedResource Include=\"../shared/A.txt\"/><EmbeddedResource Include=\"../shared/B.txt\"/>" +
            Reference("Valheim.Testing.GameSessions", typeof(GameSession).Assembly.Location) +
            Reference("Valheim.Testing.Game", typeof(GameReply).Assembly.Location) +
            Reference("Valheim.Cli.Testing", cli) +
            "</ItemGroup><Target Name=\"AddDynamicResource\" BeforeTargets=\"AssignTargetPaths\"><ItemGroup>" +
            "<EmbeddedResource Include=\"../shared/Dynamic.txt\" LogicalName=\"$(ResourceName)\"/>" +
            "</ItemGroup></Target>" +
            // One line per run of the Build target: the compile count of an --adapter-project run.
            "<Target Name=\"CountBuild\" AfterTargets=\"Build\" Condition=\"'$(BuildCountFile)' != ''\"><WriteLinesToFile File=\"$(BuildCountFile)\" Lines=\"build\"/></Target>" +
            "</Project>");
        File.WriteAllText(Path.Combine(scenario, "Scenario.cs"), """
            using Valheim.Testing.Game;
            using Valheim.Testing.GameSessions;
            using valheim_cli.Testing;
            public sealed class SeparateScenario : IOneShotServerScenario
            {
            #if OBSERVER_A
                private const string Mode = "a";
            #else
                private const string Mode = "b";
            #endif
                public string Name => OneShotHelper.Value.Name + "-" + LinkedValue.Name + "-" + ObserverHelper.Value.Name + "-" + DirectHelper.Value.Name + "-" + Mode +
                    (typeof(GameReply).GetProperty("Result")!.PropertyType == typeof(CommandResult) ? "-shared" : "-private");
                public bool RequiresClient => false;
                public Task RunAsync(GameSession session, OneShotServerContext context) => Task.CompletedTask;
            }
            """);

        string output = Path.Combine(_root, "built");
        string buildCount = Path.Combine(_root, "build-count.txt");
        string dll = await OneShotScenario.BuildAsync(Path.Combine(scenario, "Scenario.csproj"), output,
            CancellationToken.None, new Dictionary<string, string> { ["ModDll"] = "baseline.dll", ["BuildCountFile"] = buildCount },
            captureAdapterSources: true);
        // The adapter compiles once: its inputs come from that build's own answers, not from builds before and after it.
        Assert.Single(File.ReadAllLines(buildCount));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(dll)!, "OneShotHelper.dll")));
        var record = JsonSerializer.Deserialize<AdapterSourceInputs.Record>(
            File.ReadAllText(Path.Combine(output, "source-inputs", "manifest.json")))!;
        Assert.Equal(Valheim.Testing.Game.FileHash.Sha256(dll), record.AdapterSha256);
        Assert.Contains(record.OtherInputs, input => input.Path.EndsWith("adapter.props", StringComparison.Ordinal));
        Assert.Contains(record.OtherInputs, input => input.Kind == "Import" &&
            input.Path.EndsWith("options.xml", StringComparison.Ordinal));
        Assert.Contains(record.OtherInputs, input => input.Kind == "EmbeddedResource" &&
            input.Path.EndsWith("Dynamic.txt", StringComparison.Ordinal));
        Assert.Contains(record.OtherInputs, input => input.Kind == "EditorConfigFiles" &&
            input.Path.EndsWith("GeneratedMSBuildEditorConfig.editorconfig", StringComparison.Ordinal));
        Assert.Contains(record.OtherInputs, input => input.Kind == "_ResolvedProjectReferencePaths" &&
            input.Path.EndsWith("ObserverHelper.dll", StringComparison.Ordinal));
        Assert.Contains(record.OtherInputs, input => input.Kind == "ReferencePath" &&
            input.Path.EndsWith("DirectHelper.dll", StringComparison.Ordinal));
        Assert.Contains(record.OtherInputs, input => input.Kind == "CompilerProperty" &&
            input.Path == "DefineConstants");
        var source = Assert.Single(record.Sources, source => source.Document.EndsWith("Linked.cs", StringComparison.Ordinal));
        Assert.Contains("original", File.ReadAllText(source.Retained));
        string candidateOutput = Path.Combine(_root, "candidate");
        await OneShotScenario.BuildAsync(Path.Combine(scenario, "Scenario.csproj"), candidateOutput,
            CancellationToken.None, new Dictionary<string, string> { ["ModDll"] = "candidate.dll" }, captureAdapterSources: true);
        var candidate = JsonSerializer.Deserialize<AdapterSourceInputs.Record>(
            File.ReadAllText(Path.Combine(candidateOutput, "source-inputs", "manifest.json")))!;
        Assert.Equal(record.SourceInputSha256, candidate.SourceInputSha256);
        string modeOutput = Path.Combine(_root, "mode-a");
        await OneShotScenario.BuildAsync(Path.Combine(scenario, "Scenario.csproj"), modeOutput,
            CancellationToken.None, new Dictionary<string, string> { ["ModDll"] = "baseline.dll",
                ["ObserverMode"] = "OBSERVER_A" }, captureAdapterSources: true);
        var mode = JsonSerializer.Deserialize<AdapterSourceInputs.Record>(
            File.ReadAllText(Path.Combine(modeOutput, "source-inputs", "manifest.json")))!;
        Assert.Equal(record.CompiledSourceSha256, mode.CompiledSourceSha256);
        Assert.NotEqual(record.SourceInputSha256, mode.SourceInputSha256);
        string renamedOutput = Path.Combine(_root, "renamed-resource");
        await OneShotScenario.BuildAsync(Path.Combine(scenario, "Scenario.csproj"), renamedOutput,
            CancellationToken.None, new Dictionary<string, string> { ["ModDll"] = "baseline.dll",
                ["ResourceName"] = "Scenario.Renamed.txt" }, captureAdapterSources: true);
        var renamed = JsonSerializer.Deserialize<AdapterSourceInputs.Record>(
            File.ReadAllText(Path.Combine(renamedOutput, "source-inputs", "manifest.json")))!;
        Assert.NotEqual(record.SourceInputSha256, renamed.SourceInputSha256);
        File.WriteAllText(firstResource, "candidate");
        File.WriteAllText(secondResource, "baseline");
        string swappedOutput = Path.Combine(_root, "swapped");
        await OneShotScenario.BuildAsync(Path.Combine(scenario, "Scenario.csproj"), swappedOutput,
            CancellationToken.None, new Dictionary<string, string> { ["ModDll"] = "baseline.dll" }, captureAdapterSources: true);
        var swapped = JsonSerializer.Deserialize<AdapterSourceInputs.Record>(
            File.ReadAllText(Path.Combine(swappedOutput, "source-inputs", "manifest.json")))!;
        Assert.NotEqual(record.SourceInputSha256, swapped.SourceInputSha256);
        string project = Path.Combine(scenario, "Scenario.csproj");
        string[] args = ["-p:Configuration=Release", "-p:CopyLocalLockFileAssemblies=true",
            "-p:OutputPath=" + Path.Combine(output, "bin"),
            "-p:CopyOutputDLLPath=" + Path.Combine(output, "private-deployment"),
            "-p:CopyOutputDLLPath2=", "-p:CopyOutputDLLPath3=", "-p:ModDll=baseline.dll",
            "-p:DebugType=embedded", "-p:EmbedAllSources=true"];
        var build = new ProjectBuild(project, args, NuGetCaches.Owned());
        var snapshot = await AdapterSourceInputs.BeginAsync(build, Path.Combine(_root, "changed-import"), null, CancellationToken.None);
        var built = await build.BuildAsync(CancellationToken.None, query: AdapterSourceInputs.Query);
        Assert.True(built.ExitCode == 0, built.Combined);
        File.AppendAllText(Path.Combine(scenario, "adapter.props"), "<!-- changed before the run -->");
        var changed = await Assert.ThrowsAsync<InvalidDataException>(() => AdapterSourceInputs.FinishAsync(snapshot, built.Query, dll, CancellationToken.None));
        Assert.Contains("imported build settings changed", changed.Message);
        // This is the slow-world-entry race: the checkout changes after compilation, before the DLL is loaded.
        File.WriteAllText(linked, "public static class LinkedValue { public const string Name = \"changed\"; }");
        Assert.Contains("original", File.ReadAllText(source.Retained));
        Assert.Equal("helper-original-first-direct-first-b-shared", OneShotScenario.Load(dll, Path.Combine(_root, "pinned")).Runner.Name);
        File.WriteAllText(linked, "public static class LinkedValue { public const string Name = \"original\"; }");
        File.WriteAllText(Path.Combine(scenario, "adapter.props"),
            "<Project><PropertyGroup><DefineConstants>$(DefineConstants);PINNED_ADAPTER</DefineConstants></PropertyGroup></Project>");
        File.WriteAllText(helperSource, "namespace ObserverHelper; public static class Value { public const string Name = \"second\"; }");
        string changedHelperOutput = Path.Combine(_root, "changed-helper");
        string changedHelperDll = await OneShotScenario.BuildAsync(Path.Combine(scenario, "Scenario.csproj"), changedHelperOutput,
            CancellationToken.None, new Dictionary<string, string> { ["ModDll"] = "baseline.dll" }, captureAdapterSources: true);
        var changedHelper = JsonSerializer.Deserialize<AdapterSourceInputs.Record>(
            File.ReadAllText(Path.Combine(changedHelperOutput, "source-inputs", "manifest.json")))!;
        Assert.NotEqual(swapped.SourceInputSha256, changedHelper.SourceInputSha256);
        Assert.Contains("second", OneShotScenario.Load(changedHelperDll, Path.Combine(_root, "changed-helper-pinned")).Runner.Name);
        File.WriteAllText(directSource, "namespace DirectHelper; public static class Value { public const string Name = \"direct-second\"; }");
        await RunDotnet("build", Path.Combine(directHelper, "DirectHelper.csproj"), "-v", "quiet");
        File.Copy(Path.Combine(directHelper, "bin", "Debug", "net10.0", "DirectHelper.dll"), directBinary, overwrite: true);
        string directOutput = Path.Combine(_root, "changed-direct");
        string directDll = await OneShotScenario.BuildAsync(Path.Combine(scenario, "Scenario.csproj"), directOutput,
            CancellationToken.None, new Dictionary<string, string> { ["ModDll"] = "baseline.dll" }, captureAdapterSources: true);
        var changedDirect = JsonSerializer.Deserialize<AdapterSourceInputs.Record>(
            File.ReadAllText(Path.Combine(directOutput, "source-inputs", "manifest.json")))!;
        Assert.NotEqual(changedHelper.SourceInputSha256, changedDirect.SourceInputSha256);
        Assert.Contains("direct-second", OneShotScenario.Load(directDll, Path.Combine(_root, "changed-direct-pinned")).Runner.Name);
    }

    [Fact]
    public async Task AnInputEditedWhileTheOneBuildRunsIsRefused()
    {
        using var dataRoot = new FakeDataRoot(Path.Combine(_root, "machine"));
        string feed = Path.Combine(_root, "feed"), scenario = Path.Combine(_root, "scenario");
        Directory.CreateDirectory(feed);
        Directory.CreateDirectory(scenario);
        File.WriteAllText(Path.Combine(_root, "NuGet.Config"),
            $"<configuration><packageSources><clear/><add key=\"local\" value=\"{SecurityElement.Escape(feed)}\"/></packageSources></configuration>");
        string resource = Path.Combine(scenario, "Data.txt");
        File.WriteAllText(resource, "before");
        File.WriteAllText(Path.Combine(scenario, "Scenario.cs"), "public static class Marker { }");
        string project = Path.Combine(scenario, "Scenario.csproj");
        File.WriteAllText(project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
            "<ItemGroup><EmbeddedResource Include=\"Data.txt\"/></ItemGroup>" +
            "<Target Name=\"EditResource\" BeforeTargets=\"CoreCompile\" Condition=\"'$(EditResource)' != ''\">" +
            "<WriteLinesToFile File=\"$(EditResource)\" Lines=\"edited while building\" Overwrite=\"true\"/></Target></Project>");
        await OneShotScenario.BuildAsync(project, Path.Combine(_root, "unedited"), CancellationToken.None, captureAdapterSources: true);
        // The same build without source capture asks only for the target path, which MSBuild prints bare.
        Assert.True(File.Exists(await OneShotScenario.BuildAsync(project, Path.Combine(_root, "plain"), CancellationToken.None)));
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => OneShotScenario.BuildAsync(project,
            Path.Combine(_root, "edited"), CancellationToken.None, new Dictionary<string, string> { ["EditResource"] = resource },
            captureAdapterSources: true));
        Assert.Contains("changed during compilation", refused.Message);
    }

    [Fact]
    public async Task AGeneratorInTheIntermediateFolderMayRewriteADeclaredInput()
    {
        using var dataRoot = new FakeDataRoot(Path.Combine(_root, "machine"));
        string feed = Path.Combine(_root, "feed"), scenario = Path.Combine(_root, "scenario");
        Directory.CreateDirectory(feed);
        Directory.CreateDirectory(Path.Combine(scenario, "obj"));
        File.WriteAllText(Path.Combine(_root, "NuGet.Config"),
            $"<configuration><packageSources><clear/><add key=\"local\" value=\"{SecurityElement.Escape(feed)}\"/></packageSources></configuration>");
        File.WriteAllText(Path.Combine(scenario, "obj", "version.txt"), "previous run");
        File.WriteAllText(Path.Combine(scenario, "Scenario.cs"), "public static class Marker { }");
        string project = Path.Combine(scenario, "Scenario.csproj");
        File.WriteAllText(project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
            "<ItemGroup><EmbeddedResource Include=\"obj/version.txt\"/></ItemGroup>" +
            "<Target Name=\"WriteVersion\" BeforeTargets=\"CoreCompile\">" +
            "<WriteLinesToFile File=\"obj/version.txt\" Lines=\"$(VersionText)\" Overwrite=\"true\"/></Target></Project>");
        // The declared file held the previous run's text when the build began; the project's own target rewrites it.
        Assert.True(File.Exists(await OneShotScenario.BuildAsync(project, Path.Combine(_root, "generated"), CancellationToken.None,
            new Dictionary<string, string> { ["VersionText"] = "this run" }, captureAdapterSources: true)));
    }

    private static async Task RunDotnet(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, (await output) + "\n" + (await error));
    }
}
