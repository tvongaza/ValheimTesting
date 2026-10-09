using System.Runtime.InteropServices;

namespace Valheim.Testing.Game;

/// <summary>The shared selected-client slice and loader check used before copying a game.</summary>
internal static class ClientArchitectureChoice
{
    internal static string Select(string? requested, string? environment, string platform, Architecture? localArchitecture)
    {
        string selected = !string.IsNullOrEmpty(requested) ? requested
            : !string.IsNullOrEmpty(environment) ? environment
            : EnvironmentInventory.DefaultClientArchitecture(platform, localArchitecture);
        if (selected is not ("x64" or "arm64"))
            throw new ArgumentException("--client-architecture must be x64 or arm64.");
        if (selected == "arm64" && platform != "macos")
            throw new ArgumentException("--client-architecture arm64 needs a macOS client environment.");
        return selected;
    }

    internal static void Require(string install, string selected, string? loaderManifest)
    {
        string? loader = loaderManifest == null ? null : BepInExLoaderPackage.Read(loaderManifest).Root;
        GameLaunch.RequireClientArchitecture(install,
            selected == "arm64" ? ClientArchitecture.Arm64 : ClientArchitecture.X64, loader);
    }
}
