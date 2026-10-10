using System.Diagnostics;
using System.Security;
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
        string scenario = Path.Combine(_root, "scenario");
        Directory.CreateDirectory(feed);
        Directory.CreateDirectory(helper);
        Directory.CreateDirectory(scenario);
        File.WriteAllText(Path.Combine(_root, "NuGet.Config"),
            $"<configuration><packageSources><clear/><add key=\"local\" value=\"{SecurityElement.Escape(feed)}\"/></packageSources></configuration>");
        File.WriteAllText(Path.Combine(helper, "OneShotHelper.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>OneShotHelper</PackageId><Version>1.0.0</Version></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(helper, "Helper.cs"), "namespace OneShotHelper; public static class Value { public static string Name => \"helper\"; }");
        await RunDotnet("pack", Path.Combine(helper, "OneShotHelper.csproj"), "-o", feed, "-v", "quiet");

        string Reference(string name, string path) =>
            $"<Reference Include=\"{name}\"><HintPath>{SecurityElement.Escape(path)}</HintPath><Private>true</Private></Reference>";
        string cli = System.Reflection.Assembly.Load("Valheim.Cli.Testing").Location;
        File.WriteAllText(Path.Combine(scenario, "Scenario.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup>" +
            "<PackageReference Include=\"OneShotHelper\" Version=\"1.0.0\"/>" +
            Reference("Valheim.Testing.GameSessions", typeof(GameSession).Assembly.Location) +
            Reference("Valheim.Testing.Game", typeof(GameReply).Assembly.Location) +
            Reference("Valheim.Cli.Testing", cli) + "</ItemGroup></Project>");
        File.WriteAllText(Path.Combine(scenario, "Scenario.cs"), """
            using Valheim.Testing.Game;
            using Valheim.Testing.GameSessions;
            using valheim_cli.Testing;
            public sealed class SeparateScenario : IOneShotServerScenario
            {
                public string Name => OneShotHelper.Value.Name +
                    (typeof(GameReply).GetProperty("Result")!.PropertyType == typeof(CommandResult) ? "-shared" : "-private");
                public bool RequiresClient => false;
                public Task RunAsync(GameSession session, OneShotServerContext context) => Task.CompletedTask;
            }
            """);

        string output = Path.Combine(_root, "built");
        string dll = await OneShotScenario.BuildAsync(Path.Combine(scenario, "Scenario.csproj"), output, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(dll)!, "OneShotHelper.dll")));
        Assert.Equal("helper-shared", OneShotScenario.Load(dll, Path.Combine(_root, "pinned")).Runner.Name);
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
