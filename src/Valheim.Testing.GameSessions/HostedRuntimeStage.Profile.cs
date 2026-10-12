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
        IReadOnlyList<ProfileLeftOut> LeftOut)
    {
        internal string GameRoot => Game.Root;
        internal string LoaderRoot => Loader.Root;
    }

    /// <summary>One root file of the game install that a profile does not use, and why.</summary>
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
    /// The top-level entries of an install that a launch folder never takes from it: <c>logs</c>, where Steam writes a dedicated
    /// server's own runtime output (<c>connection_log_*.txt</c>, <c>stats_log.txt</c>), none of it the game's or the loader's
    /// files (#257). The one definition: the profile's links and a standalone hosted server's runtime copy
    /// (<see cref="HostedServerRun"/>) both take it from here.
    /// </summary>
    internal static readonly string[] RuntimeSkips = ["logs"];

    /// <summary>
    /// The DLLs at a Windows game root (client or dedicated server) that the game itself needs: Unity's player and the
    /// Steam runtime. <c>tier0_s64.dll</c> and <c>vstdlib_s64.dll</c> are the server install's and are what
    /// <c>steamclient64.dll</c> loads when no Steam client is running. Any other root DLL is not the game's (a mod
    /// manager's proxy such as <c>version.dll</c>), so the profile leaves it out.
    /// </summary>
    internal static readonly string[] WindowsGameRootDlls =
        ["UnityPlayer.dll", "steam_api64.dll", "steamclient64.dll", "tier0_s64.dll", "vstdlib_s64.dll"];

    /// <summary>
    /// Root DLLs that mod managers and injectors use as a proxy (Windows loads them from the executable directory in place of
    /// the system's). The profile leaves them out without a word: they are expected beside a modded install.
    /// </summary>
    internal static readonly string[] KnownProxyDlls =
        ["version.dll", "winmm.dll", "dinput8.dll", "dxgi.dll", "d3d9.dll", "xinput1_3.dll"];

    /// <summary>The files of a game install a Windows profile hard-links, and the root files it leaves out that are worth a line of evidence.</summary>
    internal sealed record WindowsProfileFiles(IReadOnlyList<string> Links, IReadOnlyList<ProfileLeftOut> LeftOut);

    /// <summary>
    /// Sorts <paramref name="files"/> (relative, <c>/</c> separators) of a Windows game install into what the profile's launch
    /// folder hard-links: game files only, never the loader (the profile gets its own), a top-level entry in
    /// <paramref name="skips"/> (<see cref="RuntimeSkips"/>), logs, or a root DLL outside <see cref="WindowsGameRootDlls"/>.
    /// A root file left out is reported unless it is the loader's or a known proxy (<see cref="KnownProxyDlls"/>), which are
    /// expected; an unknown root DLL gets an actionable reason, because a DLL the game needs and the profile dropped fails
    /// inside Steam or Unity start-up with no other hint (#651).
    /// </summary>
    internal static WindowsProfileFiles PlanWindowsProfile(IEnumerable<string> files, IReadOnlyCollection<string> skips)
    {
        var links = new List<string>();
        var leftOut = new List<ProfileLeftOut>();
        foreach (string path in files.Order(StringComparer.Ordinal))
        {
            bool root = !path.Contains('/');
            string? reason = null;
            bool report = root;
            if (path.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase) ||
                InstallPins.IsLoaderFile(path, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(".doorstop", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("doorstop", StringComparison.OrdinalIgnoreCase))
            {
                reason = "loader file";
                report = false;
            }
            else if (HostInstall.Skipped(path, skips, StringComparer.OrdinalIgnoreCase)) reason = "a runtime skip (HostedRuntimeStage.RuntimeSkips)";
            else if (path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) reason = "a log file";
            else if (root && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !WindowsGameRootDlls.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                if (KnownProxyDlls.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    reason = "a mod manager's proxy";
                    report = false;
                }
                else reason = "an unknown root DLL, so it is not linked (a proxy here would load its own chain beside this profile). " +
                    "If the game needs it, a launch fails inside Steam or Unity start-up: report it so the game's root DLL list " +
                    "(HostedRuntimeStage.WindowsGameRootDlls) gains it. Otherwise nothing to do";
            }
            if (reason == null) links.Add(path);
            else if (report) leftOut.Add(new ProfileLeftOut(path, reason));
        }
        return new WindowsProfileFiles(links, leftOut);
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
        await RequireStagePathsAsync(host, kind, source, destination, staging, files, timeout, cancellation).ConfigureAwait(false);
        bool windows = host.Shell.Kind == HostShellKind.PowerShell;
        var (selected, explicitlySelectedSettings) = SelectPayload(files, loaderPackage, host.Shell.Kind);
        if (steamRuntimePackage != null) loaderPackage?.RequireNativeMacServer();
        if (steamRuntimePackage != null)
        {
            if (kind != HostedRuntimeKind.Server || windows || host.Kind == GameHostKind.Container ||
                host.Kind == GameHostKind.Local && !OperatingSystem.IsMacOS())
                throw new InvalidDataException("A Mac Steam runtime package requires a Mac dedicated server profile on a local or SSH host.");
            if (host.Kind == GameHostKind.Local) steamRuntimePackage.Validate();
            else await MacSteamRuntimeOnHost.VerifyAsync(host, steamRuntimePackage, timeout, cancellation).ConfigureAwait(false);
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
        bool preserveSourceSettings = BepInExSettings.Choose(
            game.Files.ContainsKey(settings), loaderPackage?.Files.ContainsKey(settings) == true,
            explicitlySelectedSettings) == BepInExSettingsOrigin.Source;
        // The source loader is copied only when no reviewed package replaces it. No plugin or patcher from
        // the source is allowed into the profile, even if the source install was already modded.
        var sourceLoader = loaderPackage == null
            ? game.Files.Keys.Where(path => InstallPins.IsLoaderFile(path,
                    windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                preserveSourceSettings && path.Equals(settings,
                    windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray()
            : preserveSourceSettings ? [settings] : Array.Empty<string>();
        // Windows loads the proxy from the executable directory. Its disposable launch folder hard-links
        // game files only, then receives a separately copied loader and selected BepInEx files.
        // A mod manager may have placed another proxy beside valheim.exe (for example version.dll). Linking
        // arbitrary root DLLs into our launch folder would load that manager's chain alongside this profile.
        // Keep only the game's known root dependencies; every other source file stays untouched and out of the run.
        var plan = windows ? PlanWindowsProfile(game.Files.Keys, RuntimeSkips) : new WindowsProfileFiles([], []);
        string[] gameLinks = [.. plan.Links];
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
                    if (!selected.TryAdd(name, (Path.Combine(steamRoot, relative), sha)))
                        throw new InvalidDataException("Steam runtime target collides with another staged file: " + name);
                }
            }
            shipped = true;
            await host.ShipFilesAsync(payload, staging, timeout, cancellation).ConfigureAwait(false);
            var shipment = await HostInstall.ListAsync(host, staging, timeout, cancellation: cancellation).ConfigureAwait(false);
            foreach (var (relative, value) in selected)
                if (!shipment.Files.TryGetValue(relative, out string? hash) || !hash.Equals(value.Sha, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"The staged profile file {relative} on {host.Name} differs from its selected source.");

            // Mark the destination as possibly created before invoking the host: losing its reply still leaves it owned.
            created = true;
            var seed = await host.RunAsync(windows ? WindowsProfileSeed : BashProfileSeed,
                new Dictionary<string, string> {
                    ["source"] = source, ["runtime"] = destination,
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
            var seeded = await HostInstall.ListAsync(host, destination, timeout, cancellation: cancellation).ConfigureAwait(false);
            foreach (string relative in sourceLoader)
                if (!seeded.Files.TryGetValue(relative, out string? hash) ||
                    !hash.Equals(game.Files[relative], StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"The profile's source loader file {relative} on {host.Name} changed while it was copied.");

            string names = string.Join('\n', selected.Keys.Select(Encode));
            var applied = await host.RunAsync(windows ? WindowsApply : BashApply,
                new Dictionary<string, string> {
                    ["runtime"] = destination, ["stage"] = staging, ["files"] = names,
                    ["loader"] = loaderPackage == null ? "" : string.Join('\n', InstallPins.LoaderEntries),
                    ["preserveConfig"] = preserveSourceSettings ? "true" : "false",
                }, timeout, cancellation).ConfigureAwait(false);
            applied.EnsureSuccess($"Applying the profile files on {host.Name}");
            if (InteractiveClient.Line(applied.Stdout, "VT-STAGED") == null)
                throw new HostOperationException($"No profile-apply verdict from {host.Name}", applied);
            if (steamRuntimePackage != null && host.Kind != GameHostKind.Local)
                foreach (var (relative, sha) in await MacSteamRuntimeOnHost.StageAsync(host, steamRuntimePackage,
                    destination, timeout, cancellation).ConfigureAwait(false))
                    if (!selected.TryAdd("steam-runtime/" + relative, ("remote SteamCMD source", sha)))
                        throw new InvalidDataException("Steam runtime target collides with another staged file: " + relative);
            var profile = await HostInstall.ListAsync(host, destination, timeout, cancellation: cancellation).ConfigureAwait(false);
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
            var pins = windows ? HostInstall.Pins(profile) : HostInstall.Pins(game, profile);
            if (loaderPackage != null && !pins.Loader.Equals(loaderPackage.Loader, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"The profile loader on {host.Name} is not the reviewed package's loader.");
            var freshSource = await HostInstall.ListAsync(host, source, timeout, cancellation: cancellation).ConfigureAwait(false);
            HostInstall.RequireSame(game.Files, freshSource, "source game after profile preparation");
            return new PreparedProfile(game, windows ? profile : game, profile, pins, plan.LeftOut);
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
                            ["parent"] = destination[..destination.LastIndexOfAny(['/', '\\'])],
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

    private static string Encode(string name) => Convert.ToBase64String(Encoding.UTF8.GetBytes(name));

    internal static readonly string BashProfileSeed = """
        set -eu
        if [ -e "$runtime" ]; then echo 'VT-PROFILE-EXISTS'; exit 0; fi
        mkdir -p -- "$runtime"
        while IFS= read -r line; do
          [ -n "$line" ] || continue
          relative=$(printf '%s' "$line" | base64 -D 2>/dev/null || printf '%s' "$line" | base64 -d)
          mkdir -p -- "$(dirname "$runtime/$relative")"
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
            catch { throw "The source game and disposable launch folder must support hard links on one volume. Put the environment's runtime folder on the game's NTFS volume (a runner can instead run the install as it is with --in-place). $($_.Exception.Message)" }
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
