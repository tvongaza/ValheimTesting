using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;

/// <summary>One environment and loader choice for hosted and dedicated-server one-shot runs.</summary>
internal static class SmokeInputResolver
{
    internal sealed record LocalOverride(string Name, string Role, string? Install, string? Loader = null);

    // A one-shot command records only the recipes it selected. Campaign and regression
    // consumers read this frozen choice; a changed source inventory cannot select again.
    internal static string RecordSelected(EnvironmentInventory inventory, string output,
        IEnumerable<EnvironmentRecipe> selected)
    {
        string path = Path.Combine(output, "environments.json");
        var recorded = new EnvironmentInventory
        {
            Hosts = inventory.Hosts,
            Environments = selected.ToList(),
            LeaseHost = inventory.LeaseHost,
            LeaseDirectory = inventory.LeaseDirectory,
        };
        Directory.CreateDirectory(output);
        File.WriteAllText(path, JsonSerializer.Serialize(recorded, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }) + "\n");
        return path;
    }

    internal static (EnvironmentInventory Inventory, string? File) ReadInventory(
        IReadOnlyDictionary<string, string> options, string output, IReadOnlyList<LocalOverride> overrides,
        IReadOnlyList<string> overrideOptions, bool keepOverrideFile, string conflictMessage)
    {
        string? file = options.TryGetValue("--inventory", out string? named) ? Path.GetFullPath(named) : null;
        bool changed = overrideOptions.Any(options.ContainsKey);
        if (file != null && changed) throw new ArgumentException(conflictMessage);
        string? temporary = null;
        if (changed)
        {
            SmokeOutput.RefuseInside(output, overrides.Select(recipe => recipe.Install).OfType<string>().ToArray());
            var entries = new JsonArray();
            foreach (var recipe in overrides)
            {
                var entry = new JsonObject { ["name"] = recipe.Name, ["roles"] = new JsonArray(recipe.Role) };
                if (recipe.Install != null) entry["install"] = Path.GetFullPath(recipe.Install);
                if (recipe.Loader != null) entry["loaderPackage"] = Path.GetFullPath(recipe.Loader);
                entries.Add(entry);
            }
            if (keepOverrideFile) Directory.CreateDirectory(output);
            file = keepOverrideFile ? Path.Combine(output, "environments.json")
                : temporary = Path.Combine(Path.GetTempPath(), "vt-env-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(file, new JsonObject { ["environments"] = entries }
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        try { return (EnvironmentInventory.Read(file), keepOverrideFile ? file : named == null ? null : Path.GetFullPath(named)); }
        finally { if (temporary != null) File.Delete(temporary); }
    }

    internal static EnvironmentRecipe Pick(EnvironmentInventory inventory, string role, string? wanted,
        string option, string source, string hint, bool localOnly, string command, string? remoteHint = null)
    {
        var recipe = (wanted == null
            ? inventory.Environments.FirstOrDefault(candidate => candidate.Roles.Contains(role))
            : inventory.Environments.FirstOrDefault(candidate => candidate.Name == wanted && candidate.Roles.Contains(role)))
            ?? throw new ArgumentException(wanted != null
                ? $"{option} {wanted}: the inventory ({source}) has no {role} environment of that name."
                : $"The inventory ({source}) has no {role} environment. " +
                  (inventory.Missing.Count == 0 ? "" : string.Join(" ", inventory.Missing) + " ") + hint);
        if (localOnly && inventory.Hosts[recipe.Host].Kind != "local")
            throw new ArgumentException($"{role} environment {recipe.Name} is on {recipe.Host}, not this machine. " +
                (remoteHint ?? $"{command} runs its {role} here; choose a local {role} environment with {option}."));
        return recipe;
    }

    internal static (string? Manifest, ShippedLoader.Choice? Shipped) Loader(string role, EnvironmentRecipe recipe,
        string? explicitManifest, Func<string, string, ShippedLoader.Choice?>? shipped)
    {
        string? manifest = explicitManifest == null ? recipe.LoaderPackage : Path.GetFullPath(explicitManifest);
        var automatic = manifest == null ? shipped?.Invoke(role, recipe.Install) : null;
        return (manifest ?? automatic?.Manifest, automatic);
    }

    internal static void RecordLoader(IDictionary<string, string> provenance, string role,
        string? manifest, ShippedLoader.Choice? automatic)
    {
        if (manifest != null) provenance[role + "LoaderPackage"] = BepInExLoaderPackage.Read(manifest).Identity;
        if (automatic != null) provenance[role + "LoaderShipped"] = automatic.Reason;
    }

    internal static string SelectClientArchitecture(string? option, EnvironmentRecipe recipe)
    {
        string selected = option ?? recipe.Architecture;
        if (selected is not ("x64" or "arm64"))
            throw new ArgumentException("--client-architecture must be x64 or arm64.");
        return selected;
    }

    internal static void RequireClientArchitecture(EnvironmentInventory inventory, EnvironmentRecipe recipe,
        string selected, string? loaderManifest)
    {
        if (selected == "arm64" && inventory.Hosts[recipe.Host].Platform != "macos")
            throw new ArgumentException("--client-architecture arm64 needs a macOS client environment.");
        if (inventory.Hosts[recipe.Host].Platform != "macos") return;
        if (inventory.Hosts[recipe.Host].Kind != "local") return; // Campaign host preflight checks remote installs.
        string? loader = loaderManifest == null ? null : BepInExLoaderPackage.Read(loaderManifest).Root;
        try { GameLaunch.RequireClientArchitecture(recipe.Install,
            selected == "arm64" ? ClientArchitecture.Arm64 : ClientArchitecture.X64, loader); }
        catch (Exception failure) when (failure is IOException or InvalidOperationException)
        { throw new InvalidOperationException($"Client environment {recipe.Name} cannot launch as {selected}: {failure.Message}", failure); }
    }
}
