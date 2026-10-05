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

    /// <summary>--steam-userdata, or this machine's, under the Steam root the inventory's detection found (on Windows its registered path).</summary>
    internal static string SteamUserdata(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("--steam-userdata", out string? named)) return Path.GetFullPath(named);
        EnvironmentInventory inventory;
        try { inventory = EnvironmentInventory.Read(null); }
        catch (ArgumentException failure) { throw new DirectoryNotFoundException("Steam userdata was not found: " + failure.Message + " Give --steam-userdata DIR for the account whose owned client will launch."); }
        string userdata = inventory.SteamUserData ?? throw new DirectoryNotFoundException("Steam userdata was not found under " +
            string.Join("; ", inventory.Detected.Where(line => line.StartsWith("Steam:", StringComparison.Ordinal))) + ". Give --steam-userdata.");
        Console.WriteLine($"steam userdata: {userdata} (detected: this machine)");
        return userdata;
    }

    /// <summary>This machine's Valheim install, as the inventory's detection finds it; printed with where it came from.</summary>
    internal static string Game()
    {
        EnvironmentInventory inventory;
        try { inventory = EnvironmentInventory.Read(null); }
        catch (ArgumentException failure) { throw new DirectoryNotFoundException(failure.Message + " Give --game DIR."); }
        string game = inventory.Environments.FirstOrDefault(recipe => recipe.Roles.Contains("client"))?.Install
            ?? throw new DirectoryNotFoundException(string.Join(" ", inventory.Missing.Where(line => line.Contains("892970"))) + " Give --game DIR.");
        Console.WriteLine($"game: {game} (detected: this machine)");
        return game;
    }
}
