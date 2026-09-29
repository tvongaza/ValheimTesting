// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Valheim networking: peers, routed RPCs (with Steam's message-size limit) and packages.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

/// <summary>A connected peer: its id and, once spawned, its character's ZDO.</summary>
public sealed partial class ZNetPeer
{
    public long m_uid;
    public ZDOID m_characterID;
    public string m_playerName = "";
}
public sealed partial class ZNet
{
    public static ZNet instance = new();
    public bool Server;
    public readonly Dictionary<long, ZNetPeer> Peers = new();
    public bool IsServer() => Server;
    public ZNetPeer? GetPeer(long id) => Peers.TryGetValue(id, out var p) ? p : null;
    /// <summary>The connected peers. A peer's key in <see cref="Peers"/> is its id, so <c>m_uid</c> is set from it.</summary>
    public List<ZNetPeer> GetPeers() { foreach (var peer in Peers) peer.Value.m_uid = peer.Key; return new List<ZNetPeer>(Peers.Values); }
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
/// <summary>
/// Routed RPCs: <see cref="Invoked"/> records every call, as the game would send it, and <see cref="Deliver"/> runs a
/// registered handler as if a peer's call arrived. A package over Steam's 512 KiB message limit fails, as it does in
/// the game. Nothing is sent anywhere.
/// </summary>
public sealed partial class ZRoutedRpc
{
    public static ZRoutedRpc instance = new();
    /// <summary>Everyone, as the game's target for a broadcast.</summary>
    public const long Everybody = 0L;
    public long GetServerPeerID() => 42;
    public readonly List<(long Target, string Method, object[] Args)> Invoked = new();
    private readonly Dictionary<string, Delegate> _handlers = new();
    public void InvokeRoutedRPC(long target, string method, params object[] args)
    {
        foreach (var arg in args)
            if (arg is ZPackage p && p.Size() > 512 * 1024)
                throw new InvalidOperationException("Vanilla message exceeds Steam's limit");
        Invoked.Add((target, method, args));
    }
    /// <summary>To the server, as the game's overload without a target sends it.</summary>
    public void InvokeRoutedRPC(string method, params object[] args) => InvokeRoutedRPC(GetServerPeerID(), method, args);
    public void Register(string name, Action<long> handler) => _handlers[name] = handler;
    public void Register<T>(string name, Action<long, T> handler) => _handlers[name] = handler;
    public void Register<T, U>(string name, Action<long, T, U> handler) => _handlers[name] = handler;
    public bool IsRegistered(string name) => _handlers.ContainsKey(name);
    /// <summary>Runs the handler registered for <paramref name="method"/> with <paramref name="sender"/> and the arguments.</summary>
    public void Deliver(long sender, string method, params object[] args)
    {
        if (!_handlers.TryGetValue(method, out var handler)) throw new InvalidOperationException($"No routed RPC named {method} is registered.");
        var all = new object[args.Length + 1]; all[0] = sender; Array.Copy(args, 0, all, 1, args.Length);
        try { handler.DynamicInvoke(all); }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException != null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
}
public sealed partial class ZPackage
{
    private readonly MemoryStream stream = new();
    public void Write(int value) => new BinaryWriter(stream).Write(value);
    public void Write(byte[] value) { Write(value.Length); new BinaryWriter(stream).Write(value); }
    public int ReadInt() => new BinaryReader(stream).ReadInt32();
    public byte[] ReadByteArray() => new BinaryReader(stream).ReadBytes(ReadInt());
    public void SetPos(int pos) => stream.Position = pos;
    public int Size() => (int)stream.Length;
}
