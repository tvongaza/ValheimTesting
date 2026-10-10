using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The parts of a <see cref="PinnedServerRun"/> that differ when its dedicated server runs on the environment's server
/// host (<c>--inventory</c> or a campaign): the host lock, the runtime copied from the host's install and verified there, the world copy
/// shipped and verified there, the port check, the loopback CLI tunnel, the owned server's boots through <see cref="HostServer"/> (its <see cref="ServerActor"/>'s placement),
/// remote clients through <see cref="InteractiveClient"/> and a local macOS GUI client through <see cref="ClientSession"/>,
/// each client's Steam identity lease in a campaign, and the teardown
/// that fetches evidence, closes the tunnel and releases the leases and locks.
/// </summary>
internal sealed class HostedServerRun : IServerPlacement
{
    internal const string BepInExLog = "BepInEx/LogOutput.log", UnityLog = "toolkit-unity.log";
    private readonly IHostedRunHooks _hooks;
    private readonly string _owner;
    // The campaign's clients, their leases and their hosts' locks: owned by CampaignClients, as in a campaign without a server.
    private readonly CampaignClients _clientActors;
    private HostLock? _lock;
    private CliTunnel? _tunnel;
    private HostListing? _runtime;
    private HostListing? _loader;
    private InstallPins? _preparedPins;
    private HostedWorld? _macWorld;
    private MacServerLists? _macLists;
    private bool _serverMayRun;
    // A standalone run's own copies on the server host, journalled before their scripts run (#257): from then on teardown
    // removes them, even a partial one, or leaves them open in the journal for env recover.
    private bool _runtimeIntended;
    private WorldCopy _world;
    private enum WorldCopy { None, Intended, Shipped, Verified }

    private HostedServerRun(ResolvedEnvironment profile, GameRole role, HostProfile hostProfile, IGameHost host, string runId, string runner, IHostedRunHooks hooks,
        string? preparedRuntime)
    {
        Profile = profile; Role = role; HostProfile = hostProfile; Host = host; RunId = runId; _hooks = hooks; _owner = runner + " " + runId;
        _journal = new RunJournal(runId);
        // A campaign prepared the server's disposable install already (<runtime>/vt-prep-<id>-server/runtime): that is the one
        // copy the server runs from. A standalone run makes its own under <runtime>/<runId>.
        Prepared = preparedRuntime != null;
        RunDirectory = HostPath.Join(role.Runtime, runId);
        RuntimeDirectory = preparedRuntime ?? HostPath.Join(RunDirectory, "runtime");
        WorldDirectory = LocalMac ? HostedWorld.DefaultSaveDirectory(ClientPlatform.MacOS) : HostPath.Join(RunDirectory, "world");
        _clientActors = new CampaignClients(profile, runId, _owner, hooks, (role.Host, host), JournalAsync);
    }

    // The run's journal on each host it touches (RunJournal): a process is journalled before it starts, a lock once held.
    private readonly RunJournal _journal;
    private async Task JournalAsync(IGameHost host, string hostName, string actor, JournalEntry entry, CancellationToken cancellation)
    {
        await _journal.AppendAsync(host, RunJournal.DirectoryFor(Profile.Hosts[hostName]), actor, entry, HostedTimeouts.Quick, cancellation).ConfigureAwait(false);
        if (hostName == Role.Host) _serverJournalled = true;
    }
    private bool _serverJournalled;
    // After its effect a lost line is a warning: the effect happened, and the entry before it already names where to look.
    private async Task NoteAsync(IGameHost host, string hostName, string actor, JournalEntry entry)
    {
        try { await JournalAsync(host, hostName, actor, entry, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { Console.Error.WriteLine($"Warning: could not journal {entry.Kind} for {actor} on {hostName}: {error.Message}"); }
    }
    private Task NoteLockAsync(IGameHost host, string hostName, HostLock held, string kind) =>
        NoteAsync(host, hostName, "run", JournalEntry.Of(kind, ("lock", held.Path), ("claimant", held.Owner)));

    /// <summary>
    /// The run's end in its server host's journal, when the run wrote there at all (a standalone run; a campaign's preparation
    /// journals its own hosts). A run refused before it touched the host leaves it untouched.
    /// </summary>
    public Task JournalEndAsync(string state, bool cleanupVerified) =>
        JournalEndAsync(JournalEntry.Of(JournalEntry.RunEnded, ("state", state), ("cleanupVerified", cleanupVerified ? "true" : "false")));
    /// <summary>The run's last journal entry on its server host: its end, or that its cleanup was abandoned.</summary>
    internal Task JournalEndAsync(JournalEntry last) => !_serverJournalled ? Task.CompletedTask : NoteAsync(Host, Role.Host, "run", last);

    /// <summary>Whether the runtime is a campaign's prepared install (verified in place) rather than a copy this run makes.</summary>
    public bool Prepared { get; }

    public ResolvedEnvironment Profile { get; }
    public GameRole Role { get; }
    public HostProfile HostProfile { get; }
    private bool LocalMac => HostProfile is { Kind: "local", Platform: "macos" };
    public IGameHost Host { get; }
    public string RunId { get; }
    /// <summary>
    /// This run's directory on the server host: <c>runtime</c>, <c>world</c> and <c>boot-N</c>. The world and boot logs stay
    /// (both are fetched too); the runtime copy goes at teardown once the server has stopped (<see cref="TeardownAsync"/>).
    /// </summary>
    public string RunDirectory { get; }
    public string RuntimeDirectory { get; }
    // The owned runtime remains the log and retirement root. A Unix profile starts the executable
    // from this read-only game root while Doorstop and BepInEx remain under RuntimeDirectory.
    public string GameDirectory => Role.PreparedGameRoot ?? RuntimeDirectory;
    public string LoaderDirectory => RuntimeDirectory;
    public string WorldDirectory { get; }
    /// <summary>The runtime copy's files on the host, once copied.</summary>
    public IReadOnlyDictionary<string, string> RuntimeHashes => _runtime?.Files ?? new Dictionary<string, string>();

    /// <summary>Refuses an environment and plan that cannot run a server on the environment's server host, before anything is touched.</summary>
    public static HostedServerRun Create(ResolvedEnvironment profile, ServerRunPlan plan, string runner, IHostedRunHooks? hooks = null) =>
        Create(profile, plan, runner, hooks, prepared: false);

    /// <summary>With <paramref name="prepared"/>, the server role's install is a campaign's prepared disposable install: the run uses it as its runtime, under the campaign's run id.</summary>
    internal static HostedServerRun Create(ResolvedEnvironment profile, ServerRunPlan plan, string runner, IHostedRunHooks? hooks, bool prepared, string? campaignRunId = null)
    {
        var role = profile.Server ?? throw new ArgumentException("The environment places no dedicated server.");
        var hostProfile = profile.Hosts[role.Host];
        if (Refusal(hostProfile, role, plan) is { } refusal) throw new ArgumentException($"The server environment on host '{role.Host}': {refusal}");
        hooks ??= HostedRunHooks.Production;
        var host = hooks.CreateHost(profile, role.Host);
        return new HostedServerRun(profile, role, hostProfile, host, hooks.RunId(campaignRunId), runner, hooks, prepared ? role.Install : null);
    }

    /// <summary>Why a server environment cannot run <paramref name="plan"/>, or null: its host's platform and shell, and the plan's ports.</summary>
    internal static string? Refusal(HostProfile hostProfile, GameRole role, ServerRunPlan plan)
    {
        if (!((hostProfile.Platform == "linux" && HostShell.Parse(hostProfile.Shell).Kind == HostShellKind.Bash) ||
              (hostProfile.Platform == "macos" && hostProfile.Kind == "local" && HostShell.Parse(hostProfile.Shell).Kind == HostShellKind.Bash) ||
              (hostProfile.Platform == "windows" && HostShell.Parse(hostProfile.Shell).Kind == HostShellKind.PowerShell)))
            return $"it is {hostProfile.Platform} with {hostProfile.Shell}; a hosted dedicated server needs Linux/bash, local macOS/bash or Windows/PowerShell.";
        if (hostProfile.Kind == "local" && hostProfile.Platform != HostProfile.CurrentPlatform)
            return $"it is a local {hostProfile.Platform} host, but this machine is {HostProfile.CurrentPlatform}.";
        // The plan's runtime names its platform by its server executable; a pinned manifest lists it.
        string? planned = plan.Executable == GameLaunch.ServerWindowsExecutable || plan.Runtime.Sha256.ContainsKey(GameLaunch.ServerWindowsExecutable) ? "windows"
            : plan.Executable == GameLaunch.ServerLinuxExecutable || plan.Runtime.Sha256.ContainsKey(GameLaunch.ServerLinuxExecutable) ? "linux"
            : plan.Executable == GameLaunch.ServerMacExecutable || plan.Runtime.Sha256.ContainsKey(GameLaunch.ServerMacExecutable) ? "macos" : null;
        if (planned != null && planned != hostProfile.Platform)
            return $"the plan's runtime is a {planned} server, but the host is {hostProfile.Platform}.";
        if (hostProfile.Platform == "macos" && plan.Crossplay)
            return "macOS crossplay's native library has no hosted preflight yet; omit -crossplay for a local smoke.";
        if (role.CliPort != plan.Port)
            return $"the plan's ValheimCLI port {plan.Port} is not its cliPort {role.CliPort}; the runtime's [Server] Port must be both.";
        // The game reads its arguments lowercased, so -Port names the game port too.
        int at = Array.FindIndex(plan.Arguments, argument => argument.Equals("-port", StringComparison.OrdinalIgnoreCase));
        if (at >= 0 && at + 1 < plan.Arguments.Length && int.TryParse(plan.Expand(plan.Arguments[at + 1], "", ""), NumberStyles.None, CultureInfo.InvariantCulture, out int gamePort) && gamePort != role.GamePort)
            return $"the plan's -port {gamePort} is not its gamePort {role.GamePort}.";
        return null;
    }

    public void Record(IDictionary<string, string> provenance)
    {
        provenance["serverHost"] = Role.Host;
        provenance["serverHostKind"] = Host.Kind.ToString();
        provenance["hostRunDirectory"] = RunDirectory;
        provenance["runtimeSource"] = Role.Host + ":" + (Role.PreparedSourceRoot ?? Role.Install);
    }

    /// <summary>Takes the server host's lock, then copies the host's install into this run's runtime and verifies every file there.</summary>
    public async Task LockAndCopyRuntimeAsync(ScenarioReport report, ServerRunPlan plan, bool pinned, CancellationToken cancellation)
    {
        _preparedPins = Role.PreparedSourceRoot != null ? plan.RuntimePins : null;
        // A Windows host that can register no server task is refused before the copy (a campaign's preflight asked already).
        if (Host.Shell.Kind == HostShellKind.PowerShell)
            await report.StepAsync(StepPhase.Setup, "the server host can start a server task", () => HostServer.RequireTaskLogonAsync(Host, HostedTimeouts.Quick, cancellation)).ConfigureAwait(false);
        await report.StepAsync(StepPhase.Setup, "take the server host's lock", async () => _lock = await Host.AcquireLockAsync(HostProfile.Lock, _owner, HostedTimeouts.Quick, cancellation).ConfigureAwait(false)).ConfigureAwait(false);
        await NoteLockAsync(Host, Role.Host, _lock!, JournalEntry.LockHeld).ConfigureAwait(false);
        if (LocalMac)
            await report.StepAsync(StepPhase.Setup, "preserve the Mac server's user-level access lists", async () =>
            {
                _macLists = MacServerLists.Capture(WorldDirectory, Path.Combine(RunDirectory, "server-lists"));
                await JournalAsync(Host, Role.Host, "server", _macLists.Captured(), cancellation).ConfigureAwait(false);
                _macLists.Isolate();
            }).ConfigureAwait(false);
        // Only an unpinned plan may leave out the manifest; the copy is then recorded as found.
        bool verified = pinned || plan.Runtime.Sha256.Count != 0;
        if (Prepared)
        {
            // The campaign's preparation made the one copy; it must still be exactly what the preparation listed and bound.
            await report.StepAsync(StepPhase.Setup, verified ? "verify the prepared runtime on the server host" : "list the prepared runtime on the server host as found", async () =>
            {
                _runtime = await HostInstall.ListAsync(Host, GameDirectory, HostedTimeouts.Long, null, cancellation).ConfigureAwait(false);
                if (verified) HostInstall.RequireSame(plan.Runtime.Sha256, _runtime, "prepared runtime");
                _loader = GameDirectory == RuntimeDirectory ? _runtime :
                    await HostInstall.ListAsync(Host, RuntimeDirectory, HostedTimeouts.Long, null, cancellation).ConfigureAwait(false);
            }).ConfigureAwait(false);
            return;
        }
        await report.StepAsync(StepPhase.Setup, verified ? "copy and verify pinned runtime on the server host" : "copy unpinned runtime on the server host as found", async () =>
        {
            // Journalled before the copy, as a campaign's preparation does: a copy the journal cannot name is never made, and an
            // interrupted or partial one is env status's and env recover's to find.
            await JournalAsync(Host, Role.Host, "server", CopyIntended(RuntimeDirectory, "runtime"), cancellation).ConfigureAwait(false);
            _runtimeIntended = true;
            // Steam's own runtime output in the install (logs/) is not the runtime's: the copy leaves it out, and the pins never count it.
            await HostInstall.CopyAsync(Host, Role.Install, RuntimeDirectory, HostedTimeouts.Long, HostInstall.ServerRuntimeSkips, cancellation).ConfigureAwait(false);
            _runtime = await HostInstall.ListAsync(Host, RuntimeDirectory, HostedTimeouts.Long, null, cancellation).ConfigureAwait(false);
            _loader = _runtime;
            if (verified) HostInstall.RequireSame(HostInstall.WithoutSkipped(plan.Runtime.Sha256, HostInstall.ServerRuntimeSkips, _runtime.Names), _runtime, "runtime copy");
            await JournalAsync(Host, Role.Host, "server", CopyDone(RuntimeDirectory, _runtime), cancellation).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>For a crossplay plan: the runtime copy's <c>libparty.so</c> loads on the server host (<see cref="CrossplayLibraries"/>).</summary>
    public Task CheckCrossplayAsync(ScenarioReport report, ServerRunPlan plan, CancellationToken cancellation)
    {
        if (!plan.Crossplay) return Task.CompletedTask;
        if (Host.Shell.Kind == HostShellKind.PowerShell)
        {
            report.Step(StepPhase.Setup, "the Windows runtime has crossplay's native library", () =>
            {
                const string party = "valheim_server_Data/Plugins/x86_64/Party.dll";
                if (!(_runtime?.Files.ContainsKey(party) ?? false))
                    throw new FileNotFoundException($"The copied Windows server on {Host.Name} has no {party}; crossplay cannot start.");
                report.Provenance["crossplayLibraries"] = party + " present on " + Host.Name + "; game startup verifies it loads";
            });
            return Task.CompletedTask;
        }
        return report.StepAsync(StepPhase.Setup, "the server host can load crossplay's libraries", async () =>
            report.Provenance["crossplayLibraries"] = await CrossplayLibraries.RequireAsync(Host, GameDirectory, HostedTimeouts.Quick, cancellation).ConfigureAwait(false) + " loads on " + Host.Name);
    }

    /// <summary>Ships the verified local world copy to the host and verifies every file there.</summary>
    public Task ShipWorldAsync(ScenarioReport report, string localWorld, string output, CancellationToken cancellation) =>
        LocalMac ? report.StepAsync(StepPhase.Setup, "place the pinned world in the Mac's default worlds", () =>
        {
            // A campaign's prepared server save root contains worlds_local; HostedWorldOnHost takes the
            // contents of that folder, with the named world at its root.
            string sourceWorlds = Directory.Exists(Path.Combine(localWorld, "worlds_local"))
                ? Path.Combine(localWorld, "worlds_local") : localWorld;
            var manifest = WorldFixture.Manifest(sourceWorlds);
            var identity = WorldIdentity.Read(sourceWorlds);
            var fixture = new HostWorldPlan
            {
                World = new PinnedDirectory { Source = sourceWorlds, Sha256 = new Dictionary<string, string>(manifest, StringComparer.Ordinal) },
                WorldUid = identity.UidText,
            };
            var site = new HostedWorldOnHost.Site(Host, Role.Host, HostPath.Join(WorldDirectory, "worlds_local"),
                HostPath.Join(RunDirectory, "world-stage"), HostPath.Join(RunDirectory, "host-world"),
                (entry, token) => JournalAsync(Host, Role.Host, "server", entry, token),
                _ => _lock != null ? Task.CompletedTask : throw new InvalidOperationException("Take the Mac server host's lock before placing its world."));
            _macWorld = HostedWorldOnHost.Place(site, fixture, Path.Combine(output, "mac-world-input"), pinned: true, cancellation);
            return Task.CompletedTask;
        }) : report.StepAsync(StepPhase.Setup, "ship and verify the world copy on the server host", async () =>
        {
            var manifest = WorldFixture.Manifest(localWorld);
            // Journalled before the ship, like the runtime copy: no journal line, no world copy on the host.
            await JournalAsync(Host, Role.Host, "server", CopyIntended(WorldDirectory, "world"), cancellation).ConfigureAwait(false);
            _world = WorldCopy.Intended;
            await Host.ShipFilesAsync(localWorld, WorldDirectory, HostedTimeouts.Long, cancellation).ConfigureAwait(false);
            _world = WorldCopy.Shipped;
            var listing = await HostInstall.ListAsync(Host, WorldDirectory, HostedTimeouts.Long, null, cancellation).ConfigureAwait(false);
            HostInstall.RequireSame(manifest, listing, "world copy", ["SOURCE.txt"]);
            // Only a verified copy is journalled done: env recover hands a done world over as the run's save, and removes any other.
            await JournalAsync(Host, Role.Host, "server", CopyDone(WorldDirectory, listing), cancellation).ConfigureAwait(false);
            _world = WorldCopy.Verified;
        });

    // A copy in this run's own directory, journalled with what it holds: env recover removes it by these fields (no staging; its
    // parent is the run directory). Done only once verified, so a done copy is a whole one.
    private JournalEntry CopyIntended(string directory, string holds) =>
        JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", directory), ("stage", ""), ("parent", RunDirectory), ("holds", holds));
    private static JournalEntry CopyDone(string directory, HostListing listing) =>
        JournalEntry.Of(JournalEntry.CopyDone, ("runtime", directory), ("files", listing.Files.Count.ToString(CultureInfo.InvariantCulture)), ("verified", "true"));

    /// <summary>The checks a local runtime copy gets, on the host copy's listing.</summary>
    public void CheckRuntime(ScenarioReport report, ServerRunPlan plan, bool pinned)
    {
        var runtime = _runtime ?? throw new InvalidOperationException("Copy the runtime first.");
        // Hashes do not cover file modes: a launch also requires the copy's execute bit.
        report.Step(StepPhase.Setup, "copied runtime has the plan's server executable", () =>
        {
            var platform = HostInstall.DetectServer(runtime);
            plan.CheckExecutable(platform);
            ServerRunPlan.CheckLaunchHost(platform, HostProfile.Platform switch { "windows" => ServerPlatform.Windows, "macos" => ServerPlatform.MacOS, _ => ServerPlatform.Linux });
            string? unixExecutable = platform switch { ServerPlatform.Linux => GameLaunch.ServerLinuxExecutable, ServerPlatform.MacOS => GameLaunch.ServerMacExecutable, _ => null };
            if (unixExecutable != null && !runtime.Executables.Contains(unixExecutable))
                throw new InvalidOperationException($"{unixExecutable} is not executable in the runtime copy on {Host.Name}; restore its mode (chmod u+x) in the install {Role.Install}.");
        });
        report.Step(StepPhase.Setup, pinned ? "copied runtime is the pinned game build, loader and patchers" : "record the unpinned runtime's game build, loader and patchers", () =>
            (pinned ? HostInstall.CheckPins(plan.RuntimePins ?? throw new ArgumentException("Pin the runtime's game build, loader and patchers in runtimePins, or opt out explicitly with \"pinning\": \"none\"."), runtime, _loader ?? runtime, "runtime")
                : HostInstall.Pins(runtime, _loader ?? runtime)).Record(report.Provenance, "runtime"));
    }

    /// <summary>Refuses an incoherent Windows Doorstop pair in the copied runtime before its server can start.</summary>
    public Task CheckWindowsLoaderAsync(ScenarioReport report, CancellationToken cancellation) => Host.Shell.Kind != HostShellKind.PowerShell
        ? Task.CompletedTask
        : report.StepAsync(StepPhase.Setup, "copied Windows runtime has a coherent Doorstop loader", () =>
            HostClientPreflight.RequireWindowsLoaderAsync(Host, RuntimeDirectory, "server runtime", HostedTimeouts.Quick, cancellation));

    /// <summary>Refuses a busy CLI port on the host, then opens the loopback tunnel to it.</summary>
    public async Task OpenAsync(ScenarioReport report, CancellationToken cancellation)
    {
        // Catch an occupied port without issuing even a read to an unrelated server.
        await report.StepAsync(StepPhase.Setup, "CLI port is free on the server host", () => HostInstall.RequirePortFreeAsync(Host, Role.CliPort, HostedTimeouts.Quick, cancellation)).ConfigureAwait(false);
        await report.StepAsync(StepPhase.Setup, "open the loopback CLI tunnel to the server host", async () =>
        {
            _tunnel = await Host.OpenCliTunnelAsync(Role.CliPort, HostedTimeouts.Quick, Role.LocalCliPort, cancellation).ConfigureAwait(false);
            report.Provenance["cliTunnel"] = $"{_tunnel.Address}:{_tunnel.LocalPort} -> {Role.Host} 127.0.0.1:{_tunnel.HostPort}" + (_tunnel.Forwarded ? " (ssh forward)" : "");
        }).ConfigureAwait(false);
    }

    // The owned server's placement on the host (ServerActor owns the rest of the wiring): each boot through HostServer, its
    // log waited on in the host's log, ValheimCLI only through the tunnel.
    ServerPlatform? IServerPlacement.Platform => LocalMac ? null : Host.Shell.Kind == HostShellKind.PowerShell ? ServerPlatform.Windows : ServerPlatform.Linux;

    ServerBoot IServerPlacement.Start(int n, GameLaunch launch, string output, CancellationToken cancellation)
    {
        if (Role.PreparedSourceRoot is { } source && _preparedPins != null)
        {
            HostInstall.CheckProfilePinsAsync(Host, _preparedPins, GameDirectory, LoaderDirectory, source,
                "prepared server profile before launch", HostedTimeouts.Long, cancellation).GetAwaiter().GetResult();
        }
        string local = Path.Combine(output, "boot-" + n), bootDirectory = HostPath.Join(RunDirectory, "boot-" + n);
        if (LocalMac) return StartLocalMac(launch, local, bootDirectory, cancellation);
        HostServerProcess process;
        // Journalled before the start: a run interrupted from here leaves a record of where its server's pid file is.
        // With the command line the server will have, so its pid file alone proves it the run's (#257).
        string expected = launch.CommandLineSha256();
        JournalAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.ProcessIntended, ("bootDirectory", bootDirectory),
            ("expectedCommandLineSha256", expected)), cancellation).GetAwaiter().GetResult();
        try { process = HostServer.StartAsync(Host, launch, bootDirectory, HostedTimeouts.Quick, [BepInExLog, UnityLog], local,
            logonSeams: null, cancellation: cancellation, logRoot: LoaderDirectory).GetAwaiter().GetResult(); }
        catch (Exception error) when (UnknownOutcome(error) != null)
        {
            // The start's reply was lost: a server may be running there that no session knows. The lock stays.
            _serverMayRun = true;
            throw;
        }
        // What the stop keeps and fetches into boot-N/.
        return new ServerBoot(process,
            [new RunLog($"boot-{n} BepInEx log", Path.Combine(local, "game-0.log"), Required: true), new RunLog($"boot-{n} Unity log", Path.Combine(local, "game-1.log")),
             new RunLog($"boot-{n} stdout", Path.Combine(local, "stdout.log"))],
            new()
            {
                ["pid"] = process.Id, ["startIdentity"] = process.StartIdentity, ["host"] = Host.Name, ["bootDirectory"] = bootDirectory,
                ["startedUtc"] = DateTime.UtcNow, ["world"] = WorldDirectory, ["taskLogon"] = process.TaskLogon,
            },
            () =>
            {
                // The command line's hash is the third fact env recover requires before it stops the process (#257 Q2).
                string commandLine = HostProcessProbe.CommandLineAsync(Host, process.Id, process.StartIdentity, HostedTimeouts.Quick).GetAwaiter().GetResult() ?? "";
                WarnUnexpectedCommandLine(Host, process.Id, expected, commandLine);
                NoteAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.ProcessStarted, ("pid", process.Id.ToString(CultureInfo.InvariantCulture)),
                    ("startIdentity", process.StartIdentity), ("commandLineSha256", commandLine), ("bootDirectory", bootDirectory),
                    ("taskLogon", process.TaskLogon ?? ""))).GetAwaiter().GetResult();
            });
    }

    // The macOS server is local to the runner. Its launch must come from GameLaunch.ToStartInfo: that path carries the
    // native architecture and both DYLD variables through /usr/bin/arch, which a host-shell launch would strip.
    private ServerBoot StartLocalMac(GameLaunch launch, string local, string bootDirectory, CancellationToken cancellation)
    {
        string expected = launch.CommandLineSha256();
        JournalAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.ProcessIntended, ("bootDirectory", bootDirectory),
            ("expectedCommandLineSha256", expected)), cancellation).GetAwaiter().GetResult();
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(bootDirectory);
        foreach (var (relative, index) in new[] { (BepInExLog, 0), (UnityLog, 1) })
        {
            string previous = Path.Combine(RuntimeDirectory, relative);
            if (File.Exists(previous)) File.Move(previous, Path.Combine(bootDirectory, $"previous-{index}.log"));
        }
        string prefix = Path.Combine(local, "game");
        DirectServerProcess? process = null;
        try
        {
            process = new DirectServerProcess(launch.ToStartInfo(), prefix,
                Path.Combine(RuntimeDirectory, BepInExLog), Path.Combine(RuntimeDirectory, UnityLog));
            var found = HostProcessProbe.ProbeAsync(Host, [(process.Id, "")], HostedTimeouts.Quick, cancellation, settle: true).GetAwaiter().GetResult()[(process.Id, "")];
            if (found.State != ProbedState.Same || found.StartIdentity == null || found.CommandLineSha256 == null)
                throw new IOException($"The Mac server process {process.Id} could not be identified for recovery ({found.State}).");
            File.WriteAllText(Path.Combine(bootDirectory, "pid"), $"{process.Id} {found.StartIdentity}\n");
            var logs = new[] { new RunLog("Mac BepInEx log", prefix + ".game-0.log", Required: true),
                new RunLog("Mac Unity log", prefix + ".game-1.log"), new RunLog("Mac stdout", prefix + ".stdout.log") };
            return new ServerBoot(process, logs,
                new() { ["pid"] = process.Id, ["startIdentity"] = found.StartIdentity, ["host"] = Host.Name,
                    ["bootDirectory"] = bootDirectory, ["startedUtc"] = DateTime.UtcNow, ["world"] = WorldDirectory },
                () =>
                {
                    WarnUnexpectedCommandLine(Host, process.Id, expected, found.CommandLineSha256);
                    NoteAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.ProcessStarted,
                        ("pid", process.Id.ToString(CultureInfo.InvariantCulture)), ("startIdentity", found.StartIdentity),
                        ("commandLineSha256", found.CommandLineSha256), ("bootDirectory", bootDirectory))).GetAwaiter().GetResult();
                });
        }
        catch
        {
            if (process != null)
            {
                try { process.Stop(TimeSpan.FromSeconds(15)); process.Dispose(); }
                catch (Exception error) { _serverMayRun = true; Console.Error.WriteLine($"Warning: the Mac server may still run: {error.Message}"); }
            }
            throw;
        }
    }

    IGameTransport IServerPlacement.Connect() => Connect(Tunnel);

    StartupEvents? IServerPlacement.Events(ServerRunPlan plan)
    {
        var tunnel = Tunnel;
        string log = HostPath.Join(RuntimeDirectory, BepInExLog);
        return new StartupEvents
        {
            // This boot's log starts empty (the start moved any earlier one into the boot directory), so offset 0 is this boot's.
            CliListeningWait = async (left, token) =>
                (await Host.WaitForLogAsync(log, 0, StartupEvents.CliListening, StartupEvents.StartupFailures, left, token).ConfigureAwait(false)).EnsureMatched(),
            States = _hooks.StateWaits ? () => StateWait.Connect(tunnel.Address, tunnel.LocalPort) : null,
            ReadyStates = [StateWait.InWorldNoPlayer],
        };
    }

    private CliTunnel Tunnel => _tunnel ?? throw new InvalidOperationException("Open the CLI tunnel before the server starts.");

    /// <summary>A named campaign client's placement (<see cref="CampaignClients.Placement"/>).</summary>
    internal IClientPlacement ClientPlacement(ScenarioReport report) => _clientActors.Placement(report);
    /// <summary>The named client's host, as its client is reached.</summary>
    public IGameHost ClientHost(string name) => _clientActors.ClientHost(name);

    private IGameTransport Connect(CliTunnel tunnel) => _hooks.Connect(tunnel.Address, tunnel.LocalPort);

    /// <summary>
    /// After the owned server stopped: fetches the host's world copy, retires the runtime copy (<see cref="RunRetirement"/>),
    /// closes the tunnel, releases the clients' accounts, retires <paramref name="prepared"/>'s characters and installs under
    /// the locks this run holds, and releases the locks, each as its own step. With <paramref name="serverStopped"/> false
    /// the server host's lock and runtime are kept, because the server may still run there. Returns the failures; it never throws.
    /// </summary>
    public async Task<IReadOnlyList<Exception>> TeardownAsync(ScenarioReport report, string output, bool launched, bool serverStopped,
        PreparedHostedCampaign? prepared = null, RunRetirement? retirement = null, CancellationToken cleanup = default)
    {
        retirement ??= new RunRetirement(report, output);
        serverStopped &= !_serverMayRun;
        var failures = new List<Exception>();
        async Task Try(string step, Func<Task> action)
        {
            // An abandoned cleanup (RunCancellation.BeginCleanup) attempts nothing more; the journal names what is left.
            if (cleanup.IsCancellationRequested) action = () => throw new OperationCanceledException("Not attempted: the cleanup was abandoned.", cleanup);
            try { await report.StepAsync(StepPhase.Cleanup, step, action).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); Console.Error.WriteLine("Teardown: " + error.Message); }
        }
        bool worldFetched = false;
        if (_macWorld != null)
        {
            if (serverStopped)
            {
                int before = failures.Count;
                await Try("move the Mac server's world out of the user's worlds", () => { _macWorld.Collect(); return Task.CompletedTask; }).ConfigureAwait(false);
                if (failures.Count != before) serverStopped = false; // keep the host lock and copy for env recover
            }
            else
                await Try("keep the Mac server's world for recovery", () => throw new IOException("The server may still run; its world remains journalled under the host lock.")).ConfigureAwait(false);
        }
        if (_macLists != null)
        {
            if (serverStopped || !launched && !_serverMayRun)
            {
                int before = failures.Count;
                await Try("restore the Mac server's user-level access lists", () => { _macLists.Restore(); return Task.CompletedTask; }).ConfigureAwait(false);
                if (failures.Count == before)
                    await NoteAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.MacListsRestored, ("backup", Path.Combine(RunDirectory, "server-lists")))).ConfigureAwait(false);
                else serverStopped = false; // the lock and backup stay for env recover
            }
            else
                await Try("keep the Mac server's access lists for recovery", () => throw new IOException("The server may still run; its access-list backup remains journalled under the host lock.")).ConfigureAwait(false);
        }
        if (launched && _world >= WorldCopy.Shipped && serverStopped)
        {
            int before = failures.Count;
            await Try("fetch the server host's world copy", () => Host.FetchDirectoryAsync(WorldDirectory, Path.Combine(output, "host-world"), HostedTimeouts.Long, cleanup)).ConfigureAwait(false);
            worldFetched = failures.Count == before;
        }
        // A copy whose copy began is retired even when it never finished (its reply was lost): nothing ran from it, so all of it goes.
        if (_runtime != null || _runtimeIntended)
            try
            {
                await retirement.HostAsync(Role.Host, Host, _loader ?? _runtime ?? new HostListing(Host.Name, Host.Shell.Kind, RuntimeDirectory, new Dictionary<string, string>(), []),
                    RuntimeDirectory, launched, serverStopped, cleanup).ConfigureAwait(false);
                // A campaign's prepared install is journalled by its preparation; a standalone run's own copy here. A failed retire
                // journals nothing more: the copy stays open in the journal, for env recover.
                if (!Prepared)
                    await NoteAsync(Host, Role.Host, "server", retirement.Kept(Role.Host, RuntimeDirectory)
                        ? JournalEntry.Of(JournalEntry.CopyKept, ("runtime", RuntimeDirectory), ("why", RunRetirement.KeepRequested ? RunRetirement.KeptOnRequest : "its server may still run"))
                        : JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", RuntimeDirectory))).ConfigureAwait(false);
            }
            catch (Exception error) { failures.Add(error); Console.Error.WriteLine("Teardown: " + error.Message); }
        // An abandoned cleanup journals nothing more about the world: it stays open for env recover.
        if (_world != WorldCopy.None && !cleanup.IsCancellationRequested)
        {
            if (_world == WorldCopy.Intended)
                // A ship that failed or was interrupted left a partial world copy, which is nobody's evidence: it goes.
                await Try("remove the partial world copy on the server host", async () =>
                {
                    await HostedRuntimeStage.RetireAsync(Host, WorldDirectory, "", HostedTimeouts.Quick, cleanup, RunId).ConfigureAwait(false);
                    await NoteAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", WorldDirectory))).ConfigureAwait(false);
                }).ConfigureAwait(false);
            else if (!serverStopped)
                await NoteAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.CopyKept, ("runtime", WorldDirectory), ("why", "its server may still run"))).ConfigureAwait(false);
            else if (launched && !worldFetched)
                await NoteAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.CopyKept, ("runtime", WorldDirectory), ("why", "its fetch failed, so it is the run's only copy of the world"))).ConfigureAwait(false);
            else
                // The world copy stays on the host beside the boot logs, as the run's evidence (fetched too when its server ran; one that
                // differs from the fixture shows how): handed over, no longer the run's to remove.
                await NoteAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", WorldDirectory), ("handedOver", "true"))).ConfigureAwait(false);
        }
        if (_tunnel != null) await Try("close the CLI tunnel", () => { _tunnel.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
        await _clientActors.ReleaseAccountsAsync(Try).ConfigureAwait(false);
        // Every process is stopped (or named as possibly running), so the campaign's characters and prepared installs go, under the
        // locks held here; a host whose lock this run never took (a client that never opened) is locked for its retire.
        if (prepared != null)
        {
            var locked = _clientActors.LockedHosts.ToList();
            if (_lock != null) locked.Add(Host.Name);
            // A server that may still run kept its own copy above; the process check below only looks at the copies retired.
            foreach (var failure in await retirement.CampaignAsync(prepared, locked, cleanup).ConfigureAwait(false))
            { failures.Add(failure); Console.Error.WriteLine("Teardown: " + failure.Message); }
        }
        await _clientActors.ReleaseLocksAsync(Try).ConfigureAwait(false);
        if (_lock != null)
            await Try("release the server host's lock", () => serverStopped ? ReleaseAndNoteAsync(Host, Role.Host, _lock)
                : throw new HostLockException(new HostLockResult(HostLockState.Unknown, _lock.Owner,
                    $"Kept {_lock.Path} on {Host.Name}: the owned server there may still run. Remove {_lock.Path}/owner by hand once it has stopped."))).ConfigureAwait(false);
        return failures;
    }

    private async Task ReleaseAndNoteAsync(IGameHost host, string hostName, HostLock held)
    {
        await ReleaseAsync(held).ConfigureAwait(false);
        await NoteLockAsync(host, hostName, held, JournalEntry.LockReleased).ConfigureAwait(false);
    }

    private static async Task ReleaseAsync(HostLock held)
    {
        var result = await held.ReleaseAsync().ConfigureAwait(false);
        if (result.State is not (HostLockState.Released or HostLockState.Free)) throw new HostLockException(result);
    }

    // The started game's command line should be the one its launch journalled: where it is not, a run interrupted before its
    // process was journalled could not prove that process its own from the pid file (env status leaves it unrecoverable).
    internal static void WarnUnexpectedCommandLine(IGameHost host, int pid, string expected, string read)
    {
        if (read.Length != 0 && !string.Equals(read, expected, StringComparison.OrdinalIgnoreCase))
            Console.Error.WriteLine($"Warning: process {pid} on {host.Name} runs another command line than its launch journalled (SHA-256 {read}, expected {expected}); " +
                "had the run been interrupted before journalling it, env recover could not have proven it the run's from its pid file.");
    }

    /// <summary>
    /// Why a failure leaves the outcome unknown rather than failed: a host operation whose reply was lost or whose transport
    /// failed, or a lock whose state could not be proven. Null for every other failure.
    /// </summary>
    internal static string? UnknownOutcome(Exception? error)
    {
        for (var current = error; current != null; current = current.InnerException)
        {
            if (current is HostOperationException { Outcome: not HostOutcome.Exited } host) return host.Message;
            if (current is HostLockException { State: HostLockState.Unknown } hostLock) return hostLock.Message;
            if (current is SteamAccountLeaseException { State: SteamAccountLeaseState.Unknown } lease) return lease.Message;
            if (current is AggregateException aggregate) return aggregate.InnerExceptions.Select(UnknownOutcome).FirstOrDefault(reason => reason != null);
        }
        return null;
    }
}

/// <summary>A campaign client's leased Steam account, with what must be gone before its lease is released.</summary>
internal sealed class ClientAccount(string client, SteamAccountHold hold, IGameHost leaseHost, string leaseHostName)
{
    public string Client { get; } = client;
    public SteamAccountHold Hold { get; } = hold;
    public ClientSession? Session { get; set; }
    public IOwnedProcess? Process { get; set; }
    /// <summary>The host the lease lives on, and its inventory name: where the run journals the lease.</summary>
    public IGameHost LeaseHost { get; } = leaseHost;
    public string LeaseHostName { get; } = leaseHostName;
}

/// <summary>
/// A client <see cref="InteractiveClient"/> started for a <see cref="PinnedServerRun"/>: stopping it stops only that process (a
/// clean stop asks it to quit first),
/// keeps its BepInEx log and Player.log in its launch directory, fetches that directory here and closes its CLI tunnel.
/// </summary>
internal sealed class HostedClientProcess(InteractiveClientProcess process, IGameHost host, string install, CliTunnel tunnel, string evidence) : IOwnedProcess, IClientProcessIdentity
{
    private bool _kept;
    public int Id => process.Id;
    string IClientProcessIdentity.StartFileTimeUtc => process.StartIdentity;
    public bool HasExited => process.HasExited;
    public Task<int> WaitForExitAsync(CancellationToken cancellation) => process.WaitForExitAsync(cancellation);

    public void Stop(TimeSpan timeout) => Finish(() => process.Stop(timeout));

    /// <summary>Asks the client to quit (<see cref="InteractiveClientProcess.StopCleanly"/>), then keeps and fetches its logs as <see cref="Stop"/> does.</summary>
    public ProcessStop StopCleanly(TimeSpan quit, TimeSpan kill)
    {
        ProcessStop? stopped = null;
        Finish(() => stopped = process.StopCleanly(quit, kill));
        return stopped!;
    }

    private void Finish(Action stop)
    {
        try
        {
            stop();
            if (_kept) return;
            var kept = host.RunAsync(HostedClientScripts.Keep(host.Shell.Kind), new Dictionary<string, string> { ["install"] = install, ["dir"] = process.LaunchDirectory },
                TimeSpan.FromSeconds(60)).GetAwaiter().GetResult().EnsureSuccess($"Keeping the logs of client process {Id} on {host.Name}");
            if (InteractiveClient.Line(kept.Stdout, "VT-KEPT") == null) throw new HostOperationException($"Unexpected reply while keeping the client's logs on {host.Name}", kept);
            host.FetchDirectoryAsync(process.LaunchDirectory, evidence, TimeSpan.FromMinutes(5)).GetAwaiter().GetResult();
            _kept = true;
        }
        finally { tunnel.Dispose(); }
    }

    public void Dispose()
    {
        try { if (!_kept) Stop(TimeSpan.FromSeconds(30)); }
        finally { tunnel.Dispose(); }
    }
}

// Scripts for a hosted client's logs. Values arrive as variables; each ends with its verdict.
internal static class HostedClientScripts
{
    public static string MoveAside(HostShellKind kind) => kind == HostShellKind.Bash ? BashMoveAside : PowerShellMoveAside;
    public static string Keep(HostShellKind kind) => kind == HostShellKind.Bash ? BashKeep : PowerShellKeep;
    public static string Preloader(HostShellKind kind) => kind == HostShellKind.Bash ? BashPreloader : PowerShellPreloader;

    // Variables: install, dir. Reads only: each preloader_*.log beside the game, whether this launch wrote it (not older than
    // the launch's pid file) and the first [Error or [Fatal line of each fresh one, base64-encoded:
    // VT-PRELOADER fresh|stale <name> <line or ->, then VT-PRELOADER-END.
    public static readonly string BashPreloader = """
        set -u
        ref="$dir/pid"; [ -e "$ref" ] || ref="$dir"
        for f in "$install"/preloader_*.log; do
          [ -f "$f" ] || continue
          name=$(basename -- "$f" | base64 | tr -d '\n')
          if [ "$ref" -nt "$f" ]; then echo "VT-PRELOADER stale $name -"; continue; fi
          line=$(grep -m1 -E '\[(Error|Fatal)' -- "$f" 2> /dev/null | base64 | tr -d '\n')
          echo "VT-PRELOADER fresh $name ${line:--}"
        done
        echo "VT-PRELOADER-END"
        """.ReplaceLineEndings("\n");
    public static readonly string PowerShellPreloader = """
        $ref = Join-Path $dir 'pid'
        $since = if ([IO.File]::Exists($ref)) { [IO.File]::GetLastWriteTimeUtc($ref) } else { [IO.Directory]::GetLastWriteTimeUtc($dir) }
        $utf8 = New-Object Text.UTF8Encoding $false
        if ([IO.Directory]::Exists($install)) {
            foreach ($f in @([IO.Directory]::GetFiles($install, 'preloader_*.log'))) {
                $name = [Convert]::ToBase64String($utf8.GetBytes([IO.Path]::GetFileName($f)))
                if ([IO.File]::GetLastWriteTimeUtc($f) -lt $since) { 'VT-PRELOADER stale ' + $name + ' -'; continue }
                $first = @([IO.File]::ReadAllLines($f) | Where-Object { $_ -match '\[(Error|Fatal)' } | Select-Object -First 1)
                $line = if ($first.Count -ne 0) { [Convert]::ToBase64String($utf8.GetBytes($first[0])) } else { '-' }
                'VT-PRELOADER fresh ' + $name + ' ' + $line
            }
        }
        'VT-PRELOADER-END'
        """.ReplaceLineEndings("\n");

    /// <summary>
    /// Why a client never wrote a BepInEx log line, from BepInEx's preloader logs beside the game: the first error of each one
    /// this launch wrote (#254), and the names of older ones, which are not this launch's and explain nothing. Null when it
    /// cannot be read.
    /// </summary>
    public static async Task<PreloaderLogs.Reading?> ReadPreloaderAsync(IGameHost host, string install, string launchDirectory)
    {
        try
        {
            var result = await host.RunAsync(Preloader(host.Shell.Kind), new Dictionary<string, string> { ["install"] = install, ["dir"] = launchDirectory },
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (!result.Succeeded || InteractiveClient.Line(result.Stdout, "VT-PRELOADER-END") == null) return null;
            var fresh = new List<(string, string?)>(); var stale = new List<string>();
            foreach (string line in result.Stdout.Split('\n'))
                if (line.Trim().Split(' ') is ["VT-PRELOADER", var kind, var name, var error])
                {
                    string Decode(string value) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value)).Trim();
                    if (kind == "stale") stale.Add(Decode(name));
                    else fresh.Add((Decode(name), error == "-" ? null : Decode(error)));
                }
            return new PreloaderLogs.Reading(fresh, stale);
        }
        catch (Exception error) when (error is HostOperationException or TimeoutException or IOException or FormatException or InvalidOperationException) { return null; }
    }

    // Variables: log, to.
    public static readonly string BashMoveAside = """
        set -u
        if [ ! -e "$log" ]; then echo "VT-NONE"; exit 0; fi
        mkdir -p -- "$(dirname -- "$to")" && mv -f -- "$log" "$to" || exit 3
        echo "VT-MOVED"
        """.ReplaceLineEndings("\n");

    public static readonly string PowerShellMoveAside = """
        if (-not [IO.File]::Exists($log)) { 'VT-NONE'; exit 0 }
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
        [IO.File]::Move($log, $to)
        'VT-MOVED'
        """.ReplaceLineEndings("\n");

    // Variables: install, dir. The game's BepInEx log and Unity's Player.log (in the host user's profile) are copied into the
    // launch directory as game-0.log and game-1.log, or an .absent note says the game never wrote one.
    public static readonly string BashKeep = """
        set -u
        [ -d "$dir" ] || exit 3
        keep() { if [ -f "$1" ]; then cp -- "$1" "$dir/$2" || exit 3; else printf 'The game did not write %s\n' "$1" > "$dir/$2.absent" || exit 3; fi; }
        keep "$install/BepInEx/LogOutput.log" game-0.log
        keep "${HOME:-/nonexistent}/.config/unity3d/IronGate/Valheim/Player.log" game-1.log
        # BepInEx's preloader crash logs beside the game (#254): this launch's (not older than its pid file) are kept; older
        # ones, left by an earlier start, are only named.
        ref="$dir/pid"; [ -e "$ref" ] || ref="$dir"
        n=0; stale=""
        for f in "$install"/preloader_*.log; do
          [ -f "$f" ] || continue
          if [ "$ref" -nt "$f" ]; then stale="$stale$(basename -- "$f")
        "; else n=$((n + 1)); cp -- "$f" "$dir/game-2.preloader-$n.log" || exit 3; fi
        done
        if [ -n "$stale" ]; then printf 'Older preloader logs beside the game, from before this launch (not kept):\n%s' "$stale" > "$dir/preloader.stale" || exit 3; fi
        echo "VT-KEPT"
        """.ReplaceLineEndings("\n");

    public static readonly string PowerShellKeep = """
        if (-not [IO.Directory]::Exists($dir)) { exit 3 }
        function Save-VtLog([string]$from, [string]$name) {
            $to = Join-Path $dir $name
            if ([IO.File]::Exists($from)) { [IO.File]::Copy($from, $to, $true) } else { [IO.File]::WriteAllText($to + '.absent', 'The game did not write ' + $from) }
        }
        Save-VtLog (Join-Path $install 'BepInEx\LogOutput.log') 'game-0.log'
        Save-VtLog (Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData\LocalLow\IronGate\Valheim\Player.log') 'game-1.log'
        # BepInEx's preloader crash logs beside the game (#254): this launch's (not older than its pid file) are kept; older ones are only named.
        $ref = Join-Path $dir 'pid'; if (-not [IO.File]::Exists($ref)) { $ref = $dir }
        $since = [IO.File]::GetLastWriteTimeUtc($ref); if ($ref -eq $dir) { $since = [IO.Directory]::GetLastWriteTimeUtc($dir) }
        $n = 0; $stale = @()
        $logs = if ([IO.Directory]::Exists($install)) { @([IO.Directory]::GetFiles($install, 'preloader_*.log')) } else { @() }
        foreach ($f in $logs) {
            if ([IO.File]::GetLastWriteTimeUtc($f) -lt $since) { $stale += [IO.Path]::GetFileName($f) }
            else { $n++; [IO.File]::Copy($f, (Join-Path $dir ('game-2.preloader-' + $n + '.log')), $true) }
        }
        if ($stale.Count -ne 0) { [IO.File]::WriteAllText((Join-Path $dir 'preloader.stale'), "Older preloader logs beside the game, from before this launch (not kept):`n" + ($stale -join "`n")) }
        'VT-KEPT'
        """.ReplaceLineEndings("\n");
}

internal static class HostedRunScripts
{
    // Windows counterpart of Retire. Only this run's own runtime copy may be removed, and retained files must resolve
    // below it without a reparse point. Variables: runtime, keep, run, files, perfile, total.
    public static readonly string WindowsRetire = """
        if (-not $run -or $run -match '[\\/]' -or $run -eq '.' -or $run -eq '..') { exit 3 }
        $parent = [IO.Path]::GetDirectoryName($runtime)
        if ([IO.Path]::GetFileName($runtime) -cne 'runtime' -or [IO.Path]::GetFileName($parent) -cne $run -or
            [IO.Path]::GetFileName($keep) -cne 'runtime-changes' -or [IO.Path]::GetDirectoryName($keep) -cne $parent) { exit 3 }
        if ([IO.Directory]::Exists($keep) -or [IO.File]::Exists($keep)) { exit 3 }
        [void][IO.Directory]::CreateDirectory($keep)
        if (-not [IO.Directory]::Exists($runtime)) { 'VT-RETIRED 0 0'; exit 0 }
        if ([IO.File]::GetAttributes($runtime) -band [IO.FileAttributes]::ReparsePoint) { exit 3 }
        $root = [IO.Path]::GetFullPath($runtime).TrimEnd('\', '/') + '\'
        $kept = [long]0
        foreach ($line in ($files -split "`n")) {
            if (-not $line) { continue }
            $relative = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line))
            if ([IO.Path]::IsPathRooted($relative) -or $relative.Split([char[]]'\/') -contains '..') { 'VT-NOTKEPT ' + $line + ' -1'; continue }
            $source = [IO.Path]::GetFullPath((Join-Path $runtime $relative))
            if (-not $source.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or -not [IO.File]::Exists($source)) { 'VT-NOTKEPT ' + $line + ' -1'; continue }
            $walk = [IO.Path]::GetDirectoryName($source)
            $linked = $false
            while ($walk -and $walk.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
                if ([IO.File]::GetAttributes($walk) -band [IO.FileAttributes]::ReparsePoint) { $linked = $true; break }
                $walk = [IO.Path]::GetDirectoryName($walk)
            }
            if ($linked -or ([IO.File]::GetAttributes($source) -band [IO.FileAttributes]::ReparsePoint)) { 'VT-NOTKEPT ' + $line + ' -1'; continue }
            $size = (New-Object IO.FileInfo $source).Length
            if ($size -gt [long]$perfile -or $kept + $size -gt [long]$total) { 'VT-NOTKEPT ' + $line + ' ' + $size; continue }
            $target = Join-Path $keep $relative
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.File]::Copy($source, $target)
            $kept += $size
        }
        $bytes = [long]0
        foreach ($file in [IO.Directory]::EnumerateFiles($runtime, '*', [IO.SearchOption]::AllDirectories)) { $bytes += (New-Object IO.FileInfo $file).Length }
        [IO.Directory]::Delete($runtime, $true)
        'VT-RETIRED ' + $bytes + ' ' + $kept
        """.ReplaceLineEndings("\n");

    public static readonly string WindowsDropKept = """
        if (-not $run -or $run -match '[\\/]' -or $run -eq '.' -or $run -eq '..') { exit 3 }
        if ([IO.Path]::GetFileName($keep) -cne 'runtime-changes' -or [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($keep)) -cne $run) { exit 3 }
        if ([IO.Directory]::Exists($keep)) { [IO.Directory]::Delete($keep, $true) }
        'VT-DROPPED'
        """.ReplaceLineEndings("\n");

    // Keeps the listed files (base64 relative paths, one per line) from a run's runtime copy in $keep, within the size
    // limits, then removes the copy. Only a directory named runtime directly inside the run's own directory ($run) is
    // ever removed; a path that leaves the copy, a link or anything but a regular file is listed, never copied.
    public static readonly string Retire = """
        set -u
        case "$run" in ''|*/*|.|..) exit 3;; esac
        [ "$(basename -- "$runtime")" = runtime ] || exit 3
        [ "$(basename -- "$(dirname -- "$runtime")")" = "$run" ] || exit 3
        [ "$(basename -- "$keep")" = runtime-changes ] || exit 3
        [ "$(dirname -- "$keep")" = "$(dirname -- "$runtime")" ] || exit 3
        # A reused or linked destination could overwrite evidence outside this run. Leave the runtime for review instead.
        [ ! -e "$keep" ] && [ ! -L "$keep" ] || exit 3
        if [ ! -d "$runtime" ] || [ -L "$runtime" ]; then mkdir -p -- "$keep" || exit 3; echo "VT-RETIRED 0 0"; exit 0; fi
        mkdir -p -- "$keep" || exit 3
        real=$(readlink -f -- "$runtime") || exit 3
        kept=0
        while IFS= read -r line; do
            [ -n "$line" ] || continue
            rel=$(printf '%s' "$line" | base64 -d) || exit 3
            case "$rel" in /*|..|../*|*/../*|*/..) printf 'VT-NOTKEPT %s -1\n' "$line"; continue;; esac
            src="$runtime/$rel"
            if [ ! -f "$src" ] || [ -L "$src" ]; then printf 'VT-NOTKEPT %s -1\n' "$line"; continue; fi
            # Never through a linked directory inside the copy: the file's real path must be inside the copy too.
            case "$(readlink -f -- "$src")" in "$real"/*) ;; *) printf 'VT-NOTKEPT %s -1\n' "$line"; continue;; esac
            if [ "$(uname)" = Darwin ]; then size=$(stat -f %z -- "$src") || exit 3
            else size=$(stat -c %s -- "$src") || exit 3; fi
            if [ "$size" -gt "$perfile" ] || [ $((kept + size)) -gt "$total" ]; then printf 'VT-NOTKEPT %s %s\n' "$line" "$size"; continue; fi
            mkdir -p -- "$(dirname -- "$keep/$rel")" && cp -p -- "$src" "$keep/$rel" || exit 3
            kept=$((kept + size))
        done <<< "$files"
        if [ "$(uname)" = Darwin ]; then bytes=$(du -sk -- "$runtime") || exit 3
        else bytes=$(du -sb -- "$runtime") || exit 3; fi
        bytes=${bytes%%$'\t'*}
        if [ "$(uname)" = Darwin ]; then bytes=$((bytes * 1024)); fi
        rm -rf -- "$runtime" || exit 3
        printf 'VT-RETIRED %s %s\n' "$bytes" "$kept"
        """.ReplaceLineEndings("\n");

    // Removes the run's runtime-changes directory on the host once it was fetched; only <run>/runtime-changes.
    public static readonly string DropKept = """
        set -u
        case "$run" in ''|*/*|.|..) exit 3;; esac
        [ "$(basename -- "$keep")" = runtime-changes ] || exit 3
        [ "$(basename -- "$(dirname -- "$keep")")" = "$run" ] || exit 3
        rm -rf -- "$keep" || exit 3
        echo "VT-DROPPED"
        """.ReplaceLineEndings("\n");
}
