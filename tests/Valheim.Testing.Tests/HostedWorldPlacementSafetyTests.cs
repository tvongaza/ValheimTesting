using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

// Real host scripts with a deterministic external writer between the absence check and transfer.
public sealed class HostedWorldPlacementSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vt-placement-safety-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("Campaign.fwl", 9999)]
    [InlineData("campaign.fwl", 4242)]
    [InlineData("Campaign/_main.0.fwl2", 4242)]
    [InlineData("CAMPAIGN_backup_auto-1.fwl", 4242)]
    public void LateForeignCollisionNeverBecomesRollbackEvidence(string foreign, long uid)
        => LateCollision(new LocalGameHost("local", HostShell.ThisMachine), foreign, uid);

    [SshTheory]
    [Trait("Category", "GameHosts")]
    [InlineData("Campaign.fwl", 9999)]
    [InlineData("campaign.fwl", 4242)]
    [InlineData("Campaign/_main.0.fwl2", 4242)]
    [InlineData("CAMPAIGN_backup_auto-1.fwl", 4242)]
    public void LateForeignCollisionThroughSshNeverBecomesRollbackEvidence(string foreign, long uid) => LateCollision(SshHost(), foreign, uid);

    private static IGameHost SshHost() => new SshGameHost("ssh", Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_DESTINATION")!, HostShell.ThisMachine,
        int.TryParse(Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_PORT"), out int port) ? port : 0,
        (Environment.GetEnvironmentVariable("VALHEIM_TESTING_SSH_OPTIONS") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private void LateCollision(IGameHost inner, string foreign, long uid)
    {
        string fixture = FakeInstalls.World(Path.Combine(_root, "fixture"));
        var plan = new HostWorldPlan { World = new() { Source = fixture, Sha256 = new(WorldFixture.Manifest(fixture)) }, WorldUid = "4242" };
        string worlds = Path.Combine(_root, "worlds_local"), stage = Path.Combine(_root, "runs", "run-1", "host-world-stage");
        string keep = Path.Combine(_root, "runs", "run-1", "host-world");
        byte[] bytes = File.ReadAllBytes(Path.Combine(FakeInstalls.World(Path.Combine(_root, "foreign"), uid: uid), "Campaign.fwl"));
        var journal = new List<JournalEntry>();
        var host = new ForwardingWorldHost(inner);
        host.BeforeRun = (script, variables) =>
        {
            if (variables?.GetValueOrDefault("worlds") != stage) return;
            string target = Path.Combine(worlds, foreign);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        };
        var site = new HostedWorldOnHost.Site(host, host.Name, worlds, stage, keep,
            (entry, _) => { journal.Add(entry); return Task.CompletedTask; }, _ => Task.CompletedTask);
        Assert.ThrowsAny<Exception>(() => site.Place(plan, Path.Combine(_root, "output"), default));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(worlds, foreign)));
        Assert.DoesNotContain(journal, entry => entry.Kind == JournalEntry.CopyRetired);
        Assert.DoesNotContain(journal, entry => entry.Kind == JournalEntry.CopyDone && entry.Fields.GetValueOrDefault("verified") == "true");
        Assert.False(File.Exists(Path.Combine(keep, foreign)));
        var fields = Fields(journal);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.ThrowsAny<Exception>(() => HostedWorldOnHost.RecoverAsync(host, fields, Path.Combine(worlds, "Campaign"), "run-1", TimeSpan.FromSeconds(30), default).GetAwaiter().GetResult());
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(worlds, foreign)));
        }
    }

    private static Dictionary<string, string> Fields(List<JournalEntry> journal)
    {
        var fields = new Dictionary<string, string>();
        foreach (var entry in journal) foreach (var (key, value) in entry.Fields) fields[key] = value;
        return fields;
    }

    private (HostWorldPlan Plan, HostedWorldOnHost.Site Site, ForwardingWorldHost Host, List<JournalEntry> Journal) Setup(string suffix = "")
    {
        string root = Path.Combine(_root, suffix);
        Directory.CreateDirectory(root);
        string fixture = FakeInstalls.World(Path.Combine(root, "fixture"));
        var plan = new HostWorldPlan { World = new() { Source = fixture, Sha256 = new(WorldFixture.Manifest(fixture)) }, WorldUid = "4242" };
        var journal = new List<JournalEntry>();
        var host = new ForwardingWorldHost(new LocalGameHost("local", HostShell.ThisMachine));
        var site = new HostedWorldOnHost.Site(host, host.Name, Path.Combine(root, "worlds_local"),
            Path.Combine(root, "runs", "run-1", "host-world-stage"), Path.Combine(root, "runs", "run-1", "host-world"),
            (entry, _) => { journal.Add(entry); return Task.CompletedTask; }, _ => Task.CompletedTask);
        return (plan, site, host, journal);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("malformed")]
    [InlineData("cancel")]
    public void UncertainRepliesPreserveThePendingPlacementAndRecoveryNeverInfersOwnershipFromUid(string fault)
    {
        var (plan, site, host, journal) = Setup();
        host.AfterRun = (_, variables, result) =>
        {
            if (variables?.GetValueOrDefault("worlds") != site.Stage) return result;
            if (fault == "cancel") throw new OperationCanceledException("reply interrupted");
            return fault == "lost" ? result with { Outcome = HostOutcome.Unknown, ExitCode = null } : result with { Stdout = result.Stdout.Replace("VT-WORLD-MOVE done", "invalid") };
        };
        Assert.ThrowsAny<Exception>(() => site.Place(plan, Path.Combine(_root, "output"), default));
        Assert.DoesNotContain(journal, entry => entry.Kind == JournalEntry.CopyRetired);
        Assert.DoesNotContain(journal, entry => entry.Fields.GetValueOrDefault("verified") == "true");
        Assert.True(File.Exists(Path.Combine(site.WorldsDirectory, "Campaign.fwl")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(site.KeepIn));
        for (int attempt = 0; attempt < 2; attempt++)
            Assert.Contains("never confirmed", Assert.Throws<InvalidDataException>(() => HostedWorldOnHost.RecoverAsync(host, Fields(journal),
                Path.Combine(site.WorldsDirectory, "Campaign"), "run-1", TimeSpan.FromSeconds(30), default).GetAwaiter().GetResult()).Message);
    }

    [Fact]
    public async Task AnUnknownTransferCannotBeRetiredWhileItsDelayedMutationHasNotAppearedYet()
    {
        var (plan, site, host, journal) = Setup();
        string? delayed = null;
        IReadOnlyDictionary<string, string>? delayedVariables = null;
        host.InsteadOfRun = (script, variables) =>
        {
            if (variables?.GetValueOrDefault("worlds") != site.Stage) return null;
            delayed = script; delayedVariables = variables;
            return new HostResult(HostOutcome.Unknown, null, "", "", TimeSpan.Zero, false);
        };
        Assert.Throws<HostOperationException>(() => site.Place(plan, Path.Combine(_root, "output"), default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(site.WorldsDirectory));
        var fields = Fields(journal);
        Assert.Contains("may still run", (await Assert.ThrowsAsync<InvalidDataException>(() => HostedWorldOnHost.RecoverAsync(host, fields,
            Path.Combine(site.WorldsDirectory, "Campaign"), "run-1", TimeSpan.FromSeconds(30), default))).Message);
        Assert.DoesNotContain(journal, entry => entry.Kind == JournalEntry.CopyRetired);
        // The interrupted host command finishes after recovery looked: the journal must still describe this uncertain effect.
        Assert.NotNull(delayed);
        (await new LocalGameHost("delayed", HostShell.ThisMachine).RunAsync(delayed, delayedVariables, TimeSpan.FromSeconds(30))).EnsureSuccess("delayed transfer");
        Assert.True(File.Exists(Path.Combine(site.WorldsDirectory, "Campaign.fwl")));
        await Assert.ThrowsAsync<InvalidDataException>(() => HostedWorldOnHost.RecoverAsync(host, fields,
            Path.Combine(site.WorldsDirectory, "Campaign"), "run-1", TimeSpan.FromSeconds(30), default));
    }

    [Theory]
    [InlineData(4242)]
    [InlineData(9999)]
    public async Task PartialRecoveryNeverMovesAReplacementAtAnOldReceiptName(long uid)
    {
        var (plan, site, host, journal) = Setup();
        site = site with { Journal = (entry, _) =>
        {
            if (entry.Fields.GetValueOrDefault("verified") == "true") throw new IOException("completion journal failed");
            journal.Add(entry); return Task.CompletedTask;
        } };
        host.BeforeRun = (_, variables) =>
        {
            if (variables?.GetValueOrDefault("worlds") == site.WorldsDirectory && variables.ContainsKey("to"))
                throw new IOException("rollback interrupted");
        };
        Assert.Throws<IOException>(() => site.Place(plan, Path.Combine(_root, "output"), default));
        Assert.Equal("partial", Fields(journal)["placementState"]);
        byte[] personal = File.ReadAllBytes(Path.Combine(FakeInstalls.World(Path.Combine(_root, "personal"), uid: uid, seed: "Personal"), "Campaign.fwl"));
        File.WriteAllBytes(Path.Combine(site.WorldsDirectory, "Campaign.fwl"), personal);
        host.BeforeRun = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Assert.Contains("historical receipts", (await Assert.ThrowsAsync<InvalidDataException>(() => HostedWorldOnHost.RecoverAsync(host, Fields(journal),
                Path.Combine(site.WorldsDirectory, "Campaign"), "run-1", TimeSpan.FromSeconds(30), default))).Message);
            Assert.Equal(personal, File.ReadAllBytes(Path.Combine(site.WorldsDirectory, "Campaign.fwl")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(site.KeepIn));
        }
    }

    [Fact]
    public void ADirectoryArrivingImmediatelyBeforeTheMoveCannotReceiveANestedFixtureOrBecomeOwned()
    {
        var (_, site, host, journal) = Setup();
        string fixture = Path.Combine(_root, "chunked-fixture");
        Directory.CreateDirectory(Path.Combine(fixture, "Campaign"));
        File.Copy(Path.Combine(_root, "fixture", "Campaign.fwl"), Path.Combine(fixture, "Campaign", "_main.0.fwl2"));
        var plan = new HostWorldPlan { World = new() { Source = fixture, Sha256 = new(WorldFixture.Manifest(fixture)) }, WorldUid = "4242" };
        host.TransformScript = (script, variables) => variables?.GetValueOrDefault("worlds") != site.Stage ? script :
            host.Shell.Kind == HostShellKind.Bash
                ? script.Replace("mv -n -- \"$worlds/$leaf\" \"$to/\"", "mkdir -- \"$to/$leaf\"\nprintf personal > \"$to/$leaf/personal-save\"\nmv -n -- \"$worlds/$leaf\" \"$to/\"")
                : script.Replace("$entry = $move[0]; $dest = $move[1]", "$entry = $move[0]; $dest = $move[1]\n[void][IO.Directory]::CreateDirectory($dest)\n[IO.File]::WriteAllText((Join-Path $dest 'personal-save'), 'personal')");
        Assert.ThrowsAny<Exception>(() => site.Place(plan, Path.Combine(_root, "output"), default));
        Assert.Equal("personal", File.ReadAllText(Path.Combine(site.WorldsDirectory, "Campaign", "personal-save")));
        Assert.False(Directory.Exists(Path.Combine(site.WorldsDirectory, "Campaign", "Campaign")));
        Assert.True(File.Exists(Path.Combine(site.Stage, "Campaign", "_main.0.fwl2")));
        Assert.DoesNotContain(journal, entry => entry.Kind == JournalEntry.CopyRetired || entry.Fields.GetValueOrDefault("verified") == "true");
    }

    [OsFact("windows")]
    public async Task WindowsCrossVolumePublicationRefusesLateFilesAndDirectoriesAndPreservesUncertainDeletion()
    {
        foreach (bool chunked in new[] { false, true })
        foreach (string fault in new[] { "none", "collision", "delete" })
        {
            string variant = "cross-" + chunked + "-" + fault;
            var (plan, site, host, journal) = Setup(variant);
            string leaf = chunked ? "Campaign" : "Campaign.fwl";
            if (!chunked)
            {
                string fixture = Directory.CreateDirectory(Path.Combine(_root, variant, "single-file")).FullName;
                File.Copy(Path.Combine(plan.World.Source, "Campaign.fwl"), Path.Combine(fixture, leaf));
                plan = new HostWorldPlan { World = new() { Source = fixture, Sha256 = new(WorldFixture.Manifest(fixture)) }, WorldUid = "4242" };
            }
            else
            {
                string fixture = Path.Combine(_root, variant, "chunked");
                Directory.CreateDirectory(Path.Combine(fixture, leaf));
                File.Copy(Path.Combine(plan.World.Source, "Campaign.fwl"), Path.Combine(fixture, leaf, "_main.0.fwl2"));
                plan = new HostWorldPlan { World = new() { Source = fixture, Sha256 = new(WorldFixture.Manifest(fixture)) }, WorldUid = "4242" };
            }
            host.TransformScript = (script, variables) =>
            {
                if (variables?.GetValueOrDefault("worlds") != site.Stage) return script;
                // Exercise the actual cross-volume branch on CI's one volume, then inject at the final no-replace rename.
                script = script.Replace("[IO.Path]::GetPathRoot($entry) -ne [IO.Path]::GetPathRoot($dest)", "$true");
                if (fault == "collision")
                    script = script.Replace("Copy-Item -LiteralPath $entry -Destination $copy -Recurse", "Copy-Item -LiteralPath $entry -Destination $copy -Recurse\n" +
                        (chunked ? "[void][IO.Directory]::CreateDirectory($dest); [IO.File]::WriteAllText((Join-Path $dest 'personal-save'), 'personal')" :
                            "if ($move[2] -ceq 'Campaign.fwl') { [IO.File]::WriteAllText($dest, 'personal') }"));
                if (fault == "delete") script = script.Replace("Remove-VtTree $entry $worlds", "throw 'source-delete failed'");
                return script;
            };
            string output = Path.Combine(_root, variant, "output");
            if (fault == "none")
            {
                var placed = site.Place(plan, output, default);
                Assert.True(Path.Exists(Path.Combine(site.WorldsDirectory, leaf)));
                Assert.False(Path.Exists(Path.Combine(site.Stage, leaf)));
                placed.Collect();
            }
            else
            {
                Assert.ThrowsAny<Exception>(() => site.Place(plan, output, default));
                Assert.True(Path.Exists(Path.Combine(site.Stage, leaf)));
                if (fault == "collision")
                    Assert.Equal("personal", File.ReadAllText(chunked ? Path.Combine(site.WorldsDirectory, leaf, "personal-save") : Path.Combine(site.WorldsDirectory, leaf)));
                else
                    Assert.True(Path.Exists(Path.Combine(site.WorldsDirectory, leaf)));
                await Assert.ThrowsAsync<InvalidDataException>(() => HostedWorldOnHost.RecoverAsync(host, Fields(journal),
                    Path.Combine(site.WorldsDirectory, "Campaign"), "run-1", TimeSpan.FromSeconds(30), default));
                Assert.DoesNotContain(journal, entry => entry.Kind == JournalEntry.CopyRetired || entry.Fields.GetValueOrDefault("verified") == "true");
            }
            Assert.Empty(Directory.EnumerateDirectories(site.WorldsDirectory, ".vt-world-copy-*"));
        }
    }

    [Fact]
    public void PartialTransferRollsBackItsReceiptAndPreservesAForeignSibling()
    {
        var (plan, site, host, journal) = Setup();
        // Fail after the first real filesystem move and its receipt, before the next entry is transferred.
        host.TransformScript = (script, variables) => variables?.GetValueOrDefault("worlds") != site.Stage ? script :
            host.Shell.Kind == HostShellKind.Bash
                ? script.Replace("printf 'VT-WORLD-MOVED %s\\n' \"$leaf\"", "printf 'VT-WORLD-MOVED %s\\n' \"$leaf\"\nexit 9")
                : script.Replace("'VT-WORLD-MOVED ' + $move[2]", "'VT-WORLD-MOVED ' + $move[2]\nexit 9");
        host.AfterRun = (_, variables, result) =>
        {
            if (variables?.GetValueOrDefault("worlds") == site.Stage)
                File.WriteAllText(Path.Combine(site.WorldsDirectory, "Campaign_backup_foreign.db"), "foreign");
            return result;
        };
        Assert.ThrowsAny<Exception>(() => site.Place(plan, Path.Combine(_root, "output"), default));
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(site.WorldsDirectory, "Campaign_backup_foreign.db")));
        Assert.Single(Directory.EnumerateFileSystemEntries(site.KeepIn));
        Assert.Contains(journal, entry => entry.Fields.GetValueOrDefault("placementState") == "partial");
        Assert.Equal(JournalEntry.CopyRetired, journal[^1].Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACompletionJournalFailureRollsBackOnlyProvenEntriesAndCanBeRecoveredAgain(bool retirementJournalFails)
    {
        var (plan, site, host, journal) = Setup();
        site = site with { Journal = (entry, _) =>
        {
            if (entry.Fields.GetValueOrDefault("verified") == "true" || (retirementJournalFails && entry.Kind == JournalEntry.CopyRetired)) throw new IOException("journal unavailable");
            journal.Add(entry); return Task.CompletedTask;
        } };
        host.AfterRun = (_, variables, result) =>
        {
            if (variables?.GetValueOrDefault("worlds") == site.Stage)
                File.WriteAllText(Path.Combine(site.WorldsDirectory, "Campaign_backup_foreign.db"), "foreign");
            return result;
        };
        Assert.Throws<IOException>(() => site.Place(plan, Path.Combine(_root, "output"), default));
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(site.WorldsDirectory, "Campaign_backup_foreign.db")));
        Assert.True(File.Exists(Path.Combine(site.KeepIn, "Campaign.fwl")));
        Assert.False(File.Exists(Path.Combine(site.WorldsDirectory, "Campaign.fwl")));
        if (retirementJournalFails)
            for (int attempt = 0; attempt < 2; attempt++)
                Assert.Empty(await HostedWorldOnHost.RecoverAsync(host, Fields(journal), Path.Combine(site.WorldsDirectory, "Campaign"),
                    "run-1", TimeSpan.FromSeconds(30), default));
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(site.WorldsDirectory, "Campaign_backup_foreign.db")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExplicitSiteRefusesOccupiedStorageBeforeShipping(bool occupiedStage)
    {
        var (plan, site, _, journal) = Setup();
        string occupied = occupiedStage ? site.Stage : site.KeepIn;
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "do-not-touch"), "foreign");
        Assert.ThrowsAny<Exception>(() => site.Place(plan, Path.Combine(_root, "output"), default));
        Assert.Empty(journal);
        Assert.Equal(occupiedStage, Directory.Exists(site.Stage));
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(occupied, "do-not-touch")));
    }

    [Fact]
    public void InterruptedAllocationLeavesAnEmptyDirectoryAndTheNextPlacementCanProceed()
    {
        string output = Path.Combine(_root, "shared-output"), run = RunJournal.NewRunId();
        using var directory = RunJournal.UseLocalDirectory(Path.Combine(_root, "journal"));
        using var scope = RunJournal.UseRun(run);
        var abandoned = (HostedWorldOnHost.Site)HostedWorldOnHost.ThisMachine(output, "abandoned")(Path.Combine(_root, "worlds_local"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(output, run)));
        // The process dies after allocation: there has been no journal or user-world mutation to recover.
        var next = (HostedWorldOnHost.Site)HostedWorldOnHost.ThisMachine(output, "next")(Path.Combine(_root, "worlds_local"));
        Assert.NotEqual(abandoned.Stage, next.Stage);
        Assert.Contains("placement-2", next.Stage);
        string fixture = FakeInstalls.World(Path.Combine(_root, "fixture"));
        var plan = new HostWorldPlan { World = new() { Source = fixture, Sha256 = new(WorldFixture.Manifest(fixture)) }, WorldUid = "4242" };
        var placed = next.Place(plan, Path.Combine(_root, "input"), default);
        placed.Collect();
        Assert.True(File.Exists(Path.Combine(output, "placement-2", run, "host-world", "Campaign.fwl")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(output, run)));
    }

    [Fact]
    public async Task ParallelPlacementsIntoOneOutputReserveDifferentRunDirectories()
    {
        string output = Path.Combine(_root, "shared-output"), run = RunJournal.NewRunId();
        string fixture = FakeInstalls.World(Path.Combine(_root, "fixture"));
        var plan = new HostWorldPlan { World = new() { Source = fixture, Sha256 = new(WorldFixture.Manifest(fixture)) }, WorldUid = "4242" };
        using var directory = RunJournal.UseLocalDirectory(Path.Combine(_root, "journal"));
        using var scope = RunJournal.UseRun(run);
        using var barrier = new Barrier(2);
        await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            var site = HostedWorldOnHost.ThisMachine(output, "actor-" + index)(Path.Combine(_root, "client-" + index, "worlds_local"));
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(30)));
            var placed = site.Place(plan, Path.Combine(_root, "input-" + index), default);
            placed.Collect();
        })));
        Assert.True(File.Exists(Path.Combine(output, run, "host-world", "Campaign.fwl")));
        Assert.True(File.Exists(Path.Combine(output, "placement-2", run, "host-world", "Campaign.fwl")));
    }
}

internal sealed class ForwardingWorldHost(IGameHost inner) : IGameHost
{
    public Func<string, IReadOnlyDictionary<string, string>?, HostResult?>? InsteadOfRun { get; set; }
    public Func<string, IReadOnlyDictionary<string, string>?, string>? TransformScript { get; set; }
    public Action<string, IReadOnlyDictionary<string, string>?>? BeforeRun { get; set; }
    public Func<string, IReadOnlyDictionary<string, string>?, HostResult, HostResult>? AfterRun { get; set; }
    public string Name => inner.Name;
    public GameHostKind Kind => inner.Kind;
    public HostShell Shell => inner.Shell;
    public async Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout, CancellationToken cancellation = default)
    {
        BeforeRun?.Invoke(script, variables);
        var result = InsteadOfRun?.Invoke(script, variables) ?? await inner.RunAsync(TransformScript?.Invoke(script, variables) ?? script, variables, timeout, cancellation);
        return AfterRun?.Invoke(script, variables, result) ?? result;
    }
    public Task<HostLock> AcquireLockAsync(string path, string owner, TimeSpan timeout, CancellationToken cancellation = default) => inner.AcquireLockAsync(path, owner, timeout, cancellation);
    public Task<HostLockResult> CheckLockAsync(string path, string owner, TimeSpan timeout, CancellationToken cancellation = default) => inner.CheckLockAsync(path, owner, timeout, cancellation);
    public Task<HostLockResult> ReleaseLockAsync(string path, string owner, TimeSpan timeout, CancellationToken cancellation = default) => inner.ReleaseLockAsync(path, owner, timeout, cancellation);
    public Task<Shipment> ShipRevisionAsync(string repository, string revision, string directory, TimeSpan timeout, CancellationToken cancellation = default) => inner.ShipRevisionAsync(repository, revision, directory, timeout, cancellation);
    public Task<Shipment> ShipFilesAsync(string source, string directory, TimeSpan timeout, CancellationToken cancellation = default) => inner.ShipFilesAsync(source, directory, timeout, cancellation);
    public Task<long> LogOffsetAsync(string path, TimeSpan timeout, CancellationToken cancellation = default) => inner.LogOffsetAsync(path, timeout, cancellation);
    public Task<HostLogResult> WaitForLogAsync(string path, long offset, Regex success, IReadOnlyList<Regex>? failures, TimeSpan timeout, CancellationToken cancellation = default) => inner.WaitForLogAsync(path, offset, success, failures, timeout, cancellation);
    public Task<FetchedDirectory> FetchDirectoryAsync(string source, string directory, TimeSpan timeout, CancellationToken cancellation = default) => inner.FetchDirectoryAsync(source, directory, timeout, cancellation);
    public Task<CliTunnel> OpenCliTunnelAsync(int port, TimeSpan timeout, int localPort = 0, CancellationToken cancellation = default) => inner.OpenCliTunnelAsync(port, timeout, localPort, cancellation);
}
