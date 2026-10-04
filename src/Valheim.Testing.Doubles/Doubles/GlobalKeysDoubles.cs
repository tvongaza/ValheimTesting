// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Global keys, as ZoneSystem keeps them in Valheim 1.0.16. Only the server changes them: SetGlobalKey and
// RemoveGlobalKey are routed calls to the server, which applies the change and broadcasts its WHOLE list; a client
// replaces its list with each broadcast. The server's handlers exist once ZoneSystem.Start has run (when the game
// scene starts, with Game.Start); a call before that finds no handler and is dropped. A key is stored lower case,
// as "name" or "name value"; setting a name again replaces its value.
using System.Collections.Generic;
using System.Globalization;
using Valheim.Testing.Doubles;

/// <summary>
/// The game's named global keys (1.0.16), in its order. Everything before <see cref="NonServerOption"/> is a world
/// modifier (a server option, kept with the world's starting keys); the rest are progression keys. Any other string
/// is a valid key too; it counts as <see cref="NonServerOption"/>.
/// </summary>
public enum GlobalKeys
{
    PlayerDamage,
    EnemyDamage,
    WorldLevel,
    EventRate,
    ResourceRate,
    StaminaRate,
    AdrenalineRate,
    EitrRate,
    DurabilityRate,
    FoodRate,
    MoveStaminaRate,
    StaminaRegenRate,
    SkillGainRate,
    SkillReductionRate,
    EnemySpeedSize,
    EnemyLevelUpRate,
    CarryWeightRate,
    PlayerEvents,
    Fire,
    DeathKeepEquip,
    DeathDeleteItems,
    DeathDeleteUnequipped,
    DeathSkillsReset,
    DeathKeepInventory,
    NoBuildCost,
    NoCraftCost,
    AllPiecesUnlocked,
    NoWorkbench,
    AllRecipesUnlocked,
    WorldLevelLockedTools,
    PassiveMobs,
    NoMap,
    NoPortals,
    NoBossPortals,
    DungeonBuild,
    TeleportAll,
    NoPseudoDrops,
    NoBuildingFall,
    NoHeavySnow,
    AllHeavySnow,
    Preset,
    NonServerOption,
    defeated_eikthyr,
    defeated_dragon,
    defeated_goblinking,
    defeated_gdking,
    defeated_bonemass,
    activeBosses,
    StoneCircle,
    KilledTroll,
    killed_surtling,
    KilledBat,
    AshlandsOcean,
    Count,
}

public partial class ZoneSystem
{
    /// <summary>The key lines, lower case: "name" or "name value". Private in the game; public here for mods built against publicized assemblies.</summary>
    public readonly HashSet<string> m_globalKeys = new();
    /// <summary>The named keys that are set (any value).</summary>
    public HashSet<GlobalKeys> m_globalKeysEnums = new();
    /// <summary>Each set key's name and value ("" for a key without one).</summary>
    public Dictionary<string, string> m_globalKeysValues = new();

    /// <summary>
    /// The loaded world's starting keys (<c>ZNet.World.m_startingGlobalKeys</c> in the game): where a server sets and
    /// removes the world modifiers among its keys, and what <see cref="SetStartingGlobalKeys"/> reads back after a
    /// restart. Null when no world is loaded, as on a client; nothing is written then.
    /// </summary>
    [TestOnly] public List<string>? WorldStartingGlobalKeys;

    /// <summary>Whether <see cref="Start"/> has run.</summary>
    [TestOnly] public bool Started { get; private set; }
    private bool m_startedAsServer;

    /// <summary>
    /// As the game's ZoneSystem.Start, which runs when the game scene starts (with Game.Start): the server registers
    /// the SetGlobalKey and RemoveGlobalKey handlers on <c>ZRoutedRpc.instance</c>, a client the GlobalKeys handler.
    /// Runs once. Private in the game.
    /// </summary>
    public void Start()
    {
        if (Started) return;
        Started = true;
        m_startedAsServer = ZNet.instance.IsServer();
        if (m_startedAsServer)
        {
            ZRoutedRpc.instance.Register<string>("SetGlobalKey", RPC_SetGlobalKey);
            ZRoutedRpc.instance.Register<string>("RemoveGlobalKey", RPC_RemoveGlobalKey);
        }
        else
        {
            ZRoutedRpc.instance.Register<List<string>>("GlobalKeys", RPC_GlobalKeys);
        }
    }

    /// <summary>As the game does when a peer connects: the server sends it the whole list. Private in the game.</summary>
    public void OnNewPeer(long peerID)
    {
        if (ZNet.instance.IsServer()) SendGlobalKeys(peerID);
    }

    public void SetGlobalKey(GlobalKeys key, float value) => SetGlobalKey($"{key} {value.ToString(CultureInfo.InvariantCulture)}");
    public void SetGlobalKey(GlobalKeys key) => SetGlobalKey(key.ToString());
    /// <summary>
    /// A routed call to the server. On a client it goes out through <c>ZRoutedRpc.instance</c> and changes nothing
    /// here until the server's broadcast arrives. On the server the game's call reaches its own handler at once; the
    /// double calls that handler directly, so the call is not in <c>ZRoutedRpc.Invoked</c>. Before <see cref="Start"/>
    /// the server has no handler and the key is dropped without an error.
    /// </summary>
    public void SetGlobalKey(string name)
    {
        if (!ZNet.instance.IsServer()) { ZRoutedRpc.instance.InvokeRoutedRPC("SetGlobalKey", name); return; }
        if (Started && m_startedAsServer) RPC_SetGlobalKey(ZDOMan.GetSessionID(), name);
    }

    public void RemoveGlobalKey(GlobalKeys key) => RemoveGlobalKey(key.ToString().ToLower());
    /// <summary>Removes the key by name, whatever its value; routed like <see cref="SetGlobalKey(string)"/>.</summary>
    public void RemoveGlobalKey(string name)
    {
        if (!ZNet.instance.IsServer()) { ZRoutedRpc.instance.InvokeRoutedRPC("RemoveGlobalKey", name); return; }
        if (Started && m_startedAsServer) RPC_RemoveGlobalKey(ZDOMan.GetSessionID(), name);
    }

    public bool GetGlobalKey(GlobalKeys key) => m_globalKeysEnums.Contains(key);
    public bool GetGlobalKey(GlobalKeys key, out string value) => m_globalKeysValues.TryGetValue(key.ToString().ToLower(), out value!);
    /// <summary>The key's value read as an invariant-culture float; false (and 0) when it is not set or not a number.</summary>
    public bool GetGlobalKey(GlobalKeys key, out float value)
    {
        if (m_globalKeysValues.TryGetValue(key.ToString().ToLower(), out var text) && float.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            return true;
        value = 0f;
        return false;
    }
    /// <summary>Whether a key of this name is set, any case, any value. A "name value" string is not a name and finds nothing.</summary>
    public bool GetGlobalKey(string name) => GetGlobalKey(name, out _);
    public bool GetGlobalKey(string name, out string value) => m_globalKeysValues.TryGetValue(name.ToLower(), out value!);
    /// <summary>Whether this exact line ("name" or "name value", lower case) is set.</summary>
    public bool GetGlobalKeyExact(string fullLine) => m_globalKeys.Contains(fullLine);
    public List<string> GetGlobalKeys() => new(m_globalKeys);

    /// <summary>Splits a key at its first space into a lower-case name and the rest as the value, and names the <see cref="GlobalKeys"/> member it parses as (any case), else NonServerOption.</summary>
    public static string GetKeyValue(string key, out string value, out GlobalKeys gk)
    {
        int space = key.IndexOf(' ');
        value = "";
        string name;
        if (space > 0)
        {
            value = key.Substring(space + 1);
            name = key.Substring(0, space).ToLower();
        }
        else
        {
            name = key.ToLower();
        }
        if (!System.Enum.TryParse<GlobalKeys>(name, true, out gk)) gk = GlobalKeys.NonServerOption;
        return name;
    }

    /// <summary>
    /// Adds a key here, with no call to the server, as the game's private GlobalKeyAdd (public here for mods built against
    /// publicized assemblies). The whole line is lower-cased; a key of the same name is replaced. A world modifier also
    /// goes into <see cref="WorldStartingGlobalKeys"/> when a world is loaded and <paramref name="canSaveToServerOptionKeys"/>.
    /// </summary>
    public void GlobalKeyAdd(string keyStr, bool canSaveToServerOptionKeys = true)
    {
        string name = GetKeyValue(keyStr.ToLower(), out string value, out GlobalKeys gk);
        var world = canSaveToServerOptionKeys && gk < GlobalKeys.NonServerOption ? WorldStartingGlobalKeys : null;
        if (m_globalKeysValues.TryGetValue(name, out var oldValue))
        {
            string oldLine = (name + " " + oldValue).TrimEnd();
            m_globalKeys.Remove(oldLine);
            world?.Remove(oldLine);
        }
        string line = (name + " " + value).TrimEnd();
        m_globalKeys.Add(line);
        m_globalKeysValues[name] = value;
        if (gk != GlobalKeys.NonServerOption) m_globalKeysEnums.Add(gk);
        world?.Add(keyStr.ToLower());
    }

    /// <summary>Removes the key of this name here, whatever its value, as the game's private GlobalKeyRemove. False when it was not set.</summary>
    public bool GlobalKeyRemove(string keyStr, bool canSaveToServerOptionKeys = true)
    {
        string name = GetKeyValue(keyStr, out _, out GlobalKeys gk);
        if (!m_globalKeysValues.TryGetValue(name, out var value)) return false;
        string line = (name + " " + value).TrimEnd();
        if (canSaveToServerOptionKeys && gk < GlobalKeys.NonServerOption) WorldStartingGlobalKeys?.Remove(line);
        m_globalKeys.Remove(line);
        m_globalKeysValues.Remove(name);
        if (gk != GlobalKeys.NonServerOption) m_globalKeysEnums.Remove(gk);
        return true;
    }

    /// <summary>Sends the whole list to the peer (<c>ZRoutedRpc.Everybody</c> for all) as the GlobalKeys routed call. Private in the game.</summary>
    public void SendGlobalKeys(long peer) => ZRoutedRpc.instance.InvokeRoutedRPC(peer, "GlobalKeys", new List<string>(m_globalKeys));

    // The server's handlers: a change that changes something is broadcast to everybody. A set is skipped only when the
    // exact string (before lower-casing) is already a line, so a mixed-case name is applied and broadcast again.
    private void RPC_SetGlobalKey(long sender, string name)
    {
        if (m_globalKeys.Contains(name)) return;
        GlobalKeyAdd(name);
        SendGlobalKeys(ZRoutedRpc.Everybody);
    }

    private void RPC_RemoveGlobalKey(long sender, string name)
    {
        if (GlobalKeyRemove(name)) SendGlobalKeys(ZRoutedRpc.Everybody);
    }

    // A client's handler: the broadcast replaces the whole list, so a key only this client added is gone.
    private void RPC_GlobalKeys(long sender, List<string> keys)
    {
        ClearGlobalKeys();
        foreach (string key in keys) GlobalKeyAdd(key);
    }

    private void ClearGlobalKeys()
    {
        m_globalKeys.Clear();
        m_globalKeysEnums.Clear();
        m_globalKeysValues.Clear();
    }

    /// <summary>The lines the world's zone-system save keeps: every key except the world modifiers, which the world keeps in its starting keys.</summary>
    [TestOnly] public List<string> SaveGlobalKeys()
    {
        var kept = new HashSet<string>(m_globalKeys);
        kept.RemoveWhere(line => { GetKeyValue(line, out _, out var gk); return gk < GlobalKeys.NonServerOption; });
        return new List<string>(kept);
    }

    /// <summary>As the game's load of the zone-system save: the keys are cleared, then each saved line is added.</summary>
    [TestOnly] public void LoadGlobalKeys(IEnumerable<string> saved)
    {
        ClearGlobalKeys();
        foreach (string key in saved) GlobalKeyAdd(key);
    }

    /// <summary>
    /// As the game does when the world is set up: every world modifier is removed from the keys, then each of the
    /// world's starting keys is added (without writing them back), and with <paramref name="send"/> the list is broadcast.
    /// </summary>
    public void SetStartingGlobalKeys(bool send = true)
    {
        for (int i = 0; i < (int)GlobalKeys.NonServerOption; i++) GlobalKeyRemove(((GlobalKeys)i).ToString(), canSaveToServerOptionKeys: false);
        foreach (string key in new List<string>(WorldStartingGlobalKeys ?? new List<string>())) GlobalKeyAdd(key, canSaveToServerOptionKeys: false);
        if (send) SendGlobalKeys(ZRoutedRpc.Everybody);
    }

    /// <summary>As the resetkeys command: only the world's starting keys remain, broadcast to everybody.</summary>
    public void ResetGlobalKeys()
    {
        ClearGlobalKeys();
        SetStartingGlobalKeys(send: false);
        SendGlobalKeys(ZRoutedRpc.Everybody);
    }
}
