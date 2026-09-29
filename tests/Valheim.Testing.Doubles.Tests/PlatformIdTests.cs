using System;
using System.Collections.Generic;
using System.Linq;
using Valheim.Testing.Doubles;
using Xunit;

// Steam and crossplay (PlayFab) peers and the game's platform-prefixed ids (1.0.16). The ids are made up; the expected
// text is written out from the game's formats, not produced by the double.
public sealed class PlatformIdTests
{
    private const string SteamId = "76561198000000002", XboxId = "2535400000000001";

    [Fact] public void SteamAndCrossplayPeersReportTheirIdsDifferently()
    {
        Assert.Equal(SteamId, new ZSteamSocket(76561198000000002).GetHostName());          // no prefix
        Assert.Equal(SteamId, new ZSteamSocket(76561198000000002).GetPeerID().ToString());
        var crossplaySteam = new ZPlayFabSocket("Steam_" + SteamId, "A1B2C3");
        Assert.Equal("Steam_" + SteamId, crossplaySteam.GetHostName());
        Assert.Equal("playfab/A1B2C3", crossplaySteam.GetEndPointString());
        Assert.Equal("Xbox_" + XboxId, new ZPlayFabSocket("Xbox_" + XboxId).GetHostName());
        Assert.Equal("Xbox_" + XboxId, new ZPlayFabSocket("X_" + XboxId).GetHostName());     // a display prefix names its platform
        Assert.Equal("", new ZPlayFabSocket(SteamId).GetHostName());                          // no prefix: does not parse
        Assert.Throws<NotImplementedException>(() => crossplaySteam.Flush());
    }

    [Fact] public void PlatformIdsParseAndDisplayAsTheGamesDo()
    {
        Assert.True(Splatform.PlatformUserID.TryParse("Steam_1_2", out var id));
        Assert.Equal(("Steam", "1_2"), (id.m_platform.ToString(), id.m_userID));              // split at the first underscore
        Assert.False(Splatform.PlatformUserID.TryParse("_1", out _));
        Assert.False(Splatform.PlatformUserID.TryParse("Steam_", out _));
        Assert.False(Splatform.PlatformUserID.TryParse(SteamId, out _));
        Assert.Equal("V_" + SteamId, Splatform.PlatformUserID.FilterPlatformUserID(new("Steam_" + SteamId)).ToString());
        // Console numbers are scrambled: 1 * 0x9E3779B97F4A7C15 and 2 * it, wrapping at 2^64.
        Assert.Equal("X_11400714819323198485", Splatform.PlatformUserID.FilterPlatformUserID(new("Xbox_1")).ToString());
        Assert.Equal("S_4354685564936845354", Splatform.PlatformUserID.FilterPlatformUserID(new("PlayStation_2")).ToString());
        Assert.Equal("PlayFab_ABC", Splatform.PlatformUserID.FilterPlatformUserID(new("PlayFab_ABC")).ToString()); // not a number: unchanged
    }

    [Fact] public void TheAdminListMatchesPlatformPrefixedIdsForEveryPeer()
    {
        using var scope = new ValheimWorldScope().WithNetwork();
        var net = ZNet.instance;
        string steamPeer = new ZSteamSocket(76561198000000002).GetHostName(), crossplayPeer = new ZPlayFabSocket("Steam_" + SteamId).GetHostName();
        string xboxPeer = new ZPlayFabSocket("Xbox_" + XboxId).GetHostName();

        net.m_adminList.Load("// List admin players ID  ONE per line\nSteam_" + SteamId + "\nXbox_" + XboxId + "\n");
        Assert.True(net.IsAdmin(steamPeer)); Assert.True(net.IsAdmin(crossplayPeer)); Assert.True(net.IsAdmin(xboxPeer));
        Assert.False(net.IsAdmin(XboxId));                          // the same digits from a Steam socket are a Steam id
        // Negative control: comparing the host name with the list's lines misses the Steam peer.
        Assert.False(net.m_adminList.Contains(steamPeer));

        net.m_adminList.Load(SteamId);                              // a bare Steam id still works, for both kinds of Steam peer
        Assert.True(net.IsAdmin(steamPeer)); Assert.True(net.IsAdmin(crossplayPeer)); Assert.False(net.IsAdmin(xboxPeer));
        net.m_adminList.Load(XboxId);                               // but not for other platforms
        Assert.False(net.IsAdmin(xboxPeer));
        net.m_adminList.Load("V_" + SteamId + "\nX_11400714819323198485"); // the ids as the game displays them
        Assert.True(net.IsAdmin(steamPeer)); Assert.True(net.IsAdmin(crossplayPeer)); Assert.True(net.IsAdmin(new ZPlayFabSocket("Xbox_1").GetHostName()));
        net.m_adminList.Load(" Steam_" + SteamId + "\n\nSteam_" + SteamId + " ");  // lines are not trimmed
        Assert.False(net.IsAdmin(steamPeer)); Assert.Equal(2, net.m_adminList.Count());
    }

    [Fact] public void BanAndPermitListsUseTheSameMatch()
    {
        using var scope = new ValheimWorldScope().WithNetwork();
        var net = ZNet.instance;
        net.m_bannedList.Add("Steam_" + SteamId);
        Assert.False(net.IsAllowed(SteamId, "Someone")); Assert.False(net.IsAllowed("Steam_" + SteamId, "Someone"));
        net.m_bannedList.Add("Griefer");
        Assert.False(net.IsAllowed("Xbox_" + XboxId, "Griefer"));   // a player name bans too
        Assert.Equal(new[] { "Steam_" + SteamId, "Griefer" }, net.Banned);
        net.m_permittedList.Add("Xbox_" + XboxId);
        Assert.True(net.IsAllowed("Xbox_" + XboxId, "Friend")); Assert.False(net.IsAllowed("Xbox_2535400000000009", "Stranger"));
    }

    /// <summary>A mod's admin-only command, checked the way that works for every peer.</summary>
    private static void RPC_ModClearArea(ZRpc rpc, string area, List<string> cleared)
    {
        if (!ZNet.instance.IsAdmin(rpc.GetSocket().GetHostName())) { rpc.Invoke("Mod_Refused", area); return; }
        cleared.Add(area);
    }
    /// <summary>The same command checked by casting the socket to Steam's, as older examples do.</summary>
    private static void RPC_ModClearAreaSteamOnly(ZRpc rpc, string area, List<string> cleared)
    {
        var steamId = ((ZSteamSocket)rpc.GetSocket()).GetPeerID();
        if (!ZNet.instance.m_adminList.Contains(steamId.ToString())) { rpc.Invoke("Mod_Refused", area); return; }
        cleared.Add(area);
    }

    // Issue #32's acceptance.
    [Fact] public void AModsAdminCheckPassesForSteamAndCrossplayPeersButASteamCastThrowsForCrossplay()
    {
        using var scope = new ValheimWorldScope().WithNetwork();
        var log = scope.CaptureLog();
        ZNet.instance.m_adminList.Add("Steam_" + SteamId);
        var cleared = new List<string>();
        var steamPeer = new ZNetPeer(new ZSteamSocket(76561198000000002), server: false);
        var crossplayPeer = new ZNetPeer(new ZPlayFabSocket("Steam_" + SteamId, "A1B2C3"), server: false);
        var stranger = new ZNetPeer(new ZPlayFabSocket("Steam_76561198000000009"), server: false);
        foreach (var peer in new[] { steamPeer, crossplayPeer, stranger })
            peer.m_rpc.Register<string>("Mod_ClearArea", (rpc, area) => RPC_ModClearArea(rpc, area, cleared));
        steamPeer.m_rpc.Deliver("Mod_ClearArea", "steam"); crossplayPeer.m_rpc.Deliver("Mod_ClearArea", "crossplay"); stranger.m_rpc.Deliver("Mod_ClearArea", "stranger");
        Assert.Equal(new[] { "steam", "crossplay" }, cleared);
        Assert.Equal(new[] { "Mod_Refused" }, stranger.m_rpc.Invoked.Select(c => c.Method));

        // Negative control: the Steam cast works for a Steam peer, throws for the crossplay one, and in an RPC handler
        // the game only logs that, so the admin's command silently does nothing.
        cleared.Clear();
        ZNet.instance.m_adminList.Add(SteamId);
        Assert.Throws<InvalidCastException>(() => RPC_ModClearAreaSteamOnly(crossplayPeer.m_rpc, "direct", cleared));
        foreach (var peer in new[] { steamPeer, crossplayPeer })
            peer.m_rpc.Register<string>("Mod_ClearAreaSteamOnly", (rpc, area) => RPC_ModClearAreaSteamOnly(rpc, area, cleared));
        Assert.Equal(ZRpc.ErrorCode.Success, steamPeer.m_rpc.Deliver("Mod_ClearAreaSteamOnly", "steam"));
        Assert.Equal(ZRpc.ErrorCode.Success, crossplayPeer.m_rpc.Deliver("Mod_ClearAreaSteamOnly", "crossplay"));
        Assert.Equal(new[] { "steam" }, cleared);
        Assert.IsType<InvalidCastException>(Assert.Single(crossplayPeer.m_rpc.Exceptions));
        Assert.Contains(log, l => l.StartsWith("Exception in ZRpc::HandlePackage: ") && l.Contains("InvalidCastException"));
    }

    // A crossplay server: the peer is a PlayFab socket, its host name carries the platform, and the connection starts
    // compressing once the versions match.
    [Fact] public void ACrossplayClientJoinsAndIsCheckedByItsPrefixedId()
    {
        using var scope = new ValheimWorldScope().WithNetwork(server: true).WithZdos();
        var server = ZNet.instance; server.Uid = 1; server.OnlineBackend = OnlineBackendType.PlayFab;
        Assert.Equal(OnlineBackendType.PlayFab, ZNet.m_onlineBackend);
        server.m_adminList.Add(SteamId);
        var client = new ZNet { Server = false, Uid = 2, OnlineBackend = OnlineBackendType.PlayFab };
        var serverEnd = new ZPlayFabSocket("Steam_" + SteamId, "A1B2C3"); var clientEnd = new ZPlayFabSocket("Steam_76561198000000001", "D4E5F6");
        SocketDouble.Link(serverEnd, clientEnd);
        var atServer = new ZNetPeer(serverEnd, server: false); server.OnNewConnection(atServer);
        var atClient = new ZNetPeer(clientEnd, server: true); client.OnNewConnection(atClient);
        for (int i = 0; i < 3; i++) { server.UpdatePeers(0f); client.UpdatePeers(0f); }
        Assert.Equal(ZNet.ConnectionStatus.Connected, client.Status);
        Assert.True(serverEnd.Compressing); Assert.True(clientEnd.Compressing);
        Assert.Same(atServer, server.GetPeerByHostName("Steam_" + SteamId));
        Assert.True(server.IsAdmin(server.GetPeer(2)!.m_socket.GetHostName()));

        // Banned by its crossplay id: refused with code 8.
        server.m_bannedList.Add("Xbox_" + XboxId);
        var xbox = new ZNet { Server = false, Uid = 3, OnlineBackend = OnlineBackendType.PlayFab };
        var xboxAtServer = new ZPlayFabSocket("Xbox_" + XboxId); var xboxEnd = new ZPlayFabSocket("Steam_76561198000000001");
        SocketDouble.Link(xboxAtServer, xboxEnd);
        server.OnNewConnection(new ZNetPeer(xboxAtServer, server: false)); xbox.OnNewConnection(new ZNetPeer(xboxEnd, server: true));
        for (int i = 0; i < 3; i++) { server.UpdatePeers(0f); xbox.UpdatePeers(0f); }
        Assert.Equal(ZNet.ConnectionStatus.ErrorBanned, xbox.Status); Assert.Equal(8, (int)xbox.Status);
        Assert.False(xboxAtServer.Compressing);
    }
}
