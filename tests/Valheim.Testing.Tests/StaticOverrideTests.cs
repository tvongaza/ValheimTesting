using Valheim.Testing;
using Xunit;

// Only this class touches these statics, and xUnit runs one class's tests in sequence.
public class StaticOverrideTests
{
    private static class Settings
    {
        public static float Meander = 3f;
        public static bool Enabled { get; set; } = true;
        public static string? Source { get; private set; } = "mod";
        public static int Computed => 7;
        public static readonly int Fixed = 1;
        public const int Constant = 2;
        public static bool Locked;
        private static int _guarded = 4;
        public static int Guarded { get => _guarded; set { if (Locked) throw new InvalidOperationException("locked"); _guarded = value; } }
    }

    [Fact] public void RestoresThePreviousValueNotADefault()
    {
        Settings.Meander = 5f;
        try
        {
            using (StaticOverride.Set(() => Settings.Meander, 0f)) Assert.Equal(0f, Settings.Meander);
            Assert.Equal(5f, Settings.Meander);
        }
        finally { Settings.Meander = 3f; }
    }
    [Fact] public void RestoresFieldsAndPropertiesIncludingPrivateSettersWhenTheTestThrows()
    {
        Assert.Throws<InvalidOperationException>(() => Fail());
        Assert.Equal(3f, Settings.Meander); Assert.True(Settings.Enabled); Assert.Equal("mod", Settings.Source);

        static void Fail()
        {
            using var plain = StaticOverride.Set(() => Settings.Meander, 0f).And(() => Settings.Enabled, false).And(() => Settings.Source, null);
            Assert.Equal(0f, Settings.Meander); Assert.False(Settings.Enabled); Assert.Null(Settings.Source);
            throw new InvalidOperationException("test failed");
        }
    }
    [Fact] public void KeepRestoresWhatTheCodeUnderTestChanged()
    {
        using (StaticOverride.Keep(() => Settings.Meander)) Settings.Meander = 99f;
        Assert.Equal(3f, Settings.Meander);
    }
    [Fact] public void TheSameMemberTwiceEndsAtTheOriginalValue()
    {
        using (StaticOverride.Set(() => Settings.Meander, 1f).And(() => Settings.Meander, 2f)) Assert.Equal(2f, Settings.Meander);
        Assert.Equal(3f, Settings.Meander);
    }
    [Fact] public void EnvironmentVariablesAreSetAndRemovedOrRestored()
    {
        string absent = "VT_OVERRIDE_" + Guid.NewGuid().ToString("N"), present = "VT_OVERRIDE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(present, "before");
        try
        {
            using (StaticOverride.Environment(absent, "1").AndEnvironment(present, null))
            {
                Assert.Equal("1", Environment.GetEnvironmentVariable(absent)); Assert.Null(Environment.GetEnvironmentVariable(present));
            }
            Assert.Null(Environment.GetEnvironmentVariable(absent)); Assert.Equal("before", Environment.GetEnvironmentVariable(present));
        }
        finally { Environment.SetEnvironmentVariable(present, null); }
    }
    [Fact] public void ARestoreThatThrowsIsReportedAfterEveryOtherValueIsRestored()
    {
        var overrides = StaticOverride.Set(() => Settings.Meander, 0f).And(() => Settings.Guarded, 9).And(() => Settings.Enabled, false);
        Settings.Locked = true;
        try
        {
            var error = Assert.Throws<AggregateException>(overrides.Dispose);
            Assert.Contains("Settings.Guarded", Assert.Single(error.InnerExceptions).Message);
            Assert.Equal(3f, Settings.Meander); Assert.True(Settings.Enabled);
            overrides.Dispose(); // Only the first dispose restores.
        }
        finally { Settings.Locked = false; Settings.Guarded = 4; }
    }
    [Fact] public void OnlyWritableStaticMembersAreAccepted()
    {
        Assert.Throws<ArgumentException>(() => StaticOverride.Set(() => Settings.Computed, 1));
        Assert.Throws<ArgumentException>(() => StaticOverride.Set(() => Settings.Fixed, 2));
        Assert.Throws<ArgumentException>(() => StaticOverride.Set(() => Settings.Constant, 3));
        Assert.Throws<ArgumentException>(() => StaticOverride.Set(() => "text".Length, 4));
        Assert.Throws<ArgumentException>(() => StaticOverride.Environment("A=B", "1"));
    }
}
