using Valheim.Testing.Game;

namespace Valheim.Testing.Tests;

public sealed class BepInExSettingsTests
{
    [Theory]
    [InlineData(false, false, false, BepInExSettingsOrigin.None)]
    [InlineData(true, false, false, BepInExSettingsOrigin.Source)]
    [InlineData(true, true, false, BepInExSettingsOrigin.LoaderPackage)]
    [InlineData(true, true, true, BepInExSettingsOrigin.Explicit)]
    [InlineData(true, false, true, BepInExSettingsOrigin.Explicit)]
    [InlineData(false, true, false, BepInExSettingsOrigin.LoaderPackage)]
    public void ConfigSelectionHasOnePrecedence(bool source, bool package, bool explicitConfig, BepInExSettingsOrigin expected)
        => Assert.Equal(expected, BepInExSettings.Choose(source, package, explicitConfig));
}
