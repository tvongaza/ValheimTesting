using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Valheim.Testing.GameSessions;
using Xunit;

[CollectionDefinition(nameof(DetachedEnvironmentVariables), DisableParallelization = true)]
public sealed class DetachedEnvironmentVariables { }

[Collection(nameof(DetachedEnvironmentVariables))]
public sealed class DetachedSessionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-detached-").FullName;
    public void Dispose() => DirectoryLinks.DeleteTree(_root);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitFixtureIsPinnedBeforeStartupAndSourceOrStagedDriftIsRefused(bool stagedDrift)
    {
        using var machine = new FakeDataRoot(Path.Combine(_root, "fixture-data"));
        string mod = Directory.CreateDirectory(Path.Combine(_root, "mod-source")).FullName;
        string fixture = Directory.CreateDirectory(Path.Combine(_root, "world-source")).FullName;
        string control = Directory.CreateDirectory(Path.Combine(_root, "control-input")).FullName;
        File.WriteAllText(Path.Combine(mod, "Mod.dll"), "pinned mod");
        File.WriteAllText(Path.Combine(fixture, "World.db"), "original fixture");
        string[] args = ["--mod", Path.Combine(mod, "Mod.dll"), "--world-fixture", fixture];
        using var run = RunJournal.UseRun("run-stage-fixture");
        var staged = DetachedSession.StageInputsOwned(args, Path.Combine(_root, "inputs"), control);
        Assert.NotEqual(fixture, staged[3]);
        DetachedSession.VerifyStagedInputs(control);
        File.WriteAllText(Path.Combine(stagedDrift ? staged[3] : fixture, "World.db"), "changed");
        Assert.Throws<InvalidOperationException>(() => DetachedSession.VerifyStagedInputs(control));
        Assert.Equal("pinned mod", File.ReadAllText(staged[1]));
    }

    [Theory]
    [InlineData("output")]
    [InlineData("staging")]
    [InlineData("control")]
    [InlineData("fixture-output")]
    [InlineData("linked-output")]
    public void OutputOrStagingInsideOriginalInputIsRefusedBeforeCreatingCopies(string overlap)
    {
        using var machine = new FakeDataRoot(Path.Combine(_root, "data"));
        string source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "Mod.dll"), "original");
        string parent = Path.Combine(_root, "copies"), control = Path.Combine(_root, "control"), output = Path.Combine(_root, "evidence");
        if (overlap == "staging") parent = Path.Combine(source, "copies");
        else if (overlap == "control") control = Path.Combine(source, "control");
        else if (overlap == "linked-output")
        {
            string alias = Path.Combine(_root, "alias");
            DirectoryLinks.Create(alias, source);
            output = Path.Combine(alias, "evidence");
        }
        else output = Path.Combine(source, "evidence");
        string[] args = overlap == "fixture-output"
            ? ["--world-fixture", source, "--output", output]
            : ["--mod", Path.Combine(source, "Mod.dll"), "--output", output];
        var before = WorldFixture.Manifest(source);
        Assert.Throws<InvalidOperationException>(() => DetachedSession.StageInputsOwned(args, parent, control));
        WorldFixture.Verify(source, before);
        Assert.False(Directory.Exists(parent)); Assert.False(Directory.Exists(control)); Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void ServerOnlyDetachRequiresExplicitAbsoluteInputsBeforeLaunch()
    {
        string server = Path.Combine(_root, "server"), mod = Path.Combine(_root, "mod.dll"), output = Path.Combine(_root, "run");
        string[] valid = ["--server", server, "--mod", mod, "--server-only", "--output", output];
        Assert.True(DetachedSession.TryServerArgs(valid, out string selected, out _));
        Assert.Equal(output, selected);
        Assert.False(DetachedSession.TryServerArgs(["--server", server, "--mod", "relative.dll", "--server-only", "--output", output], out _, out string relative));
        Assert.Contains("--mod", relative);
        Assert.False(DetachedSession.TryServerArgs([.. valid, "--hold"], out _, out string held));
        Assert.Contains("detach owns the hold", held);
        Assert.False(DetachedSession.TryServerArgs([.. valid, "--bake-fixture", Path.Combine(_root, "bake")], out _, out _));
        Assert.False(DetachedSession.TryServerArgs(valid.Where(item => item != "--server-only").ToArray(), out _, out _));
        Directory.CreateDirectory(output);
        Assert.False(DetachedSession.TryServerArgs(valid, out _, out string existing));
        Assert.Contains("new absolute directory", existing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DesktopAndRegistrationContextFailuresReportARecoverableRunId(bool timeout)
    {
        string outputPath = Path.Combine(_root, "context-refusal-" + timeout);
        Exception failure = timeout
            ? new TimeoutException("registration context timed out")
            : new HostOperationException("registration context failed",
                new HostResult(HostOutcome.TransportFailed, null, "", "transport failed", TimeSpan.Zero, false));
        using var output = new StringWriter(); using var error = new StringWriter();

        int exit = await DetachedSession.RunAsync(["server-load", "--server", Path.Combine(_root, "server"),
            "--mod", Path.Combine(_root, "source", "mod.dll"), "--server-only", "--output", outputPath], output, error,
            new DetachedSession.Seams(Windows: true, RequireDesktop: () => Task.FromException(failure)));

        Assert.Equal(3, exit);
        Assert.Contains("REFUSED run run-", error.ToString());
        Assert.Contains("env recover --run run-", error.ToString());
        Assert.Contains(failure.Message, error.ToString());
    }

    [Fact]
    public async Task RegistrationTimeoutReportsTheJournalledRunAsRecoverable()
    {
        string data = Path.Combine(_root, "registration-timeout-data"), server = Path.Combine(_root, "server"), source = Path.Combine(_root, "mod");
        Directory.CreateDirectory(server); Directory.CreateDirectory(source);
        string mod = Path.Combine(source, "Mod.dll"); File.WriteAllText(mod, "mod");
        using var machine = new FakeDataRoot(data);
        using var output = new StringWriter(); using var error = new StringWriter();

        int exit = await DetachedSession.RunAsync(["server-load", "--server", server, "--mod", mod, "--server-only",
            "--output", Path.Combine(_root, "registration-timeout-output")], output, error,
            new DetachedSession.Seams(Windows: true, RequireDesktop: () => Task.CompletedTask,
                Register: _ => Task.FromException(new TimeoutException("registration timed out")),
                StageTool: _ => _root, TaskExecutable: "pwsh.exe", TaskUser: @"TEST\runner"));

        Assert.Equal(3, exit);
        Assert.Contains("registration timed out", error.ToString());
        Assert.Contains("env recover --run run-", error.ToString());
    }

    [Fact]
    public void ReadinessIsWrittenAtomicallyOnlyInsideTheRunDataRoot()
    {
        string ready = Path.Combine(_root, "probe.ready.json");
        DetachedSession.SignalHeld("run-test", Path.Combine(_root, "evidence"), 321, "12345", ready, _root);
        using var json = JsonDocument.Parse(File.ReadAllText(ready));
        Assert.Equal("run-test", json.RootElement.GetProperty("Run").GetString());
        Assert.Equal(321, json.RootElement.GetProperty("Pid").GetInt32());
        Assert.False(File.Exists(ready + ".new"));
        Assert.Throws<InvalidOperationException>(() => DetachedSession.SignalHeld("run-test", _root, 321, "12345",
            Path.Combine(_root, "other", "probe.ready.json"), _root));
        Assert.Throws<IOException>(() => DetachedSession.SignalHeld("run-test", _root, 321, "12345", ready, _root));
    }

    [Fact]
    public async Task ADirectoryInterruptedImmediatelyAfterCreationIsFoundAndRecoveredFromItsPriorIntent()
    {
        string data = Path.Combine(_root, "data"), parent = Path.Combine(data, "runs");
        using var machine = new FakeDataRoot(data);
        using var run = RunJournal.UseRun("run-owned-directory");
        Assert.Throws<JournalOwnedDirectory.InterruptedAfterCreateForTestException>(() =>
            JournalOwnedDirectory.InterruptAfterCreateForTest(parent, "detached", "test"));
        var records = File.ReadAllLines(Assert.Single(Directory.GetFiles(Path.Combine(data, "journal"), "*.jsonl", SearchOption.AllDirectories)))
            .Select(RunJournal.ParseLine).ToArray();
        var intended = Assert.Single(records);
        Assert.Equal(JournalEntry.CopyIntended, intended.Entry.Kind);
        string path = intended.Entry.Fields["runtime"];
        Assert.True(Directory.Exists(path));
        Assert.Equal("delete", intended.Entry.Fields["cleanup"]);
        new RunJournal("run-owned-directory").AppendLocal("run",
            JournalEntry.Of(JournalEntry.RunEnded, ("state", "failed"), ("cleanupVerified", "false")));

        var profile = new HostProfile { Kind = "local", Platform = HostProfile.CurrentPlatform,
            Shell = HostProfile.ShellFor(HostProfile.CurrentPlatform), Lock = Path.Combine(data, "lock") };
        var host = new LocalGameHost("local", HostShell.ThisMachine);
        var recovered = await RunRecovery.RecoverAsync(new Dictionary<string, HostProfile> { ["local"] = profile }, _ => host,
            "run-owned-directory", teardown: false, TimeSpan.FromSeconds(30));
        Assert.True(recovered.Clean, string.Join("\n", recovered.Steps));
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void WindowsTaskUsesOneDesktopTokenAndQuotesTheRunnerArguments()
    {
        var launch = new DetachedSession.Launch("run-abcd", "ValheimTesting-detached-run-abcd", _root,
            "ready", "out", "err", "control", "tool", @"C:\run's folder\launcher.ps1", @"C:\Program Files\PowerShell\7\pwsh.exe", @"TEST\runner");
        string runner = DetachedSession.WindowsLauncher(launch, ["VALHEIM_TEST_DETACH_READY=C:\\ready's file"],
            [@"C:\Program Files\dotnet\dotnet.exe", @"C:\run's folder\test.dll", "--hold"]);
        Assert.Contains("$env:VALHEIM_TEST_DETACH_READY = 'C:\\ready''s file'", runner);
        Assert.Contains("& 'C:\\Program Files\\dotnet\\dotnet.exe' 'C:\\run''s folder\\test.dll' '--hold'", runner);
        Assert.Contains("1>> 'out' 2>> 'err'", runner);
        Assert.Contains("$definition.Principal.LogonType = [int]$principalLogonType", WindowsScheduledTask.RegisterScript);
        Assert.Contains("$definition, 2, $principalUserId", WindowsScheduledTask.RegisterScript); // TASK_CREATE; 1 would never register it.
        Assert.Contains("$settings.ExecutionTimeLimit = $executionTimeLimit", WindowsScheduledTask.RegisterScript);
        Assert.DoesNotContain("S4U", WindowsScheduledTask.RegisterScript);
        Assert.Contains("C:\\run's folder\\launcher.ps1", launch.Task.Arguments);
    }

    [Fact]
    public void DetachedHoldNamesTheDetachFinishCommand()
    {
        const string run = "run-detached-hint";
        string? previous = Environment.GetEnvironmentVariable("VALHEIM_TEST_DETACH_RUN");
        try
        {
            Environment.SetEnvironmentVariable("VALHEIM_TEST_DETACH_RUN", run);
            Assert.Equal("valheim-test detach finish --run " + run, ForegroundHold.FinishUsageForRun(run));
            Assert.Equal("valheim-test finish --run another-run", ForegroundHold.FinishUsageForRun("another-run"));
        }
        finally { Environment.SetEnvironmentVariable("VALHEIM_TEST_DETACH_RUN", previous); }
    }

    [Fact]
    public async Task ServerLoadAdoptsTheStartersJournalBeforeItsRealLocalPreflight()
    {
        using var machine = new FakeDataRoot(Path.Combine(_root, "handoff-data"));
        using var rig = new RegressionRig();
        using var detectedMachine = EnvironmentInventory.UseMachine(new FakeMachine(HostProfile.CurrentPlatform)
        {
            DataRoot = Path.Combine(_root, "handoff-machine"),
        });
        string run = "run-starter-handoff";
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-t 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false }
            : new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false };
        using var starter = Process.Start(start)!;
        try
        {
            var runner = new JournalRunner(Environment.MachineName, starter.Id, starter.StartTime.ToUniversalTime());
            string journal = Path.Combine(RunJournal.LocalDirectory, run, "detached.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
            File.WriteAllText(journal, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["utc"] = DateTime.UtcNow.ToString("O"), ["run"] = run, ["actor"] = "detached", ["kind"] = JournalEntry.CopyIntended,
                ["fields"] = new Dictionary<string, string> { ["runtime"] = Path.Combine(rig.Root, "not-created") },
                ["runner"] = new { machine = runner.Machine, pid = runner.Pid, startedUtc = runner.StartedUtc.ToString("O") },
            }) + "\n");
            var probes = new LocalHostPreflight.Probes(
                Processes: (_, _, _, _) => Task.CompletedTask,
                Port: (_, _, _, _) => Task.CompletedTask,
                Lock: (_, _, _, _) => Task.FromResult(new HostLockResult(HostLockState.Free, null, "free")),
                Desktop: _ => Task.CompletedTask, MacDesktop: () => { }, SteamRunning: () => true, Packaged: () => null);
            var before = await LocalHostPreflight.InspectAsync(new EnvironmentInventory(), [], TimeSpan.FromSeconds(30), probes: probes);
            Assert.Contains(before, problem => problem.Input == "run journal" && problem.Message.Contains(run, StringComparison.Ordinal));

            string control = Directory.CreateDirectory(Path.Combine(rig.Root, "control")).FullName, output = Path.Combine(rig.Root, "output");
            using (SupervisorEnvironment(run, control, Path.Combine(rig.Root, "tool"), output))
            {
                using var preflight = LocalHostPreflight.ReplaceDefaultProbesForTest(probes);
                rig.Write("game/" + ServerRunPlan.ExecutableFor(HostProfile.CurrentPlatform switch
                {
                    "windows" => ServerPlatform.Windows, "macos" => ServerPlatform.MacOS, _ => ServerPlatform.Linux,
                }), System.Text.Encoding.UTF8.GetBytes("fake dedicated executable"));
                string adapter = rig.Write("adapter/NativeSmoke.SessionAdapter.dll",
                    RegressionRig.Assembly("NativeSmoke.SessionAdapter", new RegressionRig.Plugin(SmokeSessionContract.SessionAdapterPluginGuid)));
                DetachedCommandQueue.WriteNew(Path.Combine(control, "input-manifests.json"), new[]
                {
                    new DetachedSession.StagedInput(Path.GetDirectoryName(rig.Parent)!, Path.GetDirectoryName(rig.Parent)!,
                        WorldFixture.Manifest(Path.GetDirectoryName(rig.Parent)!)),
                });
                bool inspected = false;
                int exit = await ServerLoad.RunAsync([
                    "--server", rig.Game, "--mod", rig.Parent,
                    "--cli-manifest", rig.CliManifest(save: true, full: true), "--cli-files", Path.Combine(rig.Root, "cli"),
                    "--search-root", Path.Combine(rig.Root, "deps"), "--adapter", adapter,
                    "--output", output, "--server-only", "--preflight-only",
                ], new ServerLoad.Seams(Inspect: _ => { inspected = true; return Task.FromResult(new CampaignPreflightReport([])); }));
                Assert.Equal(0, exit);
                Assert.True(inspected);
            }
            // The supervisor held the run's control lock only while it ran (#811).
            using (Assert.IsType<DetachedControlLock>(DetachedControlLock.TryTake(control))) { }
            var adopted = File.ReadAllLines(journal).Select(RunJournal.ParseLine).Last();
            Assert.Equal(JournalEntry.ScheduledTaskStarted, adopted.Entry.Kind);
            Assert.Equal(JournalRunner.Current, adopted.Runner);
            Assert.False(starter.HasExited);
        }
        finally
        {
            if (!starter.HasExited) starter.Kill(entireProcessTree: true);
            starter.WaitForExit(5000);
        }
    }

    [Fact]
    public async Task ProductionTaskVerifierRejectsAnAddedActionBeforeRetirementCanStopOrDelete()
    {
        string guard = "if (-not (Test-VtTaskDefinition $found.Definition))";
        int verified = WindowsScheduledTask.RetireScript.IndexOf(guard, StringComparison.Ordinal);
        Assert.True(verified >= 0 && verified < WindowsScheduledTask.RetireScript.IndexOf("$found.Stop", StringComparison.Ordinal));
        Assert.True(verified < WindowsScheduledTask.RetireScript.IndexOf("$folder.DeleteTask", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows()) return;

        var host = new LocalGameHost("this machine", HostShell.Pwsh);
        var context = await WindowsScheduledTask.FindRegistrationContextAsync(host, TimeSpan.FromSeconds(30));
        var task = new WindowsScheduledTask(WindowsScheduledTask.Prefix + "run-verifier", "ValheimTesting detached run run-verifier",
            context.Executable, Path.Combine(_root, "launcher.ps1"), _root, context.PrincipalUserId);
        var result = (await host.RunAsync(WindowsScheduledTask.DefinitionVerifierProbe, task.Variables(), TimeSpan.FromSeconds(30)))
            .EnsureSuccess("Running the Task Scheduler ownership verifier without registering a task");
        Assert.Equal("changed-definition-refused", InteractiveClient.Line(result.Stdout, "VT-TASK "));
    }

    [Fact]
    [Trait("Category", "CiShell")]
    public async Task RegisteredWindowsTaskRoundTripsThroughInspectionAndRetirement()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = new LocalGameHost("this machine", HostShell.Pwsh);
        var context = await WindowsScheduledTask.FindRegistrationContextAsync(host, TimeSpan.FromSeconds(30));
        string run = "run-roundtrip-" + Guid.NewGuid().ToString("N")[..8];
        string launcher = Path.Combine(_root, "roundtrip launcher.ps1");
        File.WriteAllText(launcher, "Start-Sleep -Seconds 120\n");
        var task = new WindowsScheduledTask(WindowsScheduledTask.Prefix + run, "ValheimTesting detached run " + run,
            context.Executable, launcher, Path.GetDirectoryName(context.Executable)!, context.PrincipalUserId);
        bool retired = false;
        try
        {
            await task.RegisterAsync(host, TimeSpan.FromSeconds(30));
            var state = await task.InspectAsync(host, TimeSpan.FromSeconds(30));
            Assert.NotEqual(WindowsScheduledTask.State.Missing, state);
            Assert.NotEqual(WindowsScheduledTask.State.Mismatch, state);
            Assert.True(await task.RetireAsync(host, TimeSpan.FromSeconds(30)));
            retired = true;
            Assert.Equal(WindowsScheduledTask.State.Missing, await task.InspectAsync(host, TimeSpan.FromSeconds(30)));
        }
        finally
        {
            if (!retired)
                await host.RunAsync("""
                    $ErrorActionPreference = 'Stop'
                    $service = New-Object -ComObject Schedule.Service
                    $service.Connect()
                    $folder = $service.GetFolder('\')
                    $taskPath = '\' + $task
                    foreach ($found in @($folder.GetTasks(1) | Where-Object { $_.Path -ceq $taskPath })) {
                        $folder.DeleteTask($found.Path, 0)
                    }
                    """, new Dictionary<string, string> { ["task"] = task.Label }, TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task DetachedStartJournalsCopiesAndTaskBeforeTheFakeSchedulerCreatesAnything()
    {
        string data = Path.Combine(_root, "data"), source = Path.Combine(_root, "mod"), server = Path.Combine(_root, "server"), result = Path.Combine(_root, "result");
        Directory.CreateDirectory(source); Directory.CreateDirectory(server);
        string mod = Path.Combine(source, "Mod.dll"), dependency = Path.Combine(source, "Dependency.dll");
        File.WriteAllText(mod, "pinned mod"); File.WriteAllText(dependency, "adjacent dependency");
        using var machine = new FakeDataRoot(data);
        bool registered = false;
        var output = new StringWriter(); var error = new StringWriter();
        int exit = await DetachedSession.RunAsync(["server-load", "--server", server, "--mod", mod, "--mod", mod, "--server-only", "--output", result], output,
            error, new DetachedSession.Seams(Windows: true, RequireDesktop: () => Task.CompletedTask,
                Register: task =>
                {
                    Assert.False(registered);
                    string run = task.Label["ValheimTesting-detached-".Length..];
                    var records = Directory.GetFiles(Path.Combine(data, "journal", run), "*.jsonl").SelectMany(File.ReadAllLines)
                        .Select(RunJournal.ParseLine).OrderBy(record => record.Utc).ToArray();
                    Assert.Equal(3, records.Count(record => record.Entry.Kind == JournalEntry.CopyIntended));
                    Assert.Equal(3, records.Count(record => record.Entry.Kind == JournalEntry.CopyDone));
                    Assert.Equal(JournalEntry.ScheduledTaskIntended, records[^1].Entry.Kind);
                    Assert.All(records.Where(record => record.Entry.Kind == JournalEntry.CopyDone), record => Assert.True(Directory.Exists(record.Entry.Fields["runtime"])));
                    var stagedMod = Assert.Single(records, record => record.Entry.Kind == JournalEntry.CopyDone &&
                        record.Entry.Fields.GetValueOrDefault("purpose") == "detached mod input");
                    string payload = Path.Combine(stagedMod.Entry.Fields["runtime"], "payload");
                    Assert.Equal("pinned mod", File.ReadAllText(Path.Combine(payload, "Mod.dll")));
                    Assert.Equal("adjacent dependency", File.ReadAllText(Path.Combine(payload, "Dependency.dll")));
                    Assert.Equal(Path.Combine(data, "recovered-detached", run), records.Single(record => record.Entry.Kind == JournalEntry.CopyIntended &&
                        record.Entry.Fields.GetValueOrDefault("purpose") == "detached control and startup logs").Entry.Fields["keepIn"]);
                    File.WriteAllText(mod, "new build");
                    Assert.Equal("pinned mod", File.ReadAllText(Path.Combine(payload, "Mod.dll")));
                    registered = true;
                    return Task.CompletedTask;
                },
                WaitReady: launch => Task.FromResult(new DetachedSession.Ready(launch.Run, Path.Combine(result, "evidence"), 7, "8")),
                ExactOwner: (_, _) => true,
                StageTool: parent =>
                {
                    string payload = "";
                    JournalOwnedDirectory.Create(parent, "detached", "detached tool snapshot", directory =>
                    {
                        payload = Path.Combine(directory, "payload"); Directory.CreateDirectory(payload);
                        File.WriteAllText(Path.Combine(payload, "Valheim.Testing.NativeSmoke.dll"), "tool");
                        File.WriteAllText(Path.Combine(payload, "Valheim.Testing.NativeSmoke.runtimeconfig.json"), "{}");
                    });
                    return payload;
                }, TaskExecutable: @"C:\Program Files\PowerShell\7\pwsh.exe", TaskUser: @"TEST\runner"));
        Assert.True(exit == 0, error.ToString());
        Assert.True(registered);
        Assert.False(Directory.Exists(result));
        Assert.False(Directory.Exists(Path.Combine(data, "runs", "detached")));
        Assert.DoesNotContain(".launch.json", Directory.GetFiles(data, "*", SearchOption.AllDirectories).Select(Path.GetFileName));
    }

    [Fact]
    public void AFailedRetireKeepsTheOwnerMarkerSoARetryCanStillProveOwnership()
    {
        if (OperatingSystem.IsWindows()) return; // Unix permissions make the partial delete deterministic.
        string parent = Path.Combine(_root, "marker-last");
        string path;
        using (RunJournal.UseRun("run-marker"))
            path = JournalOwnedDirectory.Create(parent, "detached", "marker test", directory =>
            {
                Directory.CreateDirectory(Path.Combine(directory, "locked"));
                File.WriteAllText(Path.Combine(directory, "locked", "file"), "x");
            });
        string id = Path.GetFileName(path)["valheim-test-".Length..];
        var fields = new Dictionary<string, string> { ["runtime"] = path, ["ownerId"] = id, ["purpose"] = "marker test", ["completed"] = "true" };
        string locked = Path.Combine(path, "locked");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.ThrowsAny<Exception>(() => JournalOwnedDirectory.Retire("run-marker", fields));
            Assert.True(File.Exists(Path.Combine(path, JournalOwnedDirectory.OwnerFile)), "A partial delete removed the owner marker.");
        }
        finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        JournalOwnedDirectory.Retire("run-marker", fields);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task RecoveryDeletesOnlyTheSelectedRunsDiscardOnlyDirectories()
    {
        string data = Path.Combine(_root, "recovery-data"), parent = Path.Combine(data, "runs"), evidence = Path.Combine(_root, "recovered-evidence");
        using var machine = new FakeDataRoot(data);
        string first, other;
        using (RunJournal.UseRun("run-first"))
        {
            first = JournalOwnedDirectory.Create(parent, "detached", "first", directory =>
            {
                File.WriteAllText(Path.Combine(directory, "file"), "first");
                File.WriteAllText(Path.Combine(directory, "stdout.log"), "startup evidence");
            }, ("keepIn", evidence), ("keepFiles", "stdout.log\nstderr.log"));
            new RunJournal("run-first").AppendLocal("run", JournalEntry.Of(JournalEntry.RunEnded, ("state", "failed"), ("cleanupVerified", "false")));
        }
        using (RunJournal.UseRun("run-other"))
        {
            other = JournalOwnedDirectory.Create(parent, "detached", "other", directory => File.WriteAllText(Path.Combine(directory, "file"), "other"));
            new RunJournal("run-other").AppendLocal("run", JournalEntry.Of(JournalEntry.RunEnded, ("state", "failed"), ("cleanupVerified", "false")));
        }
        var profile = new HostProfile { Kind = "local", Platform = HostProfile.CurrentPlatform, Shell = HostProfile.ShellFor(HostProfile.CurrentPlatform), Lock = Path.Combine(data, "lock") };
        var hosts = new Dictionary<string, HostProfile> { ["local"] = profile };
        var host = new LocalGameHost("local", HostShell.ThisMachine);
        var report = await RunRecovery.RecoverAsync(hosts, _ => host, "run-first", teardown: false, TimeSpan.FromSeconds(30));
        Assert.True(report.Clean);
        Assert.False(Directory.Exists(first));
        Assert.True(Directory.Exists(other));
        Assert.Equal("startup evidence", File.ReadAllText(Path.Combine(evidence, "detached-stdout.log")));
        Assert.Contains(report.Steps, step => step.Outcome.Contains("evidence kept", StringComparison.Ordinal));
        Assert.Equal(JournalRunState.Recoverable, Assert.Single((await RunJournalStatus.InspectAsync(hosts, _ => host, TimeSpan.FromSeconds(30))).Runs,
            run => run.Run == "run-other").State);
    }

    [Fact]
    public void InstalledToolUsesTheRuntimeDotnetHostRatherThanItsUnstagedShim()
    {
        string runtime = Path.Combine(_root, "dotnet", "shared", "Microsoft.NETCore.App", "10.0.0");
        Directory.CreateDirectory(runtime);
        string host = Path.Combine(_root, "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        File.WriteAllText(host, "host");

        Assert.Equal(host, DetachedSession.DotnetHost(runtime));
        string shim = Path.Combine(_root, "tools", "valheim-test.exe");
        string packageDll = Path.Combine(_root, "tools", ".store", "valheim.test", "tools", "net10.0", "any", "Valheim.Testing.NativeSmoke.dll");
        string staged = Path.Combine(_root, "staged-tool");
        var command = DetachedSession.OwnCommandForPaths(staged, shim, packageDll, runtime);
        Assert.Equal(host, command.Program);
        Assert.Equal(new[] { Path.Combine(staged, "Valheim.Testing.NativeSmoke.dll") }, command.Prefix);
        Assert.NotEqual(Path.Combine(staged, "valheim-test.exe"), command.Program);
        File.Delete(host);
        Assert.Throws<InvalidOperationException>(() => DetachedSession.DotnetHost(runtime));
    }

    [Fact]
    public void RuntimeHasTheHostUsedForStagedToolDlls()
    {
        Assert.True(File.Exists(DetachedSession.DotnetHost(RuntimeEnvironment.GetRuntimeDirectory())));
    }

    [Fact]
    public void WindowsTaskScriptsParseBeforeAnyTaskIsRegistered()
    {
        if (!OperatingSystem.IsWindows()) return;
        var launch = new DetachedSession.Launch("run-abcd", "ValheimTesting-detached-run-abcd", _root,
            "ready", "out", "err", "control", "tool", Path.Combine(_root, "launcher.ps1"), "pwsh.exe", @"TEST\runner");
        string[] scripts = [WindowsScheduledTask.RegistrationContextScript, WindowsScheduledTask.RegisterScript, WindowsScheduledTask.InspectScript,
            WindowsScheduledTask.RetireScript, WindowsScheduledTask.DefinitionVerifierProbe,
            DetachedSession.WindowsLauncher(launch, ["VALHEIM_TEST_DETACH_READY=ready"], ["dotnet.exe", "runner.dll"])];
        foreach (string script in scripts)
        {
            string file = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(file, script);
            var start = new ProcessStartInfo("pwsh") { RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("$tokens=$null;$errors=$null;[void][System.Management.Automation.Language.Parser]::ParseFile('" +
                file.Replace("'", "''", StringComparison.Ordinal) + "',[ref]$tokens,[ref]$errors);if($errors.Count){$errors|Out-String|Write-Error;exit 1}");
            using var process = Process.Start(start)!;
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("PowerShell script parser did not finish.");
            }
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        }
    }

    // The environment the scheduled task gives its supervisor (DetachedSession's launcher), restored on dispose.
    internal static IDisposable SupervisorEnvironment(string run, string control, string tool, string output)
    {
        var values = new Dictionary<string, string?>
        {
            ["VALHEIM_TEST_DETACH_RUN"] = run,
            ["VALHEIM_TEST_DETACH_CONTROL"] = control,
            ["VALHEIM_TEST_DETACH_TASK"] = WindowsScheduledTask.Prefix + run,
            ["VALHEIM_TEST_DETACH_TASK_EXECUTABLE"] = OperatingSystem.IsWindows() ? @"C:\Program Files\PowerShell\7\pwsh.exe" : "/usr/local/bin/pwsh",
            ["VALHEIM_TEST_DETACH_TASK_USER"] = @"TEST\runner",
            ["VALHEIM_TEST_DETACH_TOOL"] = tool,
            ["VALHEIM_TEST_DETACH_OUTPUT"] = output,
        };
        var previous = values.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        foreach (var (key, value) in values) Environment.SetEnvironmentVariable(key, value);
        return new Restore(() => { foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value); });
    }

    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }

    // One journal line in this machine's journal, as RunJournal writes it, with the runner chosen by the test.
    internal static void LocalLine(string run, string actor, JournalRunner runner, DateTime utc, JournalEntry entry)
    {
        string file = Path.Combine(RunJournal.LocalDirectory, run, actor + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.AppendAllText(file, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["utc"] = utc.ToString("O"), ["run"] = run, ["actor"] = actor, ["kind"] = entry.Kind, ["fields"] = entry.Fields,
            ["runner"] = new { machine = runner.Machine, pid = runner.Pid, startedUtc = runner.StartedUtc.ToString("O") },
        }) + "\n");
    }

    // #811: the supervisor adopts the run only under its control lock, and holds the lock while it runs. A recovery that took the
    // lock first is clearing the run, so the supervisor refuses before it journals anything.
    [Fact]
    public void ASupervisorAdoptsOnlyUnderTheRunsControlLockAndHoldsItWhileItRuns()
    {
        using var machine = new FakeDataRoot(Path.Combine(_root, "control-lock-data"));
        const string run = "run-control-lock";
        string control = Directory.CreateDirectory(Path.Combine(_root, "control-lock")).FullName;
        string journal = Path.Combine(RunJournal.LocalDirectory, run, "detached.jsonl");
        using var environment = SupervisorEnvironment(run, control, Path.Combine(_root, "tool"), Path.Combine(_root, "output"));

        using (var recovery = DetachedControlLock.TryTake(control))
        {
            Assert.NotNull(recovery);
            var refused = Assert.Throws<InvalidOperationException>(() => DetachedSession.AdoptRun(run));
            Assert.Contains("control lock", refused.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(journal)); // Nothing adopted: the run stays the recovery's to clear.
        }

        using (DetachedSession.AdoptRun(run))
        {
            Assert.Equal(JournalEntry.ScheduledTaskStarted, RunJournal.ParseLine(Assert.Single(File.ReadAllLines(journal))).Entry.Kind);
            Assert.Null(DetachedControlLock.TryTake(control)); // A recovery cannot take it from a live supervisor.
        }
        using (Assert.IsType<DetachedControlLock>(DetachedControlLock.TryTake(control))) { }
    }

    // #811: a supervisor that started before a recovery stopped its task, and reaches adoption only after the recovery released
    // the lock, finds the task journalled retired and refuses before it journals anything.
    [Fact]
    public void ASupervisorDoesNotAdoptARunWhoseTaskARecoveryRetired()
    {
        using var machine = new FakeDataRoot(Path.Combine(_root, "retired-data"));
        const string run = "run-retired-task";
        string control = Directory.CreateDirectory(Path.Combine(_root, "retired-control")).FullName;
        string journal = Path.Combine(RunJournal.LocalDirectory, run, "detached.jsonl");
        using var environment = SupervisorEnvironment(run, control, Path.Combine(_root, "tool"), Path.Combine(_root, "output"));
        new RunJournal(run).AppendLocal("detached", JournalEntry.Of(JournalEntry.ScheduledTaskRetired, ("task", WindowsScheduledTask.Prefix + run)));
        string before = File.ReadAllText(journal);

        var refused = Assert.Throws<InvalidOperationException>(() => DetachedSession.AdoptRun(run));

        Assert.Contains("already retired", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(journal)); // Nothing journalled.
        using (Assert.IsType<DetachedControlLock>(DetachedControlLock.TryTake(control))) { } // The lock was released.
    }

    // #811: detach status reports a live run as a success; only what is left to recover makes it exit 3.
    [Fact]
    public async Task DetachStatusExitsZeroWhileARunIsLiveAndThreeOnlyForWhatIsLeftToRecover()
    {
        using var machine = new FakeDataRoot(Path.Combine(_root, "status-data"));
        using var detected = EnvironmentInventory.UseMachine(new FakeMachine(HostProfile.CurrentPlatform) { DataRoot = Path.Combine(_root, "status-machine") });
        var copy = JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", Path.Combine(_root, "status-copy")));
        LocalLine("run-status-live", "detached", JournalRunner.Current, DateTime.UtcNow.AddMinutes(-2), copy);
        LocalLine("run-status-ended", "detached", RunJournalStatusTests.Gone, DateTime.UtcNow.AddMinutes(-3),
            JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", Path.Combine(_root, "status-copy"))));

        var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(0, await DetachedSession.RunAsync(["status"], output, error, new DetachedSession.Seams(Windows: true)));
        Assert.Contains("LIVE run-status-live", output.ToString());

        LocalLine("run-status-left", "detached", RunJournalStatusTests.Gone, DateTime.UtcNow.AddMinutes(-1), copy);
        output = new StringWriter();
        Assert.Equal(3, await DetachedSession.RunAsync(["status"], output, error, new DetachedSession.Seams(Windows: true)));
        Assert.Contains("RECOVERABLE run-status-left", output.ToString());
    }

    // #811: a repeated detach finish, after the first finished and recovered the run, reports the end the run journalled, not
    // whether its supervisor still runs: passed for a run that passed; a failed end, or none (its supervisor died), fails.
    [Theory]
    [InlineData("passed", 0)]
    [InlineData("failed", 1)]
    [InlineData(null, 1)]
    public async Task ARepeatedFinishReportsTheRunsJournalledEnd(string? end, int expected)
    {
        using var machine = new FakeDataRoot(Path.Combine(_root, "finish-data"));
        using var detected = EnvironmentInventory.UseMachine(new FakeMachine(HostProfile.CurrentPlatform) { DataRoot = Path.Combine(_root, "finish-machine") });
        string run = "run-finished-" + (end ?? "none");
        string control = Path.Combine(_root, "finish-control"), output = Directory.CreateDirectory(Path.Combine(_root, "finish-output")).FullName;
        FinishedRun(run, control, output, end, retired: true);

        var text = new StringWriter(); var error = new StringWriter();
        int exit = await DetachedSession.RunAsync(["finish", "--run", run], text, error, new DetachedSession.Seams(Windows: true));

        Assert.True(exit == expected, text + "\n" + error);
        Assert.Contains((expected == 0 ? "DETACHED FINISHED run " : "DETACHED RECOVERED FAILED run ") + run, text.ToString());
        if (end != null) Assert.Contains("its journalled end: " + end, text.ToString());
    }

    // A detached run whose supervisor ended it: the starter's task intent, the supervisor's adoption and end, and (once a first
    // finish recovered it) the recovery's retirement of the task.
    internal static void FinishedRun(string run, string control, string output, string? end, bool retired)
    {
        var starter = RunJournalStatusTests.Gone;
        var supervisor = new JournalRunner(Environment.MachineName, 999_999_997, DateTime.UtcNow.AddHours(-2));
        var task = new WindowsScheduledTask(WindowsScheduledTask.Prefix + run, "ValheimTesting detached run " + run,
            @"C:\Program Files\PowerShell\7\pwsh.exe", Path.Combine(control, "launcher.ps1"), Path.Combine(control, "tool"), @"TEST\runner");
        var at = DateTime.UtcNow.AddMinutes(-30);
        LocalLine(run, "detached", starter, at, task.Entry(JournalEntry.ScheduledTaskIntended, ("control", control), ("output", output)));
        LocalLine(run, "detached", supervisor, at.AddMinutes(1), task.Entry(JournalEntry.ScheduledTaskStarted, ("control", control), ("output", output)));
        if (end != null) LocalLine(run, "run", supervisor, at.AddMinutes(2), JournalEntry.Of(JournalEntry.RunEnded, ("state", end), ("cleanupVerified", "true")));
        if (retired) LocalLine(run, RunRecovery.Actor, JournalRunner.Current, at.AddMinutes(3), task.Entry(JournalEntry.ScheduledTaskRetired));
    }
}

// #811 on the real Task Scheduler: the reviewed case, a finish after the supervisor ended with its task's record not yet
// retired. Recovery finds the task gone; the verdict is the run's journalled end, so a run that passed is reported passed.
[Trait("Os", "windows")]
[Collection(nameof(DetachedEnvironmentVariables))]
public sealed class WindowsDetachedFinishTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-detached-finish-").FullName;
    public void Dispose() => DirectoryLinks.DeleteTree(_root);

    [OsFact("windows")]
    public async Task AFinishAfterTheSupervisorEndedReportsThePassedEndItJournalled()
    {
        using var machine = new FakeDataRoot(Path.Combine(_root, "data"));
        using var detected = EnvironmentInventory.UseMachine(new FakeMachine(HostProfile.CurrentPlatform) { DataRoot = Path.Combine(_root, "machine") });
        string run = "run-ended-" + Guid.NewGuid().ToString("N")[..8]; // No task of this name is registered.
        DetachedSessionTests.FinishedRun(run, Path.Combine(_root, "control"), Directory.CreateDirectory(Path.Combine(_root, "output")).FullName,
            "passed", retired: false);

        var text = new StringWriter(); var error = new StringWriter();
        int exit = await DetachedSession.RunAsync(["finish", "--run", run], text, error);

        Assert.True(exit == 0, text + "\n" + error);
        Assert.Contains("DETACHED FINISHED run " + run + "; its journalled end: passed", text.ToString());
    }
}
