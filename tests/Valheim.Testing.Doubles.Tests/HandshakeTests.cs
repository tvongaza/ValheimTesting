using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Valheim.Testing.Doubles;
using Xunit;

// Direct peer RPCs and the join handshake against the game's 1.0.16 rules, with a server and a client ZNet in one test.
// Error codes and status names are written out from the game's ConnectionStatus, not taken from the double.
public sealed class HandshakeTests
{
    private const ulong ServerSteamId = 76561198000000001, ClientSteamId = 76561198000000002;

    private static ValheimWorldScope Network() => new ValheimWorldScope().WithNetwork(server: true).WithZdos();
    /// <summary>The server is <c>ZNet.instance</c> (id 1); the client is another ZNet (id 2) standing for the other process.</summary>
    private static (ZNet Server, ZNet Client) Sides()
    {
        ZNet.instance.Uid = 1;
        return (ZNet.instance, new ZNet { Server = false, Uid = 2 });
    }

    /// <summary>Opens a connection as the game does on each side; the prefixes stand for a mod's patches on OnNewConnection.</summary>
    private static (ZNetPeer AtServer, ZNetPeer AtClient) Connect(ZNet server, ZNet client, SocketDouble serverEnd, SocketDouble clientEnd,
        Action<ZNetPeer>? serverPrefix = null, Action<ZNetPeer>? clientPrefix = null)
    {
        SocketDouble.Link(serverEnd, clientEnd);
        var atServer = new ZNetPeer(serverEnd, server: false);
        serverPrefix?.Invoke(atServer); server.OnNewConnection(atServer);
        var atClient = new ZNetPeer(clientEnd, server: true);
        clientPrefix?.Invoke(atClient); client.OnNewConnection(atClient);
        return (atServer, atClient);
    }
    private static (ZNetPeer AtServer, ZNetPeer AtClient) ConnectSteam(ZNet server, ZNet client, ulong clientId = ClientSteamId,
        Action<ZNetPeer>? serverPrefix = null, Action<ZNetPeer>? clientPrefix = null) =>
        Connect(server, client, new ZSteamSocket(clientId), new ZSteamSocket(ServerSteamId), serverPrefix, clientPrefix);

    /// <summary>Each side handles what has arrived, server first, as frames would.</summary>
    private static void Frames(ZNet server, ZNet client, int count = 3)
    { for (int i = 0; i < count; i++) { server.UpdatePeers(0f); client.UpdatePeers(0f); } }

    private static string[] Calls(ZNetPeer peer) => peer.m_rpc.Invoked.Select(c => c.Method).ToArray();

    [Fact] public void AMatchingClientJoinsThroughTheGamesHandshake()
    {
        using var scope = Network(); var (server, client) = Sides();
        client.PlayerName = "Tester";
        var (atServer, atClient) = ConnectSteam(server, client);
        Assert.Equal(ZNet.ConnectionStatus.Connecting, client.Status);
        Assert.False(atServer.IsReady()); Assert.Empty(server.GetPeers().Where(p => p.IsReady()));
        Frames(server, client);
        Assert.Equal(ZNet.ConnectionStatus.Connected, client.Status);
        Assert.Equal(new[] { "ServerHandshake", "PeerInfo" }, Calls(atClient));
        Assert.Equal(new[] { "ClientHandshake", "PeerInfo" }, Calls(atServer));
        Assert.Same(atServer, server.GetPeer(2)); Assert.Equal("Tester", atServer.m_playerName);
        Assert.Same(atClient, client.GetPeer(1));
        Assert.Equal(ClientSteamId.ToString(), atServer.m_socket.GetHostName());
    }

    [Fact] public void ANetworkVersionMismatchIsRefusedWithCode3AndTheServerStillTakesAMatchingClient()
    {
        using var scope = Network(); var (server, client) = Sides();
        var log = scope.CaptureLog();
        client.NetworkVersion = 39;
        var (refused, _) = ConnectSteam(server, client);
        Frames(server, client);
        Assert.Equal(ZNet.ConnectionStatus.ErrorVersion, client.Status);
        Assert.Equal(3, (int)client.Status);
        Assert.Equal(("Error", 3), (refused.m_rpc.Invoked.Last().Method, (int)refused.m_rpc.Invoked.Last().Args[0]));
        Assert.False(refused.IsReady());
        Assert.Contains(log, l => l.StartsWith($"Peer {ClientSteamId} has incompatible version, mine:{DoubledGame.Version} (network version 40)   remote {DoubledGame.Version} (network version 39)"));

        var second = new ZNet { Server = false, Uid = 3 };
        var (joined, _) = ConnectSteam(server, second, clientId: 76561198000000003);
        Frames(server, second);
        Assert.Equal(ZNet.ConnectionStatus.Connected, second.Status);
        Assert.Same(joined, server.GetPeer(3));
    }

    // A client older than 0.214.301 sends no network version, so the server reads 0.
    [Fact] public void AClientThatSendsNoNetworkVersionIsRefused()
    {
        using var scope = Network(); var (server, client) = Sides();
        var log = scope.CaptureLog();
        client.VersionString = "0.214.300";
        ConnectSteam(server, client);
        Frames(server, client);
        Assert.Equal(ZNet.ConnectionStatus.ErrorVersion, client.Status);
        Assert.Contains(log, l => l.EndsWith("remote 0.214.300 (network version 0)"));
    }

    [Fact] public void TheServerChecksBansThePasswordAndASecondConnection()
    {
        using var scope = Network(); var (server, client) = Sides();
        server.m_bannedList.Add(ClientSteamId.ToString());
        ConnectSteam(server, client); Frames(server, client);
        Assert.Equal(ZNet.ConnectionStatus.ErrorBanned, client.Status); Assert.Equal(8, (int)client.Status);

        server.m_bannedList.Remove(ClientSteamId.ToString());
        server.RefusePassword = true; // stands for a wrong password; passwords themselves are not modelled
        var wrong = new ZNet { Server = false, Uid = 2 };
        var (refused, _) = ConnectSteam(server, wrong); Frames(server, wrong);
        Assert.Equal(ZNet.ConnectionStatus.ErrorPassword, wrong.Status); Assert.Equal(6, (int)wrong.Status);
        Assert.False(refused.IsReady());

        server.RefusePassword = false;
        var right = new ZNet { Server = false, Uid = 2 };
        ConnectSteam(server, right); Frames(server, right);
        Assert.Equal(ZNet.ConnectionStatus.Connected, right.Status);

        var again = new ZNet { Server = false, Uid = 2 };
        ConnectSteam(server, again); Frames(server, again);
        Assert.Equal(ZNet.ConnectionStatus.ErrorAlreadyConnected, again.Status); Assert.Equal(7, (int)again.Status);
    }

    /// <summary>
    /// A mod's version handshake, written as a mod patches it in: each side registers and sends its version when a
    /// connection opens (a prefix on OnNewConnection), and a prefix on RPC_PeerInfo stops a peer the server has not
    /// validated. <see cref="Defer"/> false refuses at once; true holds the peer info until the version arrives, then lets
    /// the game's handler run. Sent from the prefix, the version precedes ServerHandshake on the same ordered connection,
    /// so it always arrives before the peer info; a mod that sends it later is what the late case below stands for.
    /// </summary>
    private sealed class ModVersionCheck
    {
        private readonly ZNet m_net; private readonly string m_version; public bool Defer;
        public readonly HashSet<ZRpc> Validated = new();
        private readonly Dictionary<ZRpc, ZPackage> m_held = new();
        public ModVersionCheck(ZNet net, string version) { m_net = net; m_version = version; }

        public void OnNewConnection(ZNetPeer peer, bool sendNow = true)
        { peer.m_rpc.Register<ZPackage>("TestMod_Version", RPC_Version); if (sendNow) SendVersion(peer.m_rpc); }
        public void SendVersion(ZRpc rpc) { var pkg = new ZPackage(); pkg.Write(m_version); rpc.Invoke("TestMod_Version", pkg); }
        /// <summary>After the game's OnNewConnection: what a Harmony prefix on RPC_PeerInfo amounts to.</summary>
        public void PatchPeerInfo(ZNetPeer peer) =>
            peer.m_rpc.Register<ZPackage>("PeerInfo", (rpc, pkg) => { if (PeerInfoPrefix(rpc, pkg)) m_net.RPC_PeerInfo(rpc, pkg); });

        private bool PeerInfoPrefix(ZRpc rpc, ZPackage pkg)
        {
            if (!m_net.IsServer() || Validated.Contains(rpc)) return true;
            if (Defer) { m_held[rpc] = pkg; return false; }
            rpc.Invoke("Error", 3);
            return false;
        }
        private void RPC_Version(ZRpc rpc, ZPackage pkg)
        {
            if (!m_net.IsServer()) return;
            if (pkg.ReadString() != m_version) { rpc.Invoke("Error", 3); return; }
            Validated.Add(rpc);
            if (m_held.TryGetValue(rpc, out var held)) { m_held.Remove(rpc); m_net.RPC_PeerInfo(rpc, held); }
        }
    }

    private static (ZNetPeer AtServer, ZNetPeer AtClient) ConnectWithMod(ZNet server, ZNet client, ModVersionCheck atServer, ModVersionCheck atClient, bool clientSendsLate)
    {
        var peers = ConnectSteam(server, client, serverPrefix: p => atServer.OnNewConnection(p), clientPrefix: p => atClient.OnNewConnection(p, sendNow: !clientSendsLate));
        atServer.PatchPeerInfo(peers.AtServer);
        return peers;
    }

    [Fact] public void AModRefusesAMismatchedClientWithTheGamesVersionCode()
    {
        using var scope = Network(); var (server, client) = Sides();
        var (atServer, _) = ConnectWithMod(server, client, new ModVersionCheck(server, "1.1.0"), new ModVersionCheck(client, "1.0.0"), clientSendsLate: false);
        Frames(server, client);
        Assert.Equal(ZNet.ConnectionStatus.ErrorVersion, client.Status);
        Assert.False(atServer.IsReady());
        Assert.Contains(atServer.m_rpc.Invoked, c => c.Method == "Error" && (int)c.Args[0] == 3);

        var matching = new ZNet { Server = false, Uid = 3 };
        var serverCheck = new ModVersionCheck(server, "1.1.0");
        ConnectWithMod(server, matching, serverCheck, new ModVersionCheck(matching, "1.1.0"), clientSendsLate: false);
        Frames(server, matching);
        Assert.Equal(ZNet.ConnectionStatus.Connected, matching.Status);
    }

    // Issue #34: a client that sends its version later than its OnNewConnection prefix (here: after its peer info). Sent
    // from the prefix, the version always comes first on the ordered connection and both checks let the client in.
    [Theory]
    [InlineData(false, false, ZNet.ConnectionStatus.Connected)]
    [InlineData(true, false, ZNet.ConnectionStatus.Connected)]
    [InlineData(false, true, ZNet.ConnectionStatus.ErrorVersion)] // negative control: refusing at once drops a correct client
    [InlineData(true, true, ZNet.ConnectionStatus.Connected)]
    public void ALateVersionMessageOnlyBreaksACheckThatRefusesAtOnce(bool defer, bool late, ZNet.ConnectionStatus expected)
    {
        using var scope = Network(); var (server, client) = Sides();
        var serverCheck = new ModVersionCheck(server, "1.1.0") { Defer = defer };
        var (atServer, atClient) = ConnectWithMod(server, client, serverCheck, new ModVersionCheck(client, "1.1.0"), clientSendsLate: late);
        Frames(server, client, 1); // the client has sent its peer info
        Assert.Equal(new[] { late ? "ServerHandshake" : "TestMod_Version", late ? "PeerInfo" : "ServerHandshake" }, Calls(atClient).Take(2));
        if (late) new ModVersionCheck(client, "1.1.0").SendVersion(atClient.m_rpc);
        Frames(server, client);
        Assert.Equal(expected, client.Status);
        Assert.Equal(expected == ZNet.ConnectionStatus.Connected, atServer.IsReady());
    }

    [Fact] public void ADeferringCheckStillRefusesAMismatchedLateVersion()
    {
        using var scope = Network(); var (server, client) = Sides();
        var (atServer, atClient) = ConnectWithMod(server, client, new ModVersionCheck(server, "1.1.0") { Defer = true }, new ModVersionCheck(client, "1.0.0"), clientSendsLate: true);
        Frames(server, client, 1);
        new ModVersionCheck(client, "1.0.0").SendVersion(atClient.m_rpc);
        Frames(server, client);
        Assert.Equal(ZNet.ConnectionStatus.ErrorVersion, client.Status);
        Assert.False(atServer.IsReady());
    }

    // As the game: a package that ends before the handler's parameters do is taken as an incompatible version; a handler
    // that reads past the end of a package argument itself only has its exception logged.
    [Fact] public void APackageThatEndsEarlyIsAnIncompatibleVersion()
    {
        using var scope = Network(); var (server, client) = Sides();
        var log = scope.CaptureLog();
        var peer = new ZNetPeer();
        peer.m_rpc.Register<string, int>("Mod_Hello", (rpc, name, count) => { });
        Assert.Equal(ZRpc.ErrorCode.IncompatibleVersion, peer.m_rpc.Deliver("Mod_Hello", "an older peer sends only a name"));
        Assert.Contains(log, l => l.StartsWith("EndOfStreamException in ZRpc::HandlePackage: Assume incompatible version"));

        peer.m_rpc.Register<ZPackage>("Mod_Config", (rpc, pkg) => { pkg.ReadString(); pkg.ReadInt(); });
        var config = new ZPackage(); config.Write("only a string");
        Assert.Equal(ZRpc.ErrorCode.Success, peer.m_rpc.Deliver("Mod_Config", config));
        Assert.IsType<EndOfStreamException>(Assert.Single(peer.m_rpc.Exceptions));
        Assert.Contains(log, l => l.StartsWith("Exception in ZRpc::HandlePackage: "));

        var (_, atClient) = ConnectSteam(server, client);
        Frames(server, client);
        atClient.m_rpc.Register<string, int>("Mod_Hello", (rpc, name, count) => { });
        server.GetPeer(2)!.m_rpc.Invoke("Mod_Hello", "name only");
        client.UpdatePeers(0f);
        Assert.Equal(ZNet.ConnectionStatus.ErrorVersion, client.Status);
    }

    [Fact] public void ZRpcRegistersDropsAndSendsAsTheGameDoes()
    {
        var a = new ZSteamSocket(2); var b = new ZSteamSocket(1); SocketDouble.Link(a, b); // each end names the Steam user at the other
        var sender = new ZRpc(a); var receiver = new ZRpc(b);
        var got = new List<string>();
        receiver.Register<string>("Mod_Say", (rpc, text) => got.Add("first " + text));
        receiver.Register<string>("Mod_Say", (rpc, text) => got.Add("second " + text)); // replaces, as the game's
        receiver.Register("Mod_Poke", rpc => got.Add("poke from " + rpc.GetSocket().GetHostName()));
        sender.Invoke("Mod_Say", "hi"); sender.Invoke("Mod_Unknown", 1); sender.Invoke("Mod_Poke");
        Assert.Empty(got); // nothing runs until the receiving side updates
        Assert.Equal(ZRpc.ErrorCode.Success, receiver.Update(0f));
        Assert.Equal(new[] { "second hi", "poke from 1" }, got);
        Assert.Equal(new[] { "Mod_Unknown".GetStableHashCode() }, receiver.Dropped);
        Assert.Equal("Mod_Say".GetStableHashCode(), a.Sent[0].ReadInt()); Assert.Equal("hi", a.Sent[0].ReadString());

        receiver.Unregister("Mod_Say"); sender.Invoke("Mod_Say", "again"); receiver.Update(0f);
        Assert.Equal(2, got.Count);
        Assert.Contains("parameter 1 is Byte", Assert.Throws<ArgumentException>(() => receiver.Register<byte>("Mod_Byte", (rpc, v) => { })).Message);

        a.Close();
        Assert.False(receiver.IsConnected()); Assert.Equal(ZRpc.ErrorCode.Disconnected, receiver.Update(0f));
        int sent = a.Sent.Count; sender.Invoke("Mod_Say", "gone");
        Assert.Equal(sent, a.Sent.Count); Assert.Equal(4, sender.Invoked.Count); // a closed connection sends and records nothing
    }

    // Pings, the timeout, the player limit and passwords are not modelled (#307). Their members are absent, so a mod that
    // calls them fails to compile against the doubles instead of getting an answer the game would not give.
    [Theory]
    [InlineData(typeof(ZNet), "GetNrOfPlayers")] [InlineData(typeof(ZNet), "SetServerPassword")]
    [InlineData(typeof(ZRpc), "SetLongTimeout")] [InlineData(typeof(ZRpc), "GetTimeSinceLastPing")]
    [InlineData(typeof(SyncedList), "Load")] [InlineData(typeof(ZPlayFabSocket), "Compressing")]
    public void WhatIsNotModelledIsAbsent(Type type, string member) =>
        Assert.Empty(type.GetMember(member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static));
}
