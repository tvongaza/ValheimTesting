using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed partial class HostedServerRunTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootRemovalRequiresADurableLaunchSettlementEvenWhenStartedWasLost(bool settleFails)
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        host.JournalFailsFor = JournalEntry.ProcessStarted;
        int code = await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, _ =>
        {
            if (settleFails) host.JournalFailsFor = JournalEntry.LaunchSettled;
            return Task.CompletedTask;
        }));
        Assert.Equal(settleFails ? 1 : 0, code);
        Assert.Equal(settleFails, Directory.Exists(host.Local(RunDirectory + "/boot-1")));
        var journal = await RunJournalOnHost.ReadAsync(host, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(journal, record => record.Entry.Kind == JournalEntry.ProcessStarted);
        Assert.Equal(!settleFails, journal.Any(record => record.Entry.Kind == JournalEntry.LaunchSettled));
    }

    [Fact]
    public async Task APartialBootFetchDoesNotAuthorizeRemovingItsDirectory()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        host.AfterFetch = target =>
        {
            if (Path.GetFileName(target) == "boot-1") throw new IOException("boot archive failed verification");
        };
        Assert.NotEqual(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.True(Directory.Exists(Path.Combine(Output, "boot-1")));
        Assert.True(Directory.Exists(host.Local(RunDirectory + "/boot-1")));
        Assert.DoesNotContain(host.Runs, run => run.Script == "cleanup-stage" && run.Variables["runtime"] == RunDirectory + "/boot-1");
        var journal = await RunJournalOnHost.ReadAsync(host, "/var/tmp/vt/journal", RunId, TimeSpan.FromSeconds(5));
        Assert.Contains(journal, record => record.Entry.Kind == JournalEntry.CopyKept && record.Entry.Fields["runtime"] == RunDirectory + "/boot-1");
    }

    [Fact]
    public async Task APartialWorldFetchKeepsTheOnlyHostCopyVisible()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        host.AfterFetch = target =>
        {
            if (Path.GetFileName(target) == "host-world") throw new IOException("fetch interrupted after partial extraction");
        };
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        Assert.True(Directory.Exists(Path.Combine(Output, "host-world"))); // even a destination on disk is not proof
        Assert.True(Directory.Exists(host.Local(RunDirectory + "/world")));
        Assert.False(Directory.Exists(host.Local(RunDirectory + "/boot-1")));
        var status = Assert.Single((await RunJournalStatus.InspectAsync(LinuxBox(), _ => host, TimeSpan.FromSeconds(5))).Runs);
        var kept = Assert.Single(status.Items, item => item.Kind == "copy");
        Assert.Equal(RunDirectory + "/world", kept.What);
        Assert.True(kept.Kept);
        Assert.Contains("fetch failed", kept.Status);
    }

    [Fact]
    public async Task AFetchedCopyIsNotRemovedWithoutItsDurableReceipt()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server, _ =>
        {
            host.JournalFailsFor = JournalEntry.CopyDone;
            return Task.CompletedTask;
        })));
        Assert.True(File.Exists(Path.Combine(Output, "host-world", "worlds_local", "Test.db")));
        Assert.True(File.Exists(Path.Combine(Output, "boot-1", "game-0.log")));
        foreach (string name in new[] { "world", "boot-1" })
        {
            Assert.True(Directory.Exists(host.Local(RunDirectory + "/" + name)));
            Assert.DoesNotContain(host.Runs, run => run.Script == "cleanup-stage" && run.Variables["runtime"] == RunDirectory + "/" + name);
        }
    }

    [Fact]
    public async Task FetchedEvidenceIsRemovedAfterItsReceiptAndBeforeTheHostUnlocks()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        host.AfterRelease = _ => Assert.False(Directory.Exists(host.Local(RunDirectory)));
        Assert.Equal(0, await PinnedServerRun.MainAsync(TestEnvironment.Read(profile), ["run", plan, Output], Options(host, server)));
        foreach (string name in new[] { "world", "boot-1" })
        {
            string directory = RunDirectory + "/" + name;
            int fetch = host.Runs.FindIndex(run => run.Script == "fetch" && run.Variables["dir"] == directory);
            int receipt = host.Runs.FindIndex(run => run.Script == "journal" &&
                System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(run.Variables["line"])) is var line &&
                line.Contains("\"fetched\":\"true\"") && line.Contains(directory));
            int remove = host.Runs.FindIndex(run => run.Script == "cleanup-stage" && run.Variables["runtime"] == directory);
            Assert.True(fetch >= 0 && receipt > fetch && remove > receipt);
        }
        Assert.False(Directory.Exists(host.Local(RunDirectory)));
        Assert.Equal(host.Claims, host.Releases);
    }
}
