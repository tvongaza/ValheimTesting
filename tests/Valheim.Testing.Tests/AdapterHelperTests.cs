using System.Globalization;
using System.Reflection;
using Valheim.Testing.Adapter;
using Xunit;

// The adapter package's helpers that need no game (compiled in from its source): exact reflection, reply fields and the
// fixture gate. The game-side helpers are only compiled in CI (tests/Valheim.Testing.Adapter.CompileCheck).
public class AdapterHelperTests
{
    private class Base { private int m_hidden = 7; private static bool s_flag = true; public int Hidden => m_hidden; public static bool Flag => s_flag; }
    private sealed class Derived : Base
    {
        private string? m_name = "zone";
        private object? m_socket;
        private int? m_maybe;
        private void Save(bool paintOnly) => Saved = paintOnly;
        private void Save(int count) => throw new InvalidOperationException("the wrong overload ran");
        private static void Boom() => throw new FormatException("from the game");
        public bool? Saved;
        public void Touch() { m_socket = null; m_maybe = null; }
        public override string ToString() => $"{m_name} {m_socket} {m_maybe}";
    }

    [Fact] public void FieldsAreFoundOnTheTypeOrItsBasesWhateverTheirAccess()
    {
        var value = new Derived(); value.Touch();
        Assert.Equal(7, Members.Field<int>(value, "m_hidden"));
        Assert.Equal("zone", Members.Field<string>(value, "m_name"));
        Assert.True(Members.StaticField<bool>(typeof(Base), "s_flag"));
        Assert.Null(Members.Field<object?>(value, "m_socket"));
        Assert.Null(Members.Field<int?>(value, "m_maybe"));
    }
    [Fact] public void AMissingOrMistypedFieldFailsLoudlyInsteadOfReadingADefault()
    {
        var value = new Derived();
        Assert.Throws<MissingFieldException>(() => Members.Field<int>(value, "m_width"));
        Assert.Throws<MissingFieldException>(() => Members.StaticField<bool>(typeof(Derived), "m_hidden")); // instance, not static
        Assert.Throws<InvalidCastException>(() => Members.Field<float>(value, "m_hidden"));
        Assert.Throws<InvalidCastException>(() => Members.Field<int>(value, "m_maybe")); // null is not an int
    }
    [Fact] public void MethodsMatchTheirExactParametersAndCallsRethrowWhatTheMethodThrew()
    {
        var value = new Derived();
        Members.Call(Members.Method(typeof(Derived), "Save", typeof(bool)), value, true);
        Assert.True(value.Saved);
        Assert.Throws<MissingMethodException>(() => Members.Method(typeof(Derived), "Save", typeof(float)));
        Assert.Throws<MissingMethodException>(() => Members.Method(typeof(Derived), "Save"));
        var error = Assert.Throws<FormatException>(() => Members.Call(Members.Method(typeof(Derived), "Boom"), null));
        Assert.Equal("from the game", error.Message);
    }
    [Fact] public void AMethodIsDescribedByItsTypeNameAndExactParameters()
    {
        Assert.Equal("AdapterHelperTests+Derived::Save(System.Boolean)", Members.Describe(Members.Method(typeof(Derived), "Save", typeof(bool))));
        Assert.Equal("System.String::Concat(System.String,System.String)", Members.Describe(typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!));
        Assert.Equal("System.Collections.Generic.List`1[System.Int32]::Add(System.Int32)", Members.Describe(typeof(List<int>).GetMethod("Add")!));
    }

    [Fact] public void ReplyFieldsKeepQuotedSpacesAndIgnoreOtherWords()
    {
        var fields = KeyValueReply.Parse("MYMOD_STATUS result=FIXED port='Port With Spaces' cost=20 empty= note=a=b");
        Assert.Equal("FIXED", fields["result"]); Assert.Equal("Port With Spaces", fields["port"]);
        Assert.Equal("20", fields["cost"]); Assert.Equal("", fields["empty"]); Assert.Equal("a=b", fields["note"]);
        Assert.Equal(5, fields.Count);
    }
    [Fact] public void ARepeatedFieldIsAnErrorNotAChoice() =>
        Assert.Throws<FormatException>(() => KeyValueReply.Parse("result=BUG_PRESENT result=FIXED"));
    [Fact] public void OnlyOneMatchingReplyAndNoErrorLineCountsAsAnAnswer()
    {
        Assert.Equal("2", KeyValueReply.Single(["noise", "OK: MYMOD_STATUS ports=2"], "OK: MYMOD_STATUS ")["ports"]);
        Assert.Throws<InvalidOperationException>(() => KeyValueReply.Single([], "OK: MYMOD_STATUS "));
        Assert.Throws<InvalidOperationException>(() => KeyValueReply.Single(["OK: MYMOD_STATUS a=1", "OK: MYMOD_STATUS a=2"], "OK: MYMOD_STATUS "));
        var error = Assert.Throws<InvalidOperationException>(() => KeyValueReply.Single(["ERROR: no world", "OK: MYMOD_STATUS a=1"], "OK: MYMOD_STATUS "));
        Assert.Equal("ERROR: no world", error.Message);
    }
    [Fact] public void ReplyFieldsDoNotDependOnTheCurrentCulture()
    {
        var old = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR"); Assert.Equal("1.5", KeyValueReply.Parse("I=1.5")["I"]); }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact] public void FixtureCommandsRefuseUnlessTheOwnedSessionTurnedThemOn()
    {
        string enable = "VT_TEST_FIXTURES_" + Guid.NewGuid().ToString("N"), token = "VT_TEST_TOKEN_" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Contains(enable + "=1", FixtureGate.Refusal(enable, token));
            Environment.SetEnvironmentVariable(enable, "true");
            Assert.NotNull(FixtureGate.Refusal(enable, token)); // Exactly 1, not anything truthy.
            Environment.SetEnvironmentVariable(enable, "1");
            Assert.Contains(token, FixtureGate.Refusal(enable, token)); // Enabled, but not an owned session.
            Environment.SetEnvironmentVariable(token, "abc123");
            Assert.Null(FixtureGate.Refusal(enable, token));
        }
        finally { Environment.SetEnvironmentVariable(enable, null); Environment.SetEnvironmentVariable(token, null); }
    }
}
