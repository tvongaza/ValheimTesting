// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// ZDO values, and what a world save and reload keep of them. As in Valheim 1.0.16: every value is keyed by the
// int hash of its name (string.GetStableHashCode), so Set("name", v) and Set("name".GetStableHashCode(), v) are the
// same key; each type has its own table (bools are ints). A save writes only persistent ZDOs and leaves out every
// value under a session-only hash (the game's list, plus any registered with AddSessionHash). Loading a 1.0 save
// strips nothing else; only the first load of a world an older game saved removes legacy keys and empty values.
using System;
using System.Collections.Generic;
using UnityEngine;
using Valheim.Testing.Doubles;

public partial class ZDO
{
    private readonly Dictionary<int, float> m_floats = new();
    private readonly Dictionary<int, Vector3> m_vec3s = new();
    private readonly Dictionary<int, Quaternion> m_quats = new();
    private readonly Dictionary<int, int> m_ints = new();
    private readonly Dictionary<int, long> m_longs = new();
    private readonly Dictionary<int, string> m_strings = new();
    private readonly Dictionary<int, byte[]> m_byteArrays = new();

    private static T ValueOr<T>(Dictionary<int, T> values, int hash, T defaultValue) => values.TryGetValue(hash, out var v) ? v : defaultValue;

    public void Set(string name, float value) => Set(name.GetStableHashCode(), value);
    public void Set(int hash, float value) => m_floats[hash] = value;
    public float GetFloat(string name, float defaultValue = 0f) => GetFloat(name.GetStableHashCode(), defaultValue);
    public float GetFloat(int hash, float defaultValue = 0f) => ValueOr(m_floats, hash, defaultValue);
    public bool GetFloat(string name, out float value) => GetFloat(name.GetStableHashCode(), out value);
    public bool GetFloat(int hash, out float value) => m_floats.TryGetValue(hash, out value);
    public bool RemoveFloat(string name) => RemoveFloat(name.GetStableHashCode());
    public bool RemoveFloat(int hash) => m_floats.Remove(hash);

    public void Set(string name, Vector3 value) => Set(name.GetStableHashCode(), value);
    public void Set(int hash, Vector3 value) => m_vec3s[hash] = value;
    public Vector3 GetVec3(string name, Vector3 defaultValue) => GetVec3(name.GetStableHashCode(), defaultValue);
    public Vector3 GetVec3(int hash, Vector3 defaultValue) => ValueOr(m_vec3s, hash, defaultValue);
    public bool GetVec3(string name, out Vector3 value) => GetVec3(name.GetStableHashCode(), out value);
    public bool GetVec3(int hash, out Vector3 value) => m_vec3s.TryGetValue(hash, out value);
    public bool RemoveVec3(string name) => RemoveVec3(name.GetStableHashCode());
    public bool RemoveVec3(int hash) => m_vec3s.Remove(hash);

    public void Set(string name, Quaternion value) => Set(name.GetStableHashCode(), value);
    public void Set(int hash, Quaternion value) => m_quats[hash] = value;
    public Quaternion GetQuaternion(string name, Quaternion defaultValue) => GetQuaternion(name.GetStableHashCode(), defaultValue);
    public Quaternion GetQuaternion(int hash, Quaternion defaultValue) => ValueOr(m_quats, hash, defaultValue);
    public bool GetQuaternion(string name, out Quaternion value) => GetQuaternion(name.GetStableHashCode(), out value);
    public bool GetQuaternion(int hash, out Quaternion value) => m_quats.TryGetValue(hash, out value);
    public bool RemoveQuaternion(string name) => RemoveQuaternion(name.GetStableHashCode());
    public bool RemoveQuaternion(int hash) => m_quats.Remove(hash);

    public void Set(string name, int value) => Set(name.GetStableHashCode(), value);
    /// <summary>As the game's: <paramref name="okForNotOwner"/> changes nothing.</summary>
    public void Set(int hash, int value, bool okForNotOwner = false) => m_ints[hash] = value;
    public int GetInt(string name, int defaultValue = 0) => GetInt(name.GetStableHashCode(), defaultValue);
    public int GetInt(int hash, int defaultValue = 0) => ValueOr(m_ints, hash, defaultValue);
    public bool GetInt(string name, out int value) => GetInt(name.GetStableHashCode(), out value);
    public bool GetInt(int hash, out int value) => m_ints.TryGetValue(hash, out value);
    public bool RemoveInt(string name) => RemoveInt(name.GetStableHashCode());
    public bool RemoveInt(int hash) => m_ints.Remove(hash);

    // A bool is an int (1 or 0) under the same hash, as in the game.
    public void Set(string name, bool value) => Set(name.GetStableHashCode(), value);
    public void Set(int hash, bool value) => Set(hash, value ? 1 : 0);
    public bool GetBool(string name, bool defaultValue = false) => GetBool(name.GetStableHashCode(), defaultValue);
    public bool GetBool(int hash, bool defaultValue = false) => GetInt(hash, defaultValue ? 1 : 0) != 0;
    public bool GetBool(string name, out bool value) => GetBool(name.GetStableHashCode(), out value);
    public bool GetBool(int hash, out bool value) { bool found = m_ints.TryGetValue(hash, out int v); value = v != 0; return found; }
    public bool RemoveBool(int hash) => m_ints.Remove(hash);

    public void Set(string name, long value) => Set(name.GetStableHashCode(), value);
    public void Set(int hash, long value) => m_longs[hash] = value;
    public long GetLong(string name, long defaultValue = 0L) => GetLong(name.GetStableHashCode(), defaultValue);
    public long GetLong(int hash, long defaultValue = 0L) => ValueOr(m_longs, hash, defaultValue);
    public bool RemoveLong(int hash) => m_longs.Remove(hash);

    public void Set(string name, string value) => Set(name.GetStableHashCode(), value);
    public void Set(int hash, string value) => m_strings[hash] = value;
    public string GetString(string name, string defaultValue = "") => GetString(name.GetStableHashCode(), defaultValue);
    public string GetString(int hash, string defaultValue = "") => ValueOr(m_strings, hash, defaultValue);
    public bool GetString(string name, out string value) => GetString(name.GetStableHashCode(), out value);
    public bool GetString(int hash, out string value) => m_strings.TryGetValue(hash, out value!);
    public bool RemoveString(int hash) => m_strings.Remove(hash);

    public void Set(string name, byte[] bytes) => Set(name.GetStableHashCode(), bytes);
    public void Set(int hash, byte[] bytes) => m_byteArrays[hash] = bytes;
    public byte[]? GetByteArray(string name, byte[]? defaultValue = null) => GetByteArray(name.GetStableHashCode(), defaultValue);
    public byte[]? GetByteArray(int hash, byte[]? defaultValue = null) => m_byteArrays.TryGetValue(hash, out var v) ? v : defaultValue;
    public bool GetByteArray(string name, out byte[] value) => GetByteArray(name.GetStableHashCode(), out value);
    public bool GetByteArray(int hash, out byte[] value) => m_byteArrays.TryGetValue(hash, out value!);
    public bool RemoveByteArray(int hash) => m_byteArrays.Remove(hash);

    /// <summary>Removes the hash from the first table that has it, in the game's order (float, int, long, Vector3, Quaternion, string, byte array).</summary>
    public bool Remove(int hash) =>
        m_floats.Remove(hash) || m_ints.Remove(hash) || m_longs.Remove(hash) || m_vec3s.Remove(hash) || m_quats.Remove(hash) || m_strings.Remove(hash) || m_byteArrays.Remove(hash);

    /// <summary>
    /// As the game's: the hash's values, on every ZDO, are not saved from now on. The registration belongs to the
    /// world (<see cref="ZDOMan.SessionOnlyHashes"/>) and a reload forgets it. The game's animator sync registers each
    /// animator parameter it writes (at Animator.StringToHash(name) + 438569).
    /// </summary>
    public void AddSessionHash(int hash) =>
        (ZDOMan.instance ?? throw new InvalidOperationException("AddSessionHash needs a ZDOMan: session-only hashes belong to the world.")).SessionOnlyHashes.Add(hash);

    /// <summary>Which world file a reload reads: one this game version saved, or one an older game saved and 1.0.16 upgrades on first load.</summary>
    [TestOnly] public enum SavedWorld
    {
        /// <summary>A world saved by Valheim 1.0 (chunked save). Loading strips nothing.</summary>
        Current,
        /// <summary>A world saved by an older game in the format before chunked saves. The first load removes legacy keys.</summary>
        BeforeChunkedSave,
        /// <summary>A world saved before the game's new ZDO save format. The first load also drops empty strings, empty byte arrays and identity quaternions, and converts old spawn keys.</summary>
        BeforeNewSaveFormat,
    }

    /// <summary>
    /// What this ZDO is after the world is saved and loaded again, as Valheim 1.0.16 does it: values under a
    /// session-only hash are not saved, the ZDO has no owner, and it gets a new id (a stored ZDOID no longer finds it).
    /// With an older <paramref name="savedBy"/>, the ZDO's values stand for what that old save held and the game's
    /// first-load upgrade is applied instead of the session filter. Throws for a ZDO that is not persistent: the game
    /// does not save it, so after a restart it does not exist. <see cref="ZDOMan.RoundTripThroughSave"/> does a whole world.
    /// </summary>
    [TestOnly] public void RoundTripThroughSave(SavedWorld savedBy = SavedWorld.Current)
    {
        if (!Persistent)
            throw new InvalidOperationException("A non-persistent ZDO is not saved; after a reload it does not exist.");
        ReloadFromSave(ZDOMan.instance?.SessionOnlyHashes ?? ZDOMan.DefaultSessionOnlyHashes(), savedBy);
    }

    internal void ReloadFromSave(HashSet<int> sessionOnly, SavedWorld savedBy)
    {
        if (savedBy == SavedWorld.Current)
        {
            foreach (int hash in sessionOnly) RemoveFromEveryTable(hash);
        }
        else
        {
            if (savedBy == SavedWorld.BeforeNewSaveFormat)
                StripOldFormatValues();
            RemoveObsoleteValues();
        }
        m_owner = 0;
        m_uid = new ZDOID(0L, s_nextId++);
    }

    // The old-format reader's per-value rules: empty strings and byte arrays and identity rotations are not read,
    // and the old spawn point/time keys move to their current names.
    private void StripOldFormatValues()
    {
        RemoveWhere(m_strings, value => string.IsNullOrEmpty(value));
        RemoveWhere(m_byteArrays, value => value.Length == 0);
        RemoveWhere(m_quats, value => IsIdentity(value));
        if (m_vec3s.TryGetValue(UpgradeKey.SpawnPointOld, out var point)) { m_vec3s.Remove(UpgradeKey.SpawnPointOld); m_vec3s[UpgradeKey.SpawnPoint] = point; }
        foreach (int old in new[] { UpgradeKey.SpawnTimeOld, UpgradeKey.SpawnTimeOld2 })
            if (m_longs.TryGetValue(old, out long time)) { m_longs.Remove(old); m_longs[UpgradeKey.SpawnTime] = time; }
    }

    // The upgrade's obsolete-data pass (any world saved before chunked saves). Not modelled: its unnamed legacy long
    // hashes and animator parameters, and the other conversions the upgrade makes (portals, spawners, containers, ...).
    private void RemoveObsoleteValues()
    {
        foreach (int hash in new[] { UpgradeKey.Support, UpgradeKey.Noise }) m_floats.Remove(hash);
        foreach (int hash in new[] { UpgradeKey.Vel, UpgradeKey.LookTarget, UpgradeKey.BodyVelocity, UpgradeKey.BodyVel, UpgradeKey.BodyAVel }) m_vec3s.Remove(hash);
        m_quats.Remove(UpgradeKey.TiltRot);
        m_longs.Remove(UpgradeKey.SessionCatchIdUser); m_longs.Remove(UpgradeKey.SessionCatchIdId);
        for (int i = 0; i <= 5; i++) m_strings.Remove((i + "_crafterName").GetStableHashCode());
        m_byteArrays.Remove(UpgradeKey.Health);
        foreach (int hash in LegacyKeys) RemoveFromEveryTable(hash);
    }

    private void RemoveFromEveryTable(int hash)
    {
        m_floats.Remove(hash); m_vec3s.Remove(hash); m_quats.Remove(hash); m_ints.Remove(hash);
        m_longs.Remove(hash); m_strings.Remove(hash); m_byteArrays.Remove(hash);
    }

    private static void RemoveWhere<T>(Dictionary<int, T> values, Func<T, bool> drop)
    {
        var gone = new List<int>();
        foreach (var pair in values) if (drop(pair.Value)) gone.Add(pair.Key);
        foreach (int hash in gone) values.Remove(hash);
    }

    // The Quaternion double holds Euler angles; whole turns on every axis are the identity.
    private static bool IsIdentity(Quaternion q) => q.EulerX % 360f == 0f && q.EulerY % 360f == 0f && q.EulerZ % 360f == 0f;

    // Keys the upgrade removes from every value type.
    private static readonly HashSet<int> LegacyKeys = BuildLegacyKeys();
    private static HashSet<int> BuildLegacyKeys()
    {
        var keys = new HashSet<int>();
        for (int i = 0; i <= 10; i++) keys.Add(("burnt" + i).GetStableHashCode());
        for (int i = 0; i < 256; i++) keys.Add(("room" + i + "_seed").GetStableHashCode());
        foreach (string name in new[] { "autoDespawn", "catchID", "cut_time", "generated", "inBase", "LookDir", "patrolSpawnPoint", "RideSpeed", "targetHear", "targetSee" })
            keys.Add(name.GetStableHashCode());
        return keys;
    }

    private static class UpgradeKey
    {
        public static readonly int Support = "support".GetStableHashCode(), Noise = "noise".GetStableHashCode(), Health = "health".GetStableHashCode();
        public static readonly int Vel = "vel".GetStableHashCode(), LookTarget = "LookTarget".GetStableHashCode(), BodyVelocity = "BodyVelocity".GetStableHashCode();
        public static readonly int BodyVel = "body_vel".GetStableHashCode(), BodyAVel = "body_avel".GetStableHashCode(), TiltRot = "tiltrot".GetStableHashCode();
        public static readonly int SessionCatchIdUser = "CatchID_u".GetStableHashCode(), SessionCatchIdId = "CatchID_i".GetStableHashCode();
        public static readonly int SpawnPoint = "spawnpoint".GetStableHashCode(), SpawnPointOld = "SpawnPoint".GetStableHashCode();
        public static readonly int SpawnTime = "spawntime".GetStableHashCode(), SpawnTimeOld = "SpawnTime".GetStableHashCode(), SpawnTimeOld2 = "spawn_time".GetStableHashCode();
    }
}

public partial class ZDOMan
{
    /// <summary>
    /// Hashes whose values a save leaves out: the game's own session-only keys (physics and AI state such as
    /// <c>support</c>, <c>vel</c> and <c>InUse</c>) plus any registered with <see cref="ZDO.AddSessionHash"/>.
    /// A new world, or a reload, starts again from the game's list.
    /// </summary>
    [TestOnly] public HashSet<int> SessionOnlyHashes { get; private set; } = DefaultSessionOnlyHashes();

    /// <summary>The game's session-only keys (ZDOVars.s_sessionHashes in 1.0.16).</summary>
    [TestOnly] public static HashSet<int> DefaultSessionOnlyHashes()
    {
        var hashes = new HashSet<int>();
        foreach (string name in new[] { "CatchID_u", "CatchID_i", "alert", "animation_speed", "body_avel", "body_vel", "BodyVelocity", "haveTarget", "InUse", "landed", "LookTarget", "noise", "support", "tiltrot", "vel", "velRel" })
            hashes.Add(name.GetStableHashCode());
        return hashes;
    }

    /// <summary>
    /// The world after a save and a restart, as Valheim 1.0.16 does it: only persistent ZDOs come back (with their
    /// session-only values left out, no owner and new ids), the destroy queue is gone (a ZDO still waiting in it was
    /// saved, so it comes back), and hashes registered with <see cref="ZDO.AddSessionHash"/> are forgotten. With an
    /// older <paramref name="savedBy"/>, see <see cref="ZDO.RoundTripThroughSave"/>. Returns how many ZDOs came back.
    /// </summary>
    [TestOnly] public int RoundTripThroughSave(ZDO.SavedWorld savedBy = ZDO.SavedWorld.Current)
    {
        var sessionOnly = SessionOnlyHashes;
        DestroyQueue.Clear();
        Zdos.RemoveAll(zdo => !zdo.Persistent);
        foreach (var zdo in Zdos) zdo.ReloadFromSave(sessionOnly, savedBy);
        SessionOnlyHashes = DefaultSessionOnlyHashes();
        return Zdos.Count;
    }
}
