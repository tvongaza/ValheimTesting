// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Valheim connections and player identity: the socket under a peer's ZRpc, as a Steam or a crossplay (PlayFab) peer
// reports it, the platform-prefixed ids of the game's 1.0.16 admin, ban and permit lists, and ZNet's checks against them.
using System;
using System.Collections.Generic;
using Valheim.Testing.Doubles;

/// <summary>The game's connection under a peer's <see cref="ZRpc"/> (1.0.16).</summary>
public partial interface ISocket
{
    bool IsConnected();
    void Send(ZPackage pkg);
    /// <summary>The next package that arrived, or null when there is none.</summary>
    ZPackage? Recv();
    int GetSendQueueSize();
    int GetCurrentSendRate();
    bool IsHost();
    void Dispose();
    bool GotNewData();
    void Close();
    string GetEndPointString();
    void GetAndResetStats(out int totalSent, out int totalRecv);
    void GetConnectionQuality(out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec);
    ISocket? Accept();
    int GetHostPort();
    bool Flush();
    /// <summary>The remote player's id as the game's admin, ban and permit checks read it; see the Steam and PlayFab sockets.</summary>
    string GetHostName();
    void VersionMatch();
}

/// <summary>
/// An in-process connection end. <see cref="Link"/> joins two ends: a package sent on one arrives on the other, in order,
/// when that side's <see cref="ZRpc.Update"/> reads it, so a test decides the order in which two sides handle their
/// messages. An unlinked end is connected and keeps what it sends in <see cref="Sent"/>. Closing either end closes both
/// (the game's remote side notices a little later). Sizes, compression, timing and loss are not modelled.
/// </summary>
[TestOnly] public abstract partial class SocketDouble : ISocket, IDisposable
{
    private readonly Queue<byte[]> m_inbox = new();
    private SocketDouble? m_remote;
    private bool m_connected = true;
    /// <summary>Every package sent on this end, as sent (pings included), each at read position 0.</summary>
    public readonly List<ZPackage> Sent = new();

    /// <summary>Connects two ends, as a join does.</summary>
    public static void Link(SocketDouble a, SocketDouble b)
    {
        if (a == b) throw new ArgumentException("A socket cannot be linked to itself.");
        a.m_remote = b; b.m_remote = a;
    }

    public bool IsConnected() => m_connected;
    /// <summary>As the game's sockets: an empty package, or one sent after the connection closed, goes nowhere.</summary>
    public void Send(ZPackage pkg)
    {
        if (pkg.Size() == 0 || !m_connected) return;
        byte[] bytes = pkg.GetArray();
        Sent.Add(new ZPackage(bytes));
        m_remote?.m_inbox.Enqueue(bytes);
    }
    public ZPackage? Recv() => m_connected && m_inbox.Count > 0 ? new ZPackage(m_inbox.Dequeue()) : null;
    public bool GotNewData() => m_connected && m_inbox.Count > 0;
    public int GetSendQueueSize() => 0;
    public int GetCurrentSendRate() => 0;
    public bool IsHost() => false;
    public virtual void Close() { m_connected = false; if (m_remote != null) m_remote.m_connected = false; }
    public void Dispose() => Close();
    public abstract string GetEndPointString();
    public abstract string GetHostName();
    public void GetAndResetStats(out int totalSent, out int totalRecv) { totalSent = 0; totalRecv = 0; }
    public void GetConnectionQuality(out float localQuality, out float remoteQuality, out int ping, out float outByteSec, out float inByteSec)
    { localQuality = 0f; remoteQuality = 0f; ping = 0; outByteSec = 0f; inByteSec = 0f; }
    public ISocket? Accept() => null;
    /// <summary>As the game's: -1 on a connection that is not the host's listening socket.</summary>
    public int GetHostPort() => -1;
    public virtual bool Flush() => true;
    public virtual void VersionMatch() { }
}

/// <summary>
/// A peer connected through Steam, as on a server started without crossplay. As in the game (1.0.16), its host name and
/// end point are the bare SteamID64, with no platform prefix.
/// </summary>
public partial class ZSteamSocket : SocketDouble
{
    private readonly ulong m_steamID;
    /// <summary>A connection to the Steam user <paramref name="steamID"/> (a test constructor; the game's take a Steam connection).</summary>
    [TestOnly] public ZSteamSocket(ulong steamID) => m_steamID = steamID;
    public Steamworks.CSteamID GetPeerID() => new(m_steamID);
    public override string GetHostName() => m_steamID.ToString();
    public override string GetEndPointString() => m_steamID.ToString();
}

/// <summary>
/// A peer connected through PlayFab, as on a crossplay server. As in the game (1.0.16), its host name is the platform id
/// the client sent, parsed and written back with its platform prefix (<c>Steam_7656…</c>, <c>Xbox_2535…</c>; a one-letter
/// display prefix such as <c>X_</c> becomes the platform's name, and an id without a prefix does not parse, which leaves
/// the host name empty). Its end point is <c>playfab/</c> and the remote PlayFab entity id.
/// </summary>
public partial class ZPlayFabSocket : SocketDouble
{
    public readonly string m_remotePlayerId;
    private readonly Splatform.PlatformUserID m_platformPlayerId;
    /// <summary>
    /// A connection to the player whose platform sent <paramref name="platformUserID"/>, with PlayFab entity
    /// <paramref name="remotePlayerID"/> (a test constructor; the game's take a PlayFab player or a server id).
    /// </summary>
    [TestOnly] public ZPlayFabSocket(string platformUserID, string remotePlayerID = "")
    {
        m_platformPlayerId = new Splatform.PlatformUserID(platformUserID);
        m_remotePlayerId = remotePlayerID;
    }
    /// <summary>True once <see cref="VersionMatch"/> ran: the game compresses a PlayFab peer's traffic from then on.</summary>
    [TestOnly] public bool Compressing { get; private set; }
    public override string GetHostName() => m_platformPlayerId.ToString();
    public override string GetEndPointString() => "playfab/" + m_remotePlayerId;
    public override void VersionMatch() => Compressing = true;
    /// <summary>Not implemented in the game (1.0.16) either.</summary>
    public override bool Flush() => throw new NotImplementedException();
}

namespace Steamworks
{
    /// <summary>A Steam user id, as Steamworks.NET's; its text is the decimal SteamID64.</summary>
    public partial struct CSteamID : IEquatable<CSteamID>
    {
        public ulong m_SteamID;
        public CSteamID(ulong ulSteamID) => m_SteamID = ulSteamID;
        public static explicit operator ulong(CSteamID that) => that.m_SteamID;
        public static explicit operator CSteamID(ulong value) => new(value);
        public bool Equals(CSteamID other) => m_SteamID == other.m_SteamID;
        public override bool Equals(object? obj) => obj is CSteamID other && Equals(other);
        public override int GetHashCode() => m_SteamID.GetHashCode();
        public static bool operator ==(CSteamID x, CSteamID y) => x.m_SteamID == y.m_SteamID;
        public static bool operator !=(CSteamID x, CSteamID y) => !(x == y);
        public override string ToString() => m_SteamID.ToString();
    }
}

namespace Splatform
{
    /// <summary>A platform's name (<c>Steam</c>, <c>Xbox</c>, ...), as the game's; an empty name is the unknown platform.</summary>
    public partial struct Platform : IEquatable<Platform>, IEquatable<string>
    {
        public readonly string? m_platform;
        public Platform(string platformName) => m_platform = string.IsNullOrEmpty(platformName) ? null : platformName;
        public static Platform Unknown => default;
        public bool IsValid => m_platform != null;
        public bool Equals(Platform other) => m_platform == other.m_platform;
        public bool Equals(string? other) => m_platform == other;
        public override bool Equals(object? obj) => obj is Platform p ? Equals(p) : obj is string s && Equals(s);
        public override int GetHashCode() => m_platform?.GetHashCode() ?? 0;
        public static bool operator ==(Platform lhs, Platform rhs) => lhs.Equals(rhs);
        public static bool operator !=(Platform lhs, Platform rhs) => !lhs.Equals(rhs);
        public static bool operator ==(Platform lhs, string rhs) => lhs.Equals(rhs);
        public static bool operator !=(Platform lhs, string rhs) => !lhs.Equals(rhs);
        public override string ToString() => m_platform ?? "";
    }

    /// <summary>
    /// A player's id on a platform, written <c>Platform_id</c> (<c>Steam_76561198000000001</c>), with the game's parsing
    /// and display form (Splatform as shipped with 1.0.16).
    /// </summary>
    public partial struct PlatformUserID : IEquatable<PlatformUserID>
    {
        // The one-letter prefixes the game shows an id with, and accepts back when parsing.
        private static readonly Dictionary<string, string> s_platformToDisplayPrefixes = new()
        { ["PlayStation"] = "S", ["Xbox"] = "X", ["Nintendo"] = "N", ["GameCenter"] = "A", ["Steam"] = "V" };
        private static readonly Dictionary<string, string> s_displayPrefixesToPlatform = Invert(s_platformToDisplayPrefixes);
        private static Dictionary<string, string> Invert(Dictionary<string, string> map)
        { var inverse = new Dictionary<string, string>(); foreach (var pair in map) inverse[pair.Value] = pair.Key; return inverse; }

        public readonly Platform m_platform;
        public readonly string? m_userID;
        public static PlatformUserID None => default;
        public bool IsValid => m_userID != null && m_platform.IsValid;

        /// <summary>Parses <c>Platform_id</c>, as the game's; text that does not parse gives an invalid id and logs that.</summary>
        public PlatformUserID(string platformUserID)
        {
            if (TryParse(platformUserID, out var parsed)) { m_platform = parsed.m_platform; m_userID = parsed.m_userID; }
            else { ZLog.Log("PlatformUserID \"" + platformUserID + "\" failed to parse!"); m_platform = Platform.Unknown; m_userID = null; }
        }
        public PlatformUserID(Platform platform, string userID) { m_platform = platform; m_userID = userID; }
        public PlatformUserID(string platform, string userID) : this(new Platform(platform), userID) { }
        public PlatformUserID(Platform platform, ulong userID, bool zeroIsInvalid = true)
        { m_platform = platform; m_userID = zeroIsInvalid && userID == 0 ? null : userID.ToString(); }
        public PlatformUserID(string platform, ulong userID, bool zeroIsInvalid = true) : this(new Platform(platform), userID, zeroIsInvalid) { }

        /// <summary>
        /// Splits at the first underscore, which must be neither the first nor the last character. The part before it is
        /// the platform, or a one-letter display prefix that stands for one (<c>X_</c> is Xbox, <c>V_</c> Steam).
        /// </summary>
        public static bool TryParse(string platformUserID, out PlatformUserID platform)
        {
            platform = None;
            if (string.IsNullOrEmpty(platformUserID)) return false;
            int split = platformUserID.IndexOf('_');
            if (split <= 0 || split == platformUserID.Length - 1) return false;
            string prefix = platformUserID.Substring(0, split), id = platformUserID.Substring(split + 1);
            platform = new PlatformUserID(new Platform(s_displayPrefixesToPlatform.TryGetValue(prefix, out var name) ? name : prefix), id);
            return true;
        }
        public bool TryParseAsUInt64(out ulong result) => ulong.TryParse(m_userID, out result);
        public static string GetPlatformPrefix(string platformString) => platformString + "_";
        public static string GetPlatformPrefix(Platform platform) => platform + "_";
        public override string ToString() => IsValid ? GetPlatformPrefix(m_platform) + m_userID : "";

        /// <summary>
        /// The form the game shows an id in: a numeric id with its platform's one-letter prefix (<c>V_</c> for Steam), and
        /// for console platforms the number scrambled (multiplied by 0x9E3779B97F4A7C15, wrapping). Other ids are unchanged.
        /// </summary>
        public static PlatformUserID FilterPlatformUserID(PlatformUserID platID)
        {
            if (!platID.TryParseAsUInt64(out ulong number)) return platID;
            string platform = s_platformToDisplayPrefixes.TryGetValue(platID.m_platform.ToString(), out var prefix) ? prefix : platID.m_platform.ToString();
            return new PlatformUserID(platform, IsPlatformUserIDNumberFiltered(platID) ? unchecked(number * 0x9E3779B97F4A7C15UL) : number, true);
        }
        public static bool IsPlatformUserIDNumberFiltered(PlatformUserID platID) =>
            platID.m_platform == "Nintendo" || platID.m_platform == "PlayStation" || platID.m_platform == "Xbox" || platID.m_platform == "GameCenter";

        public bool Equals(PlatformUserID other) =>
            !IsValid && !other.IsValid || m_platform == other.m_platform && m_userID == other.m_userID;
        public override bool Equals(object? obj) => obj is PlatformUserID other && Equals(other);
        public override int GetHashCode() => ToString().GetHashCode();
        public static bool operator ==(PlatformUserID lhs, PlatformUserID rhs) => lhs.Equals(rhs);
        public static bool operator !=(PlatformUserID lhs, PlatformUserID rhs) => !lhs.Equals(rhs);
    }
}

/// <summary>Which network the game runs on: Steam, or PlayFab for crossplay (the server's <c>-crossplay</c>).</summary>
public enum OnlineBackendType { Steamworks, PlayFab, EOS, CustomSocket, None }

/// <summary>
/// One of the game's id lists (<c>adminlist.txt</c>, <c>bannedlist.txt</c>, <c>permittedlist.txt</c>): one entry per line,
/// compared as exact strings. <see cref="Load"/> reads a file's text as the game does. The file itself is not modelled.
/// </summary>
public partial class SyncedList
{
    private readonly List<string> m_list = new();
    [TestOnly] public SyncedList(params string[] entries) { foreach (var entry in entries) Add(entry); }
    /// <summary>
    /// Replaces the entries with a file's lines, as the game reads it: empty lines and lines starting with <c>//</c> are
    /// skipped and nothing is trimmed, so a line with a stray space never matches.
    /// </summary>
    [TestOnly] public void Load(string fileText)
    {
        m_list.Clear();
        using var reader = new System.IO.StringReader(fileText);
        for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
            if (line.Length > 0 && !line.StartsWith("//")) m_list.Add(line);
    }
    public List<string> GetList() => m_list;
    public int Count() => m_list.Count;
    public bool Contains(string s) => m_list.Contains(s);
    public void Add(string s) { if (!m_list.Contains(s)) m_list.Add(s); }
    public void Remove(string s) => m_list.Remove(s);
}

/// <summary>The admin, ban and permit lists and the game's checks against them (1.0.16).</summary>
public sealed partial class ZNet
{
    /// <summary>The network this ZNet runs on: static in the game (<c>m_onlineBackend</c>), one per ZNet here.</summary>
    [TestOnly] public OnlineBackendType OnlineBackend = OnlineBackendType.Steamworks;
    public static OnlineBackendType m_onlineBackend { get => instance.OnlineBackend; set => instance.OnlineBackend = value; }
    /// <summary>Private in the game (1.0.16); public here for mods built against publicized assemblies.</summary>
    public SyncedList m_adminList = new(), m_bannedList = new(), m_permittedList = new();
    public List<string> Banned => m_bannedList.GetList();
    private static readonly Splatform.Platform s_steamPlatform = new("Steam");

    /// <summary>
    /// Whether <paramref name="hostName"/> (a peer's <c>m_socket.GetHostName()</c>) is on the admin list, as the game
    /// matches it: a Steam peer's bare SteamID64 and a crossplay peer's <c>Steam_…</c> both match either <c>Steam_…</c> or
    /// the bare id; any other platform matches only its prefixed id (<c>Xbox_…</c>); the id's display form
    /// (<c>V_…</c>, or <c>X_</c> and the scrambled number for Xbox) matches too.
    /// </summary>
    public bool IsAdmin(string hostName) => ListContainsId(m_adminList, hostName);

    /// <summary>The game's id match for every list (private in 1.0.16); see <see cref="IsAdmin"/>.</summary>
    public bool ListContainsId(SyncedList list, string idString)
    {
        if (!Splatform.PlatformUserID.TryParse(idString, out var id)) id = new Splatform.PlatformUserID(s_steamPlatform, idString);
        bool found = id.m_platform == s_steamPlatform
            ? list.Contains(id.ToString()) || list.Contains(id.m_userID!)
            : list.Contains(id.ToString());
        var shown = Splatform.PlatformUserID.FilterPlatformUserID(id);
        if (shown != id) found |= list.Contains(shown.ToString());
        return found;
    }

    /// <summary>
    /// False for a banned host name or player name, or a host name missing from a non-empty permit list, as the game
    /// checks a joining peer (private in 1.0.16).
    /// </summary>
    public bool IsAllowed(string hostName, string playerName)
    {
        if (ListContainsId(m_bannedList, hostName) || m_bannedList.Contains(playerName)) return false;
        return m_permittedList.Count() == 0 || ListContainsId(m_permittedList, hostName);
    }

    /// <summary>The ready peer whose socket reports <paramref name="endpoint"/> as its host name, or null.</summary>
    public ZNetPeer? GetPeerByHostName(string endpoint)
    {
        foreach (var peer in GetPeers()) if (peer.IsReady() && peer.m_socket.GetHostName() == endpoint) return peer;
        return null;
    }
}
