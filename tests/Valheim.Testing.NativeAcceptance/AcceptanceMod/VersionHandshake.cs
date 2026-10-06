using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace AcceptanceMod;

/// <summary>
/// A version handshake adapted from the Valheim-Modding wiki's RPC-Version-Handshaking concept
/// (https://github.com/Valheim-Modding/Wiki/wiki/RPC-Version-Handshaking): on every new connection
/// each side registers an RPC and sends its version, and the server refuses a peer whose version differs with the game's
/// own "incompatible version" error (3), as the game refuses a client of another network version. One difference: AcceptanceMod
/// is server-side, so a client without AcceptanceMod (which never sends a version) is let in, and only a client with another AcceptanceMod
/// build is refused. The wiki's variant also refuses a client whose version has not arrived by the time its peer info
/// does; refusing only on a received mismatch has no such ordering race.
/// <para>
/// The net version comes from the build (<c>-p:AcceptanceModNetVersion=N</c>, default 1), so a mismatched build for the
/// <c>refused-join</c> scenario is one build command away.
/// </para>
/// </summary>
internal static class VersionHandshake
{
    public const string Rpc = "AcceptanceMod_Version";
    /// <summary>The game's error code for an incompatible version (<c>ZNet.ConnectionStatus.ErrorVersion</c>).</summary>
    public const int IncompatibleVersion = 3;
    public static readonly int NetVersion = ReadNetVersion();
    // Connections this server refused; a refused connection's ZRpc is never reused, so it stays listed.
    private static readonly HashSet<ZRpc> Refused = new HashSet<ZRpc>();

    private static int ReadNetVersion()
    {
        var attribute = typeof(VersionHandshake).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false)
            .Cast<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "AcceptanceModNetVersion");
        return attribute != null && int.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version) ? version : 0;
    }

    // Both sides: register the RPC on the new connection and send our version, before the game's own handshake.
    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    private static class SendVersion
    {
        private static void Prefix(ZNet __instance, ZNetPeer peer)
        {
            peer.m_rpc.Register<ZPackage>(Rpc, (rpc, received) => Received(__instance, rpc, received));
            var version = new ZPackage();
            version.Write(NetVersion);
            peer.m_rpc.Invoke(Rpc, version);
        }
    }

    private static void Received(ZNet net, ZRpc rpc, ZPackage package)
    {
        int theirs = package.ReadInt();
        if (theirs == NetVersion) return;
        if (!net.IsServer())
        {
            Plugin.Log.LogWarning($"The server runs AcceptanceMod net version {theirs} and this client {NetVersion}: the server refuses this client.");
            return;
        }
        Plugin.Log.LogWarning($"Refusing a client with AcceptanceMod net version {theirs}; this server runs {NetVersion}.");
        Refused.Add(rpc);
        rpc.Invoke("Error", IncompatibleVersion);
    }

    // Server: the game's peer info handling runs only for a connection that was not refused, so a refused client never
    // gets in, whatever it sends next.
    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    private static class RefuseMismatched
    {
        private static bool Prefix(ZNet __instance, ZRpc rpc) => !(__instance.IsServer() && Refused.Contains(rpc));
    }
}
