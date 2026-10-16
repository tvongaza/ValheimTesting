using System.Globalization;
using System.Text.Json;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>One thing a recovery did or could not do, on one host.</summary>
internal sealed record RecoveryStep(string Host, string What, string Outcome, bool Failed);

/// <summary>The game logs a recovery kept from one copy (in <see cref="Folder"/>), and those it left out as older than the copy.</summary>
internal sealed record RecoveredLogs(string Folder, IReadOnlyList<string> Kept, IReadOnlyList<string> Older)
{
    /// <summary>The copy's step outcome once it is removed.</summary>
    public string Describe() => (Kept.Count == 0 ? "removed (it held no game logs of the run)" : $"removed; its game logs are in {Folder} ({string.Join(", ", Kept)})") +
        (Older.Count == 0 ? "" : $"; left out as older than the copy: {string.Join(", ", Older)}");
}

/// <summary>A recovery of one run: refused before anything changed (with why), or its steps.</summary>
internal sealed record RecoveryReport(string Run, JournalRunState Before, string? Refused, IReadOnlyList<RecoveryStep> Steps)
{
    public JournalRunState? After { get; init; }
    public IReadOnlyList<JournalItem> Remaining { get; init; } = [];
    /// <summary>Hosts whose journals could not be read and that the run left no readable entry on: whatever the run left only there is not part of this result.</summary>
    public IReadOnlyList<string> NotRead { get; init; } = [];
    public bool Recovered => Refused == null && Steps.All(step => !step.Failed);
    public bool Clean => Recovered && After == JournalRunState.Ended && Remaining.Count == 0;
}

/// <summary>
/// <c>valheim-test env recover|teardown --run ID</c> (#257 step 4): clears what one run left, as <see cref="RunJournalStatus"/>
/// judged it, and nothing else. A run that is live, unknown or unrecoverable is refused before anything changes. In phases
/// across every host: each process the run started is stopped only when its ID, start identity and command line still all
/// match the journal (Q2), checked again right before the stops; then, on each host where none was left, under the host's lock
/// (the run's own when it still holds it, else taken for the recovery), its disposable characters and prepared copies are
/// retired once no game conflicts; then, only when no process was left on any host and every host was read, its Steam leases
/// are released by their own lease id; last, each lock by its own claimant on a host where nothing of the run is left. Each effect is journalled after it happens, so <c>env status</c> shows what is left, and a failed step leaves the
/// rest of that host for a later recovery. Recover leaves what the run kept on purpose; teardown removes that too. Nothing is
/// removed by age (Q8).
/// </summary>
internal static class RunRecovery
{
    /// <summary>The actor a recovery journals as on every host: its entries never make the recovering process the run's runner.</summary>
    public const string Actor = "recovery";

    // How long a process killed by a recovery has to exit before the stop says it is still running.
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a recovery lets a process quit when asked before it kills it: what its own stop would have given it (#665). A
    /// server boot journals its plan's <see cref="ServerRunPlan.QuitSeconds"/> (<c>quitSeconds</c>), taken at most up to a plan's
    /// maximum (a journal is a file on the host, which an edit or a cut line could aim anywhere); a client journals none and gets its
    /// session's default <see cref="ClientSession.QuitTimeout"/>.
    /// </summary>
    internal static TimeSpan QuitBudget(IReadOnlyDictionary<string, string> fields) =>
        fields.GetValueOrDefault("quitSeconds") is { } text && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds)
            ? TimeSpan.FromSeconds(Math.Min(seconds, ServerRunPlan.MaximumQuitSeconds)) : ClientSession.DefaultQuitTimeout;

    public static Task<RecoveryReport> RecoverAsync(IReadOnlyDictionary<string, HostProfile> hosts, Func<string, IGameHost> hostFactory, string runId,
        bool teardown, TimeSpan timeout, CancellationToken cancellation = default, string? leaseHost = null, string? leaseDirectory = null,
        bool launchGone = false) =>
        RecoverCoreAsync(hosts, hostFactory, runId, teardown, timeout, cancellation, leaseHost, leaseDirectory, launchGone, null);

    private static async Task<RecoveryReport> RecoverCoreAsync(IReadOnlyDictionary<string, HostProfile> hosts, Func<string, IGameHost> hostFactory, string runId,
        bool teardown, TimeSpan timeout, CancellationToken cancellation, string? leaseHost, string? leaseDirectory,
        bool launchGone, IDictionary<string, JournaledHostLock>? recoveryClaims)
    {
        var journal = new RunJournal(runId);
        var status = await RunJournalStatus.InspectAsync(hosts, hostFactory, timeout, cancellation, leaseHost, leaseDirectory).ConfigureAwait(false);
        var run = status.Runs.SingleOrDefault(candidate => candidate.Run == runId);
        if (run == null)
            return new(runId, JournalRunState.Unknown, "no readable host's journal names this run" +
                string.Concat(status.Hosts.Where(host => host.Error != null).Select(host => $"; host {host.Name} could not be read ({host.Error})")), []);
        if (launchGone)
        {
            var claims = new Dictionary<string, JournaledHostLock>(StringComparer.Ordinal);
            RecoveryReport result;
            try
            {
                var abandoned = await AbandonLaunchesAsync(hosts, hostFactory, journal, run, claims, timeout, cancellation).ConfigureAwait(false);
                if (abandoned.Refused != null || abandoned.Steps.Any(step => step.Failed)) result = abandoned;
                else
                {
                    var recovered = await RecoverCoreAsync(hosts, hostFactory, runId, teardown, timeout, cancellation,
                        leaseHost, leaseDirectory, launchGone: false, recoveryClaims: claims).ConfigureAwait(false);
                    var combinedSteps = abandoned.Steps.Concat(recovered.Steps).ToList();
                    if (recovered.Refused != null)
                        combinedSteps.Add(new("run", "remaining recovery", "not attempted after the launch was journalled abandoned: " + recovered.Refused, true));
                    result = new(runId, run.State, null, combinedSteps)
                    {
                        After = recovered.After, Remaining = recovered.Remaining, NotRead = recovered.NotRead,
                    };
                }
            }
            catch
            {
                await ReleaseRecoveryClaimsAsync(claims).ConfigureAwait(false);
                throw;
            }
            var released = await ReleaseRecoveryClaimsAsync(claims).ConfigureAwait(false);
            return released.Count == 0 ? result : result with { Steps = result.Steps.Concat(released).ToList() };
        }
        string? Refusal(JournalRunStatus judged) => judged.State switch
        {
            JournalRunState.Live => "it is still going: " + judged.Reason,
            JournalRunState.Unknown => judged.Reason,
            JournalRunState.Unrecoverable => judged.Reason + ": " + string.Join("; ", judged.Items.Where(item => item.Unrecoverable).Select(item => $"{item.Kind} {item.What} on {item.Host}: {item.Status}")),
            JournalRunState.Kept when !teardown => "it kept what it left on purpose; env teardown --run " + runId + " removes it",
            _ => null,
        };
        string? refused = Refusal(run);
        if (refused != null) return new(runId, run.State, refused, []);

        // A detached run (#811): its scheduled supervisor may adopt the run between that read and the first stop, as the run's
        // runner. Under the run's control lock no supervisor can adopt it, and one that already did holds the lock while it runs;
        // so the run is read again under the lock, and that read is what the recovery acts on. Released once its task is retired.
        if (s_afterFirstReadForTest.Value is { } afterFirstRead) await afterFirstRead().ConfigureAwait(false);
        using var control = new ControlLocks();
        if (control.Take(run, hosts, runId) is { } held) return new(runId, run.State, held, []);
        if (control.Any)
        {
            status = await RunJournalStatus.InspectAsync(hosts, hostFactory, timeout, cancellation, leaseHost, leaseDirectory).ConfigureAwait(false);
            run = status.Runs.SingleOrDefault(candidate => candidate.Run == runId);
            if (run == null) return new(runId, JournalRunState.Unknown, "no readable host's journal names this run any more", []);
            if (Refusal(run) is { } now) return new(runId, run.State, now, []);
        }

        var items = run.Items.Where(item => teardown || !item.Kept).ToList();
        if (items.Count == 0 && run.SettledLaunches.Count == 0)
        {
            // A local runner can prove that an interrupted run left nothing. Record that conclusion so another machine,
            // which cannot verify a Mac/Linux runner's start identity, does not mistake the old run for a live one.
            if (run.State != JournalRunState.Ended || run.EndRecorded)
                return new(runId, run.State, null, []) { After = run.State, Remaining = run.Items };
            if (status.Hosts.Any(host => host.Error != null))
                return new(runId, JournalRunState.Unknown, "not every host journal could be read; cannot record a clean end", []);
            string name = run.Hosts[0];
            try
            {
                await RunFinish.RecordForGoneRunnerAsync(journal, hostFactory(name), RunJournal.DirectoryFor(hosts[name]),
                    new RunEnd(RunOutcome.Recovered, CleanupProven: true), timeout, cancellation).ConfigureAwait(false);
                return new(runId, JournalRunState.Ended, null, [new(name, "run end", "recorded after proving its runner gone and no owned state remains", false)])
                { After = JournalRunState.Ended };
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return new(runId, JournalRunState.Unknown, "could not record the clean end: " + error.Message, []);
            }
        }
        var steps = new List<RecoveryStep>();
        var connections = items.Concat(run.SettledLaunches).Select(item => item.Host).Distinct(StringComparer.Ordinal).ToDictionary(name => name, hostFactory, StringComparer.Ordinal);
        void Step(string host, string what, string outcome, bool failed = false) => steps.Add(new(host, what, outcome, failed));
        bool Failed(string host) => steps.Any(step => step.Host == host && step.Failed);
        async Task Note(string name, JournalEntry entry)
        {
            // Most notes follow an effect. Launch settlement also authorizes deleting its ownership files, so losing that
            // line must keep the host's copies: a later status still needs the pid/launcher record to resolve the launch.
            try { await journal.AppendAsync(connections[name], RunJournal.DirectoryFor(hosts[name]), Actor, entry, timeout, cancellation).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            { Step(name, "journal " + entry.Kind, "could not be journalled: " + error.Message, failed: entry.Kind == JournalEntry.LaunchSettled); }
        }
        IEnumerable<JournalItem> Of(string kind) => items.Where(item => item.Kind == kind);
        async Task RetireMacClientAgent(string hostName, string? directory)
        {
            if (hosts[hostName] is not { Kind: "ssh", Platform: "macos" } ||
                directory is not { Length: > 0 }) return;
            var result = (await connections[hostName].RunAsync(InteractiveScripts.MacAgentRetire,
                new Dictionary<string, string> { ["dir"] = directory }, timeout, cancellation).ConfigureAwait(false))
                .EnsureSuccess($"Removing the Mac client GUI agent on {hostName}");
            if (InteractiveClient.Line(result.Stdout, "VT-AGENT ") != "removed")
                throw new HostOperationException($"The Mac client GUI agent on {hostName} could not be proven removed", result);
        }
        // A process adopted from a launch's pid file: once it is stopped or gone, its launch is settled too. A Windows launch's
        // launcher and start task, which its ownership record names (#814), are retired first, through the launcher's one owner,
        // only while the launcher is still the recorded one; a launch that cannot be retired stays unsettled.
        async Task SettleAdopted(JournalItem process)
        {
            if (process.Fields.GetValueOrDefault("adoptedFrom") is not { Length: > 0 } directory) return;
            if (process.Fields.ContainsKey("launcherPid"))
            {
                var owner = HostServerLauncherRecord.FromFields(process.Fields);
                string what = $"launcher {owner.LauncherPid} (started {owner.LauncherStartIdentity})";
                try
                {
                    string retired = await HostServerLauncher.RetireAsync(connections[process.Host], owner, directory, KillWait, cancellation).ConfigureAwait(false);
                    Step(process.Host, what, (retired == "gone" ? "already gone" : "stopped") + $"; its start task {owner.TaskName} removed if it was left");
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    Step(process.Host, what, "not stopped: " + error.Message, failed: true);
                    return;
                }
            }
            await Note(process.Host, Settled(process.Actor, directory)).ConfigureAwait(false);
        }
        bool Local(JournalItem item) => item.Fields.GetValueOrDefault("local") == "true" && hosts[item.Host].Kind == "local";

        // 0. A detached supervisor's OS task first. Its exact definition is checked again before it is stopped and removed;
        // a failed removal keeps every directory the task may still use.
        foreach (var taskItem in Of("scheduled-task"))
        {
            string what = "scheduled task " + taskItem.What;
            try
            {
                var task = WindowsScheduledTask.FromJournal(taskItem.Fields, runId);
                bool removed = await task.RetireAsync(connections[taskItem.Host], timeout, cancellation).ConfigureAwait(false);
                Step(taskItem.Host, what, removed ? "ended and removed" : "already gone");
                await Note(taskItem.Host, task.Entry(JournalEntry.ScheduledTaskRetired)).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { Step(taskItem.Host, what, "not removed: " + error.Message, failed: true); }
        }
        // With its task gone no supervisor can start; the control directory the lock is in may now be removed with the run's copies.
        control.Dispose();

        // 0b. A server start interrupted before its pid file existed (#800): the game its launcher recorded is stopped first,
        // as its run would have stopped it (asked to quit, then killed), once its ID, start time and command line still match;
        // then the launcher's own group or tree, and on Windows its task registration, only while the launcher is still the one
        // recorded. A reused ID is never touched.
        foreach (var launcher in Of("launcher"))
        {
            string what = "launcher " + launcher.What;
            try
            {
                var fields = launcher.Fields;
                var owner = HostServerLauncherRecord.FromFields(fields);
                var host = connections[launcher.Host];
                string boot = fields["bootDirectory"], commandLine = fields.GetValueOrDefault("expectedCommandLineSha256") ?? "";
                string gameOutcome = "";
                if (owner.GamePid is { } game && owner.GameStartIdentity is { } start)
                {
                    var found = (await HostProcessProbe.ProbeAsync(host, [(game, start)], timeout, cancellation).ConfigureAwait(false))[(game, start)];
                    if (found.State is ProbedState.Gone or ProbedState.Reused) gameOutcome = "; its recorded game was already gone";
                    else if (found.State != ProbedState.Same || commandLine.Length == 0 ||
                        !string.Equals(found.CommandLineSha256, commandLine, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"its recorded game {game} no longer has its ID, start time and command line; nothing was stopped");
                    else
                    {
                        var quit = QuitBudget(fields);
                        if (hosts[launcher.Host].Platform == "macos")
                            await MacProcess.StopAsync(host, $"Mac process {game} on {host.Name}", game, start, quit, KillWait, commandLine, cancellation).ConfigureAwait(false);
                        else
                            await HostServerProcess.StopOnHostAsync(host, owner.Platform, game, start, boot, quit, KillWait, cancellation).ConfigureAwait(false);
                        gameOutcome = "; its recorded game was stopped";
                    }
                }
                string retired = await HostServerLauncher.RetireAsync(host, owner, boot, KillWait, cancellation).ConfigureAwait(false);
                Step(launcher.Host, what, (retired == "gone" ? "already gone" : "stopped") + gameOutcome);
                await Note(launcher.Host, Settled(launcher.Actor, boot)).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { Step(launcher.Host, what, "not stopped: " + error.Message, failed: true); }
        }

        // 1. Every process on every host first: until all are proven gone, nothing they may use is touched anywhere.
        foreach (var group in Of("process").GroupBy(item => item.Host, StringComparer.Ordinal))
        {
            var host = connections[group.Key];
            var asked = group.Select(item => (Pid: int.Parse(item.Fields["pid"], NumberStyles.None, CultureInfo.InvariantCulture), Start: item.Fields["startIdentity"])).ToList();
            IReadOnlyDictionary<(int Pid, string StartIdentity), ProbedProcess> now;
            try { now = await HostProcessProbe.ProbeAsync(host, asked, timeout, cancellation).ConfigureAwait(false); } // checked again right before the stops
            catch (Exception error) when (error is not OperationCanceledException)
            {
                foreach (var process in group) Step(group.Key, "process " + process.What, "not stopped: " + error.Message, failed: true);
                continue;
            }
            foreach (var process in group)
            {
                string what = "process " + process.What;
                int pid = int.Parse(process.Fields["pid"], NumberStyles.None, CultureInfo.InvariantCulture);
                string start = process.Fields["startIdentity"], commandLine = process.Fields.GetValueOrDefault("commandLineSha256") ?? "";
                var probed = now[(pid, start)];
                try
                {
                    if (probed.State is ProbedState.Gone or ProbedState.Reused)
                    {
                        await RetireMacClientAgent(group.Key, process.Fields.GetValueOrDefault("launchDirectory")).ConfigureAwait(false);
                        Step(group.Key, what, "already gone");
                        await Note(group.Key, Stopped(pid, start)).ConfigureAwait(false);
                        await SettleAdopted(process).ConfigureAwait(false);
                        continue;
                    }
                    if (probed.State != ProbedState.Same || commandLine.Length == 0 || !string.Equals(probed.CommandLineSha256, commandLine, StringComparison.OrdinalIgnoreCase))
                    {
                        Step(group.Key, what, "not stopped: its ID, start time and command line no longer all match the journal", failed: true);
                        continue;
                    }
                    // Stopped as its own run would have stopped it: asked to quit, given its quit budget, then killed (#665). On macOS a
                    // server and a client share the one stop (MacProcess), which checks the command line again itself; a Mac client's GUI
                    // agent is retired next. Elsewhere by its kind's stop: the server's (SIGINT, or Ctrl+Break) or the client's.
                    var quit = QuitBudget(process.Fields);
                    bool quitWhenAsked, alreadyGone;
                    if (hosts[group.Key].Platform == "macos")
                    {
                        string word = await MacProcess.StopAsync(host, $"Mac process {pid} on {host.Name}", pid, start, quit, KillWait, commandLine, cancellation).ConfigureAwait(false);
                        quitWhenAsked = word == "quit"; alreadyGone = word == "gone";
                    }
                    else if (process.Fields.GetValueOrDefault("bootDirectory") is { Length: > 0 } boot)
                    {
                        var stopped = await HostServerProcess.StopOnHostAsync(host, host.Shell.Kind == HostShellKind.PowerShell ? ClientPlatform.Windows : ClientPlatform.Linux,
                            pid, start, boot, quit, KillWait, cancellation).ConfigureAwait(false);
                        quitWhenAsked = stopped == HostServerStop.Quit; alreadyGone = stopped == HostServerStop.AlreadyGone;
                    }
                    else
                    {
                        var owned = new InteractiveClientProcess(host, host.Shell.Kind == HostShellKind.PowerShell ? ClientPlatform.Windows : ClientPlatform.Linux,
                            pid, start, process.Fields.GetValueOrDefault("launchDirectory") ?? "", null);
                        var stopped = await owned.StopAsync(quit, KillWait, cancellation).ConfigureAwait(false);
                        quitWhenAsked = stopped == InteractiveStop.Quit; alreadyGone = stopped == InteractiveStop.AlreadyGone;
                    }
                    await RetireMacClientAgent(group.Key, process.Fields.GetValueOrDefault("launchDirectory")).ConfigureAwait(false);
                    Step(group.Key, what, alreadyGone ? "already gone" : quitWhenAsked ? "quit when asked" : "stopped");
                    await Note(group.Key, Stopped(pid, start)).ConfigureAwait(false);
                    await SettleAdopted(process).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException) { Step(group.Key, what, "not stopped: " + error.Message, failed: true); }
            }
        }
        // A launch never journalled as started is settled once its pid file's process is gone, so no later status depends on that
        // file still being there.
        foreach (var launch in run.SettledLaunches)
        {
            Step(launch.Host, "launch " + launch.What, "settled: its pid file's process is gone");
            await Note(launch.Host, Settled(launch.Actor, launch.What)).ConfigureAwait(false);
        }
        // An agent can outlive a game that exited on its own, or a stop whose bootout reply was lost.
        // Keep it visible to env status until this exact run-owned launch directory is retired.
        foreach (var agent in Of("mac-agent"))
        {
            string what = "Mac GUI agent " + agent.What;
            if (Failed(agent.Host)) { Step(agent.Host, what, "not attempted: its process was not proven stopped", failed: true); continue; }
            try
            {
                await RetireMacClientAgent(agent.Host, agent.What).ConfigureAwait(false);
                Step(agent.Host, what, "removed");
                await Note(agent.Host, JournalEntry.Of(JournalEntry.MacAgentRetired, ("launchDirectory", agent.What))).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException) { Step(agent.Host, what, "not removed: " + error.Message, failed: true); }
        }
        bool processLeft = steps.Any(step => step.Failed);

        // 2a. Copies this machine journalled in process (WorldFixture): no host lock; OwnedCopies refuses one a process uses.
        foreach (var copy in Of("copy").Where(Local))
        {
            string what = $"copy {copy.What}";
            if (Failed(copy.Host)) { Step(copy.Host, what, "not attempted: a process on this host was not stopped", failed: true); continue; }
            try
            {
                string path = copy.What;
                if (!Directory.Exists(path)) { Step(copy.Host, what, "already gone"); await Note(copy.Host, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", path), ("local", "true"))).ConfigureAwait(false); continue; }
                if (copy.Fields.GetValueOrDefault("cleanup") == "delete")
                {
                    var evidence = JournalOwnedDirectory.Retire(runId, copy.Fields);
                    Step(copy.Host, what, "removed (discard-only owned directory)" +
                        (evidence.Count == 0 ? "" : "; evidence kept in " + string.Join(", ", evidence)));
                    await Note(copy.Host, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", path), ("local", "true"),
                        ("cleanup", "delete"), ("ownerId", copy.Fields["ownerId"]), ("purpose", copy.Fields.GetValueOrDefault("purpose") ?? ""))).ConfigureAwait(false);
                    continue;
                }
                if (!OwnedCopies.IsCopyName(path)) throw new InvalidDataException("not a valheim-test-<32 hex> copy directory");
                var found = OwnedCopies.Find(path);
                if (found.Count == 0)
                {
                    // Interrupted before its manifest was written: nothing in it was the run's yet.
                    WorldFixture.DeleteTree(path);
                    Step(copy.Host, what, "removed (the copy never finished)");
                    await Note(copy.Host, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", path), ("local", "true"))).ConfigureAwait(false);
                }
                else if (found[0].Kind == OwnedCopyKind.World)
                {
                    // A run's save is evidence: handed over to the output it is in, as a run that ends does.
                    Step(copy.Host, what, "kept as the run's save (handed over to its output)");
                    await Note(copy.Host, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", path), ("local", "true"), ("handedOver", "true"))).ConfigureAwait(false);
                }
                else
                {
                    var retired = OwnedCopies.Remove(path);
                    Step(copy.Host, what, $"removed; its changes are in {retired.KeptIn}");
                    await Note(copy.Host, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", path), ("local", "true"), ("keptIn", retired.KeptIn))).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException) { Step(copy.Host, what, "not removed: " + error.Message, failed: true); }
        }

        // 2b. Each host's characters and prepared copies, under its lock, once no process of the run is left on it.
        foreach (var group in Of("user-files").Concat(Of("character")).Concat(Of("copy").Where(item => !Local(item))).GroupBy(item => item.Host, StringComparer.Ordinal))
        {
            var host = connections[group.Key];
            var lists = group.Where(item => item.Kind == "user-files").ToList();
            var characters = group.Where(item => item.Kind == "character").ToList();
            // Move a journalled fixture out of user saves before handling its collected directory on the same host.
            var copies = group.Where(item => item.Kind == "copy")
                .OrderBy(item => item.Fields.GetValueOrDefault("holds") == HostedWorldOnHost.Holds ? 0 : 1).ToList();
            if (Failed(group.Key))
            {
                // A copy a process may still run from, and a character it may still use, stay.
                foreach (var item in group) Step(group.Key, $"{item.Kind} {item.What}", "not attempted: a process on this host was not stopped", failed: true);
                continue;
            }
            HostLock? claim = null;
            try
            {
                // Under the run's own lock when it still holds it, else the host's lock taken for the recovery: another run that holds it is going.
                if (!Of("lock").Any(item => item.Host == group.Key))
                    claim = await host.AcquireLockAsync(hosts[group.Key].Lock, "recover " + runId, timeout, cancellation).ConfigureAwait(false);
                // A profile's executable lives in its source install, outside the folder recovery retires.
                // Direct local clients do not journal their process here, so require the whole client session
                // to be idle before retiring their profile, even after the runner itself has died.
                await HostedRuntimeStage.RequireStoppedAsync(host, timeout,
                    runtimes: copies.Select(copy => copy.Fields.GetValueOrDefault("launchRoot") ?? copy.What).ToList(),
                    // An actor's caller-chosen name is not its role: every client profile journals runtimeKind client.
                    clientSession: characters.Count != 0 || copies.Any(copy => copy.Fields.GetValueOrDefault("runtimeKind") == "client") ||
                        lists.Any(item => UserSaveFiles.KindOf(item.Fields) == UserSaveFiles.Kind.ClientServerList))
                    .ConfigureAwait(false);
                foreach (var item in lists)
                {
                    // The user's own Valheim files a game wrote outside the run (#822): put back as the run found them.
                    string what = UserSaveFiles.KindOf(item.Fields) == UserSaveFiles.Kind.MacServerLists ? "Mac server access lists" : "client recent-servers list";
                    try
                    {
                        if (UserSaveFiles.KindOf(item.Fields) == UserSaveFiles.Kind.MacServerLists && hosts[group.Key].Platform != "macos")
                            throw new InvalidDataException("Mac server access lists can be restored only on their Mac host.");
                        var files = host.Kind == GameHostKind.Local
                            ? UserSaveFiles.FromJournal(item.Fields, runId, HostedWorld.DefaultSaveDirectory(GameLaunch.CurrentClientHost))
                            : UserSaveFiles.FromJournal(item.Fields, runId, await HostedWorldOnHost.ClientSaveRootAsync(host, timeout, cancellation).ConfigureAwait(false),
                                UserSaveFilesOnHost.Remote(host, timeout));
                        await files.RestoreAsync(entry => journal.AppendAsync(host, RunJournal.DirectoryFor(hosts[group.Key]), Actor, entry, timeout, cancellation)).ConfigureAwait(false);
                        Step(group.Key, what, "restored; versions written during the run kept in its backup");
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { Step(group.Key, what, "not restored: " + error.Message, failed: true); }
                }
                if (Failed(group.Key)) continue; // preserve the run's other copies and lock until its user files are restored
                foreach (var character in characters)
                {
                    string what = $"character {character.What}";
                    try
                    {
                        // Intent without completion is not ownership. A missing file is safe to clear;
                        // only regression copies with an exact registered hash may be retired.
                        if (character.Fields.GetValueOrDefault("staged") != "true")
                        {
                            string fileName = character.Fields["fileName"];
                            HostListing? listing;
                            // Hash only this character's possible files, including backups. Retirement
                            // removes all of them, so an unexpected backup must block retirement.
                            try { listing = await HostInstall.ListCaseInsensitiveAsync(host, character.Fields["characters"], timeout,
                                DisposableCharacterStore.OwnedFilePatterns(fileName, HostInstall.LiteralPattern), cancellation).ConfigureAwait(false); }
                            catch (DirectoryNotFoundException) { listing = null; }
                            var owned = listing?.Files.Where(file => DisposableCharacterStore.IsCharacterFile(Path.GetFileName(file.Key), fileName)).ToArray() ?? [];
                            if (owned.Length == 0)
                            {
                                Step(group.Key, what, "no character copy was written");
                                await Note(group.Key, JournalEntry.Of(JournalEntry.CharacterRetired, ("fileName", fileName))).ConfigureAwait(false);
                                continue;
                            }
                            // A hosted stage checks for collisions only inside its install command, after
                            // intent was journalled. Even identical bytes could be a pre-existing save.
                            // Its unconfirmed file requires a human decision; never retire by name.
                            if (character.Fields.GetValueOrDefault("characterKind") != "regression")
                                throw new InvalidDataException("the hosted character stage never confirmed completion; a same-named save may be personal even if its bytes match. Move it out of characters_local for safekeeping after inspection, then retry recovery");
                            string expected = character.Fields.GetValueOrDefault("expectedSha256") ?? "";
                            if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
                                throw new InvalidDataException("the registered character copy did not record completion or its expected SHA-256; its filename is not proof of ownership. Inspect the save and journal, move the file out of characters_local for safekeeping if personal, then retry recovery");
                            if (owned.Length != 1 || !owned[0].Key.Equals(DisposableCharacterStore.SaveFile(fileName),
                                    host.Shell.Kind == HostShellKind.PowerShell ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                                !owned[0].Value.Equals(expected, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("the registered character copy did not record completion and the file differs from its journalled SHA-256; inspect the save without deleting it, move it out of characters_local for safekeeping if personal, then retry recovery");
                        }
                        await HostedCharacterStage.RetireAsync(host, new HostedCampaignCharacter
                        {
                            FileName = character.Fields["fileName"], CharactersLocalDirectory = character.Fields["characters"], SteamUserDataDirectory = character.Fields["userData"],
                        }, timeout).ConfigureAwait(false);
                        Step(group.Key, what, "retired");
                        await Note(group.Key, JournalEntry.Of(JournalEntry.CharacterRetired, ("fileName", character.Fields["fileName"]))).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not OperationCanceledException) { Step(group.Key, what, "not retired: " + error.Message, failed: true); }
                }
                foreach (var copy in copies)
                {
                    string what = $"copy {copy.What}";
                    try
                    {
                        if (!copy.Fields.TryGetValue("stage", out string? stage)) throw new InvalidDataException("its journal names no staging directory");
                        // A hosted world in a campaign client's own worlds (#258 step 8b; this machine's too, #807): moved out into the run's
                        // folder there, never deleted, and only once the world there states the UID the run journalled placing.
                        if (copy.Fields.GetValueOrDefault("holds") == HostedWorldOnHost.Holds)
                        {
                            string keepIn = copy.Fields["keepIn"];
                            var moved = await HostedWorldOnHost.RecoverAsync(host, copy.Fields, copy.What, runId, timeout, cancellation).ConfigureAwait(false);
                            Step(group.Key, what, moved.Count == 0 ? "nothing left in the client's worlds"
                                : $"moved out of the client's worlds into {keepIn} (the run's confirmed placement, world UID {copy.Fields["worldUid"]} as journalled, handed over): {string.Join(", ", moved)}");
                            await Note(group.Key, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", copy.What), ("handedOver", "true"), ("keptIn", keepIn))).ConfigureAwait(false);
                            continue;
                        }
                        // New server evidence remains owned until its verified fetch is durable. Recovery cannot invent a
                        // missing fetch receipt: keep the only copy visible, unless teardown explicitly asks to remove it.
                        if (copy.Fields.GetValueOrDefault("retention") == "after-fetch")
                        {
                            HostedRuntimeStage.RequirePrepared(host, copy.What, runId);
                            if (Failed(group.Key)) throw new IOException("an earlier recovery step on this host failed; its evidence stays");
                            if (!teardown && copy.Fields.GetValueOrDefault("fetched") != "true")
                            {
                                const string why = "its fetch was not proven; its server evidence stays";
                                await Note(group.Key, JournalEntry.Of(JournalEntry.CopyKept, ("runtime", copy.What), ("why", why))).ConfigureAwait(false);
                                Step(group.Key, what, "kept: " + why);
                                continue;
                            }
                            await HostedRuntimeStage.RetireAsync(host, copy.What, stage, timeout, cancellation, runId).ConfigureAwait(false);
                            await Note(group.Key, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", copy.What))).ConfigureAwait(false);
                            Step(group.Key, what, "removed (server evidence fetched or teardown requested)");
                            continue;
                        }
                        // A hosted run's world copy (HostedServerRun journals what each of its copies holds) has no game logs.
                        if (copy.Fields.GetValueOrDefault("holds") == "world")
                        {
                            HostedRuntimeStage.RequirePrepared(host, copy.What, runId);
                            if (copy.Fields.GetValueOrDefault("verified") == "true")
                            {
                                // A hosted run's verified world copy is its save, evidence: handed over where it is, as a run that ends does.
                                Step(group.Key, what, "kept as the run's save (handed over, in its run directory)");
                                await Note(group.Key, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", copy.What), ("handedOver", "true"))).ConfigureAwait(false);
                                continue;
                            }
                            // A ship that never finished is nobody's save, and a world holds no game logs.
                            await HostedRuntimeStage.RetireAsync(host, copy.What, stage, timeout, runId: runId).ConfigureAwait(false);
                            Step(group.Key, what, "removed (the ship never finished)");
                            await Note(group.Key, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", copy.What))).ConfigureAwait(false);
                            continue;
                        }
                        // The journalled stage is read from disk: it must be this copy's own before anything runs on the host, logs included.
                        if (stage.Length != 0) HostedRuntimeStage.RequireStaging(host, HostedRuntimeStage.RequirePrepared(host, copy.What, runId), stage);
                        // The game's logs go with the copy: kept first, so a copy whose logs could not be kept stays (#412).
                        RecoveredLogs logs;
                        try { logs = await KeepLogsAsync(host, copy.What, runId, copy.Actor, copy.SinceUtc, timeout, cancellation).ConfigureAwait(false); }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            throw new IOException($"its game logs could not be kept, so it stays ({error.Message}); recover again once they can be, or remove it by hand", error);
                        }
                        await HostedRuntimeStage.RetireAsync(host, copy.What, stage, timeout, runId: runId).ConfigureAwait(false);
                        Step(group.Key, what, logs.Describe());
                        await Note(group.Key, JournalEntry.Of(JournalEntry.CopyRetired,
                            [("runtime", copy.What), .. logs.Kept.Count == 0 ? Array.Empty<(string, string)>() : [("logsKeptIn", logs.Folder)]])).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is not OperationCanceledException) { Step(group.Key, what, "not removed: " + error.Message, failed: true); }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                foreach (var item in group) Step(group.Key, $"{item.Kind} {item.What}", "not attempted: " + error.Message, failed: true);
            }
            finally
            {
                if (claim != null)
                    try { await claim.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception error) when (error is not OperationCanceledException) { Step(group.Key, "the recovery's lock " + claim.Path, "not released: " + error.Message, failed: true); }
            }
        }

        // 3. Steam leases, only once every process of the run is proven gone on every host, and every host was read: a client
        // still running on an account (here or on a host that could not be read) keeps its lease.
        var unread = status.Hosts.Where(host => host.Error != null).Select(host => host.Name).ToList();
        foreach (var lease in Of("lease"))
        {
            string what = "lease on Steam account " + lease.Fields.GetValueOrDefault("account");
            if (processLeft || unread.Count != 0)
            {
                Step(lease.Host, what, "kept: " + (processLeft ? "a process of the run was not stopped" : $"host {string.Join(", ", unread)} could not be read, and a client there may use it"), failed: true);
                continue;
            }
            try
            {
                var pool = new SteamAccountPool
                {
                    Pool = lease.Fields["pool"], LeaseDirectory = lease.Fields["directory"], Accounts = [new SteamPoolAccount { Name = lease.Fields["account"] }],
                };
                var result = await pool.RunAsync(connections[lease.Host], "release", timeout, cancellation, lease: lease.Fields["leaseId"], account: lease.Fields["account"],
                    number: lease.Fields["number"]).ConfigureAwait(false);
                string verdict = result.Succeeded ? result.Stdout.Split('\n')[0].Trim() : "";
                if (verdict != "VT-LEASE released" && !verdict.StartsWith("VT-LEASE lost ", StringComparison.Ordinal))
                    throw new HostOperationException("The release is not proven", result);
                Step(lease.Host, what, verdict == "VT-LEASE released" ? "released" : "had already ended");
                await Note(lease.Host, JournalEntry.Of(JournalEntry.LeaseReleased, [.. lease.Fields.Select(field => (field.Key, field.Value))])).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException) { Step(lease.Host, what, "not released: " + error.Message, failed: true); }
        }

        // 4. The run's own locks last, each only once nothing of the run is left on its host.
        foreach (var runLock in Of("lock"))
        {
            string what = "lock " + runLock.What;
            JournaledHostLock? heldByRecovery = recoveryClaims != null && recoveryClaims.TryGetValue(runLock.Host, out var recoveryHeld)
                ? recoveryHeld : null;
            bool recoveryClaim = heldByRecovery != null &&
                string.Equals(runLock.Fields.GetValueOrDefault("claimant"), heldByRecovery.Owner, StringComparison.Ordinal);
            if (Failed(runLock.Host))
            {
                Step(runLock.Host, what, recoveryClaim
                    ? "kept as the recovery claim: something of the run is still left on this host"
                    : "kept: something of the run is still left on this host", failed: true);
                // The recovery claim is now the run's open lock. Leave it held and in the journal so preflight keeps refusing
                // this host until a later recovery settles the failed item and releases the exact claimant.
                if (recoveryClaim) recoveryClaims!.Remove(runLock.Host);
                continue;
            }
            try
            {
                string claimant = runLock.Fields["claimant"];
                var released = recoveryClaim
                    ? await heldByRecovery!.ReleaseAsync(CancellationToken.None).ConfigureAwait(false)
                    : await connections[runLock.Host].ReleaseLockAsync(runLock.What, claimant, timeout, cancellation).ConfigureAwait(false);
                if (released.State is not (HostLockState.Released or HostLockState.Free)) throw new HostLockException(released);
                Step(runLock.Host, what, "released");
                // JournaledHostLock couples its own release to the durable note. Older run-owned claims use the recovery note.
                if (!recoveryClaim)
                    await Note(runLock.Host, JournalEntry.Of(JournalEntry.LockReleased, ("lock", runLock.What), ("claimant", claimant))).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException) { Step(runLock.Host, what, "not released: " + error.Message, failed: true); }
        }

        // Each readable host of the run says it was recovered, and how far.
        bool whole = steps.All(step => !step.Failed);
        foreach (string name in run.Hosts)
            try
            {
                await journal.AppendAsync(connections.GetValueOrDefault(name) ?? hostFactory(name), RunJournal.DirectoryFor(hosts[name]), Actor, JournalEntry.Of(JournalEntry.RunRecovered,
                    ("teardown", teardown ? "true" : "false"), ("complete", whole ? "true" : "false")), timeout, cancellation).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException) { Step(name, "journal " + JournalEntry.RunRecovered, "could not be journalled: " + error.Message); }
        JournalRunState? afterState = null;
        IReadOnlyList<JournalItem> remaining = [];
        IReadOnlyList<string> notRead = [];
        if (steps.All(step => !step.Failed))
        {
            // A successful sequence of effects is not proof that the environment is empty:
            // recover intentionally skips kept copies. Read the same journal as env status.
            try
            {
                var after = await RunJournalStatus.InspectAsync(hosts, hostFactory, timeout, cancellation,
                    leaseHost, leaseDirectory).ConfigureAwait(false);
                var checkedRun = after.Runs.SingleOrDefault(candidate => candidate.Run == runId);
                // Judged over the run's own hosts: a host it never touched (powered off) does not fail a recovery that did every step.
                // A host the first read found the run on counts too, though it has gone since.
                var ownUnread = after.Hosts.Where(host => host.Error != null && (run.Hosts.Contains(host.Name) || checkedRun?.Unreadable.Any(own => own.Name == host.Name) == true)).ToList();
                notRead = after.Hosts.Where(host => host.Error != null && !ownUnread.Contains(host)).Select(host => host.Name).ToList();
                if (checkedRun == null || ownUnread.Count != 0)
                    Step("run", "post-recovery status", "could not find the run or read every journal it rests on" +
                        string.Concat(ownUnread.Select(host => $"; host {host.Name} ({host.Error})")), failed: true);
                else
                {
                    afterState = checkedRun.State;
                    remaining = checkedRun.Items;
                    if (remaining.Any(item => !item.Kept) || afterState is not (JournalRunState.Ended or JournalRunState.Kept))
                        Step("run", "post-recovery status", $"run is {afterState} with {remaining.Count} item(s) left; inspect env status", failed: true);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { Step("run", "post-recovery status", "could not verify what remains: " + error.Message, failed: true); }
        }
        return new(runId, run.State, null, steps) { After = afterState, Remaining = remaining, NotRead = notRead };
    }

    private static readonly AsyncLocal<Func<Task>?> s_afterFirstReadForTest = new();

    /// <summary>
    /// For tests only: runs after a recovery's first read of the run and before it takes the run's control lock, the window in which
    /// a detached supervisor can adopt the run (#811).
    /// </summary>
    internal static IDisposable AfterFirstReadForTest(Func<Task> action)
    {
        var outer = s_afterFirstReadForTest.Value;
        s_afterFirstReadForTest.Value = action;
        return new Restore(() => s_afterFirstReadForTest.Value = outer);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    // The control locks of a run's detached supervisors (#811), one per scheduled task it journalled.
    private sealed class ControlLocks : IDisposable
    {
        private readonly List<DetachedControlLock> _held = [];
        public bool Any => _held.Count != 0;

        // Why the recovery is refused, or null once every lock is held. A task whose control directory is gone has no launcher, so
        // no supervisor of it can start. The lock is a file handle on the run's own machine: another machine cannot hold it.
        public string? Take(JournalRunStatus run, IReadOnlyDictionary<string, HostProfile> hosts, string runId)
        {
            foreach (var task in run.Items.Where(item => item.Kind == "scheduled-task"))
            {
                string control = task.Fields.GetValueOrDefault("control") ?? "";
                string? refused = hosts[task.Host].Kind != "local"
                    ? $"its detached supervisor's control lock is on {task.Host}; recover the run on that machine"
                    : !Path.IsPathFullyQualified(control) ? $"scheduled task {task.What} journalled no absolute control directory" : null;
                if (refused == null)
                    try
                    {
                        if (DetachedControlLock.TryTake(control) is { } taken) _held.Add(taken);
                        else refused = $"another process holds its control lock in {control}: its detached supervisor adopted the run " +
                            $"(valheim-test detach finish --run {runId} finishes it), or another recovery is clearing it";
                    }
                    catch (DirectoryNotFoundException) { }
                if (refused != null) { Dispose(); return refused; }
            }
            return null;
        }

        public void Dispose()
        {
            foreach (var held in _held) held.Dispose();
            _held.Clear();
        }
    }

    private static async Task<IReadOnlyList<RecoveryStep>> ReleaseRecoveryClaimsAsync(
        IReadOnlyDictionary<string, JournaledHostLock> claims)
    {
        var steps = new List<RecoveryStep>();
        foreach (var (host, claim) in claims)
            try
            {
                var released = await claim.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
                if (released.State == HostLockState.Released)
                    steps.Add(new(host, "recovery lock " + claim.Path, "released", false));
            }
            catch (Exception error)
            {
                steps.Add(new(host, "recovery lock " + claim.Path, "not released: " + error.Message, true));
            }
        return steps;
    }

    // --launch-gone is an explicit operator confirmation, but it is not accepted on trust alone. Every unreadable item must
    // be an interrupted launch. The recovery checks under a provably stale lock that the run still owns, or under a fresh
    // journalled recovery claim when no such lock exists. Only an idle result permits a stale lock to be swapped for the
    // recovery claim. Ordinary recovery receives those same claims, so no launch can begin between the observation, the append
    // and the handoff.
    private static async Task<RecoveryReport> AbandonLaunchesAsync(IReadOnlyDictionary<string, HostProfile> hosts,
        Func<string, IGameHost> hostFactory, RunJournal journal, JournalRunStatus run,
        IDictionary<string, JournaledHostLock> claims, TimeSpan timeout, CancellationToken cancellation)
    {
        var unprovable = run.Items.Where(item => item.Unrecoverable).ToList();
        string? refused = run.State != JournalRunState.Unrecoverable
            ? $"--launch-gone applies only to an UNRECOVERABLE run, not {run.State.ToString().ToUpperInvariant()}"
            : run.Unreadable.Count != 0
                ? "a journal the run rests on is unreadable: " + string.Join("; ", run.Unreadable.Select(host => $"{host.Name}: {host.Error}"))
            : unprovable.Count == 0 || unprovable.Any(item => item.Kind != "launch")
                ? "its unprovable open items are not only interrupted launches: " + string.Join("; ", unprovable.Select(item => $"{item.Kind} {item.What} on {item.Host}"))
            : null;
        if (refused != null) return new(run.Run, run.State, refused, []);

        var connections = unprovable.Select(item => item.Host).Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, hostFactory, StringComparer.Ordinal);

        // A host without this run's stale lock needs a recovery claim before it can be checked. A host with the stale lock is
        // already excluded from new runs; keep that exact lock untouched until its process check says idle.
        foreach (var group in unprovable.GroupBy(item => item.Host, StringComparer.Ordinal))
        {
            if (run.Items.Any(item => item.Kind == "lock" && item.Host == group.Key)) continue;
            try
            {
                claims.Add(group.Key, await JournaledHostLock.AcquireAsync(connections[group.Key], hosts[group.Key].Lock,
                    $"recover --launch-gone {run.Run}", journal, RunJournal.DirectoryFor(hosts[group.Key]), Actor,
                    timeout, cancellation).ConfigureAwait(false));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { return new(run.Run, run.State, $"host {group.Key}'s lock was not granted to this recovery: {error.Message}", []); }
        }

        foreach (var group in unprovable.GroupBy(item => item.Host, StringComparer.Ordinal))
        {
            var hashes = group.Select(item => item.Fields.GetValueOrDefault("expectedCommandLineSha256"))
                .Where(hash => hash is { Length: 64 } && hash.All(Uri.IsHexDigit)).Select(hash => hash!.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
            try { await RequireNoLaunchProcessAsync(connections[group.Key], hashes, timeout, cancellation).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException)
            { return new(run.Run, run.State, $"host {group.Key} did not prove every Valheim and journalled launch process gone: {error.Message}", []); }
        }

        // Every process check is idle. Replace each stale run claim with the recovery's own durable claim. There is necessarily
        // a small release/claim boundary; another run that wins it makes the recovery refuse before any launch is abandoned.
        foreach (var group in unprovable.GroupBy(item => item.Host, StringComparer.Ordinal))
        {
            var staleLocks = run.Items.Where(item => item.Kind == "lock" && item.Host == group.Key).ToList();
            if (staleLocks.Count == 0) continue;
            try
            {
                foreach (var stale in staleLocks)
                {
                    string claimant = stale.Fields["claimant"];
                    var released = await connections[group.Key].ReleaseLockAsync(stale.What, claimant, timeout, cancellation).ConfigureAwait(false);
                    if (released.State is not (HostLockState.Released or HostLockState.Free)) throw new HostLockException(released);
                    await journal.AppendAsync(connections[group.Key], RunJournal.DirectoryFor(hosts[group.Key]), Actor,
                        JournalEntry.Of(JournalEntry.LockReleased, ("lock", stale.What), ("claimant", claimant)),
                        timeout, CancellationToken.None).ConfigureAwait(false);
                }
                claims.Add(group.Key, await JournaledHostLock.AcquireAsync(connections[group.Key], hosts[group.Key].Lock,
                    $"recover --launch-gone {run.Run}", journal, RunJournal.DirectoryFor(hosts[group.Key]), Actor,
                    timeout, cancellation).ConfigureAwait(false));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { return new(run.Run, run.State, $"host {group.Key}'s stale lock could not be replaced by this recovery: {error.Message}", []); }
        }

        var steps = new List<RecoveryStep>();
        foreach (var launch in unprovable)
        {
            try
            {
                await journal.AppendAsync(connections[launch.Host], RunJournal.DirectoryFor(hosts[launch.Host]), Actor,
                    JournalEntry.Of(JournalEntry.LaunchAbandoned, ("actor", launch.Actor), ("directory", launch.What),
                        ("reason", "the host-specific process check found no Valheim image or journalled launch command line and reported sufficient visibility"),
                        ("operatorConfirmed", "--launch-gone")), timeout, cancellation).ConfigureAwait(false);
                steps.Add(new(launch.Host, "launch " + launch.What,
                    "journalled abandoned while this recovery held the host lock after the host-specific process check reported idle; the operator confirmed --launch-gone", false));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { steps.Add(new(launch.Host, "launch " + launch.What, "could not journal launch-abandoned: " + error.Message, true)); }
        }
        return new(run.Run, run.State, null, steps);
    }

    internal static async Task RequireNoLaunchProcessAsync(IGameHost host, IReadOnlyCollection<string> expectedHashes,
        TimeSpan timeout, CancellationToken cancellation = default, string processSnapshot = "")
    {
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsLaunchGone : BashLaunchGone,
            new Dictionary<string, string> { ["hashes"] = string.Join('\n', expectedHashes), ["processSnapshot"] = processSnapshot }, timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Checking for Valheim and interrupted-launch processes on {host.Name}");
        string verdict = InteractiveClient.Line(result.Stdout, "VT-LAUNCH-GONE ")
            ?? throw new HostOperationException($"No interrupted-launch process verdict from {host.Name}", result);
        if (verdict == "idle") return;
        if (verdict.StartsWith("busy ", StringComparison.Ordinal))
            throw new InvalidOperationException("process " + verdict["busy ".Length..] + " still runs");
        throw new HostOperationException($"The interrupted-launch process check on {host.Name} was inconclusive ({verdict})", result);
    }

    // processSnapshot is a deterministic test seam. Production passes empty and enumerates the host. Tests supply the same
    // fields the listing would have returned; "__unreadable__" exercises the fail-closed verdict without relying on privileges.
    internal static readonly string WindowsLaunchGone = """
        $busy = @(); $wanted = @($hashes -split "`n" | Where-Object { $_ })
        $utf8 = New-Object Text.UTF8Encoding $false
        $sha = [Security.Cryptography.SHA256]::Create()
        $gameImages = @('valheim.exe', 'valheim_server.exe', 'valheim', 'valheim_server')
        if ($processSnapshot -ceq '__unreadable__') { 'VT-LAUNCH-GONE unreadable process-list'; exit 0 }
        if ($processSnapshot) {
            try { $processes = @($processSnapshot | ConvertFrom-Json -ErrorAction Stop) }
            catch { 'VT-LAUNCH-GONE unreadable supplied-process-list'; exit 0 }
        } else {
            try { $processes = @(Get-CimInstance Win32_Process -ErrorAction Stop) }
            catch { 'VT-LAUNCH-GONE unreadable process-list: ' + $_.Exception.Message; exit 0 }
        }
        foreach ($p in $processes) {
            $processIdText = [string]$p.ProcessId
            $name = [string]$p.Name
            $path = [string]$p.ExecutablePath
            $image = if ($name) { $name } elseif ($path) { [IO.Path]::GetFileName($path) } else { '' }
            # Exact game images only: the toolkit's own valheim-test and Valheim.Testing.* processes must never match.
            $nameMatch = $gameImages -contains $image.ToLowerInvariant()
            if (-not $nameMatch -and $path) {
                $pathImage = [IO.Path]::GetFileName($path)
                if ($gameImages -contains $pathImage.ToLowerInvariant()) { $image = $pathImage; $nameMatch = $true }
            }
            $label = if ($image) { $processIdText + ' (' + $image + ')' } else { $processIdText }
            if ($nameMatch) { $busy += $label; continue }

            # Win32_Process deliberately returns no path or command line for kernel/minimal processes (including these names),
            # and a non-elevated caller cannot read those fields for some other users' ordinary processes. Neither is a game
            # image. Only readable command lines can match a journalled hash; an unreadable non-game process is ignored.
            if (($p.ProcessId -eq 0 -or $p.ProcessId -eq 4 -or $name -in @('Registry', 'Memory Compression', 'Secure System')) -and -not $path) { continue }
            if ($wanted.Count -ne 0 -and $null -ne $p.CommandLine) {
                $hash = -join ($sha.ComputeHash($utf8.GetBytes([string]$p.CommandLine)) | ForEach-Object { $_.ToString('x2') })
                if ($wanted -contains $hash) { $busy += $label }
            }
        }
        if ($busy.Count -ne 0) { 'VT-LAUNCH-GONE busy ' + ($busy -join ',') }
        else { 'VT-LAUNCH-GONE idle' }
        """.ReplaceLineEndings("\n");

    internal static readonly string BashLaunchGone = """
        set -u
        export LC_ALL=C
        os=$(uname -s) || exit 4
        supplied=
        if [ "$processSnapshot" = __unreadable__ ]; then echo 'VT-LAUNCH-GONE unreadable process-list'; exit 0
        elif [ -n "$processSnapshot" ]; then processes=$processSnapshot; supplied=1
        else
          if [ "$os" != Darwin ]; then
            if [ "$(id -u)" != 0 ] && awk '$2 == "/proc" && $4 ~ /(^|,)hidepid=(1|2|invisible|noaccess|ptraceable)(,|$)/ { found=1 } END { exit !found }' /proc/mounts 2>/dev/null; then
              echo 'VT-LAUNCH-GONE unreadable process-list: /proc hides other users from this non-root process'; exit 0
            fi
            if [ -e /.dockerenv ] || [ -e /run/.containerenv ] || [ "$(awk '/^NSpid:/ { print NF }' /proc/self/status 2>/dev/null)" -gt 2 ] 2>/dev/null; then
              echo 'VT-LAUNCH-GONE unreadable process-list: a container cannot see the host PID namespace'; exit 0
            fi
            if [ -e /proc/1/ns/pid ] && [ -e /proc/self/ns/pid ]; then
              first=$(readlink /proc/1/ns/pid 2>/dev/null) || { echo 'VT-LAUNCH-GONE unreadable process-list: cannot identify PID namespace'; exit 0; }
              self=$(readlink /proc/self/ns/pid 2>/dev/null) || { echo 'VT-LAUNCH-GONE unreadable process-list: cannot identify PID namespace'; exit 0; }
              if [ "$first" != "$self" ]; then
                echo 'VT-LAUNCH-GONE unreadable process-list: this process is not in PID 1s namespace'; exit 0
              fi
            fi
            processes=$(ps -axww -o pid=,uid=,comm=) || { echo 'VT-LAUNCH-GONE unreadable process-list'; exit 0; }
          else
            # comm is last: read assigns the rest of the line to the executable path, including spaces. When hashes are needed,
            # command is also listed once for every process; there is no per-process ps/shasum query.
            processes=$(ps -axww -o pid=,uid=,comm=) || { echo 'VT-LAUNCH-GONE unreadable process-list'; exit 0; }
            commands=
            if [ -n "$hashes" ]; then commands=$(ps -axww -o pid=,uid=,command=) || { echo 'VT-LAUNCH-GONE unreadable process-list'; exit 0; }; fi
          fi
        fi
        busy=; unknown=
        check_process() {
          pid=$1; image=$2; mode=$3; supplied_hash=${4-}
          [ -n "$pid" ] || return
          lower=$(printf '%s' "$image" | tr '[:upper:]' '[:lower:]')
          match=
          if [ "$mode" != mac-hash ]; then
            # Exact game images only (Linux comm truncates valheim_server.x86_64 to "valheim_server."): never the toolkit itself.
            name=${lower##*/}; name=${name%% *}; case "$name" in valheim|valheim.exe|valheim_server|valheim_server.exe|valheim.x86_64|valheim_server.x86_64|valheim_server.) match=1 ;; esac
          fi
          hash=; unreadable=
          if [ -z "$match" ] && [ -n "$hashes" ]; then
            if [ "$mode" = supplied ]; then
              if [ "$supplied_hash" = unreadable ]; then unreadable=1; else hash=$supplied_hash; fi
            elif [ "$mode" = mac-hash ]; then
              if [ -n "$image" ]; then hash=$(printf '%s' "$image" | shasum -a 256 | awk '{print $1}') || unreadable=1
              else unreadable=1
              fi
            elif [ -r "/proc/$pid/cmdline" ]; then
              hash=$(sha256sum < "/proc/$pid/cmdline" 2>/dev/null | cut -d' ' -f1) || unreadable=1
            elif [ -e "/proc/$pid" ]; then unreadable=1
            else return
            fi
          fi
          if [ -z "$match" ] && [ -n "$hashes" ] && [ -z "$unreadable" ] && [ "$mode" != supplied ] && ! [[ $hash =~ ^[0-9a-f]{64}$ ]]; then
            unreadable=1; hash=
          fi
          if [ -z "$match" ] && [ -n "$hash" ]; then
            while IFS= read -r wanted; do [ -n "$wanted" ] && [ "$hash" = "$wanted" ] && match=1; done <<< "$hashes"
          fi
          if [ -n "$match" ]; then busy="${busy:+$busy,}$pid"
          elif [ -n "$unreadable" ]; then unknown="${unknown:+$unknown,}$pid"
          fi
        }
        if [ -n "$supplied" ]; then
          # Test rows are pid, uid, precomputed hash (or unreadable), then the image/command as the rest of the line.
          while read -r pid owner supplied_hash image; do check_process "$pid" "$image" supplied "$supplied_hash"; done <<< "$processes"
        elif [ "$os" = Darwin ]; then
          while read -r pid owner image; do check_process "$pid" "$image" mac-name; done <<< "$processes"
          if [ -n "$hashes" ]; then
            while read -r pid owner image; do check_process "$pid" "$image" mac-hash; done <<< "$commands"
          fi
        else
          while read -r pid owner image; do check_process "$pid" "$image" linux; done <<< "$processes"
        fi
        if [ -n "$busy" ]; then echo "VT-LAUNCH-GONE busy $busy"
        elif [ -n "$unknown" ]; then echo "VT-LAUNCH-GONE unreadable command-line $unknown"
        else echo 'VT-LAUNCH-GONE idle'
        fi
        """.ReplaceLineEndings("\n");

    /// <summary>
    /// Keeps the game's own logs from a host copy of <paramref name="actor"/> in the run's evidence folder on that host,
    /// <c>&lt;runs&gt;/&lt;run&gt;/recovered-&lt;actor&gt;</c> beside its <c>boot-N</c> and client launch folders: BepInEx's
    /// <c>BepInEx/LogOutput.log</c>, Unity's <see cref="GameLaunch.UnityLogName"/> (a server's <c>-logFile</c>; a client's is in its
    /// launch folder, already the run's) and BepInEx's <c>preloader_*.log</c> beside the game. Only a log written since the
    /// copy was made (<paramref name="copiedUtc"/>, its journalled start) is the run's: an older one came with the install and is
    /// only named. Throws when a log could not be kept.
    /// </summary>
    internal static async Task<RecoveredLogs> KeepLogsAsync(IGameHost host, string runtime, string runId, string actor, DateTime copiedUtc,
        TimeSpan timeout, CancellationToken cancellation = default)
    {
        string parent = HostedRuntimeStage.RequirePrepared(host, runtime, runId);
        int at = parent.LastIndexOfAny(['/', '\\']);
        if (at <= 0) throw new ArgumentException("A prepared copy's folder has no parent to keep its logs in.", nameof(runtime));
        if (!RunJournal.SafeName(runId) || !RunJournal.SafeName(actor)) throw new ArgumentException("A run id and an actor are letters, digits, '.', '_' and '-'.");
        string folder = HostPath.Join(parent[..at], runId, "recovered-" + actor);
        var result = (await host.RunAsync(host.Shell.Kind == HostShellKind.PowerShell ? WindowsKeepLogs : BashKeepLogs, new Dictionary<string, string>
        {
            ["runtime"] = runtime, ["keep"] = folder, ["unitylog"] = GameLaunch.UnityLogName,
            ["since"] = new DateTimeOffset(copiedUtc.ToUniversalTime()).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        }, timeout, cancellation).ConfigureAwait(false)).EnsureSuccess($"Keeping the game logs of {runtime} on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-LOGS-DONE") == null) throw new HostOperationException($"Unexpected reply while keeping the game logs of {runtime} on {host.Name}", result);
        List<string> Named(string verdict) => result.Stdout.Split('\n').Select(line => line.Trim())
            .Where(line => line.StartsWith(verdict + " ", StringComparison.Ordinal)).Select(line => line[(verdict.Length + 1)..]).ToList();
        return new(folder, Named("VT-LOG-KEPT"), Named("VT-LOG-OLDER"));
    }

    // Variables: runtime, keep, unitylog (GameLaunch.UnityLogName), since (Unix seconds: when the copy was made). Each log written since the copy was made is copied into keep with its time, and named;
    // an older one is only named; links are never followed, and the folder is made only for a log. Then the verdict.
    internal static readonly string BashKeepLogs = """
        set -u
        save() {
          if [ -f "$1" ] && [ ! -L "$1" ]; then
            written=$(date -r "$1" +%s) || exit 3
            if [ "$written" -lt "$since" ]; then echo "VT-LOG-OLDER $2"; return 0; fi
            mkdir -p -- "$(dirname -- "$keep/$2")" && cp -p -- "$1" "$keep/$2" || exit 3
            echo "VT-LOG-KEPT $2"
          fi
        }
        if [ -d "$runtime" ]; then
          save "$runtime/BepInEx/LogOutput.log" "BepInEx/LogOutput.log"
          save "$runtime/$unitylog" "$unitylog"
          for f in "$runtime"/preloader_*.log; do save "$f" "$(basename -- "$f")"; done
        fi
        echo "VT-LOGS-DONE"
        """.ReplaceLineEndings("\n");

    internal static readonly string WindowsKeepLogs = """
        $copied = [DateTimeOffset]::FromUnixTimeSeconds([long]$since).UtcDateTime
        function Save-VtRecovered([string]$from, [string]$name) {
            if (-not [IO.File]::Exists($from)) { return }
            if (([IO.File]::GetAttributes($from) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return }
            if ([IO.File]::GetLastWriteTimeUtc($from) -lt $copied) { 'VT-LOG-OLDER ' + $name; return }
            $to = Join-Path $keep $name
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
            [IO.File]::Copy($from, $to, $true)
            'VT-LOG-KEPT ' + $name
        }
        if ([IO.Directory]::Exists($runtime)) {
            Save-VtRecovered (Join-Path $runtime 'BepInEx\LogOutput.log') 'BepInEx/LogOutput.log'
            Save-VtRecovered (Join-Path $runtime $unitylog) $unitylog
            foreach ($f in @([IO.Directory]::GetFiles($runtime, 'preloader_*.log'))) { Save-VtRecovered $f ([IO.Path]::GetFileName($f)) }
        }
        'VT-LOGS-DONE'
        """.ReplaceLineEndings("\n");

    private static JournalEntry Settled(string actor, string directory) =>
        JournalEntry.Of(JournalEntry.LaunchSettled, ("actor", actor), ("directory", directory));

    private static JournalEntry Stopped(int pid, string start) =>
        JournalEntry.Of(JournalEntry.ProcessStopped, ("pid", pid.ToString(CultureInfo.InvariantCulture)), ("startIdentity", start));

    /// <summary>The report as <c>valheim-test env recover|teardown</c> prints it.</summary>
    public static void Write(RecoveryReport report, TextWriter output, bool json)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(new { report.Run, report.Before, report.After, report.Refused, report.Steps, report.Recovered,
                report.Clean, report.Remaining, report.NotRead },
                new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
            return;
        }
        if (report.Refused != null)
        {
            output.WriteLine($"REFUSED {report.Run} ({report.Before.ToString().ToUpperInvariant()}): {report.Refused}. Nothing was changed.");
            return;
        }
        foreach (var step in report.Steps) output.WriteLine($"{(step.Failed ? "FAILED" : "done")} {step.What} on {step.Host}: {step.Outcome}");
        if (report.Recovered && report.NotRead.Count != 0)
            output.WriteLine($"NOTE host {string.Join(", ", report.NotRead)} could not be read and shows nothing of run {report.Run}; whatever it left only there is not part of this result. Run env status once it is back.");
        if (report.Clean) output.WriteLine($"RECOVERED {report.Run}: nothing of it is left.");
        else if (report.Recovered && report.Remaining.Count != 0)
        {
            output.WriteLine($"RECOVERED {report.Run} WITH RETAINED EVIDENCE: {report.Remaining.Count} item(s) remain: " +
                string.Join("; ", report.Remaining.Select(item => $"{item.Kind} on {item.Host}: {item.What}")) + ".");
            output.WriteLine($"The environment is not empty. Run valheim-test env teardown --run {report.Run} to remove the retained items.");
        }
        else output.WriteLine($"PARTLY RECOVERED {report.Run}: run env status for what is left, and recover again once it can be.");
    }
}
