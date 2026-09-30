using System.IO;
using BepInEx.Configuration;
using HarmonyLib;

namespace MyMod;

/// <summary>
/// One config entry the server decides for every client, synced with the game's routed RPCs and no library. The server
/// sends its value to each client that joins and to everyone when it changes (<c>mymod_greeting &lt;word&gt;</c> on the
/// server, as an admin would); a client uses the server's value while joined without writing it to its own config file,
/// and starts its next session from its file again. A client without MyMod ignores the RPC: the game drops a routed RPC
/// that nobody registered. MyMod only logs the value; a real mod would use it.
/// </summary>
internal static class SyncedGreeting
{
    public const string Rpc = "MyMod_Greeting";
    public const string Section = "Server", Key = "Greeting";
    private static ConfigFile _file = null!;
    private static ConfigEntry<string> _entry = null!;

    public static string Value => _entry.Value;

    internal static void Bind(ConfigFile config)
    {
        _file = config;
        _entry = config.Bind(Section, Key, "hello", "A word the server decides for every joined client: its value wins over the client's.");
        _entry.SettingChanged += (_, _) =>
        {
            // Only the server broadcasts; a client's entry changes when the server's value arrives.
            if (ZNet.instance != null && ZNet.instance.IsServer() && ZRoutedRpc.instance != null)
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, Rpc, _entry.Value);
        };
    }

    /// <summary>The server's admin change: saved to the server's config file and sent to every client.</summary>
    internal static void Set(string value) => _entry.Value = value;

    // Each session creates its own ZNet and routed RPC manager: register the handler there.
    [HarmonyPatch(typeof(ZNet), "Awake")]
    private static class RegisterRpc
    {
        private static void Postfix(ZNet __instance)
        {
            // A client starts from its own file, never from the last server's value.
            if (!__instance.IsServer() && File.Exists(_file.ConfigFilePath)) _file.Reload();
            ZRoutedRpc.instance.Register<string>(Rpc, Received);
            // The game calls this once a joining peer can receive routed RPCs, after the server sent it the world.
            if (__instance.IsServer()) ZRoutedRpc.instance.m_onNewPeer += peer => ZRoutedRpc.instance.InvokeRoutedRPC(peer, Rpc, _entry.Value);
        }
    }

    private static void Received(long sender, string value)
    {
        var net = ZNet.instance;
        string role = net == null ? "no session" : !net.IsServer() ? "client" : net.IsDedicated() ? "server" : "host";
        Plugin.Log.LogInfo($"Greeting \"{value}\" received from {sender} ({role})");
        // A broadcast also runs on the server itself (a host is server and client in one process): it already has the value.
        if (net == null || net.IsServer()) return;
        bool save = _file.SaveOnConfigSet;
        _file.SaveOnConfigSet = false; // The server's value is not the client's own setting.
        try { _entry.Value = value; }
        finally { _file.SaveOnConfigSet = save; }
    }
}
