namespace Valheim.Testing.Game;

internal enum BepInExSettingsOrigin { None, Source, LoaderPackage, Explicit }

// The same precedence applies whether a local one-shot, an owned client, or a hosted
// campaign stages a disposable runtime. A loader without a config does not suppress
// the source config; an explicitly selected config wins over either one.
internal static class BepInExSettings
{
    internal const string RelativePath = "BepInEx/config/BepInEx.cfg";

    internal static BepInExSettingsOrigin Choose(bool sourceExists, bool packageExists, bool explicitExists)
        => explicitExists ? BepInExSettingsOrigin.Explicit
            : packageExists ? BepInExSettingsOrigin.LoaderPackage
            : sourceExists ? BepInExSettingsOrigin.Source
            : BepInExSettingsOrigin.None;
}
