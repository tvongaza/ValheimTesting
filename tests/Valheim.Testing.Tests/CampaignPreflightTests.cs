using System.Text.Json;
using System.Text.Json.Nodes;
using Valheim.Testing.Game;
using Xunit;
using Valheim.Testing.GameSessions;

public sealed class CampaignPreflightTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("campaign-preflight-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string name, object data)
    {
        string file = Path.Combine(_root, name);
        File.WriteAllText(file, JsonSerializer.Serialize(data));
        return file;
    }

    // One Windows host carrying a server environment and a client environment: both actors may share it.
    private string Inventory(bool valid = true) => Write("inventory.json", new
    {
        hosts = new { pc = new { kind = "ssh", platform = "windows", shell = "powershell", @lock = @"C:\locks\test.lock", destination = "test@pc" } },
        environments = new object[]
        {
            new { name = "pc-server", host = "pc", roles = new[] { "server" }, install = @"C:\game", runtime = @"C:\runs", cliPort = 5577, localCliPort = 6577, gamePort = 2456 },
            new { name = "pc-client", host = valid ? "pc" : "unlisted", roles = new[] { "client" }, install = @"C:\client", runtime = @"C:\runs", cliPort = 5578, localCliPort = 6578 },
        },
        leaseHost = "pc",
        leaseDirectory = @"C:\leases",
    });

    [Fact]
    public async Task IndependentFaultsAcrossRolesAreAllReportedBeforeContactingAnyHost()
    {
        string inventory = Inventory(valid: false); // A client on an unlisted host: the inventory is invalid independently of the files below.
        string file = Write("campaign.json", new
        {
            inventory, world = Path.Combine(_root, "missing-world"),
            server = new { dependencyLock = "missing-server-lock.json", loaderPackage = "missing-loader.json" },
            clients = new Dictionary<string, object> { ["client-a"] = new { dependencyLock = "missing-client-lock.json" } },
        });
        var report = HostedCampaignPreparation.Inspect(file);
        Assert.False(report.Ready);
        Assert.Contains(report.Problems, problem => problem.Actor == "campaign" && problem.Input == "inventory");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "loader");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "dependencies and CLI packs");
        Assert.Contains(report.Problems, problem => problem.Actor == "client-a" && problem.Input == "dependencies and CLI packs");
        Assert.Contains(report.Problems, problem => problem.Actor == "client-a" && problem.Input == "character");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "world fixture");
        Assert.True(report.Problems.Count >= 6);
        using var output = new StringWriter();
        Assert.Equal(3, await SessionCommand.RunAsync(["check", file], output, new StringWriter()));
        Assert.Empty(report.Actors); // An invalid inventory selects no actor, even when other inputs can be checked.
        Assert.Contains("REFUSED server loader", output.ToString());
        Assert.Contains("REFUSED client-a character", output.ToString());
        var error = Assert.Throws<ArgumentException>(() => HostedCampaignPreparation.Check(file));
        Assert.Contains("client-a character", error.Message);
        Assert.Contains("server loader", error.Message);
    }

    [Fact]
    public async Task ReviewedFixtureUidIsCheckedIndependentlyOfMissingPackages()
    {
        string inventory = Inventory();
        string world = Path.Combine(_root, "world");
        FakeInstalls.World(world, "Small", seed: "Seed");
        string file = Write("campaign.json", new
        {
            inventory, world, worldUid = "9999", server = new { dependencyLock = "missing-lock.json" },
            clients = new Dictionary<string, object>(),
        });
        var report = HostedCampaignPreparation.Inspect(file);
        Assert.Contains(report.Actors, actor => actor.Name == "server" && actor.Host == "pc" && actor.Platform == "windows");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "world fixture" && problem.Message.Contains("world UID"));
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "dependencies and CLI packs");
        using var output = new StringWriter();
        Assert.Equal(3, await SessionCommand.RunAsync(["check", file, "--json"], output, new StringWriter()));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("Ready").GetBoolean());
        Assert.Contains(json.RootElement.GetProperty("Problems").EnumerateArray(),
            problem => problem.GetProperty("Input").GetString() == "world fixture");
    }

    [Fact]
    public async Task EnvAndSessionCommandsRefuseInvalidArgumentsWithoutReadingAHost()
    {
        foreach (string[] args in new string[][] { [], ["preflight", "--hosts"], ["preflight", "a.json", "--inventory", "b.json"], ["preflight", "--inventory"],
            ["preflight", "a", "b"], ["list", "--hosts"], ["list", "a.json"], ["list", "--inventory"], ["list", "--inventory", "--json"], ["setup"] })
        {
            var error = new StringWriter();
            Assert.Equal(2, await EnvCommand.RunAsync(args, new StringWriter(), error));
            Assert.Contains("Usage: valheim-test env", error.ToString());
        }
        // A session file given to env preflight (its old form) names the command that checks a session.
        foreach (string[] args in new string[][] { ["preflight", "session.json", "--hosts"], ["preflight", "--hosts", "session.json"], ["preflight", "--json", "session.json"] })
        {
            var moved = new StringWriter();
            Assert.Equal(2, await EnvCommand.RunAsync(args, new StringWriter(), moved));
            Assert.Contains("A session's check is valheim-test session check SESSION [--hosts] [--json].", moved.ToString());
        }
        var plain = new StringWriter();
        Assert.Equal(2, await EnvCommand.RunAsync(["preflight", "--hosts"], new StringWriter(), plain));
        Assert.DoesNotContain("session check", plain.ToString());
        foreach (string[] args in new string[][] { [], ["check"], ["check", "--hosts"], ["check", "a.json", "b.json"], ["run", "a.json"], ["check", "a.json", "--inventory", "b.json"] })
        {
            var error = new StringWriter();
            Assert.Equal(2, await SessionCommand.RunAsync(args, new StringWriter(), error));
            Assert.Contains("Usage: valheim-test session check SESSION", error.ToString());
        }
    }

    // env list shows the inventory as preflight reads it, and judges nothing: no verdict, and no refusal for a missing role or
    // a run this machine's journal holds.
    [Fact]
    public async Task EnvListShowsTheInventoryWithoutAVerdict()
    {
        var mirror = new FakeServerHost("mirror", Path.Combine(_root, "machine"));
        using var journal = RunJournal.UseLocalDirectory(mirror.Local(RunJournalStatusTests.Journal));
        RunJournalStatusTests.Line(mirror, "run-left", "server", RunJournalStatusTests.Gone, JournalEntry.CopyIntended, ("runtime", "/srv/runs/left/runtime"));
        string serverOnly = Write("server-only.json", new
        {
            hosts = new { pc = new { kind = "ssh", platform = "windows", shell = "powershell", @lock = @"C:\locks\test.lock", destination = "test@pc" } },
            environments = new object[] { new { name = "pc-server", host = "pc", roles = new[] { "server" }, install = @"C:\server", runtime = @"C:\runs", cliPort = 5577, gamePort = 2456 } },
        });
        using var listed = new StringWriter();
        Assert.Equal(0, await EnvCommand.RunAsync(["list", "--inventory", serverOnly], listed, new StringWriter()));
        Assert.Contains(@"pc-server: server on pc; install C:\server; runtime C:\runs; ValheimCLI port 5577, game port 2456", listed.ToString());
        Assert.DoesNotContain("REFUSED", listed.ToString());
        Assert.DoesNotContain("ELIGIBLE", listed.ToString());
        using var json = new StringWriter();
        Assert.Equal(0, await EnvCommand.RunAsync(["list", "--json", "--inventory", serverOnly], json, new StringWriter()));
        using (var document = JsonDocument.Parse(json.ToString()))
        {
            Assert.Equal("pc-server", Assert.Single(document.RootElement.GetProperty("Environments").EnumerateArray()).GetProperty("Name").GetString());
            Assert.False(document.RootElement.TryGetProperty("Ready", out _));
        }
        // The same inventory is refused by preflight: no client environment, and the run this machine's journal holds.
        using var refused = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight", "--inventory", serverOnly], refused, new StringWriter()));
        Assert.Contains("REFUSED: the inventory has no client environment.", refused.ToString());
        Assert.Contains(@"pc-server: server on pc; install C:\server", refused.ToString()); // the same listing, then the verdict
        Assert.Contains("REFUSED this-machine run journal: run run-left", refused.ToString());
        // An unreadable inventory is refused by list too.
        File.WriteAllText(serverOnly, "null");
        Assert.Equal(3, await EnvCommand.RunAsync(["list", "--inventory", serverOnly], new StringWriter(), new StringWriter()));
    }

    // With no campaign, preflight shows the inventory: this machine (here a test machine without Steam) plus the file's
    // machines. It names what was not found and refuses an inventory a one-off cannot run on.
    [Fact]
    public async Task EnvPreflightWithoutACampaignReportsTheInventoryAndWhatIsMissing()
    {
        // This machine's journal is this test's own: runs other test processes left in the shared one never decide it.
        using var journal = RunJournal.UseLocalDirectory(Path.Combine(_root, "journal"));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight"], output, error));
        Assert.Contains("This machine has none", error.ToString());
        Assert.Contains("Steam was not found", error.ToString());

        // A file of other machines is shown as written: nothing is detected for it.
        using var listed = new StringWriter();
        Assert.Equal(0, await EnvCommand.RunAsync(["preflight", "--inventory", Inventory()], listed, new StringWriter()));
        Assert.DoesNotContain("detected:", listed.ToString());
        Assert.Contains("ELIGIBLE: the inventory has a server and a client environment", listed.ToString());

        // A local environment the test machine cannot fill is named, with what was tried.
        string local = Write("local.json", new { environments = new object[] { new { name = "local-client", roles = new[] { "client" } } } });
        using var refused = new StringWriter();
        using var refusal = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight", "--inventory", local], refused, refusal));
        Assert.Contains("Environment local-client needs absolute install and runtime paths on local", refusal.ToString());
        Assert.Contains("Steam was not found (tried", refusal.ToString());

        // An empty file is refused, not a crash.
        File.WriteAllText(local, "null");
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight", "--inventory", local], new StringWriter(), new StringWriter()));
    }

    // #406: valheim-test inside a Windows package (a packaged desktop app that starts it) has its AppData writes redirected into
    // the package, where the server task and the game cannot see them. Preflight refuses with the cause and the fix; an
    // unpackaged process passes. This test process is never packaged, on any OS.
    [Fact]
    public async Task EnvPreflightRefusesAProcessInsideAWindowsPackage()
    {
        Assert.Null(PackagedApp.Refusal());
        Assert.Null(PackagedApp.RefusalFor(null));
        // This test's own empty journal: the default one is shared with tests running beside it.
        using var journal = RunJournal.UseLocalDirectory(Path.Combine(_root, "packaged-journal"));
        string inventory = Inventory();
        using var refused = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight", "--inventory", inventory], refused, new StringWriter(),
            () => PackagedApp.RefusalFor("Claude_1.0.0.0_x64__test")));
        Assert.Contains("REFUSED this-machine packaged app: valheim-test is running inside the packaged app Claude_1.0.0.0_x64__test.", refused.ToString());
        Assert.Contains("Run valheim-test from an ordinary terminal", refused.ToString());
        Assert.EndsWith("REFUSED: valheim-test runs inside a packaged app; run it from an ordinary terminal." + Environment.NewLine, refused.ToString());
        using var json = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight", "--json", "--inventory", inventory], json, new StringWriter(),
            () => PackagedApp.RefusalFor("Claude_1.0.0.0_x64__test")));
        var listing = System.Text.Json.JsonDocument.Parse(json.ToString()).RootElement;
        Assert.False(listing.GetProperty("Ready").GetBoolean());
        Assert.Contains(listing.GetProperty("Problems").EnumerateArray(), problem => problem.GetProperty("Input").GetString() == "packaged app");
        // Not packaged: the same inventory is eligible.
        using var eligible = new StringWriter();
        Assert.True(await EnvCommand.RunAsync(["preflight", "--inventory", inventory], eligible, new StringWriter(), () => null) == 0, eligible.ToString());
        Assert.Contains("ELIGIBLE:", eligible.ToString());
        Assert.Equal(OperatingSystem.IsWindows(), eligible.ToString().Contains("This process is not inside a packaged app.", StringComparison.Ordinal));
    }

    // #257: a bare preflight reads this machine's journal (through its own shell) and refuses while a run of another process
    // left something there unrecovered, in the session check's words; once that run is over, the inventory is eligible again.
    [Fact]
    public async Task EnvPreflightWithoutACampaignRefusesWhileThisMachinesJournalHoldsAnUnrecoveredRun()
    {
        var mirror = new FakeServerHost("mirror", Path.Combine(_root, "machine"));
        using var journal = RunJournal.UseLocalDirectory(mirror.Local(RunJournalStatusTests.Journal));
        RunJournalStatusTests.Line(mirror, "run-left", "server", RunJournalStatusTests.Gone, JournalEntry.CopyIntended, ("runtime", "/srv/runs/left/runtime"));
        string inventory = Inventory();

        using var refused = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight", "--inventory", inventory], refused, new StringWriter()));
        Assert.Contains("REFUSED this-machine run journal: run run-left left 1 thing(s) on this-machine (never journalled its end; its runner is gone; " +
            "it left what is provably its own); see valheim-test env status, then valheim-test env recover --run run-left", refused.ToString());
        Assert.Contains("REFUSED: a run on this machine is going or was left unrecovered", refused.ToString());
        Assert.DoesNotContain("ELIGIBLE", refused.ToString());
        using var json = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight", "--inventory", inventory, "--json"], json, new StringWriter()));
        using (var document = JsonDocument.Parse(json.ToString()))
        {
            Assert.False(document.RootElement.GetProperty("Ready").GetBoolean());
            Assert.Equal("run journal", Assert.Single(document.RootElement.GetProperty("Problems").EnumerateArray()).GetProperty("Input").GetString());
        }

        RunJournalStatusTests.Line(mirror, "run-left", "server", RunJournalStatusTests.Gone, JournalEntry.CopyRetired, ("runtime", "/srv/runs/left/runtime"));
        RunJournalStatusTests.Line(mirror, "run-left", "run", RunJournalStatusTests.Gone, JournalEntry.RunEnded, ("state", "passed"), ("cleanupVerified", "true"));
        // A run of another machine that used this one only as its lease host, and holds nothing here now, is no conflict (as in a
        // campaign's check); one that left a copy here may still be going and is.
        var elsewhere = new JournalRunner("another-machine-" + Guid.NewGuid().ToString("N")[..6], 4242, DateTime.UtcNow.AddHours(-1));
        RunJournalStatusTests.Line(mirror, "run-lease-host", "player", elsewhere, JournalEntry.LeaseReleased, ("account", "alt1"), ("owner", "o"));
        RunJournalStatusTests.Line(mirror, "run-uses-here", "server", elsewhere, JournalEntry.CopyIntended, ("runtime", "/srv/runs/elsewhere/runtime"));
        using var elsewhereUsed = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight", "--inventory", inventory], elsewhereUsed, new StringWriter()));
        Assert.Contains("run run-uses-here on this-machine may still be going", elsewhereUsed.ToString());
        Assert.DoesNotContain("run-lease-host", elsewhereUsed.ToString());
        RunJournalStatusTests.Line(mirror, "run-uses-here", "server", elsewhere, JournalEntry.CopyRetired, ("runtime", "/srv/runs/elsewhere/runtime"));
        using var eligible = new StringWriter();
        Assert.Equal(0, await EnvCommand.RunAsync(["preflight", "--inventory", inventory], eligible, new StringWriter()));
        Assert.Contains("ELIGIBLE: the inventory has a server and a client environment, and this machine's journal holds no run going or left unrecovered.", eligible.ToString());
    }

    // A campaign that leaves out its inventory is assigned on this machine; without Steam that is refused with what was tried.
    [Fact]
    public void ACampaignWithoutAnInventoryUsesThisMachine()
    {
        string file = Write("campaign.json", new
        {
            server = new { dependencyLock = "server.lock.json" },
            clients = new Dictionary<string, object>(),
        });
        var report = HostedCampaignPreparation.Inspect(file);
        var problem = Assert.Single(report.Problems, problem => problem.Input == "inventory");
        Assert.Contains("This machine has none", problem.Message);
        Assert.Contains("Steam was not found", problem.Message);
    }

    [Fact]
    public async Task ReadOnlyHostCheckKeepsIndependentLocalSessionAndInstallFailures()
    {
        string inventory = Inventory();
        string file = Write("campaign.json", new
        {
            inventory, server = new { dependencyLock = "missing-lock.json" }, clients = new Dictionary<string, object>(),
        });
        var host = new FakeServerHost("pc", Path.Combine(_root, "mirror"), windows: true) { GameActive = true, PortBusy = true };
        var report = await HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(2), _ => host);
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "dependencies and CLI packs");
        Assert.Contains(report.Problems, problem => problem.Actor == "pc" && problem.Input == "session");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "game and loader");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "ValheimCLI port");
        Assert.Contains(host.Scripts, script => script == "game-process");
        Assert.Contains(host.Scripts, script => script == "list");
        Assert.DoesNotContain(host.Scripts, script => script is "copy" or "start" or "apply-stage");
        Assert.DoesNotContain(report.Problems, problem => problem.Input == "server task"); // an elevated host registers its S4U task
    }

    // Stage 2's journal item (#257): a run another process left on a campaign host refuses the preflight and names the command;
    // a live run's client is named as that run's in the conflicting-use refusal, an unknown one as the user's own.
    [Fact]
    public async Task ARunLeftOnACampaignHostRefusesThePreflightAndConflictsNameTheirRun()
    {
        string file = Write("campaign.json", new
        {
            inventory = Inventory(), server = new { dependencyLock = "missing-lock.json" }, clients = new Dictionary<string, object>(),
        });
        var host = new FakeServerHost("pc", Path.Combine(_root, "mirror"), windows: true);
        void Line(string run, JournalRunner runner, string kind, params (string Key, string Value)[] fields)
        {
            string path = host.Local(@"C:\locks\journal\" + run + @"\server.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["utc"] = DateTime.UtcNow.AddMinutes(-5).ToString("O"), ["run"] = run, ["actor"] = "server", ["kind"] = kind,
                ["fields"] = fields.ToDictionary(field => field.Key, field => field.Value),
                ["runner"] = new Dictionary<string, object> { ["machine"] = runner.Machine, ["pid"] = runner.Pid, ["startedUtc"] = runner.StartedUtc.ToString("O") },
            }) + "\n");
        }
        Line("run-left", RunJournalStatusTests.Gone, JournalEntry.CopyIntended, ("runtime", @"C:\runs\vt-prep-left-server\runtime"), ("stage", @"C:\runs\vt-prep-left-server\staging"));

        var report = await HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(2), _ => host);
        var left = Assert.Single(report.Problems, problem => problem.Input == "run journal");
        Assert.Equal("pc", left.Actor);
        Assert.Contains("valheim-test env recover --run run-left", left.Message);
        Assert.DoesNotContain(host.Scripts, script => script is "copy" or "start" or "apply-stage" or "cleanup-stage");

        // Recovered, it no longer refuses.
        Assert.True((await RunRecovery.RecoverAsync(new Dictionary<string, HostProfile> { ["pc"] = new() { Kind = "ssh", Lock = @"C:\locks\test.lock" } },
            _ => host, "run-left", false, TimeSpan.FromSeconds(2))).Recovered);
        report = await HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(2), _ => host);
        Assert.DoesNotContain(report.Problems, problem => problem.Input == "run journal");

        // Another run going on this host, whose runner is a live process on this machine: the preflight refuses it, and the
        // session refusal names its client as that run's and another client as the user's own.
        using var runner = OperatingSystem.IsWindows()
            ? System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!
            : System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sleep", "60") { UseShellExecute = false })!;
        var going = new JournalRunner(Environment.MachineName, runner.Id, runner.StartTime.ToUniversalTime());
        host.Running(77, "555");
        Line("run-going", going, JournalEntry.ProcessStarted, ("pid", "77"), ("startIdentity", "555"), ("commandLineSha256", FakeServerHost.CommandLineSha256("77")));
        host.GameActive = true; host.GameProcessIds = "77,78";
        try
        {
            report = await HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(2), _ => host);
            Assert.Contains(report.Problems, problem => problem.Input == "run journal" && problem.Message.StartsWith("run run-going is still going on pc"));
            string message = Assert.Single(report.Problems, problem => problem.Input == "session").Message;
            Assert.Contains("process 77 of run run-going (LIVE)", message);
            Assert.Contains("process 78, which no run journalled: a game of your own", message);
        }
        finally { runner.Kill(); runner.WaitForExit(10_000); }
    }

    // A Windows server host whose account can register neither server task (not elevated, and no desktop session of its own)
    // is refused before anything is copied, naming why; the station's first zero-config launch failed there instead.
    [Fact]
    public async Task AServerHostThatCannotRegisterTheServerTaskIsRefusedBeforeCopying()
    {
        string file = Write("campaign.json", new
        {
            inventory = Inventory(), server = new { dependencyLock = "missing-lock.json" }, clients = new Dictionary<string, object>(),
        });
        var host = new FakeServerHost("pc", Path.Combine(_root, "mirror"), windows: true)
        { ServerTaskLogon = "unsupported pc\\tester is not elevated, which a session-0 server task needs, and has no desktop session for one in its own session; run from your own desktop session, or over SSH as an administrator" };
        var report = await HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(2), _ => host);
        var problem = Assert.Single(report.Problems, problem => problem.Input == "server task");
        Assert.Equal("server", problem.Actor);
        Assert.Contains("is not elevated", problem.Message);
        Assert.Contains(host.Scripts, script => script == "server-logon");
        Assert.DoesNotContain(host.Scripts, script => script is "copy" or "start");
        // A desktop session's interactive task is as good as an elevated one's S4U task.
        host.ServerTaskLogon = "interactive";
        report = await HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(2), _ => host);
        Assert.DoesNotContain(report.Problems, problem => problem.Input == "server task");
    }

    // A Mac hosts clients only: the inventory refuses a macOS server environment, local or remote, before any host check,
    // so the macOS port check (netstat) never clears one (#435's preflight check for it was unreachable and is gone).
    [Fact]
    public async Task ADedicatedServerOnAMacIsRefusedByTheInventory()
    {
        string inventory = Write("mac-inventory.json", new
        {
            hosts = new { mac = new { kind = "ssh", platform = "macos", shell = "bash", @lock = "/vt/lock", destination = "test@mac" } },
            environments = new object[]
            {
                new { name = "mac-server", host = "mac", roles = new[] { "server" }, install = "/opt/server", runtime = "/vt/runs", cliPort = 5577, localCliPort = 6577, gamePort = 2456 },
            },
            leaseHost = "mac", leaseDirectory = "/vt/leases",
        });
        string file = Write("campaign.json", new
        {
            inventory, server = new { dependencyLock = "missing-lock.json" }, clients = new Dictionary<string, object>(),
        });
        var host = new FakeServerHost("mac", Path.Combine(_root, "mirror"));
        var report = await HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(2), _ => host);
        Assert.False(report.Ready);
        Assert.Contains(report.Problems, problem => problem.Input == "inventory" && problem.Message.Contains("macOS dedicated servers are not supported"));
        Assert.DoesNotContain(host.Scripts, script => script is "copy" or "ship" or "start" or "apply-stage" or "port");
    }

    // A probe's refusal of a kind no check listed (a bash host that cannot tell which ports are in use) fills its own line
    // beside the others; it used to escape the concurrent preflight as an exception and lose every other fault.
    [Fact]
    public async Task AHostThatCannotTellItsPortsIsReportedBesideTheOtherFaultsNotThrown()
    {
        string inventory = Write("linux-inventory.json", new
        {
            hosts = new { box = new { kind = "ssh", platform = "linux", shell = "bash", @lock = "/vt/lock", destination = "test@box" } },
            environments = new object[]
            {
                new { name = "box-server", host = "box", roles = new[] { "server" }, install = "/opt/server", runtime = "/vt/runs", cliPort = 5577, localCliPort = 6577, gamePort = 2456 },
            },
            leaseHost = "box", leaseDirectory = "/vt/leases",
        });
        string file = Write("campaign.json", new
        {
            inventory, server = new { dependencyLock = "missing-lock.json" }, clients = new Dictionary<string, object>(),
        });
        var host = new FakeServerHost("box", Path.Combine(_root, "mirror")) { PortReply = "VT-PORT unknown\n", GameActive = true };
        var report = await HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(2), _ => host);
        Assert.False(report.Ready);
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "ValheimCLI port" && problem.Message.Contains("cannot tell which ports are in use"));
        Assert.Contains(report.Problems, problem => problem.Actor == "box" && problem.Input == "session");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "dependencies and CLI packs");
        Assert.DoesNotContain(host.Scripts, script => script is "copy" or "ship" or "start" or "apply-stage");
    }

    [Fact]
    public async Task UnverifiableSteamIdentityIsReportedWithoutHidingOtherHostFaults()
    {
        string inventory = Inventory();
        string file = Write("campaign.json", new
        {
            inventory, server = new { dependencyLock = "missing-server-lock.json" },
            clients = new Dictionary<string, object> { ["client-a"] = new { dependencyLock = "missing-client-lock.json" } },
        });
        var host = new FakeServerHost("pc", Path.Combine(_root, "mirror"), windows: true) { SteamUserReply = "VT-STEAMUSER unreadable\n" };
        var report = await HostedCampaignPreparation.InspectAsync(file, TimeSpan.FromSeconds(2), _ => host);
        Assert.Contains(report.Problems, problem => problem.Actor == "client-a" && problem.Input == "Steam identity");
        Assert.Contains(report.Problems, problem => problem.Actor == "server" && problem.Input == "game and loader");
        Assert.Contains(report.Problems, problem => problem.Actor == "client-a" && problem.Input == "game and loader");
        // The server and the client share the host: that is no conflict.
        Assert.Equal(["pc", "pc"], report.Actors.Select(actor => actor.Host));
    }
}
