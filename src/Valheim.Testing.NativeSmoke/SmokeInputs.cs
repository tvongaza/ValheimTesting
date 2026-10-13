using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>Resolve optional local setup inputs without guessing among builds or account roots.</summary>
internal static class SmokeInputs
{
    /// <summary>
    /// The ValheimCLI core-and-pack set to stage, printed with where it came from: --cli-manifest/--cli-files, then
    /// VALHEIMCLI_BUNDLE (a folder with one manifest and its DLLs), then the pinned bundle Valheim.Testing.GameSessions ships (<see cref="PinnedCliBundle"/>), extracted once under
    /// ValheimTesting's own folder. Whatever ValheimCLI an install happens to hold is never used unless named with --cli-files.
    /// </summary>
    internal static (string Manifest, string Files) Cli(IReadOnlyDictionary<string, string> options) => Cli(options, () => PinnedCliBundle.Source());

    internal static (string Manifest, string Files) Cli(IReadOnlyDictionary<string, string> options, Func<CliBundleSource?> shipped)
    {
        string? manifest = options.TryGetValue("--cli-manifest", out string? namedManifest) ? Path.GetFullPath(namedManifest) : null;
        string? files = options.TryGetValue("--cli-files", out string? namedFiles) ? Path.GetFullPath(namedFiles) : null;
        string origin = "given (--cli-manifest/--cli-files)";
        if (manifest == null && files == null && Environment.GetEnvironmentVariable("VALHEIMCLI_BUNDLE") is { } bundle && !string.IsNullOrWhiteSpace(bundle))
        {
            files = Path.GetFullPath(bundle);
            origin = "VALHEIMCLI_BUNDLE";
        }
        if (manifest == null && files == null)
        {
            var source = shipped() ?? throw new InvalidDataException("This valheim-test carries no ValheimCLI bundle (its build had none to embed). " +
                "Give --cli-manifest and --cli-files from one build, or set VALHEIMCLI_BUNDLE to a folder with its manifest and DLLs.");
            Console.WriteLine("ValheimCLI: " + source.Origin);
            return (source.Manifest, source.Files);
        }
        if (manifest == null)
        {
            string[] candidates = Directory.Exists(files) ? Directory.EnumerateFiles(files!, "*.json", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) is "cli-manifest.json" or "cli-capabilities.json")
                .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray() : [];
            if (candidates.Length != 1)
                throw new InvalidDataException(candidates.Length == 0
                    ? $"No ValheimCLI capability manifest is in {files} ({origin}). Give --cli-manifest and --cli-files from one build, " +
                      "or set VALHEIMCLI_BUNDLE to a directory containing its manifest and DLLs."
                    : "Several ValheimCLI capability manifests were found: " + string.Join(", ", candidates) +
                      ". Choose one build with --cli-manifest and --cli-files.");
            manifest = candidates[0];
        }
        files ??= Path.GetDirectoryName(manifest)!;
        // The resolver checks the chosen manifest's capabilities, GUIDs and exact DLL hashes before launch.
        CliCapabilityManifest.Read(manifest);
        Console.WriteLine($"ValheimCLI: {manifest} ({origin})");
        return (manifest, files);
    }

    /// <summary>--steam-userdata, or this machine's, under the Steam root SteamDetection finds (on Windows its registered path).</summary>
    internal static string SteamUserdata(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("--steam-userdata", out string? named)) return Path.GetFullPath(named);
        var machine = EnvironmentInventory.ThisMachine;
        var (root, _, reason) = SteamDetection.FindRoot(machine);
        string? userdata = root == null ? null : HostPath.Join(root, "userdata");
        if (userdata == null || !machine.DirectoryExists(userdata))
            throw new DirectoryNotFoundException("Steam userdata was not found: " + (reason ?? userdata + " does not exist") +
                ". Give --steam-userdata DIR for the account whose owned client will launch.");
        Console.WriteLine($"steam userdata: {userdata} (detected: this machine)");
        return userdata;
    }
}
