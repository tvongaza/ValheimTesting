using System.Globalization;
using System.Text.Json;

namespace Valheim.Testing.Game;

/// <summary>One thing a recovery did or could not do, on one host.</summary>
internal sealed record RecoveryStep(string Host, string What, string Outcome, bool Failed);

/// <summary>A recovery of one run: refused before anything changed (with why), or its steps.</summary>
internal sealed record RecoveryReport(string Run, JournalRunState Before, string? Refused, IReadOnlyList<RecoveryStep> Steps)
{
    public bool Recovered => Refused == null && Steps.All(step => !step.Failed);
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

    public static async Task<RecoveryReport> RecoverAsync(IReadOnlyDictionary<string, HostProfile> hosts, Func<string, IGameHost> hostFactory, string runId,
        bool teardown, TimeSpan timeout, CancellationToken cancellation = default, string? leaseHost = null, string? leaseDirectory = null)
    {
        var journal = new RunJournal(runId);
        var status = await RunJournalStatus.InspectAsync(hosts, hostFactory, timeout, cancellation, leaseHost, leaseDirectory).ConfigureAwait(false);
        var run = status.Runs.SingleOrDefault(candidate => candidate.Run == runId);
        if (run == null)
            return new(runId, JournalRunState.Unknown, "no readable host's journal names this run" +
                string.Concat(status.Hosts.Where(host => host.Error != null).Select(host => $"; host {host.Name} could not be read ({host.Error})")), []);
        string? refused = run.State switch
        {
            JournalRunState.Live => "it is still going: " + run.Reason,
            JournalRunState.Unknown => run.Reason,
            JournalRunState.Unrecoverable => run.Reason + ": " + string.Join("; ", run.Items.Where(item => item.Unrecoverable).Select(item => $"{item.Kind} {item.What} on {item.Host}: {item.Status}")),
            JournalRunState.Kept when !teardown => "it kept what it left on purpose; env teardown --run " + runId + " removes it",
            _ => null,
        };
        if (refused != null) return new(runId, run.State, refused, []);

        var items = run.Items.Where(item => teardown || !item.Kept).ToList();
        if (items.Count == 0 && run.SettledLaunches.Count == 0) return new(runId, run.State, null, []); // it left nothing to recover
        var steps = new List<RecoveryStep>();
        var connections = items.Concat(run.SettledLaunches).Select(item => item.Host).Distinct(StringComparer.Ordinal).ToDictionary(name => name, hostFactory, StringComparer.Ordinal);
        void Step(string host, string what, string outcome, bool failed = false) => steps.Add(new(host, what, outcome, failed));
        bool Failed(string host) => steps.Any(step => step.Host == host && step.Failed);
        async Task Note(string name, JournalEntry entry)
        {
            // After the effect: a lost line only makes a later recovery find nothing to do.
            try { await journal.AppendAsync(connections[name], RunJournal.DirectoryFor(hosts[name]), Actor, entry, timeout, cancellation).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException) { Step(name, "journal " + entry.Kind, "could not be journalled: " + error.Message); }
        }
        IEnumerable<JournalItem> Of(string kind) => items.Where(item => item.Kind == kind);
        // A process adopted from a launch's pid file: once it is stopped or gone, its launch is settled too.
        Task SettleAdopted(JournalItem process) => process.Fields.GetValueOrDefault("adoptedFrom") is { Length: > 0 } directory
            ? Note(process.Host, Settled(process.Actor, directory)) : Task.CompletedTask;
        bool Local(JournalItem item) => item.Fields.GetValueOrDefault("local") == "true" && hosts[item.Host].Kind == "local";

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
                    string directory = process.Fields.GetValueOrDefault("bootDirectory") is { Length: > 0 } boot ? boot : process.Fields.GetValueOrDefault("launchDirectory") ?? "";
                    var owned = new InteractiveClientProcess(host, host.Shell.Kind == HostShellKind.PowerShell ? ClientPlatform.Windows : ClientPlatform.Linux, pid, start, directory, null);
                    var outcome = await owned.StopAsync(TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false);
                    Step(group.Key, what, outcome == InteractiveStop.AlreadyGone ? "already gone" : "stopped");
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
        foreach (var group in Of("character").Concat(Of("copy").Where(item => !Local(item))).GroupBy(item => item.Host, StringComparer.Ordinal))
        {
            var host = connections[group.Key];
            var characters = group.Where(item => item.Kind == "character").ToList();
            var copies = group.Where(item => item.Kind == "copy").ToList();
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
                await HostedRuntimeStage.RequireStoppedAsync(host, timeout, runtimes: copies.Select(copy => copy.What).ToList(), clientSession: characters.Count != 0)
                    .ConfigureAwait(false);
                foreach (var character in characters)
                {
                    string what = $"character {character.What}";
                    try
                    {
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
                        await HostedRuntimeStage.RetireAsync(host, copy.What, stage, timeout).ConfigureAwait(false);
                        Step(group.Key, what, "removed");
                        await Note(group.Key, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", copy.What))).ConfigureAwait(false);
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
            if (Failed(runLock.Host)) { Step(runLock.Host, what, "kept: something of the run is still left on this host", failed: true); continue; }
            try
            {
                string claimant = runLock.Fields["claimant"];
                var released = await connections[runLock.Host].ReleaseLockAsync(runLock.What, claimant, timeout, cancellation).ConfigureAwait(false);
                if (released.State is not (HostLockState.Released or HostLockState.Free)) throw new HostLockException(released);
                Step(runLock.Host, what, "released");
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
        return new(runId, run.State, null, steps);
    }

    private static JournalEntry Settled(string actor, string directory) =>
        JournalEntry.Of(JournalEntry.LaunchSettled, ("actor", actor), ("directory", directory));

    private static JournalEntry Stopped(int pid, string start) =>
        JournalEntry.Of(JournalEntry.ProcessStopped, ("pid", pid.ToString(CultureInfo.InvariantCulture)), ("startIdentity", start));

    /// <summary>The report as <c>valheim-test env recover|teardown</c> prints it.</summary>
    public static void Write(RecoveryReport report, TextWriter output, bool json)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(new { report.Run, report.Before, report.Refused, report.Steps, report.Recovered },
                new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
            return;
        }
        if (report.Refused != null)
        {
            output.WriteLine($"REFUSED {report.Run} ({report.Before.ToString().ToUpperInvariant()}): {report.Refused}. Nothing was changed.");
            return;
        }
        foreach (var step in report.Steps) output.WriteLine($"{(step.Failed ? "FAILED" : "done")} {step.What} on {step.Host}: {step.Outcome}");
        output.WriteLine(report.Recovered ? $"RECOVERED {report.Run}: nothing of it is left." : $"PARTLY RECOVERED {report.Run}: run env status for what is left, and recover again once it can be.");
    }
}
