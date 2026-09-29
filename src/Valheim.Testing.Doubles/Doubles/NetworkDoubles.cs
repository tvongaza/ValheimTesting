// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Valheim networking: peers, routed and per-object RPCs (delivered and routed by the game's 1.0.16 rules, with Steam's
// message-size limit) and packages with the game's byte encoding.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

/// <summary>A connected peer: its id and, once spawned, its character's ZDO.</summary>
public sealed partial class ZNetPeer
{
    public long m_uid;
    public ZDOID m_characterID;
    public string m_playerName = "";
    /// <summary>
    /// False while the peer is still joining. Like the game's, such a peer's <c>m_uid</c> is 0 and it receives no routed
    /// RPC. A test switch, not a game field.
    /// </summary>
    public bool Ready = true;
    /// <summary>As the game's: a peer is ready once it has its id.</summary>
    public bool IsReady() => Ready && m_uid != 0;
}
public sealed partial class ZNet
{
    public static ZNet instance = new();
    public bool Server;
    public readonly Dictionary<long, ZNetPeer> Peers = new();
    public bool IsServer() => Server;
    /// <summary>The ready peer with that id, or null, as the game finds a peer by its id.</summary>
    public ZNetPeer? GetPeer(long id) => Peers.TryGetValue(id, out var p) && Sync(id, p).Ready ? p : null;
    /// <summary>
    /// The connected peers, joining ones included. A peer's key in <see cref="Peers"/> is its id, so <c>m_uid</c> is set
    /// from it; a peer that is not <see cref="ZNetPeer.Ready"/> has <c>m_uid</c> 0, as in the game.
    /// </summary>
    public List<ZNetPeer> GetPeers() { foreach (var peer in Peers) Sync(peer.Key, peer.Value); return new List<ZNetPeer>(Peers.Values); }
    private static ZNetPeer Sync(long id, ZNetPeer peer) { peer.m_uid = peer.Ready ? id : 0; return peer; }
    public void Start() { }
    public void Update() { }
}
/// <summary>The local player: set on a client or a host that has spawned, null on a dedicated server.</summary>
public sealed partial class Player
{
    public static Player? m_localPlayer;
    public Transform transform = new();
    public void OnSpawned() { }
}
/// <summary>A value a mod writes into an RPC's package itself, as the game's interface.</summary>
public partial interface ISerializableParameter
{
    void Serialize(ref ZPackage pkg);
    void Deserialize(ref ZPackage pkg);
}
/// <summary>The handler types for four to six arguments, as the game declares them.</summary>
public partial class RoutedMethod<T, U, V, B> { public delegate void Method(long sender, T p0, U p1, V p2, B p3); }
public partial class RoutedMethod<T, U, V, B, K> { public delegate void Method(long sender, T p0, U p1, V p2, B p3, K p4); }
public partial class RoutedMethod<T, U, V, B, K, M> { public delegate void Method(long sender, T p0, U p1, V p2, B p3, K p4, M p5); }

/// <summary>
/// Routed RPCs, by the game's 1.0.16 rules. Arguments are written into a <see cref="ZPackage"/> with the game's type
/// table and the handler reads them back by its parameter types. A call to <see cref="Everybody"/> or to this peer runs
/// the local handler at once, before anything is sent; a call to a name nobody registered is dropped without an error.
/// <see cref="Sent"/> holds what each ready peer would receive, <see cref="Dropped"/> every call that found no handler,
/// and <see cref="Invoked"/> every call as made. <see cref="Deliver"/> and <see cref="Receive"/> handle a call as if it
/// arrived from a peer. A package over Steam's 512 KiB message limit fails. Nothing is sent anywhere.
/// </summary>
public sealed partial class ZRoutedRpc
{
    /// <summary>A routed call as the game puts it on the wire.</summary>
    public sealed partial class RoutedRPCData
    {
        public long m_msgID;
        public long m_senderPeerID;
        public long m_targetPeerID;
        public ZDOID m_targetZDO;
        public int m_methodHash;
        public ZPackage m_parameters = new();
        /// <summary>The method's name, for messages; the game sends only its hash.</summary>
        public string Method = "";
        internal List<Type>? ArgumentTypes;
        public void Serialize(ZPackage pkg)
        {
            pkg.Write(m_msgID); pkg.Write(m_senderPeerID); pkg.Write(m_targetPeerID); pkg.Write(m_targetZDO);
            pkg.Write(m_methodHash); pkg.Write(m_parameters);
        }
        public void Deserialize(ZPackage pkg)
        {
            m_msgID = pkg.ReadLong(); m_senderPeerID = pkg.ReadLong(); m_targetPeerID = pkg.ReadLong(); m_targetZDO = pkg.ReadZDOID();
            m_methodHash = pkg.ReadInt(); m_parameters = pkg.ReadPackage();
        }
    }

    public static ZRoutedRpc instance = new();
    /// <summary>Everyone, as the game's target for a broadcast.</summary>
    public const long Everybody = 0L;
    private const int SteamMessageLimit = 512 * 1024;
    private long? m_id;
    private int m_rpcMsgID = 1;
    private readonly Dictionary<int, (string Name, Delegate Handler)> m_functions = new();
    /// <summary>Every call this peer made, as made: target, method and the caller's arguments.</summary>
    public readonly List<(long Target, string Method, object[] Args)> Invoked = new();
    /// <summary>What each ready peer receives, in order, decoded as that peer reads it.</summary>
    public readonly List<(long Peer, RoutedRPCData Data)> Sent = new();
    /// <summary>
    /// Every call handled here that reached no handler: an unregistered name, an object that is not there, or a name the
    /// object did not register. The game drops the first two silently and logs the third.
    /// </summary>
    public readonly List<(long Sender, ZDOID TargetZDO, string Method, string Reason)> Dropped = new();

    /// <summary>Sets this peer's id, as the game does from the session id.</summary>
    public void SetUID(long uid) => m_id = uid;
    /// <summary>This peer's id: the one <see cref="SetUID"/> gave, else this session's (<c>ZDOMan.m_sessionID</c>), else 1.</summary>
    public long PeerId => m_id ?? ZDOMan.instance?.m_sessionID ?? 1;
    private static bool IsServer => ZNet.instance.IsServer();

    /// <summary>
    /// The target of a call without one, as the game picks it: the server is this peer, a client sends to its first
    /// ready peer, or to <see cref="Everybody"/> when it has none. Private in the game (1.0.16); public here for mods
    /// built against publicized assemblies.
    /// </summary>
    public long GetServerPeerID()
    {
        if (IsServer) return PeerId;
        foreach (var peer in ZNet.instance.Peers) if (peer.Value.Ready) return peer.Key;
        return Everybody;
    }

    public void InvokeRoutedRPC(long target, string method, params object[] args) => InvokeRoutedRPC(target, ZDOID.None, method, args);
    /// <summary>To the server, as the game's overload without a target sends it (see <see cref="GetServerPeerID"/>).</summary>
    public void InvokeRoutedRPC(string method, params object[] args) => InvokeRoutedRPC(GetServerPeerID(), method, args);
    /// <summary>A call to one object's handler (<c>ZNetView.InvokeRPC</c> uses this).</summary>
    public void InvokeRoutedRPC(long target, ZDOID targetZDO, string method, params object[] args)
    {
        var parameters = new ZPackage(); var types = new List<Type>();
        SerializeArguments(method, args, parameters, types);
        if (parameters.Size() > SteamMessageLimit) throw new InvalidOperationException("Vanilla message exceeds Steam's limit");
        Invoked.Add((target, method, args));
        long id = PeerId;
        var data = new RoutedRPCData
        {
            m_msgID = id + m_rpcMsgID++, m_senderPeerID = id, m_targetPeerID = target, m_targetZDO = targetZDO,
            m_methodHash = method.GetStableHashCode(), m_parameters = parameters, Method = method, ArgumentTypes = types,
        };
        parameters.SetPos(0);
        if (target == id || target == Everybody) Handle(data);
        if (target != id) Route(data);
    }

    /// <summary>Handles <paramref name="method"/> as if <paramref name="sender"/>'s call to this peer arrived.</summary>
    public void Deliver(long sender, string method, params object[] args) => Receive(sender, PeerId, ZDOID.None, method, args);
    /// <summary>
    /// Handles a call from <paramref name="sender"/> as the game handles one arriving from the network: it runs here
    /// when aimed at this peer or <see cref="Everybody"/>, and the server passes on what is aimed at anyone else.
    /// </summary>
    public void Receive(long sender, long target, ZDOID targetZDO, string method, params object[] args)
    {
        var parameters = new ZPackage(); var types = new List<Type>();
        SerializeArguments(method, args, parameters, types);
        parameters.SetPos(0);
        var data = new RoutedRPCData
        {
            m_senderPeerID = sender, m_targetPeerID = target, m_targetZDO = targetZDO, m_methodHash = method.GetStableHashCode(),
            m_parameters = parameters, Method = method, ArgumentTypes = types,
        };
        long id = PeerId;
        if (target == id || target == Everybody) Handle(data);
        if (IsServer && target != id) Route(data);
    }

    private void Handle(RoutedRPCData data)
    {
        if (data.m_targetZDO.IsNone())
        {
            if (m_functions.TryGetValue(data.m_methodHash, out var function))
                Call(function.Handler, data, $"Routed RPC '{data.Method}'");
            else Dropped.Add((data.m_senderPeerID, data.m_targetZDO, data.Method, "no handler registered"));
            return;
        }
        var zdos = ZDOMan.instance ?? throw new InvalidOperationException(
            $"RPC '{data.Method}' is aimed at object {data.m_targetZDO}, which the game finds through ZDOMan.instance; install one (ValheimWorldScope.WithZdos).");
        var zdo = zdos.GetZDO(data.m_targetZDO);
        var view = zdo == null ? null : ZNetView.Find(zdo);
        if (view == null) { Dropped.Add((data.m_senderPeerID, data.m_targetZDO, data.Method, zdo == null ? "no such ZDO" : "no live object")); return; }
        if (!view.m_functions.ContainsKey(data.m_methodHash)) Dropped.Add((data.m_senderPeerID, data.m_targetZDO, data.Method, "not registered on the object"));
        view.HandleRoutedRPC(data);
    }

    private void Route(RoutedRPCData data)
    {
        if (IsServer && data.m_targetPeerID != Everybody)
        {
            if (ZNet.instance.Peers.TryGetValue(data.m_targetPeerID, out var target) && target.Ready) Send(data.m_targetPeerID, data);
            return;
        }
        foreach (var peer in new List<KeyValuePair<long, ZNetPeer>>(ZNet.instance.Peers))
            if (peer.Value.Ready && (!IsServer || peer.Key != data.m_senderPeerID)) Send(peer.Key, data);
    }

    private void Send(long peer, RoutedRPCData data)
    {
        var wire = new ZPackage(); data.Serialize(wire); wire.SetPos(0);
        var received = new RoutedRPCData { Method = data.Method, ArgumentTypes = data.ArgumentTypes };
        received.Deserialize(wire);
        Sent.Add((peer, received));
    }

    public void Register(string name, Action<long> handler) => Add(name, handler);
    public void Register<T>(string name, Action<long, T> handler) => Add(name, handler);
    public void Register<T, U>(string name, Action<long, T, U> handler) => Add(name, handler);
    public void Register<T, U, V>(string name, Action<long, T, U, V> handler) => Add(name, handler);
    public void Register<T, U, V, B>(string name, RoutedMethod<T, U, V, B>.Method handler) => Add(name, handler);
    public void Register<T, U, V, B, K>(string name, RoutedMethod<T, U, V, B, K>.Method handler) => Add(name, handler);
    public void Register<T, U, V, B, K, M>(string name, RoutedMethod<T, U, V, B, K, M>.Method handler) => Add(name, handler);
    /// <summary>Whether a handler is registered under the name. Not a game method: for tests only.</summary>
    public bool IsRegistered(string name) => m_functions.ContainsKey(name.GetStableHashCode());
    private void Add(string name, Delegate handler) => AddHandler(m_functions, name, handler, "ZRoutedRpc");

    internal static void AddHandler(Dictionary<int, (string Name, Delegate Handler)> functions, string name, Delegate handler, string owner)
    {
        int hash = name.GetStableHashCode();
        // The game adds to a dictionary keyed by the name's hash, so a second registration throws there too.
        if (functions.TryGetValue(hash, out var existing))
            throw new ArgumentException(existing.Name == name
                ? $"{owner}: an RPC named '{name}' is already registered; the game throws on a second registration."
                : $"{owner}: '{name}' has the same hash as the registered '{existing.Name}'; the game throws on it.");
        functions.Add(hash, (name, handler));
    }

    // ---- the game's argument table (ZRpc.Serialize and ZRpc.Deserialize in 1.0.16) ----

    private const string Supported = "int, uint, long, float, double, bool, string, ZPackage, List<string>, Vector3, Quaternion, ZDOID, HitData and ISerializableParameter";

    /// <summary>
    /// Writes the arguments as the game does. The game skips an argument of any other type without an error, so its
    /// handler reads the wrong bytes; here that throws, naming the argument. (HitData is not doubled.)
    /// </summary>
    internal static void SerializeArguments(string method, object[] args, ZPackage pkg, List<Type> types)
    {
        for (int i = 0; i < args.Length; i++)
        {
            object? arg = args[i];
            Type type;
            switch (arg)
            {
                case int v: pkg.Write(v); type = typeof(int); break;
                case uint v: pkg.Write(v); type = typeof(uint); break;
                case long v: pkg.Write(v); type = typeof(long); break;
                case float v: pkg.Write(v); type = typeof(float); break;
                case double v: pkg.Write(v); type = typeof(double); break;
                case bool v: pkg.Write(v); type = typeof(bool); break;
                case string v: pkg.Write(v); type = typeof(string); break;
                case ZPackage v: pkg.Write(v); type = typeof(ZPackage); break;
                case List<string> v: pkg.Write(v.Count); foreach (var item in v) pkg.Write(item); type = typeof(List<string>); break;
                case UnityEngine.Vector3 v: pkg.Write(v); type = typeof(UnityEngine.Vector3); break;
                case UnityEngine.Quaternion v: pkg.Write(v); type = typeof(UnityEngine.Quaternion); break;
                case ZDOID v: pkg.Write(v); type = typeof(ZDOID); break;
                case ISerializableParameter v: var into = pkg; v.Serialize(ref into); type = v.GetType(); break;
                default:
                    throw new ArgumentException($"RPC '{method}': argument {i + 1} is {(arg == null ? "null" : Name(arg.GetType()))}. " +
                        $"The game writes only {Supported}, and skips anything else without an error, so the handler reads the wrong bytes. " +
                        "Convert it first (an enum to int, a byte[] into a ZPackage).");
            }
            types.Add(type);
        }
    }

    internal static bool IsReadable(Type type) =>
        type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(float) || type == typeof(double) ||
        type == typeof(bool) || type == typeof(string) || type == typeof(ZPackage) || type == typeof(List<string>) ||
        type == typeof(UnityEngine.Vector3) || type == typeof(UnityEngine.Quaternion) || type == typeof(ZDOID) ||
        typeof(ISerializableParameter).IsAssignableFrom(type);

    internal static object ReadArgument(ZPackage pkg, Type type)
    {
        if (type == typeof(int)) return pkg.ReadInt();
        if (type == typeof(uint)) return pkg.ReadUInt();
        if (type == typeof(long)) return pkg.ReadLong();
        if (type == typeof(float)) return pkg.ReadSingle();
        if (type == typeof(double)) return pkg.ReadDouble();
        if (type == typeof(bool)) return pkg.ReadBool();
        if (type == typeof(string)) return pkg.ReadString();
        if (type == typeof(ZPackage)) return pkg.ReadPackage();
        if (type == typeof(List<string>))
        {
            int count = pkg.ReadInt(); var list = new List<string>(count);
            for (int i = 0; i < count; i++) list.Add(pkg.ReadString());
            return list;
        }
        if (type == typeof(UnityEngine.Vector3)) return pkg.ReadVector3();
        if (type == typeof(UnityEngine.Quaternion)) return pkg.ReadQuaternion();
        if (type == typeof(ZDOID)) return pkg.ReadZDOID();
        var value = (ISerializableParameter)Activator.CreateInstance(type)!;
        value.Deserialize(ref pkg);
        return value;
    }

    /// <summary>
    /// Runs a handler as the game does: its parameters after the sender are read from the package by their own types.
    /// Where the game would misread (a parameter of another type than the argument sent, or more parameters than
    /// arguments) or pass nothing (a type it cannot read), this throws and names the parameter. The type check is exact,
    /// so it also refuses reinterpretations the game gets away with (an int read as a uint, a subclass sent to a
    /// base-class parameter): refused, not misread. Arguments beyond the
    /// handler's parameters are ignored, as in the game. A handler's exception is rethrown as it was thrown.
    /// </summary>
    internal static void Call(Delegate handler, RoutedRPCData data, string context)
    {
        var parameters = handler.Method.GetParameters();
        var values = new object[parameters.Length];
        values[0] = data.m_senderPeerID;
        var sent = data.ArgumentTypes;
        for (int i = 1; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;
            if (!IsReadable(type))
                throw new InvalidOperationException($"{context}: the handler's parameter {i} is {Name(type)}, which the game cannot read from a package " +
                    $"(it reads {Supported}); it passes no value and the call fails.");
            if (sent != null && i > sent.Count)
                throw new InvalidOperationException($"{context}: the call sent {sent.Count} argument(s) but the handler reads {parameters.Length - 1}; " +
                    "the game would read past the end of the package.");
            if (sent != null && sent[i - 1] != type)
                throw new InvalidOperationException($"{context}: argument {i} was sent as {Name(sent[i - 1])} but the handler reads {Name(type)}; " +
                    "the game reads the handler's type from the bytes, so it would misread this and every later argument.");
            values[i] = ReadArgument(data.m_parameters, type);
        }
        try { handler.DynamicInvoke(values); }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException != null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }

    private static string Name(Type type) =>
        type == typeof(List<string>) ? "List<string>" : type.IsEnum ? $"{type.Name} (an enum)" : type.Name;
}

/// <summary>Per-object RPCs, as the game's: each view registers its own handlers, and a call goes through the routed RPCs.</summary>
public partial class ZNetView
{
    /// <summary>Everyone, as the game's target for a broadcast to an object.</summary>
    public static long Everybody = 0L;
    internal readonly Dictionary<int, (string Name, Delegate Handler)> m_functions = new();

    public void Register(string name, Action<long> f) => Add(name, f);
    public void Register<T>(string name, Action<long, T> f) => Add(name, f);
    public void Register<T, U>(string name, Action<long, T, U> f) => Add(name, f);
    public void Register<T, U, V>(string name, Action<long, T, U, V> f) => Add(name, f);
    public void Register<T, U, V, B>(string name, RoutedMethod<T, U, V, B>.Method f) => Add(name, f);
    public void Register<T, U, V, B, K>(string name, RoutedMethod<T, U, V, B, K>.Method f) => Add(name, f);
    public void Register<T, U, V, B, K, M>(string name, RoutedMethod<T, U, V, B, K, M>.Method f) => Add(name, f);
    public void Unregister(string name) => m_functions.Remove(name.GetStableHashCode());
    /// <summary>Whether this object registered a handler under the name. Not a game method: for tests only.</summary>
    public bool IsRegistered(string name) => m_functions.ContainsKey(name.GetStableHashCode());
    private void Add(string name, Delegate handler) => ZRoutedRpc.AddHandler(m_functions, name, handler, "ZNetView");

    /// <summary>Runs this object's handler for the call, or logs "Failed to find rpc method" with its hash, as the game does.</summary>
    public void HandleRoutedRPC(ZRoutedRpc.RoutedRPCData rpcData)
    {
        if (m_functions.TryGetValue(rpcData.m_methodHash, out var function))
            ZRoutedRpc.Call(function.Handler, rpcData, $"RPC '{rpcData.Method}' on object {Zdo.m_uid}");
        else ZLog.LogWarning("Failed to find rpc method " + rpcData.m_methodHash);
    }

    /// <summary>A call to this object's handler on <paramref name="targetID"/>, or on every peer (<see cref="Everybody"/>).</summary>
    public void InvokeRPC(long targetID, string method, params object[] parameters) => ZRoutedRpc.instance.InvokeRoutedRPC(targetID, Zdo.m_uid, method, parameters);
    /// <summary>A call to this object's handler on its owner; an object without an owner (owner 0) broadcasts, as in the game.</summary>
    public void InvokeRPC(string method, params object[] parameters) => ZRoutedRpc.instance.InvokeRoutedRPC(Zdo.GetOwner(), Zdo.m_uid, method, parameters);

    /// <summary>
    /// The view a call to <paramref name="zdo"/> reaches. With a scene, the object must be live in it, as in the game (a
    /// ghost or destroyed object receives nothing); a view built around a bare ZDO, with no game object, is found through
    /// its ZDO.
    /// </summary>
    internal static ZNetView? Find(ZDO zdo)
    {
        var live = ZNetScene.instance?.FindInstance(zdo);
        if (live != null) return live;
        var bare = zdo.m_view;
        return bare != null && bare.gameObject == null && bare.Zdo == zdo ? bare : null;
    }
}
public partial class ZDO
{
    /// <summary>The view built around this ZDO last.</summary>
    internal ZNetView? m_view;
}

/// <summary>The game's log. Lines go to the log capture (<c>ManualLogSource.Captured</c>) and the console.</summary>
public partial class ZLog
{
    public static void Log(object o) => Write("INFO ", o);
    public static void LogWarning(object o) => Write("WARN ", o);
    public static void LogError(object o) => Write("ERROR", o);
    private static void Write(string level, object o)
    {
        string line = o?.ToString() ?? "";
        BepInEx.Logging.ManualLogSource.Captured?.Add(line);
        Console.WriteLine($"[{level}] {line}");
    }
}

/// <summary>
/// The game's package: one stream written and read at the same position, with the game's byte encoding for every
/// Write/Read pair (little-endian numbers, strings with a 7-bit length prefix and UTF-8, a byte array or package with an
/// int length). Write, then <see cref="SetPos"/>(0) to read back. Compressed packages round-trip, but their bytes come
/// from the runtime's gzip at its default level, not the game's gzip at its Fastest level, so they differ.
/// </summary>
public sealed partial class ZPackage
{
    private readonly MemoryStream m_stream = new();
    private readonly BinaryWriter m_writer;
    private readonly BinaryReader m_reader;

    public ZPackage() { m_writer = new BinaryWriter(m_stream); m_reader = new BinaryReader(m_stream); }
    public ZPackage(string base64String) : this() { if (!string.IsNullOrEmpty(base64String)) Load(Convert.FromBase64String(base64String)); }
    public ZPackage(byte[] data) : this() => Load(data);
    public ZPackage(byte[] data, int dataSize) : this() { m_stream.Write(data, 0, dataSize); m_stream.Position = 0; }
    public void Load(byte[] data) { Clear(); m_stream.Write(data, 0, data.Length); m_stream.Position = 0; }

    public void Write(ZPackage pkg) { var array = pkg.GetArray(); m_writer.Write(array.Length); m_writer.Write(array); }
    public void WriteCompressed(ZPackage pkg) { var array = Compress(pkg.GetArray()); m_writer.Write(array.Length); m_writer.Write(array); }
    public byte[] GetCompressed() => Compress(GetArray());
    /// <summary>As the game's: the decompressed bytes overwrite the start of the package, which is then read from 0.</summary>
    public void Decompress() { var array = Decompress(GetArray()); m_stream.Position = 0; m_stream.Write(array, 0, array.Length); m_stream.Position = 0; }
    public void Write(byte[] array) { m_writer.Write(array.Length); m_writer.Write(array); }
    public void Write(byte data) => m_writer.Write(data);
    public void Write(sbyte data) => m_writer.Write(data);
    public void Write(char data) => m_writer.Write(data);
    public void Write(bool data) => m_writer.Write(data);
    public void Write(int data) => m_writer.Write(data);
    public void Write(uint data) => m_writer.Write(data);
    public void Write(short data) => m_writer.Write(data);
    public void Write(ushort data) => m_writer.Write(data);
    public void Write(long data) => m_writer.Write(data);
    public void Write(ulong data) => m_writer.Write(data);
    public void Write(float data) => m_writer.Write(data);
    public void Write(double data) => m_writer.Write(data);
    public void Write(string data) => m_writer.Write(data);
    /// <summary>The creator's session id (long), then the object's number (uint), as the game writes a ZDOID.</summary>
    public void Write(ZDOID id)
    {
        if (id.ID < 0 || id.ID > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(id), id.ID, "The game's ZDOID number is a uint.");
        m_writer.Write(id.UserID); m_writer.Write((uint)id.ID);
    }
    public void Write(UnityEngine.Vector3 v3) { m_writer.Write(v3.x); m_writer.Write(v3.y); m_writer.Write(v3.z); }
    /// <summary>Euler angles in half-degree steps: two bytes for a yaw-only rotation, else four, as the game packs them.</summary>
    public void WriteSmallRotation(UnityEngine.Vector3 v3)
    {
        v3 *= 2f;
        uint x = (uint)v3.x, y = (uint)v3.y, z = (uint)v3.z;
        if ((x <= 1 || x >= 719) && (z <= 1 || z >= 719)) { m_writer.Write(unchecked((short)(y | 0x8000))); return; }
        uint packed = x | y << 10 | z << 20;
        m_writer.Write((ushort)(packed >> 16)); m_writer.Write(unchecked((ushort)packed));
    }
    public void Write(Vector2i v2) { m_writer.Write(v2.x); m_writer.Write(v2.y); }
    public void Write(Vector2s v2) { m_writer.Write(v2.x); m_writer.Write(v2.y); }
    public void Write(UnityEngine.Quaternion q) { m_writer.Write(q.x); m_writer.Write(q.y); m_writer.Write(q.z); m_writer.Write(q.w); }
    /// <summary>A count in one byte below 128, else two (high bit set on the first), as the game writes it.</summary>
    public void WriteNumItems(int numItems)
    {
        if (numItems < 128) { m_writer.Write((byte)numItems); return; }
        m_writer.Write((byte)((numItems >> 8) | 0x80)); m_writer.Write(unchecked((byte)numItems));
    }

    public ZDOID ReadZDOID() => new(m_reader.ReadInt64(), m_reader.ReadUInt32());
    public bool ReadBool() => m_reader.ReadBoolean();
    public char ReadChar() => m_reader.ReadChar();
    public byte ReadByte() => m_reader.ReadByte();
    public int ReadNumItems() { int n = m_reader.ReadByte(); return (n & 0x80) != 0 ? (n & 0x7F) << 8 | m_reader.ReadByte() : n; }
    public sbyte ReadSByte() => m_reader.ReadSByte();
    public short ReadShort() => m_reader.ReadInt16();
    public ushort ReadUShort() => m_reader.ReadUInt16();
    public int ReadInt() => m_reader.ReadInt32();
    public uint ReadUInt() => m_reader.ReadUInt32();
    public long ReadLong() => m_reader.ReadInt64();
    public ulong ReadULong() => m_reader.ReadUInt64();
    public float ReadSingle() => m_reader.ReadSingle();
    public double ReadDouble() => m_reader.ReadDouble();
    public string ReadString() => m_reader.ReadString();
    public UnityEngine.Vector3 ReadVector3() => new(m_reader.ReadSingle(), m_reader.ReadSingle(), m_reader.ReadSingle());
    public UnityEngine.Vector3 ReadSmallRotation()
    {
        uint packed = m_reader.ReadUInt16();
        if ((packed & 0x8000) != 0) return new UnityEngine.Vector3(0f, (packed & 0x7FFF) * 0.5f, 0f);
        packed = packed << 16 | m_reader.ReadUInt16();
        return new UnityEngine.Vector3(packed & 0x3FF, packed >> 10 & 0x3FF, packed >> 20 & 0x3FF) * 0.5f;
    }
    public Vector2i ReadVector2i() => new(m_reader.ReadInt32(), m_reader.ReadInt32());
    public Vector2s ReadVector2s() => new(m_reader.ReadInt16(), m_reader.ReadInt16());
    public UnityEngine.Quaternion ReadQuaternion() => new(m_reader.ReadSingle(), m_reader.ReadSingle(), m_reader.ReadSingle(), m_reader.ReadSingle());
    public ZPackage ReadCompressedPackage() => new(Decompress(m_reader.ReadBytes(m_reader.ReadInt32())));
    public ZPackage ReadPackage() => new(m_reader.ReadBytes(m_reader.ReadInt32()));
    public void ReadPackage(ref ZPackage pkg) => pkg.Load(m_reader.ReadBytes(m_reader.ReadInt32()));
    public byte[] ReadByteArray() => m_reader.ReadBytes(m_reader.ReadInt32());
    public byte[] ReadByteArray(int num) => m_reader.ReadBytes(num);

    public string GetBase64() => Convert.ToBase64String(GetArray());
    /// <summary>Everything written, whatever the read position.</summary>
    public byte[] GetArray() { m_writer.Flush(); return m_stream.ToArray(); }
    public void SetPos(int pos) => m_stream.Position = pos;
    public int GetPos() => (int)m_stream.Position;
    public int Size() { m_writer.Flush(); return (int)m_stream.Length; }
    public void Flush() => m_writer.Flush();
    public void Clear() { m_writer.Flush(); m_stream.SetLength(0); m_stream.Position = 0; }
    public byte[] GenerateHash() { using var sha = System.Security.Cryptography.SHA512.Create(); return sha.ComputeHash(GetArray()); }

    private static byte[] Compress(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress)) gzip.Write(input, 0, input.Length);
        return output.ToArray();
    }
    private static byte[] Decompress(byte[] input)
    {
        using var gzip = new GZipStream(new MemoryStream(input), CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
