// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// The process's role as the game sees it (dedicated server, listen-server host, client, main menu), the players each
// role has, as ValheimWorldScope presets.
using System;
using System.Collections.Generic;
using Valheim.Testing.Doubles;

/// <summary>A player, as mod code finds one: its view (and so its ZDO), its name and id from the ZDO, and the list of every player object.</summary>
public sealed partial class Player
{
    internal static List<Player> s_players = new();
    /// <summary>Every player object, as the game's list: the main-menu preview and remote players count too.</summary>
    public static List<Player> GetAllPlayers() => s_players;
    /// <summary>The player's view: over its ZDO in a world, over none for the main-menu preview. Protected in the game (Character); public here, as with publicized assemblies.</summary>
    public ZNetView m_nview = new(null!);
    public bool IsOwner() => m_nview.IsValid() && m_nview.IsOwner();
    public ZDOID GetZDOID() => m_nview.IsValid() ? m_nview.GetZDO().m_uid : ZDOID.None;
    /// <summary>The name in the ZDO ("..." when unset), or "" with no ZDO, as the game's.</summary>
    public string GetPlayerName() => m_nview.IsValid() ? m_nview.GetZDO().GetString(ZDOVars.s_playerName, "...") : "";
    public long GetPlayerID() => m_nview.IsValid() ? m_nview.GetZDO().GetLong(ZDOVars.s_playerID) : 0L;
}

public sealed partial class ZNet
{
    /// <summary>A dedicated server (no local player, no Steam client). A test switch; <c>ValheimWorldScope.AsDedicatedServer</c> sets it.</summary>
    [TestOnly] public bool Dedicated;
    public bool IsDedicated() => Dedicated;
}

public static partial class ZDOVars
{
    public static readonly int s_playerName = "playerName".GetStableHashCode();
    public static readonly int s_playerID = "playerID".GetStableHashCode();
}

namespace Valheim.Testing.Doubles
{
    /// <summary>
    /// Role presets. Each installs a fresh network, ZDOs, zone system and scene (or, at the main menu, none), and the
    /// players the role has:
    /// <list type="bullet">
    /// <item><see cref="AsDedicatedServer"/>: <c>IsServer()</c> and <c>IsDedicated()</c> true, no local player.
    /// Localization and PlatformPrefs work, as on a 1.0.16 dedicated server.</item>
    /// <item><see cref="AsHost"/>: a listen server: <c>IsServer()</c> true, not dedicated, a local player.</item>
    /// <item><see cref="AsClient"/>: not the server, a local player.</item>
    /// <item><see cref="AtMainMenu"/>: no world (<c>ZNet.instance</c>, <c>ZDOMan.instance</c>, <c>ZNetScene.instance</c> and the
    /// others are null) and a preview <see cref="Player"/> with no ZDO that is not the local player, as the character
    /// select screen has.</item>
    /// </list>
    /// <see cref="AddRemotePlayer"/> adds another peer's player. What the presets change, the scope puts back on dispose
    /// with every other static (WorldDoubles.cs).
    /// </summary>
    public sealed partial class ValheimWorldScope
    {
        /// <summary>The preview character <see cref="AtMainMenu"/> made, or null.</summary>
        public Player? PreviewPlayer { get; private set; }

        /// <summary>
        /// A dedicated server: server, dedicated, no local player, a world's singletons, and fresh preferences and
        /// localization. In 1.0.16 PlatformPrefs falls back to PlayerPrefs on a dedicated server, so a plugin's Awake may
        /// touch Localization and PlatformPrefs here; to test against the pre-1.0 client failure instead, set
        /// <see cref="PlatformPrefs.Unavailable"/> (the scope restores it).
        /// </summary>
        public ValheimWorldScope AsDedicatedServer()
        {
            EnterWorld(server: true, dedicated: true);
            Player.m_localPlayer = null;
            return this;
        }

        /// <summary>A listen-server host: server, not dedicated, with a local player at <paramref name="position"/> (owned by this session). A broadcast runs both its server and client handlers.</summary>
        public ValheimWorldScope AsHost(UnityEngine.Vector3 position = default)
        {
            EnterWorld(server: true, dedicated: false);
            SpawnLocalPlayer(position);
            return this;
        }
        /// <summary>A client in a world: not the server, with a local player at <paramref name="position"/>.</summary>
        public ValheimWorldScope AsClient(UnityEngine.Vector3 position = default)
        {
            EnterWorld(server: false, dedicated: false);
            SpawnLocalPlayer(position);
            return this;
        }
        /// <summary>
        /// The main menu: no world, network, scene or local player, and a preview <see cref="Player"/> (<see cref="PreviewPlayer"/>)
        /// whose view has no ZDO; like the game's preview it is in <see cref="Player.GetAllPlayers"/>, so a player patch runs for it.
        /// </summary>
        public ValheimWorldScope AtMainMenu()
        {
            EmptyUnityScene();
            ZNet.instance = null!; ZRoutedRpc.instance = null!;
            ZDOMan.instance = null; ZoneSystem.instance = null; ZNetScene.instance = null; WorldGenerator.instance = null;
            global::Heightmap.s_heightmaps = new List<global::Heightmap>(); _ownsHeightmaps = true;
            Player.m_localPlayer = null;
            Player.s_players = new List<Player>();
            PreviewPlayer = new Player();
            Player.s_players.Add(PreviewPlayer);
            return this;
        }

        /// <summary>
        /// Another peer's player at <paramref name="position"/>: its ZDO is owned by <paramref name="peerId"/> and carries
        /// its name. On a server the peer is also connected (<c>ZNet.GetPeers</c>, with the player as its character); a
        /// client connects only to the server, so there it is only a player object.
        /// </summary>
        public Player AddRemotePlayer(long peerId, UnityEngine.Vector3 position, string name = "")
        {
            var zdos = ZDOMan.instance ?? throw new InvalidOperationException("A remote player needs a world: use a preset (AsHost, AsClient, AsDedicatedServer) or WithZdos first.");
            if (peerId == zdos.m_sessionID) throw new ArgumentException("That is this session's own id.", nameof(peerId));
            var player = SpawnPlayer(position, peerId, name);
            if (ZNet.instance != null && ZNet.instance.IsServer())
                ZNet.instance.Peers[peerId] = new ZNetPeer { m_uid = peerId, m_characterID = player.GetZDOID(), m_playerName = name };
            return player;
        }

        /// <summary>A config disk with no files yet: plugin configs start from their defaults.</summary>
        public ValheimWorldScope WithConfigFiles() { BepInEx.Configuration.ConfigFile.s_files = new Dictionary<string, string>(StringComparer.Ordinal); return this; }

        private void EnterWorld(bool server, bool dedicated)
        {
            WithNetwork(server);
            ZNet.instance.Dedicated = dedicated;
            WithZdos(); WithZoneSystem(); WithScene();
            Player.s_players = new List<Player>();
            PreviewPlayer = null;
            Localization.Current = null; ZInput.Current = null;
            PlatformPrefs.s_values = new Dictionary<string, object>();
        }
        private void SpawnLocalPlayer(UnityEngine.Vector3 position) => Player.m_localPlayer = SpawnPlayer(position, ZDOMan.instance!.m_sessionID, "");
        private static Player SpawnPlayer(UnityEngine.Vector3 position, long owner, string name)
        {
            var zdo = ZDOMan.instance!.CreateNewZDO(position, "Player".GetStableHashCode());
            zdo.SetOwner(owner);
            if (name.Length > 0) zdo.Set(ZDOVars.s_playerName, name);
            var player = new Player { m_nview = new ZNetView(zdo) };
            player.transform.position = position;
            Player.s_players.Add(player);
            return player;
        }
    }
}
