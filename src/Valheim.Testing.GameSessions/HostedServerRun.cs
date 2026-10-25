using System.Globalization;
using System.Text;
using System.Text.Json;
using Valheim.Testing.Game;
using valheimCLI;
using valheim_cli.Testing;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The parts of a <see cref="PinnedServerRun"/> that differ when its dedicated server runs on the environment's server
/// host (<c>--inventory</c> or a campaign): the host lock, the server's disposable loader profile (a campaign's prepared one, or the
/// one a standalone run stages from the host's install, <see cref="LockAndPrepareRuntimeAsync"/>), the world copy
/// shipped and verified there, the port check, the loopback CLI tunnel, the owned server's boots through <see cref="HostServer"/> (its <see cref="ServerActor"/>'s placement),
/// remote clients through <see cref="InteractiveClient"/> and a local macOS GUI client through <see cref="ClientSession"/>,
/// each client's Steam identity lease in a campaign, and the teardown
/// that fetches evidence, closes the tunnel and releases the leases and locks.
/// </summary>
internal sealed class HostedServerRun : IServerPlacement
{
    internal const string BepInExLog = "BepInEx/LogOutput.log";
    private readonly IHostedRunHooks _hooks;
    private readonly string _owner;
    private readonly ServerRunPlan _plan;
    // The campaign's clients, their leases and their hosts' locks: owned by CampaignClients, as in a campaign without a server.
    private readonly CampaignClients _clientActors;
    private JournaledHostLock? _lock;
    private CliTunnel? _tunnel;
    private HostListing? _runtime;
    private HostListing? _loader;
    private InstallPins? _preparedPins;
    private IReadOnlyDictionary<string, string>? _preparedPluginFiles;
    private ProfileSourceCheck? _sourceCheck;
    private readonly SortedDictionary<string, string> _pinOrigins = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, string> _sourceCliPins = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _replacedCliPaths = [];
    private HostedWorld? _macWorld;
    private UserSaveFiles? _macLists;
    private bool _serverMayRun;
    // A standalone run's own profile and world copy on the server host, journalled before their scripts run (#257): from then on
    // teardown removes them, even a partial one, or leaves them open in the journal for env recover.
    private bool _runtimeIntended;
    // The reviewed loader package a standalone run's inventory recipe names (EnvironmentRuns.PlaceServer), else null: the pinned one.
    private readonly string? _loaderPackage;
    private WorldCopy _world;
    private readonly List<BootEvidence> _boots = [];
    private sealed class BootEvidence(string directory, string output)
    {
        internal string Directory { get; } = directory;
        internal string Output { get; } = output;
        internal HostServerProcess? Process { get; set; }
    }
    // The current boot's launch-recorded identity and its start probe, kept until startup verification has passed: only
    // then is it published as the local server command lease (IServerPlacement.PublishCommandLease).
    private (int Pid, string StartIdentity, ProbedProcess? Found)? _leaseCandidate;
    private enum WorldCopy { None, Intended, Shipped, Verified }

    private HostedServerRun(ResolvedEnvironment profile, GameRole role, HostProfile hostProfile, IGameHost host, string runId, string runner, ServerRunPlan plan, IHostedRunHooks hooks,
        string? preparedRuntime, string? loaderPackage, IReadOnlyDictionary<string, ProfileSourceCheck>? sourceChecks)
    {
        Profile = profile; Role = role; HostProfile = hostProfile; Host = host; RunId = runId; _hooks = hooks; _owner = runner + " " + runId;
        _plan = plan;
        _loaderPackage = loaderPackage;
        _journal = new RunJournal(runId);
        // A campaign prepared the server's disposable profile already (<runtime>/vt-prep-<id>-server/runtime): that is the one
        // profile the server runs from. A standalone run stages its own under <runtime>/<runId>, its staging folder beside it.
        Prepared = preparedRuntime != null;
        RunDirectory = HostPath.Join(role.Runtime, runId);
        RuntimeDirectory = preparedRuntime ?? HostPath.Join(RunDirectory, "runtime");
        StagingDirectory = HostPath.Join(RunDirectory, "staging");
        WorldDirectory = LocalMac ? HostedWorld.DefaultSaveDirectory(ClientPlatform.MacOS) : HostPath.Join(RunDirectory, "world");
        var runFolders = new RunFolder.Claims();
        _runFolder = new RunFolder(host, role.Host, RunJournal.DirectoryFor(hostProfile), role.Runtime, runId,
            (entry, token) => JournalAsync(Host, Role.Host, "server", entry, token), runFolders);
        _clientActors = new CampaignClients(profile, runId, _owner, hooks, (role.Host, host), JournalAsync, sourceChecks, runFolders);
        if (sourceChecks?.TryGetValue("server", out var check) == true) _sourceCheck = check;
    }

    // The run's journal on each host it touches (RunJournal): a process and an exact lock claimant are journalled before their effects.
    private readonly RunJournal _journal;
    // RunDirectory, made once the lock is held and removed whole at teardown after its fetch (#791).
    private readonly RunFolder _runFolder;
    private Task JournalAsync(IGameHost host, string hostName, string actor, JournalEntry entry, CancellationToken cancellation) =>
        _journal.AppendAsync(host, RunJournal.DirectoryFor(Profile.Hosts[hostName]), actor, entry, HostedTimeouts.Quick, cancellation);
    // After its effect a lost line is a warning: the effect happened, and the entry before it already names where to look.
    private async Task NoteAsync(IGameHost host, string hostName, string actor, JournalEntry entry)
    {
        try { await JournalAsync(host, hostName, actor, entry, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { Console.Error.WriteLine($"Warning: could not journal {entry.Kind} for {actor} on {hostName}: {error.Message}"); }
    }
    /// <summary>Whether the runtime is a campaign's prepared profile (verified in place) rather than one this run stages.</summary>
    public bool Prepared { get; }

    public ResolvedEnvironment Profile { get; }
    public GameRole Role { get; }
    public HostProfile HostProfile { get; }
    private bool LocalMac => HostProfile is { Kind: "local", Platform: "macos" };
    private bool RemoteMac => HostProfile is { Kind: "ssh", Platform: "macos" };
    private bool MacServer => HostProfile.Platform == "macos";
    public IGameHost Host { get; }
    public string RunId { get; }
    /// <summary>
    /// This run's directory on the server host: <c>runtime</c> (a standalone run's profile, with <c>staging</c> while it is
    /// staged), <c>world</c> and <c>boot-N</c>. The world and boot logs go after their verified fetch; the profile goes at teardown
    /// once the server has stopped, and then what is left of the directory, once fetched (<see cref="RunFolder"/>, <see cref="TeardownAsync"/>).
    /// </summary>
    public string RunDirectory { get; }
    /// <summary>The server's profile on the host: its loader root, where BepInEx writes, and the folder retired at teardown.</summary>
    public string RuntimeDirectory { get; }
    // A standalone profile's staging folder (HostedRuntimeStage.RequireStaging's rule: a sibling named staging).
    private string StagingDirectory { get; }
    // The owned runtime remains the log and retirement root. A Windows or Linux profile is linked, so it is the game root too;
    // a macOS profile starts the executable from this read-only game root while Doorstop and BepInEx remain under RuntimeDirectory.
    public string GameDirectory => Role.PreparedGameRoot ?? RuntimeDirectory;
    public string LoaderDirectory => RuntimeDirectory;
    public string? MacSteamRuntimeDirectory => Role.PreparedSteamRuntimeRoot;
    // Every server's profile is linked (ProfileLayouts.For).
    bool IServerPlacement.Linked => ProfileLayouts.For(HostProfile.Platform, server: true) == ProfileLayout.Linked;
    public string WorldDirectory { get; private set; }
    /// <summary>The profile's game files on the host (the listing a campaign's server profile records too), once staged.</summary>
    public IReadOnlyDictionary<string, string> RuntimeHashes => _runtime?.Files ?? new Dictionary<string, string>();
    /// <summary>The standalone run's pin groups and whether each came from the input plan or the staged profile.</summary>
    internal IReadOnlyDictionary<string, string> PinOrigins => _pinOrigins;
    /// <summary>Source ValheimCLI pins the input plan supplied and the run checked before replacing those files.</summary>
    internal IReadOnlyDictionary<string, string> SourceCliPins => _sourceCliPins;
    /// <summary>Install-relative ValheimCLI paths the standalone profile replaced with its coherent staged set.</summary>
    internal IReadOnlyList<string> ReplacedCliPaths => _replacedCliPaths;
    /// <summary>The run's builds (#860): the server's install is admitted where its profile's selection lists it.</summary>
    internal GameVersionAgreement.Ledger? Builds { get; private init; }

    /// <summary>Refuses an environment and plan that cannot run a server on the environment's server host, before anything is touched.</summary>
    public static HostedServerRun Create(ResolvedEnvironment profile, ServerRunPlan plan, string runner, IHostedRunHooks? hooks = null) =>
        Create(profile, plan, runner, hooks, prepared: false);

    /// <summary>
    /// With <paramref name="prepared"/>, the server role's install is a campaign's prepared disposable profile: the run uses it as its
    /// runtime, under the campaign's run id. Otherwise the run stages its own from the role's install, on <paramref name="loaderPackage"/>
    /// (the inventory recipe's reviewed loader package) when given, else on the pinned one (<see cref="ShippedLoader"/>).
    /// </summary>
    internal static HostedServerRun Create(ResolvedEnvironment profile, ServerRunPlan plan, string runner, IHostedRunHooks? hooks, bool prepared,
        string? campaignRunId = null, string? loaderPackage = null,
        IReadOnlyDictionary<string, ProfileSourceCheck>? sourceChecks = null, GameVersionAgreement.Ledger? builds = null)
    {
        var role = profile.Server ?? throw new ArgumentException("The environment places no dedicated server.");
        var hostProfile = profile.Hosts[role.Host];
        if (Refusal(hostProfile, role, plan) is { } refusal) throw new ArgumentException($"The server environment on host '{role.Host}': {refusal}");
        if (hostProfile is { Kind: "ssh", Platform: "macos" } && !prepared)
            throw new InvalidOperationException("A remote Mac dedicated server needs a prepared profile campaign with its pinned native loader and SteamCMD runtime; a standalone run cannot stage it.");
        if (prepared && loaderPackage != null) throw new ArgumentException("A campaign's prepared server profile already has its loader.", nameof(loaderPackage));
        hooks ??= HostedRunHooks.Production;
        var host = hooks.CreateHost(profile, role.Host);
        return new HostedServerRun(profile, role, hostProfile, host, hooks.RunId(campaignRunId), runner, plan, hooks,
            prepared ? role.Install : null, loaderPackage, sourceChecks) { Builds = builds };
    }

    /// <summary>Why a server environment cannot run <paramref name="plan"/>, or null: its host's platform and shell, and the plan's ports.</summary>
    internal static string? Refusal(HostProfile hostProfile, GameRole role, ServerRunPlan plan)
    {
        if (hostProfile.Platform is not ("linux" or "macos" or "windows") || hostProfile.Shell != HostProfile.ShellFor(hostProfile.Platform) ||
            (hostProfile.Platform == "macos" && hostProfile.Kind is not ("local" or "ssh")))
            return $"it is {hostProfile.Platform} with {hostProfile.Shell}; a hosted dedicated server needs Linux/bash, macOS/bash on a local or SSH host, or Windows/pwsh.";
        if (hostProfile.Kind == "local" && hostProfile.Platform != EnvironmentInventory.ThisMachine.Platform)
            return $"it is a local {hostProfile.Platform} host, but this machine is {EnvironmentInventory.ThisMachine.Platform}.";
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
        if (GamePort(plan) is int gamePort && gamePort != role.GamePort)
            return $"the plan's -port {gamePort} is not its gamePort {role.GamePort}.";
        return null;
    }

    /// <summary>The game port the plan's arguments name with <c>-port</c> (in any case: the game reads its arguments lowercased), or null.</summary>
    internal static int? GamePort(ServerRunPlan plan)
    {
        int at = Array.FindIndex(plan.Arguments, argument => argument.Equals("-port", StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < plan.Arguments.Length && int.TryParse(plan.Expand(plan.Arguments[at + 1], "", ""), NumberStyles.None,
            CultureInfo.InvariantCulture, out int port) ? port : null;
    }

    public void Record(IDictionary<string, string> provenance)
    {
        provenance["serverHost"] = Role.Host;
        provenance["serverHostKind"] = Host.Kind.ToString();
        if (Host is SshGameHost ssh) provenance["serverHostSshClient"] = ssh.SshClientChoice;
        provenance["hostRunDirectory"] = RunDirectory;
        provenance["runtimeSource"] = Role.Host + ":" + (Role.PreparedSourceRoot ?? Role.Install);
    }

    /// <summary>
    /// The hard-link storage check of a linked profile, before this run takes a lock or stages its profile. On a Linux or Mac
    /// host it writes one trial link in a new folder under the runtime root and removes both (<see cref="HostedRuntimeStage.RequireProfileStorageAsync"/>).
    /// </summary>
    internal async Task<string?> PreflightProfileStorageAsync(CancellationToken cancellation) =>
        (await HostedRuntimeStage.RequireProfileStorageAsync(Host, HostedRuntimeKind.Server, Role.Install, Role.Runtime,
            HostedTimeouts.Quick, cancellation, explicitRuntime: Role.RuntimeExplicit).ConfigureAwait(false)).Description;

    internal GameBuild? WorldBuild { get; private set; }
    internal WorldIdentity.Admission? WorldAdmission { get; private set; }
    internal WorldIdentity? AdmittedWorld => WorldAdmission?.Identity;
    internal async Task AdmitWorldAsync(ServerRunPlan plan, CancellationToken cancellation, IReadOnlyDictionary<string, string> files)
    {
        if (plan.PreflightWorld() == null) return;
        var listing = Prepared && _sourceCheck != null ? _sourceCheck.PreparedGame
            : await HostInstall.ListAsync(Host, Role.Install, HostedTimeouts.Long, null, cancellation).ConfigureAwait(false);
        WorldBuild = await GameVersionAgreement.ReadAsync(Host, listing, HostedTimeouts.Long, cancellation).ConfigureAwait(false);
        if (Builds != null) WorldBuild = Builds.Admit(new("server", Host.Name, listing.Root, WorldBuild));
        var worldFiles = files.Where(file => file.Key.Replace('\\', '/').StartsWith("worlds_local/", StringComparison.Ordinal))
            .ToDictionary(file => file.Key["worlds_local/".Length..], file => file.Value, StringComparer.Ordinal);
        WorldAdmission = WorldIdentity.Capture(Path.Combine(plan.World.Source, "worlds_local"), WorldBuild, worldFiles);
        if (plan.PreflightWorld(WorldBuild, plan.World.Source) != WorldAdmission.Identity)
            throw new InvalidDataException("The server world changed during admission.");
        WorldAdmission.Verify();
    }

    /// <summary>
    /// Takes the server host's lock, then readies the server's profile: a campaign's prepared one is verified where it is; a
    /// standalone run stages its own from the host's install (<see cref="StageProfileAsync"/>).
    /// </summary>
    public async Task LockAndPrepareRuntimeAsync(ScenarioReport report, ServerRunPlan plan, CancellationToken cancellation)
    {
        // A campaign's prepared profile is checked before each boot against the pins its preparation bound; a standalone run's
        // are bound once it has staged its own (StageProfileAsync), never taken from a role an earlier run staged.
        _preparedPins = Prepared && Role.PreparedSourceRoot != null ? plan.RuntimePins : null;
        if (RemoteMac)
            WorldDirectory = await HostedWorldOnHost.MacSaveRootAsync(Host, HostedTimeouts.Quick, cancellation).ConfigureAwait(false);
        // A Windows host that can register no server task is refused before the profile (a campaign's preflight asked already).
        if (Host.Shell.Kind == HostShellKind.PowerShell)
            await report.StepAsync(StepPhase.Setup, "the server host can start a server task", () => HostServer.RequireTaskLogonAsync(Host, HostedTimeouts.Quick, cancellation)).ConfigureAwait(false);
        await report.StepAsync(StepPhase.Setup, "take the server host's lock", async () => _lock = await JournaledHostLock.AcquireAsync(
            Host, HostProfile.Lock, _owner, _journal, RunJournal.DirectoryFor(HostProfile), "run", HostedTimeouts.Quick, cancellation).ConfigureAwait(false)).ConfigureAwait(false);
        // Everything this run writes on the host lies under its run directory, which must lie outside the install that runs never
        // change: decided on the host, with links resolved there (a prepared campaign's preparation checked its own profile too).
        await HostInstall.RequireApartAsync(Host, Role.Install, [RunDirectory], "the install", HostedTimeouts.Quick, cancellation).ConfigureAwait(false);
        if (MacServer)
            await report.StepAsync(StepPhase.Setup, "preserve the Mac server's user-level access lists", async () =>
            {
                await _runFolder.MakeAsync(cancellation).ConfigureAwait(false); // the backup goes in it
                string backup = HostPath.Join(RunDirectory, UserSaveFiles.BackupName(RunId, "server"));
                _macLists = await UserSaveFiles.CaptureAsync(UserSaveFiles.Kind.MacServerLists, WorldDirectory, backup,
                    UserSaveFilesOnHost.Markers(Host, HostProfile), LocalMac ? null : UserSaveFilesOnHost.Remote(Host, HostedTimeouts.Quick),
                    entry => JournalAsync(Host, Role.Host, "server", entry, cancellation)).ConfigureAwait(false);
                await _macLists.ProtectAsync(entry => JournalAsync(Host, Role.Host, "server", entry, CancellationToken.None)).ConfigureAwait(false);
            }).ConfigureAwait(false);
        // A supplied manifest remains an expected pin. A standalone strict run may omit it and bind the profile listing below.
        bool verified = plan.Runtime.Sha256.Count != 0;
        if (Prepared)
        {
            // Reuse the preparation owner's listings. The before-boot pin row below is the only fresh, narrow check; listing the
            // source-backed game root here would recreate a broad rule and reject unrelated source output.
            await report.StepAsync(StepPhase.Setup, verified ? "reuse the verified prepared runtime listing" : "reuse the prepared runtime listing as found", () =>
            {
                var owner = _sourceCheck ?? throw new InvalidOperationException("The prepared server profile has no source-check owner.");
                _runtime = owner.PreparedGame;
                if (verified) HostInstall.RequireSame(plan.Runtime.Sha256, _runtime, "prepared runtime");
                _loader = owner.PreparedLoader;
                _preparedPluginFiles = ProfilePluginFiles(_loader);
                plan.RequireStagedPlugins();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            return;
        }
        await report.StepAsync(StepPhase.Setup, verified ? "check the server host's install against the pinned runtime and stage its profile"
            : "stage the server's profile and pin it as found",
            () => StageProfileAsync(report, plan, verified, cancellation)).ConfigureAwait(false);
    }

    /// <summary>
    /// A standalone run's server profile, made the way an owned local client's is (#730): the install's own BepInEx plugins,
    /// scripts, config and patchers (<see cref="InstallSelection"/>, read on the host and fetched here from another one) and the
    /// toolkit's pinned ValheimCLI set in place of any of the install's own, staged on the pinned loader (or the inventory recipe's
    /// reviewed package) by the one profile owner (<see cref="HostedRuntimeStage.PrepareProfileAsync"/>). The plan is checked
    /// against the install first: its runtime manifest (Steam's <c>logs/</c> left out, <see cref="HostedRuntimeStage.RuntimeSkips"/>)
    /// and its <see cref="ServerRunPlan.RuntimePins"/>; nothing is staged for an install that differs. Then the plan is bound to
    /// the profile, as a campaign's server is (<see cref="PreparedHostedCampaign.ApplyTo"/>): its runtime manifest to the profile's
    /// game listing, its runtime pins to the profile's (the loader is the profile's own), and its ValheimCLI pins to the staged
    /// set's. The profile is journalled before it is made, done once made, and retired at teardown (<see cref="TeardownAsync"/>).
    /// </summary>
    private async Task StageProfileAsync(ScenarioReport report, ServerRunPlan plan, bool verified, CancellationToken cancellation)
    {
        InstallPins? expectedRuntimePins = plan.RuntimePins;
        var pluginPinsFromPlan = plan.Pins.Where(pin => !Expectations.IsWorldKey(pin.Key))
            .ToDictionary(pin => pin.Key, pin => pin.Value, StringComparer.Ordinal);
        var bundle = _hooks.CliSet() ?? throw new InvalidDataException("This build of Valheim.Testing.GameSessions carries no pinned ValheimCLI bundle " +
            "(it was built without one); a standalone dedicated server's profile stages it in place of the install's own ValheimCLI.");
        var (cli, cliFiles, setFolder) = InstallSelection.ReadCliSet(bundle.Manifest);
        BepInExLoaderPackage? loader = null;
        if (_loaderPackage != null)
        {
            loader = BepInExLoaderPackage.Read(_loaderPackage);
            report.Provenance["serverLoaderPackage"] = loader.Identity;
        }
        else if (_hooks.PinnedLoader("server", Role.Install, HostProfile.Platform) is { } choice)
        {
            ShippedLoader.Print("server", choice);
            report.Provenance["serverLoaderShipped"] = choice.Reason;
            loader = BepInExLoaderPackage.Read(choice.Manifest);
        }
        using var selection = await InstallSelection.ReadAsync(Host, HostedRuntimeKind.Server, Role.Install, cli, cliFiles, loader, Role.CliPort,
            client: null, HostedTimeouts.Long, cancellation, Builds, "server").ConfigureAwait(false);
        var source = selection.Source;
        if (WorldBuild != null) WorldIdentity.RequireSameTarget(WorldBuild, selection.Build ?? new GameBuild(null, null, "the hosting install is unavailable"));

        if (verified)
            HostInstall.RequireSame(HostInstall.WithoutSkipped(plan.Runtime.Sha256, HostedRuntimeStage.RuntimeSkips, source.Names), source, "server install",
                source.Files.Keys.Where(path => HostInstall.Skipped(path, HostedRuntimeStage.RuntimeSkips, source.Names)));
        var bound = selection.BindPins("server", plan.RuntimePins, pluginPinsFromPlan, cli, setFolder);
        var stagedPins = bound.Plugins;
        foreach (var pin in bound.SourceCliPins) _sourceCliPins[pin.Key] = pin.Value;
        _replacedCliPaths = selection.Replaced.Order(StringComparer.Ordinal).ToArray();
        if (_replacedCliPaths.Count != 0)
        {
            string replaced = string.Join(", ", _replacedCliPaths);
            Console.WriteLine($"server: the profile stages ValheimCLI {cli.Build} in place of the install's own {replaced}.");
            report.Provenance["replacedValheimCliPaths"] = replaced;
        }
        if (_sourceCliPins.Count != 0)
            report.Provenance["sourceValheimCliPins"] = string.Join(" ", _sourceCliPins.Select(pin => pin.Key + "=" + pin.Value));
        // Journalled before the profile: an interrupted preparation leaves a record of every path it may own.
        await _runFolder.MakeAsync(cancellation).ConfigureAwait(false);
        await JournalAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", RuntimeDirectory), ("stage", StagingDirectory),
            ("parent", RunDirectory), ("runtimeKind", "server"),
            ("launchRoot", HostedRuntimeStage.LaunchRoot(HostProfile.Platform, HostedRuntimeKind.Server, Role.Install, RuntimeDirectory))), cancellation).ConfigureAwait(false);
        _runtimeIntended = true;
        HostedRuntimeStage.PreparedProfile profile;
        try
        {
            profile = await HostedRuntimeStage.PrepareProfileAsync(Host, HostedRuntimeKind.Server, Role.Install, RuntimeDirectory, StagingDirectory,
                selection.Files, HostedTimeouts.Long, cancellation, loader, source).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not AggregateException)
        {
            // The stage removed what it made (an unproven removal is an AggregateException: the teardown retires it, else env recover).
            _runtimeIntended = false;
            await NoteAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", RuntimeDirectory), ("failed", "true"))).ConfigureAwait(false);
            throw;
        }
        _runtime = profile.Game;
        _loader = profile.Loader;
        _sourceCheck = profile.SourceCheck;
        Role.PreparedSourceRoot = profile.Source.Root;
        if (profile.GameRoot != profile.LoaderRoot)
        {
            Role.PreparedGameRoot = profile.GameRoot;
            Role.PreparedLoaderRoot = profile.LoaderRoot;
        }
        if (HostedRuntimeStage.ProfileLeftOut.Describe(profile.LeftOut) is { } leftOut) report.Provenance["serverProfileLeftOut"] = leftOut;
        await JournalAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.CopyDone, ("runtime", RuntimeDirectory),
            ("files", profile.Loader.Files.Count.ToString(CultureInfo.InvariantCulture)), ("runtimeKind", "server"),
            ("launchRoot", profile.GameRoot)), cancellation).ConfigureAwait(false);
        // The baseline an interrupted run's recovery settles the install from, before the server launches; teardown retires the profile.
        await profile.SourceCheck.PersistAsync(Host, RuntimeDirectory, "server", entry => JournalAsync(Host, Role.Host, "server", entry, cancellation),
            HostedTimeouts.Long, cancellation).ConfigureAwait(false);
        plan.Runtime = new PinnedDirectory { Source = profile.Game.Root, Sha256 = new Dictionary<string, string>(profile.Game.Files, StringComparer.Ordinal) };
        plan.RuntimePins = _preparedPins = profile.Pins;
        plan.Pins = plan.Pins.Where(pin => Expectations.IsWorldKey(pin.Key)).Concat(stagedPins)
            .ToDictionary(pin => pin.Key, pin => pin.Value, StringComparer.Ordinal);
        _preparedPluginFiles = ProfilePluginFiles(profile.Loader);
        _pinOrigins["runtimeManifest"] = verified ? "plan" : "observed";
        _pinOrigins["runtimePins"] = expectedRuntimePins != null && expectedRuntimePins.SameAs(profile.Pins) ? "plan" : "observed";
        foreach (var (key, origin) in bound.PluginOrigins()) _pinOrigins[key] = origin;
        plan.RequireStagedPlugins();
    }

    private static Dictionary<string, string> ProfilePluginFiles(HostListing profile) => profile.Files
        .Where(file => HostedRuntimeStage.IsStartupPlugin(file.Key))
        .ToDictionary(file => file.Key, file => file.Value, profile.Names);

    /// <summary>For a crossplay plan: the server's <c>libparty.so</c> loads on the server host (<see cref="CrossplayLibraries"/>).</summary>
    public Task CheckCrossplayAsync(ScenarioReport report, ServerRunPlan plan, CancellationToken cancellation)
    {
        if (!plan.Crossplay) return Task.CompletedTask;
        if (Host.Shell.Kind == HostShellKind.PowerShell)
        {
            report.Step(StepPhase.Setup, "the Windows runtime has crossplay's native library", () =>
            {
                const string party = "valheim_server_Data/Plugins/x86_64/Party.dll";
                if (!(_runtime?.Files.ContainsKey(party) ?? false))
                    throw new FileNotFoundException($"The Windows server's profile on {Host.Name} has no {party}; crossplay cannot start.");
                report.Provenance["crossplayLibraries"] = party + " present on " + Host.Name + "; game startup verifies it loads";
            });
            return Task.CompletedTask;
        }
        return report.StepAsync(StepPhase.Setup, "the server host can load crossplay's libraries", async () =>
            report.Provenance["crossplayLibraries"] = await CrossplayLibraries.RequireAsync(Host,
                // The launch every boot builds (ServerActor.Launch), without the plan's arguments: libparty.so loads with its environment.
                ServerActor.Launch(this, [], new Dictionary<string, string>()),
                HostedTimeouts.Quick, cancellation).ConfigureAwait(false) + " loads on " + Host.Name);
    }

    /// <summary>Ships the verified local world copy to the host and verifies every file there.</summary>
    public Task ShipWorldAsync(ScenarioReport report, string localWorld, string output, CancellationToken cancellation) =>
        MacServer ? report.StepAsync(StepPhase.Setup, "place the pinned world in the Mac's default worlds", async () =>
        {
            await _runFolder.MakeAsync(cancellation).ConfigureAwait(false); // the world's stage and collection go in it
            // A server's world copy is its save root (ServerRunPlan.PreflightWorld; a campaign's prepared one too): HostedWorldOnHost
            // takes the contents of its worlds_local, with the named world at its root.
            string sourceWorlds = Path.Combine(localWorld, "worlds_local");
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
                _ => _lock != null ? Task.CompletedTask : throw new InvalidOperationException("Take the Mac server host's lock before placing its world."))
                { RetireRunCopies = RunId };
            _macWorld = HostedWorldOnHost.Place(site, fixture, Path.Combine(output, "mac-world-input"), cancellation);
        }) : report.StepAsync(StepPhase.Setup, "ship and verify the world copy on the server host", async () =>
        {
            await _runFolder.MakeAsync(cancellation).ConfigureAwait(false);
            var manifest = WorldFixture.Manifest(localWorld);
            // Journalled before the ship, like the profile: no journal line, no world copy on the host.
            await JournalAsync(Host, Role.Host, "server", WorldCopyIntended(), cancellation).ConfigureAwait(false);
            _world = WorldCopy.Intended;
            await Host.ShipFilesAsync(localWorld, WorldDirectory, HostedTimeouts.Long, cancellation).ConfigureAwait(false);
            _world = WorldCopy.Shipped;
            var listing = await HostInstall.ListAsync(Host, WorldDirectory, HostedTimeouts.Long, null, cancellation).ConfigureAwait(false);
            HostInstall.RequireSame(manifest, listing, "world copy", ["SOURCE.txt"]);
            // Only a verified copy is journalled done: env recover hands a done world over as the run's save, and removes any other.
            await JournalAsync(Host, Role.Host, "server", WorldCopyDone(listing), cancellation).ConfigureAwait(false);
            _world = WorldCopy.Verified;
        });

    // A copy in this run's own directory, journalled with what it holds: env recover removes it by these fields (no staging; its
    // parent is the run directory). Done only once verified, so a done copy is a whole one.
    private JournalEntry WorldCopyIntended() =>
        JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", WorldDirectory), ("stage", ""), ("parent", RunDirectory), ("holds", "world"), ("retention", "after-fetch"));
    private JournalEntry WorldCopyDone(HostListing listing) =>
        JournalEntry.Of(JournalEntry.CopyDone, ("runtime", WorldDirectory), ("files", listing.Files.Count.ToString(CultureInfo.InvariantCulture)), ("verified", "true"));

    // The caller is this placement's ServerActor after its exact clean stop. Intermediate checkpoints never release
    // the lock/profile or hand the live world over; final retirement stays in TeardownAsync.
    void IServerPlacement.ArchiveCheckpoint(string archive, CancellationToken cancellation)
    {
        RequireCheckpointOwnership();
        var before = HostInstall.ListAsync(Host, WorldDirectory, HostedTimeouts.Long, null, cancellation).GetAwaiter().GetResult();
        string destination = Path.Combine(archive, "world");
        Host.FetchDirectoryAsync(WorldDirectory, destination, HostedTimeouts.Long, cancellation).GetAwaiter().GetResult();
        var localHashes = before.Files.ToDictionary(pair => pair.Key.Replace('/', Path.DirectorySeparatorChar), pair => pair.Value, StringComparer.Ordinal);
        WorldFixture.Verify(destination, localHashes);
        var after = HostInstall.ListAsync(Host, WorldDirectory, HostedTimeouts.Long, null, cancellation).GetAwaiter().GetResult();
        HostInstall.RequireSame(before.Files, after, "stopped checkpoint world");
        File.WriteAllText(Path.Combine(archive, "world.sha256.json"), JsonSerializer.Serialize(localHashes));
        JournalAsync(Host, Role.Host, "server", JournalEntry.Of("checkpoint-world-archived",
            ("runtime", WorldDirectory), ("archive", destination)), cancellation).GetAwaiter().GetResult();
    }

    void IServerPlacement.RestoreCheckpoint(string original, IReadOnlyDictionary<string, string> hashes, string archive, CancellationToken cancellation)
    {
        RequireCheckpointOwnership();
        WorldFixture.Verify(original, hashes);
        var archived = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(archive, "world.sha256.json")))
            ?? throw new InvalidDataException("The checkpoint world has no archive manifest.");
        WorldFixture.Verify(Path.Combine(archive, "world"), archived);
        var stopped = HostInstall.ListAsync(Host, WorldDirectory, HostedTimeouts.Long, null, cancellation).GetAwaiter().GetResult();
        HostInstall.RequireSame(archived, stopped, "stopped world before restore");
        JournalAsync(Host, Role.Host, "server", JournalEntry.Of("checkpoint-world-restore-intended",
            ("runtime", WorldDirectory), ("source", original)), cancellation).GetAwaiter().GetResult();
        // The common protected-path remover is the only remover; a failed remove/ship remains recoverable in this run.
        HostedRuntimeStage.RetireAsync(Host, WorldDirectory, "", HostedTimeouts.Long, cancellation, RunId).GetAwaiter().GetResult();
        JournalAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", WorldDirectory)), cancellation).GetAwaiter().GetResult();
        _world = WorldCopy.None;
        JournalAsync(Host, Role.Host, "server", WorldCopyIntended(), cancellation).GetAwaiter().GetResult();
        _world = WorldCopy.Intended;
        Host.ShipFilesAsync(original, WorldDirectory, HostedTimeouts.Long, cancellation).GetAwaiter().GetResult();
        _world = WorldCopy.Shipped;
        var listing = HostInstall.ListAsync(Host, WorldDirectory, HostedTimeouts.Long, null, cancellation).GetAwaiter().GetResult();
        HostInstall.RequireSame(hashes, listing, "restored original fixture", ["SOURCE.txt"]);
        WorldFixture.Verify(original, hashes);
        JournalAsync(Host, Role.Host, "server", WorldCopyDone(listing), cancellation).GetAwaiter().GetResult();
        _world = WorldCopy.Verified;
    }

    void IServerPlacement.SwapCheckpointPlugin(string operation, string source, string oldSha256, string newSha256,
        Action admission, CancellationToken cancellation)
    {
        RequireCheckpointOwnership();
        if (!Guid.TryParseExact(operation, "N", out _) || _sourceCheck == null || _preparedPins == null || _preparedPluginFiles == null)
            throw new InvalidOperationException("A stopped swap requires its prepared profile checks.");
        HostInstall.CheckProfilePinsAsync(Host, _preparedPins, GameDirectory, LoaderDirectory, _sourceCheck,
            "stopped profile before swap", HostedTimeouts.Long, cancellation, _preparedPluginFiles).GetAwaiter().GetResult();
        var matches = _preparedPluginFiles.Where(file => file.Value == oldSha256).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The swap must identify exactly one staged plugin.");
        string relative = matches[0].Key;
        StoppedPluginSwap.Apply(LoaderDirectory, relative, source, oldSha256, newSha256, operation,
            () => { RequireCheckpointOwnership(); admission(); }, entry =>
                JournalAsync(Host, Role.Host, "server", entry, cancellation).GetAwaiter().GetResult(), cancellation);
        _sourceCheck.ReplacePreparedPlugin(relative, oldSha256, newSha256);
        _loader = _sourceCheck.PreparedLoader; _runtime = _sourceCheck.PreparedGame;
        _preparedPluginFiles = new Dictionary<string, string>(_preparedPluginFiles, _loader.Names) { [relative] = newSha256 };
        JournalAsync(Host, Role.Host, "server", JournalEntry.Of("checkpoint-plugin-swap-completed",
            ("operation", operation), ("source", source), ("target", Path.Combine(LoaderDirectory, relative)), ("after", newSha256)), cancellation).GetAwaiter().GetResult();
    }

    private void RequireCheckpointOwnership()
    {
        if (HostProfile is not { Kind: "local", Platform: "windows" } || _lock == null || _serverMayRun || _world != WorldCopy.Verified)
            throw new InvalidOperationException("Detached checkpoints require this run's locked, verified local Windows server placement.");
    }

    private static InstallPins RequiredRuntimePins(ServerRunPlan plan) => plan.RuntimePins ??
        throw new InvalidOperationException("The standalone profile was not bound to its observed runtime pins.");

    /// <summary>The server's executable and its pins, on the listings of its profile and its game, before anything launches.</summary>
    public void CheckRuntime(ScenarioReport report, ServerRunPlan plan)
    {
        var runtime = _runtime ?? throw new InvalidOperationException("Ready the server's profile first.");
        // Hashes do not cover file modes: a launch also requires the game's execute bit.
        report.Step(StepPhase.Setup, "server runtime has the plan's server executable", () =>
        {
            var platform = HostInstall.DetectServer(runtime);
            plan.CheckExecutable(platform);
            ServerRunPlan.CheckLaunchHost(platform, HostProfile.Platform switch { "windows" => ServerPlatform.Windows, "macos" => ServerPlatform.MacOS, _ => ServerPlatform.Linux });
            string? unixExecutable = platform switch { ServerPlatform.Linux => GameLaunch.ServerLinuxExecutable, ServerPlatform.MacOS => GameLaunch.ServerMacExecutable, _ => null };
            if (unixExecutable != null && !runtime.Executables.Contains(unixExecutable))
                throw new InvalidOperationException($"{unixExecutable} is not executable in {runtime.Root} on {Host.Name}; restore its mode (chmod u+x) in the install {Role.PreparedSourceRoot ?? Role.Install}.");
        });
        report.Step(StepPhase.Setup, "server runtime is the pinned game build, loader and patchers", () =>
            HostInstall.CheckPins(RequiredRuntimePins(plan), runtime, _loader ?? runtime, "runtime").Record(report.Provenance, "runtime"));
    }

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
    ServerPlatform? IServerPlacement.Platform => LocalMac ? null : RemoteMac ? ServerPlatform.MacOS
        : Host.Shell.Kind == HostShellKind.PowerShell ? ServerPlatform.Windows : ServerPlatform.Linux;

    ServerBoot IServerPlacement.Start(int n, GameLaunch launch, string output, TimeSpan quit, CancellationToken cancellation)
    {
        if (_sourceCheck != null && _preparedPins != null)
        {
            HostInstall.CheckProfilePinsAsync(Host, _preparedPins, GameDirectory, LoaderDirectory, _sourceCheck,
                "prepared server profile before launch", HostedTimeouts.Long, cancellation,
                _preparedPluginFiles).GetAwaiter().GetResult();
        }
        // A new boot has no command capability until it passes startup verification; the preceding boot's is retired now.
        _leaseCandidate = null;
        RetireServerCommandLease(output);
        string local = Path.Combine(output, "boot-" + n), bootDirectory = HostPath.Join(RunDirectory, "boot-" + n);
        // The placement owns every boot directory before the launcher can create it. A cut or failed fetch stays visible.
        _runFolder.MakeAsync(cancellation).GetAwaiter().GetResult();
        JournalAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", bootDirectory),
            ("stage", ""), ("parent", RunDirectory), ("holds", "server-boot"), ("retention", "after-fetch")), cancellation).GetAwaiter().GetResult();
        var evidence = new BootEvidence(bootDirectory, local);
        _boots.Add(evidence);
        // The stop's quit budget, journalled so env recover gives the server the same time to save (#665).
        string quitSeconds = WaitText.WholeSeconds(quit);
        if (LocalMac) return StartLocalMac(launch, output, local, bootDirectory, quitSeconds, cancellation);
        HostServerProcess process;
        // Journalled before the start: a run interrupted from here leaves a record of where its server's pid file is, with the
        // command line the server will have, so its pid file alone proves it the run's (#257). It also names the launcher's
        // ownership file (and Windows start task), which the launcher writes before the game exists: a start killed before the
        // pid file was published is still named by it (#800).
        string expected = launch.CommandLineSha256();
        var ownership = HostServerLaunchOwnership.Create(launch.Platform);
        JournalAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.ProcessIntended, ("bootDirectory", bootDirectory),
            ("expectedCommandLineSha256", expected), ("quitSeconds", quitSeconds),
            ("launcherOwnershipFile", ownership.FileName), ("launcherTask", ownership.TaskName)), cancellation).GetAwaiter().GetResult();
        try { process = HostServer.StartAsync(Host, launch, bootDirectory, HostedTimeouts.Quick, [BepInExLog, GameLaunch.UnityLogName], local,
            logonSeams: null, cancellation: cancellation, ownership: ownership).GetAwaiter().GetResult(); }
        catch (Exception error) when (UnknownOutcome(error) != null)
        {
            // The start's reply was lost: a server may be running there that no session knows. The lock stays.
            _serverMayRun = true;
            throw;
        }
        evidence.Process = process;
        // The server writes into boot-N/ on every platform, including Windows after the short-lived launch task exits.
        var logs = new List<RunLog> { new($"boot-{n} BepInEx log", Path.Combine(local, "game-0.log"), Required: true), new($"boot-{n} Unity log", Path.Combine(local, "game-1.log")) };
        logs.Add(new($"boot-{n} stdout", Path.Combine(local, "stdout.log")));
        logs.Add(new($"boot-{n} stderr", Path.Combine(local, "stderr.log")));
        return new ServerBoot(process, logs,
            new()
            {
                ["pid"] = process.Id, ["startIdentity"] = process.StartIdentity, ["host"] = Host.Name, ["bootDirectory"] = bootDirectory,
                ["startedUtc"] = DateTime.UtcNow, ["world"] = WorldDirectory, ["taskLogon"] = process.TaskLogon,
            },
            () =>
            {
                // The command line's hash is the third fact env recover requires before it stops the process (#257 Q2).
                var found = HostProcessProbe.StartedProcessAsync(Host, process.Id, process.StartIdentity, HostedTimeouts.Quick).GetAwaiter().GetResult();
                string commandLine = found?.CommandLineSha256 ?? "";
                WarnUnexpectedCommandLine(Host, process.Id, expected, commandLine);
                NoteAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.ProcessStarted, ("pid", process.Id.ToString(CultureInfo.InvariantCulture)),
                    ("startIdentity", process.StartIdentity), ("commandLineSha256", commandLine), ("bootDirectory", bootDirectory),
                    ("taskLogon", process.TaskLogon ?? ""), ("quitSeconds", quitSeconds))).GetAwaiter().GetResult();
                _leaseCandidate = (process.Id, process.StartIdentity, found);
            });
    }

    // The macOS server is local to the runner. Its launch must come from GameLaunch.ToStartInfo: that path carries the
    // native architecture and both DYLD variables through /usr/bin/arch, which a host-shell launch would strip.
    private ServerBoot StartLocalMac(GameLaunch launch, string output, string local, string bootDirectory, string quitSeconds, CancellationToken cancellation)
    {
        string expected = launch.CommandLineSha256();
        JournalAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.ProcessIntended, ("bootDirectory", bootDirectory),
            ("expectedCommandLineSha256", expected), ("quitSeconds", quitSeconds)), cancellation).GetAwaiter().GetResult();
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(bootDirectory);
        foreach (var (relative, index) in new[] { (BepInExLog, 0), (GameLaunch.UnityLogName, 1) })
        {
            string previous = Path.Combine(RuntimeDirectory, relative);
            if (File.Exists(previous)) File.Move(previous, Path.Combine(bootDirectory, $"previous-{index}.log"));
        }
        string prefix = Path.Combine(local, "game");
        DirectServerProcess? process = null;
        try
        {
            process = new DirectServerProcess(launch.ToStartInfo(), prefix,
                Path.Combine(RuntimeDirectory, BepInExLog), Path.Combine(RuntimeDirectory, GameLaunch.UnityLogName));
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
                        ("commandLineSha256", found.CommandLineSha256), ("bootDirectory", bootDirectory), ("quitSeconds", quitSeconds))).GetAwaiter().GetResult();
                    _leaseCandidate = (process.Id, found.StartIdentity, found);
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

    // The lease is an optional diagnostic capability: no failure to retire, verify or write it ever fails a boot. Each one
    // warns and leaves no lease, so a separate cli refuses instead.
    private void RetireServerCommandLease(string output)
    {
        if (HostProfile.Kind != "local") return;
        try { OwnedClientCommandLease.RetireServer(output); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Warning: the owned server's previous command lease could not be retired: " + error.Message); }
    }

    internal void WriteServerCommandLease(string output, int pid, string startIdentity, ProbedProcess? found)
    {
        if (HostProfile.Kind != "local") return; // A lease is usable only by another invocation on this machine.
        // A restart or a failed refresh must not leave the preceding boot's live capability behind.
        RetireServerCommandLease(output);
        // A PID which is gone or belongs to another process is no longer this launch's capability. In particular, never
        // adopt the current occupant's identity: the launch-recorded pair is the only one this run may publish.
        if (found?.State is ProbedState.Gone or ProbedState.Reused) return;
        if (found?.State != ProbedState.Same || found.StartIdentity != startIdentity)
        {
            Console.Error.WriteLine($"Warning: the owned server process {pid} on {Host.Name} could not be verified before its command lease was written " +
                $"({found?.State.ToString() ?? "probe failed"}); no server command lease was published.");
            return;
        }

        if (LocalLeaseIdentity.TryRead(_hooks, "server", pid, startIdentity) is not { } leaseIdentity) return;

        ProbedProcess confirmed;
        try
        {
            confirmed = HostProcessProbe.ProbeAsync(Host, [(pid, startIdentity)], HostedTimeouts.Quick)
                .GetAwaiter().GetResult()[(pid, startIdentity)];
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Warning: the owned server process {pid} on {Host.Name} could not be rechecked before its command lease was written: " +
                $"{error.Message}; no server command lease was published.");
            return;
        }
        if (confirmed.State != ProbedState.Same || confirmed.StartIdentity != startIdentity)
        {
            Console.Error.WriteLine($"Warning: the owned server process {pid} on {Host.Name} changed while its command lease was prepared " +
                $"({confirmed.State}); no server command lease was published.");
            return;
        }
        try { OwnedClientCommandLease.WriteServer(output, pid, leaseIdentity, Role.CliPort, RuntimeDirectory, _plan); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Warning: the owned server's command lease could not be written: {error.Message}; no server command lease was published.");
            RetireServerCommandLease(output); // Never leave half a capability.
        }
    }

    // ServerActor calls these only after Start or Restart returned: after the read-only bootstrap and strict verification.
    void IServerPlacement.PublishCommandLease(string output)
    {
        if (_leaseCandidate is { } boot) WriteServerCommandLease(output, boot.Pid, boot.StartIdentity, boot.Found);
    }

    void IServerPlacement.PublishVerifiedWorld(string worldUid, string output)
    {
        if (HostProfile.Kind != "local") return;
        try { OwnedClientCommandLease.WriteVerifiedServerWorld(output, _plan, worldUid); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine($"Warning: the owned server's world command pins could not be written: {error.Message}; world-phase commands stay refused."); }
    }
    bool IServerPlacement.PublishesCommandLease => HostProfile.Kind == "local";

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
    /// After the owned server stopped: fetches the host's world copy, retires the server's profile (<see cref="RunRetirement"/>,
    /// keeping what the run changed in it; a standalone profile whose staging failed part-way goes whole),
    /// fetches and removes the run's folder on the server host and each client host (<see cref="RunFolder"/>), closes the tunnel,
    /// releases the clients' accounts, retires <paramref name="prepared"/>'s characters and installs under the locks this run
    /// holds, and releases the locks, each as its own step. With <paramref name="serverStopped"/> false
    /// the server host's lock and runtime are kept, because the server may still run there. Returns the failures; it never throws.
    /// </summary>
    public async Task<IReadOnlyList<Exception>> TeardownAsync(ScenarioReport report, string output, bool launched, bool serverStopped,
        PreparedHostedCampaign? prepared = null, RunRetirement? retirement = null, CancellationToken cleanup = default)
    {
        retirement ??= new RunRetirement(report, output);
        serverStopped &= !_serverMayRun;
        var failures = new List<Exception>();
        if (HostProfile.Kind == "local")
            try { OwnedClientCommandLease.RetireServer(output); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { failures.Add(error); Console.Error.WriteLine("Teardown: " + error.Message); }
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
                await Try("restore the Mac server's user-level access lists", () => _macLists.RestoreAsync(entry => JournalAsync(Host, Role.Host, "server", entry, CancellationToken.None))).ConfigureAwait(false);
                if (failures.Count != before) serverStopped = false; // the lock and backup stay for env recover
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
        if (_runtime != null)
        {
            SourceVerdict? settled = null;
            if (!Prepared && _sourceCheck != null)
            {
                // The install settled before the profile goes (its run folder holds the baseline and the moved files).
                var verdict = serverStopped
                    ? await _sourceCheck.SettleAsync(Host, SourceSettlement.Run("server", RuntimeDirectory, output),
                        entry => JournalAsync(Host, Role.Host, "server", entry, cleanup), HostedTimeouts.Long, cleanup).ConfigureAwait(false)
                    : SourceVerdict.Unknown("server", "the owned server was not proven stopped");
                // A verdict of a stop that was not proven is the run's unknown outcome already; any other failing verdict is a failure.
                try { verdict.Record(report); }
                catch (IOException error) when (serverStopped) { failures.Add(error); Console.Error.WriteLine("Teardown: " + error.Message); }
                catch (IOException) { }
                // A settlement that did not finish keeps the profile, journalled kept by the settlement, so the run folder stays too.
                settled = verdict;
            }
            try
            {
                settled?.RequireRetirable();
                await retirement.HostAsync(Role.Host, Host, _loader ?? _runtime, RuntimeDirectory, launched, serverStopped, cleanup).ConfigureAwait(false);
                // A campaign's prepared profile is journalled by its preparation; a standalone run's own profile here. A failed retire
                // journals nothing more: the profile stays open in the journal, for env recover.
                if (!Prepared)
                    await NoteAsync(Host, Role.Host, "server", retirement.Kept(Role.Host, RuntimeDirectory)
                        ? JournalEntry.Of(JournalEntry.CopyKept, ("runtime", RuntimeDirectory), ("why", RunRetirement.KeepRequested ? RunRetirement.KeptOnRequest : "its server may still run"))
                        : JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", RuntimeDirectory))).ConfigureAwait(false);
            }
            catch (Exception error) { failures.Add(error); Console.Error.WriteLine("Teardown: " + error.Message); }
        }
        else if (_runtimeIntended)
            // A profile whose staging failed and whose own cleanup was not proven (a lost reply): nothing ran from it, so all of it
            // goes, its staging folder too. A failed retire leaves it open in the journal, for env recover.
            await Try("remove the server's partial profile on the server host", async () =>
            {
                await HostedRuntimeStage.RetireAsync(Host, RuntimeDirectory, StagingDirectory, HostedTimeouts.Quick, cleanup, RunId).ConfigureAwait(false);
                await NoteAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", RuntimeDirectory))).ConfigureAwait(false);
            }).ConfigureAwait(false);
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
                await Try("remove the fetched world copy on the server host", () => RetireEvidenceAsync(WorldDirectory, worldFetched, cleanup)).ConfigureAwait(false);
        }
        foreach (var boot in _boots)
        {
            if (cleanup.IsCancellationRequested) break; // its intended entry remains open
            bool fetched = boot.Process?.EvidenceFetched == true;
            if (LocalMac && serverStopped)
            {
                // The direct Mac process writes its current logs into output already. Preserve the host's pid and previous
                // logs beside them, through the same verified fetch, before removing its separate boot directory.
                int before = failures.Count;
                await Try("fetch the Mac server's boot files", () => Host.FetchDirectoryAsync(boot.Directory,
                    Path.Combine(boot.Output, "host-files"), HostedTimeouts.Long, cleanup)).ConfigureAwait(false);
                fetched = failures.Count == before;
            }
            if (!serverStopped || !fetched)
                await NoteAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.CopyKept, ("runtime", boot.Directory),
                    ("why", !serverStopped ? "its server may still run" : "its fetch was not proven, so its boot evidence stays"))).ConfigureAwait(false);
            else
                await Try("remove the fetched boot files on the server host", async () =>
                {
                    // ProcessStarted is best-effort. Settle the launch durably after the exact stop before removing its pid/owner
                    // files, or a crash before RunEnded could leave an unresolvable ProcessIntended with no ownership files.
                    await JournalAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.LaunchSettled,
                        ("actor", "server"), ("directory", boot.Directory)), cleanup).ConfigureAwait(false);
                    await RetireEvidenceAsync(boot.Directory, true, cleanup).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        // What is left in the run's folder on the server host goes whole once fetched; it stays, journalled, while the server may
        // still run, on request, or while anything the run journalled in it is not settled (a copy kept above).
        if (_runFolder.Made && !cleanup.IsCancellationRequested)
            await Try("fetch and remove the server host's run folder", () => _runFolder.SettleAsync(_runFolder.LocalDirectory(output),
                !serverStopped ? "its server may still run" : RunRetirement.KeepRequested ? RunRetirement.KeptOnRequest : null, cleanup)).ConfigureAwait(false);
        if (_tunnel != null) await Try("close the CLI tunnel", () => { _tunnel.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
        await _clientActors.ReleaseAccountsAsync(Try).ConfigureAwait(false);
        _clientActors.RecordInPlaceSourceVerdicts(report);
        // Every process is stopped (or named as possibly running), so the campaign's characters and prepared installs go, under the
        // locks held here; a host whose lock this run never took (a client that never opened) is locked for its retire.
        if (prepared != null)
        {
            var locked = _clientActors.LockedHosts.ToList();
            if (_lock != null) locked.Add(Host.Name);
            // A server that may still run kept its own copy above; the process check below only looks at the copies retired.
            foreach (var failure in await retirement.CampaignAsync(prepared, locked, cleanup,
                _clientActors.PendingMacAgentHosts).ConfigureAwait(false))
            { failures.Add(failure); Console.Error.WriteLine("Teardown: " + failure.Message); }
        }
        await _clientActors.RetireRunFoldersAsync(output, Try, cleanup).ConfigureAwait(false);
        await _clientActors.ReleaseLocksAsync(Try).ConfigureAwait(false);
        if (_lock != null)
            await Try("release the server host's lock", () => serverStopped && !_clientActors.HasPendingMacAgent(Role.Host)
                ? _lock.ReleaseAsync()
                : throw new HostLockException(new HostLockResult(HostLockState.Unknown, _lock.Owner,
                    $"Kept {_lock.Path} on {Host.Name}: the owned server or a Mac GUI client agent there may still run. Use env recover after it has stopped."))).ConfigureAwait(false);
        return failures;
    }

    // The fetch receipt must be durable before deletion: if deletion or its acknowledgement fails, recovery can finish it.
    // A journal failure prevents deletion, unlike the best-effort note after the effect. The host lock is still held here.
    private async Task RetireEvidenceAsync(string directory, bool fetched, CancellationToken cleanup)
    {
        await JournalAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.CopyDone, ("runtime", directory),
            ("fetched", fetched ? "true" : "false")), cleanup).ConfigureAwait(false);
        await HostedRuntimeStage.RetireAsync(Host, directory, "", HostedTimeouts.Quick, cleanup, RunId).ConfigureAwait(false);
        await NoteAsync(Host, Role.Host, "server", JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", directory))).ConfigureAwait(false);
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
/// keeps its BepInEx log and Unity's log (the launch's <see cref="GameLaunch.UnityLog"/>) in its launch directory, fetches that
/// directory here and closes its CLI tunnel.
/// </summary>
internal class HostedClientProcess(InteractiveClientProcess process, IGameHost host, string install, string unityLog, CliTunnel tunnel, string evidence,
    Action? afterStopped = null) : IOwnedProcess, IClientProcessIdentity
{
    /// <summary>
    /// The one choice of a started hosted client's process: with <paramref name="local"/> (its process is on this machine) a
    /// <see cref="LocalHostedClientProcess"/> carrying the local lease identity, so its <see cref="ClientSession"/> publishes the
    /// command lease once the menu pins verify; otherwise, or when that identity cannot be read, one that publishes none. A launch
    /// that exits before its menu fails startup, so a lease never names a PID reused in between.
    /// </summary>
    internal static HostedClientProcess Of(InteractiveClientProcess process, IGameHost host, string install, string unityLog, CliTunnel tunnel, string evidence,
        IHostedRunHooks? local, Action? afterStopped = null) =>
        local != null && LocalLeaseIdentity.TryRead(local, "client", process.Id, process.StartIdentity) is { } identity
            ? new LocalHostedClientProcess(process, host, install, unityLog, tunnel, evidence, identity, afterStopped)
            : new HostedClientProcess(process, host, install, unityLog, tunnel, evidence, afterStopped);

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
            afterStopped?.Invoke();
            if (_kept) return;
            var kept = host.RunAsync(HostedClientScripts.Keep(host.Shell.Kind), new Dictionary<string, string>
                { ["install"] = install, ["dir"] = process.LaunchDirectory, ["unitylog"] = unityLog },
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

// A client hosted through the same scripts as a remote client (a desktop task, or a campaign client on a `local` host) whose
// process is on this machine (HostedClientProcess.Of). The distinct identity type is the one rule ClientSession uses when
// deciding whether it may publish a local lease; its identity is the lease's local rule (LocalLeaseIdentity).
internal sealed class LocalHostedClientProcess(InteractiveClientProcess process, IGameHost host, string install, string unityLog, CliTunnel tunnel, string evidence,
    string localStartFileTimeUtc, Action? afterStopped = null) : HostedClientProcess(process, host, install, unityLog, tunnel, evidence, afterStopped), ILocalClientProcessIdentity
{
    string IClientProcessIdentity.StartFileTimeUtc => localStartFileTimeUtc;
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

    // Variables: install (the loader root), dir, unitylog (the launch's -logFile, GameLaunch.UnityLog). The game's BepInEx log and
    // Unity's log are copied into the launch directory as game-0.log and game-1.log, or an .absent note says the game never wrote one.
    public static readonly string BashKeep = """
        set -u
        [ -d "$dir" ] || exit 3
        keep() { if [ -f "$1" ]; then cp -- "$1" "$dir/$2" || exit 3; else printf 'The game did not write %s\n' "$1" > "$dir/$2.absent" || exit 3; fi; }
        keep "$install/BepInEx/LogOutput.log" game-0.log
        keep "$unitylog" game-1.log
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
        Save-VtLog $unitylog 'game-1.log'
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
    // Windows counterpart of Retire. Only this run's own runtime copy may be removed, and a retained file must be a regular
    // file that lies inside it once links are resolved (ProtectedPaths.PowerShellInside). Variables: runtime, keep, run, files, perfile, total.
    public static readonly string WindowsRetire = ProtectedPaths.PowerShellInside + ProtectedPaths.PowerShellRemove + """
        if (-not $run -or $run -match '[\\/]' -or $run -eq '.' -or $run -eq '..') { exit 3 }
        $parent = [IO.Path]::GetDirectoryName($runtime)
        if ([IO.Path]::GetFileName($runtime) -cne 'runtime' -or [IO.Path]::GetFileName($parent) -cne $run -or
            [IO.Path]::GetFileName($keep) -cne 'runtime-changes' -or [IO.Path]::GetDirectoryName($keep) -cne $parent) { exit 3 }
        if ([IO.Directory]::Exists($keep) -or [IO.File]::Exists($keep)) { exit 3 }
        [void][IO.Directory]::CreateDirectory($keep)
        # A runtime that is a link (dangling or to a file too) keeps nothing: it is unlinked, never entered, and its target stays.
        if (Test-VtLink $runtime) { Remove-VtTree $runtime $parent; 'VT-RETIRED 0 0'; exit 0 }
        if (-not [IO.Directory]::Exists($runtime)) { 'VT-RETIRED 0 0'; exit 0 }
        $real = Get-VtPhysical $runtime # the copy's physical path; each kept file must resolve below it
        $kept = [long]0
        foreach ($line in ($files -split "`n")) {
            if (-not $line) { continue }
            $relative = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line))
            if ([IO.Path]::IsPathRooted($relative) -or $relative.Split([char[]]'\/') -contains '..') { 'VT-NOTKEPT ' + $line + ' -1'; continue }
            $source = [IO.Path]::GetFullPath((Join-Path $runtime $relative))
            if (-not [IO.File]::Exists($source) -or ([IO.File]::GetAttributes($source) -band [IO.FileAttributes]::ReparsePoint)) { 'VT-NOTKEPT ' + $line + ' -1'; continue }
            # Never through a link that leaves the copy: the file's physical path must be inside the copy too. Unknown is outside.
            $inside = $false
            try { $inside = Test-VtInside $real $source } catch { }
            if (-not $inside) { 'VT-NOTKEPT ' + $line + ' -1'; continue }
            $size = (New-Object IO.FileInfo $source).Length
            if ($size -gt [long]$perfile -or $kept + $size -gt [long]$total) { 'VT-NOTKEPT ' + $line + ' ' + $size; continue }
            $target = Join-Path $keep $relative
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.File]::Copy($source, $target)
            $kept += $size
        }
        # The copy's size and its removal never pass through a link in it (as du and rm -rf on a bash host): a link counts nothing
        # and is removed as a link first, so the recursive delete meets no junction and never follows one.
        $bytes = [long]0
        $links = @()
        $walk = New-Object 'Collections.Generic.Stack[string]'
        $walk.Push($runtime)
        while ($walk.Count -gt 0) {
            foreach ($entry in [IO.Directory]::GetFileSystemEntries($walk.Pop())) {
                $attributes = [IO.File]::GetAttributes($entry)
                if ($attributes -band [IO.FileAttributes]::ReparsePoint) { $links += , @($entry, [bool]($attributes -band [IO.FileAttributes]::Directory)) }
                elseif ($attributes -band [IO.FileAttributes]::Directory) { $walk.Push($entry) }
                else { $bytes += (New-Object IO.FileInfo $entry).Length }
            }
        }
        foreach ($link in $links) { if ($link[1]) { [IO.Directory]::Delete($link[0]) } else { [IO.File]::Delete($link[0]) } }
        Remove-VtTree $runtime $parent
        'VT-RETIRED ' + $bytes + ' ' + $kept
        """.ReplaceLineEndings("\n");

    public static readonly string WindowsDropKept = ProtectedPaths.PowerShellInside + ProtectedPaths.PowerShellRemove + """
        if (-not $run -or $run -match '[\\/]' -or $run -eq '.' -or $run -eq '..') { exit 3 }
        if ([IO.Path]::GetFileName($keep) -cne 'runtime-changes' -or [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($keep)) -cne $run) { exit 3 }
        Remove-VtTree $keep ([IO.Path]::GetDirectoryName($keep))
        'VT-DROPPED'
        """.ReplaceLineEndings("\n");

    // Keeps the listed files (base64 relative paths, one per line) from a run's runtime copy in $keep, within the size
    // limits, then removes the copy. Only a directory named runtime directly inside the run's own directory ($run) is
    // ever removed; a path that leaves the copy, a link or anything but a regular file is listed, never copied.
    public static readonly string Retire = "set -u\n" + ProtectedPaths.BashInside + ProtectedPaths.BashRemove + """
        case "$run" in ''|*/*|.|..) exit 3;; esac
        [ "$(basename -- "$runtime")" = runtime ] || exit 3
        [ "$(basename -- "$(dirname -- "$runtime")")" = "$run" ] || exit 3
        [ "$(basename -- "$keep")" = runtime-changes ] || exit 3
        [ "$(dirname -- "$keep")" = "$(dirname -- "$runtime")" ] || exit 3
        # A reused or linked destination could overwrite evidence outside this run. Leave the runtime for review instead.
        [ ! -e "$keep" ] && [ ! -L "$keep" ] || exit 3
        # A runtime that is a link (or not a folder) keeps nothing: a link is unlinked, never entered, and its target stays.
        if [ ! -d "$runtime" ] || [ -L "$runtime" ]; then
            mkdir -p -- "$keep" || exit 3
            # The root is the run's own directory (checked above), so what vt_remove adds here is the link handling, not a wider guard.
            if [ -L "$runtime" ]; then vt_remove "$runtime" "$(dirname -- "$runtime")" || exit 3; fi
            echo "VT-RETIRED 0 0"; exit 0
        fi
        mkdir -p -- "$keep" || exit 3
        real=$(vt_physical "$runtime") || exit 3 # the copy's physical path; each kept file must resolve below it
        kept=0
        while IFS= read -r line; do
            [ -n "$line" ] || continue
            rel=$(printf '%s' "$line" | base64 -d) || exit 3
            case "$rel" in /*|..|../*|*/../*|*/..) printf 'VT-NOTKEPT %s -1\n' "$line"; continue;; esac
            src="$runtime/$rel"
            if [ ! -f "$src" ] || [ -L "$src" ]; then printf 'VT-NOTKEPT %s -1\n' "$line"; continue; fi
            # Never through a link that leaves the copy: the file's physical path must be inside the copy too (ProtectedPaths.BashInside).
            vt_inside "$real" "$src" || { printf 'VT-NOTKEPT %s -1\n' "$line"; continue; }
            if [ "$vt_darwin" = 1 ]; then size=$(stat -f %z -- "$src") || exit 3
            else size=$(stat -c %s -- "$src") || exit 3; fi
            if [ "$size" -gt "$perfile" ] || [ $((kept + size)) -gt "$total" ]; then printf 'VT-NOTKEPT %s %s\n' "$line" "$size"; continue; fi
            mkdir -p -- "$(dirname -- "$keep/$rel")" && cp -p -- "$src" "$keep/$rel" || exit 3
            kept=$((kept + size))
        done <<< "$files"
        if [ "$vt_darwin" = 1 ]; then bytes=$(du -sk -- "$runtime") || exit 3
        else bytes=$(du -sb -- "$runtime") || exit 3; fi
        bytes=${bytes%%$'\t'*}
        if [ "$vt_darwin" = 1 ]; then bytes=$((bytes * 1024)); fi
        vt_remove "$runtime" "$(dirname -- "$runtime")" || exit 3
        printf 'VT-RETIRED %s %s\n' "$bytes" "$kept"
        """.ReplaceLineEndings("\n");

    // Removes the run's runtime-changes directory on the host once it was fetched; only <run>/runtime-changes.
    public static readonly string DropKept = "set -u\n" + ProtectedPaths.BashInside + ProtectedPaths.BashRemove + """
        case "$run" in ''|*/*|.|..) exit 3;; esac
        [ "$(basename -- "$keep")" = runtime-changes ] || exit 3
        [ "$(basename -- "$(dirname -- "$keep")")" = "$run" ] || exit 3
        vt_remove "$keep" "$(dirname -- "$keep")" || exit 3
        echo "VT-DROPPED"
        """.ReplaceLineEndings("\n");
}
