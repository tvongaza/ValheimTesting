using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>A Windows Task Scheduler-owned server hold that survives the command which started it.</summary>
internal static class DetachedSession
{
    internal const string Usage = "valheim-test detach server-load --server DIR --mod DLL --server-only --output NEW_DIR [assertion and timing options] | " +
        "valheim-test detach status [--evidence DIR] | valheim-test detach save|stop|restart --evidence DIR | valheim-test detach finish --run ID | " +
        "valheim-test detach swap --evidence DIR --mod DLL --sha256 HASH --build-inputs FILE --dependencies FILE";
    private const string ReadyVariable = "VALHEIM_TEST_DETACH_READY";
    private const string RunVariable = "VALHEIM_TEST_DETACH_RUN";
    private const string ControlVariable = "VALHEIM_TEST_DETACH_CONTROL";
    private const string TaskVariable = "VALHEIM_TEST_DETACH_TASK";
    private const string TaskExecutableVariable = "VALHEIM_TEST_DETACH_TASK_EXECUTABLE";
    private const string TaskUserVariable = "VALHEIM_TEST_DETACH_TASK_USER";
    private const string ToolVariable = "VALHEIM_TEST_DETACH_TOOL";
    private const string OutputVariable = "VALHEIM_TEST_DETACH_OUTPUT";
    private const string Actor = "detached";

    internal sealed record Ready(string Run, string Evidence, int Pid, string Started);
    internal sealed record Launch(string Run, string Label, string Output, string ReadyFile, string StandardOut,
        string StandardError, string ControlDirectory, string ToolDirectory, string LauncherPath, string TaskExecutable, string TaskUser)
    {
        internal WindowsScheduledTask Task => new(Label, "ValheimTesting detached run " + Run, TaskExecutable, LauncherPath, ToolDirectory, TaskUser);
    }
    internal sealed record Seams(bool Windows = false, Func<Task>? RequireDesktop = null,
        Func<WindowsScheduledTask, Task>? Register = null, Func<Launch, Task<Ready>>? WaitReady = null,
        Func<int, string, bool>? ExactOwner = null, Func<string, string>? StageTool = null, string? TaskExecutable = null,
        string? TaskUser = null);

    private static string RunsRoot => Path.Combine(CliBundle.DataRoot, "runs");
    private static string RecoveryLogs(string run) => Path.Combine(CliBundle.DataRoot, "recovered-detached", run);
    internal static string? ConfiguredRun => Environment.GetEnvironmentVariable(RunVariable);
    internal static string? ConfiguredControlDirectory => Environment.GetEnvironmentVariable(ControlVariable);

    internal static async Task<int> RunAsync(string[] args, TextWriter? output = null, TextWriter? error = null, Seams? seams = null)
    {
        output ??= Console.Out; error ??= Console.Error;
        if (!(seams?.Windows == true || OperatingSystem.IsWindows()))
        { error.WriteLine("REFUSED: detached sessions currently require a local Windows Task Scheduler owner; foreground --hold works on other hosts."); return 3; }
        // A live run is a successful status (#811): exit 3 only when something is left to recover or a journal could not be read.
        if (args is ["status"])
            return (await EnvironmentRuns.WriteRunStatusReportAsync(null, output).ConfigureAwait(false)).NothingToRecover ? 0 : 3;
        if (args is ["finish", "--run", var finishRun]) return await FinishAsync(finishRun, output, error).ConfigureAwait(false);
        if (args is ["swap", "--evidence", var swapEvidence, "--mod", var mod, "--sha256", var sha256,
            "--build-inputs", var buildInputs, "--dependencies", var dependencies])
        {
            try { return DetachedCommandQueue.SubmitCheckpoint(swapEvidence, "swap", output, error, new(mod, sha256, buildInputs, dependencies)); }
            catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or
                UnauthorizedAccessException or JsonException or HostOperationException)
            { error.WriteLine("REFUSED: " + failure.Message); return 3; }
        }
        if (args is [var operation, "--evidence", var evidence] && operation is "save" or "stop" or "restart" or "status")
        {
            try
            {
                if (operation == "status")
                {
                    var route = DetachedCommandQueue.CurrentRoute(Path.GetFullPath(evidence));
                    await RequireCommandOwnerAsync(route).ConfigureAwait(false);
                    var status = DetachedCommandQueue.Read<DetachedCommandQueue.Status>(Path.Combine(evidence, DetachedCommandQueue.StateFile));
                    output.WriteLine($"DETACHED {route.Run} boot {route.Boot}: {status.State}");
                    return status.State == "unknown" ? 3 : 0;
                }
                return DetachedCommandQueue.SubmitCheckpoint(evidence, operation, output, error);
            }
            catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or
                UnauthorizedAccessException or JsonException or HostOperationException)
            { error.WriteLine("REFUSED: " + failure.Message); return 3; }
        }
        if (args is not ["server-load", .. var serverArgs])
        { error.WriteLine("Usage: " + Usage); return 2; }
        if (!TryServerArgs(serverArgs, out string selectedOutput, out string reason))
        { error.WriteLine("Usage: " + Usage + "\nREFUSED: " + reason); return 2; }

        // A world fixture is one complete world before anything is staged for the detached run (up to 4 GB of it).
        int worldFixture = Array.IndexOf(serverArgs, "--world-fixture");
        ModInputStage.Inspection admission;
        try
        {
            if (worldFixture >= 0) _ = FixtureBake.Check(serverArgs[worldFixture + 1]);
            if (!ServerLoad.TryRead(serverArgs, out var parsed, out string parseError, allowImplicitMod: false))
                throw new ArgumentException(parseError);
            admission = ModInputStage.Inspect(new ModInputStage.Request(ModInputStage.Route.Dedicated, selectedOutput, parsed!.Options,
                parsed.Switches, parsed.Mods, parsed.Roots, parsed.Optional, parsed.BuildDependencies));
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        { error.WriteLine("REFUSED: " + failure.Message + " Nothing was staged."); return 3; }

        string run = RunJournal.NewRunId();
        try
        {
            if (seams?.RequireDesktop != null) await seams.RequireDesktop().ConfigureAwait(false);
            else await InteractiveClient.RequireWindowsDesktopSessionAsync(new LocalGameHost("this machine", HostShell.Pwsh)).ConfigureAwait(false);
            LocalGameHost? taskHost = null;
            string taskExecutable, taskUser;
            if (seams?.TaskExecutable is { } executable && seams.TaskUser is { } user)
            {
                taskExecutable = executable;
                taskUser = user;
            }
            else
            {
                taskHost = new LocalGameHost("this machine", HostShell.Pwsh);
                var registration = await WindowsScheduledTask.FindRegistrationContextAsync(taskHost, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                taskExecutable = registration.Executable;
                taskUser = registration.PrincipalUserId;
            }

            _ = ModInputStage.VerifyAdmission(admission);
            RequireInputSeparation(serverArgs, RunsRoot, selectedOutput);
            Directory.CreateDirectory(RunsRoot);
            using var runScope = RunJournal.UseRun(run);
            string control = JournalOwnedDirectory.Create(RunsRoot, Actor, "detached control and startup logs", _ => { },
                ("keepIn", RecoveryLogs(run)), ("keepFiles", "stdout.log\nstderr.log"));
            string tool = seams?.StageTool?.Invoke(RunsRoot) ?? StageTool(RunsRoot);
            string[] stagedArgs = StageInputsOwned(serverArgs, RunsRoot, control, admission);
            _ = ModInputStage.VerifyAdmission(admission);
            string label = WindowsScheduledTask.Prefix + run;
            var launch = new Launch(run, label, selectedOutput, Path.Combine(control, "ready.json"),
                Path.Combine(control, "stdout.log"), Path.Combine(control, "stderr.log"), control, tool, Path.Combine(control, "launcher.ps1"), taskExecutable, taskUser);
            var command = OwnCommand(tool);
            var childEnvironment = new List<string>
            {
                ReadyVariable + "=" + launch.ReadyFile, RunVariable + "=" + run, ControlVariable + "=" + control,
                TaskVariable + "=" + label, TaskExecutableVariable + "=" + taskExecutable,
                TaskUserVariable + "=" + taskUser,
                ToolVariable + "=" + tool, OutputVariable + "=" + selectedOutput,
                "PATH=" + Environment.GetEnvironmentVariable("PATH"),
            };
            foreach (string key in new[] { "DOTNET_ROOT", "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH" })
                if (Environment.GetEnvironmentVariable(key) is { } value) childEnvironment.Add(key + "=" + value);
            string[] program = [command.Program, .. command.Prefix, "server-load", .. stagedArgs, "--hold"];
            File.WriteAllText(launch.LauncherPath, WindowsLauncher(launch, childEnvironment, program), new System.Text.UTF8Encoding(true));

            var journal = new RunJournal(run);
            journal.AppendLocal(Actor, launch.Task.Entry(JournalEntry.ScheduledTaskIntended,
                ("control", control), ("output", selectedOutput)));
            try
            {
                if (seams?.Register != null) await seams.Register(launch.Task).ConfigureAwait(false);
                else await launch.Task.RegisterAsync(taskHost!, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is IOException or InvalidOperationException or HostOperationException or TimeoutException or
                                                     System.ComponentModel.Win32Exception)
            {
                error.WriteLine("REFUSED: " + failure.Message + ". Inspect env status, then use valheim-test env recover --run " + run + ".");
                return 3;
            }

            Ready ready;
            try { ready = seams?.WaitReady != null ? await seams.WaitReady(launch).ConfigureAwait(false) : await WaitReadyAsync(launch).ConfigureAwait(false); }
            catch (Exception failure) when (failure is IOException or TimeoutException or InvalidDataException or HostOperationException)
            {
                error.WriteLine("REFUSED: " + failure.Message + " Private startup logs: " + launch.StandardOut + " and " + launch.StandardError +
                    ". Check valheim-test env status, then use valheim-test env recover --run " + run + ".");
                return 3;
            }
            bool exact = seams?.ExactOwner?.Invoke(ready.Pid, ready.Started) ?? ForegroundHold.ExactOwner(ready.Pid, ready.Started);
            if (ready.Run != run || !exact)
            {
                error.WriteLine("REFUSED: the held runner exited or changed identity before detach returned; inspect env status for " + run + ".");
                return 3;
            }
            output.WriteLine("DETACHED run " + run + "; evidence " + ready.Evidence);
            output.WriteLine("Finish and verify cleanup: valheim-test detach finish --run " + run);
            return 0;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or
                                             UnauthorizedAccessException or HostOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            error.WriteLine("REFUSED run " + run + ": " + failure.Message + ". Inspect env status, then use valheim-test env recover --run " + run + ".");
            return 3;
        }
    }

    internal static bool TryServerArgs(string[] args, out string output, out string error)
    {
        output = ""; error = "";
        if (args.Contains("--hold") || args.Contains("--preflight-only") || args.Contains("--bake-fixture"))
        { error = "detach owns the hold; preflight-only and fixture export cannot be held."; return false; }
        if (args.Count(arg => arg == "--output") != 1 || args.Count(arg => arg == "--server") != 1 ||
            !args.Contains("--server-only") || !args.Contains("--mod"))
        { error = "give one absolute --output and --server, --server-only, and at least one absolute --mod."; return false; }
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--config" or "--plugin-file" or "--plugin-dir" or "--search-root" or "--adapter" or
                "--cli-manifest" or "--cli-files" or "--loader-package" or "--inventory")
            { error = "detached server input staging currently supports --mod, --server and --output only; use foreground --hold for " + args[i] + "."; return false; }
            if (args[i] is not ("--output" or "--server" or "--mod" or "--world-fixture")) continue;
            if (++i >= args.Length || !Path.IsPathFullyQualified(args[i]))
            { error = args[i - 1] + " needs an absolute path in a detached run."; return false; }
            if (args[i - 1] == "--output") output = Path.GetFullPath(args[i]);
        }
        if (args.Count(arg => arg == "--world-fixture") > 1)
        { error = "Give at most one --world-fixture."; return false; }
        if (output.Length == 0 || Directory.Exists(output) || File.Exists(output))
        { error = "--output must name a new absolute directory."; return false; }
        return true;
    }

    internal sealed record StagedInput(string Source, string Staged, IReadOnlyDictionary<string, string> Hashes)
    {
        public string? TargetInstall { get; init; }
        public string? TargetAssemblySha256 { get; init; }
        public string? SourceManifest { get; init; }
        public string? StagedManifest { get; init; }
        public string? ManifestSha256 { get; init; }
    }
    internal static string[] StageInputsOwned(string[] args, string parent, string control, ModInputStage.Inspection? admission = null,
        Action? afterWorldCopy = null)
    {
        if (admission != null) _ = ModInputStage.VerifyAdmission(admission);
        int output = Array.IndexOf(args, "--output");
        RequireInputSeparation(args, parent, control, output >= 0 ? args[output + 1] : parent);
        var inputs = new List<StagedInput>();
        string Stage(string source, string purpose, long maximum)
        {
            var hashes = WorldFixture.Manifest(source);
            string payload = "";
            JournalOwnedDirectory.Create(parent, Actor, purpose, owned =>
            {
                payload = Path.Combine(owned, "payload"); Directory.CreateDirectory(payload);
                CopyDirectory(source, payload, 10000, maximum, "A detached input directory is too large to stage", hashes);
                WorldFixture.Verify(source, hashes);
            });
            inputs.Add(new(source, payload, hashes));
            return payload;
        }
        string[] staged = CopyMods(args, source => Stage(source, "detached mod input", 512L << 20));
        int fixture = Array.IndexOf(staged, "--world-fixture");
        if (fixture >= 0)
        {
            string original = args[fixture + 1];
            var (worlds, manifestFile) = FixtureBake.Paths(original);
            var worldAdmission = admission?.WorldAdmission;
            if (worldAdmission != null && !Path.TrimEndingDirectorySeparator(Path.GetFullPath(worlds)).Equals(
                    Path.TrimEndingDirectorySeparator(worldAdmission.Source), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Stage the original fixture path retained by world-format admission.");
            string? manifestHash = worldAdmission != null ? worldAdmission.ManifestSha256 : File.Exists(manifestFile) ? FileHash.Sha256(manifestFile) : null;
            var hashes = worldAdmission?.Files ?? WorldFixture.Manifest(worlds);
            var identity = worldAdmission?.Identity ?? WorldIdentity.ReadObserved(worlds, hashes);
            if (manifestHash != null) FixtureBake.VerifyBakedManifest(manifestFile, identity, hashes);
            string payload = "";
            JournalOwnedDirectory.Create(parent, Actor, "detached world input", owned =>
            {
                payload = Path.Combine(owned, "payload");
                string copy = Path.Combine(payload, "worlds_local");
                Directory.CreateDirectory(copy);
                var observed = CopyDirectory(worlds, copy, 10000, 4L << 30, "A detached world is too large to stage", hashes);
                if (worldAdmission != null) _ = worldAdmission.World.ForObservedCopy(copy, observed);
                else if (WorldIdentity.ReadObserved(copy, observed) != identity)
                    throw new InvalidDataException("The detached world identity changed during staging.");
                afterWorldCopy?.Invoke();
                string stagedManifest = Path.Combine(payload, FixtureBake.ManifestFile);
                if (manifestHash != null)
                {
                    File.Copy(manifestFile, stagedManifest);
                    if (FileHash.Sha256(stagedManifest) != manifestHash || FileHash.Sha256(manifestFile) != manifestHash)
                        throw new InvalidDataException("The baked fixture manifest changed during detached staging.");
                }
                if (manifestHash != null) FixtureBake.VerifyBakedManifest(stagedManifest, identity, observed);
                // Keep source/target refusal inside the owner so a failed copy is retired by that same owner.
                if (admission != null) _ = ModInputStage.VerifyAdmission(admission);
                if (worldAdmission == null) WorldFixture.Verify(worlds, hashes);
            });
            inputs.Add(new(worlds, Path.Combine(payload, "worlds_local"), hashes)
            { SourceManifest = manifestFile, StagedManifest = Path.Combine(payload, FixtureBake.ManifestFile), ManifestSha256 = manifestHash });
            staged[fixture + 1] = payload;
        }
        if (admission != null)
        {
            if (fixture < 0) _ = ModInputStage.VerifyAdmission(admission);
            inputs[0] = inputs[0] with
            {
                TargetInstall = (admission.Choice.Server ?? admission.Choice.Client!).Install,
                TargetAssemblySha256 = admission.HostingBuild!.WorldSupport!.AssemblySha256,
            };
        }
        DetachedCommandQueue.WriteNew(Path.Combine(control, "input-manifests.json"), inputs, bounded: false);
        return staged;
    }

    internal static void RequireInputSeparation(string[] args, params string[] destinations)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is not ("--mod" or "--world-fixture")) continue;
            string source = args[i] == "--mod" ? Path.GetDirectoryName(args[++i])! : args[++i];
            foreach (string destination in destinations)
                if (ProtectedPaths.Contains(source, destination))
                    throw new InvalidOperationException("Detached output and staging must be outside each original input directory: " + source);
            if (args[i - 1] == "--world-fixture")
            {
                var (_, manifest, _) = FixtureBake.Inspect(source);
                foreach (string destination in destinations)
                    if (ProtectedPaths.Contains(destination, manifest))
                        throw new InvalidOperationException("Detached staging must be separate from the baked manifest: " + manifest);
            }
        }
    }

    internal static void VerifyStagedInputs(string? control = null, Action? afterStagedVerification = null)
    {
        control ??= ConfiguredControlDirectory ?? throw new InvalidOperationException("Missing detached input control directory.");
        var inputs = JsonSerializer.Deserialize<StagedInput[]>(File.ReadAllText(Path.Combine(control, "input-manifests.json")))
            ?? throw new InvalidDataException("Missing detached input manifests.");
        if (inputs.Length == 0) throw new InvalidDataException("No pinned detached inputs.");
        foreach (var input in inputs)
        {
            if (input.TargetInstall != null) _ = ReadPinnedTarget(input);
            WorldFixture.Verify(input.Staged, input.Hashes);
            afterStagedVerification?.Invoke();
            // Preserve the late original-content boundary after the potentially long staged scan.
            WorldFixture.Verify(input.Source, input.Hashes);
            if (input.SourceManifest != null)
            {
                if (input.ManifestSha256 != (File.Exists(input.SourceManifest) ? FileHash.Sha256(input.SourceManifest) : null) ||
                    input.ManifestSha256 != (File.Exists(input.StagedManifest) ? FileHash.Sha256(input.StagedManifest!) : null))
                    throw new InvalidDataException("A detached baked fixture manifest changed after staging.");
                var sourceIdentity = WorldIdentity.ReadObserved(input.Source, input.Hashes);
                var stagedIdentity = WorldIdentity.ReadObserved(input.Staged, input.Hashes);
                if (stagedIdentity != sourceIdentity) throw new InvalidDataException("The staged world identity differs from its source.");
                if (input.ManifestSha256 != null)
                {
                    FixtureBake.VerifyBakedManifest(input.SourceManifest, sourceIdentity, input.Hashes);
                    FixtureBake.VerifyBakedManifest(input.StagedManifest!, stagedIdentity, input.Hashes);
                }
            }
        }
    }

    private static GameBuild ReadPinnedTarget(StagedInput input)
    {
        var current = GameVersionAgreement.ReadInPlaceAsync(input.TargetInstall!, CancellationToken.None).GetAwaiter().GetResult();
        WorldIdentity.RequireSameTarget(input.TargetAssemblySha256, current);
        return current;
    }

    internal static GameBuild? PinnedHostingBuild()
    {
        if (ConfiguredControlDirectory is not { } control) return null;
        var inputs = JsonSerializer.Deserialize<StagedInput[]>(File.ReadAllText(Path.Combine(control, "input-manifests.json")));
        var target = inputs?.FirstOrDefault(input => input.TargetInstall != null);
        return target == null ? null : ReadPinnedTarget(target);
    }

    internal static FixtureBake.Admission? PinnedWorldAdmission(string? control = null)
    {
        control ??= ConfiguredControlDirectory;
        if (control == null) return null;
        var inputs = JsonSerializer.Deserialize<StagedInput[]>(File.ReadAllText(Path.Combine(control, "input-manifests.json")))
            ?? throw new InvalidDataException("Missing detached input manifests.");
        var world = inputs.SingleOrDefault(input => input.SourceManifest != null);
        if (world == null) return null;
        var target = inputs.SingleOrDefault(input => input.TargetInstall != null)
            ?? throw new InvalidDataException("Missing detached hosting game admission.");
        var retained = new FixtureBake.Admission(WorldIdentity.Capture(world.Staged, ReadPinnedTarget(target), world.Hashes),
            world.StagedManifest ?? throw new InvalidDataException("Missing detached fixture manifest path."), world.ManifestSha256);
        FixtureBake.VerifyManifestAdmission(retained);
        return retained;
    }

    private static string[] CopyMods(string[] args, Func<string, string> stage)
    {
        var staged = (string[])args.Clone();
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--mod") continue;
            string file = args[++i], directory = Path.GetDirectoryName(file)!;
            if (!File.Exists(file)) throw new FileNotFoundException("A detached mod does not exist: " + file, file);
            if (!sources.TryGetValue(directory, out string? copy)) sources.Add(directory, copy = stage(directory));
            staged[i] = Path.Combine(copy, Path.GetFileName(file));
        }
        return staged;
    }

    private static IReadOnlyDictionary<string, string> CopyDirectory(string source, string target, int maxFiles, long maxBytes, string tooLarge,
        IReadOnlyDictionary<string, string>? pinned = null)
    {
        var manifest = pinned ?? WorldFixture.Manifest(source);
        if (manifest.Count > maxFiles || manifest.Keys.Sum(relative => new FileInfo(Path.Combine(source, relative)).Length) > maxBytes)
            throw new InvalidDataException(tooLarge + ": " + source);
        foreach (var (relative, hash) in manifest)
        {
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(source, relative), destination);
        }
        var observed = WorldFixture.VerifyObserved(target, manifest);
        // Callers with retained pins own the later original-input check across staging.
        if (pinned == null) WorldFixture.Verify(source, manifest);
        return observed;
    }

    // The supervisor's first journal line adopts the starter's run before the campaign creates any host state. It is written under
    // the run's control lock (#811), which the supervisor then holds for as long as it runs: a recovery that holds the lock first
    // is clearing the run, so the supervisor refuses before it adopts anything. Dispose the result when the supervisor ends.
    internal static IDisposable AdoptRun(string run)
    {
        string configured = ConfiguredRun ?? "";
        string control = ConfiguredControlDirectory ?? "";
        string label = Environment.GetEnvironmentVariable(TaskVariable) ?? "";
        string executable = Environment.GetEnvironmentVariable(TaskExecutableVariable) ?? "";
        string taskUser = Environment.GetEnvironmentVariable(TaskUserVariable) ?? "";
        string tool = Environment.GetEnvironmentVariable(ToolVariable) ?? "";
        string output = Environment.GetEnvironmentVariable(OutputVariable) ?? "";
        if (run != configured || !RunJournal.SafeName(run) || label != WindowsScheduledTask.Prefix + run ||
            executable.Length == 0 || taskUser.Length == 0 || !Path.IsPathFullyQualified(control) || !Path.IsPathFullyQualified(tool) || !Path.IsPathFullyQualified(output))
            throw new InvalidOperationException("The detached run or scheduled owner environment is invalid.");
        var task = new WindowsScheduledTask(label, "ValheimTesting detached run " + run, executable, Path.Combine(control, "launcher.ps1"), tool, taskUser);
        var held = DetachedControlLock.TryTake(control)
            ?? throw new InvalidOperationException("Another process holds detached run " + run + "'s control lock: a recovery is clearing the run, so this supervisor does not adopt it.");
        // A recovery that already retired this task (under the lock, which it has since released) is removing the run's copies:
        // a supervisor that started before that stop and only now reaches this point must not adopt the run under it.
        if (TaskRetired(run, label))
        {
            held.Dispose();
            throw new InvalidOperationException("A recovery already retired detached run " + run + "'s scheduled task, so this supervisor does not adopt it.");
        }
        try { new RunJournal(run).AppendLocal(Actor, task.Entry(JournalEntry.ScheduledTaskStarted, ("control", control), ("output", output))); }
        catch { held.Dispose(); throw; }
        return held;
    }

    // Whether this machine's journal for the run records the named task retired. Unreadable lines are skipped: only a journalled
    // retirement refuses adoption.
    private static bool TaskRetired(string run, string task)
    {
        string directory = Path.Combine(RunJournal.LocalDirectory, run);
        if (!Directory.Exists(directory)) return false;
        foreach (string file in Directory.EnumerateFiles(directory, "*.jsonl"))
        {
            string text;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
            foreach (string line in text.Split('\n').Where(line => line.Trim().Length != 0))
            {
                JournalRecord record;
                try { record = RunJournal.ParseLine(line); }
                catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException) { continue; }
                if (record.Entry.Kind == JournalEntry.ScheduledTaskRetired && record.Entry.Fields.GetValueOrDefault("task") == task) return true;
            }
        }
        return false;
    }

    internal static void SignalHeld(string run, string evidence, int pid, string started, string? readyOverride = null, string? controlOverride = null)
    {
        string? path = readyOverride ?? Environment.GetEnvironmentVariable(ReadyVariable);
        if (path == null) return;
        string control = controlOverride ?? ConfiguredControlDirectory ?? "";
        if (!Path.IsPathFullyQualified(path) || !Path.IsPathFullyQualified(control) ||
            !Path.GetDirectoryName(path)!.Equals(control, StringComparison.Ordinal))
            throw new InvalidOperationException("The detached readiness path is outside this run's journalled control directory.");
        string temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(stream, new Ready(run, evidence, pid, started));
        File.Move(temporary, path);
    }

    private static string StageTool(string parent)
    {
        string payload = "";
        JournalOwnedDirectory.Create(parent, Actor, "detached tool snapshot", owned =>
        {
            payload = Path.Combine(owned, "payload"); Directory.CreateDirectory(payload);
            string assembly = Assembly.GetEntryAssembly()?.Location ?? "";
            if (!Path.IsPathFullyQualified(assembly) || !File.Exists(assembly))
                throw new InvalidOperationException("The valheim-test DLL path is unavailable for staging.");
            string source = Path.GetDirectoryName(assembly)!;
            CopyDirectory(source, payload, int.MaxValue, long.MaxValue, "The runner build is too large to stage");
            if (!File.Exists(Path.Combine(payload, Path.GetFileName(assembly))) ||
                !File.Exists(Path.Combine(payload, Path.GetFileNameWithoutExtension(assembly) + ".runtimeconfig.json")))
                throw new InvalidDataException("The runner build has no complete .NET runtime configuration.");
        });
        return payload;
    }

    private static (string Program, string[] Prefix) OwnCommand(string toolDirectory)
    {
        string process = Environment.ProcessPath ?? throw new InvalidOperationException("The valheim-test executable path is unavailable.");
        string assembly = Assembly.GetEntryAssembly()?.Location ?? "";
        return OwnCommandForPaths(toolDirectory, process, assembly, RuntimeEnvironment.GetRuntimeDirectory());
    }

    internal static (string Program, string[] Prefix) OwnCommandForPaths(string toolDirectory, string process, string assembly, string runtimeDirectory)
    {
        string host = Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? process : DotnetHost(runtimeDirectory);
        return (host, [Path.Combine(toolDirectory, Path.GetFileName(assembly))]);
    }

    internal static string DotnetHost(string runtimeDirectory)
    {
        string host = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        if (!File.Exists(host)) throw new InvalidOperationException("The .NET host beside the active runtime was not found: " + host);
        return host;
    }

    private static string Ps(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    internal static string WindowsLauncher(Launch launch, IReadOnlyList<string> environment, IReadOnlyList<string> program)
    {
        var script = new System.Text.StringBuilder("$ErrorActionPreference = 'Stop'\n");
        foreach (string item in environment)
        {
            int equals = item.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || !item[..equals].All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_'))
                throw new ArgumentException("Invalid detached environment variable.");
            script.Append("$env:").Append(item[..equals]).Append(" = ").Append(Ps(item[(equals + 1)..])).Append('\n');
        }
        script.Append("& ").Append(Ps(program[0]));
        foreach (string argument in program.Skip(1)) script.Append(' ').Append(Ps(argument));
        script.Append(" 1>> ").Append(Ps(launch.StandardOut)).Append(" 2>> ").Append(Ps(launch.StandardError)).Append('\n');
        script.Append("exit $LASTEXITCODE\n");
        return script.ToString();
    }

    private static async Task<Ready> WaitReadyAsync(Launch launch)
    {
        using var watcher = new FileSystemWatcher(launch.ControlDirectory, Path.GetFileName(launch.ReadyFile))
        { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, EnableRaisingEvents = true };
        TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FileSystemEventHandler onChange = (_, _) => changed.TrySetResult();
        RenamedEventHandler onRename = (_, _) => changed.TrySetResult();
        watcher.Created += onChange; watcher.Changed += onChange; watcher.Renamed += onRename;
        try
        {
            DateTime started = DateTime.UtcNow, deadline = started + TimeSpan.FromMinutes(10);
            var host = new LocalGameHost("this machine", HostShell.Pwsh);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(launch.ReadyFile))
                {
                    var ready = JsonSerializer.Deserialize<Ready>(File.ReadAllText(launch.ReadyFile))
                        ?? throw new InvalidDataException("The detached readiness record is empty.");
                    if (ready.Run != launch.Run || ready.Pid <= 0 || !Path.IsPathFullyQualified(ready.Evidence))
                        throw new InvalidDataException("The detached readiness record is invalid.");
                    return ready;
                }
                var state = await launch.Task.InspectAsync(host, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                if (state == WindowsScheduledTask.State.NotStarted && DateTime.UtcNow - started > TimeSpan.FromSeconds(30))
                    throw new IOException("The Windows detached task did not start within thirty seconds.");
                if (state is WindowsScheduledTask.State.Missing or WindowsScheduledTask.State.Mismatch or WindowsScheduledTask.State.Stopped)
                    throw new IOException("The Windows detached task ended or changed before the game entered its held state (" + state + ").");
                await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                if (changed.Task.IsCompleted) changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            throw new TimeoutException("The detached runner did not reach its held state within ten minutes.");
        }
        finally { watcher.Created -= onChange; watcher.Changed -= onChange; watcher.Renamed -= onRename; }
    }

    private sealed record Owner(WindowsScheduledTask Task, string Control, string Output, JournalRunner Runner);

    // The evidence pointer is routing information, never a second ownership authority. Prove it against the existing journal.
    internal static async Task RequireCommandOwnerAsync(DetachedCommandQueue.Route route)
    {
        var owner = await ReadOwnerAsync(route.Run).ConfigureAwait(false);
        if (owner.Control != route.Control || !owner.Runner.OnThisMachine || owner.Runner.Pid != route.Pid ||
            owner.Runner.StartedUtc.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture) != route.Started ||
            !ForegroundHold.ExactOwner(route.Pid, route.Started))
            throw new InvalidOperationException("The exact journalled detached supervisor is gone or changed; no command was sent.");
    }

    // The run's last journalled detached owner, retired or not: a repeated finish reads the same owner and reports the run's end.
    private static async Task<Owner> ReadOwnerAsync(string run)
    {
        var host = new LocalGameHost("this machine", HostShell.ThisMachine);
        // In time order, as RunJournalStatus reads them: per-actor files are not ordered among themselves.
        var records = (await RunJournalOnHost.ReadAsync(host, RunJournal.LocalDirectory, run, TimeSpan.FromSeconds(60)).ConfigureAwait(false))
            .OrderBy(record => record.Utc).ToList();
        var open = records.LastOrDefault(record => record.Entry.Kind is JournalEntry.ScheduledTaskIntended or JournalEntry.ScheduledTaskStarted);
        if (open == null) throw new InvalidDataException("Run " + run + " has no journalled detached owner to finish.");
        string control = open.Entry.Fields.GetValueOrDefault("control") ?? "";
        string output = open.Entry.Fields.GetValueOrDefault("output") ?? "";
        if (!Path.IsPathFullyQualified(control) || !Path.IsPathFullyQualified(output))
            throw new InvalidDataException("Run " + run + " has an invalid detached owner journal entry.");
        var runner = RunJournalStatus.LastNamedRunner(records)
            ?? throw new InvalidDataException("Run " + run + " has no runner in its detached owner journal entry.");
        return new(WindowsScheduledTask.FromJournal(open.Entry.Fields, run), control, output, runner);
    }

    private static async Task<int> FinishAsync(string run, TextWriter output, TextWriter error)
    {
        if (!RunJournal.SafeName(run)) { error.WriteLine("Usage: " + Usage); return 2; }
        try
        {
            var owner = await ReadOwnerAsync(run).ConfigureAwait(false);
            bool live = owner.Runner.OnThisMachine && owner.Runner.StillRuns();
            int ownerExit = 0; // Only an owner this finish waited for has an exit to read.
            if (live)
            {
                ForegroundHold.Request(run, owner.Control);
                using var process = Process.GetProcessById(owner.Runner.Pid);
                using var wait = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                ownerExit = process.ExitCode;
            }
            if (Directory.Exists(owner.Output))
            {
                // Evidence only: a log the launcher still holds must not stop recovery of what the run owns.
                foreach (string log in new[] { "stdout.log", "stderr.log" })
                    try { CopyLog(Path.Combine(owner.Control, log), Path.Combine(owner.Output, "detached-" + log)); }
                    catch (IOException copy) { error.WriteLine($"warning: could not keep detached {log}: {copy.Message}"); }
            }
            int clean = await EnvCommand.RunAsync(["recover", "--run", run], output, error).ConfigureAwait(false);
            if (clean != 0) throw new InvalidOperationException("The run still owns state; inspect env status.");
            // The verdict is the run's journalled end (#811), not whether its supervisor still runs: a repeated finish, or one after the
            // supervisor ended, reports the end the run wrote. A run its owner never ended (it died) has none and never passes.
            var end = (await EnvironmentRuns.RunStatusAsync(null, run).ConfigureAwait(false))?.End;
            bool passed = end is { Passed: true } && ownerExit == 0;
            output.WriteLine((passed ? "DETACHED FINISHED run " : "DETACHED RECOVERED FAILED run ") + run + "; its journalled end: " +
                (end?.State ?? "none") + "; owned state verified clear.");
            return passed ? 0 : 1;
        }
        catch (Exception failure) when (failure is ArgumentException or IOException or InvalidDataException or InvalidOperationException or
                                             UnauthorizedAccessException or OperationCanceledException or JsonException or HostOperationException or
                                             System.ComponentModel.Win32Exception or TimeoutException)
        { error.WriteLine("REFUSED: " + failure.Message); return 3; }
    }

    private static void CopyLog(string source, string target)
    {
        if (!File.Exists(source)) return;
        if (File.Exists(target))
        {
            if (FileHash.Sha256(source) != FileHash.Sha256(target)) throw new IOException("A different detached log already exists: " + target);
            return;
        }
        // The launcher may still have the log open for writing.
        using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var to = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        from.CopyTo(to);
    }
}
