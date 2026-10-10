using System.Diagnostics;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

if (args is ["help" or "--help"])
{
    Console.WriteLine("valheim-test start [--mod DLL ...] [--output NEW_DIR] [--inventory FILE | --game DIR] [--client-env NAME] [--client-architecture x64|arm64] [setup options] (with no --mod, the current project's one built plugin)");
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
if (!StartArguments.TryRead(args, out var options, out var mods, out var roots, out var optionalReferences, out var error, allowImplicitMod: true))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine("Usage: valheim-test start [--mod DLL ...] [--output NEW_DIR] [--inventory FILE | --game DIR] [--client-env NAME] [--client-architecture x64|arm64] [--join-seconds 10..900] [--hold] [--source COMMIT] [--cli-manifest FILE --cli-files DIR] [--compare-mod DLL --compare-source COMMIT] [--search-root DIR ...] [--optional-reference ASSEMBLY ...] [--client-loader-package FILE] [--expected-log-error EXACT_HEADER --expected-log-reason REASON]");
    return 2;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, press) => { press.Cancel = true; cancel.Cancel(); };
bool holdRequested = options!.ContainsKey("--hold");
string runId = RunJournal.NewRunId();
using var runJournal = RunJournal.UseRun(runId);
var elapsed = Stopwatch.StartNew();
TargetedRegression? runner = null;
ScenarioReport? lastArm = null; string? lastArmOutput = null;
int exitCode = 3;
string? outcome = null;
bool journalClean = true;
bool characterPending = false;
string? output = null;
bool outputChecked = false;
try
{
    output = SmokeCommandOptions.Output(options!);
    var modSelection = SmokeModInput.Select(mods!, Environment.CurrentDirectory);
    var selectedMods = modSelection.Mods.ToList();
    Console.WriteLine("mod selection: " + modSelection.Reason);
    // The client: the inventory's (this machine's Valheim with no --inventory); --game overrides the install and --client-loader-package the loader.
    var (inventory, client, shippedLoader) = SmokeInputs.Client(options, output, ShippedLoader.Instead,
        recordSelection: false);
    string architecture = SmokeInputResolver.SelectClientArchitecture(options.GetValueOrDefault("--client-architecture"), client, inventory);
    SmokeInputResolver.RequireClientArchitecture(inventory, client, architecture, client.LoaderPackage);
    var localProblems = await LocalHostPreflight.InspectAsync(inventory,
        [new LocalHostPreflight.Actor("client", client)], TimeSpan.FromSeconds(60), cancel.Token);
    if (localProblems.Count != 0)
        throw new InvalidOperationException(string.Join("; ", localProblems.Select(problem =>
            $"{problem.Actor} {problem.Input}: {problem.Message}")));
    foreach (string line in inventory.Detected) Console.WriteLine("detected: " + line);
    Console.WriteLine($"client: {client.Name} on {client.Host}: install {client.Install}; ValheimCLI port {client.CliPort}; architecture {architecture}");
    string game = client.Install;
    string mod = selectedMods[0];
    var (cliManifest, cliFiles) = SmokeInputs.Cli(options);
    string? loader = client.LoaderPackage;
    foreach (var (name, path) in new[] { ("game", game), ("--cli-files", cliFiles) })
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(name + " directory does not exist: " + path);
    if (loader != null && !File.Exists(loader)) throw new FileNotFoundException("--client-loader-package file does not exist: " + loader, loader);
    var loaderPackage = loader == null ? null : BepInExLoaderPackage.Read(loader);
    SmokeOutput.RefuseResolved(output, cliFiles, inventory, [client], loaderPackage?.Root);
    outputChecked = true;
    SmokeInputs.RecordClient(inventory, client, output);
    foreach (var (name, path) in selectedMods.Select(path => ("--mod", path)).Append(("--cli-manifest", cliManifest)))
        if (!File.Exists(path)) throw new FileNotFoundException(name + " file does not exist: " + path, path);
    string core = Path.Combine(loaderPackage?.Root ?? game, InstallPins.CoreDirectory);
    var request = SmokeDependencyInputs.Request(selectedMods, game, core, cliManifest, cliFiles,
        roots!, optionalReferences!, CliCapabilities.HostedRounds);
    var dependencies = NativeDependencyResolver.Resolve(request);
    Directory.CreateDirectory(output);
    dependencies.Write(Path.Combine(output, "dependencies.lock.json"));
    if (!dependencies.Ready)
        throw new InvalidDataException("Dependency choices remain: " + SmokeDependencyInputs.Gaps(dependencies));
    NativeDependencyLock? comparison = null;
    string? compareMod = null;
    if (options.TryGetValue("--compare-mod", out string? compareFile))
    {
        compareMod = Path.GetFullPath(compareFile);
        if (!File.Exists(compareMod)) throw new FileNotFoundException("--compare-mod file does not exist: " + compareMod, compareMod);
        var compareRequest = SmokeDependencyInputs.Request([compareMod, .. selectedMods.Skip(1)], game, core,
            cliManifest, cliFiles, request.SearchRoots, request.OptionalReferences, request.Capabilities);
        comparison = NativeDependencyResolver.Resolve(compareRequest);
        comparison.Write(Path.Combine(output, "comparison-dependencies.lock.json"));
        if (!comparison.Ready)
            throw new InvalidDataException("Comparison dependency choices remain: " + SmokeDependencyInputs.Gaps(comparison));
        dependencies.RequireSameFixedInputs(comparison);
    }

    var world = DefaultSmokeWorld.Prepare(Path.Combine(output, "world-source"));
    var character = DefaultSmokeCharacter.Prepare(Path.Combine(output, "character-source"));
    var inputs = new RegressionInputs
    {
        // A name per run: its shared hosted-runtime copy is never shared with another start.
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
    Console.WriteLine("run ID: " + runId);
    bool passed = true;
    foreach (string arm in inputs.Mod.Arms.Keys)
    {
        string armOutput = Path.Combine(output, "evidence", arm);
        LocalClientJournal? processJournal = null;
        var report = runner.Run(arm, armOutput, "selected plugin loads in a hosted fixture",
            ["first"], round =>
            {
                if (holdRequested && arm == inputs.Mod.Arms.Keys.Last())
                    round.Step("keep the owned client running until finish", () =>
                        ForegroundHold.HoldAsync(runId, armOutput, cancel.Token,
                            [("client", processJournal?.Process ?? throw new InvalidOperationException("The owned client process was not recorded."))])
                            .GetAwaiter().GetResult());
            }, cancel.Token, afterPinnedClientOpened: ready =>
            {
                ready.Provenance["runId"] = runId;
                ready.Provenance["firstModLoadedSecondsFromCommand"] =
                    elapsed.Elapsed.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                ready.Provenance["modSelection"] = modSelection.Reason;
                SmokeInputResolver.RecordLoader(ready.Provenance, "client", loader, shippedLoader);
            }, openClient: (plan, directory, logs, token) =>
                {
                    bool desktopTask = OperatingSystem.IsWindows() &&
                        DesktopClientSession.NeedsDesktopTask(Process.GetCurrentProcess().SessionId);
                    processJournal = new LocalClientJournal(plan, directory, desktopTask);
                    return OperatingSystem.IsWindows()
                        ? DesktopClientSession.Open(plan, directory, logs, token, processJournal.Begin, processJournal.Started)
                        : ClientSession.Open(plan, directory, logs, token, processJournal.Begin, processJournal.Started);
                }, afterStaged: null, characterJournal: (point, characters, userData, name, expectedSha256) =>
                {
                    var entry = point switch
                    {
                        CharacterStageEvent.Intended => JournalEntry.Of(JournalEntry.CharacterIntended,
                            ("characters", characters), ("userData", userData), ("fileName", name),
                            ("characterKind", "regression"), ("local", "true"), ("expectedSha256", expectedSha256)),
                        CharacterStageEvent.Done => JournalEntry.Of(JournalEntry.CharacterDone,
                            ("fileName", name), ("staged", "true")),
                        _ => JournalEntry.Of(JournalEntry.CharacterRetired, ("fileName", name)),
                    };
                    RunJournal.ThisProcess.AppendLocal("client", entry);
                    if (point == CharacterStageEvent.Intended) characterPending = true;
                    if (point == CharacterStageEvent.Retired) characterPending = false;
                });
        if (processJournal != null)
            try { processJournal.Complete(); }
            catch (Exception failure)
            {
                journalClean = false;
                report.RecordFailure(StepPhase.Cleanup, "owned client process was not proved stopped", failure);
                report.Write(armOutput);
            }
        // Even a setup failure before the client opens has a journalled run ID in its result.
        report.Provenance["runId"] = runId;
        report.Write(armOutput);
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
    if (outputChecked) SmokeOutput.MarkRefused(output, "start", [failure.Message]);
}
finally
{
    // A thrown runner error must not claim cleanup was verified if the character never retired.
    if (characterPending) journalClean = false;
    if (runner != null && !journalClean)
    {
        Console.Error.WriteLine("CLEANUP REFUSED: the owned client stop or staged character cleanup is unproven; the disposable install stays at " + runner.Install + ". Inspect valheim-test env status before recovery.");
        if (lastArm != null && lastArmOutput != null)
        {
            lastArm.Provenance["disposableInstall"] = "kept: owned client or character cleanup unproven";
            lastArm.Write(lastArmOutput);
        }
        exitCode = 1;
    }
    else if (runner != null)
        // With a run, its last arm's result.json records the removal as its Cleanup step; before any run there is no report.
        try
        {
            if (lastArm != null) runner.Remove(lastArm, lastArmOutput!); else runner.Remove();
        }
        catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            journalClean = false;
            // Recovery follows the hosted runtime's journal and ownership rules; never delete a copy by name.
            Console.Error.WriteLine("CLEANUP REFUSED: " + cleanup.Message + "; inspect the disposable install at " + runner.Install);
            if (exitCode == 0) exitCode = 1;
        }
    if (journalClean)
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
    public static bool TryRead(string[] args, out Dictionary<string, string>? result, out List<string>? mods,
        out List<string>? roots, out List<string>? optionalReferences, out string error, bool allowImplicitMod = false)
    {
        result = null; mods = null; roots = null; optionalReferences = null;
        if (!SmokeCommandOptions.TryRead(args, SmokeCommandOptions.Command.Start, allowImplicitMod, out var parsed, out error))
            return false;
        result = parsed!.Options;
        foreach (string flag in parsed.Switches) result[flag] = "true";
        mods = parsed.List("--mod");
        roots = parsed.List("--search-root");
        optionalReferences = parsed.List("--optional-reference");
        return true;
    }
}
