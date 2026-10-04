// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Valheim's direct peer RPCs (ZRpc, ZNetPeer.m_rpc) and the join handshake ZNet runs over them, by the game's 1.0.16
// rules, so a mod's version check or join refusal can be tested with both sides in one process.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Valheim.Testing.Doubles;

/// <summary>
/// A peer's direct RPCs over its socket, as the game's (1.0.16). <see cref="Invoke"/> writes the method's hash and the
/// arguments (the routed RPCs' type table) and sends them; <see cref="Update"/> reads what arrived, in order, and runs
/// each handler with its parameters read from the bytes by their types. As in the game: registering a name again replaces
/// its handler; a call nobody registered is ignored (kept in <see cref="Dropped"/>); a package that ends before its
/// parameters do makes <see cref="Update"/> stop and report <see cref="ErrorCode.IncompatibleVersion"/>; any other handler
/// exception is logged and the next package handled (kept in <see cref="Exceptions"/>). Pings are sent once a second of
/// <see cref="Update"/> time and the connection closes after 30 s without a reply. Unlike the game, an argument it cannot
/// write throws (see <see cref="ZRoutedRpc"/>), and so does registering a handler with a parameter it cannot read.
/// </summary>
public partial class ZRpc : IDisposable
{
    public enum ErrorCode { Success, Disconnected, IncompatibleVersion }
    public partial class RpcMethod { private RpcMethod() { } public delegate void Method(ZRpc RPC); }
    public partial class RpcMethod<T, U, V, B> { private RpcMethod() { } public delegate void Method(ZRpc RPC, T p0, U p1, V p2, B p3); }

    private readonly ISocket m_socket;
    private readonly Dictionary<int, (string Name, Delegate Handler)> m_functions = new();
    private float m_pingTimer;
    private float m_timeSinceLastPing;
    private static readonly float m_pingInterval = 1f;
    private static float m_timeout = 30f;
    /// <summary>Every call sent through this channel, as made.</summary>
    [TestOnly] public readonly List<(string Method, object[] Args)> Invoked = new();
    /// <summary>The method hash of every call that arrived with no handler; the game ignores them silently.</summary>
    [TestOnly] public readonly List<int> Dropped = new();
    /// <summary>Every exception a handler threw while a package was handled; the game only logs them.</summary>
    [TestOnly] public readonly List<Exception> Exceptions = new();

    public ZRpc(ISocket socket) => m_socket = socket;
    public void Dispose() => m_socket.Dispose();
    public ISocket GetSocket() => m_socket;
    public bool IsConnected() => m_socket.IsConnected();
    public float GetTimeSinceLastPing() => m_timeSinceLastPing;

    public void Register(string name, RpcMethod.Method f) => Add(name, f);
    public void Register<T>(string name, Action<ZRpc, T> f) => Add(name, f);
    public void Register<T, U>(string name, Action<ZRpc, T, U> f) => Add(name, f);
    public void Register<T, U, V>(string name, Action<ZRpc, T, U, V> f) => Add(name, f);
    public void Register<T, U, V, W>(string name, RpcMethod<T, U, V, W>.Method f) => Add(name, f);
    public void Unregister(string name) => m_functions.Remove(name.GetStableHashCode());
    /// <summary>Whether a handler is registered under <paramref name="name"/>.</summary>
    [TestOnly] public bool IsRegistered(string name) => m_functions.ContainsKey(name.GetStableHashCode());
    private void Add(string name, Delegate handler)
    {
        var parameters = handler.Method.GetParameters();
        for (int i = 1; i < parameters.Length; i++)
            if (!ZRoutedRpc.IsReadable(parameters[i].ParameterType))
                throw new ArgumentException($"ZRpc '{name}': the handler's parameter {i} is {parameters[i].ParameterType.Name}, which the game cannot read " +
                    "from a package; every call to it would fail.");
        m_functions[name.GetStableHashCode()] = (name, handler);
    }

    /// <summary>Sends the call to the other end; nothing happens once the connection is closed, as in the game.</summary>
    public void Invoke(string method, params object[] parameters)
    {
        if (!IsConnected()) return;
        var pkg = Package(method, parameters);
        Invoked.Add((method, parameters));
        m_socket.Send(pkg);
    }

    /// <summary>
    /// Reads and handles everything that has arrived, as the game does each frame, then pings: <see cref="ErrorCode.Disconnected"/>
    /// once the connection is closed, <see cref="ErrorCode.IncompatibleVersion"/> when a package ended too early.
    /// </summary>
    public ErrorCode Update(float dt)
    {
        if (!m_socket.IsConnected()) return ErrorCode.Disconnected;
        for (var pkg = m_socket.Recv(); pkg != null; pkg = m_socket.Recv())
            if (Handle(pkg) == ErrorCode.IncompatibleVersion) return ErrorCode.IncompatibleVersion;
        UpdatePing(dt);
        return ErrorCode.Success;
    }

    /// <summary>Handles a call as if it had just arrived, as <see cref="Update"/> would, and returns its result.</summary>
    [TestOnly] public ErrorCode Deliver(string method, params object[] args)
    {
        var pkg = Package(method, args); pkg.SetPos(0);
        return Handle(pkg);
    }

    /// <summary>The game's timeout without a ping reply: 30 s, or 90 s with <paramref name="enable"/>. Process-wide, as in the game.</summary>
    public static void SetLongTimeout(bool enable)
    {
        m_timeout = enable ? 90f : 30f;
        ZLog.Log($"ZRpc timeout set to {m_timeout}s ");
    }

    /// <summary>
    /// Writes RPC arguments with the game's type table, as the game's <c>ZRpc.Serialize</c> does for peer and routed calls.
    /// Unlike the game, which skips an argument of any other type without an error, an argument it cannot write throws.
    /// </summary>
    public static void Serialize(object[] parameters, ref ZPackage pkg) => ZRoutedRpc.SerializeArguments("ZRpc.Serialize", parameters, pkg, new List<Type>());

    private static ZPackage Package(string method, object[] args)
    {
        var pkg = new ZPackage(); pkg.Write(method.GetStableHashCode());
        ZRoutedRpc.SerializeArguments(method, args, pkg, new List<Type>());
        return pkg;
    }

    private ErrorCode Handle(ZPackage pkg)
    {
        try { HandlePackage(pkg); }
        catch (EndOfStreamException error)
        {
            ZLog.LogError("EndOfStreamException in ZRpc::HandlePackage: Assume incompatible version: " + error.Message);
            return ErrorCode.IncompatibleVersion;
        }
        catch (Exception error)
        {
            ZLog.Log("Exception in ZRpc::HandlePackage: " + error);
            Exceptions.Add(error is TargetInvocationException { InnerException: { } inner } ? inner : error);
        }
        return ErrorCode.Success;
    }

    // As the game: a handler without parameters is called directly; any other reads its parameters first (a package that
    // ends early fails there) and is then invoked by reflection, which wraps whatever the handler itself throws.
    private void HandlePackage(ZPackage package)
    {
        int hash = package.ReadInt();
        if (hash == 0) { ReceivePing(package); return; }
        if (!m_functions.TryGetValue(hash, out var function)) { Dropped.Add(hash); return; }
        if (function.Handler is RpcMethod.Method direct) { direct(this); return; }
        var parameters = function.Handler.Method.GetParameters();
        var values = new object[parameters.Length];
        values[0] = this;
        for (int i = 1; i < parameters.Length; i++) values[i] = ZRoutedRpc.ReadArgument(package, parameters[i].ParameterType);
        function.Handler.DynamicInvoke(values);
    }

    private void UpdatePing(float dt)
    {
        m_pingTimer += dt;
        if (m_pingTimer > m_pingInterval)
        {
            m_pingTimer = 0f;
            var ping = new ZPackage(); ping.Write(0); ping.Write(true);
            m_socket.Send(ping);
        }
        m_timeSinceLastPing += dt;
        if (m_timeSinceLastPing > m_timeout) { ZLog.LogWarning("ZRpc timeout detected"); m_socket.Close(); }
    }

    private void ReceivePing(ZPackage package)
    {
        if (package.ReadBool()) { var reply = new ZPackage(); reply.Write(0); reply.Write(false); m_socket.Send(reply); }
        else m_timeSinceLastPing = 0f;
    }
}

/// <summary>A peer's connection: its socket and the direct RPCs over it, as the game's.</summary>
public sealed partial class ZNetPeer : IDisposable
{
    public ZRpc m_rpc;
    public ISocket m_socket;
    /// <summary>True on a client for its connection to the server.</summary>
    public bool m_server;
    public UnityEngine.Vector3 m_refPos;
    public string m_playfabId = "";
    public ZNetPeer(ISocket socket, bool server) { m_socket = socket; m_rpc = new ZRpc(socket); m_server = server; }
    /// <summary>A peer on an unlinked Steam socket (Steam id 0): what it sends stays in its socket's <c>Sent</c>.</summary>
    [TestOnly] public ZNetPeer() : this(new ZSteamSocket(0), server: false) { }
    public void Dispose() { m_socket.Dispose(); m_rpc.Dispose(); }
}

/// <summary>
/// The join handshake, as the game runs it (1.0.16). A client's <see cref="OnNewConnection"/> sends ServerHandshake;
/// the server answers ClientHandshake (whether a password is needed, and the salt); the client sends PeerInfo (its id,
/// game and network version, name and, hashed, the password); the server checks the network version (a mismatch is
/// refused with <c>Error</c> 3), the ban and permit lists (8), the player count (9), the password (6) and a second
/// connection with the same id (7), then answers with its own PeerInfo. The client checks the version the same way and
/// is <see cref="ConnectionStatus.Connected"/>; an <c>Error</c> sets its status to that code. Each side handles what has
/// arrived when its <see cref="UpdatePeers"/> runs.
/// </summary>
public sealed partial class ZNet
{
    public enum ConnectionStatus
    {
        None, Connecting, Connected, ErrorVersion, ErrorDisconnected, ErrorConnectFailed, ErrorPassword, ErrorAlreadyConnected,
        ErrorBanned, ErrorFull, ErrorPlatformExcluded, ErrorCrossplayPrivilege, ErrorKicked,
    }

    /// <summary>
    /// This side's connection status. Static in the game; one per ZNet here, like the server flag, so a test can hold a
    /// server and a client. <see cref="GetConnectionStatus"/> reads <see cref="instance"/>'s.
    /// </summary>
    [TestOnly] public ConnectionStatus Status;
    public static ConnectionStatus GetConnectionStatus() => instance.Status;
    /// <summary>The game version this side sends and reports (1.0.16's by default).</summary>
    [TestOnly] public string VersionString = "1.0.16";
    /// <summary>The network version this side sends and requires (1.0.16's is 40).</summary>
    [TestOnly] public uint NetworkVersion = 40;
    /// <summary>This side's id (the game's static <c>GetUID()</c>): the ZDOMan session id unless set. Give each side of a two-sided test its own.</summary>
    [TestOnly] public long? Uid;
    public static long GetUID() => instance.OwnUid;
    private long OwnUid => Uid ?? ZDOMan.GetSessionID();
    /// <summary>The player name this side sends (the game sends its player profile's).</summary>
    [TestOnly] public string PlayerName = "";
    /// <summary>The PlayFab id this side sends.</summary>
    [TestOnly] public string PlayFabId = "";
    /// <summary>The password a client joins with when the server asks for one (the game asks in a dialog; empty leaves it waiting).</summary>
    [TestOnly] public string JoinPassword = "";
    private string m_serverPassword = "";
    private string m_serverPasswordSalt = "";
    private static long s_joiningKey = long.MinValue;

    /// <summary>Makes this server ask for <paramref name="password"/>, stored salted and hashed as the game does.</summary>
    [TestOnly] public void SetServerPassword(string password) => m_serverPassword = string.IsNullOrEmpty(password) ? "" : HashPassword(password, ServerPasswordSalt());

    /// <summary>
    /// A new connection, as the game handles it: the peer joins <see cref="Peers"/> as joining (not ready, <c>m_uid</c> 0)
    /// and gets the handshake handlers; a client also sends ServerHandshake. Private in the game (1.0.16), where mods patch it.
    /// The game's other connection RPCs (saving profiles, simulation distance) are not registered.
    /// </summary>
    public void OnNewConnection(ZNetPeer peer)
    {
        peer.Ready = false;
        Peers.Add(s_joiningKey++, peer);
        peer.m_rpc.Register<ZPackage>("PeerInfo", RPC_PeerInfo);
        peer.m_rpc.Register("Disconnect", RPC_Disconnect);
        if (Server) { peer.m_rpc.Register<string>("ServerHandshake", RPC_ServerHandshake); return; }
        if (Status == ConnectionStatus.None) Status = ConnectionStatus.Connecting;
        peer.m_rpc.Register("Kicked", RPC_Kicked);
        peer.m_rpc.Register<int>("Error", RPC_Error);
        peer.m_rpc.Register<bool, string>("ClientHandshake", RPC_ClientHandshake);
        peer.m_rpc.Invoke("ServerHandshake", ""); // the invite key; invites are not modelled
    }

    /// <summary>The peer on that channel, joining or ready, or null (private in the game).</summary>
    public ZNetPeer? GetPeer(ZRpc rpc)
    {
        foreach (var peer in Peers) if (peer.Value.m_rpc == rpc) return Sync(peer.Key, peer.Value);
        return null;
    }

    /// <summary>Whether <paramref name="uid"/> is this side or a ready peer.</summary>
    public bool IsConnected(long uid)
    {
        if (uid == OwnUid) return true;
        foreach (var peer in Peers) if (peer.Value.Ready && peer.Key == uid) return true;
        return false;
    }

    /// <summary>
    /// The players in the game, approximated: the ready peers, and the host when it has a local player. The game counts its
    /// player list, which it refreshes on joins and leaves and which includes the host unless the server is headless.
    /// </summary>
    public int GetNrOfPlayers()
    {
        int players = Player.m_localPlayer != null ? 1 : 0;
        foreach (var peer in Peers.Values) if (peer.Ready) players++;
        return players;
    }

    /// <summary>Removes the peer and closes its connection.</summary>
    public void Disconnect(ZNetPeer peer)
    {
        foreach (var entry in Peers) if (entry.Value == peer) { Peers.Remove(entry.Key); break; }
        peer.Dispose();
    }

    /// <summary>
    /// What the game does each frame for its peers (private in 1.0.16): drops the first peer whose connection closed (a
    /// client whose server connection closed while connecting is <see cref="ConnectionStatus.ErrorConnectFailed"/>, else
    /// <see cref="ConnectionStatus.ErrorDisconnected"/>), then lets every peer's <see cref="ZRpc.Update"/> handle what
    /// arrived; a package that ended too early sets <see cref="ConnectionStatus.ErrorVersion"/>.
    /// </summary>
    public void UpdatePeers(float dt)
    {
        foreach (var peer in GetPeers())
        {
            if (peer.m_rpc.IsConnected()) continue;
            if (peer.m_server) Status = Status == ConnectionStatus.Connecting ? ConnectionStatus.ErrorConnectFailed : ConnectionStatus.ErrorDisconnected;
            Disconnect(peer);
            break;
        }
        foreach (var peer in GetPeers())
            if (peer.m_rpc.Update(dt) == ZRpc.ErrorCode.IncompatibleVersion) Status = ConnectionStatus.ErrorVersion;
    }

    public void RPC_ServerHandshake(ZRpc rpc, string secretKey)
    {
        var peer = GetPeer(rpc);
        if (peer == null) return;
        ZLog.Log("Got handshake from client " + peer.m_socket.GetEndPointString());
        // No matchmaking provider here, so an invite key never waives the password.
        peer.m_rpc.Invoke("ClientHandshake", !string.IsNullOrEmpty(m_serverPassword), ServerPasswordSalt());
    }

    public void RPC_ClientHandshake(ZRpc rpc, bool needPassword, string serverPasswordSalt)
    {
        m_serverPasswordSalt = serverPasswordSalt;
        if (!needPassword) SendPeerInfo(rpc);
        else if (!string.IsNullOrEmpty(JoinPassword)) SendPeerInfo(rpc, JoinPassword);
    }

    /// <summary>
    /// Sends this side's PeerInfo as the game lays it out. A server's world fields are empty; a client's Steam session
    /// ticket is empty (the server does not verify it) and it sends no invite key.
    /// </summary>
    public void SendPeerInfo(ZRpc rpc, string password = "")
    {
        var pkg = new ZPackage();
        pkg.Write(OwnUid);
        pkg.Write(VersionString);
        if (TryParseVersion(VersionString, out var version) && AtLeast(version, FirstVersionWithNetworkVersion)) pkg.Write(NetworkVersion);
        pkg.Write(new UnityEngine.Vector3(0f, 0f, 0f));
        pkg.Write(PlayerName);
        pkg.Write(PlayFabId);
        pkg.Write(2); pkg.Write(2); pkg.Write(true); // simulation distance: near, far, classic
        if (Server)
        {
            pkg.Write(""); pkg.Write(0); pkg.Write(""); pkg.Write(0L); pkg.Write(0); pkg.Write(0.0); // world name, seed, seed name, uid, generator version, time
        }
        else
        {
            pkg.Write(string.IsNullOrEmpty(password) ? "" : HashPassword(password, ServerPasswordSalt()));
            pkg.Write(""); // invite key
            pkg.Write(Array.Empty<byte>()); // Steam session ticket
        }
        rpc.Invoke("PeerInfo", pkg);
    }

    /// <summary>The game's PeerInfo handler (private in 1.0.16, where mods patch it); see the class summary for its checks.</summary>
    public void RPC_PeerInfo(ZRpc rpc, ZPackage pkg)
    {
        var peer = GetPeer(rpc);
        if (peer == null) return;
        long uid = pkg.ReadLong();
        string versionString = pkg.ReadString();
        uint theirs = 0;
        bool parsed = TryParseVersion(versionString, out var version);
        if (parsed && AtLeast(version, FirstVersionWithNetworkVersion)) theirs = pkg.ReadUInt();
        string name = peer.m_socket.GetEndPointString();
        string hostName = peer.m_socket.GetHostName();
        ZLog.Log("Network version check, their:" + theirs + ", mine:" + NetworkVersion);
        if (theirs != NetworkVersion)
        {
            if (Server) rpc.Invoke("Error", (int)ConnectionStatus.ErrorVersion);
            else Status = ConnectionStatus.ErrorVersion;
            TryParseVersion(VersionString, out var mine);
            ZLog.Log("Peer " + name + " has incompatible version, mine:" + VersionText(mine) + " (network version " + NetworkVersion + ")   remote "
                + VersionText(version) + " (network version " + (theirs == uint.MaxValue ? "unknown" : theirs.ToString()) + ")");
            return;
        }
        var refPos = pkg.ReadVector3();
        string playerName = pkg.ReadString();
        string playfabId = pkg.ReadString();
        pkg.ReadInt(); pkg.ReadInt(); pkg.ReadBool(); // simulation distance (not modelled)
        if (Server)
        {
            if (!IsAllowed(hostName, playerName))
            {
                rpc.Invoke("Error", (int)ConnectionStatus.ErrorBanned);
                ZLog.Log("Player " + playerName + " : " + hostName + " is blacklisted or not in whitelist.");
                return;
            }
            string password = pkg.ReadString();
            pkg.ReadString(); // invite key
            if (OnlineBackend == OnlineBackendType.Steamworks)
            {
                pkg.ReadByteArray(); // the game verifies this session ticket against the Steam peer's id; every ticket passes here
                if (peer.m_socket is not ZSteamSocket)
                    throw new InvalidOperationException($"This server runs on Steam but the peer's socket is {peer.m_socket.GetType().Name}; the game reads a Steam id here. Set OnlineBackend to PlayFab for crossplay peers.");
            }
            if (OnlineBackend == OnlineBackendType.PlayFab)
            {
                if (!new Splatform.PlatformUserID(hostName).IsValid) ZLog.LogError("Failed to parse peer id! Using blank ID with unknown platform.");
                if (peer.m_socket is not ZPlayFabSocket) // the game then checks the PlayFab player's authentication; every player passes here
                    throw new InvalidOperationException($"This server runs on PlayFab (crossplay) but the peer's socket is {peer.m_socket.GetType().Name}; the game reads a PlayFab id here.");
            }
            if (GetNrOfPlayers() >= 10)
            {
                rpc.Invoke("Error", (int)ConnectionStatus.ErrorFull);
                ZLog.Log("Peer " + name + " disconnected due to server is full");
                return;
            }
            if (m_serverPassword != password)
            {
                rpc.Invoke("Error", (int)ConnectionStatus.ErrorPassword);
                ZLog.Log("Peer " + name + " has wrong password");
                return;
            }
            if (IsConnected(uid))
            {
                rpc.Invoke("Error", (int)ConnectionStatus.ErrorAlreadyConnected);
                ZLog.Log("Already connected to peer with UID:" + uid + "  " + name);
                return;
            }
        }
        else
        {
            pkg.ReadString(); pkg.ReadInt(); pkg.ReadString(); pkg.ReadLong(); pkg.ReadInt(); pkg.ReadDouble(); // world (not modelled)
        }
        peer.m_refPos = refPos;
        peer.m_playerName = playerName;
        peer.m_playfabId = playfabId;
        foreach (var entry in Peers) if (entry.Value == peer) { Peers.Remove(entry.Key); break; }
        peer.Ready = true; peer.m_uid = uid; Peers[uid] = peer;
        if (Server) { SendPeerInfo(rpc); peer.m_socket.VersionMatch(); }
        else { peer.m_socket.VersionMatch(); Status = ConnectionStatus.Connected; }
    }

    public void RPC_Error(ZRpc rpc, int error)
    {
        Status = (ConnectionStatus)error;
        ZLog.Log("Got connectoin error msg " + Status); // sic, as the game logs it
    }

    public void RPC_Disconnect(ZRpc rpc)
    {
        ZLog.Log("RPC_Disconnect");
        var peer = GetPeer(rpc);
        if (peer == null) return;
        if (peer.m_server) Status = ConnectionStatus.ErrorDisconnected;
        Disconnect(peer);
    }

    public void RPC_Kicked(ZRpc rpc)
    {
        var peer = GetPeer(rpc);
        if (peer == null || !peer.m_server) return;
        Status = ConnectionStatus.ErrorKicked;
        Disconnect(peer);
    }

    private string ServerPasswordSalt()
    {
        if (m_serverPasswordSalt.Length == 0)
        {
            var bytes = new byte[16];
            using (var random = System.Security.Cryptography.RandomNumberGenerator.Create()) random.GetBytes(bytes);
            m_serverPasswordSalt = Encoding.ASCII.GetString(bytes);
        }
        return m_serverPasswordSalt;
    }

    private static string HashPassword(string password, string salt)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return Encoding.ASCII.GetString(md5.ComputeHash(Encoding.ASCII.GetBytes(password + salt)));
    }

    // The game's version text: major.minor[.patch], a patch "rcN" being a release candidate (stored negative).
    private static readonly (int Major, int Minor, int Patch) FirstVersionWithNetworkVersion = (0, 214, 301);
    private static bool TryParseVersion(string text, out (int Major, int Minor, int Patch) version)
    {
        version = default;
        var parts = text.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor)) return false;
        int patch = 0;
        if (parts.Length > 2)
        {
            if (parts[2].StartsWith("rc")) { if (!int.TryParse(parts[2].Substring(2), out patch)) return false; patch = -patch; }
            else if (!int.TryParse(parts[2], out patch)) return false;
        }
        version = (major, minor, patch);
        return true;
    }
    private static bool AtLeast((int Major, int Minor, int Patch) other, (int Major, int Minor, int Patch) reference)
    {
        if (other == reference || other.Major > reference.Major) return true;
        if (other.Major != reference.Major) return false;
        if (other.Minor != reference.Minor) return other.Minor > reference.Minor;
        if (reference.Patch >= 0) return other.Patch > reference.Patch;
        return other.Patch >= 0 || other.Patch < reference.Patch;
    }
    private static string VersionText((int Major, int Minor, int Patch) v) =>
        v.Major == 0 && v.Minor == 0 && v.Patch == 0 ? "" : v.Patch == 0 ? $"{v.Major}.{v.Minor}" : v.Patch < 0 ? $"{v.Major}.{v.Minor}.rc{-v.Patch}" : $"{v.Major}.{v.Minor}.{v.Patch}";
}
