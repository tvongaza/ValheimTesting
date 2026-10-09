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
                      ". Choose one build with --cli-manifest and --cli-files; never mix packs.");
            manifest = candidates[0];
        }
        files ??= Path.GetDirectoryName(manifest)!;
        // The resolver checks the chosen manifest's capabilities, GUIDs and exact DLL hashes before launch.
        CliCapabilityManifest.Read(manifest);
        Console.WriteLine($"ValheimCLI: {manifest} ({origin})");
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

    /// <summary>
    /// start's client environment: --client-env, or the first, of the --inventory file's or this machine's (Valheim from
    /// Steam), with --game as this machine's install override and --client-loader-package for the selected client's loader. It must be on this machine. The chosen
    /// environment and its host are written to <c>environments.json</c> in <paramref name="output"/>, beside the run's
    /// <c>regression.json</c>, so the run's consumer and its bundle read the machine it used. <paramref name="shippedLoader"/> is the
    /// shipped-loader decision (<see cref="ShippedLoader.Instead(string, string)"/> in a real run; it reads the real install,
    /// so a test leaves it out).
    /// </summary>
    internal static (EnvironmentInventory Inventory, EnvironmentRecipe Client, ShippedLoader.Choice? ShippedLoader) Client(IReadOnlyDictionary<string, string> options, string output,
        Func<string, string, ShippedLoader.Choice?>? shippedLoader = null, Action? requireMacGui = null)
    {
        EnvironmentInventory inventory;
        string? file;
        try
        {
            (inventory, file) = SmokeInputResolver.ReadInventory(options, output,
                [new SmokeInputResolver.LocalOverride("local-client", "client", options.GetValueOrDefault("--game"))],
                ["--game"], keepOverrideFile: false,
                "--game overrides this machine's client; with --inventory, set its install in the file. --client-loader-package may still override the selected client's loader.");
        }
        catch (ArgumentException failure) when (!options.ContainsKey("--inventory"))
        {
            throw new ArgumentException(failure.Message + (failure.Message.Contains("892970", StringComparison.Ordinal) ? " Give --game DIR." : ""), failure);
        }
        string? wanted = options.GetValueOrDefault("--client-env");
        var client = SmokeInputResolver.Pick(inventory, "client", wanted, "--client-env", file ?? "this machine",
            "Give --game DIR.", localOnly: true, command: "start");
        // Refuse a locked or non-console Mac before creating run evidence or a disposable install.
        // The optional probe lets every test host prove the refusal order without an actual GUI session.
        if (requireMacGui != null) requireMacGui();
        else if (OperatingSystem.IsMacOS()) MacGuiSession.Require();
        // An install whose own Doorstop pair does not match takes the shipped BepInExPack in its disposable copy (one printed line).
        var (clientManifest, shipped) = SmokeInputResolver.Loader("client", client,
            options.GetValueOrDefault("--client-loader-package"), shippedLoader);
        client.LoaderPackage = clientManifest;
        // The machine the run uses, as a one-environment inventory beside its inputs.
        var recorded = new EnvironmentInventory
        {
            Hosts = new() { [client.Host] = inventory.Hosts[client.Host] }, Environments = [client],
            LeaseHost = client.Host, LeaseDirectory = inventory.LeaseHost == client.Host && inventory.LeaseDirectory.Length != 0 ? inventory.LeaseDirectory
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(client.Runtime))!, "leases"),
        };
        SmokeInputResolver.RecordSelected(recorded, output, [client]);
        return (inventory, client, shipped);
    }
}
