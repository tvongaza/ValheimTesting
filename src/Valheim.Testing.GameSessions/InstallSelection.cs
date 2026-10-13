using valheim_cli.Testing;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>
/// The files an owned loader profile takes from a modded install (#730): the install's own BepInEx plugins, scripts, config and
/// patchers, without any ValheimCLI build of its own (another build loaded beside the staged set is what the static check refuses),
/// and the run's ValheimCLI set in <c>BepInEx/plugins</c>, with ValheimCLI's port when the install has no config of its own for it.
/// A loader package (the pinned BepInExPack) holds only loader files and a <c>BepInEx.cfg</c>, none of them selected here: the
/// install's <c>BepInEx.cfg</c> is never selected: the profile stage decides it from the same listing (<see cref="BepInExSettings"/>, in
/// <see cref="HostedRuntimeStage.PrepareProfileAsync"/>).
/// The one owner of reading that selection from an install, on any host, for a client or a dedicated server. The install is
/// inspected first (<see cref="HostedRuntimeStage.InspectSourceAsync"/>); the selection is read from that listing: in place on this
/// machine, or fetched into an owned local folder from another host (<see cref="IGameHost.FetchDirectoryAsync"/>) and checked
/// against the listing's hashes. A file changed in place after the listing is refused by the profile stage, which compares the
/// install with its listing once the profile is made. Paths compare as the install's host lists them, never as the runner's filesystem does. Dispose it
/// once the profile is prepared: that removes the fetched files and the generated config.
/// </summary>
internal sealed class InstallSelection : IDisposable
{
    private static readonly string[] Folders = ["plugins", "scripts", "config", "patchers"];
    private readonly string _work;

    private InstallSelection(string work, HostListing source, IReadOnlyList<HostedRuntimeFile> files, IReadOnlyList<HostedRuntimeFile> replaced)
    { _work = work; Source = source; Files = files; ReplacedFiles = replaced; }

    /// <summary>The install as <see cref="HostedRuntimeStage.InspectSourceAsync"/> listed it: pass it on as the profile's inspected source.</summary>
    internal HostListing Source { get; }
    /// <summary>The selected files, each a local file: the install's own in place, or fetched with the listing's hash.</summary>
    internal IReadOnlyList<HostedRuntimeFile> Files { get; }
    /// <summary>The install's own ValheimCLI builds that the staged set replaces, kept locally so supplied source pins can be checked.</summary>
    internal IReadOnlyList<HostedRuntimeFile> ReplacedFiles { get; }
    /// <summary>The relative paths of <see cref="ReplacedFiles"/>.</summary>
    internal IReadOnlyList<string> Replaced => ReplacedFiles.Select(file => file.RelativePath).ToArray();

    internal sealed record BoundPins(Dictionary<string, string> Plugins, IReadOnlySet<string> FromPlan,
        IReadOnlyDictionary<string, string> SourceCliPins)
    {
        /// <summary>Each bound plugin's origin as <c>plugins.&lt;guid&gt;</c>: <c>plan</c> when supplied, else <c>observed</c>.</summary>
        internal IEnumerable<KeyValuePair<string, string>> PluginOrigins() => Plugins.Keys.Order(StringComparer.Ordinal)
            .Select(plugin => new KeyValuePair<string, string>("plugins." + plugin, FromPlan.Contains(plugin) ? "plan" : "observed"));
    }

    /// <summary>
    /// Checks every supplied install and plugin pin against this source selection before the profile replaces its loader or
    /// ValheimCLI, then returns the complete startup plugin set the profile will enforce. Every supplied ValheimCLI pin is a
    /// source-install expectation; the coherent replacement set is bound separately as observed. Omitted plugins are observed
    /// from the selection.
    /// </summary>
    internal BoundPins BindPins(string role, InstallPins? expectedInstallPins, IReadOnlyDictionary<string, string> planned,
        CliCapabilityManifest cli, string setFolder)
    {
        if (expectedInstallPins != null) HostInstall.CheckPins(expectedInstallPins, Source, role + " install");
        var staged = BindCliPins(HostedCampaignPreparation.PluginPins(role, Files, planned), cli, setFolder);
        var fromPlan = new HashSet<string>(StringComparer.Ordinal);
        var cliSetFiles = Files.Where(file => file.RelativePath.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFullPath(file.Source).StartsWith(Path.GetFullPath(setFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Select(file => Path.GetFullPath(file.Source)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceCliExpected = planned.Where(pin => CliBuildManifest.IsCliPlugin(pin.Key))
            .ToDictionary(pin => pin.Key, pin => pin.Value, StringComparer.Ordinal);
        var sourcePins = sourceCliExpected.Count == 0 ? new Dictionary<string, string>(StringComparer.Ordinal) :
            HostedCampaignPreparation.PluginPins(role + " install", ReplacedFiles, sourceCliExpected,
                Files.Where(file => !cliSetFiles.Contains(Path.GetFullPath(file.Source))).Concat(ReplacedFiles));
        // The core has one stable file name even when an old or synthetic build has no readable BepInEx metadata. The file was
        // selected as ValheimCLI by that name, so its supplied core GUID pin can still be checked against its source bytes.
        if (sourceCliExpected.ContainsKey(CliCapabilityManifest.PluginGuid) && !sourcePins.ContainsKey(CliCapabilityManifest.PluginGuid))
        {
            var cores = ReplacedFiles.Where(file => Path.GetFileName(file.RelativePath).Equals(cli.Plugin.File, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (cores.Length == 1) sourcePins[CliCapabilityManifest.PluginGuid] = FileHash.Md5(cores[0].Source);
        }
        var checkedSourceCli = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (plugin, expected) in planned)
        {
            if (CliBuildManifest.IsCliPlugin(plugin))
            {
                sourcePins.TryGetValue(plugin, out string? sourceFound);
                if (expected == "absent" && sourceFound == null)
                {
                    checkedSourceCli[plugin] = expected;
                    continue;
                }
                if (sourceFound != null && sourceFound.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    checkedSourceCli[plugin] = sourceFound;
                    continue;
                }
                staged.TryGetValue(plugin, out string? stagedFound);
                string sourceDetail = sourceFound == null ? "the install build it replaces does not contain it"
                    : $"the install build it replaces is {sourceFound}";
                throw new InvalidOperationException($"The {role} profile would not hold the plan's pinned plugin {plugin}={expected}: " +
                    $"the staged build is {stagedFound ?? "absent"}, and {sourceDetail}.");
            }
            if (expected == "absent" && !staged.ContainsKey(plugin))
            {
                staged[plugin] = expected;
                fromPlan.Add(plugin);
                continue;
            }
            if (staged.TryGetValue(plugin, out string? found) && found.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                fromPlan.Add(plugin);
                continue;
            }
            string detail = found == null ? "the staged selection does not contain it." : $"the staged build is {found}.";
            throw new InvalidOperationException($"The {role} profile would not hold the plan's pinned plugin {plugin}={expected}: " + detail);
        }
        return new(staged, fromPlan, checkedSourceCli);
    }

    /// <summary>Removes the local folder holding fetched files and the generated config. The install is never touched.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    /// <summary>
    /// Reads the selection of <paramref name="install"/> on <paramref name="host"/> for a <paramref name="role"/> process.
    /// <paramref name="cli"/> and <paramref name="cliFiles"/> are the ValheimCLI set the profile stages (its manifest and located
    /// DLLs); <paramref name="loader"/> the reviewed loader package that replaces the install's loader, or null; <paramref name="cliPort"/>
    /// the port a generated ValheimCLI config listens on. A client's need for ValheimCLI mutation access follows its
    /// <paramref name="client"/> plan (<see cref="CliServerConfig.RequiresClientMutations"/>; null is treated as joined); a dedicated
    /// server has no client and passes none.
    /// </summary>
    internal static async Task<InstallSelection> ReadAsync(IGameHost host, HostedRuntimeKind role, string install,
        CliCapabilityManifest cli, CliSetLocation cliFiles, BepInExLoaderPackage? loader, int cliPort, ClientRunPlan? client,
        TimeSpan timeout, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(cli);
        ArgumentNullException.ThrowIfNull(cliFiles);
        if (cliFiles.Problems.Count != 0)
            throw new ArgumentException($"The ValheimCLI set to stage is not complete: {string.Join("; ", cliFiles.Problems.Select(problem => problem.Message))}.", nameof(cliFiles));
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        if (role == HostedRuntimeKind.Server && client != null) throw new ArgumentException("A dedicated server has no client plan.", nameof(client));
        var source = await HostedRuntimeStage.InspectSourceAsync(host, role, install, loader, timeout, cancellation).ConfigureAwait(false);
        string work = Path.Combine(Path.GetTempPath(), "vt-install-selection-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (files, replaced) = await SelectAsync(host, role, install, source, cli, cliFiles, loader, work, timeout, cancellation).ConfigureAwait(false);
            // ValheimCLI's port, as a session writes it, when the install has no config of its own for it.
            files = CliServerConfig.Stage(files, role == HostedRuntimeKind.Server ? "Server" : "Client", cliPort,
                Path.Combine(work, "generated", OwnedClientPreflight.CliConfig),
                role == HostedRuntimeKind.Client && CliServerConfig.RequiresClientMutations(client));
            return new InstallSelection(work, source, files, replaced);
        }
        catch
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            throw;
        }
    }

    /// <summary>
    /// The ValheimCLI set a profile stages: the capability manifest at <paramref name="manifestPath"/> and its DLLs beside it (as a
    /// bundle holds them), refused when a DLL it names is missing or differs. Returns the manifest, the located DLLs and their folder.
    /// </summary>
    internal static (CliCapabilityManifest Manifest, CliSetLocation Location, string Folder) ReadCliSet(string manifestPath)
    {
        var manifest = CliCapabilityManifest.Read(manifestPath);
        string folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var location = manifest.Locate(Directory.EnumerateFiles(folder, "*.dll"), "beside its manifest", othersLoad: false);
        if (location.Problems.Count != 0)
            throw new InvalidDataException($"The ValheimCLI set to stage ({manifestPath}) is not complete: {string.Join("; ", location.Problems.Select(problem => problem.Message))}.");
        return (manifest, location, folder);
    }

    /// <summary>
    /// A plan's plugin <paramref name="pins"/> bound to the ValheimCLI set its profile staged: every ValheimCLI pin the plan carried
    /// is replaced by the staged core's plugins at that file's MD5 (as the session derives them), from <paramref name="setFolder"/>
    /// beside its <paramref name="cli"/> manifest. The plan's other pins (its mods, the world) stay as they are.
    /// </summary>
    internal static Dictionary<string, string> BindCliPins(IReadOnlyDictionary<string, string> pins, CliCapabilityManifest cli, string setFolder)
    {
        var bound = pins.Where(pin => !CliBuildManifest.IsCliPlugin(pin.Key)).ToDictionary(pin => pin.Key, pin => pin.Value, StringComparer.Ordinal);
        string md5 = FileHash.Md5(Path.Combine(setFolder, cli.Plugin.File));
        foreach (string guid in cli.Plugin.Plugins) bound[guid] = md5;
        return bound;
    }

    private static async Task<(List<HostedRuntimeFile> Files, List<HostedRuntimeFile> Replaced)> SelectAsync(IGameHost host, HostedRuntimeKind role,
        string install, HostListing source, CliCapabilityManifest cli, CliSetLocation cliFiles, BepInExLoaderPackage? loader, string work,
        TimeSpan timeout, CancellationToken cancellation)
    {
        // The install's paths compare as its host's listing does: ignoring case on Windows, exactly on a bash host.
        var names = source.Names;
        bool inPlace = host.Kind == GameHostKind.Local;
        var selection = new List<HostedRuntimeFile>();
        var replaced = new List<HostedRuntimeFile>();
        foreach (string folder in Folders)
        {
            string prefix = "BepInEx/" + folder + "/";
            // Each listed file below the folder, by its path below it (macOS Finder and AppleDouble files are never part of an install).
            var found = source.Files.Where(file => file.Key.Length > prefix.Length && names.Equals(file.Key[..prefix.Length], prefix) &&
                !FileHash.IsMacMetadata(file.Key)).ToList();
            if (found.Count == 0) continue;
            // The folder as the install spells it, which a Windows host may write in another case.
            string spelling = found[0].Key[..(prefix.Length - 1)];
            var listed = found.ToDictionary(file => file.Key[prefix.Length..], file => (file.Key, Sha: file.Value), StringComparer.Ordinal);
            string fetched = Path.Combine(work, "BepInEx", folder);
            if (!inPlace)
            {
                await host.FetchDirectoryAsync(HostPath.Join(install, spelling), fetched, timeout, cancellation).ConfigureAwait(false);
                // Exactly the listed files arrived, each with the listing's hash.
                foreach (string file in Directory.EnumerateFiles(fetched, "*", SearchOption.AllDirectories))
                {
                    string below = Path.GetRelativePath(fetched, file).Replace('\\', '/');
                    if (!FileHash.IsMacMetadata(below) && !listed.ContainsKey(below))
                        throw new IOException($"BepInEx/{folder}/{below} arrived from {install} on {host.Name}, but the install's listing does not hold it.");
                }
                foreach (var (below, (key, sha)) in listed)
                {
                    string local = Path.Combine(fetched, below.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(local) || !FileHash.Sha256(local).Equals(sha, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"{key} in {install} on {host.Name} arrived missing or changed: it differs from the install's listing.");
                }
            }
            foreach (var (below, (key, _)) in listed.OrderBy(file => file.Key, StringComparer.Ordinal))
            {
                string relative = prefix + below;
                string local = inPlace ? Path.Combine(install, key.Replace('/', Path.DirectorySeparatorChar))
                    : Path.Combine(fetched, below.Replace('/', Path.DirectorySeparatorChar));
                if (names.Equals(key, BepInExSettings.RelativePath)) continue;
                if (folder is "plugins" or "scripts" && relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && (Path.GetFileName(relative).Equals(cli.Plugin.File, StringComparison.OrdinalIgnoreCase) || CliBuildManifest.Plugins(local)?.Any(CliBuildManifest.IsCliPlugin) == true))
                {
                    replaced.Add(new HostedRuntimeFile(local, relative));
                    continue;
                }
                if (!HostedRuntimeStage.IsStageable(relative))
                    throw new ArgumentException($"{relative} in {install} cannot be staged into a disposable profile: a staged path is letters, digits, '.', '_', '-' and spaces. " +
                        (role == HostedRuntimeKind.Client ? "Rename it, or run your install in place (--in-place)." : "Rename it."));
                selection.Add(new HostedRuntimeFile(local, relative));
            }
        }
        foreach (var cliFile in cliFiles.Located)
            selection.Add(new HostedRuntimeFile(cliFile.Path, "BepInEx/plugins/" + cliFile.File.File));
        return (selection, replaced);
    }
}
