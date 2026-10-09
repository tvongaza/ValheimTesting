using System.Diagnostics;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

if (args is ["help" or "--help"])
{
    Console.WriteLine("valheim-test start --mod DLL [--mod DLL ...] --output NEW_DIR [--inventory FILE | --game DIR] [--client-env NAME] [--client-architecture x64|arm64] [setup options] (the inventory's client; this machine's Valheim with no --inventory)");
    Console.WriteLine(ServerLoad.Usage + " (a server and one clean client from the inventory; this machine when no --inventory)");
    Console.WriteLine("valheim-test server-load-ab --mod DLL --mod DLL --remove-mod DLL --output NEW_DIR [server-load options except --hold and --preflight-only]");
    Console.WriteLine("valheim-test init [server] --output NEW_DIR (editable NuGet.org-only consumer)");
    Console.WriteLine(EnvCommand.Usage + " (list, preflight and status read only; recover and teardown clear what a run left)");
    Console.WriteLine(ForegroundHold.FinishUsage + " (asks the live owner of a held run to finish and clean up)");
    Console.WriteLine(OwnedCliCommand.Usage + " (one strictly pinned command to a running owned Windows client)");
    Console.WriteLine(SessionCommand.Usage + " (read only; --hosts adds the host checks)");
    return 0;
}
if (args.Length != 0 && args[0] == "init") return await SmokeProject.InitAsync(args[1..]);
if (args.Length != 0 && args[0] == "env") return await EnvCommand.RunAsync(args[1..]);
if (args.Length != 0 && args[0] == "finish") return ForegroundHold.FinishCommand(args[1..]);
if (args.Length != 0 && args[0] == "cli") return OwnedCliCommand.Run(args[1..]);
if (args.Length != 0 && args[0] == "session") return await SessionCommand.RunAsync(args[1..]);
if (args.Length != 0 && args[0] == "start") args = args[1..];
if (args.Length != 0 && args[0] is "server-load" or "server-load-ab")
{
    int result = args[0] == "server-load" ? await ServerLoad.RunAsync(args[1..]) : await ServerLoadComparison.RunAsync(args[1..]);
    if (result is 0 or 1) SmokeProject.PrintHint(server: true); // a run happened (2: usage, 3: refused)
    return result;
}

// The default path hosts a world in an owned client; server-load uses an owned dedicated server.
if (!StartArguments.TryRead(args, out var options, out var mods, out var roots, out var optionalReferences, out var error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine("Usage: valheim-test start --mod DLL [--mod DLL ...] --output NEW_DIR [--inventory FILE | --game DIR] [--client-env NAME] [--client-architecture x64|arm64] [--join-seconds 10..900] [--hold] [--source COMMIT] [--cli-manifest FILE --cli-files DIR] [--compare-mod DLL --compare-source COMMIT] [--search-root DIR ...] [--optional-reference ASSEMBLY ...] [--loader-package FILE] [--expected-log-error EXACT_HEADER --expected-log-reason REASON]");
    return 2;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, press) => { press.Cancel = true; cancel.Cancel(); };
bool holdRequested = options!.ContainsKey("--hold");
string? holdRunId = holdRequested ? RunJournal.NewRunId() : null;
using var holdJournal = holdRunId == null ? null : RunJournal.UseRun(holdRunId);
var elapsed = Stopwatch.StartNew();
TargetedRegression? runner = null;
ScenarioReport? lastArm = null; string? lastArmOutput = null;
int exitCode = 3;
string? outcome = null;
bool journalClean = true;
bool copyJournalled = false;
bool copyDone = false;
try
{
    string output = Path.GetFullPath(options!["--output"]);
    if (Path.Exists(output)) throw new IOException("--output must be a new directory; an earlier run or personal files will not be changed: " + output);
    // The client: the inventory's (this machine's Valheim with no --inventory); --game and --loader-package override it.
    var (inventory, client, shippedLoader) = SmokeInputs.Client(options, output, ShippedLoader.Instead);
    string architecture = ClientArchitectureChoice.Select(options.GetValueOrDefault("--client-architecture"), client);
    ClientArchitectureChoice.RequireLocal(inventory, client, architecture, client.LoaderPackage);
    foreach (string line in inventory.Detected) Console.WriteLine("detected: " + line);
    Console.WriteLine($"client: {client.Name} on {client.Host}: install {client.Install}; ValheimCLI port {client.CliPort}; architecture {architecture}");
    // An SSH-launched Windows runner is in session 0. Check the desktop before copying the fixture or disposable game;
    // DesktopClientSession repeats the check and starts the client in that session after staging.
    if (OperatingSystem.IsWindows())
        await DesktopClientSession.PreflightAsync(cancel.Token);
    string game = client.Install;
    var selectedMods = mods!.Select(Path.GetFullPath).ToList();
    string mod = selectedMods[0];
    var (cliManifest, cliFiles) = SmokeInputs.Cli(options);
    string? loader = client.LoaderPackage;
    foreach (var (name, path) in new[] { ("game", game), ("--cli-files", cliFiles) })
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(name + " directory does not exist: " + path);
    SmokeOutput.RefuseInside(output, new[] { game, cliFiles, inventory.SteamUserData }.OfType<string>().ToArray());
    foreach (var (name, path) in selectedMods.Select(path => ("--mod", path)).Append(("--cli-manifest", cliManifest)))
        if (!File.Exists(path)) throw new FileNotFoundException(name + " file does not exist: " + path, path);
    if (loader != null && !File.Exists(loader)) throw new FileNotFoundException("--loader-package file does not exist: " + loader, loader);
    string core = loader == null ? Path.Combine(game, InstallPins.CoreDirectory)
        : Path.Combine(BepInExLoaderPackage.Read(loader).Root, InstallPins.CoreDirectory);
    var request = new NativeDependencyRequest
    {
        Mods = selectedMods, GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(game))!,
        BepInExCore = core, CliManifest = cliManifest, CliFiles = cliFiles,
        SearchRoots = roots!.Select(Path.GetFullPath).ToList(),
        Capabilities = [.. CliCapabilities.HostedRounds],
        OptionalReferences = [.. optionalReferences!],
    };
    var dependencies = NativeDependencyResolver.Resolve(request);
    Directory.CreateDirectory(output);
    dependencies.Write(Path.Combine(output, "dependencies.lock.json"));
    if (!dependencies.Ready)
        throw new InvalidDataException("Dependency choices remain: " + string.Join("; ", dependencies.Gaps.Select(gap => gap.Kind + " " + gap.Name + ": " + gap.Reason)));
    NativeDependencyLock? comparison = null;
    string? compareMod = null;
    if (options.TryGetValue("--compare-mod", out string? compareFile))
    {
        compareMod = Path.GetFullPath(compareFile);
        if (!File.Exists(compareMod)) throw new FileNotFoundException("--compare-mod file does not exist: " + compareMod, compareMod);
        var compareRequest = new NativeDependencyRequest
        {
            Mods = [compareMod, .. selectedMods.Skip(1)], GameManaged = request.GameManaged,
            BepInExCore = request.BepInExCore, CliManifest = cliManifest, CliFiles = cliFiles,
            SearchRoots = [.. request.SearchRoots], Capabilities = [.. request.Capabilities],
            OptionalReferences = [.. request.OptionalReferences],
        };
        comparison = NativeDependencyResolver.Resolve(compareRequest);
        comparison.Write(Path.Combine(output, "comparison-dependencies.lock.json"));
        if (!comparison.Ready)
            throw new InvalidDataException("Comparison dependency choices remain: " + string.Join("; ", comparison.Gaps.Select(gap => gap.Kind + " " + gap.Name + ": " + gap.Reason)));
        dependencies.RequireSameFixedInputs(comparison);
    }

    var world = DefaultSmokeWorld.Prepare(Path.Combine(output, "world-source"));
    var character = DefaultSmokeCharacter.Prepare(Path.Combine(output, "character-source"));
    var inputs = new RegressionInputs
    {
        // A name per run: its disposable install is <runtime>/regression-<name>, never shared with another start.
        Name = "native-smoke-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture),
        Fixture = new RegressionFixture { Root = Path.Combine(output, "world-source"), WorldUid = world.UidText },
        Client = new RegressionClient { Character = DefaultSmokeCharacter.Name, CharacterStore = character.Root, Architecture = architecture },
        Mod = new RegressionMod { InstallAs = Path.GetFileName(mod), Arms = new Dictionary<string, RegressionArm>
            { [comparison == null ? "smoke" : "before"] = new() { File = mod, Sha256 = FileHash.Sha256(mod),
                // Only a real source commit; the artifact's own SHA-256 is already the arm's Sha256.
                Commit = options.GetValueOrDefault("--source") } } },
    };
    if (options.TryGetValue("--join-seconds", out string? joinSeconds))
        inputs.Client.JoinSeconds = int.Parse(joinSeconds, System.Globalization.CultureInfo.InvariantCulture);
    if (options.TryGetValue("--expected-log-error", out string? expectedError))
        inputs.LogScan[LogScanner.UnknownError] = new LogClassification
        { Expected = [expectedError], Reason = options["--expected-log-reason"] };
    dependencies.ApplyTo(inputs, Path.Combine(output, "cli-capabilities.json"));
    // TargetedRegression calls one build its arm; other deliberately selected mods are fixed plugins in that same arm.
    // The resolver checks their combined dependency closure, duplicate GUIDs and declared incompatibilities first.
    inputs.Plugins.AddRange(dependencies.Mods.Skip(1).Select(file => new RegressionFile { File = file.File, Sha256 = file.Sha256 }));
    if (comparison != null)
        inputs.Mod.Arms.Add("after", new RegressionArm
        { File = compareMod!, Sha256 = FileHash.Sha256(compareMod!), Commit = options["--compare-source"] });
    inputs.Write(Path.Combine(output, "regression.json"));
    runner = TargetedRegression.Read(Path.Combine(output, "regression.json")); // the inputs on the machine recorded beside them
    Console.WriteLine($"disposable install: {runner.Install}");
    if (holdRunId != null)
    {
        if (Directory.Exists(runner.Install)) throw new IOException("The disposable install already exists: " + runner.Install);
        RunJournal.ThisProcess.AppendLocal(WorldFixture.Actor, JournalEntry.Of(JournalEntry.CopyIntended,
            ("runtime", runner.Install), ("local", "true"), ("copyKind", "regression"), ("evidenceRoot", output)));
        copyJournalled = true;
    }
    bool passed = true;
    foreach (string arm in inputs.Mod.Arms.Keys)
    {
        string armOutput = Path.Combine(output, "evidence", arm);
        LocalClientJournal? processJournal = null;
        var report = runner.Run(arm, armOutput, "selected plugin loads in a hosted fixture",
            ["first"], round =>
            {
                if (holdRunId != null && arm == inputs.Mod.Arms.Keys.Last())
                    round.Step("keep the owned client running until finish", () =>
                        ForegroundHold.HoldAsync(holdRunId, armOutput, cancel.Token,
                            [("client", processJournal?.Process ?? throw new InvalidOperationException("The owned client process was not recorded."))])
                            .GetAwaiter().GetResult());
            }, cancel.Token, afterPinnedClientOpened: ready =>
            {
                if (holdRunId != null) ready.Provenance["runId"] = holdRunId;
                ready.Provenance["firstModLoadedSecondsFromCommand"] =
                    elapsed.Elapsed.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                if (shippedLoader != null) ready.Provenance["bepInExPackageShipped"] = shippedLoader.Reason;
            }, openClient: holdRunId == null ? (OperatingSystem.IsWindows() ? DesktopClientSession.Open : null)
                : (plan, directory, logs, token) =>
                {
                    bool desktopTask = OperatingSystem.IsWindows() &&
                        DesktopClientSession.NeedsDesktopTask(Process.GetCurrentProcess().SessionId);
                    processJournal = new LocalClientJournal(plan, directory, desktopTask);
                    return OperatingSystem.IsWindows()
                        ? DesktopClientSession.Open(plan, directory, logs, token, processJournal.Begin, processJournal.Started)
                        : ClientSession.Open(plan, directory, logs, token, processJournal.Begin, processJournal.Started);
                }, afterStaged: () =>
                {
                    if (!copyJournalled || copyDone) return;
                    RunJournal.ThisProcess.AppendLocal(WorldFixture.Actor, JournalEntry.Of(JournalEntry.CopyDone,
                        ("runtime", runner.Install), ("local", "true"), ("copyKind", "regression")));
                    copyDone = true;
                });
        if (processJournal != null)
            try { processJournal.Complete(); }
            catch (Exception failure)
            {
                journalClean = false;
                report.RecordFailure(StepPhase.Cleanup, "owned client process was not proved stopped", failure);
                report.Write(armOutput);
            }
        lastArm = report; lastArmOutput = armOutput; // Only an arm whose report was written records the removal.
        passed &= report.Passed;
        // A failed arm's evidence is enough to diagnose it; do not silently call an A/B comparison complete.
        if (!report.Passed) break;
    }
    exitCode = passed ? 0 : 1;
    outcome = $": hosted fixture, {selectedMods.Count} selected mod(s), {inputs.Mod.Arms.Count} arm(s); private evidence in {output}";
}
catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or FormatException or OperationCanceledException)
{
    Console.Error.WriteLine("REFUSED: " + failure.Message);
}
finally
{
    if (runner != null && !journalClean)
    {
        Console.Error.WriteLine("CLEANUP REFUSED: the owned client stop is unproven; the disposable install stays at " + runner.Install + ". Inspect valheim-test env status before recovery.");
        if (lastArm != null && lastArmOutput != null)
        {
            lastArm.Provenance["disposableInstall"] = "kept: owned client stop unproven";
            lastArm.Write(lastArmOutput);
        }
        exitCode = 1;
    }
    else if (runner != null)
        // With a run, its last arm's result.json records the removal as its Cleanup step; before any run there is no report.
        try
        {
            if (lastArm != null) runner.Remove(lastArm, lastArmOutput!); else runner.Remove();
            if (copyJournalled)
                RunJournal.ThisProcess.AppendLocal(WorldFixture.Actor, JournalEntry.Of(JournalEntry.CopyRetired,
                    ("runtime", runner.Install), ("local", "true")));
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            journalClean = false;
            // A partially copied install may not yet have its ownership marker. Never delete it by path alone.
            Console.Error.WriteLine("CLEANUP REFUSED: " + cleanup.Message + "; inspect the disposable install at " + runner.Install);
            if (exitCode == 0) exitCode = 1;
        }
    if (holdRunId != null && journalClean)
        try
        {
            RunJournal.ThisProcess.AppendLocal("run", JournalEntry.Of(JournalEntry.RunEnded,
                ("state", exitCode == 0 ? "passed" : "failed"), ("cleanupVerified", "true")));
        }
        catch (Exception journalError) when (journalError is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("CLEANUP UNVERIFIED: the run ended but its journal could not record that: " + journalError.Message);
            exitCode = 1;
        }
}
if (outcome != null)
{
    Console.WriteLine((exitCode == 0 ? "PASS" : "FAIL") + outcome + $"; {elapsed.Elapsed.TotalSeconds:F1}s");
    SmokeProject.PrintHint(server: false);
}
return exitCode;

internal static class StartArguments
{
    private static readonly HashSet<string> Required = ["--mod", "--output"];
    private static readonly HashSet<string> Allowed = [.. Required, "--game", "--inventory", "--client-env", "--client-architecture", "--join-seconds", "--source", "--cli-manifest", "--cli-files", "--loader-package", "--expected-log-error", "--expected-log-reason", "--compare-mod", "--compare-source"];

    public static bool TryRead(string[] args, out Dictionary<string, string>? result, out List<string>? mods,
        out List<string>? roots, out List<string>? optionalReferences, out string error)
    {
        result = null;
        mods = null;
        roots = null;
        optionalReferences = null;
        error = "";
        int holds = args.Count(arg => arg == "--hold");
        if (holds > 1) { error = "Repeated option: --hold"; return false; }
        args = args.Where(arg => arg != "--hold").ToArray();
        if (args.Length % 2 != 0) { error = "Every option needs one value."; return false; }
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (holds == 1) found.Add("--hold", "true");
        var selected = new List<string>();
        var searchRoots = new List<string>();
        var optional = new List<string>();
        for (int i = 0; i < args.Length; i += 2)
        {
            if (args[i] == "--mod" && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                selected.Add(args[i + 1]);
                found.TryAdd("--mod", args[i + 1]);
                continue;
            }
            if (args[i] == "--search-root" || args[i] == "--optional-reference")
            {
                if (string.IsNullOrWhiteSpace(args[i + 1])) { error = "Empty option: " + args[i]; return false; }
                (args[i] == "--search-root" ? searchRoots : optional).Add(args[i + 1]);
                continue;
            }
            if (!Allowed.Contains(args[i]) || !found.TryAdd(args[i], args[i + 1]) || string.IsNullOrWhiteSpace(args[i + 1]))
            { error = "Unknown, repeated or empty option: " + args[i]; return false; }
        }
        var missing = Required.Where(key => !found.ContainsKey(key)).ToList();
        if (missing.Count != 0) { error = "Missing: " + string.Join(", ", missing); return false; }
        if (found.ContainsKey("--expected-log-error") != found.ContainsKey("--expected-log-reason"))
        { error = "--expected-log-error needs --expected-log-reason (and vice versa); an unexplained error is never ignored."; return false; }
        if (found.ContainsKey("--compare-mod") != found.ContainsKey("--compare-source"))
        { error = "--compare-mod needs --compare-source (and vice versa); both builds need provenance."; return false; }
        if (found.TryGetValue("--join-seconds", out string? joinSeconds) &&
            (!int.TryParse(joinSeconds, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int seconds) || seconds is < 10 or > 900))
        { error = "--join-seconds must be a whole number from 10 to 900."; return false; }
        if (found.TryGetValue("--client-architecture", out string? architecture) && architecture is not ("x64" or "arm64"))
        { error = "--client-architecture must be x64 or arm64."; return false; }
        result = found;
        mods = selected;
        roots = searchRoots;
        optionalReferences = optional;
        return true;
    }
}
