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
        string shared = Path.Combine(_root, "shared");
        string scenario = Path.Combine(_root, "scenario");
        Directory.CreateDirectory(feed);
        Directory.CreateDirectory(helper);
        Directory.CreateDirectory(shared);
        Directory.CreateDirectory(scenario);
        File.WriteAllText(Path.Combine(_root, "NuGet.Config"),
            $"<configuration><packageSources><clear/><add key=\"local\" value=\"{SecurityElement.Escape(feed)}\"/></packageSources></configuration>");
        File.WriteAllText(Path.Combine(helper, "OneShotHelper.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>OneShotHelper</PackageId><Version>1.0.0</Version></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(helper, "Helper.cs"), "namespace OneShotHelper; public static class Value { public static string Name => \"helper\"; }");
        string linked = Path.Combine(shared, "Linked.cs");
        File.WriteAllText(linked, "public static class LinkedValue { public const string Name = \"original\"; }");
        await RunDotnet("pack", Path.Combine(helper, "OneShotHelper.csproj"), "-o", feed, "-v", "quiet");

        string Reference(string name, string path) =>
            $"<Reference Include=\"{name}\"><HintPath>{SecurityElement.Escape(path)}</HintPath><Private>true</Private></Reference>";
        string cli = System.Reflection.Assembly.Load("Valheim.Cli.Testing").Location;
        File.WriteAllText(Path.Combine(scenario, "adapter.props"),
            "<Project><PropertyGroup><DefineConstants>$(DefineConstants);PINNED_ADAPTER</DefineConstants></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(scenario, "Scenario.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"adapter.props\"/><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup>" +
            "<PackageReference Include=\"OneShotHelper\" Version=\"1.0.0\"/>" +
            "<Compile Include=\"../shared/Linked.cs\" Link=\"Linked.cs\"/>" +
            Reference("Valheim.Testing.GameSessions", typeof(GameSession).Assembly.Location) +
            Reference("Valheim.Testing.Game", typeof(GameReply).Assembly.Location) +
            Reference("Valheim.Cli.Testing", cli) + "</ItemGroup></Project>");
        File.WriteAllText(Path.Combine(scenario, "Scenario.cs"), """
            using Valheim.Testing.Game;
            using Valheim.Testing.GameSessions;
            using valheim_cli.Testing;
            public sealed class SeparateScenario : IOneShotServerScenario
            {
                public string Name => OneShotHelper.Value.Name + "-" + LinkedValue.Name +
                    (typeof(GameReply).GetProperty("Result")!.PropertyType == typeof(CommandResult) ? "-shared" : "-private");
                public bool RequiresClient => false;
                public Task RunAsync(GameSession session, OneShotServerContext context) => Task.CompletedTask;
            }
            """);

        string output = Path.Combine(_root, "built");
        string dll = await OneShotScenario.BuildAsync(Path.Combine(scenario, "Scenario.csproj"), output,
            CancellationToken.None, new Dictionary<string, string> { ["ModDll"] = "baseline.dll" }, captureAdapterSources: true);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(dll)!, "OneShotHelper.dll")));
        var record = JsonSerializer.Deserialize<AdapterSourceInputs.Record>(
            File.ReadAllText(Path.Combine(output, "source-inputs", "manifest.json")))!;
        Assert.Equal(Valheim.Testing.Game.FileHash.Sha256(dll), record.AdapterSha256);
        Assert.Contains(record.OtherInputs, input => input.Path.EndsWith("adapter.props", StringComparison.Ordinal));
        var source = Assert.Single(record.Sources, source => source.Document.EndsWith("Linked.cs", StringComparison.Ordinal));
        Assert.Contains("original", File.ReadAllText(source.Retained));
        string candidateOutput = Path.Combine(_root, "candidate");
        await OneShotScenario.BuildAsync(Path.Combine(scenario, "Scenario.csproj"), candidateOutput,
            CancellationToken.None, new Dictionary<string, string> { ["ModDll"] = "candidate.dll" }, captureAdapterSources: true);
        var candidate = JsonSerializer.Deserialize<AdapterSourceInputs.Record>(
            File.ReadAllText(Path.Combine(candidateOutput, "source-inputs", "manifest.json")))!;
        Assert.Equal(record.SourceInputSha256, candidate.SourceInputSha256);
        string project = Path.Combine(scenario, "Scenario.csproj");
        string packages = ModBuild.WritableCache("NUGET_PACKAGES", "packages");
        string http = ModBuild.WritableCache("NUGET_HTTP_CACHE_PATH", "http");
        string[] args = [project, "-p:Configuration=Release", "-p:CopyLocalLockFileAssemblies=true",
            "-p:OutputPath=" + Path.Combine(output, "bin"),
            "-p:CopyOutputDLLPath=" + Path.Combine(output, "private-deployment"),
            "-p:CopyOutputDLLPath2=", "-p:CopyOutputDLLPath3=", "-p:ModDll=baseline.dll",
            "-p:DebugType=embedded", "-p:EmbedAllSources=true"];
        var snapshot = await AdapterSourceInputs.BeforeAsync(project, Path.Combine(_root, "changed-import"),
            args, packages, http, CancellationToken.None);
        File.AppendAllText(Path.Combine(scenario, "adapter.props"), "<!-- changed before the run -->");
        await Assert.ThrowsAsync<InvalidDataException>(() => AdapterSourceInputs.AfterAsync(snapshot, dll,
            args, packages, http, CancellationToken.None));
        // This is the slow-world-entry race: the checkout changes after compilation, before the DLL is loaded.
        File.WriteAllText(linked, "public static class LinkedValue { public const string Name = \"changed\"; }");
        Assert.Contains("original", File.ReadAllText(source.Retained));
        Assert.Equal("helper-original-shared", OneShotScenario.Load(dll, Path.Combine(_root, "pinned")).Runner.Name);
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
