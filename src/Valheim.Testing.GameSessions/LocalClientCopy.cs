using valheim_cli.Testing;
using System.Globalization;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// One owned client's disposable loader profile on this machine (#296): the default for every owned client a run opens here
/// (unless it runs in place), so the user's install never changes. It is made the way a session prepares a client on a host,
/// by the same stage owner (<see cref="HostedRuntimeStage.PrepareProfileAsync"/> on this machine as a <see cref="LocalGameHost"/>):
/// the install's BepInEx plugins, scripts, config and patchers (<see cref="InstallSelection"/>) are staged into the profile without
/// any ValheimCLI build of its own, and with the ValheimCLI set the run checks against (the plan's <see cref="ClientRunPlan.CliManifest"/>, else the toolkit's
/// pinned bundle); a Windows profile runs on the pinned BepInExPack, never the install's loader (<see cref="ShippedLoader"/>); a macOS
/// source bundle is checked so macOS opens it without a dialog. The profile is journalled before it is made (this machine's run
/// journal, so <c>valheim-test env status</c> and <c>env recover</c> see it) and retired after the client closes. The plan is
/// bound to the profile: its game and loader roots, install pins, the staged set's plugin pins and manifest (<see cref="ClientRunPlan.Prepared"/>).
/// </summary>
internal sealed class LocalClientCopy
{
    /// <summary>The longest any one step may take; listing and hashing a 4 GB install takes minutes on a slow disk.</summary>
    internal static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(30);

    private readonly IGameHost _host;
    private readonly string _actor, _runtime, _stage, _launchRoot;
    private readonly RunJournal _journal;
    private readonly ClientRunPlan _plan;
    private readonly Unbound _unbound;
    private readonly ProfileSourceCheck _sourceCheck;
    private int _retired;
    private SourceVerdict? _sourceVerdict;

    // The plan as it was before it was bound, restored when the profile is retired: a plan reused later makes a new profile.
    private sealed record Unbound(string Install, InstallPins? InstallPins, Dictionary<string, string> Pins, string? CliManifest,
        string? LoaderRoot, string? LaunchMode, string? SourceGameRoot, string? SourceGameHash, string? LeftOut,
        SortedDictionary<string, string> PinOrigins, Dictionary<string, string> SourceCliPins, IReadOnlyList<string> ReplacedCliPaths,
        Action? SourceCheck, GameBuild? Build);

    private LocalClientCopy(IGameHost host, string actor, string runtime, string stage, string launchRoot, RunJournal journal,
        ClientRunPlan plan, Unbound unbound, ProfileSourceCheck sourceCheck)
    { _host = host; _actor = actor; _runtime = runtime; _stage = stage; _launchRoot = launchRoot; _journal = journal; _plan = plan; _unbound = unbound; _sourceCheck = sourceCheck; }

    /// <summary>The owned folder retired with this client's profile.</summary>
    internal string Runtime => _runtime;
    /// <summary>The game root used to prove the launched process has stopped before retiring the owned folder.</summary>
    internal string LaunchRoot => _launchRoot;
    /// <summary>The client's name in the journal and in its source verdict.</summary>
    internal string Actor => _actor;
    /// <summary>The actor's own evidence folder: where its source settlement's evidence goes when no run names one (<see cref="LocalClientPlacement.Evidence"/>).</summary>
    internal string? Output { get; set; }

    /// <summary>
    /// Settles the install once every client launched from the profile is stopped (<see cref="ProfileSourceCheck.SettleAsync"/>) and
    /// records the verdict in <paramref name="report"/> (<see cref="SourceVerdict.Record"/>; without one it only throws when it
    /// fails), its evidence under <paramref name="output"/> (else <see cref="Output"/>). Once only.
    /// </summary>
    internal async Task SettleSourceAsync(ScenarioReport? report, string? output = null)
    {
        var verdict = Volatile.Read(ref _sourceVerdict);
        if (verdict == null)
        {
            try
            {
                verdict = await _sourceCheck.SettleAsync(_host, SourceSettlement.Run(_actor, _runtime, output ?? Output),
                    entry => { _journal.AppendLocal(_actor, entry); return Task.CompletedTask; }, StepTimeout).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Never retired without a recorded verdict: a settlement that threw before it compared anything is unknown, and its
                // profile stays, journalled kept, for env teardown.
                verdict = SourceVerdict.NotCompared(_actor, "the settlement failed before it compared the install: " + error.Message) with
                { Settled = false, Profile = _runtime };
                try { _journal.AppendLocal(_actor, JournalEntry.Of(JournalEntry.CopyKept, ("runtime", _runtime), ("why", "its source settlement did not finish"))); }
                catch (Exception lost) { Console.Error.WriteLine($"Warning: could not journal {JournalEntry.CopyKept} for {_actor}: {lost.Message}"); }
            }
            if (Interlocked.CompareExchange(ref _sourceVerdict, verdict, null) != null) return;
            verdict.Record(report);
        }
    }

    /// <summary>
    /// Makes <paramref name="plan"/>'s disposable profile and binds the plan to it. <paramref name="actor"/> names the client in the
    /// journal. The install is checked first as the profile's source (<see cref="ClientRunPlan.Preflight()"/>: its install pins, its
    /// plugin builds but ValheimCLI's, its standing pins). <paramref name="dataRoot"/> (default <see cref="CliBundle.DataRoot"/>)
    /// holds the profiles (<c>runs/local-clients</c>) on Unix and on a Windows install on the same volume; another-volume Windows
    /// install uses that volume's <c>ValheimTesting/runs/local-clients</c>. Profiles on this machine are journalled as <see cref="WorldFixture"/>'s are, in
    /// process and without the host lock, which a run with its server here holds;
    /// <paramref name="host"/> is this machine (a test passes its own); <paramref name="cliBundle"/> the pinned set when the plan
    /// names none (default <see cref="PinnedCliBundle.Source"/>); <paramref name="loader"/> the pinned-loader rule (default
    /// <see cref="ShippedLoader.Local"/>).
    /// </summary>
    internal static async Task<LocalClientCopy> PrepareAsync(string actor, ClientRunPlan plan, CancellationToken cancellation,
        string? dataRoot = null, IGameHost? host = null, Func<CliBundleSource?>? cliBundle = null, Func<string, string, ShippedLoader.Choice?>? loader = null,
        GameVersionAgreement.Ledger? builds = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CopySource) throw new InvalidOperationException("Only an owned client that does not run in place, and is not bound to a profile yet, gets a disposable profile.");
        actor = JournalActor(actor);
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.Install));
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"The client install {source} does not exist.");

        // The ValheimCLI set the profile stages: the plan's, its DLLs beside its manifest, or the toolkit's pinned bundle.
        string manifestPath = plan.CliManifest ?? (cliBundle ?? (() => PinnedCliBundle.Source()))()?.Manifest
            ?? throw new InvalidDataException("This build of Valheim.Testing.GameSessions carries no pinned ValheimCLI bundle (it was built without one). " +
                "Name the ValheimCLI set to stage with the client plan's cliManifest (its DLLs beside it), or run your install in place (--in-place).");
        var (manifest, location, setFolder) = InstallSelection.ReadCliSet(manifestPath);

        string data = dataRoot ?? CliBundle.DataRoot;
        host ??= new LocalGameHost("this machine", HostShell.ThisMachine);
        var journal = RunJournal.ThisProcess;
        var runtimeRoot = await HostedRuntimeStage.DefaultProfileRootAsync(host, HostedRuntimeKind.Client, source, data, StepTimeout, cancellation).ConfigureAwait(false);
        string root = HostPath.Join(runtimeRoot.Path, "local-clients");
        Console.WriteLine($"profile runtime root: {root} ({runtimeRoot.Reason}).");
        await HostedRuntimeStage.RequireProfileStorageAsync(host, HostedRuntimeKind.Client, source, root, StepTimeout, cancellation,
            explicitRuntime: false).ConfigureAwait(false);
        // One folder per profile: a run may prepare two plans for one client name (a client opened without its mod, then with it).
        string parent = Path.Combine(root, "vt-prep-" + journal.RunId + "-" + actor + "-" + Guid.NewGuid().ToString("N")[..8]);
        string runtime = Path.Combine(parent, "runtime"), stage = Path.Combine(parent, "staging");

        var choice = (loader ?? ShippedLoader.Local)(actor, source);
        if (choice != null) ShippedLoader.Print(actor, choice);
        var loaderPackage = choice == null ? null : BepInExLoaderPackage.Read(choice.Manifest);
        // One client per desktop session, and never a profile prepared while a game runs: refused before the install is read.
        await HostedRuntimeStage.RequireStoppedAsync(host, StepTimeout, cancellation, clientSession: true).ConfigureAwait(false);
        Console.WriteLine($"Preparing an owned mod profile for {source}; the game install stays in place.");
        using var selection = await InstallSelection.ReadAsync(host, HostedRuntimeKind.Client, source, manifest, location, loaderPackage,
            plan.Port, plan, StepTimeout, cancellation, builds, actor).ConfigureAwait(false);
        if (selection.Replaced.Count != 0)
            Console.WriteLine($"{actor}: the profile stages ValheimCLI {manifest.Build} in place of the install's own {string.Join(", ", selection.Replaced)}.");
        var bound = selection.BindPins(actor, plan.InstallPins, plan.Pins, manifest, setFolder);
        Directory.CreateDirectory(root);
        // Journalled before the profile: an interrupted preparation leaves a record of every path it may own.
        journal.AppendLocal(actor, JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", runtime), ("stage", stage), ("parent", parent),
            ("runtimeKind", "client"), ("launchRoot", HostedRuntimeStage.LaunchRoot(host.Shell.Kind == HostShellKind.PowerShell ? "windows" : "macos", HostedRuntimeKind.Client, source, runtime))));
        HostedRuntimeStage.PreparedProfile profile;
        try
        {
            profile = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Client, source, runtime, stage,
                selection.Files, StepTimeout, cancellation, loaderPackage, selection.Source).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not AggregateException)
        {
            // The stage removed what it made (an unproven removal is an AggregateException and stays open for env recover).
            journal.AppendLocal(actor, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", runtime), ("failed", "true")));
            throw;
        }
        journal.AppendLocal(actor, JournalEntry.Of(JournalEntry.CopyDone, ("runtime", runtime),
            ("files", profile.Loader.Files.Count.ToString(CultureInfo.InvariantCulture)), ("source", source),
            ("runtimeKind", "client"), ("launchRoot", profile.GameRoot)));
        var unbound = new Unbound(plan.Install, plan.InstallPins, plan.Pins, plan.CliManifest, plan.PreparedLoaderRoot, plan.PreparedLaunchMode,
            plan.PreparedSourceGameRoot, plan.PreparedSourceGameHash, plan.PreparedLeftOut, plan.PreparedPinOrigins,
            plan.PreparedSourceCliPins, plan.PreparedReplacedCliPaths, plan.PreparedSourceCheck, plan.PreparedBuild);
        if (selection.Build != null) plan.PreparedBuild = selection.Build;
        Bind(plan, source, profile, manifestPath, bound, selection.Replaced, host);
        var copy = new LocalClientCopy(host, actor, runtime, stage, profile.GameRoot, journal, plan, unbound, profile.SourceCheck);
        // The baseline an interrupted run's recovery settles the install from, before anything launches from the profile.
        try
        {
            await profile.SourceCheck.PersistAsync(host, runtime, actor, entry => { journal.AppendLocal(actor, entry); return Task.CompletedTask; },
                StepTimeout, cancellation).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            try { await copy.RetireAsync(verifySource: false).ConfigureAwait(false); }
            catch (Exception cleanup) { throw new AggregateException("The source baseline was not persisted and the disposable client profile was not retired.", error, cleanup); }
            throw;
        }
        return copy;
    }

    /// <summary>
    /// Removes the profile once every client launched from it was stopped (the game's logs were kept beside the evidence then),
    /// journals it retired and restores the plan as it was before it was bound. Once only. A game still running from the
    /// profile's launch root is refused first, which leaves it to <c>env recover</c>. With <paramref name="verifySource"/> the install
    /// is settled first (<see cref="SettleSourceAsync"/>, without a report), unless it was already.
    /// </summary>
    internal async Task RetireAsync(bool verifySource = true)
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0) return;
        await HostedRuntimeStage.RequireStoppedAsync(_host, StepTimeout, runtimes: [_launchRoot], clientSession: false).ConfigureAwait(false);
        Exception? integrity = null;
        try { if (verifySource) await SettleSourceAsync(null).ConfigureAwait(false); }
        catch (Exception error) { integrity = error; }
        // A settlement that did not finish keeps the profile with its baseline and moved files, journalled kept, for env teardown.
        Volatile.Read(ref _sourceVerdict)?.RequireRetirable(integrity);
        try
        {
            await HostedRuntimeStage.RetireAsync(_host, _runtime, _stage, StepTimeout).ConfigureAwait(false);
            _journal.AppendLocal(_actor, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", _runtime)));
        }
        catch (Exception cleanup) when (integrity != null)
        { throw new AggregateException("Source integrity failed and the disposable client profile was not retired.", integrity, cleanup); }
        _plan.Install = _unbound.Install; _plan.InstallPins = _unbound.InstallPins; _plan.Pins = _unbound.Pins; _plan.CliManifest = _unbound.CliManifest;
        _plan.PreparedLoaderRoot = _unbound.LoaderRoot; _plan.PreparedLaunchMode = _unbound.LaunchMode;
        _plan.PreparedSourceGameRoot = _unbound.SourceGameRoot; _plan.PreparedSourceGameHash = _unbound.SourceGameHash;
        _plan.PreparedLeftOut = _unbound.LeftOut;
        _plan.PreparedPinOrigins = _unbound.PinOrigins; _plan.PreparedSourceCliPins = _unbound.SourceCliPins;
        _plan.PreparedReplacedCliPaths = _unbound.ReplacedCliPaths;
        _plan.PreparedSourceCheck = _unbound.SourceCheck;
        _plan.PreparedBuild = _unbound.Build;
        _plan.Prepared = false;
        if (integrity != null) throw integrity;
    }

    // The plan, bound to its profile: the profile's game and loader roots and install pins, its plugin pins with the staged
    // set's in place of any ValheimCLI pin the plan carried (each file's MD5, as the session derives them), and the staged set's manifest.
    private static void Bind(ClientRunPlan plan, string source, HostedRuntimeStage.PreparedProfile profile,
        string manifestPath, InstallSelection.BoundPins bound, IReadOnlyList<string> replacedCliPaths, IGameHost host)
    {
        plan.Install = profile.GameRoot;
        plan.PreparedLoaderRoot = profile.GameRoot == profile.LoaderRoot ? null : profile.LoaderRoot;
        plan.PreparedLaunchMode = "profile";
        plan.PreparedLeftOut = HostedRuntimeStage.ProfileLeftOut.Describe(profile.LeftOut);
        plan.PreparedSourceGameRoot = source;
        plan.PreparedSourceGameHash = profile.Pins.Game;
        var expectedInstallPins = plan.InstallPins;
        plan.InstallPins = profile.Pins;
        plan.Pins = bound.Plugins;
        plan.PreparedPinOrigins = new(StringComparer.Ordinal)
        {
            ["installPins"] = expectedInstallPins != null && expectedInstallPins.SameAs(profile.Pins) ? "plan" : "observed",
        };
        plan.PreparedSourceCheck = () => HostInstall.CheckProfilePinsAsync(host, profile.Pins, profile.GameRoot,
            profile.LoaderRoot, profile.SourceCheck, "prepared client profile before launch", StepTimeout,
            CancellationToken.None).GetAwaiter().GetResult();
        foreach (var (key, origin) in bound.PluginOrigins()) plan.PreparedPinOrigins[key] = origin;
        plan.PreparedSourceCliPins = new Dictionary<string, string>(bound.SourceCliPins, StringComparer.Ordinal);
        plan.PreparedReplacedCliPaths = replacedCliPaths.Order(StringComparer.Ordinal).ToArray();
        plan.CliManifest = Path.GetFullPath(manifestPath);
        plan.Prepared = true;
    }

    // The journal's actor name (letters, digits, '.', '_' and '-'): a client's name, or its evidence folder's.
    private static string JournalActor(string name)
    {
        string safe = new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray());
        return safe.Trim('.', '-').Length == 0 ? "client" : safe;
    }
}
