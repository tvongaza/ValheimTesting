using Valheim.Testing.Game;

internal static class ClientArchitectureChoice
{
    internal static string Select(string? option, EnvironmentRecipe recipe)
    {
        string selected = option ?? recipe.Architecture;
        if (selected is not ("x64" or "arm64"))
            throw new ArgumentException("--client-architecture must be x64 or arm64.");
        return selected;
    }

    internal static void RequireLocal(EnvironmentInventory inventory, EnvironmentRecipe recipe, string selected, string? loaderManifest)
    {
        if (selected == "arm64" && inventory.Hosts[recipe.Host].Platform != "macos")
            throw new ArgumentException("--client-architecture arm64 needs a macOS client environment.");
        if (inventory.Hosts[recipe.Host].Platform != "macos") return;
        if (inventory.Hosts[recipe.Host].Kind != "local") return; // The campaign's host preflight checks remote installs.
        string? loader = loaderManifest == null ? null : BepInExLoaderPackage.Read(loaderManifest).Root;
        try { GameLaunch.RequireClientArchitecture(recipe.Install,
            selected == "arm64" ? ClientArchitecture.Arm64 : ClientArchitecture.X64, loader); }
        catch (Exception failure) when (failure is IOException or InvalidOperationException)
        { throw new InvalidOperationException($"Client environment {recipe.Name} cannot launch as {selected}: {failure.Message}", failure); }
    }
}
