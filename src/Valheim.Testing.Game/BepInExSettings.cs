namespace Valheim.Testing.Game;

internal enum BepInExSettingsOrigin { None, Source, LoaderPackage, Explicit }

// Where a disposable runtime's BepInEx.cfg comes from, the same whether a local one-shot, an owned client, or a hosted
// campaign stages it (#711): one the plan selects explicitly wins; else a loader package's (the pinned pack's default).
// A Windows or Linux runtime on a package never takes the install's. A macOS runtime keeps the install's file when it runs
// on the install's own loader, or on an arm64 package without one (#544: the arm64 entrypoint setting lives there).
internal static class BepInExSettings
{
    internal const string RelativePath = "BepInEx/config/BepInEx.cfg";

    /// <summary><paramref name="packageExists"/> is whether the loader package holds the file; null when there is no package.</summary>
    internal static BepInExSettingsOrigin Choose(bool sourceExists, bool? packageExists, bool explicitExists, bool macOS)
        => explicitExists ? BepInExSettingsOrigin.Explicit
            : packageExists == true ? BepInExSettingsOrigin.LoaderPackage
            : sourceExists && (packageExists == null || macOS) ? BepInExSettingsOrigin.Source
            : BepInExSettingsOrigin.None;
}
