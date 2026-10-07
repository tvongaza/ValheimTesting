// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Jotunn's custom RPCs, recording what was sent.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Valheim.Testing.Doubles;

namespace Jotunn.Entities
{
    /// <summary>A registered Jotunn RPC: records what it sends; a test delivers a package by running Server or Client.</summary>
    public sealed partial class CustomRPC
    {
        [TestOnly] public CustomRPC(string name) { Name = name; }
        public readonly string Name;
        [TestOnly] public readonly List<(long Peer, ZPackage Package)> Sent = new();
        [TestOnly] public Jotunn.Managers.NetworkManager.CoroutineHandler Server = null!, Client = null!;
        public void Initiate() { }
        public void SendPackage(long peer, ZPackage package) => Sent.Add((peer, package));
        public void SendPackage(List<ZNetPeer> peers, ZPackage package) { foreach (var peer in peers) SendPackage(peer.m_uid, package); }
    }
}
namespace Jotunn.Managers
{
    public sealed partial class NetworkManager
    {
        public delegate IEnumerator CoroutineHandler(long sender, ZPackage package);
        public static NetworkManager Instance { get; private set; } = new();
        /// <summary>Every registered RPC by name; <c>ValheimWorldScope.WithNetwork</c> gives a test a fresh manager.</summary>
        [TestOnly] public readonly Dictionary<string, Jotunn.Entities.CustomRPC> Rpcs = new();
        /// <summary>The RPC registered last; a mod with more than one looks them up in <see cref="Rpcs"/>.</summary>
        [TestOnly] public Jotunn.Entities.CustomRPC Rpc = null!;
        public Jotunn.Entities.CustomRPC AddRPC(string name, CoroutineHandler server, CoroutineHandler client)
            => Rpc = Rpcs[name] = new Jotunn.Entities.CustomRPC(name) { Server = server, Client = client };
    }
}
