using valheim_cli.Testing;
using System.Globalization;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// One owned client's disposable copy on this machine (#296): the default for every owned client a run opens here, so the
/// user's install never changes. It is made the way a session prepares a client on a host, by the same copy owner
/// (<see cref="HostedRuntimeStage"/> on this machine as a <see cref="LocalGameHost"/>): the install is copied and verified
/// file by file, its BepInEx plugins, scripts, config and patchers are staged into the copy without any ValheimCLI build of its
/// own, and with the ValheimCLI set the run checks against (the plan's <see cref="ClientRunPlan.CliManifest"/>, else the toolkit's
/// pinned bundle); a Doorstop pair that does not match takes the shipped BepInExPack (<see cref="ShippedLoader"/>); a macOS copy
/// is repaired and checked so macOS opens it without a dialog. The copy is journalled before it is made (this machine's run
/// journal, so <c>valheim-test env status</c> and <c>env recover</c> see it) and retired after the client closes. The plan is
/// bound to the copy: its install, install pins, the staged set's plugin pins and manifest (<see cref="ClientRunPlan.Prepared"/>).
/// </summary>
internal sealed class LocalClientCopy
{
    /// <summary>The longest any one step may take; copying a 4 GB install takes minutes on a slow disk.</summary>
    internal static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(30);

    private readonly IGameHost _host;
    private readonly string _actor, _runtime, _stage, _launchRoot;
    private readonly RunJournal _journal;
    private readonly ClientRunPlan _plan;
    private readonly Unbound _unbound;
    private int _retired;

    // The plan as it was before it was bound, restored when the copy is retired: a plan reused later makes a new copy.
    private sealed record Unbound(string Install, InstallPins? InstallPins, Dictionary<string, string> Pins, string? CliManifest,
        string? LoaderRoot, string? LaunchMode);

    private LocalClientCopy(IGameHost host, string actor, string runtime, string stage, string launchRoot, RunJournal journal, ClientRunPlan plan, Unbound unbound)
    { _host = host; _actor = actor; _runtime = runtime; _stage = stage; _launchRoot = launchRoot; _journal = journal; _plan = plan; _unbound = unbound; }

    /// <summary>The owned folder retired with this client's profile or copy.</summary>
    internal string Runtime => _runtime;
    /// <summary>The game root used to prove the launched process has stopped before retiring the owned folder.</summary>
    internal string LaunchRoot => _launchRoot;

    /// <summary>
    /// Makes <paramref name="plan"/>'s disposable copy and binds the plan to it. <paramref name="actor"/> names the client in the
    /// journal. The install is checked first as the copy's source (<see cref="ClientRunPlan.Preflight()"/>: its install pins, its
    /// plugin builds but ValheimCLI's, its standing pins). <paramref name="dataRoot"/> (default <see cref="CliBundle.DataRoot"/>)
    /// holds the copies (<c>runs/local-clients</c>); copies on this machine are journalled as <see cref="WorldFixture"/>'s are, in
    /// process and without the host lock, which a run with its server here holds;
    /// <paramref name="host"/> is this machine (a test passes its own); <paramref name="cliBundle"/> the pinned set when the plan
    /// names none (default <see cref="PinnedCliBundle.Source"/>); <paramref name="loader"/> the shipped-loader decision (default
    /// <see cref="ShippedLoader.Instead(string, string)"/>).
    /// </summary>
    internal static async Task<LocalClientCopy> PrepareAsync(string actor, ClientRunPlan plan, CancellationToken cancellation,
        string? dataRoot = null, IGameHost? host = null, Func<CliBundleSource?>? cliBundle = null, Func<string, string, ShippedLoader.Choice?>? loader = null,
        bool copyGame = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CopySource) throw new InvalidOperationException("Only an owned client that does not run in place, and is not bound to a copy yet, gets a disposable copy.");
        actor = JournalActor(actor);
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(plan.Install));
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"The client install {source} does not exist.");
        plan.CheckOwnedInstall(); // The source's own checks: a wrong game build or plugin is refused before a copy.

        // The ValheimCLI set the copy stages: the plan's, its DLLs beside its manifest, or the toolkit's pinned bundle.
        string manifestPath = plan.CliManifest ?? (cliBundle ?? (() => PinnedCliBundle.Source()))()?.Manifest
            ?? throw new InvalidDataException("This build of Valheim.Testing.GameSessions carries no pinned ValheimCLI bundle (it was built without one). " +
                "Name the ValheimCLI set to stage with the client plan's cliManifest (its DLLs beside it), or run your install in place (--in-place).");
        var manifest = CliCapabilityManifest.Read(manifestPath);
        string setFolder = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var location = manifest.Locate(Directory.EnumerateFiles(setFolder, "*.dll"), "beside its manifest", othersLoad: false);
        if (location.Problems.Count != 0)
            throw new InvalidDataException($"The ValheimCLI set to stage ({manifestPath}) is not complete: {string.Join("; ", location.Problems.Select(problem => problem.Message))}.");

        string data = dataRoot ?? CliBundle.DataRoot;
        host ??= new LocalGameHost("this machine", OperatingSystem.IsWindows() ? HostShell.WindowsPowerShell : HostShell.Bash);
        var journal = RunJournal.ThisProcess;
        string root = Path.Combine(data, "runs", "local-clients");
        // One folder per copy: a run may copy two plans for one client name (a client opened without its mod, then with it).
        string parent = Path.Combine(root, "vt-prep-" + journal.RunId + "-" + actor + "-" + Guid.NewGuid().ToString("N")[..8]);
        string runtime = Path.Combine(parent, "runtime"), stage = Path.Combine(parent, "staging");

        var choice = (loader ?? ShippedLoader.Instead)(actor, source);
        var loaderPackage = choice == null ? null : BepInExLoaderPackage.Read(choice.Manifest);
        string? config = null;
        try
        {
            var selection = Selection(source, manifest, location, plan, loaderPackage, out config, out var replaced);
            // One client per desktop session, and never a copy over a game that runs.
            await HostedRuntimeStage.RequireStoppedAsync(host, StepTimeout, cancellation, clientSession: true).ConfigureAwait(false);
            Directory.CreateDirectory(root);
            if (copyGame)
            {
                var capacity = await HostCopyCapacityProbe.InspectAsync(host, source, root, StepTimeout, cancellation).ConfigureAwait(false);
                HostCopyCapacityProbe.RequireCombined(host.Name, [(actor, capacity)]);
                Console.WriteLine($"Copying your Valheim install ({DiskSpace.Format(capacity.SourceBytes)}) so nothing in it changes; --in-place runs your install directly.");
            }
            else Console.WriteLine($"Preparing an owned mod profile for {source}; the game install stays in place.");
            if (replaced.Count != 0)
                Console.WriteLine($"{actor}: the copy stages ValheimCLI {manifest.Build} in place of the install's own {string.Join(", ", replaced)}.");
            // Journalled before the copy: an interrupted preparation leaves a record of every path it may own.
            journal.AppendLocal(actor, JournalEntry.Of(JournalEntry.CopyIntended, ("runtime", runtime), ("stage", stage), ("parent", parent),
                ("launchMode", copyGame ? "copy" : "profile")));
            HostListing listing;
            InstallPins pins;
            string gameRoot, loaderRoot;
            try
            {
                if (copyGame)
                {
                    listing = await HostedRuntimeStage.PrepareAsync(host, HostedRuntimeKind.Client, source, runtime, stage, selection, StepTimeout,
                        cancellation, loaderPackage).ConfigureAwait(false);
                    pins = HostInstall.Pins(listing); gameRoot = runtime; loaderRoot = runtime;
                }
                else
                {
                    var profile = await HostedRuntimeStage.PrepareProfileAsync(host, HostedRuntimeKind.Client, source, runtime, stage,
                        selection, StepTimeout, cancellation, loaderPackage).ConfigureAwait(false);
                    listing = profile.Loader; pins = profile.Pins;
                    gameRoot = profile.GameRoot; loaderRoot = profile.LoaderRoot;
                }
            }
            catch (Exception error) when (error is not AggregateException)
            {
                // The stage removed what it made (an unproven removal is an AggregateException and stays open for env recover).
                journal.AppendLocal(actor, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", runtime), ("failed", "true")));
                throw;
            }
            journal.AppendLocal(actor, JournalEntry.Of(JournalEntry.CopyDone, ("runtime", runtime),
                ("files", listing.Files.Count.ToString(CultureInfo.InvariantCulture)), ("source", source),
                ("launchMode", copyGame ? "copy" : "profile")));
            var unbound = new Unbound(plan.Install, plan.InstallPins, plan.Pins, plan.CliManifest, plan.PreparedLoaderRoot, plan.PreparedLaunchMode);
            Bind(plan, source, gameRoot, loaderRoot, pins, manifest, manifestPath, setFolder, copyGame);
            return new LocalClientCopy(host, actor, runtime, stage, gameRoot, journal, plan, unbound);
        }
        finally { if (config != null) File.Delete(config); }
    }

    /// <summary>
    /// Removes the copy once every client launched from it was stopped (the game's logs were kept beside the evidence then),
    /// journals it retired and restores the plan as it was before it was bound. Once only. Where the system names a running
    /// game by its path (Windows, macOS), a game still running from the copy is refused first, which leaves it to <c>env recover</c>.
    /// </summary>
    internal async Task RetireAsync()
    {
        if (Interlocked.Exchange(ref _retired, 1) != 0) return;
        // Linux's process list names a game without its path, so there the caller's proven stop is the check.
        if (!OperatingSystem.IsLinux())
            await HostedRuntimeStage.RequireStoppedAsync(_host, StepTimeout, runtimes: [_launchRoot], clientSession: false).ConfigureAwait(false);
        await HostedRuntimeStage.RetireAsync(_host, _runtime, _stage, StepTimeout).ConfigureAwait(false);
        _journal.AppendLocal(_actor, JournalEntry.Of(JournalEntry.CopyRetired, ("runtime", _runtime)));
        _plan.Install = _unbound.Install; _plan.InstallPins = _unbound.InstallPins; _plan.Pins = _unbound.Pins; _plan.CliManifest = _unbound.CliManifest;
        _plan.PreparedLoaderRoot = _unbound.LoaderRoot; _plan.PreparedLaunchMode = _unbound.LaunchMode;
        _plan.CopiedFrom = null; _plan.Prepared = false;
    }

    // The files staged into the copy: the install's own plugins, scripts, config and patchers without any ValheimCLI build
    // (another build loaded beside the staged set is what the static check refuses), and the staged set in BepInEx/plugins.
    // A loader package (the shipped BepInExPack) brings its own files, its BepInEx.cfg among them: the install's are left out.
    private static List<HostedRuntimeFile> Selection(string source, CliCapabilityManifest manifest, CliSetLocation location, ClientRunPlan plan,
        BepInExLoaderPackage? loader, out string? config, out List<string> replaced)
    {
        var names = manifest.Files.Select(file => file.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selection = new List<HostedRuntimeFile>();
        replaced = [];
        config = null;
        var settingsOrigin = BepInExSettings.Choose(
            File.Exists(Path.Combine(source, "BepInEx", "config", "BepInEx.cfg")),
            loader?.Files.ContainsKey(BepInExSettings.RelativePath) == true,
            explicitExists: false);
        foreach (string folder in new[] { "plugins", "scripts", "config", "patchers" })
        {
            string directory = Path.Combine(source, "BepInEx", folder);
            if (!Directory.Exists(directory)) continue;
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                if (FileHash.IsMacMetadata(file)) continue;
                string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                if (relative.Equals(BepInExSettings.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                    settingsOrigin != BepInExSettingsOrigin.Source) continue;
                if (loader != null && loader.Files.Keys.Any(path => path.Equals(relative, StringComparison.OrdinalIgnoreCase))) continue;
                if (folder is "plugins" or "scripts" && file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && (names.Contains(Path.GetFileName(file)) || CliBuildManifest.Plugins(file)?.Any(CliBuildManifest.IsCliPlugin) == true))
                {
                    replaced.Add(relative);
                    continue;
                }
                if (!HostedRuntimeStage.IsStageable(relative))
                    throw new ArgumentException($"{relative} in {source} cannot be staged into a disposable copy: a staged path is letters, digits, '.', '_', '-' and spaces. " +
                        "Rename it, or run your install in place (--in-place).");
                selection.Add(new HostedRuntimeFile(file, relative));
            }
        }
        foreach (var found in location.Located)
            selection.Add(new HostedRuntimeFile(found.Path, "BepInEx/plugins/" + found.File.File));
        // ValheimCLI's port, as a session writes it, when the install has no config of its own for it.
        config = Path.Combine(Path.GetTempPath(), "vt-local-client-" + Guid.NewGuid().ToString("N") + ".cfg");
        return CliServerConfig.Stage(selection, "Client", plan.Port, config);
    }

    // The plan, bound to its copy: the copy's install and install pins, its plugin pins with the staged set's in place of any
    // ValheimCLI pin the plan carried (each file's MD5, as the session derives them), and the staged set's manifest.
    private static void Bind(ClientRunPlan plan, string source, string gameRoot, string loaderRoot, InstallPins runtimePins,
        CliCapabilityManifest manifest, string manifestPath, string setFolder, bool copyGame)
    {
        plan.CopiedFrom = copyGame ? source : null;
        plan.Install = gameRoot;
        plan.PreparedLoaderRoot = gameRoot == loaderRoot ? null : loaderRoot;
        plan.PreparedLaunchMode = copyGame ? "copy" : "profile";
        if (plan.Pinned)
        {
            plan.InstallPins = runtimePins;
            var pins = plan.Pins.Where(pin => !CliBuildManifest.IsCliPlugin(pin.Key)).ToDictionary(pin => pin.Key, pin => pin.Value, StringComparer.Ordinal);
            foreach (var file in manifest.Files)
            {
                string md5 = FileHash.Md5(Path.Combine(setFolder, file.File));
                foreach (string guid in file.Plugins) pins[guid] = md5;
            }
            plan.Pins = pins;
        }
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
