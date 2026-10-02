using Valheim.Testing.Game;

/// <summary>Resolve optional local setup inputs without guessing among builds or account roots.</summary>
internal static class SmokeInputs
{
    internal static (string Manifest, string Files) Cli(IReadOnlyDictionary<string, string> options, string game)
    {
        string? manifest = options.TryGetValue("--cli-manifest", out string? namedManifest) ? Path.GetFullPath(namedManifest) : null;
        string? files = options.TryGetValue("--cli-files", out string? namedFiles) ? Path.GetFullPath(namedFiles) : null;
        if (manifest != null && files != null) return (manifest, files);
        if (manifest != null) return (manifest, Path.GetDirectoryName(manifest)!);

        string? bundle = Environment.GetEnvironmentVariable("VALHEIMCLI_BUNDLE");
        if (files == null && !string.IsNullOrWhiteSpace(bundle)) files = Path.GetFullPath(bundle);
        string[] roots = files == null
            ? [Path.Combine(game, "BepInEx", "plugins"), Path.Combine(game, "BepInEx", "scripts")]
            : [files];
        string[] candidates = roots.Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
            .Where(path => Path.GetFileName(path) is "cli-manifest.json" or "cli-capabilities.json")
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        if (candidates.Length != 1)
            throw new InvalidDataException(candidates.Length == 0
                ? "No ValheimCLI capability manifest was found. Give --cli-manifest and --cli-files from one build, " +
                  "or set VALHEIMCLI_BUNDLE to a directory containing its manifest and DLLs."
                : "Several ValheimCLI capability manifests were found: " + string.Join(", ", candidates) +
                  ". Choose one build with --cli-manifest and --cli-files; never mix packs.");
        manifest = candidates[0];
        files ??= Path.GetDirectoryName(manifest)!;
        // The resolver checks the chosen manifest's capabilities, GUIDs and exact DLL hashes before launch.
        CliCapabilityManifest.Read(manifest);
        return (manifest, files);
    }

    internal static string SteamUserdata(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("--steam-userdata", out string? named)) return Path.GetFullPath(named);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "userdata")]
            : OperatingSystem.IsMacOS()
                ? [Path.Combine(home, "Library", "Application Support", "Steam", "userdata")]
                : [Path.Combine(home, ".local", "share", "Steam", "userdata"), Path.Combine(home, ".steam", "steam", "userdata")];
        string[] found = candidates.Where(Directory.Exists).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (found.Length != 1)
            throw new DirectoryNotFoundException(found.Length == 0
                ? "Steam userdata was not found at the platform default. Give --steam-userdata for the account whose owned client will launch."
                : "Several Steam userdata directories exist. Give --steam-userdata explicitly; account state is not guessed.");
        return found[0];
    }
}
