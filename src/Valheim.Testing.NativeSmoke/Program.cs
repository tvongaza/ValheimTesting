using System.Diagnostics;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

if (ToolCommandLine.TryAnswer(args, Console.Out, out int answered)) return answered;
if (args.Length != 0 && args[0] == "init") return await SmokeProject.InitAsync(args[1..]);
if (args.Length != 0 && args[0] == "build") return await ModBuild.RunAsync(args[1..]);
if (args.Length != 0 && args[0] == "env") return await EnvCommand.RunAsync(args[1..]);
if (args.Length != 0 && args[0] == "finish") return ForegroundHold.FinishCommand(args[1..]);
if (args.Length != 0 && args[0] == "detach") return await DetachedSession.RunAsync(args[1..]);
if (args.Length != 0 && args[0] == "cli") return OwnedCliCommand.Run(args[1..]);
if (args.Length != 0 && args[0] == "save-world") return WorldSave.Run(args[1..]);
if (args.Length != 0 && args[0] == "session") return await SessionCommand.RunAsync(args[1..]);
if (args.Length != 0 && args[0] == "steam-runtime") return MacSteamRuntimeCommand.Run(args[1..]);
if (args.Length != 0 && args[0] == "server-load-phases") return await ServerLoadPhases.RunAsync(args[1..]);
if (args.Length != 0 && args[0] == "start") args = args[1..];
if (args.Length != 0 && args[0] is "server-load" or "server-load-ab")
{
    int result = args[0] == "server-load" ? await ServerLoad.RunAsync(args[1..]) : await ServerLoadComparison.RunAsync(args[1..]);
    if (result is 0 or 1) SmokeProject.PrintHint(server: true); // a run happened (2: usage, 3: refused)
    return result;
}

// The default path hosts a world in an owned client; server-load uses an owned dedicated server.
if (!StartArguments.TryRead(args, out var parsedStart, out var error, allowImplicitMod: true))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine("Usage: " + ToolCommandLine.StartUsage);
    return 2;
}
var options = parsedStart!.Options;
var mods = parsedStart.List("--mod");
var roots = parsedStart.List("--search-root");
var optionalReferences = parsedStart.List("--optional-reference");
var buildDependencies = parsedStart.List("--dependency");

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, press) => { press.Cancel = true; cancel.Cancel(); };
bool holdRequested = options!.ContainsKey("--hold");
// #1009: the client stops at its main menu and is held there (the parser requires --hold and refuses every world option).
bool menuOnly = parsedStart.Switches.Contains("--menu-only");
string runId = RunJournal.NewRunId();
using var runJournal = RunJournal.UseRun(runId);
// A held run can be finished while it is still starting (#964): finish cancels it as Ctrl+C does.
ForegroundHold.BootWatch? bootWatch = null;
var elapsed = Stopwatch.StartNew();
TargetedRegression? runner = null;
ScenarioReport? lastArm = null; string? lastArmOutput = null;
int exitCode = 3;
string? outcome = null;
bool journalClean = true;
bool characterPending = false;
string? output = null;
var progress = new ModInputStage.Progress();
try
{
    if (holdRequested && DetachedSession.ConfiguredRun == null)
    {
        bootWatch = ForegroundHold.BootWatch.Start(runId, cancel);
        Console.WriteLine(ForegroundHold.Starting(runId));
    }
    output = SmokeCommandOptions.Output(options!);
    // A chosen world is one complete world, matching its manifest, before anything is built or staged; it is copied and checked again below.
    string? worldFixture = options!.TryGetValue("--world-fixture", out string? requestedWorld) ? Path.GetFullPath(requestedWorld) : null;
    if (worldFixture != null) _ = FixtureBake.Check(worldFixture);
    // The client (the inventory's; this machine's Valheim with no --inventory), its loader, the mod, its build inputs,
    // the scenario and the dependency lock: one stage, shared with build and server-load.
    var stage = await ModInputStage.RunAsync(new ModInputStage.Request(ModInputStage.Route.Hosted, output, options!,
        parsedStart.Switches,
        mods!, roots!, optionalReferences!, buildDependencies!)
        { Shipped = ShippedLoader.Choose, Progress = progress, RunInputs = parsedStart.RunInputs,
            PluginFiles = parsedStart.List("--plugin-file"), PluginDirectories = parsedStart.List("--plugin-dir"),
            NoMod = parsedStart.Switches.Contains("--no-mod") }, cancel.Token).ConfigureAwait(false);
    var selectedMods = stage.Mods;
    string game = stage.Compile.Recipe.Install;
    string architecture = stage.ClientArchitecture!;
    string? mod = stage.NoMod ? null : selectedMods[0];
    var custom = stage.Scenario;
    var hostedScenario = (IOneShotHostedScenario?)custom?.Runner;
    var dependencies = stage.Dependencies!;
    NativeDependencyLock? comparison = null;
    string? compareMod = null;
    if (options.TryGetValue("--compare-mod", out string? compareFile))
    {
        compareMod = Path.GetFullPath(compareFile);
        if (!File.Exists(compareMod)) throw new FileNotFoundException("--compare-mod file does not exist: " + compareMod, compareMod);
        var compareRequest = SmokeDependencyInputs.Request([compareMod, .. selectedMods.Skip(1)], game, stage.Core,
            stage.CliManifest, stage.CliFiles, stage.SearchRoots, optionalReferences!, stage.Capabilities);
        comparison = NativeDependencyResolver.Resolve(compareRequest);
        comparison.Write(Path.Combine(output, "comparison-dependencies.lock.json"));
        if (!comparison.Ready)
            throw new InvalidDataException("Comparison dependency choices remain: " + SmokeDependencyInputs.Gaps(comparison));
        dependencies.RequireSameFixedInputs(comparison);
    }

    // The bundled world, or a saved one (save-world, server-load --bake-fixture): copied into the run's output and verified first.
    _ = ModInputStage.VerifyAdmission(stage.Admission!);
    var fixtureInput = parsedStart.Create == null ? FixtureBake.Prepare(worldFixture, output, stage.WorldAdmission, stage.HostingBuild) : null;
    var world = fixtureInput?.Identity;
    string worldRoot = Path.Combine(output, "world-source", "worlds_local");
    // The packaged world stays the arm's admitted fixture, so regression.json is a complete start input; a menu-only run never
    // places or hosts it. It stages no character: a character made at the menu must not meet the packaged one's name.
    var character = menuOnly ? null : DefaultSmokeCharacter.Prepare(Path.Combine(output, "character-source"));
    var inputs = new RegressionInputs
    {
        // A name per run: its shared hosted-runtime copy is never shared with another start.
        Name = "native-smoke-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture),
        Fixture = parsedStart.Create is { } create ? new RegressionFixture { Create = create } :
            new RegressionFixture { Root = worldRoot, WorldUid = world!.UidText },
        Client = new RegressionClient { Character = DefaultSmokeCharacter.Name, CharacterStore = character?.Root, Architecture = architecture },
        NoMod = stage.NoMod,
        Mod = mod == null ? new RegressionMod() : new RegressionMod { InstallAs = Path.GetFileName(mod), Arms = new Dictionary<string, RegressionArm>
            { [comparison == null ? "smoke" : "before"] = new() { File = mod, Sha256 = FileHash.Sha256(mod),
                // Only a real source commit; the artifact's own SHA-256 is already the arm's Sha256.
                Commit = options.GetValueOrDefault("--source") } } },
    };
    stage.RunInputs.ApplyTo(inputs);
    // The same companion mapping server-load stages, recorded with each admitted hash; configs travel in configs.
    var companions = ModInputStage.Companions(stage.Files);
    if (companions.Count != 0) inputs.PluginFiles = companions;
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
    runner.MenuOnly = menuOnly;
    runner.InitialHostingBuild = stage.HostingBuild;
    runner.InitialWorldAdmission = fixtureInput?.CopyAdmission;
    runner.InitialConfigSha256 = stage.RunInputs.Configs.ToDictionary(config => config.Destination, config => config.Sha256, StringComparer.Ordinal);
    Console.WriteLine($"disposable loader profile: {runner.Install}");
    Console.WriteLine("run ID: " + runId);
    bool passed = true;
    string finalArm = inputs.ArmNames.Last();
    foreach (string arm in inputs.ArmNames)
    {
        stage.RunInputs.Verify();
        string armOutput = Path.Combine(output, "evidence", arm);
        LocalClientJournal? processJournal = null;
        string scenario = custom?.Name ?? (menuOnly
            ? (inputs.NoMod ? "CLI-only control held at the main menu; no mod under test, no world" : "selected plugin loads and the client is held at the main menu; no world")
            : inputs.NoMod ? "CLI-only control hosts the fixture; no mod under test" : "selected plugin loads in a hosted fixture");
        var report = runner.Run(arm, armOutput, scenario, [menuOnly ? "menu" : "first"], round =>
            {
                hostedScenario?.Run(round, custom!.ValidatedInputs);
                if (holdRequested && arm == finalArm)
                    round.Step("keep the owned client running until finish", () =>
                        ForegroundHold.HoldAsync(runId, armOutput, cancel.Token,
                            [("client", processJournal?.Process ?? throw new InvalidOperationException("The owned client process was not recorded."))])
                            .GetAwaiter().GetResult());
            }, cancel.Token, afterPinnedClientOpened: ready =>
            {
                ready.Provenance["runId"] = runId;
                ready.Provenance[inputs.NoMod ? "controlReadySecondsFromCommand" : "firstModLoadedSecondsFromCommand"] =
                    elapsed.Elapsed.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                stage.Record(ready.Provenance);
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
                    RunJournal.ThisProcess.AppendLocal("client", TargetedRegression.CharacterEntry(point, characters, userData, name, expectedSha256));
                    if (point == CharacterStageEvent.Intended) characterPending = true;
                    if (point == CharacterStageEvent.Retired) characterPending = false;
                }, deferReportWrite: true);
        if (processJournal != null)
            try { processJournal.Complete(); }
            catch (Exception failure)
            {
                journalClean = false;
                report.RecordFailure(StepPhase.Cleanup, "owned client process was not proved stopped", failure);
            }
        Console.WriteLine(NativeSmokeLogSummary.Format(report.Logs, inputs.ArmNames.Count > 1 ? arm : null));
        if (report.Provenance.TryGetValue("saveFoldersAdded", out string? added))
            Console.WriteLine(added == "none" ? "The game added nothing to the client's save folders."
                : $"Moved what the game added to the client's save folders into {Path.Combine(armOutput, SaveFolderChanges.AddedFolder)}: {added}");
        // Final-arm evidence is written once after the copy-cleanup verdict in finally.
        report.Provenance["runId"] = runId;
        lastArm = report; lastArmOutput = armOutput;
        if (arm != finalArm && report.Passed) report.Write(armOutput);
        passed &= report.Passed;
        // A failed arm's evidence is enough to diagnose it; do not silently call an A/B comparison complete.
        if (!report.Passed) break;
    }
    exitCode = passed ? 0 : 1;
    outcome = $": {(menuOnly ? "held at the main menu, no world entered" : "hosted fixture")}, {(inputs.NoMod ? "no mod under test" : selectedMods.Count + " selected mod(s)")}, {inputs.ArmNames.Count} arm(s); private evidence in {output}";
}
catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or FormatException or OperationCanceledException or PlatformNotSupportedException)
{
    Console.Error.WriteLine("REFUSED: " + failure.Message);
    if (progress.OutputChecked) SmokeOutput.MarkRefused(output, "start", [failure.Message]);
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
        // Any failure here, an AggregateException from a retirement whose lock release also failed included, still reaches the
        // run's end below: a removal that throws past it would leave the run never journalled as ended.
        catch (Exception cleanup)
        {
            journalClean = false;
            // Recovery follows the hosted runtime's journal and ownership rules; never delete a copy by name.
            // An expected removal failure says its message; anything else (a bug, an AggregateException's inner failures) keeps its
            // stack, which its message alone would lose.
            string why = cleanup is IOException or UnauthorizedAccessException or InvalidOperationException ? cleanup.Message : cleanup.ToString();
            Console.Error.WriteLine("CLEANUP REFUSED: " + why + "; inspect the disposable install at " + runner.Install);
            if (exitCode == 0) exitCode = 1;
        }
    // The run's end, in every journal this run wrote (none for a refusal before anything was journalled), whatever failed (#667).
    if (!await RunFinish.EndAsync(runJournal, new RunEnd(exitCode == 0 ? RunOutcome.Passed : RunOutcome.Failed, journalClean)))
    {
        Console.Error.WriteLine("CLEANUP UNVERIFIED: the run ended but its journal could not record that.");
        exitCode = 1;
    }
    bootWatch?.Dispose();
}
if (outcome != null)
{
    Console.WriteLine((exitCode == 0 ? "PASS" : "FAIL") + outcome + $"; {elapsed.Elapsed.TotalSeconds:F1}s");
    if (!parsedStart.Switches.Contains("--no-mod")) SmokeProject.PrintHint(server: false);
}
return exitCode;

internal static class StartArguments
{
    internal static bool TryRead(string[] args, out SmokeCommandOptions.Parsed? parsed, out string error, bool allowImplicitMod = false)
    {
        if (!SmokeCommandOptions.TryRead(args, SmokeCommandOptions.Command.Start, allowImplicitMod, out parsed, out error)) return false;
        foreach (string flag in parsed!.Switches) parsed.Options[flag] = "true";
        return true;
    }

    public static bool TryRead(string[] args, out Dictionary<string, string>? result, out List<string>? mods,
        out List<string>? roots, out List<string>? optionalReferences, out string error, bool allowImplicitMod = false)
        => TryRead(args, out result, out mods, out roots, out optionalReferences, out _, out error, allowImplicitMod);

    public static bool TryRead(string[] args, out Dictionary<string, string>? result, out List<string>? mods,
        out List<string>? roots, out List<string>? optionalReferences, out List<string>? buildDependencies,
        out string error, bool allowImplicitMod = false)
    {
        result = null; mods = null; roots = null; optionalReferences = null; buildDependencies = null;
        if (!TryRead(args, out var parsed, out error, allowImplicitMod))
            return false;
        result = parsed!.Options;
        foreach (string flag in parsed.Switches) result[flag] = "true";
        mods = parsed.List("--mod");
        roots = parsed.List("--search-root");
        optionalReferences = parsed.List("--optional-reference");
        buildDependencies = parsed.List("--dependency");
        return true;
    }
}
