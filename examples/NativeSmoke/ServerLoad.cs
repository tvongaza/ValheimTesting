using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using valheimCLI;
using Valheim.Testing.Game;

/// <summary>The server half of #156: pinned selected mods on an owned dedicated server. A clean-client join follows.</summary>
internal static class ServerLoad
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryRead(args, out var options, out var mods, out var roots, out var optional, out string error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Usage: native-smoke server-load --server DIR --mod DLL [--mod DLL ...] --adapter DLL --cli-manifest FILE --cli-files DIR --output NEW_DIR [--search-root DIR ...] [--optional-reference ASSEMBLY ...] [--cli-port 5688] [--game-port 2486] [--expected-log-error EXACT_HEADER --expected-log-reason REASON]");
            return 2;
        }
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, press) => { press.Cancel = true; cancel.Cancel(); };
        var clock = Stopwatch.StartNew();
        try
        {
            string output = Path.GetFullPath(options!["--output"]);
            if (Path.Exists(output)) throw new IOException("--output must be new; existing evidence will not be overwritten: " + output);
            string server = Path.GetFullPath(options["--server"]);
            string adapter = Path.GetFullPath(options["--adapter"]);
            string cliManifest = Path.GetFullPath(options["--cli-manifest"]);
            string cliFiles = Path.GetFullPath(options["--cli-files"]);
            int cliPort = options.TryGetValue("--cli-port", out string? cliValue) ? int.Parse(cliValue, CultureInfo.InvariantCulture) : 5688;
            int gamePort = options.TryGetValue("--game-port", out string? gameValue) ? int.Parse(gameValue, CultureInfo.InvariantCulture) : 2486;
            if (Math.Abs(cliPort - gamePort) < 3) throw new ArgumentException("Choose a CLI port away from the game's three-port range.");
            foreach (string path in new[] { server, cliFiles })
                if (!Directory.Exists(path)) throw new DirectoryNotFoundException("A server or ValheimCLI directory is missing: " + path);
            foreach (string path in new[] { adapter, cliManifest }.Concat(mods!.Select(Path.GetFullPath)))
                if (!File.Exists(path)) throw new FileNotFoundException("A selected file is missing: " + path, path);
            var request = new NativeDependencyRequest
            {
                Mods = mods!.Select(Path.GetFullPath).ToList(), SearchRoots = roots!.Select(Path.GetFullPath).ToList(),
                GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(server))!,
                BepInExCore = Path.Combine(server, InstallPins.CoreDirectory),
                CliManifest = cliManifest, CliFiles = cliFiles,
                Capabilities = ["valheim.session/state"], OptionalReferences = [.. optional!],
            };
            var dependencies = NativeDependencyResolver.Resolve(request);
            Directory.CreateDirectory(output);
            dependencies.Write(Path.Combine(output, "dependencies.lock.json"));
            if (!dependencies.Ready)
                throw new InvalidDataException("Dependency choices remain: " + string.Join("; ", dependencies.Gaps.Select(gap => gap.Kind + " " + gap.Name + ": " + gap.Reason)));
            string world = Path.Combine(output, "world-source");
            DefaultSmokeWorld.PrepareServerSaveRoot(world);
            using var runtime = NativeServerRuntime.Prepare(server, Path.Combine(output, "staged-runtime"), dependencies, adapter, cliPort);
            var plan = runtime.Plan(world, cliPort, gamePort);
            if (options.TryGetValue("--expected-log-error", out string? expectedError))
                plan.LogScan[LogScanner.UnknownError] = new LogClassification
                { Expected = [expectedError], Reason = options["--expected-log-reason"] };
            string planFile = Path.Combine(output, "plan.json");
            File.WriteAllText(planFile, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
            int result = await PinnedServerRun.MainAsync(["run", planFile, Path.Combine(output, "evidence")],
                new PinnedServerRunOptions<ServerRunPlan>
                {
                    Name = "native-smoke-server-load",
                    ReadPlan = path =>
                    {
                        var read = ServerRunPlan.Read<ServerRunPlan>(path);
                        read.ValidateServerPlan(read.Pins.Keys.Where(key => key != "worlduid"), NativeServerRuntime.SessionTokenVariable);
                        return read;
                    },
                    SessionCapability = NativeServerRuntime.SessionCapability,
                    SessionTokenVariable = NativeServerRuntime.SessionTokenVariable,
                    EnableDevcommands = false,
                    Scenario = run =>
                    {
                        run.Report.Step("selected server mods loaded and world identity matches", () =>
                        {
                            var worlds = run.Server.Execute("cli_world").Output.Select(Expectations.ParseWorld).OfType<WorldFacts>().ToArray();
                            if (worlds.Length != 1 || worlds[0].Uid != DefaultSmokeWorld.Uid)
                                throw new InvalidDataException("The dedicated server did not load the packaged smoke world UID.");
                        });
                        run.Report.Step("dedicated server accepts a game connection", () =>
                            OwnedServerSession.WaitUntilJoinable(run.Server, NativeServerRuntime.SessionCapability,
                                TimeSpan.FromSeconds(run.Plan.StartupSeconds), run.Cancellation));
                        return Task.CompletedTask;
                    },
                });
            Console.WriteLine((result == 0 ? "SERVER_LOAD_PASS" : "SERVER_LOAD_FAIL") +
                $": {mods!.Count} selected mod(s), {clock.Elapsed.TotalSeconds:F1}s. A clean-client join has not run. Private evidence in {output}");
            return result;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or FormatException)
        {
            Console.Error.WriteLine("REFUSED: " + failure.Message);
            return 3;
        }
    }

    private static bool TryRead(string[] args, out Dictionary<string, string>? options, out List<string>? mods,
        out List<string>? roots, out List<string>? optional, out string error)
    {
        options = null; mods = null; roots = null; optional = null; error = "";
        var required = new HashSet<string>(StringComparer.Ordinal) { "--server", "--mod", "--adapter", "--cli-manifest", "--cli-files", "--output" };
        var allowed = new HashSet<string>(required, StringComparer.Ordinal)
        { "--cli-port", "--game-port", "--expected-log-error", "--expected-log-reason" };
        if (args.Length % 2 != 0) { error = "Every option needs one value."; return false; }
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var selected = new List<string>(); var searches = new List<string>(); var omissions = new List<string>();
        for (int i = 0; i < args.Length; i += 2)
        {
            string key = args[i], value = args[i + 1];
            if (string.IsNullOrWhiteSpace(value)) { error = "Empty option: " + key; return false; }
            if (key == "--mod") { selected.Add(value); found.TryAdd(key, value); }
            else if (key == "--search-root") searches.Add(value);
            else if (key == "--optional-reference") omissions.Add(value);
            else if (!allowed.Contains(key) || !found.TryAdd(key, value))
            { error = "Unknown or repeated option: " + key; return false; }
        }
        var missing = required.Where(key => !found.ContainsKey(key)).ToList();
        if (missing.Count != 0) { error = "Missing: " + string.Join(", ", missing); return false; }
        if (found.ContainsKey("--expected-log-error") != found.ContainsKey("--expected-log-reason"))
        { error = "An expected log error needs its full header and a written reason."; return false; }
        options = found; mods = selected; roots = searches; optional = omissions;
        return true;
    }
}
