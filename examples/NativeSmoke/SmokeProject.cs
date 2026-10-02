using System.Diagnostics;
using System.Net;

/// <summary>
/// Leave an editable native scenario beside the evidence. Prove it builds from the published package before copying
/// it into the output: a source checkout or a stale global NuGet cache cannot stand in for a release.
/// </summary>
internal static class SmokeProject
{
    internal const string GameVersion = "0.1.0-preview.20";
    private const string Feed = "https://api.nuget.org/v3/index.json";

    internal static async Task<int> InitAsync(string[] args)
    {
        bool server = args.Length > 0 && args[0] == "server";
        string[] rest = server ? args[1..] : args;
        if (rest is not ["--output", var path] || string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine("Usage: valheim-test init [server] --output NEW_DIR");
            return 2;
        }
        string output = Path.GetFullPath(path);
        if (Path.Exists(output))
        {
            Console.Error.WriteLine("REFUSED: The consumer output must be new: " + output);
            return 3;
        }
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            Directory.CreateDirectory(output);
            await CreateAsync(output, server, cancel.Token);
            Console.WriteLine("READY: editable consumer in " + Path.Combine(output, "consumer") +
                "; Valheim.Testing.Game " + GameVersion + " restored and built from NuGet.org only.");
            return 0;
        }
        catch (Exception failure) when (failure is IOException or InvalidOperationException or HttpRequestException or UnauthorizedAccessException or OperationCanceledException)
        {
            Console.Error.WriteLine("REFUSED: " + failure.Message);
            if (Directory.Exists(output) && !Directory.EnumerateFileSystemEntries(output).Any()) Directory.Delete(output);
            return 3;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    internal static async Task CreateAsync(string output, bool server, CancellationToken cancellation)
    {
        string stage = Path.Combine(Path.GetTempPath(), "valheimtesting-smoke-project-" + Guid.NewGuid().ToString("N"));
        try
        {
            await RequirePublishedAsync(cancellation);
            Directory.CreateDirectory(stage);
            Write(stage, server);
            string packages = Path.Combine(stage, "packages"), httpCache = Path.Combine(stage, "http-cache");
            Directory.CreateDirectory(packages);
            Directory.CreateDirectory(httpCache);
            await RunDotnetAsync(stage, packages, httpCache, cancellation, "restore", "SmokeCheck.csproj", "--configfile", "NuGet.Config");
            await RunDotnetAsync(stage, packages, httpCache, cancellation, "build", "SmokeCheck.csproj", "-c", "Release", "--no-restore");
            string project = Path.Combine(output, "consumer");
            Directory.CreateDirectory(project);
            foreach (string file in new[] { "SmokeCheck.csproj", "Program.cs", "NuGet.Config", "README.md" })
                File.Copy(Path.Combine(stage, file), Path.Combine(project, file));
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    internal static void Write(string directory, bool server)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SmokeCheck.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup><PackageReference Include="Valheim.Testing.Game" Version="[{{GameVersion}}]" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "NuGet.Config"), $"<configuration><packageSources><clear/><add key=\"nuget.org\" value=\"{Feed}\"/></packageSources></configuration>\n");
        File.WriteAllText(Path.Combine(directory, "Program.cs"), server ? """
            using valheimCLI;
            using Valheim.Testing.Game;

            // The plan was written by valheim-test server-load. Supply a new result directory for each run.
            if (args is not [var planFile, var resultDirectory])
            {
                Console.Error.WriteLine("Usage: dotnet run -- plan.json NEW_RESULT_DIRECTORY");
                return 2;
            }
            return await PinnedServerRun.MainAsync(["run", planFile, resultDirectory], new PinnedServerRunOptions<ServerRunPlan>
            {
                Name = "my-native-server-check",
                ReadPlan = path =>
                {
                    var plan = ServerRunPlan.Read<ServerRunPlan>(path);
                    plan.ValidateServerPlan(plan.Pins.Keys.Where(key => key != "worlduid"), NativeServerRuntime.SessionTokenVariable);
                    return plan;
                },
                SessionCapability = NativeServerRuntime.SessionCapability,
                SessionTokenVariable = NativeServerRuntime.SessionTokenVariable,
                EnableDevcommands = false,
                Scenario = run =>
                {
                    run.Report.Step("selected server mods loaded", () =>
                    {
                        var worlds = run.Server.Execute("cli_world").Output.Select(Expectations.ParseWorld).OfType<WorldFacts>().ToArray();
                        if (worlds.Length != 1 || worlds[0].Uid != DefaultSmokeWorld.Uid)
                            throw new InvalidDataException("The dedicated server loaded a different fixture world.");
                    });
                    // Add your mod's focused observation and assertion here using run.Server and run.Report.Step(...).
                    return Task.CompletedTask;
                },
            });
            """ : """
            using Valheim.Testing.Game;

            // Run this from consumer/ after the native smoke has written ../environment.json.
            // Supply a fresh result directory for each run; the game install and selected DLLs remain pinned.
            if (args is not [var environmentFile, var resultDirectory])
            {
                Console.Error.WriteLine("Usage: dotnet run -- environment.json NEW_RESULT_DIRECTORY");
                return 2;
            }
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            var environment = RegressionEnvironment.Read(environmentFile);
            var regression = new TargetedRegression(environment);
            var report = regression.Run("smoke", resultDirectory, "selected plugin loads and my mod's behavior",
                ["first"], round =>
                {
                    // Add focused assertions here using round.Server and round.Step(...).
                    // A load smoke alone does not prove that the mod's feature works.
                }, cancellation.Token);
            return report.Passed ? 0 : 1;
            """);
        File.WriteAllText(Path.Combine(directory, "README.md"), $$"""
            # Extend this native smoke

            This project restores `Valheim.Testing.Game` from NuGet.org only. The pinned plan and private evidence
            live one directory above. Add your mod-specific observations in `Program.cs`, then run from this
            directory with a fresh result directory:

            ```sh
            dotnet run -c Release -- ../{{(server ? "plan" : "environment")}}.json ../my-assertions-1
            ```

            Keep the fixture, installed DLL hashes and ValheimCLI pins fixed while investigating a regression.
            A passing load check means the selected plugins started, not that their gameplay is correct.
            Do not publish the environment or evidence unchanged; they can contain private machine paths.
            """);
    }

    private static async Task RequirePublishedAsync(CancellationToken cancellation)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        string version = GameVersion.ToLowerInvariant();
        string url = $"https://api.nuget.org/v3-flatcontainer/valheim.testing.game/{version}/valheim.testing.game.{version}.nupkg";
        using var reply = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), cancellation);
        if (reply.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Valheim.Testing.Game {GameVersion} is not served by NuGet.org (HTTP {(int)reply.StatusCode}). Publish that Game API before creating a native smoke consumer; no source checkout will substitute for it.");
    }

    private static async Task RunDotnetAsync(string directory, string packages, string httpCache, CancellationToken cancellation,
        params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment["NUGET_PACKAGES"] = packages;
        start.Environment["NUGET_HTTP_CACHE_PATH"] = httpCache;
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The .NET SDK could not start; install .NET 10 before running this smoke.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellation);
        try { await process.WaitForExitAsync(cancellation); }
        catch (OperationCanceledException)
        {
            // This process belongs to this one project build. Never leave a child restore running after Ctrl+C.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        string output = (await stdout) + "\n" + (await stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The generated consumer did not {args[0]} from NuGet.org (exit {process.ExitCode}): {output.Trim()}");
        if (args[0] == "restore")
        {
            string metadata = Path.Combine(packages, "valheim.testing.game", GameVersion.ToLowerInvariant(), ".nupkg.metadata");
            if (!File.Exists(metadata) || !File.ReadAllText(metadata).Contains(Feed, StringComparison.Ordinal))
                throw new InvalidOperationException("The generated consumer did not restore Valheim.Testing.Game from NuGet.org; its source metadata is absent or different.");
        }
    }
}
