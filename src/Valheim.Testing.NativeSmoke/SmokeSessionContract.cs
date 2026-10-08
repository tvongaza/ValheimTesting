// The session adapter's protocol stays with NativeSmoke after its Mac-only runtime builders are removed.
internal static class SmokeSessionContract
{
    internal const string SessionAdapterPluginGuid = "valheim.testing.native-smoke";
    internal const string SessionCapability = "valheim.testing.native-smoke/session";
    internal const string SessionTokenVariable = "VT_NATIVE_SMOKE_SESSION_TOKEN";
    internal const string SelectedGuidsVariable = "VT_NATIVE_SMOKE_PLUGIN_GUIDS";
    internal const string PasswordVariable = "VT_NATIVE_SMOKE_JOIN_PASSWORD";
}
