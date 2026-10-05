using System.Text.Json;
using System.Text.Json.Nodes;
using Valheim.Testing.Game;

/// <summary>Resolve optional local setup inputs without guessing among builds or account roots.</summary>
internal static class SmokeInputs
{
    /// <summary>
    /// The ValheimCLI core-and-pack set to stage, printed with where it came from: --cli-manifest/--cli-files, then
    /// VALHEIMCLI_BUNDLE (a folder with one manifest and its DLLs), then the pinned bundle this tool ships, extracted once under
    /// ValheimTesting's own folder. Whatever ValheimCLI an install happens to hold is never used unless named with --cli-files.
    /// </summary>
    internal static (string Manifest, string Files) Cli(IReadOnlyDictionary<string, string> options) => Cli(options, Shipped);

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

    // The bundle this tool was built with: cli-dependency.json's pinned commit and bundle hash, and the zip, both embedded.
    private static CliBundleSource? Shipped()
    {
        var tool = typeof(SmokeInputs).Assembly;
        using var zip = tool.GetManifestResourceStream("valheimcli-bundle.zip");
        using var pinStream = tool.GetManifestResourceStream("cli-dependency.json");
        if (zip == null || pinStream == null) return null;
        using var pin = System.Text.Json.JsonDocument.Parse(pinStream);
        if (!pin.RootElement.TryGetProperty("bundle", out var bundle)) return null;
        string commit = pin.RootElement.GetProperty("commit").GetString() ?? "";
        return CliBundle.Extract(zip, bundle.GetProperty("sha256").GetString()!, commit, $"the {commit[..Math.Min(7, commit.Length)]} bundle shipped with valheim-test");
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
    /// Steam), with --game and --loader-package as this machine's override. It must be on this machine. The chosen
    /// environment and its host are written to <c>environments.json</c> in <paramref name="output"/>, beside the run's
    /// <c>regression.json</c>, so the run's consumer and its bundle read the machine it used. <paramref name="shippedLoader"/> is the
    /// shipped-loader decision (<see cref="ShippedLoader.Instead(string, string)"/> in a real run; it reads the real install,
    /// so a test leaves it out).
    /// </summary>
    internal static (EnvironmentInventory Inventory, EnvironmentRecipe Client, ShippedLoader.Choice? ShippedLoader) Client(IReadOnlyDictionary<string, string> options, string output,
        Func<string, string, ShippedLoader.Choice?>? shippedLoader = null)
    {
        string? file = options.TryGetValue("--inventory", out string? named) ? Path.GetFullPath(named) : null;
        bool overrides = options.ContainsKey("--game") || options.ContainsKey("--loader-package");
        if (file != null && overrides)
            throw new ArgumentException("--game and --loader-package override this machine's client; with --inventory, set install and loaderPackage in the file.");
        string? overrideFile = null;
        if (overrides)
        {
            options.TryGetValue("--game", out string? game);
            if (game != null) SmokeOutput.RefuseInside(output, game);
            var entry = new JsonObject { ["name"] = "local-client", ["roles"] = new JsonArray("client") };
            if (game != null) entry["install"] = Path.GetFullPath(game);
            if (options.TryGetValue("--loader-package", out string? loader)) entry["loaderPackage"] = Path.GetFullPath(loader);
            overrideFile = Path.Combine(Path.GetTempPath(), "vt-start-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(overrideFile, new JsonObject { ["environments"] = new JsonArray(entry) }.ToJsonString());
        }
        EnvironmentInventory inventory;
        try { inventory = EnvironmentInventory.Read(file ?? overrideFile); }
        catch (ArgumentException failure) when (file == null)
        {
            throw new ArgumentException(failure.Message + (failure.Message.Contains("892970", StringComparison.Ordinal) ? " Give --game DIR." : ""), failure);
        }
        finally { if (overrideFile != null) File.Delete(overrideFile); }
        string? wanted = options.GetValueOrDefault("--client-env");
        var client = (wanted == null ? inventory.Environments.FirstOrDefault(recipe => recipe.Roles.Contains("client"))
                : inventory.Environments.FirstOrDefault(recipe => recipe.Name == wanted && recipe.Roles.Contains("client")))
            ?? throw new ArgumentException(wanted != null ? $"--client-env {wanted}: the inventory has no client environment of that name."
                : "The inventory has no client environment. " + string.Join(" ", inventory.Missing) + " Give --game DIR.");
        if (inventory.Hosts[client.Host].Kind != "local")
            throw new ArgumentException($"Client environment {client.Name} is on {client.Host}, not this machine. start runs its client here; name a client environment on this machine with --client-env.");
        // An install whose own Doorstop pair does not match takes the shipped BepInExPack in its disposable copy (one printed line).
        var shipped = client.LoaderPackage == null ? shippedLoader?.Invoke("client", client.Install) : null;
        if (shipped != null) client.LoaderPackage = shipped.Manifest;
        // The machine the run uses, as a one-environment inventory beside its inputs.
        var recorded = new EnvironmentInventory
        {
            Hosts = new() { [client.Host] = inventory.Hosts[client.Host] }, Environments = [client],
            LeaseHost = client.Host, LeaseDirectory = inventory.LeaseHost == client.Host && inventory.LeaseDirectory.Length != 0 ? inventory.LeaseDirectory
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(client.Runtime))!, "leases"),
        };
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "environments.json"), JsonSerializer.Serialize(recorded,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }) + "\n");
        return (inventory, client, shipped);
    }
}
