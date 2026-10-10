using Valheim.Testing.Game;

/// <summary>Builds the same dependency request shape for each one-shot run and comparison arm.</summary>
internal static class SmokeDependencyInputs
{
    internal static NativeDependencyRequest Request(IEnumerable<string> mods, string gameInstall, string bepinExCore,
        string cliManifest, string cliFiles, IEnumerable<string> searchRoots, IEnumerable<string> optionalReferences,
        IEnumerable<string> capabilities) => new()
    {
        Mods = mods.Select(Path.GetFullPath).ToList(),
        GameManaged = Path.GetDirectoryName(InstallPins.GameAssembly(gameInstall))!,
        BepInExCore = bepinExCore,
        CliManifest = cliManifest,
        CliFiles = cliFiles,
        SearchRoots = searchRoots.Select(Path.GetFullPath).ToList(),
        OptionalReferences = optionalReferences.ToList(),
        Capabilities = capabilities.ToList(),
        StageAllCliPacks = true,
    };

    internal static string Gaps(NativeDependencyLock value) =>
        string.Join("; ", value.Gaps.Select(gap => gap.Kind + " " + gap.Name + ": " + gap.Reason));
}
