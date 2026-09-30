using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

/// <summary>
/// A host that keeps its directories in a local mirror and answers the server, install and client scripts the way a Linux host
/// does, without a shell: the full remote lifecycle runs against it. Every script run is recorded with its variables.
/// </summary>
internal sealed class FakeServerHost : IGameHost
{
    private readonly string _mirror;
    private readonly FakeOwnedServer? _server;
    private readonly object _sync = new();
    private readonly Dictionary<int, (string Start, Func<CancellationToken, Task<int>> Exit, Action Stop)> _processes = [];
    private int _nextClient = 77;

    public FakeServerHost(string name, string mirror, FakeOwnedServer? server = null, int tunnelPort = 15577)
    {
        Name = name; _mirror = mirror; _server = server; TunnelPort = tunnelPort;
    }
    public string Name { get; }
    public GameHostKind Kind => GameHostKind.Ssh;
    public HostShell Shell => HostShell.Bash;
    public int TunnelPort { get; }
    public List<(string Script, IReadOnlyDictionary<string, string> Variables)> Runs { get; } = [];
    public List<string> Claims { get; } = [];
    public List<string> Releases { get; } = [];
    /// <summary>Another run's claimant holding the lock.</summary>
    public string? HeldBy { get; set; }
    public Dictionary<string, HostResult> Failures { get; } = [];
    public Exception? TunnelFailure { get; set; }
    public bool PortBusy { get; set; }
    /// <summary>The server ignores the clean stop's SIGINT, so it is killed after the wait.</summary>
    public bool IgnoreQuit { get; set; }
    private string? _runtime; // The host runtime of the last server start, whose log a clean stop appends to.
    public List<FakeForward> Tunnels { get; } = [];
    /// <summary>What the copy does to the runtime after copying, for example editing a file.</summary>
    public Action<string>? AfterCopy { get; set; }
    public List<(string Game, string Start)> Stops { get; } = [];
    public IReadOnlyList<string> Scripts { get { lock (_sync) return Runs.Select(run => run.Script).ToList(); } }

    public string Local(string hostPath) => Path.Combine([_mirror, .. hostPath.Split('/', StringSplitOptions.RemoveEmptyEntries)]);

    private static HostResult Ok(string stdout) => new(HostOutcome.Exited, 0, stdout, "", TimeSpan.FromMilliseconds(3), false);
    public static HostResult TransportFailure => new(HostOutcome.TransportFailed, null, "", "ssh: connect to host test port 22: Connection refused", TimeSpan.FromMilliseconds(3), false);

    private static string ScriptName(string script) =>
        ReferenceEquals(script, HostInstallScripts.Copy) ? "copy" :
        ReferenceEquals(script, HostInstallScripts.BashList) ? "list" :
        ReferenceEquals(script, HostInstallScripts.BashPort) ? "port" :
        ReferenceEquals(script, HostServerScripts.Start) ? "start" :
        ReferenceEquals(script, HostServerScripts.Keep) ? "keep" :
        ReferenceEquals(script, InteractiveScripts.LinuxWait) ? "wait" :
        ReferenceEquals(script, InteractiveScripts.LinuxStop) ? "stop" :
        ReferenceEquals(script, InteractiveScripts.LinuxStart) ? "client-start" :
        ReferenceEquals(script, HostedClientScripts.BashKeep) ? "client-keep" :
        ReferenceEquals(script, HostedClientScripts.BashMoveAside) ? "move-aside" : "other";

    public async Task<HostResult> RunAsync(string script, IReadOnlyDictionary<string, string>? variables, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var v = variables ?? new Dictionary<string, string>();
        string name = ScriptName(script);
        lock (_sync) Runs.Add((name, v));
        if (Failures.TryGetValue(name, out var failure)) return failure;
        switch (name)
        {
            case "copy":
                CopyDirectory(Local(v["source"]), Local(v["dest"]));
                AfterCopy?.Invoke(Local(v["dest"]));
                return Ok("VT-COPY copied\n");
            case "list":
            {
                string root = Local(v["root"]);
                if (!Directory.Exists(root)) return Ok("VT-LIST missing\n");
                var text = new StringBuilder();
                foreach (var (relative, sha) in WorldFixture.Manifest(root)) text.Append(sha).Append("  ./").Append(relative.Replace('\\', '/')).Append('\n');
                string patchers = Path.Combine(root, "BepInEx", "patchers");
                if (Directory.Exists(patchers))
                    foreach (string entry in Directory.EnumerateFileSystemEntries(patchers)) text.Append("VT-PATCHER ").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFileName(entry)))).Append('\n');
                if (File.Exists(Path.Combine(root, ServerLaunch.LinuxExecutable))) text.Append("VT-EXEC ").Append(ServerLaunch.LinuxExecutable).Append('\n');
                return Ok(text.Append("VT-LIST done\n").ToString());
            }
            case "port": return Ok(PortBusy ? "VT-PORT busy\n" : "VT-PORT free\n");
            case "start":
            {
                string token = Spec(v["spec"]).Single(line => line.Kind == "env" && line.Text.StartsWith("TEST_SESSION_TOKEN=", StringComparison.Ordinal)).Text["TEST_SESSION_TOKEN=".Length..];
                var process = (FakeServerProcess)_server!.Launch(token);
                string boot = Local(v["dir"]);
                Directory.CreateDirectory(boot);
                File.WriteAllText(Path.Combine(boot, "stdout.log"), "server stdout\n");
                _runtime = v["runtime"];
                string log = Path.Combine(Local(v["runtime"]), "BepInEx", "LogOutput.log");
                File.WriteAllText(log, $"[Info   :   BepInEx] fake boot {process.Id}\n[Info   :valheimCLI] Command server listening on 127.0.0.1:5577\n");
                // A crossplay server opens a lobby per boot, as the game logs it.
                if (Spec(v["spec"]).Any(line => line.Kind == "arg" && line.Text == "-crossplay"))
                    File.AppendAllText(log, $"[Info   : Unity Log] Created PlayFab lobby with ID \"lobby-{process.Id}\", ConnectionString \"c\" and owned by \"P\"\n");
                string start = (9000 + process.Id).ToString();
                lock (_sync) _processes[process.Id] = (start, ct => process.WaitForExitAsync(ct), () => process.Stop(TimeSpan.FromSeconds(1)));
                return Ok($"VT-SERVER started {process.Id} {start}\n");
            }
            case "client-start":
            {
                var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                int id; lock (_sync) id = _nextClient++;
                Directory.CreateDirectory(Local(v["dir"]));
                File.WriteAllText(Path.Combine(Local(v["install"]), "BepInEx", "LogOutput.log"), "[Info   :valheimCLI] Command server listening on 127.0.0.1:5578\n");
                lock (_sync) _processes[id] = ("555", ct => exit.Task.WaitAsync(ct), () => exit.TrySetResult(137));
                return Ok($"VT-INTERACTIVE started {id} 555\n");
            }
            case "wait":
            {
                (string Start, Func<CancellationToken, Task<int>> Exit, Action Stop) process;
                lock (_sync) process = _processes[int.Parse(v["game"])];
                if (process.Start != v["start"]) return Ok("VT-WAIT exited ?\n");
                return Ok($"VT-WAIT exited {await process.Exit(cancellation)}\n");
            }
            case "stop":
            {
                lock (_sync) Stops.Add((v["game"], v["start"]));
                (string Start, Func<CancellationToken, Task<int>> Exit, Action Stop) process;
                lock (_sync) if (!_processes.TryGetValue(int.Parse(v["game"]), out process) || process.Start != v["start"]) return Ok("VT-STOP gone\n");
                process.Stop();
                if (v.TryGetValue("quit", out string? quit) && quit != "0" && !IgnoreQuit)
                {
                    // The game quits by itself. A server's shutdown retires its boot's lobby, as 1.0.16 logs it; a client host has no server runtime.
                    if (_runtime == null) return Ok("VT-STOP quit\n");
                    string log = Path.Combine(Local(_runtime), "BepInEx", "LogOutput.log");
                    string text = File.Exists(log) ? File.ReadAllText(log) : "";
                    File.AppendAllText(log, "[Info   : Unity Log] Unregister PlayFab server \"MyModTest\" and leaving network \"n\"\n" +
                        string.Concat(CrossplayServer.LobbyCreated.Matches(text).Select(m => $"[Info   : Unity Log] Deactivated PlayFab lobby {m.Groups["lobby"].Value}\n")));
                    return Ok("VT-STOP quit\n");
                }
                return Ok("VT-STOP stopped\n");
            }
            case "keep":
            {
                string runtime = Local(v["runtime"]), boot = Local(v["dir"]);
                var logs = v["logs"].Split('\n');
                for (int i = 0; i < logs.Length; i++)
                {
                    string source = Path.Combine(runtime, logs[i]);
                    if (File.Exists(source)) File.Move(source, Path.Combine(boot, $"game-{i}.log"));
                    else File.WriteAllText(Path.Combine(boot, $"game-{i}.log.absent"), "absent");
                }
                return Ok("VT-KEPT\n");
            }
            case "client-keep":
            {
                string dir = Local(v["dir"]);
                File.Copy(Path.Combine(Local(v["install"]), "BepInEx", "LogOutput.log"), Path.Combine(dir, "game-0.log"));
                File.WriteAllText(Path.Combine(dir, "game-1.log.absent"), "absent");
                return Ok("VT-KEPT\n");
            }
            case "move-aside":
            {
                string log = Local(v["log"]);
                if (!File.Exists(log)) return Ok("VT-NONE\n");
                Directory.CreateDirectory(Path.GetDirectoryName(Local(v["to"]))!);
                File.Move(log, Local(v["to"]));
                return Ok("VT-MOVED\n");
            }
        }
        throw new InvalidOperationException("Unexpected script on the fake host.");
    }

    public static IReadOnlyList<(string Kind, string Text)> Spec(string spec) =>
        spec.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(' ', 2)).Select(parts => (parts[0], Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])))).ToList();

    public Task<HostLock> AcquireLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default)
    {
        if (HeldBy != null) throw new HostLockException(new HostLockResult(HostLockState.HeldByOther, HeldBy, $"{lockPath} on {Name} is held by another run: {HeldBy}"));
        string claimant = owner + " [fake]";
        lock (_sync) Claims.Add(claimant);
        return Task.FromResult(new HostLock(this, lockPath, claimant, timeout));
    }
    public Task<HostLockResult> CheckLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<HostLockResult> ReleaseLockAsync(string lockPath, string owner, TimeSpan timeout, CancellationToken cancellation = default)
    {
        lock (_sync) Releases.Add(owner);
        return Task.FromResult(new HostLockResult(HostLockState.Released, null, "released"));
    }
    public Task<Shipment> ShipRevisionAsync(string repository, string revision, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<Shipment> ShipFilesAsync(string localDirectory, string hostDirectory, TimeSpan timeout, CancellationToken cancellation = default)
    {
        lock (_sync) Runs.Add(("ship", new Dictionary<string, string> { ["dest"] = hostDirectory }));
        CopyDirectory(localDirectory, Local(hostDirectory));
        File.WriteAllText(Path.Combine(Local(hostDirectory), "SOURCE.txt"), "files=world\n");
        return Task.FromResult(new Shipment(hostDirectory, new string('a', 64), 1, null, null));
    }
    public Task<long> LogOffsetAsync(string logPath, TimeSpan timeout, CancellationToken cancellation = default) => throw new NotSupportedException();
    public Task<HostLogResult> WaitForLogAsync(string logPath, long fromOffset, Regex success, IReadOnlyList<Regex>? failures, TimeSpan timeout, CancellationToken cancellation = default)
    {
        lock (_sync) Runs.Add(("follow", new Dictionary<string, string> { ["log"] = logPath, ["offset"] = fromOffset.ToString() }));
        string? line = File.Exists(Local(logPath)) ? File.ReadAllLines(Local(logPath)).FirstOrDefault(success.IsMatch) : null;
        return Task.FromResult(new HostLogResult(line != null ? HostLogOutcome.Matched : HostLogOutcome.TimedOut, "listening", line, TimeSpan.Zero, line));
    }
    public Task<FetchedDirectory> FetchDirectoryAsync(string hostDirectory, string localDirectory, TimeSpan timeout, CancellationToken cancellation = default)
    {
        lock (_sync) Runs.Add(("fetch", new Dictionary<string, string> { ["dir"] = hostDirectory, ["local"] = localDirectory }));
        if (Directory.Exists(localDirectory)) throw new InvalidOperationException("Fetch into a new local directory.");
        CopyDirectory(Local(hostDirectory), localDirectory);
        return Task.FromResult(new FetchedDirectory(localDirectory, new string('b', 64), 1, Directory.GetFiles(localDirectory, "*", SearchOption.AllDirectories).Length));
    }
    public Task<CliTunnel> OpenCliTunnelAsync(int hostPort, TimeSpan readyTimeout, int localPort = 0, CancellationToken cancellation = default)
    {
        lock (_sync) Runs.Add(("tunnel", new Dictionary<string, string> { ["port"] = hostPort.ToString() }));
        if (TunnelFailure != null) throw TunnelFailure;
        var forward = new FakeForward(["-L", $"127.0.0.1:{TunnelPort}:127.0.0.1:{hostPort}"], listen: false);
        Tunnels.Add(forward);
        return Task.FromResult(new CliTunnel(forward, TunnelPort, hostPort));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}

// PinnedServerRun --profile: the dedicated server on the profile's server host, against a fake host (no shell, no game).
public sealed class HostedServerRunTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hosted-run-").FullName;
    private const string Install = "/opt/valheim/server", Runs = "/srv/vt/runs", RunId = "run-test";
    private const string RunDirectory = Runs + "/" + RunId;
    private string Mirror => Path.Combine(_root, "host");
    private string Output => Path.Combine(_root, "out");
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private FakeServerHost NewHost(FakeOwnedServer? server = null) => new("linux-box", Mirror, server);
    private FakeOwnedServer NewServer() => new("test.mod", saveRoot: RunDirectory + "/world");

    // The host's install (the runtime the plan pins) and a local world; returns the plan and the profile.
    private (string Plan, string Profile) Write(FakeServerHost host, int planPort = 5577, string hostPlatform = "linux", string hostShell = "bash", bool withClient = false, bool unpinned = false,
        bool crossplay = false, string portOption = "-port", string gamePort = "2456")
    {
        string install = host.Local(Install);
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, ServerLaunch.LinuxExecutable), "server");
        FakeInstalls.Server(install);
        File.WriteAllText(Path.Combine(install, "BepInEx", "core", "BepInEx.Preloader.dll"), "preloader");
        string world = Path.Combine(_root, "world");
        Directory.CreateDirectory(Path.Combine(world, "worlds_local"));
        File.WriteAllText(Path.Combine(world, "worlds_local", "Test.db"), "world");
        var plan = new Dictionary<string, object>
        {
            ["scenario"] = "smoke",
            ["runtime"] = new { source = install, sha256 = unpinned ? new Dictionary<string, string>() : WorldFixture.Manifest(install) },
            ["world"] = new { source = world, sha256 = WorldFixture.Manifest(world) },
            ["arguments"] = new[] { "-batchmode", "-nographics", "-savedir", "{world}", portOption, gamePort, "-logFile", "{runtime}/toolkit-unity.log" },
            ["pins"] = unpinned ? new Dictionary<string, string>() : new Dictionary<string, string> { ["worlduid"] = "1" },
            ["port"] = planPort,
        };
        if (crossplay) plan["crossplay"] = true;
        if (unpinned) plan["pinning"] = "none"; else plan["runtimePins"] = InstallPins.Of(install);
        string planPath = Path.Combine(_root, "plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
        var hosts = new Dictionary<string, object>
        {
            ["linux-box"] = new { kind = "ssh", platform = hostPlatform, shell = hostShell, destination = "tester@linux-box.example", @lock = hostPlatform == "windows" ? @"C:\vt\lock" : "/var/tmp/vt/lock" },
        };
        var clients = new Dictionary<string, object>();
        if (withClient)
        {
            hosts["linux-gpu"] = new { kind = "ssh", platform = "linux", shell = "bash", destination = "tester@linux-gpu.example", @lock = "/home/tester/lock" };
            clients["player"] = new { host = "linux-gpu", install = "/home/tester/valheim", runtime = "/home/tester/runs", cliPort = 5578 };
        }
        var server = hostPlatform == "windows"
            ? new { host = "linux-box", install = @"C:\valheim\server", runtime = @"C:\vt\runs", cliPort = 5577, gamePort = 2456 }
            : new { host = "linux-box", install = Install, runtime = Runs, cliPort = 5577, gamePort = 2456 };
        string profilePath = Path.Combine(_root, "environment.json");
        File.WriteAllText(profilePath, JsonSerializer.Serialize(new { hosts, server, clients }));
        return (planPath, profilePath);
    }

    private static PinnedServerRunOptions<ServerRunPlan> Options(FakeServerHost host, FakeOwnedServer? server, Func<PinnedServerRunContext<ServerRunPlan>, Task>? scenario = null,
        FakeServerHost? clientHost = null, IGameTransport? clientTransport = null) => new()
    {
        Name = "toolkit-smoke",
        ReadPlan = path => { var plan = ServerRunPlan.Read<ServerRunPlan>(path); plan.ValidateServerPlan([], "TEST_SESSION_TOKEN"); return plan; },
        SessionCapability = "test.mod/session", SessionTokenVariable = "TEST_SESSION_TOKEN", EnableDevcommands = false,
        Scenario = scenario ?? (_ => Task.CompletedTask),
        HostSeams = new HostedSeams
        {
            Host = name => name == "linux-box" ? host : clientHost ?? throw new InvalidOperationException("No fake host " + name),
            Connect = port => port == 15578 ? clientTransport! : server!.Connect(),
            StateWaits = false, RunId = RunId,
        },
    };
    private JsonElement Result() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "result.json"))).RootElement;
    private IReadOnlyList<string?> StepNames() => Result().GetProperty("Steps").EnumerateArray().Select(step => step.GetProperty("Name").GetString()).ToList();
    private JsonElement Step(string name) => Result().GetProperty("Steps").EnumerateArray().Single(step => step.GetProperty("Name").GetString() == name);

    [Fact] public async Task ARemoteRunCopiesAndVerifiesOnTheHostReachesTheCliThroughTheTunnelAndStopsOnlyItsServer()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host);
        IGameHost? seenHost = null; string? seenRuntime = null;
        int code = await PinnedServerRun.MainAsync(["--profile", profile, "run", plan, Output], Options(host, server, run =>
        {
            seenHost = run.ServerHost; seenRuntime = run.RuntimeDirectory;
            run.Session.Restart(); // A second boot: its own boot directory and log, and a stop of only the first.
            return Task.CompletedTask;
        }));
        Assert.Equal(0, code);
        Assert.Same(host, seenHost); Assert.Equal(RunDirectory + "/runtime", seenRuntime);
        Assert.Equal(new[] { "take the server host's lock", "copy and verify pinned runtime on the server host", "copy and verify pinned world", "ship and verify the world copy on the server host",
                "copied runtime has the plan's server executable", "copied runtime's BepInEx patchers are the plan's", "copied runtime is the pinned game build, BepInEx core and patchers",
                "CLI port is free on the server host", "open the loopback CLI tunnel to the server host", "start and verify owned dedicated fixture", "stop only owned server",
                "fetch the server host's world copy", "close the CLI tunnel", "release the server host's lock", "scan run logs" }, StepNames());

        // The copy is the host's install, into this run's own directory; the world copy is shipped there.
        var copy = Assert.Single(host.Runs, run => run.Script == "copy");
        Assert.Equal(Install, copy.Variables["source"]); Assert.Equal(RunDirectory + "/runtime", copy.Variables["dest"]);
        Assert.Equal(RunDirectory + "/world", Assert.Single(host.Runs, run => run.Script == "ship").Variables["dest"]);
        // Each boot starts with the plan's arguments on the host's paths, its own boot directory and the logs moved aside.
        var starts = host.Runs.Where(run => run.Script == "start").ToList();
        Assert.Equal(new[] { RunDirectory + "/boot-1", RunDirectory + "/boot-2" }, starts.Select(start => start.Variables["dir"]));
        Assert.Equal("BepInEx/LogOutput.log\ntoolkit-unity.log", starts[0].Variables["logs"]);
        var spec = FakeServerHost.Spec(starts[0].Variables["spec"]);
        Assert.Equal(new[] { "-batchmode", "-nographics", "-savedir", RunDirectory + "/world", "-port", "2456", "-logFile", RunDirectory + "/runtime/toolkit-unity.log" },
            spec.Where(line => line.Kind == "arg").Select(line => line.Text));
        Assert.Contains(("env", "DOORSTOP_TARGET_ASSEMBLY=" + RunDirectory + "/runtime/BepInEx/core/BepInEx.Preloader.dll"), spec);
        Assert.Contains(("prepend", "LD_LIBRARY_PATH=" + RunDirectory + "/runtime/linux64:" + RunDirectory + "/runtime/doorstop_libs"), spec);
        // The listening line is awaited in this boot's log on the host, from its start.
        Assert.All(host.Runs.Where(run => run.Script == "follow"), run => { Assert.Equal(RunDirectory + "/runtime/BepInEx/LogOutput.log", run.Variables["log"]); Assert.Equal("0", run.Variables["offset"]); });
        // Only the two processes this run started were stopped, each by its own identity, and each boot's evidence came back.
        Assert.Equal(new[] { ("1", "9001"), ("2", "9002") }, host.Stops);
        Assert.Equal(new[] { "launch1", "stop1", "launch2", "stop2" }, server.Events.Where(e => e.StartsWith("launch") || e.StartsWith("stop")));
        Assert.Contains("fake boot 1", File.ReadAllText(Path.Combine(Output, "boot-1", "game-0.log")));
        Assert.Contains("fake boot 2", File.ReadAllText(Path.Combine(Output, "boot-2", "game-0.log")));
        Assert.True(File.Exists(Path.Combine(Output, "host-world", "worlds_local", "Test.db")));
        var process = JsonDocument.Parse(File.ReadAllText(Path.Combine(Output, "boot-1.process.json"))).RootElement;
        Assert.Equal(1, process.GetProperty("pid").GetInt32()); Assert.Equal("9001", process.GetProperty("startIdentity").GetString());
        Assert.Equal(RunDirectory + "/boot-1", process.GetProperty("bootDirectory").GetString());
        // The tunnel closed, the lock was taken and released once, by the same claimant.
        Assert.True(Assert.Single(host.Tunnels).Stopped);
        Assert.Equal(host.Claims, host.Releases); Assert.StartsWith("toolkit-smoke run-test", Assert.Single(host.Claims));
        var provenance = Result().GetProperty("Provenance");
        Assert.Equal("linux-box", provenance.GetProperty("serverHost").GetString());
        Assert.Equal(RunDirectory, provenance.GetProperty("hostRunDirectory").GetString());
        Assert.Equal("linux-box:" + Install, provenance.GetProperty("runtimeSource").GetString());
        Assert.Equal(InstallPins.Of(host.Local(Install)).Game, provenance.GetProperty("runtimeGameSha256").GetString());
        Assert.Equal("1,2", provenance.GetProperty("ownedPids").GetString());
        Assert.False(provenance.TryGetProperty("outcome", out _));
    }

    // The remote launch is the plan's launch arguments: a crossplay plan's server starts with -crossplay, any other without it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACrossplayPlansServerStartsOnTheHostWithCrossplay(bool crossplay)
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host, crossplay: crossplay);
        Assert.Equal(0, await PinnedServerRun.MainAsync(["--profile", profile, "run", plan, Output], Options(host, server)));
        var arguments = FakeServerHost.Spec(Assert.Single(host.Runs, run => run.Script == "start").Variables["spec"])
            .Where(line => line.Kind == "arg").Select(line => line.Text).ToList();
        if (crossplay) Assert.Equal("-crossplay", arguments.Last()); else Assert.DoesNotContain("-crossplay", arguments);
        Assert.Equal(crossplay ? "true" : "false", Result().GetProperty("Provenance").GetProperty("crossplay").GetString());
    }

    [Fact] public async Task ACrossplayRunWhoseServerHadToBeKilledFailsAndSaysWhy()
    {
        var server = NewServer(); var host = NewHost(server);
        host.IgnoreQuit = true;
        var (plan, profile) = Write(host, crossplay: true);
        Assert.Equal(1, await PinnedServerRun.MainAsync(["--profile", profile, "run", plan, Output], Options(host, server)));
        var step = Step("every boot quit cleanly and retired its crossplay lobby");
        Assert.False(step.GetProperty("Passed").GetBoolean());
        Assert.Contains("boot-1 was killed", step.GetProperty("Error").GetString());
        Assert.Contains("lobby-1", step.GetProperty("Error").GetString());
        Assert.StartsWith("boot-1 killed", Result().GetProperty("Provenance").GetProperty("serverStops").GetString());
        Assert.Equal("120", Assert.Single(host.Runs, run => run.Script == "stop").Variables["quit"]);
    }

    [Fact] public async Task ACleanCrossplayRunRecordsEachBootsRetiredLobby()
    {
        var server = NewServer(); var host = NewHost(server);
        var (plan, profile) = Write(host, crossplay: true);
        Assert.Equal(0, await PinnedServerRun.MainAsync(["--profile", profile, "run", plan, Output], Options(host, server)));
        Assert.True(Step("every boot quit cleanly and retired its crossplay lobby").GetProperty("Passed").GetBoolean());
        var provenance = Result().GetProperty("Provenance");
        Assert.StartsWith("boot-1 clean", provenance.GetProperty("serverStops").GetString());
        Assert.Contains("lobbies created lobby-1; deactivated lobby-1", provenance.GetProperty("crossplayLobbies").GetString());
    }

    // The game reads its arguments lowercased: -Port names the game port, and one that differs from the profile's is refused.
    [Fact] public async Task AGamePortInAnyCaseMustBeTheProfilesGamePort()
    {
        var host = NewHost();
        var (plan, profile) = Write(host, portOption: "-Port", gamePort: "2457");
        Assert.Equal(1, await PinnedServerRun.MainAsync(["--profile", profile, "run", plan, Output], Options(host, null)));
        Assert.False(Directory.Exists(Output)); Assert.Empty(host.Runs);
    }

    [Fact] public async Task ValidateOnAHostCopiesAndVerifiesThereAndStartsNothing()
    {
        var host = NewHost();
        var (plan, profile) = Write(host);
        Assert.Equal(0, await PinnedServerRun.MainAsync(["--profile", profile, "validate", plan, Output], Options(host, null)));
        Assert.Contains("prepared only; no game launched", StepNames());
        Assert.DoesNotContain(host.Runs, run => run.Script is "port" or "tunnel" or "start" or "fetch");
        Assert.Equal(host.Claims, host.Releases); Assert.Single(host.Claims);
        Assert.True(Directory.Exists(host.Local(RunDirectory + "/runtime")));
    }

    [Fact] public async Task ARuntimeCopyThatDiffersOnTheHostFailsBeforeAnythingStarts()
    {
        var host = NewHost(NewServer());
        var (plan, profile) = Write(host);
        host.AfterCopy = runtime => File.WriteAllText(Path.Combine(runtime, "BepInEx", "core", "BepInEx.dll"), "another bepinex");
        Assert.Equal(1, await PinnedServerRun.MainAsync(["--profile", profile, "run", plan, Output], Options(host, NewServer())));
        var copy = Step("copy and verify pinned runtime on the server host");
        Assert.False(copy.GetProperty("Passed").GetBoolean());
        Assert.Contains("different hash: BepInEx/core/BepInEx.dll", copy.GetProperty("Error").GetString());
        Assert.DoesNotContain(host.Runs, run => run.Script is "start" or "tunnel");
        Assert.Equal(host.Claims, host.Releases);
    }

    [Fact] public async Task AnotherRunsLockRefusesTheRunBeforeTheHostIsTouched()
    {
        var host = NewHost(NewServer()); host.HeldBy = "other-runner run-x [0123]";
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(["--profile", profile, "run", plan, Output], Options(host, NewServer())));
        var step = Step("take the server host's lock");
        Assert.False(step.GetProperty("Passed").GetBoolean()); Assert.Contains("other-runner run-x [0123]", step.GetProperty("Error").GetString());
        Assert.Empty(host.Runs);
        Assert.Empty(host.Releases);
        Assert.DoesNotContain("release the server host's lock", StepNames());
    }

    [Fact] public async Task ALostStartReplyIsAnUnknownOutcomeAndKeepsTheLock()
    {
        var server = NewServer(); var host = NewHost(server);
        host.Failures["start"] = FakeServerHost.TransportFailure;
        var (plan, profile) = Write(host);
        Assert.Equal(3, await PinnedServerRun.MainAsync(["--profile", profile, "run", plan, Output], Options(host, server)));
        var result = Result();
        Assert.False(result.GetProperty("Passed").GetBoolean());
        Assert.StartsWith("unknown: ", result.GetProperty("Provenance").GetProperty("outcome").GetString());
        // A server may be running there that no session knows: nothing is stopped by guesswork, and the host stays locked.
        Assert.Empty(host.Stops);
        Assert.Empty(host.Releases);
        Assert.Contains("may still run", Step("release the server host's lock").GetProperty("Error").GetString());
        Assert.True(Assert.Single(host.Tunnels).Stopped);
    }

    [Fact] public async Task AnUnprovenStopIsAnUnknownOutcomeAndKeepsTheLock()
    {
        var server = NewServer(); var host = NewHost(server);
        host.Failures["stop"] = new HostResult(HostOutcome.Unknown, null, "", "", TimeSpan.FromSeconds(45), true);
        var (plan, profile) = Write(host);
        Assert.Equal(3, await PinnedServerRun.MainAsync(["--profile", profile, "run", plan, Output], Options(host, server)));
        Assert.False(Step("stop only owned server").GetProperty("Passed").GetBoolean());
        Assert.Empty(host.Releases);
        Assert.DoesNotContain("fetch the server host's world copy", StepNames());
        Assert.StartsWith("unknown: ", Result().GetProperty("Provenance").GetProperty("outcome").GetString());
    }

    [Fact] public async Task AFailingScenarioIsAFailureEvenWhenTeardownIsUnknown()
    {
        var server = NewServer(); var host = NewHost(server);
        host.Failures["stop"] = FakeServerHost.TransportFailure;
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(["--profile", profile, "run", plan, Output], Options(host, server, _ => throw new InvalidOperationException("marker missing"))));
        Assert.False(Result().GetProperty("Provenance").TryGetProperty("outcome", out _));
    }

    [Fact] public async Task ATunnelThatCannotOpenFailsTheRunBeforeTheServerStarts()
    {
        var server = NewServer(); var host = NewHost(server);
        host.TunnelFailure = new WaitFailedException("the forward to listen on 127.0.0.1:15577", "ssh exited with code 255", TimeSpan.FromSeconds(1), "bind [127.0.0.1]:15577: Address already in use");
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(["--profile", profile, "run", plan, Output], Options(host, server)));
        Assert.False(Step("open the loopback CLI tunnel to the server host").GetProperty("Passed").GetBoolean());
        Assert.DoesNotContain(host.Runs, run => run.Script == "start");
        Assert.Empty(server.Events);
        Assert.Equal(host.Claims, host.Releases);
    }

    [Fact] public async Task ABusyCliPortOnTheHostIsRefusedBeforeTheTunnel()
    {
        var host = NewHost(NewServer()); host.PortBusy = true;
        var (plan, profile) = Write(host);
        Assert.Equal(1, await PinnedServerRun.MainAsync(["--profile", profile, "run", plan, Output], Options(host, NewServer())));
        Assert.Contains("already listens on port 5577", Step("CLI port is free on the server host").GetProperty("Error").GetString());
        Assert.DoesNotContain(host.Runs, run => run.Script is "tunnel" or "start");
    }

    [Fact] public async Task AProfileThatDoesNotFitThePlanIsRefusedBeforeAnythingIsWritten()
    {
        var host = NewHost();
        var (plan, profile) = Write(host, planPort: 5590);
        Assert.Equal(1, await PinnedServerRun.MainAsync(["--profile", profile, "run", plan, Output], Options(host, null)));
        Assert.False(Directory.Exists(Output)); Assert.Empty(host.Runs);
        (plan, profile) = Write(host, hostPlatform: "windows", hostShell: "powershell");
        Assert.Equal(1, await PinnedServerRun.MainAsync(["--profile", profile, "run", plan, Output], Options(host, null)));
        Assert.False(Directory.Exists(Output)); Assert.Empty(host.Runs);
        Assert.Equal(2, await PinnedServerRun.MainAsync(["--profile", profile], Options(host, null)));
    }

    [Fact] public async Task AProfileClientStartsInItsHostsDesktopSessionAndStopsOnlyThatClient()
    {
        var server = NewServer(); var host = NewHost(server);
        var clientHost = new FakeServerHost("linux-gpu", Path.Combine(_root, "gpu"), tunnelPort: 15578);
        string clientInstall = clientHost.Local("/home/tester/valheim");
        Directory.CreateDirectory(Path.Combine(clientInstall, "BepInEx", "core"));
        FakeInstalls.Client(clientInstall);
        File.WriteAllText(Path.Combine(clientInstall, ClientLaunch.LinuxExecutable), "client");
        File.WriteAllText(Path.Combine(clientInstall, "BepInEx", "LogOutput.log"), "an earlier run's log\n");
        var (plan, profile) = Write(host, withClient: true);
        var clientTransport = new ScriptedTransport();
        var client = new ClientRunPlan { Mode = "owned", Install = _root, Port = 5578, Pinning = "none", StartSeconds = 30, LaunchArguments = ["+connect", "linux-box:2456"] };
        int? pid = null;
        int code = await PinnedServerRun.MainAsync(["--profile", profile, "run", plan, Output], Options(host, server, run =>
        {
            using (var session = run.OpenClient(client)) pid = session.ProcessId;
            return Task.CompletedTask;
        }, clientHost, clientTransport));
        Assert.Equal(0, code);
        Assert.Equal(77, pid);
        // The launch used the profile's install and a launch directory in this run's directory on the client's host.
        var start = Assert.Single(clientHost.Runs, run => run.Script == "client-start");
        Assert.Equal("/home/tester/valheim", start.Variables["install"]);
        Assert.Equal("/home/tester/runs/" + RunId + "/client-1", start.Variables["dir"]);
        Assert.Contains(FakeServerHost.Spec(start.Variables["spec"]), line => line.Kind == "arg" && line.Text == "+connect");
        // The earlier log moved aside first, the new one was awaited from its start; only that client was stopped.
        Assert.Equal("/home/tester/runs/" + RunId + "/client-1.previous-LogOutput.log", Assert.Single(clientHost.Runs, run => run.Script == "move-aside").Variables["to"]);
        Assert.Equal("0", Assert.Single(clientHost.Runs, run => run.Script == "follow").Variables["offset"]);
        Assert.Equal(new[] { ("77", "555") }, clientHost.Stops);
        Assert.Contains("listening on 127.0.0.1:5578", File.ReadAllText(Path.Combine(Output, "client-1", "game-0.log")));
        Assert.True(Assert.Single(clientHost.Tunnels).Stopped);
        // The client's host was locked for the run and released at teardown; its logs were scanned with the server's.
        Assert.Equal(clientHost.Claims, clientHost.Releases); Assert.Single(clientHost.Claims);
        Assert.Contains("release client host linux-gpu's lock", StepNames());
        Assert.Contains(Result().GetProperty("Logs").EnumerateArray(), log => log.GetProperty("Role").GetString() == "client-1 BepInEx log");
    }
}
