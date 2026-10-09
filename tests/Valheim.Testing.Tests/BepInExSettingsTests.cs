using Valheim.Testing.Game;
using Xunit;

namespace Valheim.Testing.Tests;

public sealed class BepInExSettingsTests
{
    [Theory]
    [InlineData(false, false, false, "None")]
    [InlineData(true, false, false, "Source")]
    [InlineData(true, true, false, "LoaderPackage")]
    [InlineData(true, true, true, "Explicit")]
    [InlineData(true, false, true, "Explicit")]
    [InlineData(false, true, false, "LoaderPackage")]
    public void ConfigSelectionHasOnePrecedence(bool source, bool package, bool explicitConfig, string expected)
        => Assert.Equal(Enum.Parse<BepInExSettingsOrigin>(expected), BepInExSettings.Choose(source, package, explicitConfig));
}
