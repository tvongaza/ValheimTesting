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

public sealed partial class ZNetPeer { }
public sealed partial class ZNet
{
    public static ZNet instance = new();
    public bool Server;
    public readonly Dictionary<long, ZNetPeer> Peers = new();
    public bool IsServer() => Server;
    public ZNetPeer? GetPeer(long id) => Peers.TryGetValue(id, out var p) ? p : null;
    public void Start() { }
    public void Update() { }
}
public sealed partial class Player { public void OnSpawned() { } }
public sealed partial class ZRoutedRpc
{
    public static ZRoutedRpc instance = new();
    public long GetServerPeerID() => 42;
    // Retained to make the original direct-send defect fail this harness.
    public void InvokeRoutedRPC(long peer, string name, params object[] args)
    {
        foreach (var arg in args)
            if (arg is ZPackage p && p.Size() > 512 * 1024)
                throw new InvalidOperationException("Vanilla message exceeds Steam's limit");
    }
    public void Register(string name, Action<long> handler) { }
    public void Register<T>(string name, Action<long, T> handler) { }
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
