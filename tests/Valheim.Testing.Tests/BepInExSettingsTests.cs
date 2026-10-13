using Valheim.Testing.Game;
using Xunit;

namespace Valheim.Testing.Tests;

[Trait("Os", "neutral")]
public sealed class BepInExSettingsTests
{
    // #711: an explicit file wins; a loader package brings its own default or, on Windows and Linux, none: the install's is
    // never carried onto a package there. macOS keeps the install's file beside a package without one (#544).
    // The package column: null without a package, else whether it holds BepInEx.cfg.
    [Theory]
    [InlineData(false, null, false, false, "None")]
    [InlineData(true, null, false, false, "Source")]
    [InlineData(true, true, false, false, "LoaderPackage")]
    [InlineData(true, false, false, false, "None")]
    [InlineData(true, false, false, true, "Source")]
    [InlineData(false, false, false, true, "None")]
    [InlineData(true, true, false, true, "LoaderPackage")]
    [InlineData(true, true, true, false, "Explicit")]
    [InlineData(true, null, true, true, "Explicit")]
    [InlineData(false, true, false, false, "LoaderPackage")]
    public void ConfigSelectionHasOnePrecedence(bool source, bool? package, bool explicitConfig, bool macOS, string expected)
        => Assert.Equal(Enum.Parse<BepInExSettingsOrigin>(expected), BepInExSettings.Choose(source, package, explicitConfig, macOS));
}
