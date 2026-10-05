using System.Diagnostics;
using Valheim.Testing.Game;

/// <summary>
/// <c>valheim-test init</c>: an editable native scenario, the only thing this tool builds. Prove it builds from NuGet.org
/// alone before copying it into the output: a source checkout or a stale global NuGet cache cannot stand in for a release.
/// <c>start</c> and <c>server-load</c> never build it; they run from this tool's own assemblies, offline.
/// </summary>
internal static class SmokeProject
{
    // The consumer pins the Valheim.Testing.Game this tool was built with and runs in process, so the environment.json or
    // plan.json the tool writes and the consumer's reader are one version by construction (one source: the Game assembly).
    internal static readonly string GameVersion = typeof(TargetedRegression).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "unknown";
    private const string Feed = "https://api.nuget.org/v3/index.json";

    /// <summary>Why a consumer cannot pin <paramref name="version"/>: a local candidate build or an unknown version is never on NuGet.org.</summary>
    internal static string? Unpublishable(string version) =>
        version == "unknown" ? "This tool's Valheim.Testing.Game version is unknown, so a consumer cannot pin it."
        : version.Contains("-candidate", StringComparison.OrdinalIgnoreCase)
            ? $"This tool was built with the local candidate Valheim.Testing.Game {version}, which NuGet.org never serves. Use a released valheim-test to create a consumer."
            : null;

    /// <summary>The one line a run prints after its result: how to get a consumer for the file it wrote (none when init would refuse).</summary>
    internal static void PrintHint(bool server)
    {
        if (Unpublishable(GameVersion) == null)
            Console.WriteLine($"To extend this check with your own assertions: valheim-test init{(server ? " server" : "")} --output NEW_DIR (an editable consumer of {(server ? "plan" : "environment")}.json; needs NuGet.org).");
    }

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
        if (Unpublishable(GameVersion) is { } refusal)
        {
            Console.Error.WriteLine("REFUSED: " + refusal);
            return 3;
        }
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
        catch (Exception failure) when (failure is IOException or InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            Console.Error.WriteLine("REFUSED: " + failure.Message);
            if (Directory.Exists(output) && !Directory.EnumerateFileSystemEntries(output).Any()) Directory.Delete(output);
            return 3;
        }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    private static async Task CreateAsync(string output, bool server, CancellationToken cancellation)
    {
        string stage = Path.Combine(Path.GetTempPath(), "valheimtesting-smoke-project-" + Guid.NewGuid().ToString("N"));
        try
        {
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

            // Run this with the campaign.json valheim-test server-load wrote (plan.json and client-plan.json beside it), or on a
            // Mac with its plan.json. Supply a new result directory for each run.
            if (args is not [var runFile, var resultDirectory])
            {
                Console.Error.WriteLine("Usage: dotnet run -- RUN_OUTPUT/campaign.json NEW_RESULT_DIRECTORY (on a Mac: RUN_OUTPUT/plan.json)");
                return 2;
            }
            var options = new PinnedServerRunOptions<ServerRunPlan>
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
                TestAccess = false,
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
            };
            if (Path.GetFileName(runFile) != "campaign.json") return await PinnedServerRun.MainAsync(["run", runFile, resultDirectory], options);
            // The campaign's server plan (and its clean client's), unbound: the run binds them to the prepared actors again.
            string directory = Path.GetDirectoryName(Path.GetFullPath(runFile))!;
            var serverPlan = ServerRunPlan.Read<ServerRunPlan>(Path.Combine(directory, "plan.json"));
            var clients = new Dictionary<string, ClientRunPlan>();
            string clientFile = Path.Combine(directory, "client-plan.json");
            if (File.Exists(clientFile))
            {
                clients["client"] = System.Text.Json.JsonSerializer.Deserialize<ClientRunPlan>(File.ReadAllText(clientFile))!;
                // The clean client joins with the server's password, passed only through the environment.
                Environment.SetEnvironmentVariable(NativeCleanClientRuntime.PasswordVariable, serverPlan.Arguments[serverPlan.Arguments.IndexOf("-password") + 1]);
            }
            return await PinnedServerRun.RunCampaignAsync(runFile, serverPlan, _ => clients, resultDirectory, options);
            """ : """
            using Valheim.Testing.Game;

            // Run this with the environment.json a `valheim-test start` run wrote beside its evidence.
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

            This project restores `Valheim.Testing.Game` {{GameVersion}} from NuGet.org only: the version the
            `valheim-test` that created it runs, so it reads that tool's files. Add your mod-specific observations in
            `Program.cs`, then run from this directory with the {{(server ? "campaign" : "environment")}}.json a
            `valheim-test {{(server ? "server-load" : "start")}}` run wrote and a fresh result directory{{(server ? " (on a Mac, its plan.json)" : "")}}:

            ```sh
            dotnet run -c Release -- RUN_OUTPUT/{{(server ? "campaign" : "environment")}}.json RUN_OUTPUT/my-assertions-1
            ```

            Keep the fixture, installed DLL hashes and ValheimCLI pins fixed while investigating a regression.
            A passing load check means the selected plugins started, not that their gameplay is correct.
            Do not publish the environment or evidence unchanged; they can contain private machine paths.
            """);
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
        // NU1101-NU1103: the version is not on NuGet.org, as for a tool built from a checkout ahead of the last release.
        if (process.ExitCode != 0 && args[0] == "restore" && System.Text.RegularExpressions.Regex.IsMatch(output, @"\bNU110[123]\b"))
            throw new InvalidOperationException($"NuGet.org does not serve Valheim.Testing.Game {GameVersion}, the version this tool runs; a tool built from a source checkout ahead of the last release cannot create a consumer. Use a released valheim-test. ({output.Trim()})");
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
