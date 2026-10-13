using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>A Windows Task Scheduler-owned server hold that survives the command which started it.</summary>
internal static class DetachedSession
{
    internal const string Usage = "valheim-test detach server-load --server DIR --mod DLL --server-only --output NEW_DIR [assertion and timing options] | " +
        "valheim-test detach status | valheim-test detach finish --run ID";
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
        if (args is ["status"])
            return await EnvironmentRuns.WriteRunStatusAsync(null, output).ConfigureAwait(false) ? 0 : 3;
        if (args is ["finish", "--run", var finishRun]) return await FinishAsync(finishRun, output, error).ConfigureAwait(false);
        if (args is not ["server-load", .. var serverArgs])
        { error.WriteLine("Usage: " + Usage); return 2; }
        if (!TryServerArgs(serverArgs, out string selectedOutput, out string reason))
        { error.WriteLine("Usage: " + Usage + "\nREFUSED: " + reason); return 2; }

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

            Directory.CreateDirectory(RunsRoot);
            using var runScope = RunJournal.UseRun(run);
            string control = JournalOwnedDirectory.Create(RunsRoot, Actor, "detached control and startup logs", _ => { },
                ("keepIn", RecoveryLogs(run)), ("keepFiles", "stdout.log\nstderr.log"));
            string tool = seams?.StageTool?.Invoke(RunsRoot) ?? StageTool(RunsRoot);
            string[] stagedArgs = StageModsOwned(serverArgs, RunsRoot);
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
            bool exact = seams?.ExactOwner?.Invoke(ready.Pid, ready.Started) ?? ExactOwner(ready.Pid, ready.Started);
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
                "--cli-manifest" or "--cli-files" or "--loader-package" or "--world-fixture" or "--inventory")
            { error = "detached server input staging currently supports --mod, --server and --output only; use foreground --hold for " + args[i] + "."; return false; }
            if (args[i] is not ("--output" or "--server" or "--mod")) continue;
            if (++i >= args.Length || !Path.IsPathFullyQualified(args[i]))
            { error = args[i - 1] + " needs an absolute path in a detached run."; return false; }
            if (args[i - 1] == "--output") output = Path.GetFullPath(args[i]);
        }
        if (output.Length == 0 || Directory.Exists(output) || File.Exists(output))
        { error = "--output must name a new absolute directory."; return false; }
        return true;
    }

    private static string[] StageModsOwned(string[] args, string parent) => CopyMods(args, source =>
    {
        string payload = "";
        JournalOwnedDirectory.Create(parent, Actor, "detached mod input", owned =>
        {
            payload = Path.Combine(owned, "payload"); Directory.CreateDirectory(payload);
            CopyDirectory(source, payload, 10000, 512L << 20, "A detached mod directory is too large to stage");
        });
        return payload;
    });

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

    private static void CopyDirectory(string source, string target, int maxFiles, long maxBytes, string tooLarge)
    {
        var manifest = WorldFixture.Manifest(source);
        if (manifest.Count > maxFiles || manifest.Keys.Sum(relative => new FileInfo(Path.Combine(source, relative)).Length) > maxBytes)
            throw new InvalidDataException(tooLarge + ": " + source);
        foreach (var (relative, hash) in manifest)
        {
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(source, relative), destination);
            if (!FileHash.Sha256(destination).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The detached input changed during staging: " + relative);
        }
        WorldFixture.Verify(source, manifest);
    }

    // The supervisor's first journal line adopts the starter's run before the campaign creates any host state.
    internal static void AdoptRun(string run)
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
        new RunJournal(run).AppendLocal(Actor, task.Entry(JournalEntry.ScheduledTaskStarted, ("control", control), ("output", output)));
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

    private static bool ExactOwner(int pid, string started)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture) == started;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    private sealed record Owner(WindowsScheduledTask Task, string Control, string Output, JournalRunner Runner);

    private static async Task<Owner> ReadOwnerAsync(string run)
    {
        var host = new LocalGameHost("this machine", HostShell.ThisMachine);
        // In time order, as RunJournalStatus reads them: per-actor files are not ordered among themselves.
        var records = (await RunJournalOnHost.ReadAsync(host, RunJournal.LocalDirectory, run, TimeSpan.FromSeconds(60)).ConfigureAwait(false))
            .OrderBy(record => record.Utc).ToList();
        var open = records.Where(record => record.Entry.Kind is JournalEntry.ScheduledTaskIntended or JournalEntry.ScheduledTaskStarted)
            .LastOrDefault(record => !records.Any(later => later.Utc >= record.Utc && later.Entry.Kind == JournalEntry.ScheduledTaskRetired &&
                later.Entry.Fields.GetValueOrDefault("task") == record.Entry.Fields.GetValueOrDefault("task")));
        if (open == null) throw new InvalidDataException("Run " + run + " has no journalled detached owner to finish.");
        string control = open.Entry.Fields.GetValueOrDefault("control") ?? "";
        string output = open.Entry.Fields.GetValueOrDefault("output") ?? "";
        if (!Path.IsPathFullyQualified(control) || !Path.IsPathFullyQualified(output))
            throw new InvalidDataException("Run " + run + " has an invalid detached owner journal entry.");
        var runner = records.Last(record => record.Actor != RunRecovery.Actor).Runner;
        return new(WindowsScheduledTask.FromJournal(open.Entry.Fields, run), control, output, runner);
    }

    private static async Task<int> FinishAsync(string run, TextWriter output, TextWriter error)
    {
        if (!RunJournal.SafeName(run)) { error.WriteLine("Usage: " + Usage); return 2; }
        try
        {
            var owner = await ReadOwnerAsync(run).ConfigureAwait(false);
            bool live = owner.Runner.OnThisMachine && owner.Runner.StillRuns();
            int ownerExit = live ? 0 : 1;
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
            output.WriteLine((ownerExit == 0 ? "DETACHED FINISHED run " : "DETACHED RECOVERED FAILED run ") + run + "; owned state verified clear.");
            return ownerExit == 0 ? 0 : 1;
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
