using System;
using System.Collections.Generic;
using System.Linq;
using Valheim.Testing.Doubles;
using Xunit;

// The package and RPC doubles against the game's 1.0.16 encoding and delivery rules. Expected bytes are written out by
// hand from the game's encoding (little-endian numbers, 7-bit length-prefixed UTF-8 strings), not produced by the double.
public sealed class RpcTests
{
    public enum Mode { A, B }
    public sealed class Stamp : ISerializableParameter
    {
        public int A; public string B = "";
        public void Serialize(ref ZPackage pkg) { pkg.Write(A); pkg.Write(B); }
        public void Deserialize(ref ZPackage pkg) { A = pkg.ReadInt(); B = pkg.ReadString(); }
    }

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes);
    private static string Bytes(Action<ZPackage> write) { var pkg = new ZPackage(); write(pkg); return Hex(pkg.GetArray()); }
    /// <summary>A network with this session (peer 1) as server or client and the given peers ready.</summary>
    private static ValheimWorldScope Network(bool server, params long[] peers)
    {
        var scope = new ValheimWorldScope().WithNetwork(server).WithZdos();
        foreach (var peer in peers) ZNet.instance.Peers.Add(peer, new ZNetPeer());
        return scope;
    }

    [Fact] public void EveryPackageWriteHasTheGamesBytes()
    {
        Assert.Equal("01-00-00-00", Bytes(p => p.Write(1)));
        Assert.Equal("EF-BE-AD-DE", Bytes(p => p.Write(0xDEADBEEFu)));
        Assert.Equal("FE-FF-FF-FF-FF-FF-FF-FF", Bytes(p => p.Write(-2L)));
        Assert.Equal("01-00-00-00-00-00-00-00", Bytes(p => p.Write(1UL)));
        Assert.Equal("FE-FF", Bytes(p => p.Write((short)-2)));
        Assert.Equal("EF-BE", Bytes(p => p.Write((ushort)0xBEEF)));
        Assert.Equal("07", Bytes(p => p.Write((byte)7)));
        Assert.Equal("FF", Bytes(p => p.Write((sbyte)-1)));
        Assert.Equal("C3-A9", Bytes(p => p.Write('é')));
        Assert.Equal("01", Bytes(p => p.Write(true)));
        Assert.Equal("00-00-C0-3F", Bytes(p => p.Write(1.5f)));
        Assert.Equal("00-00-00-00-00-00-F0-3F", Bytes(p => p.Write(1.0)));
        Assert.Equal("03-68-C3-A9", Bytes(p => p.Write("hé")));                     // byte count, then UTF-8
        Assert.StartsWith("C8-01-61", Bytes(p => p.Write(new string('a', 200)))); // a 7-bit length over two bytes
        Assert.Equal("02-00-00-00-01-02", Bytes(p => p.Write(new byte[] { 1, 2 })));
        Assert.Equal("04-00-00-00-01-00-00-00", Bytes(p => { var inner = new ZPackage(); inner.Write(1); p.Write(inner); }));
        Assert.Equal("02-00-00-00-00-00-00-00-03-00-00-00", Bytes(p => p.Write(new ZDOID(2, 3))));
        Assert.Equal("00-00-80-3F-00-00-00-40-00-00-40-40", Bytes(p => p.Write(new UnityEngine.Vector3(1, 2, 3))));
        Assert.Equal("00-00-00-00-00-00-00-00-00-00-00-00-00-00-80-3F", Bytes(p => p.Write(UnityEngine.Quaternion.identity)));
        Assert.Equal("01-00-00-00-FF-FF-FF-FF", Bytes(p => p.Write(new Vector2i(1, -1))));
        Assert.Equal("01-00-FF-FF", Bytes(p => p.Write(new Vector2s(1, -1))));
        Assert.Equal("05", Bytes(p => p.WriteNumItems(5)));
        Assert.Equal("81-2C", Bytes(p => p.WriteNumItems(300)));
        Assert.Equal("B4-80", Bytes(p => p.WriteSmallRotation(new UnityEngine.Vector3(0, 90, 0))));    // yaw only: two bytes
        Assert.Equal("C0-03-14-A0", Bytes(p => p.WriteSmallRotation(new UnityEngine.Vector3(10, 20, 30))));
    }

    [Fact] public void EveryPackageReadReturnsWhatWasWritten()
    {
        var inner = new ZPackage(); inner.Write("inner");
        var pkg = new ZPackage();
        pkg.Write(1); pkg.Write(0xDEADBEEFu); pkg.Write(-2L); pkg.Write(1UL); pkg.Write((short)-2); pkg.Write((ushort)0xBEEF);
        pkg.Write((byte)7); pkg.Write((sbyte)-1); pkg.Write('é'); pkg.Write(true); pkg.Write(1.5f); pkg.Write(1.0); pkg.Write("hé");
        pkg.Write(new byte[] { 1, 2 }); pkg.Write(inner); pkg.Write(new ZDOID(2, 3)); pkg.Write(new UnityEngine.Vector3(1, 2, 3));
        pkg.Write(new UnityEngine.Quaternion(0.5f, 0.5f, 0.5f, 0.5f)); pkg.Write(new Vector2i(1, -1)); pkg.Write(new Vector2s(1, -1));
        pkg.WriteNumItems(300); pkg.WriteSmallRotation(new UnityEngine.Vector3(10, 20, 30)); pkg.WriteCompressed(inner);
        pkg.SetPos(0);
        Assert.Equal(1, pkg.ReadInt()); Assert.Equal(0xDEADBEEFu, pkg.ReadUInt()); Assert.Equal(-2L, pkg.ReadLong()); Assert.Equal(1UL, pkg.ReadULong());
        Assert.Equal((short)-2, pkg.ReadShort()); Assert.Equal((ushort)0xBEEF, pkg.ReadUShort()); Assert.Equal((byte)7, pkg.ReadByte());
        Assert.Equal((sbyte)-1, pkg.ReadSByte()); Assert.Equal('é', pkg.ReadChar()); Assert.True(pkg.ReadBool());
        Assert.Equal(1.5f, pkg.ReadSingle()); Assert.Equal(1.0, pkg.ReadDouble()); Assert.Equal("hé", pkg.ReadString());
        Assert.Equal(new byte[] { 1, 2 }, pkg.ReadByteArray()); Assert.Equal("inner", pkg.ReadPackage().ReadString());
        var id = pkg.ReadZDOID(); Assert.Equal((2L, 3L), (id.UserID, id.ID));
        var v = pkg.ReadVector3(); Assert.Equal((1f, 2f, 3f), (v.x, v.y, v.z));
        var q = pkg.ReadQuaternion(); Assert.Equal((0.5f, 0.5f, 0.5f, 0.5f), (q.x, q.y, q.z, q.w));
        Assert.Equal(new Vector2i(1, -1), pkg.ReadVector2i()); Assert.Equal(new Vector2s(1, -1), pkg.ReadVector2s());
        Assert.Equal(300, pkg.ReadNumItems());
        var r = pkg.ReadSmallRotation(); Assert.Equal((10f, 20f, 30f), (r.x, r.y, r.z));
        Assert.Equal("inner", pkg.ReadCompressedPackage().ReadString());
        Assert.Equal(pkg.Size(), pkg.GetPos());
    }

    [Fact] public void APackageIsOneStreamThatCanBeRereadCopiedAndCleared()
    {
        var pkg = new ZPackage(); pkg.Write(7); pkg.Write("x");
        Assert.Throws<System.IO.EndOfStreamException>(() => pkg.ReadInt()); // written, not rewound: the game reads at the end too
        pkg.SetPos(4); Assert.Equal("x", pkg.ReadString()); pkg.SetPos(0); Assert.Equal(7, pkg.ReadInt());
        var bytes = pkg.GetArray();
        Assert.Equal(6, pkg.Size()); Assert.Equal(6, bytes.Length);
        Assert.Equal(7, new ZPackage(bytes).ReadInt());
        Assert.Equal(4, new ZPackage(bytes, 4).Size());
        var fromBase64 = new ZPackage(pkg.GetBase64()); Assert.Equal(7, fromBase64.ReadInt()); Assert.Equal("x", fromBase64.ReadString());
        Assert.Equal(new byte[] { 7, 0 }, new ZPackage(bytes).ReadByteArray(2));
        Assert.Equal(64, pkg.GenerateHash().Length); Assert.Equal(pkg.GenerateHash(), new ZPackage(bytes).GenerateHash());
        var outer = new ZPackage(); outer.Write(pkg); outer.SetPos(0);
        var into = new ZPackage(); into.Write(99); outer.ReadPackage(ref into);
        Assert.Equal(bytes, into.GetArray()); Assert.Equal(0, into.GetPos());
        var compressed = new ZPackage(pkg.GetCompressed()); compressed.Decompress();
        Assert.Equal(7, compressed.ReadInt()); Assert.Equal("x", compressed.ReadString());
        pkg.Clear(); Assert.Equal(0, pkg.Size()); Assert.Equal(0, pkg.GetPos());
        pkg.Load(new byte[] { 1, 0, 0, 0 }); Assert.Equal(1, pkg.ReadInt());
    }

    [Fact] public void RoutedArgumentsAreWrittenWithTheGamesTypeTable()
    {
        using var scope = Network(server: false, 5);
        ZRoutedRpc.instance.InvokeRoutedRPC(5, "Mod_Numbers", 1, 0xDEADBEEFu, -2L, 1.5f, 1.0, true);
        var inner = new ZPackage(); inner.Write(1);
        ZRoutedRpc.instance.InvokeRoutedRPC(5, "Mod_Values", "hé", inner, new List<string> { "a", "b" }, new UnityEngine.Vector3(1, 2, 3),
            UnityEngine.Quaternion.identity, new ZDOID(2, 3), new Stamp { A = 7, B = "x" });
        Assert.Equal(new[] { 5L, 5L }, ZRoutedRpc.instance.Sent.Select(s => s.Peer));
        var numbers = ZRoutedRpc.instance.Sent[0].Data;
        Assert.Equal("01-00-00-00-EF-BE-AD-DE-FE-FF-FF-FF-FF-FF-FF-FF-00-00-C0-3F-00-00-00-00-00-00-F0-3F-01", Hex(numbers.m_parameters.GetArray()));
        Assert.Equal(("Mod_Numbers".GetStableHashCode(), 1L, 5L, 2L), (numbers.m_methodHash, numbers.m_senderPeerID, numbers.m_targetPeerID, numbers.m_msgID));
        Assert.True(numbers.m_targetZDO.IsNone());
        Assert.Equal("03-68-C3-A9" + "-04-00-00-00-01-00-00-00" + "-02-00-00-00-01-61-01-62" + "-00-00-80-3F-00-00-00-40-00-00-40-40"
            + "-00-00-00-00-00-00-00-00-00-00-00-00-00-00-80-3F" + "-02-00-00-00-00-00-00-00-03-00-00-00" + "-07-00-00-00-01-78",
            Hex(ZRoutedRpc.instance.Sent[1].Data.m_parameters.GetArray()));
    }

    [Fact] public void AHandlerReadsEverySupportedTypeBack()
    {
        using var scope = Network(server: true);
        var got = new List<object>();
        ZRoutedRpc.instance.Register<int, uint, long, float, double, bool>("Mod_Numbers", (s, a, b, c, d, e, f) => got.AddRange(new object[] { a, b, c, d, e, f }));
        ZRoutedRpc.instance.Register<string, ZPackage, List<string>, UnityEngine.Vector3, UnityEngine.Quaternion, ZDOID>("Mod_Values",
            (s, a, b, c, d, e, f) => got.AddRange(new object[] { a, b.ReadInt(), string.Join(",", c), d.y, e.w, f.UserID }));
        ZRoutedRpc.instance.Register<Stamp>("Mod_Stamp", (s, stamp) => got.Add(stamp.A + stamp.B));
        ZRoutedRpc.instance.Deliver(5, "Mod_Numbers", 1, 2u, 3L, 4f, 5.0, true);
        var inner = new ZPackage(); inner.Write(6);
        ZRoutedRpc.instance.Deliver(5, "Mod_Values", "s", inner, new List<string> { "a", "b" }, new UnityEngine.Vector3(0, 7, 0), UnityEngine.Quaternion.identity, new ZDOID(8, 9));
        ZRoutedRpc.instance.Deliver(5, "Mod_Stamp", new Stamp { A = 10, B = "x" });
        Assert.Equal(new object[] { 1, 2u, 3L, 4f, 5.0, true, "s", 6, "a,b", 7f, 1f, 8L, "10x" }, got);
    }

    // The game writes a package argument into the call and the handler reads a new one, at position 0.
    [Fact] public void HandlersReceiveCopiesReadFromTheCall()
    {
        using var scope = Network(server: true);
        ZPackage? received = null; List<string>? names = null;
        ZRoutedRpc.instance.Register<ZPackage, List<string>>("Mod_Sync", (s, p, l) => { received = p; names = l; });
        var sent = new ZPackage(); sent.Write(42); var list = new List<string> { "a" };
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "Mod_Sync", sent, list);
        Assert.NotSame(sent, received); Assert.NotSame(list, names);
        Assert.Equal(0, received!.GetPos()); Assert.Equal(42, received.ReadInt()); Assert.Equal(new[] { "a" }, names);
    }

    // The game skips these without an error, so the handler reads the wrong bytes; the double refuses them by name.
    [Fact] public void AnArgumentTheGameCannotWriteIsRefusedByName()
    {
        using var scope = Network(server: true);
        ZRoutedRpc.instance.Register<Mode>("Mod_Mode", (s, m) => { });
        ZRoutedRpc.instance.Register<int>("Mod_Int", (s, n) => { });
        Assert.Contains("argument 1 is Mode (an enum)", Assert.Throws<ArgumentException>(() => ZRoutedRpc.instance.Deliver(5, "Mod_Mode", Mode.B)).Message);
        Assert.Contains("argument 1 is Byte[]", Assert.Throws<ArgumentException>(() => ZRoutedRpc.instance.Deliver(5, "Mod_Int", new byte[] { 1 })).Message);
        Assert.Contains("argument 2 is Vector2i", Assert.Throws<ArgumentException>(() => ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "Mod_Int", 1, new Vector2i(1, 1))).Message);
        Assert.Contains("argument 1 is Byte", Assert.Throws<ArgumentException>(() => ZRoutedRpc.instance.InvokeRoutedRPC(5, "Mod_Int", (byte)1)).Message);
        Assert.Contains("argument 1 is null", Assert.Throws<ArgumentException>(() => ZRoutedRpc.instance.InvokeRoutedRPC(5, "Mod_Int", new object?[] { null }!)).Message);
        Assert.Empty(ZRoutedRpc.instance.Invoked); Assert.Empty(ZRoutedRpc.instance.Sent);
    }

    // The game reads each parameter's own encoding from the bytes, so another type misreads; the double throws instead.
    [Fact] public void AHandlerThatWouldMisreadTheCallFails()
    {
        using var scope = Network(server: true);
        var got = new List<object>();
        ZRoutedRpc.instance.Register<long>("Mod_Long", (s, n) => got.Add(n));
        ZRoutedRpc.instance.Register<double>("Mod_Double", (s, n) => got.Add(n));
        ZRoutedRpc.instance.Register<int, int>("Mod_Pair", (s, a, b) => got.Add(a + b));
        ZRoutedRpc.instance.Register<int>("Mod_One", (s, a) => got.Add(a));
        ZRoutedRpc.instance.Register<Mode>("Mod_Mode", (s, m) => got.Add(m));
        Assert.Contains("argument 1 was sent as Int32 but the handler reads Int64", Assert.Throws<InvalidOperationException>(() => ZRoutedRpc.instance.Deliver(5, "Mod_Long", 5)).Message);
        Assert.Contains("sent as Single but the handler reads Double", Assert.Throws<InvalidOperationException>(() => ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "Mod_Double", 1f)).Message);
        Assert.Contains("the call sent 1 argument(s) but the handler reads 2", Assert.Throws<InvalidOperationException>(() => ZRoutedRpc.instance.Deliver(5, "Mod_Pair", 1)).Message);
        Assert.Contains("parameter 1 is Mode (an enum), which the game cannot read", Assert.Throws<InvalidOperationException>(() => ZRoutedRpc.instance.Deliver(5, "Mod_Mode", 1)).Message);
        ZRoutedRpc.instance.Deliver(5, "Mod_One", 1, 2); // extra arguments are left unread, as in the game
        ZRoutedRpc.instance.Deliver(5, "Mod_Long", 5L);
        Assert.Equal(new object[] { 1, 5L }, got);
    }

    [Fact] public void ABroadcastOrACallToSelfRunsTheLocalHandlerBeforeAnythingIsSent()
    {
        using var scope = Network(server: true, 5);
        var sentWhenRun = new List<int>();
        ZRoutedRpc.instance.Register<int>("Mod_Ping", (s, n) => sentWhenRun.Add(ZRoutedRpc.instance.Sent.Count));
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "Mod_Ping", 1);
        Assert.Equal(new[] { 0 }, sentWhenRun); Assert.Equal(new[] { 5L }, ZRoutedRpc.instance.Sent.Select(s => s.Peer));
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.PeerId, "Mod_Ping", 2);
        Assert.Equal(new[] { 0, 1 }, sentWhenRun); Assert.Single(ZRoutedRpc.instance.Sent); // to self: nothing sent
        ZRoutedRpc.instance.InvokeRoutedRPC(5, "Mod_Ping", 3);
        Assert.Equal(2, sentWhenRun.Count); Assert.Equal(2, ZRoutedRpc.instance.Sent.Count);  // to a peer: not run here
    }

    // Issue #16's acceptance: "am I a client" guarded by a local player is also true on a listen-server host.
    [Fact] public void AClientOnlyBroadcastHandlerStillRunsOnAListenServerHost()
    {
        using var scope = Network(server: true, 5);
        scope.WithLocalPlayer(new UnityEngine.Vector3(0, 30, 0));
        var shown = new List<string>();
        ZRoutedRpc.instance.Register<string>("Mod_ShowMessage", (sender, text) => { if (Player.m_localPlayer != null) shown.Add(text); });
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "Mod_ShowMessage", "hello");
        Assert.True(ZNet.instance.IsServer()); Assert.Equal(new[] { "hello" }, shown);
    }

    [Fact] public void ACallToAnUnregisteredNameIsDroppedSilentlyAndCounted()
    {
        using var scope = Network(server: true);
        var log = scope.CaptureLog();
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "Mod_Nobody", 1);
        ZRoutedRpc.instance.Deliver(5, "Mod_Nobody");
        Assert.Equal(new[] { (1L, "Mod_Nobody", "no handler registered"), (5L, "Mod_Nobody", "no handler registered") },
            ZRoutedRpc.instance.Dropped.Select(d => (d.Sender, d.Method, d.Reason)));
        Assert.Empty(log);
    }

    [Fact] public void APeerThatIsNotReadyReceivesNothing()
    {
        using var scope = Network(server: true, 5);
        ZNet.instance.Peers.Add(6, new ZNetPeer { Ready = false });
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "Mod_Ping");
        ZRoutedRpc.instance.InvokeRoutedRPC(6, "Mod_Ping");
        Assert.Equal(new[] { 5L }, ZRoutedRpc.instance.Sent.Select(s => s.Peer));
        Assert.Equal(new[] { 5L, 0L }, ZNet.instance.GetPeers().Select(p => p.m_uid)); Assert.Null(ZNet.instance.GetPeer(6));
        Assert.False(ZNet.instance.Peers[6].IsReady()); Assert.True(ZNet.instance.Peers[5].IsReady());
        ZNet.instance.Peers[6].Ready = true;
        ZRoutedRpc.instance.InvokeRoutedRPC(6, "Mod_Ping");
        Assert.Equal(new[] { 5L, 6L }, ZRoutedRpc.instance.Sent.Select(s => s.Peer));
    }

    [Fact] public void RegisteringANameTwiceThrows()
    {
        using var scope = Network(server: true);
        ZRoutedRpc.instance.Register("Mod_Once", s => { });
        Assert.Throws<ArgumentException>(() => ZRoutedRpc.instance.Register<int>("Mod_Once", (s, n) => { }));
        var view = new ZNetView(ZDOMan.instance!.CreateNewZDO(UnityEngine.Vector3.zero, 1));
        view.Register("RPC_Once", s => { });
        Assert.Throws<ArgumentException>(() => view.Register("RPC_Once", s => { }));
        view.Unregister("RPC_Once"); view.Register("RPC_Once", s => { }); // the game's ZNetView can unregister
        Assert.True(view.IsRegistered("RPC_Once"));
    }

    [Fact] public void ACallWithoutATargetGoesToTheServerOrTheFirstPeer()
    {
        var ran = new List<long>();
        using (Network(server: true, 5))
        {
            ZRoutedRpc.instance.Register("Mod_ToServer", s => ran.Add(s));
            ZRoutedRpc.instance.InvokeRoutedRPC("Mod_ToServer");
            Assert.Equal(1L, ZRoutedRpc.instance.GetServerPeerID()); Assert.Equal(new[] { 1L }, ran); Assert.Empty(ZRoutedRpc.instance.Sent);
        }
        using (Network(server: false, 5))
        {
            ZRoutedRpc.instance.Register("Mod_ToServer", s => ran.Add(s));
            ZRoutedRpc.instance.InvokeRoutedRPC("Mod_ToServer");
            Assert.Equal(5L, ZRoutedRpc.instance.GetServerPeerID()); Assert.Equal(new[] { 1L }, ran);
            Assert.Equal(new[] { (5L, 5L) }, ZRoutedRpc.instance.Sent.Select(s => (s.Peer, s.Data.m_targetPeerID)));
        }
        using (Network(server: false))
        {
            ZRoutedRpc.instance.Register("Mod_ToServer", s => ran.Add(s));
            ZRoutedRpc.instance.InvokeRoutedRPC("Mod_ToServer"); // no peer: to Everybody, so it runs here
            Assert.Equal(ZRoutedRpc.Everybody, ZRoutedRpc.instance.GetServerPeerID()); Assert.Equal(new[] { 1L, 1L }, ran);
        }
    }

    [Fact] public void HandlersTakeUpToSixArguments()
    {
        using var scope = Network(server: true);
        var got = new List<string>();
        ZRoutedRpc.instance.Register<int, int, int, int>("Mod_Four", (s, a, b, c, d) => got.Add($"{a}{b}{c}{d}"));
        ZRoutedRpc.instance.Register<int, int, int, int, int>("Mod_Five", (s, a, b, c, d, e) => got.Add($"{a}{b}{c}{d}{e}"));
        ZRoutedRpc.instance.Register<int, long, float, bool, string, ZDOID>("Mod_Six", (s, a, b, c, d, e, f) => got.Add($"{a}{b}{c}{d}{e}{f.ID}"));
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "Mod_Four", 1, 2, 3, 4);
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "Mod_Five", 1, 2, 3, 4, 5);
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "Mod_Six", 1, 2L, 3f, true, "x", new ZDOID(0, 6));
        Assert.Equal(new[] { "1234", "12345", "123Truex6" }, got);
    }

    // As the game's RPC_RoutedRPC: the server runs what is for it and passes the rest on, never back to the sender.
    [Fact] public void TheServerRelaysAPeersCallToEveryoneButTheSender()
    {
        using var scope = Network(server: true, 5, 6, 7);
        var ran = new List<long>();
        ZRoutedRpc.instance.Register<int>("Mod_Chat", (s, n) => ran.Add(s));
        ZRoutedRpc.instance.Receive(5, ZRoutedRpc.Everybody, ZDOID.None, "Mod_Chat", 1);
        Assert.Equal(new[] { 5L }, ran); Assert.Equal(new[] { 6L, 7L }, ZRoutedRpc.instance.Sent.Select(s => s.Peer));
        ZRoutedRpc.instance.Receive(5, 7, ZDOID.None, "Mod_Chat", 2);
        Assert.Single(ran); Assert.Equal(new[] { 6L, 7L, 7L }, ZRoutedRpc.instance.Sent.Select(s => s.Peer));
        Assert.All(ZRoutedRpc.instance.Sent, s => Assert.Equal(5L, s.Data.m_senderPeerID));
    }

    [Fact] public void PerObjectCallsReachOnlyThatObjectOnItsOwnerOrEverybody()
    {
        using var scope = Network(server: true, 5).WithScene();
        var prefab = ZNetScene.instance!.AddPrefab("piece_chest");
        var a = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(1, 30, 1), UnityEngine.Quaternion.identity).GetComponent<ZNetView>();
        var b = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(2, 30, 2), UnityEngine.Quaternion.identity).GetComponent<ZNetView>();
        var hits = new List<(string View, long Sender, int Damage)>();
        a.Register<int>("RPC_Damage", (s, n) => hits.Add(("a", s, n)));
        b.Register<int>("RPC_Damage", (s, n) => hits.Add(("b", s, n)));
        a.InvokeRPC(ZNetView.Everybody, "RPC_Damage", 1);
        Assert.Equal(new[] { ("a", 1L, 1) }, hits);
        var sent = Assert.Single(ZRoutedRpc.instance.Sent);
        Assert.Equal((5L, a.GetZDO().m_uid), (sent.Peer, sent.Data.m_targetZDO));
        a.InvokeRPC("RPC_Damage", 2);                        // owned here: runs here, nothing sent
        a.GetZDO().SetOwner(5); a.InvokeRPC("RPC_Damage", 3); // owned by peer 5: sent there only
        a.GetZDO().SetOwner(0); a.InvokeRPC("RPC_Damage", 4); // no owner: target 0 is Everybody
        Assert.Equal(new[] { ("a", 1L, 1), ("a", 1L, 2), ("a", 1L, 4) }, hits);
        Assert.Equal(new[] { (5L, 0L), (5L, 5L), (5L, 0L) }, ZRoutedRpc.instance.Sent.Select(s => (s.Peer, s.Data.m_targetPeerID)));
        Assert.Empty(ZRoutedRpc.instance.Dropped);
    }

    [Fact] public void APerObjectCallToAnUnknownNameIsLoggedAndOneToAMissingObjectIsDropped()
    {
        using var scope = Network(server: true).WithScene();
        var log = scope.CaptureLog();
        var prefab = ZNetScene.instance!.AddPrefab("piece_chest");
        var chest = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(1, 30, 1), UnityEngine.Quaternion.identity);
        var view = chest.GetComponent<ZNetView>();
        view.InvokeRPC(ZNetView.Everybody, "RPC_Unknown");
        Assert.Equal(new[] { "Failed to find rpc method " + "RPC_Unknown".GetStableHashCode() }, log);
        view.Register("RPC_Open", s => { }); view.Unregister("RPC_Open"); view.InvokeRPC(ZNetView.Everybody, "RPC_Open");
        Assert.Equal(2, log.Count);
        var id = view.GetZDO().m_uid;
        ZNetScene.instance.Destroy(chest);
        Assert.Throws<NullReferenceException>(() => view.InvokeRPC(ZNetView.Everybody, "RPC_Open")); // the view let go of its ZDO, as the game's
        ZRoutedRpc.instance.InvokeRoutedRPC(ZNetView.Everybody, id, "RPC_Open");  // destroyed: no live object
        ZDOMan.instance!.ProcessDestroyed();
        ZRoutedRpc.instance.InvokeRoutedRPC(ZNetView.Everybody, id, "RPC_Open");  // and then no ZDO either
        Assert.Equal(2, log.Count);
        Assert.Equal(new[] { "not registered on the object", "not registered on the object", "no live object", "no such ZDO" }, ZRoutedRpc.instance.Dropped.Select(d => d.Reason));
    }

    [Fact] public void AViewBuiltAroundABareZdoIsReachedThroughIt()
    {
        using var scope = Network(server: true);
        var view = new ZNetView(ZDOMan.instance!.CreateNewZDO(UnityEngine.Vector3.zero, 1));
        var got = new List<long>();
        view.Register<long>("RPC_Mark", (s, n) => got.Add(n));
        view.InvokeRPC(ZNetView.Everybody, "RPC_Mark", 9L);
        ZRoutedRpc.instance.Receive(5, ZRoutedRpc.Everybody, view.GetZDO().m_uid, "RPC_Mark", 10L); // as if from peer 5
        Assert.Equal(new[] { 9L, 10L }, got);
    }
}
