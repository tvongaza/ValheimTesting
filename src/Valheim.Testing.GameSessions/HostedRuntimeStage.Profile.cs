using System.Text;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

internal static partial class HostedRuntimeStage
{
    /// <summary>The immutable game and the disposable profile prepared for one launch.</summary>
    internal sealed record PreparedProfile(HostListing Source, HostListing Game, HostListing Loader, InstallPins Pins)
    {
        internal string GameRoot => Game.Root;
        internal string LoaderRoot => Loader.Root;
    }

    /// <summary>
    /// Stage a client's or server's loader and selected files without copying the game. The caller journals
    /// <paramref name="destination"/> and <paramref name="staging"/> before calling and owns their retirement.
    /// This is the profile arm of the same stage owner that prepares full copies; it shares payload selection,
    /// BepInEx settings precedence, the apply script and pinned-file checks with that arm.
    /// </summary>
    internal static async Task<PreparedProfile> PrepareProfileAsync(IGameHost host, HostedRuntimeKind kind,
        string source, string destination, string staging, IReadOnlyList<HostedRuntimeFile> files, TimeSpan timeout,
        CancellationToken cancellation = default, BepInExLoaderPackage? loaderPackage = null,
        HostListing? inspectedSource = null)
    {
        RequireStagePaths(host, kind, source, destination, staging, files);
        bool windows = host.Shell.Kind == HostShellKind.PowerShell;
        var (selected, explicitlySelectedSettings) = SelectPayload(files, loaderPackage, host.Shell.Kind);
        if (inspectedSource != null && (inspectedSource.HostName != host.Name || inspectedSource.Root != source))
            throw new ArgumentException("The inspected source belongs to a different host or install.", nameof(inspectedSource));
        var game = inspectedSource ?? await InspectSourceAsync(host, kind, source, loaderPackage, timeout, cancellation).ConfigureAwait(false);
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
        var gameLinks = windows ? game.Files.Keys.Where(path =>
            !path.StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".log", StringComparison.OrdinalIgnoreCase) &&
            !InstallPins.IsLoaderFile(path, StringComparison.OrdinalIgnoreCase) &&
            (path.Contains('/') || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                new[] { "UnityPlayer.dll", "steam_api64.dll", "steamclient64.dll" }.Contains(path, StringComparer.OrdinalIgnoreCase)) &&
            !path.StartsWith(".doorstop", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("doorstop", StringComparison.OrdinalIgnoreCase)).ToArray() : [];
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
            return new PreparedProfile(game, windows ? profile : game, profile, pins);
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
        if [ -e "$runtime" ]; then echo 'Profile destination already exists' >&2; exit 3; fi
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
        if ([IO.Directory]::Exists($runtime) -or [IO.File]::Exists($runtime)) { throw 'Profile destination already exists' }
        [void][IO.Directory]::CreateDirectory($runtime)
        foreach ($line in ($gameFiles -split "`n")) {
            if (-not $line) { continue }
            $relative = $utf8.GetString([Convert]::FromBase64String($line))
            $from = Join-Path $source $relative
            $to = Join-Path $runtime $relative
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($to))
            try { New-Item -ItemType HardLink -Path $to -Target $from -ErrorAction Stop | Out-Null }
            catch { throw "The source game and disposable launch folder must support hard links on one volume. Use --copy-game. $($_.Exception.Message)" }
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
