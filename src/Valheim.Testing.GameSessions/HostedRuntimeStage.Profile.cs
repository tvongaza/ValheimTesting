using System.Text;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

internal static partial class HostedRuntimeStage
{
    /// <summary>
    /// The immutable game and the disposable profile prepared for one launch. <see cref="LeftOut"/> is what the profile did not
    /// take from the game's root that a reader of the run should know about (<see cref="ProfileLeftOut"/>).
    /// </summary>
    internal sealed record PreparedProfile(HostListing Source, HostListing Game, HostListing Loader, InstallPins Pins,
        IReadOnlyList<ProfileLeftOut> LeftOut, ProfileSourceCheck SourceCheck)
    {
        internal string GameRoot => Game.Root;
        internal string LoaderRoot => Loader.Root;
    }

    /// <summary>One root file or aggregated top-level folder of the game install that a profile does not use, and why.</summary>
    internal sealed record ProfileLeftOut(string Path, string Reason)
    {
        public override string ToString() => $"{Path}: {Reason}";

        /// <summary>The run-evidence text for <paramref name="leftOut"/> (one line); null when nothing was left out.</summary>
        internal static string? Describe(IEnumerable<ProfileLeftOut> leftOut)
        {
            var items = leftOut.Select(item => item.ToString()).ToArray();
            return items.Length == 0 ? null : string.Join("; ", items);
        }
    }

    /// <summary>
    /// The top-level entries a standalone hosted server leaves out when comparing its install with the plan's runtime manifest:
    /// <c>logs</c>, where Steam writes runtime output (<c>connection_log_*.txt</c>, <c>stats_log.txt</c>) (#257).
    /// </summary>
    internal static readonly string[] RuntimeSkips = ["logs"];

    /// <summary>
    /// The DLLs at a Windows game root (client or dedicated server) that the game itself needs: Unity's player and the
    /// Steam runtime. <c>steamwebrtc64.dll</c>, <c>tier0_s64.dll</c> and <c>vstdlib_s64.dll</c> are in the server install's
    /// 64-bit Steam closure when no Steam client is running. Any other root DLL is not the game's (a mod
    /// manager's proxy such as <c>version.dll</c>), so the profile leaves it out.
    /// </summary>
    internal static readonly string[] WindowsGameRootDlls =
        ["UnityPlayer.dll", "steam_api64.dll", "steamclient64.dll", "steamwebrtc64.dll", "tier0_s64.dll", "vstdlib_s64.dll"];

    /// <summary>32-bit Steam client libraries present beside the 64-bit dedicated server but not loaded by it.</summary>
    internal static readonly string[] WindowsOmittedSteam32Dlls =
        ["steamclient.dll", "steamwebrtc.dll", "tier0_s.dll", "vstdlib_s.dll"];

    /// <summary>Other immutable root files Unity reads or starts for a Windows client or dedicated server.</summary>
    internal static readonly string[] WindowsGameRootFiles =
        [GameLaunch.ClientWindowsExecutable, GameLaunch.ServerWindowsExecutable, "UnityCrashHandler64.exe"];

    /// <summary>
    /// Root DLLs that mod managers and injectors use as a proxy (Windows loads them from the executable directory in place of
    /// the system's). The profile leaves them out without a word: they are expected beside a modded install.
    /// </summary>
    internal static readonly string[] KnownProxyDlls =
        ["version.dll", "winmm.dll", "dinput8.dll", "dxgi.dll", "d3d9.dll", "xinput1_3.dll"];

    /// <summary>
    /// The shared libraries at a Linux dedicated server's root that the game needs: Unity's player, and the crossplay
    /// <c>libparty.so</c> should a build ship it beside the executable rather than in <c>valheim_server_Data/Plugins</c>. The
    /// Steam client library is <c>linux64/steamclient.so</c>, linked with its folder. Any other root library is not the game's,
    /// so the profile leaves it out.
    /// </summary>
    internal static readonly string[] LinuxGameRootLibraries = ["UnityPlayer.so", "libparty.so"];

    /// <summary>
    /// The files of a game install a linked profile hard-links; of those, the native code re-checked before each boot (the
    /// executable and the libraries beside it, <see cref="Checked"/>); and the root files it leaves out that are worth a line
    /// of evidence.
    /// </summary>
    internal sealed record LinkedProfileFiles(IReadOnlyList<string> Links, IReadOnlyList<ProfileLeftOut> LeftOut, IReadOnlyList<string> Checked);

    // One platform's link table: each executable and the data tree it reads, the other root files and the root libraries the
    // game needs, whole trees, the root libraries left out for a stated reason or without a word (a mod manager's proxy), the
    // reason an unknown root library gets, how the platform's file system compares names, and the folder whose files (the
    // executable and its native libraries) are re-checked before each boot: the root, or the Mac server's valheim_server/.
    private sealed record LinkTable(string Name, StringComparison Names, IReadOnlyDictionary<string, string> DataRoots,
        string[] RootFiles, string Library, string[] Libraries, string[] Trees, string[] Omitted, string OmittedReason,
        string[] Proxies, string UnknownLibrary, string NativeFolder = "");

    private static readonly LinkTable WindowsLinks = new("Windows", StringComparison.OrdinalIgnoreCase,
        new Dictionary<string, string> { [GameLaunch.ClientWindowsExecutable] = "valheim_Data/", [GameLaunch.ServerWindowsExecutable] = "valheim_server_Data/" },
        WindowsGameRootFiles, ".dll", WindowsGameRootDlls, ["D3D12/", "MonoBleedingEdge/"],
        WindowsOmittedSteam32Dlls, "32-bit Steam client library, not loaded by the 64-bit game", KnownProxyDlls,
        "an unknown root DLL, so it is not linked (a proxy here would load its own chain beside this profile). " +
        "If the game needs it, a launch fails inside Steam or Unity start-up: report it so the game's root DLL list " +
        "(HostedRuntimeStage.WindowsGameRootDlls) gains it. Otherwise nothing to do");

    private static readonly LinkTable LinuxLinks = new("Linux", StringComparison.Ordinal,
        new Dictionary<string, string> { [GameLaunch.ServerLinuxExecutable] = "valheim_server_Data/" },
        [GameLaunch.ServerLinuxExecutable], ".so", LinuxGameRootLibraries, ["linux64/"], [], "", [],
        "an unknown root library, so it is not linked. " +
        "If the game needs it, a launch fails inside Steam or Unity start-up: report it so the game's root library list " +
        "(HostedRuntimeStage.LinuxGameRootLibraries) gains it. Otherwise nothing to do");

    // The Mac dedicated server keeps its executable, libraries and data in valheim_server/ (Valheim, *.dylib, Data/**,
    // MonoBleedingEdge/**, PlugIns/**), linked whole. Its root holds only what the launch does not use: Steam's own folders,
    // the manual and the start script.
    private static readonly LinkTable MacLinks = new("macOS", StringComparison.Ordinal,
        new Dictionary<string, string> { [GameLaunch.ServerMacExecutable] = "valheim_server/" },
        [], ".dylib", [], [], [], "", [],
        "an unknown root library, so it is not linked: the Mac server's libraries are in valheim_server/, which is linked whole. " +
        "If the game needs it, a launch fails during start-up: report it. Otherwise nothing to do", NativeFolder: "valheim_server/");

    /// <summary>
    /// Sorts <paramref name="files"/> (relative, <c>/</c> separators) of a <paramref name="platform"/> game install into what
    /// the profile's launch folder hard-links. On Windows: the installed executable, its matching <c>_Data</c> tree, Unity's
    /// <c>MonoBleedingEdge</c> and <c>D3D12</c> trees, <see cref="WindowsGameRootFiles"/> and <see cref="WindowsGameRootDlls"/>.
    /// On Linux: <c>valheim_server.x86_64</c>, its <c>valheim_server_Data</c> tree, the <c>linux64</c> tree (the Steam client
    /// library) and <see cref="LinuxGameRootLibraries"/>. On macOS (the dedicated server): its whole <c>valheim_server</c> folder.
    /// Writable text/log/crash output and Finder's <c>.DS_Store</c> are excluded even below an otherwise selected tree, and so
    /// are the loader and the start scripts. Every unrelated nested file is reported in one aggregate per
    /// top-level folder. A root file left out is reported unless it is the loader's or a known proxy
    /// (<see cref="KnownProxyDlls"/>), which are expected; an unknown root library gets an actionable reason, because a library
    /// the game needs and the profile dropped fails inside Steam or Unity start-up with no other hint (#651).
    /// </summary>
    internal static LinkedProfileFiles PlanLinkedProfile(ClientPlatform platform, IEnumerable<string> files)
    {
        var table = platform switch
        {
            ClientPlatform.Windows => WindowsLinks,
            ClientPlatform.Linux => LinuxLinks,
            _ => MacLinks,
        };
        var names = StringComparer.FromComparison(table.Names);
        string[] listing = files.Order(StringComparer.Ordinal).ToArray();
        var dataRoots = table.DataRoots.Where(item => listing.Contains(item.Key, names)).Select(item => item.Value).ToList();
        var links = new List<string>();
        var leftOut = new List<ProfileLeftOut>();
        var nestedLeftOut = new Dictionary<string, (string Name, List<string> Reasons)>(names);
        foreach (string path in listing)
        {
            bool root = !path.Contains('/');
            string? reason = null;
            bool report = true;
            if (path.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase) ||
                InstallPins.IsLoaderFile(path, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(".doorstop", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("doorstop", StringComparison.OrdinalIgnoreCase))
            {
                reason = "loader file";
                report = false;
            }
            // Runtime output is not immutable. Exclude it even inside a selected game tree, so an existing output name
            // can never become a write-through hard link to the source install (#656).
            else if (path.StartsWith("logs/", StringComparison.OrdinalIgnoreCase)) reason = "runtime log output";
            else if (path.Split('/').Contains("crashdumps", StringComparer.OrdinalIgnoreCase) ||
                path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".mdmp", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".dump", StringComparison.OrdinalIgnoreCase)) reason = "a crash dump";
            else if (path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) reason = "a log file";
            else if (path.Split('/')[^1] == ".DS_Store") reason = "Finder's folder metadata";
            else if (path.Equals("steam_appid.txt", StringComparison.OrdinalIgnoreCase)) reason = "not needed because the launch supplies SteamAppId";
            else if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) reason = "a writable text output";
            else if (table.RootFiles.Contains(path, names) || table.Libraries.Contains(path, names) ||
                table.Trees.Any(prefix => path.StartsWith(prefix, table.Names)) ||
                dataRoots.Any(prefix => path.StartsWith(prefix, table.Names)))
            {
                links.Add(path);
                continue;
            }
            else if (root && table.Omitted.Contains(path, names)) reason = table.OmittedReason;
            else if (root && path.EndsWith(table.Library, StringComparison.OrdinalIgnoreCase))
            {
                if (table.Proxies.Contains(path, names))
                {
                    reason = "a mod manager's proxy";
                    report = false;
                }
                else reason = table.UnknownLibrary;
            }
            else reason = $"not an input to the {table.Name} game launch";
            if (!report) continue;
            if (root)
            {
                leftOut.Add(new ProfileLeftOut(path, reason));
            }
            else
            {
                string folder = path[..path.IndexOf('/')];
                if (!nestedLeftOut.TryGetValue(folder, out var group))
                    nestedLeftOut.Add(folder, group = (folder, []));
                group.Reasons.Add(reason);
            }
        }
        foreach (var group in nestedLeftOut.Values)
        {
            var reasons = group.Reasons.GroupBy(reason => reason, StringComparer.Ordinal)
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => item.Count() == 1 ? item.Key : $"{item.Key} ({item.Count()})");
            string count = group.Reasons.Count == 1 ? "1 file" : $"{group.Reasons.Count} files";
            leftOut.Add(new ProfileLeftOut(group.Name + "/**", $"{count} left out: {string.Join("; ", reasons)}"));
        }
        string[] native = links.Where(path => path.StartsWith(table.NativeFolder, table.Names) &&
            !path[table.NativeFolder.Length..].Contains('/')).ToArray();
        return new LinkedProfileFiles(links, leftOut.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray(), native);
    }

    /// <summary>
    /// Stage a client's or server's loader and selected files without copying the game. The caller journals
    /// <paramref name="destination"/> and <paramref name="staging"/> before calling and owns their retirement.
    /// Every owned launch the toolkit prepares (a campaign's actors, a local client, a targeted regression) is staged here.
    /// </summary>
    internal static async Task<PreparedProfile> PrepareProfileAsync(IGameHost host, HostedRuntimeKind kind,
        string source, string destination, string staging, IReadOnlyList<HostedRuntimeFile> files, TimeSpan timeout,
        CancellationToken cancellation = default, BepInExLoaderPackage? loaderPackage = null,
        HostListing? inspectedSource = null,
        Func<IGameHost, string, TimeSpan, CancellationToken, Task<MacBundleInspection.Verdict>>? inspectMac = null,
        MacSteamRuntimePackage? steamRuntimePackage = null)
    {
        RequireStagePaths(host, kind, source, destination, staging, files);
        bool windows = host.Shell.Kind == HostShellKind.PowerShell;
        var storage = await RequireProfileStorageAsync(host, kind, source, destination, timeout, cancellation, apart: [destination, staging]).ConfigureAwait(false);
        bool linked = storage.Layout == ProfileLayout.Linked;
        // Where the linked game files go: the profile itself, or two folders down for a Mac server (ProfileLayouts.GameFolder).
        string gameFolder = linked ? ProfileLayouts.GameFolder(storage.Platform, kind == HostedRuntimeKind.Server) : "";
        string gameRoot = gameFolder.Length == 0 ? destination : HostPath.Join(destination, gameFolder);
        var (selected, explicitlySelectedSettings) = SelectPayload(files, loaderPackage, host.Shell.Kind);
        if (steamRuntimePackage != null) loaderPackage?.RequireNativeMacServer();
        if (steamRuntimePackage != null)
        {
            if (kind != HostedRuntimeKind.Server || windows || host.Kind == GameHostKind.Container ||
                host.Kind == GameHostKind.Local && !OperatingSystem.IsMacOS())
                throw new InvalidDataException("A Mac Steam runtime package requires a Mac dedicated server profile on a local or SSH host.");
            await MacSteamRuntimeOnHost.VerifyAsync(host, steamRuntimePackage, timeout, cancellation).ConfigureAwait(false);
        }
        if (inspectedSource != null && (inspectedSource.HostName != host.Name || inspectedSource.Root != source))
            throw new ArgumentException("The inspected source belongs to a different host or install.", nameof(inspectedSource));
        var game = inspectedSource ?? await InspectSourceAsync(host, kind, source, loaderPackage, timeout, cancellation).ConfigureAwait(false);
        if (kind == HostedRuntimeKind.Client && MacAppBundle.IsMacClient(game))
        {
            var bundle = await (inspectMac ?? MacAppBundle.InspectAsync)(host, source, HostedTimeouts.MacBundleAssessment(timeout), cancellation).ConfigureAwait(false);
            if (MacBundleInspection.SourceRefusal(bundle) is { } refusal)
                throw new InvalidOperationException($"The source Valheim.app on {host.Name} cannot launch: {refusal}");
        }
        const string settings = BepInExSettings.RelativePath;
        bool macOS = kind == HostedRuntimeKind.Server ? HostInstall.DetectServer(game) == ServerPlatform.MacOS : MacAppBundle.IsMacClient(game);
        bool preserveSourceSettings = BepInExSettings.Choose(game.Files.ContainsKey(settings),
            loaderPackage?.Files.ContainsKey(settings), explicitlySelectedSettings, macOS) == BepInExSettingsOrigin.Source;
        // The source loader is copied only when no reviewed package replaces it (a macOS install's own loader); its BepInEx.cfg
        // as BepInExSettings decides. No plugin or patcher from the source is allowed into the profile, even if the source
        // install was already modded.
        var sourceLoader = loaderPackage == null
            ? game.Files.Keys.Where(path => InstallPins.IsLoaderFile(path,
                    windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                preserveSourceSettings && path.Equals(settings,
                    windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray()
            : preserveSourceSettings ? [settings] : Array.Empty<string>();
        // A linked profile is the launch folder: Windows loads the proxy from the executable directory, and a server's
        // Paths.GameRootPath (where BepInEx's preloader log goes) is found from the executable's path. It hard-links game files
        // only, then receives a separately copied loader and selected BepInEx files.
        // A mod manager may have placed another proxy beside valheim.exe (for example version.dll). Linking
        // arbitrary root DLLs into our launch folder would load that manager's chain alongside this profile.
        // Keep only the game's known root dependencies; every other source file stays untouched and out of the run.
        var plan = linked ? PlanLinkedProfile(SourcePlatform(host, kind, game), game.Files.Keys) : new LinkedProfileFiles([], [], []);
        string[] gameLinks = [.. plan.Links];
        var sourceCheck = ProfileSourceCheck.Prepare(game, plan.Checked, storage.Layout);
        if (plan.LeftOut.Count != 0)
            Console.WriteLine($"The profile {destination} on {host.Name} leaves out {ProfileLeftOut.Describe(plan.LeftOut)}.");
        string payload = Path.Combine(Path.GetTempPath(), "valheim-profile-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(payload);
        bool shipped = false, created = false;
        try
        {
            foreach (var (relative, value) in selected)
            {
                string target = Path.Combine(payload, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(value.Source, target);
                if (!FileHash.Sha256(target).Equals(value.Sha, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The local profile payload changed: " + relative);
            }
            if (steamRuntimePackage != null && host.Kind == GameHostKind.Local)
            {
                string steamRoot = Path.Combine(payload, "steam-runtime");
                foreach (var (relative, sha) in steamRuntimePackage.StageSignedFiles(steamRoot))
                {
                    string name = "steam-runtime/" + relative;
                    if (!selected.TryAdd(name, Select(Path.Combine(steamRoot, relative), sha, name)))
                        throw new InvalidDataException("Steam runtime target collides with another staged file: " + name);
                }
                await MacSteamRuntimeOnHost.VerifyAsync(host, steamRuntimePackage, timeout, cancellation).ConfigureAwait(false);
            }
            shipped = true;
            await host.ShipFilesAsync(payload, staging, timeout, cancellation).ConfigureAwait(false);
            var shipment = await ProfileSourceCheck.ListSelectedAsync(host, staging, selected.Keys, timeout, cancellation).ConfigureAwait(false)
                ?? throw new InvalidOperationException("A profile payload unexpectedly selected no files.");
            foreach (var (relative, value) in selected)
                if (!shipment.Files.TryGetValue(relative, out string? hash) || !hash.Equals(value.Sha, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"The staged profile file {relative} on {host.Name} differs from its selected source.");

            // Mark the destination as possibly created before invoking the host: losing its reply still leaves it owned.
            created = true;
            var seed = await host.RunAsync(windows ? WindowsProfileSeed : BashProfileSeed,
                new Dictionary<string, string> {
                    ["source"] = source, ["runtime"] = destination, ["game"] = gameRoot,
                    ["files"] = string.Join('\n', sourceLoader.Select(Encode)),
                    ["gameFiles"] = string.Join('\n', gameLinks.Select(Encode)),
                }, timeout, cancellation).ConfigureAwait(false);
            if (InteractiveClient.Line(seed.Stdout, "VT-PROFILE-EXISTS") != null)
            {
                // The host explicitly proved this invocation did not create the destination.
                // Retire staging only; an existing profile may belong to another run.
                created = false;
                throw new IOException($"The profile destination {destination} already exists on {host.Name}; no existing files were removed.");
            }
            seed.EnsureSuccess($"Seeding the profile loader on {host.Name}");
            if (InteractiveClient.Line(seed.Stdout, "VT-PROFILE-SEEDED") == null)
                throw new HostOperationException($"No profile-seed verdict from {host.Name}", seed);
            var seeded = await ProfileSourceCheck.ListSelectedAsync(host, destination, sourceLoader, timeout, cancellation).ConfigureAwait(false);
            if (seeded != null)
                foreach (string relative in sourceLoader)
                    if (!seeded.Files.TryGetValue(relative, out string? hash) ||
                        !hash.Equals(game.Files[relative], StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"The profile's source loader file {relative} on {host.Name} changed while it was copied.");

            string names = string.Join('\n', selected.Select(file => Encode(file.Key) + " " + file.Value.Kind));
            var applied = await host.RunAsync(windows ? WindowsApply : BashApply,
                new Dictionary<string, string> {
                    ["runtime"] = destination, ["stage"] = staging, ["parent"] = HostPath.Parent(destination), ["files"] = names,
                    ["loader"] = loaderPackage == null ? "" : string.Join('\n', InstallPins.LoaderEntries),
                    ["preserveConfig"] = preserveSourceSettings ? "true" : "false",
                }, timeout, cancellation).ConfigureAwait(false);
            applied.EnsureSuccess($"Applying the profile files on {host.Name}");
            if (InteractiveClient.Line(applied.Stdout, "VT-STAGED") == null)
                throw new HostOperationException($"No profile-apply verdict from {host.Name}", applied);
            if (steamRuntimePackage != null && host.Kind != GameHostKind.Local)
            {
                foreach (var (relative, sha) in await MacSteamRuntimeOnHost.StageAsync(host, steamRuntimePackage,
                    destination, timeout, cancellation).ConfigureAwait(false))
                {
                    string name = "steam-runtime/" + relative;
                    if (!selected.TryAdd(name, Select("remote SteamCMD source", sha, name)))
                        throw new InvalidDataException("Steam runtime target collides with another staged file: " + relative);
                }
                await MacSteamRuntimeOnHost.VerifyAsync(host, steamRuntimePackage, timeout, cancellation).ConfigureAwait(false);
            }
            var profile = await HostInstall.ListAsync(host, destination, timeout,
                ProfileSourceCheck.StagingVerificationPaths(selected.Keys.Concat(sourceLoader)), cancellation).ConfigureAwait(false);
            foreach (var (relative, value) in selected)
                if (!profile.Files.TryGetValue(relative, out string? hash) || !hash.Equals(value.Sha, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"The prepared profile file {relative} on {host.Name} differs from its pin.");
            if (preserveSourceSettings && (!profile.Files.TryGetValue(settings, out string? configHash) ||
                !configHash.Equals(game.Files[settings], StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"The profile's {settings} on {host.Name} differs from its pinned source.");
            foreach (string directory in new[] { "plugins", "scripts", "config", "patchers" })
            {
                string prefix = "BepInEx/" + directory + "/";
                var expected = selected.Keys.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToHashSet(HostNames);
                if (directory == "config" && preserveSourceSettings) expected.Add(settings);
                if (profile.Files.Keys.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Any(name => !expected.Contains(name)))
                    throw new IOException($"The prepared profile on {host.Name} retained an unselected {directory} file.");
            }
            HostListing preparedGame, loader;
            InstallPins pins;
            if (!linked) (preparedGame, loader, pins) = (game, profile, HostInstall.Pins(game, profile));
            else if (gameRoot == destination)
            {
                preparedGame = loader = LinkedProfileListing(game, profile, plan.Links);
                pins = HostInstall.Pins(profile);
            }
            else
            {
                // A Mac server's game lies below the loader: its linked game code is listed where it runs, and the loader's
                // listing carries the links under their folder, so retirement tells them from what the run wrote.
                var linkedGame = await HostInstall.ListAsync(host, gameRoot, timeout, InstallPins.ManagedFolders, cancellation).ConfigureAwait(false);
                preparedGame = LinkedProfileListing(game, linkedGame, plan.Links);
                loader = LinkedProfileListing(game, profile, plan.Links, gameFolder + "/");
                pins = HostInstall.Pins(preparedGame, profile);
            }
            if (loaderPackage != null && !pins.Loader.Equals(loaderPackage.Loader, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"The profile loader on {host.Name} is not the reviewed package's loader.");
            sourceCheck.RecordPreparedProfile(preparedGame, loader,
                selected.Keys.Where(path => path.StartsWith("steam-runtime/", StringComparison.Ordinal)));
            return new PreparedProfile(game, preparedGame, loader, pins, plan.LeftOut, sourceCheck);
        }
        catch (Exception original)
        {
            if (shipped || created)
            {
                try
                {
                    var cleanup = await host.RunAsync(windows ? WindowsCleanup : BashCleanup,
                        new Dictionary<string, string> {
                            ["runtime"] = created ? destination : "", ["stage"] = shipped ? staging : "",
                            ["parent"] = HostPath.Parent(destination),
                        }, timeout, CancellationToken.None).ConfigureAwait(false);
                    cleanup.EnsureSuccess($"Cleaning failed profile preparation on {host.Name}");
                    if (InteractiveClient.Line(cleanup.Stdout, "VT-STAGE-CLEANED") == null)
                        throw new HostOperationException($"Profile cleanup on {host.Name} was not proven", cleanup);
                }
                catch (Exception cleanup)
                {
                    throw new AggregateException("Profile preparation failed and cleanup was not proven.", original, cleanup);
                }
            }
            throw;
        }
        finally { Directory.Delete(payload, recursive: true); }
    }

    // A linked profile's full game listing without re-hashing its hard links: their bytes are the preparation listing's
    // bytes, while every copied loader/BepInEx path comes from the narrow verified profile listing above. The links are named
    // under prefix when the game lies below the listed root (a Mac server's loader listing).
    private static HostListing LinkedProfileListing(HostListing source, HostListing profile, IEnumerable<string> links, string prefix = "")
    {
        var files = new Dictionary<string, string>(profile.Names);
        var states = new Dictionary<string, HostFileState>(profile.Names);
        foreach (string path in links)
        {
            files[prefix + path] = source.Files[path];
            states[prefix + path] = source.States[path];
        }
        foreach (var (path, hash) in profile.Files) files[path] = hash;
        foreach (var (path, state) in profile.States) states[path] = state;
        return new HostListing(profile.HostName, profile.Shell, profile.Root, files,
            source.Executables.Select(path => prefix + path).Where(files.ContainsKey).ToArray(), states);
    }

    private static string Encode(string name) => Convert.ToBase64String(Encoding.UTF8.GetBytes(name));

    // A linked profile's game files are hard links, never copies, made in game (the profile, or a Mac server's folder below it):
    // a failed link refuses the profile, as on Windows.
    internal static readonly string BashProfileSeed = """
        set -eu
        if [ -e "$runtime" ]; then echo 'VT-PROFILE-EXISTS'; exit 0; fi
        mkdir -p -- "$runtime"
        while IFS= read -r line; do
          [ -n "$line" ] || continue
          # The x keeps a name's trailing line break, which $( ) would strip.
          relative=$({ printf '%s' "$line" | base64 -D 2>/dev/null || printf '%s' "$line" | base64 -d; } && printf x) || exit 3
          relative=${relative%x}
          folder=$(dirname -- "$game/$relative" && printf x) || exit 3
          mkdir -p -- "${folder%?x}"
          if ! error=$(ln -- "$source/$relative" "$game/$relative" 2>&1); then
            echo "Could not create a hard link in runtime folder $runtime from the install's mount containing $source: $error" >&2
            exit 1
          fi
        done <<< "$gameFiles"
        while IFS= read -r line; do
          [ -n "$line" ] || continue
          # The x keeps a name's trailing line break, which $( ) would strip.
          relative=$({ printf '%s' "$line" | base64 -D 2>/dev/null || printf '%s' "$line" | base64 -d; } && printf x) || exit 3
          relative=${relative%x}
          folder=$(dirname -- "$runtime/$relative" && printf x) || exit 3
          mkdir -p -- "${folder%?x}"
          cp "$source/$relative" "$runtime/$relative"
        done <<< "$files"
        echo 'VT-PROFILE-SEEDED'
        """.ReplaceLineEndings("\n");

    internal static readonly string WindowsProfileSeed = """
        $utf8 = New-Object Text.UTF8Encoding $false
        if ([IO.Directory]::Exists($runtime) -or [IO.File]::Exists($runtime)) { 'VT-PROFILE-EXISTS'; exit 0 }
        [void][IO.Directory]::CreateDirectory($runtime)
        foreach ($line in ($gameFiles -split "`n")) {
            if (-not $line) { continue }
            $relative = $utf8.GetString([Convert]::FromBase64String($line))
            $from = Join-Path $source $relative
            $to = Join-Path $runtime $relative
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
            try { New-Item -ItemType HardLink -Path $to -Target $from -ErrorAction Stop | Out-Null }
            catch { throw "Could not create a hard link in runtime folder $runtime from the install's volume containing $source`: $($_.Exception.Message)" }
        }
        foreach ($line in ($files -split "`n")) {
            if (-not $line) { continue }
            $relative = $utf8.GetString([Convert]::FromBase64String($line))
            $from = Join-Path $source $relative
            $to = Join-Path $runtime $relative
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
            [IO.File]::Copy($from, $to)
        }
        'VT-PROFILE-SEEDED'
        """.ReplaceLineEndings("\n");
}
