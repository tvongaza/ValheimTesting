using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed partial class HostedServerRunTests
{
    private const string AccountReleaseStep = "release client player's Steam account lease";

    private void AssertAccountReleased(ObservedLeaseHost host)
    {
        var release = Step(AccountReleaseStep);
        if (release.GetProperty("Passed").GetBoolean()) return;
        // Outside _root: Dispose removes the fixture even when an assertion fails. Retain only the report and the fake
        // account's claim/receipt, never the prepared profile, install or unrelated machine's journal.
        string retained = Directory.CreateTempSubdirectory("vt-steam-release-failure-").FullName;
        File.WriteAllText(Path.Combine(retained, "result.json"), SanitizeLeaseDiagnostic(File.ReadAllText(Path.Combine(Output, "result.json"))));
        File.WriteAllText(Path.Combine(retained, "release-host.json"), SanitizeLeaseDiagnostic(JsonSerializer.Serialize(host.ReleaseResult)));
        string account = Path.Combine(Leases, LeaseBox.Pool, LeaseBox.Account);
        if (Directory.Exists(account))
            foreach (string file in Directory.GetFiles(account, "claim-*"))
                File.Copy(file, Path.Combine(retained, Path.GetFileName(file)));
        Assert.Fail(SanitizeLeaseDiagnostic(release.GetProperty("Error").GetString() ?? "The release step has no error.") +
            " Retained report, host result and claim/receipt: " + retained);
    }

    private string SanitizeLeaseDiagnostic(string text) => text.Replace(LeaseBox.SteamId, "<fake-steam-id>", StringComparison.Ordinal)
        .Replace(LeaseBox.OtherSteamId, "<fake-steam-id>", StringComparison.Ordinal).Replace(_root, "<fixture>", StringComparison.Ordinal)
        .Replace(_root.Replace("\\", "\\\\", StringComparison.Ordinal), "<fixture>", StringComparison.Ordinal)
        .Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<home>", StringComparison.Ordinal);

    [Theory]
    [InlineData("blocked receipt", false, SteamAccountState.Held)]
    [InlineData("lost reply", false, SteamAccountState.Free)]
    [InlineData("journal failure", true, SteamAccountState.Free)]
    public async Task ARefusedClientsRealLeaseReleaseDistinguishesWriteReplyAndJournalFailures(string fault, bool passed, SteamAccountState state)
    {
        LeaseBox.WritePool(_root, Leases);
        var run = WithClient(Accounts(check: true));
        run.Client.SteamUserReply = "VT-STEAMUSER unreadable controlled refusal\n";
        var leaseHost = new ObservedLeaseHost(Leases, fault);
        await PinnedServerRun.MainAsync(TestEnvironment.Read(run.Profile), ["run", run.Plan, Output], Options(run.Host, run.Server, context =>
        {
            using (context.OpenClient(PreparedClient(run.Client))) { }
            return Task.CompletedTask;
        }, run.Client, SignedInClientTransport(), leaseHost.Host));
        var release = Step(AccountReleaseStep);
        Assert.Equal(passed, release.GetProperty("Passed").GetBoolean());
        Assert.Equal(1, leaseHost.Releases);
        Assert.Equal(fault == "journal failure" ? 1 : 0, leaseHost.JournalFailures);
        Assert.NotNull(leaseHost.ReleaseResult);
        Assert.Equal(state, (await LeaseBox.StatusAsync(PoolFile)).State);
        string claim = Assert.Single(Directory.GetFiles(Path.Combine(Leases, LeaseBox.Pool, LeaseBox.Account), "claim-*")
            .Where(file => !file.EndsWith(".released", StringComparison.Ordinal)));
        Assert.Equal(fault != "blocked receipt", File.Exists(claim + ".released"));
        if (!passed) Assert.Contains("is not proven", release.GetProperty("Error").GetString());
        Assert.DoesNotContain(run.Client.Runs, entry => entry.Script == "client-start");
        if (fault == "blocked receipt")
        {
            var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertAccountReleased(leaseHost));
            string retained = error.Message.Split("Retained report, host result and claim/receipt: ", StringSplitOptions.None)[1];
            try
            {
                Directory.Delete(_root, recursive: true); // Simulate fixture cleanup: the diagnostic must survive it.
                Assert.Contains("is not proven", error.Message);
                Assert.DoesNotContain(_root, error.Message);
                Assert.True(File.Exists(Path.Combine(retained, "result.json")));
                Assert.True(File.Exists(Path.Combine(retained, "release-host.json")));
                Assert.True(File.Exists(Path.Combine(retained, Path.GetFileName(claim))));
                Assert.False(File.Exists(Path.Combine(retained, Path.GetFileName(claim) + ".released")));
                Assert.DoesNotContain(LeaseBox.SteamId, File.ReadAllText(Path.Combine(retained, "result.json")));
            }
            finally { Directory.Delete(retained, recursive: true); }
        }
    }

    [Fact]
    public async Task AClaimHeldOpenOnWindowsIsNotMistakenForAProvenRelease()
    {
        if (!OperatingSystem.IsWindows()) return; // FileShare.None is a Windows read refusal; the Windows lane runs this control.
        LeaseBox.WritePool(_root, Leases);
        var run = WithClient(Accounts(check: true));
        run.Client.SteamUserReply = "VT-STEAMUSER unreadable controlled refusal\n";
        var leaseHost = new ObservedLeaseHost(Leases, "held claim");
        await PinnedServerRun.MainAsync(TestEnvironment.Read(run.Profile), ["run", run.Plan, Output], Options(run.Host, run.Server, context =>
        {
            using (context.OpenClient(PreparedClient(run.Client))) { }
            return Task.CompletedTask;
        }, run.Client, SignedInClientTransport(), leaseHost.Host));
        var release = Step(AccountReleaseStep);
        Assert.False(release.GetProperty("Passed").GetBoolean());
        Assert.Contains("had already ended (gone)", release.GetProperty("Error").GetString());
        Assert.Equal(SteamAccountState.Held, (await LeaseBox.StatusAsync(PoolFile)).State);
        Assert.Equal(1, leaseHost.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRefusalsReleaseTheirOwnClaimsWithSharedOrIsolatedJournals(bool isolated)
    {
        using var other = new HostedServerRunTests();
        int arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task AtRelease()
        {
            if (Interlocked.Increment(ref arrived) == 2) both.TrySetResult();
            await both.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        async Task Check(HostedServerRunTests fixture)
        {
            LeaseBox.WritePool(fixture._root, fixture.Leases);
            var run = fixture.WithClient(Accounts(check: true));
            var profile = TestEnvironment.Read(run.Profile);
            // Both retain the real hosted fixture's repeated run-test id. Only the journal/lock root varies; the account
            // stores are always fixture-owned, as in the original test. Force the real releases to overlap.
            if (isolated) profile.Hosts[LeaseBox.Name].Lock = Path.Combine(fixture._root, "lease-lock");
            run.Client.SteamUserReply = "VT-STEAMUSER unreadable controlled refusal\n";
            var leaseHost = new ObservedLeaseHost(fixture.Leases, beforeRelease: AtRelease);
            await PinnedServerRun.MainAsync(profile, ["run", run.Plan, fixture.Output], Options(run.Host, run.Server, context =>
            {
                using (context.OpenClient(PreparedClient(run.Client))) { }
                return Task.CompletedTask;
            }, run.Client, SignedInClientTransport(), leaseHost.Host));
            fixture.AssertAccountReleased(leaseHost);
            Assert.Equal(SteamAccountState.Free, (await LeaseBox.StatusAsync(fixture.PoolFile)).State);
            Assert.Equal(1, leaseHost.Releases);
        }
        await Task.WhenAll(Check(this), Check(other));
    }
}

// The injected host remains a LocalGameHost: RunJournalOnHost uses its type to identify this machine's journal. Observe
// the existing process seam beneath it, forwarding every normal call to the real launcher without changing its payload.
internal sealed class ObservedLeaseHost : IProcessLauncher
{
    private readonly string _leases;
    private readonly string? _fault;
    private readonly Func<Task>? _beforeRelease;
    public LocalGameHost Host { get; }
    public int Releases { get; private set; }
    public int JournalFailures { get; private set; }
    public ProcessExit? ReleaseResult { get; private set; }

    public ObservedLeaseHost(string leases, string? fault = null, Func<Task>? beforeRelease = null)
    {
        _leases = leases; _fault = fault; _beforeRelease = beforeRelease;
        Host = new LocalGameHost(LeaseBox.Name, HostShell.Parse(LeaseBox.Shell), this);
    }

    public IStartedProcess Start(string executable, IReadOnlyList<string> arguments) => SystemProcessLauncher.Instance.Start(executable, arguments);

    public async Task<ProcessExit> RunAsync(ProcessCall call, CancellationToken cancellation)
    {
        string script = FakeLauncher.Script(call);
        var line = Regex.Match(script, @"^(?:line=|\$line = )'([^']*)'", RegexOptions.Multiline);
        if (_fault == "journal failure" && line.Success &&
            Encoding.UTF8.GetString(Convert.FromBase64String(line.Groups[1].Value)).Contains("\"kind\":\"lease-released\"", StringComparison.Ordinal))
        {
            JournalFailures++;
            return FakeLauncher.Exit(3, "", "controlled journal write failure" + FakeLauncher.Report(3));
        }
        string action = Host.Shell.Kind == HostShellKind.Bash ? "action='release'" : "$action = 'release'";
        bool lease = script.Contains(LeaseScripts.Bash, StringComparison.Ordinal) || script.Contains(LeaseScripts.PowerShell, StringComparison.Ordinal);
        if (!lease || !script.Contains(action, StringComparison.Ordinal)) return await SystemProcessLauncher.Instance.RunAsync(call, cancellation);
        Releases++;
        if (_beforeRelease != null) await _beforeRelease();
        string claim = Directory.GetFiles(Path.Combine(_leases, LeaseBox.Pool, LeaseBox.Account), "claim-*")
            .Single(file => Regex.IsMatch(Path.GetFileName(file), @"^claim-[0-9]{9}$"));
        if (_fault == "blocked receipt")
        {
            // Bash's -e treats a directory as an existing receipt; obstruct the path after load so this exercises the
            // real receipt write, not the already-released branch. PowerShell's File.Exists excludes directories.
            if (Host.Shell.Kind == HostShellKind.Bash)
            {
                const string write = "then : > \"$f.released\"";
                if (!script.Contains(write, StringComparison.Ordinal)) throw new TestSeamMismatchException("The lease receipt write seam no longer matches.");
                script = script.Replace(write, "then mkdir -- \"$f.released\"; : > \"$f.released\"", StringComparison.Ordinal);
                call = call with { Input = ScriptedGameHost.Payload(script) };
            }
            else Directory.CreateDirectory(claim + ".released");
        }
        using var held = _fault == "held claim" ? new FileStream(claim, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        try
        {
            var result = await SystemProcessLauncher.Instance.RunAsync(call, cancellation);
            ReleaseResult = result;
            return _fault == "lost reply" ? result with { Stdout = "", Stderr = "controlled lost reply" } : result;
        }
        finally { if (_fault == "blocked receipt") Directory.Delete(claim + ".released"); }
    }
}
