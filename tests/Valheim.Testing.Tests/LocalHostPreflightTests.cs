using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
using Xunit;

public sealed class LocalHostPreflightTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("local-preflight-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private EnvironmentInventory Inventory(bool withClient = true)
    {
        var inventory = new EnvironmentInventory
        {
            Hosts = new() { ["here"] = new HostProfile
            {
                Kind = "local", Platform = "macos", Shell = "bash", Lock = Path.Combine(_root, "host.lock"),
            } },
            Environments = [new EnvironmentRecipe { Name = "server", Host = "here", Roles = ["server"],
                Install = Path.Combine(_root, "server"), Runtime = Path.Combine(_root, "runs", "server"), CliPort = 5551 }],
        };
        if (withClient) inventory.Environments.Add(new EnvironmentRecipe { Name = "client", Host = "here", Roles = ["client"],
            Install = Path.Combine(_root, "client"), Runtime = Path.Combine(_root, "runs", "client"), CliPort = 5552 });
        return inventory;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChecksEachSelectedLocalActorAndOnlyNeedsSteamForAClient(bool withClient)
    {
        var inventory = Inventory(withClient);
        var host = new FakeServerHost("here", Path.Combine(_root, "mirror"));
        var ports = new List<int>();
        var processKinds = new List<bool>();
        int steam = 0, desktop = 0, journal = 0;
        var probes = new LocalHostPreflight.Probes(
            Processes: (_, client, _, _) => { processKinds.Add(client); return Task.CompletedTask; },
            Port: (_, port, _, _) => { ports.Add(port); return Task.CompletedTask; },
            Lock: (_, _, _, _) => Task.FromResult(new HostLockResult(HostLockState.Free, null, "free")),
            MacDesktop: () => desktop++, SteamRunning: () => { steam++; return true; },
            Journals: (_, _) => { journal++; return Task.FromResult<IReadOnlyList<CampaignPreflightProblem>>([]); },
            Packaged: () => null);

        var problems = await LocalHostPreflight.InspectAsync(inventory,
            inventory.Environments.Select(recipe => new LocalHostPreflight.Actor(recipe.Name, recipe)),
            TimeSpan.FromSeconds(1), hostFactory: _ => host, probes: probes);

        Assert.Empty(problems);
        Assert.Equal(withClient ? [5551, 5552] : [5551], ports);
        Assert.Equal([withClient], processKinds); // one process scan for the shared host
        Assert.Equal(withClient ? 1 : 0, steam);
        Assert.Equal(withClient ? 1 : 0, desktop);
        Assert.Equal(1, journal);
    }

    [Fact]
    public async Task ReportsIndependentPreflightProblemsWithoutStoppingAtTheFirstOne()
    {
        var inventory = Inventory();
        var host = new FakeServerHost("here", Path.Combine(_root, "mirror"));
        var probes = new LocalHostPreflight.Probes(
            Processes: (_, _, _, _) => throw new InvalidOperationException("client already active"),
            Port: (_, port, _, _) => port == 5552 ? throw new InvalidOperationException("port busy") : Task.CompletedTask,
            Lock: (_, _, _, _) => Task.FromResult(new HostLockResult(HostLockState.HeldByOther, "another", "held by another run")),
            MacDesktop: () => throw new InvalidOperationException("screen locked"),
            SteamRunning: () => false,
            Journals: (_, _) => Task.FromResult<IReadOnlyList<CampaignPreflightProblem>>([new("here", "run journal", "run not recovered")]),
            Packaged: () => "packaged app");

        var problems = await LocalHostPreflight.InspectAsync(inventory,
            inventory.Environments.Select(recipe => new LocalHostPreflight.Actor(recipe.Name, recipe)),
            TimeSpan.FromSeconds(1), hostFactory: _ => host, probes: probes);

        Assert.Equal(["packaged app", "run journal", "host lock", "client desktop", "Steam session", "session", "ValheimCLI port"],
            problems.Select(problem => problem.Input));
        Assert.Equal(Assert.Throws<InvalidOperationException>(() => ClientSession.RequireSteamRunning(() => false)).Message,
            Assert.Single(problems, problem => problem.Input == "Steam session").Message);
        Assert.Contains(problems, problem => problem.Actor == "client" && problem.Input == "ValheimCLI port" && problem.Message == "port busy");
    }

    [Fact]
    public async Task WindowsProfileStorageUsesTheVolumeProbeBeforeAnyStage()
    {
        var inventory = new EnvironmentInventory
        {
            Hosts = new() { ["pc"] = new HostProfile { Kind = "local", Platform = "windows", Shell = "pwsh", Lock = @"C:\vt\lock" } },
            Environments = [new EnvironmentRecipe { Name = "server", Host = "pc", Roles = ["server"],
                Install = @"C:\game", Runtime = @"C:\runs", CliPort = 5551 }],
        };
        var host = new FakeServerHost("pc", Path.Combine(_root, "storage-mirror"), windows: true, kind: GameHostKind.Local);
        var source = new WindowsStorageVolume(@"C:\", "source", "NTFS", false);
        WindowsStorageVolume runtime = source;
        var probes = new LocalHostPreflight.Probes(
            Processes: (_, _, _, _) => Task.CompletedTask,
            Port: (_, _, _, _) => Task.CompletedTask,
            Lock: (_, _, _, _) => Task.FromResult(new HostLockResult(HostLockState.Free, null, "free")),
            ProfileStorage: (_, sourcePath, runtimePath, explicitRuntime, _, _) =>
            {
                if (ProfileRuntimeStorage.Refusal(sourcePath, source, runtimePath, runtime, explicitRuntime) is { } refusal)
                    throw new InvalidOperationException(refusal);
                return Task.FromResult<string?>("same NTFS volume");
            },
            Journals: (_, _) => Task.FromResult<IReadOnlyList<CampaignPreflightProblem>>([]), Packaged: () => null);

        var selected = inventory.Environments.Select(recipe => new LocalHostPreflight.Actor(recipe.Name, recipe));
        Assert.Empty(await LocalHostPreflight.InspectAsync(inventory, selected, TimeSpan.FromSeconds(1), hostFactory: _ => host, probes: probes));

        runtime = new WindowsStorageVolume(@"D:\", "runtime", "NTFS", false);
        var cross = Assert.Single(await LocalHostPreflight.InspectAsync(inventory, selected, TimeSpan.FromSeconds(1), hostFactory: _ => host, probes: probes),
            problem => problem.Input == "profile storage");
        Assert.Contains("explicit inventory runtime folder", cross.Message);

        runtime = new WindowsStorageVolume(@"C:\", "source", "exFAT", false);
        var unsupported = Assert.Single(await LocalHostPreflight.InspectAsync(inventory, selected, TimeSpan.FromSeconds(1), hostFactory: _ => host, probes: probes),
            problem => problem.Input == "profile storage");
        Assert.Contains("cannot hold the hard links", unsupported.Message);
        Assert.DoesNotContain(host.Scripts, script => script is "profile-seed" or "apply-stage");
    }

    // #612: `env preflight` on a Linux machine checks its server's linked profile through the one storage owner, the bash probe
    // on this machine's host; a Mac's server profile launches its source, so a Mac is asked nothing about mounts.
    [Fact]
    public async Task ALinuxServerProfileOnAnotherMountIsRefusedAndAMacOneIsNotProbed()
    {
        EnvironmentInventory Inventory(string platform) => new()
        {
            Hosts = new() { ["box"] = new HostProfile { Kind = "local", Platform = platform, Shell = "bash", Lock = "/vt/lock" } },
            Environments = [new EnvironmentRecipe { Name = "server", Host = "box", Roles = ["server"],
                Install = "/srv/valheim", Runtime = "/home/tester/runs", CliPort = 5551 }],
        };
        var probes = new LocalHostPreflight.Probes(
            Processes: (_, _, _, _) => Task.CompletedTask,
            Port: (_, _, _, _) => Task.CompletedTask,
            Lock: (_, _, _, _) => Task.FromResult(new HostLockResult(HostLockState.Free, null, "free")),
            Journals: (_, _) => Task.FromResult<IReadOnlyList<CampaignPreflightProblem>>([]), Packaged: () => null);
        var host = new FakeServerHost("box", Path.Combine(_root, "linux-storage-mirror"), kind: GameHostKind.Local);
        host.LinuxVolumes["/srv"] = new LinuxStorageVolume("/srv", "2065", "ext4", Writable: true);

        var linux = Inventory("linux");
        var refused = Assert.Single(await LocalHostPreflight.InspectAsync(linux, linux.Environments.Select(recipe => new LocalHostPreflight.Actor(recipe.Name, recipe)),
            TimeSpan.FromSeconds(1), hostFactory: _ => host, probes: probes));
        Assert.Equal(("server", "profile storage"), (refused.Actor, refused.Input));
        Assert.Contains("A Linux profile hard-links the install's game files, and a hard link cannot cross mounts. " +
            "Put the inventory runtime on the install's mount /srv.", refused.Message);
        Assert.Single(host.Runs, run => run.Script == "profile-storage");
        Assert.DoesNotContain(host.Scripts, script => script is "profile-seed" or "apply-stage" or "ship");

        var mac = Inventory("macos");
        Assert.Empty(await LocalHostPreflight.InspectAsync(mac, mac.Environments.Select(recipe => new LocalHostPreflight.Actor(recipe.Name, recipe)),
            TimeSpan.FromSeconds(1), hostFactory: _ => host, probes: probes));
        Assert.Single(host.Runs, run => run.Script == "profile-storage");
    }

    // #821: a client's profile is staged under the environment's own runtime folder (a regression, a campaign), so preflight
    // checks that folder through the same check the stage runs. It once substituted LocalClientCopy's default folder and
    // reported ELIGIBLE for an inventory runtime on another volume, which start then refused.
    private static EnvironmentInventory WindowsClientInventory(string runtime, bool runtimeExplicit = true) => new()
    {
        Hosts = new() { ["pc"] = new HostProfile { Kind = "local", Platform = "windows", Shell = "pwsh", Lock = @"C:\vt\lock" } },
        Environments = [new EnvironmentRecipe { Name = "client", Host = "pc", Roles = ["client"], Install = @"C:\game",
            Runtime = runtime, RuntimeExplicit = runtimeExplicit, CliPort = 5551 }],
    };

    private static LocalHostPreflight.Probes AllButStorage() => new(
        Processes: (_, _, _, _) => Task.CompletedTask,
        Port: (_, _, _, _) => Task.CompletedTask,
        Lock: (_, _, _, _) => Task.FromResult(new HostLockResult(HostLockState.Free, null, "free")),
        Desktop: _ => Task.CompletedTask, SteamRunning: () => true,
        Journals: (_, _) => Task.FromResult<IReadOnlyList<CampaignPreflightProblem>>([]), Packaged: () => null);

    [Fact]
    public async Task AClientRuntimeOnAnotherVolumeIsRefusedWithTheHardLinkLimit()
    {
        var inventory = WindowsClientInventory(@"V:\vtrun\local-client");
        var host = new FakeServerHost("pc", Path.Combine(_root, "client-storage-mirror"), windows: true, kind: GameHostKind.Local)
        {
            ProfileSourceVolume = new WindowsStorageVolume(@"C:\", "volume-c", "NTFS", false),
            ProfileRuntimeVolume = new WindowsStorageVolume(@"V:\", "volume-v", "NTFS", false),
        };

        var problems = await LocalHostPreflight.InspectAsync(inventory,
            [new LocalHostPreflight.Actor("client", inventory.Environments[0])], TimeSpan.FromSeconds(1),
            hostFactory: _ => host, probes: AllButStorage());

        var refused = Assert.Single(problems);
        Assert.Equal(("client", "profile storage"), (refused.Actor, refused.Input));
        Assert.Contains(@"the explicit inventory runtime folder V:\vtrun\local-client is on V:\ (NTFS), not the source install's volume C:\ (NTFS)", refused.Message);
        Assert.Contains("hard-links the install's game files, and a hard link cannot cross volumes", refused.Message);
        Assert.Contains(@"Put the inventory runtime on the install's NTFS volume C:\.", refused.Message);
        // The folder probed is the inventory's own, never a default the stage does not use; nothing was staged.
        Assert.Equal(@"V:\vtrun\local-client", host.Runs.Single(run => run.Script == "profile-storage").Variables["runtime"]);
        Assert.DoesNotContain(host.Scripts, script => script is "profile-seed" or "apply-stage");
    }

    [Fact]
    public async Task AClientRuntimeOnTheInstallVolumeAndTheDefaultRuntimePass()
    {
        foreach (bool runtimeExplicit in new[] { true, false })
        {
            var inventory = WindowsClientInventory(@"C:\vtrun\local-client", runtimeExplicit);
            var host = new FakeServerHost("pc", Path.Combine(_root, "client-same-mirror-" + runtimeExplicit), windows: true, kind: GameHostKind.Local);

            Assert.Empty(await LocalHostPreflight.InspectAsync(inventory,
                [new LocalHostPreflight.Actor("client", inventory.Environments[0])], TimeSpan.FromSeconds(1),
                hostFactory: _ => host, probes: AllButStorage()));
        }
    }

    // #821: env preflight is the same check start runs, with the same words, so the operator learns of the unreachable runtime
    // before starting. (The seams are only the fake host and its volumes; the verdict, wording and runtime are production's.)
    [Fact]
    public async Task EnvPreflightRefusesACrossVolumeClientRuntimeWithTheWordsStartUses()
    {
        var host = new FakeServerHost("pc", Path.Combine(_root, "env-mirror"), windows: true, kind: GameHostKind.Local)
        {
            ProfileSourceVolume = new WindowsStorageVolume(@"C:\", "volume-c", "NTFS", false),
            ProfileRuntimeVolume = new WindowsStorageVolume(@"V:\", "volume-v", "NTFS", false),
        };
        using var probes = LocalHostPreflight.ReplaceDefaultProbesForTest(AllButStorage() with { HostFactory = _ => host });
        string file = Path.Combine(_root, "windows-inventory.json");
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new
        {
            hosts = new Dictionary<string, object> { ["pc"] = new { kind = "local", platform = "windows", shell = "pwsh", @lock = @"C:\vt\lock" } },
            environments = new[] { new { name = "client", host = "pc", roles = new[] { "client" }, install = @"C:\game",
                runtime = @"V:\vtrun\local-client", architecture = "x64", cliPort = 5551 } },
            leaseHost = "pc", leaseDirectory = @"C:\vt\leases",
        }));

        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(3, await EnvCommand.RunAsync(["preflight", "--inventory", file], output, error));

        Assert.True(error.ToString().Length == 0, error.ToString());
        string refusal = Assert.Single(output.ToString().Split('\n'), line => line.StartsWith("REFUSED client profile storage: ", StringComparison.Ordinal));
        Assert.Contains(@"the explicit inventory runtime folder V:\vtrun\local-client is on V:\ (NTFS), not the source install's volume C:\ (NTFS)", refusal);
        Assert.Contains("a hard link cannot cross volumes", refusal);
        Assert.DoesNotContain("ELIGIBLE", output.ToString());
    }

    // #703: a Windows host without PowerShell 7 is refused by preflight with the one fix: the winget command, run in a desktop
    // terminal on that machine. A local host cannot start pwsh; a host reached over ssh hears it from its login shell (cmd.exe).
    // Every check that reaches the host refuses the same way, so preflight never reports the host as merely busy or unknown.
    [Theory]
    [InlineData("local")]
    [InlineData("ssh")]
    public async Task AWindowsHostWithoutPwshIsRefusedWithTheInstallCommand(string kind)
    {
        using var useMachine = EnvironmentInventory.UseMachine(new FakeMachine(HostProfile.CurrentPlatform));
        var inventory = new EnvironmentInventory
        {
            Hosts = new() { ["pc"] = new HostProfile { Kind = "local", Platform = "windows", Shell = "pwsh", Lock = @"C:\vt\lock" } },
            Environments = [new EnvironmentRecipe { Name = "server", Host = "pc", Roles = ["server"], Install = @"C:\Games\Valheim dedicated server",
                Runtime = @"C:\vt\runs", CliPort = 5551 }],
        };
        var missing = new NoPwsh(kind);
        IGameHost host = kind == "local" ? new LocalGameHost("pc", HostShell.Pwsh, missing)
            : new SshGameHost("pc", "tester@pc.example", HostShell.Pwsh, 0, null, null, "ssh", missing);
        var probes = new LocalHostPreflight.Probes(
            Journals: (_, _) => Task.FromResult<IReadOnlyList<CampaignPreflightProblem>>([]), Packaged: () => null);

        var problems = await LocalHostPreflight.InspectAsync(inventory,
            inventory.Environments.Select(recipe => new LocalHostPreflight.Actor(recipe.Name, recipe)),
            TimeSpan.FromSeconds(5), hostFactory: _ => host, probes: probes);

        Assert.True(missing.Calls > 0);
        Assert.Equal(["host lock", "session", "ValheimCLI port"], problems.Select(problem => problem.Input));
        Assert.All(problems, problem =>
        {
            Assert.Contains("winget install --id Microsoft.PowerShell --source winget", problem.Message);
            Assert.Contains("desktop terminal on that machine", problem.Message);
        });
    }

    // Every command fails as a Windows host without pwsh fails it: locally pwsh cannot be started; over ssh cmd.exe, the OpenSSH
    // login shell, does not know the command (exit 9009, which ssh reports as its low byte).
    private sealed class NoPwsh(string kind) : IProcessLauncher
    {
        public int Calls;
        public Task<ProcessExit> RunAsync(ProcessCall call, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(kind == "local"
                ? ProcessRunner.NotStarted(new System.Diagnostics.ProcessStartInfo("pwsh"), new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified."), TimeSpan.Zero, windows: true)
                : new ProcessExit(ProcessEnd.Exited, 9009 & 0xFF, "", "'pwsh' is not recognized as an internal or external command,\r\noperable program or batch file.\r\n", TimeSpan.Zero));
        }
        public IStartedProcess Start(string executable, IReadOnlyList<string> arguments) => throw new NotSupportedException();
    }
}
