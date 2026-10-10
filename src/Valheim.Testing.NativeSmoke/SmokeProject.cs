using System.Diagnostics;
using System.Security;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>
/// <c>valheim-test init</c>: an editable native consumer. Prove it builds from NuGet.org
/// (or CI's exact local candidate feed) before copying it into the output: a stale global NuGet cache cannot stand in.
/// <c>start</c> and <c>server-load</c> run their built-in scenarios from this tool's assemblies;
/// their optional <c>--project</c> and <c>--scenario-project</c> inputs are built separately.
/// </summary>
internal static class SmokeProject
{
    // The consumer pins the Valheim.Testing.Game this tool was built with and runs in process, so the regression.json or
    // plan.json the tool writes and the consumer's reader are one version by construction (one source: the Game assembly).
    internal static readonly string GameVersion = VersionOf(typeof(ClientSession));
    // The server consumer runs a session (PinnedServerRun), from the Valheim.Testing.GameSessions this tool was built with; that
    // package depends on exactly the Game above.
    internal static readonly string GameSessionsVersion = VersionOf(typeof(PinnedServerRun));
    private static string VersionOf(Type type) => type.Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "unknown";
    // Both generated consumers run sessions; each restores the exact Game and GameSessions assemblies used by this tool.
    private static IEnumerable<(string Package, string Version)> Restored(bool server) =>
        [("Valheim.Testing.Game", GameVersion), ("Valheim.Testing.GameSessions", GameSessionsVersion)];
    private static string? Refusal(bool server) => Restored(server).Select(item => Unpublishable(item.Version, item.Package)).FirstOrDefault(refusal => refusal != null);
    private const string Feed = "https://api.nuget.org/v3/index.json";

    /// <summary>Why a consumer cannot pin <paramref name="version"/>: a local candidate build or an unknown version is never on NuGet.org.</summary>
    internal static string? Unpublishable(string version, string package = "Valheim.Testing.Game") =>
        version == "unknown" ? $"This tool's {package} version is unknown, so a consumer cannot pin it."
        : version.Contains("-candidate", StringComparison.OrdinalIgnoreCase)
            ? $"This tool was built with the local candidate {package} {version}, which NuGet.org never serves. Use a released valheim-test to create a consumer."
            : null;

    /// <summary>The one line a run prints after its result: how to get a consumer for the file it wrote (none when init would refuse).</summary>
    internal static void PrintHint(bool server)
    {
        if (Refusal(server) == null)
            Console.WriteLine($"To extend this check with your own assertions: valheim-test init{(server ? " server" : "")} --output NEW_DIR (an editable consumer of {(server ? "the run's campaign.json and the plan.json beside it" : "the run's regression.json")}; needs NuGet.org).");
    }

    internal static async Task<int> InitAsync(string[] args)
    {
        bool server = args.Length > 0 && args[0] == "server";
        string[] rest = server ? args[1..] : args;
        string? candidateFeed = rest is ["--candidate-feed", var feed, "--output", _] ? Path.GetFullPath(feed) : null;
        string? path = rest is ["--output", var ordinary] ? ordinary
            : rest is ["--candidate-feed", _, "--output", var candidate] ? candidate : null;
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.Error.WriteLine("Usage: valheim-test init [server] [--candidate-feed LOCAL_PACKAGE_DIR] --output NEW_DIR");
            return 2;
        }
        string output = Path.GetFullPath(path);
        if (candidateFeed == null && Refusal(server) is { } refusal)
        {
            Console.Error.WriteLine("REFUSED: " + refusal);
            return 3;
        }
        if (candidateFeed != null && (!Directory.Exists(candidateFeed) || Restored(server).Any(item =>
                !item.Version.Contains("-candidate.", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(candidateFeed, $"{item.Package}.{item.Version}.nupkg")))))
        {
            Console.Error.WriteLine("REFUSED: --candidate-feed needs this tool's exact candidate Game and GameSessions packages in an existing local feed.");
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
            await CreateAsync(output, server, candidateFeed, cancel.Token);
            Console.WriteLine("READY: editable consumer in " + Path.Combine(output, "consumer") +
                "; Valheim.Testing.Game " + GameVersion + (server ? " and Valheim.Testing.GameSessions " + GameSessionsVersion : "") +
                (candidateFeed == null ? " restored and built from NuGet.org only." : " restored and built from the local candidate feed."));
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

    private static async Task CreateAsync(string output, bool server, string? candidateFeed, CancellationToken cancellation)
    {
        string stage = Path.Combine(Path.GetTempPath(), "valheimtesting-smoke-project-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stage);
            Write(stage, server, candidateFeed);
            string packages = Path.Combine(stage, "packages"), httpCache = Path.Combine(stage, "http-cache");
            Directory.CreateDirectory(packages);
            Directory.CreateDirectory(httpCache);
            await RunDotnetAsync(server, stage, packages, httpCache, candidateFeed, cancellation, "restore", "SmokeCheck.csproj", "--configfile", "NuGet.Config");
            await RunDotnetAsync(server, stage, packages, httpCache, candidateFeed, cancellation, "build", "SmokeCheck.csproj", "-c", "Release", "--no-restore");
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

    internal static void Write(string directory, bool server, string? candidateFeed = null)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SmokeCheck.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
              <ItemGroup><PackageReference Include="Valheim.Testing.Game" Version="[{{GameVersion}}]" /><PackageReference Include="Valheim.Testing.GameSessions" Version="[{{GameSessionsVersion}}]" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "NuGet.Config"), candidateFeed == null
            ? $"<configuration><packageSources><clear/><add key=\"nuget.org\" value=\"{Feed}\"/></packageSources></configuration>\n"
            : $"<configuration><packageSources><clear/><add key=\"local-preview\" value=\"{SecurityElement.Escape(candidateFeed)}\"/><add key=\"nuget.org\" value=\"{Feed}\"/></packageSources><packageSourceMapping><packageSource key=\"local-preview\"><package pattern=\"Valheim.Testing\"/><package pattern=\"Valheim.Testing.*\"/></packageSource><packageSource key=\"nuget.org\"><package pattern=\"*\"/></packageSource></packageSourceMapping></configuration>\n");
        File.WriteAllText(Path.Combine(directory, "Program.cs"), server ? $$"""
            using valheimCLI;
            using Valheim.Testing.Game;
            using Valheim.Testing.GameSessions;

            // Run this with the campaign.json valheim-test server-load wrote (plan.json and client-plan.json beside it).
            // Supply a new result directory for each campaign run.
            if (args is not [var runFile, var resultDirectory])
            {
                Console.Error.WriteLine("Usage: dotnet run -- RUN_OUTPUT/campaign.json NEW_RESULT_DIRECTORY");
                return 2;
            }
            var options = new PinnedServerRunOptions<ServerRunPlan>
            {
                Name = "my-native-server-check",
                ReadPlan = path =>
                {
                    var plan = ServerRunPlan.Read<ServerRunPlan>(path);
                    plan.ValidateServerPlan(plan.Pins.Keys.Where(key => key != "worlduid"), "{{SmokeSessionContract.SessionTokenVariable}}");
                    return plan;
                },
                Mod = new("{{SmokeSessionContract.SessionCapability}}", "{{SmokeSessionContract.SessionTokenVariable}}"),
                Scenario = (session, plan) =>
                {
                    var server = session.Server!.Game;
                    session.Report.Step("selected server mods loaded", () =>
                    {
                        var worlds = server.Execute("cli_world").Output.Select(Expectations.ParseWorld).OfType<WorldFacts>().ToArray();
                        if (worlds.Length != 1 || worlds[0].Uid != DefaultSmokeWorld.Uid)
                            throw new InvalidDataException("The dedicated server loaded a different fixture world.");
                    });
                    // Add your mod's focused observation and assertion here using session.Server.Game and session.Report.Step(...).
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
                int passwordIndex = serverPlan.Arguments.ToList().IndexOf("-password");
                if (passwordIndex < 0 || passwordIndex + 1 >= serverPlan.Arguments.Length)
                    throw new InvalidDataException("The server plan has no join password argument.");
                Environment.SetEnvironmentVariable("{{SmokeSessionContract.PasswordVariable}}", serverPlan.Arguments[passwordIndex + 1]);
            }
            return await PinnedServerRun.RunCampaignAsync(runFile, serverPlan, _ => clients, resultDirectory, options);
            """ : """
            using Valheim.Testing.Game;
            using Valheim.Testing.GameSessions;

            // Run this with the regression.json a `valheim-test start` run wrote beside its evidence (and its environments.json,
            // when --game overrode this machine's Valheim). Supply a fresh result directory for each run; the selected DLLs remain pinned.
            if (args is not [var inputsFile, var resultDirectory])
            {
                Console.Error.WriteLine("Usage: dotnet run -- regression.json NEW_RESULT_DIRECTORY");
                return 2;
            }
            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            var regression = TargetedRegression.Read(inputsFile);
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

            This project restores `Valheim.Testing.Game` {{GameVersion}}{{(server ? $" and `Valheim.Testing.GameSessions` {GameSessionsVersion}" : "")}} from NuGet.org only: the versions the
            `valheim-test` that created it runs, so it reads that tool's files. Add your mod-specific observations in
            `Program.cs`, then run from this directory with the {{(server ? "campaign" : "regression")}}.json a
            `valheim-test {{(server ? "server-load" : "start")}}` run wrote and a fresh result directory:

            ```sh
            dotnet run -c Release -- RUN_OUTPUT/{{(server ? "campaign" : "regression")}}.json RUN_OUTPUT/my-assertions-1
            ```

            Keep the fixture, installed DLL hashes and ValheimCLI pins fixed while investigating a regression.
            A passing load check means the selected plugins started, not that their gameplay is correct.
            {{(server ? "" : "The generated hosted-client consumer launches directly into this process's desktop session. On Windows, run it from an interactive desktop terminal; an SSH/session-0 invocation is refused before staging. Use `valheim-test start` for a Windows SSH launch because it preflights and uses the desktop route.")}}
            Do not publish the environment or evidence unchanged; they can contain private machine paths.
            """);
    }

    private static async Task RunDotnetAsync(bool server, string directory, string packages, string httpCache, string? candidateFeed, CancellationToken cancellation,
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
            throw new InvalidOperationException($"NuGet.org does not serve {string.Join(" and ", Restored(server).Select(item => item.Package + " " + item.Version))}, the {(server ? "versions" : "version")} this tool runs; a tool built from a source checkout ahead of the last release cannot create a consumer. Use a released valheim-test. ({output.Trim()})");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The generated consumer did not {args[0]} from {(candidateFeed == null ? "NuGet.org" : "the local candidate feed")} (exit {process.ExitCode}): {output.Trim()}");
        if (args[0] == "restore")
            foreach (var (package, version) in Restored(server)) RequireFromSource(packages, package, version, candidateFeed ?? Feed);
    }

    internal static void RequireFromSource(string packages, string package, string version, string source)
    {
        string metadata = Path.Combine(packages, package.ToLowerInvariant(), version.ToLowerInvariant(), ".nupkg.metadata");
        if (!File.Exists(metadata) || !MetadataSourceMatches(File.ReadAllText(metadata), source))
            throw new InvalidOperationException($"The generated consumer did not restore {package} from {source}; its source metadata is absent or different.");
    }

    private static bool MetadataSourceMatches(string metadata, string expected)
    {
        try
        {
            using var json = JsonDocument.Parse(metadata);
            if (!json.RootElement.TryGetProperty("source", out var property) || property.ValueKind != JsonValueKind.String)
                return false;
            string? actual = property.GetString();
            if (actual == null) return false;
            if (expected.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return string.Equals(actual, expected, StringComparison.Ordinal);
            return string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
