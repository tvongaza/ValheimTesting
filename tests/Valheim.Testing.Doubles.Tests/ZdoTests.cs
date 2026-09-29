using System;
using Valheim.Testing.Doubles;
using Xunit;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;

// ZDO values keyed by hash, and what a world save and reload keep of them (Valheim 1.0.16).
public sealed class ZdoTests
{
    // Expected values come from a separate implementation of the game's algorithm (UTF-16 code units, stop at '\0').
    [Theory]
    [InlineData("", 371857150)]
    [InlineData("a", 372029373)]
    [InlineData("ab", 1093630535)]
    [InlineData("abc", 1099313834)]
    [InlineData("TCData", 1305470367)]
    [InlineData("creator", 881008290)]
    [InlineData("support", 642166391)]
    [InlineData("health", -883022132)]
    [InlineData("piece_workbench", -958010034)]
    [InlineData("Ägir", 1494795288)]
    [InlineData("\U0001F642", 1408593083)] // a surrogate pair hashes as two code units
    [InlineData("ab\0cd", 1093630535)]     // stops at the first '\0'
    [InlineData("\0x", 371857150)]
    public void TheStableHashIsTheGames(string name, int expected) => Assert.Equal(expected, name.GetStableHashCode());

    [Fact] public void ZdoVarsAreTheHashesOfTheirNames()
    {
        Assert.Equal(1305470367, ZDOVars.s_TCData);
        Assert.Equal(881008290, ZDOVars.s_creator);
    }

    [Fact] public void EveryValueTypeReadsBackByNameOrByHash()
    {
        var zdo = new ZDO(Vector3.zero, 1);
        int H(string name) => name.GetStableHashCode();
        zdo.Set("f", 1.5f); zdo.Set(H("f2"), 2.5f);
        Assert.Equal(1.5f, zdo.GetFloat(H("f"))); Assert.Equal(2.5f, zdo.GetFloat("f2"));
        Assert.True(zdo.GetFloat(H("f"), out float f)); Assert.Equal(1.5f, f);
        zdo.Set("v", new Vector3(1, 2, 3)); zdo.Set(H("v2"), new Vector3(4, 5, 6));
        Assert.Equal(new Vector3(1, 2, 3), zdo.GetVec3(H("v"), Vector3.zero)); Assert.Equal(new Vector3(4, 5, 6), zdo.GetVec3("v2", Vector3.zero));
        Assert.True(zdo.GetVec3("v", out var v)); Assert.Equal(3f, v.z);
        zdo.Set("q", Quaternion.Euler(0, 90, 0)); zdo.Set(H("q2"), Quaternion.Euler(10, 0, 0));
        Assert.Equal(90f, zdo.GetQuaternion(H("q"), default).EulerY); Assert.Equal(10f, zdo.GetQuaternion("q2", default).EulerX);
        Assert.True(zdo.GetQuaternion("q", out var q)); Assert.Equal(90f, q.EulerY);
        zdo.Set("i", 7); zdo.Set(H("i2"), 8);
        Assert.Equal(7, zdo.GetInt(H("i"))); Assert.Equal(8, zdo.GetInt("i2"));
        Assert.True(zdo.GetInt(H("i"), out int i)); Assert.Equal(7, i);
        zdo.Set("b", true); zdo.Set(H("b2"), true);
        Assert.True(zdo.GetBool(H("b"))); Assert.True(zdo.GetBool("b2"));
        Assert.True(zdo.GetBool("b", out bool b)); Assert.True(b);
        zdo.Set("l", 1L << 40); zdo.Set(H("l2"), 5L);
        Assert.Equal(1L << 40, zdo.GetLong(H("l"))); Assert.Equal(5L, zdo.GetLong("l2"));
        zdo.Set("s", "text"); zdo.Set(H("s2"), "more");
        Assert.Equal("text", zdo.GetString(H("s"))); Assert.Equal("more", zdo.GetString("s2"));
        Assert.True(zdo.GetString(H("s"), out string s)); Assert.Equal("text", s);
        zdo.Set("a", new byte[] { 1 }); zdo.Set(H("a2"), new byte[] { 2 });
        Assert.Equal(new byte[] { 1 }, zdo.GetByteArray(H("a"))); Assert.Equal(new byte[] { 2 }, zdo.GetByteArray("a2"));
        Assert.True(zdo.GetByteArray("a", out byte[] a)); Assert.Equal(new byte[] { 1 }, a);
    }

    [Fact] public void EachTypeHasItsOwnTableAndABoolIsAnInt()
    {
        var zdo = new ZDO(Vector3.zero, 1);
        zdo.Set("key", 3);
        Assert.Equal(0f, zdo.GetFloat("key")); Assert.False(zdo.GetFloat("key", out _)); Assert.Equal(0L, zdo.GetLong("key"));
        Assert.Equal("", zdo.GetString("key")); Assert.False(zdo.GetString("key", out _)); Assert.Null(zdo.GetByteArray("key"));
        Assert.True(zdo.GetBool("key"));
        zdo.Set("flag", false);
        Assert.Equal(0, zdo.GetInt("flag", -1)); Assert.False(zdo.GetBool("flag", true)); Assert.True(zdo.GetBool("flag", out _));
        Assert.True(zdo.GetBool("absent", true)); Assert.False(zdo.GetBool("absent", out _));
        zdo.Set("key", 2f);
        Assert.True(zdo.Remove("key".GetStableHashCode())); // the float goes first, as in the game
        Assert.Equal(0f, zdo.GetFloat("key")); Assert.Equal(3, zdo.GetInt("key"));
        Assert.True(zdo.RemoveInt("key")); Assert.False(zdo.Remove("key".GetStableHashCode()));
    }

    // The issue's premise was that every load strips empty values. In 1.0.16 only the upgrade of an old-format world does.
    [Fact] public void A10SaveKeepsEmptyMarkersButDropsSessionOnlyValues()
    {
        using var world = new ValheimWorldScope().WithZdos();
        var zdo = ZDOMan.instance!.CreateNewZDO(new Vector3(5, 30, 5), 1);
        zdo.Persistent = true; zdo.SetOwner(ZDOMan.GetSessionID());
        var id = zdo.m_uid;
        zdo.Set("mymod_marker", ""); zdo.Set("mymod_bytes", Array.Empty<byte>()); zdo.Set("mymod_rot", Quaternion.Euler(0, 0, 0));
        zdo.Set("support", 750f); zdo.Set("vel", new Vector3(1, 0, 0)); zdo.Set("InUse", true);
        zdo.AddSessionHash("mymod_anim".GetStableHashCode()); zdo.Set("mymod_anim", 4);
        zdo.Set(ZDOVars.s_TCData, new byte[] { 9 });

        zdo.RoundTripThroughSave();

        Assert.True(zdo.GetString("mymod_marker", out string marker)); Assert.Equal("", marker);
        Assert.True(zdo.GetByteArray("mymod_bytes", out byte[] bytes)); Assert.Empty(bytes);
        Assert.True(zdo.GetQuaternion("mymod_rot", out _));
        Assert.False(zdo.GetFloat("support", out _)); Assert.False(zdo.GetVec3("vel", out _)); Assert.False(zdo.GetBool("InUse", out _));
        Assert.False(zdo.GetInt("mymod_anim", out _));
        Assert.Equal(new byte[] { 9 }, zdo.GetByteArray("TCData"));
        Assert.False(zdo.HasOwner()); Assert.NotEqual(id, zdo.m_uid); Assert.Null(ZDOMan.instance.GetZDO(id));
    }

    [Fact] public void OnlyPersistentZdosAreSaved()
    {
        using var world = new ValheimWorldScope().WithZdos();
        var zdos = ZDOMan.instance!;
        var kept = zdos.CreateNewZDO(new Vector3(1, 30, 1), 1); kept.Persistent = true;
        var queued = zdos.CreateNewZDO(new Vector3(2, 30, 2), 1); queued.Persistent = true; zdos.DestroyZDO(queued);
        var temporary = zdos.CreateNewZDO(new Vector3(3, 30, 3), 1);
        Assert.Throws<InvalidOperationException>(() => temporary.RoundTripThroughSave());

        Assert.Equal(2, zdos.RoundTripThroughSave());
        Assert.Equal(new[] { kept, queued }, zdos.Zdos); Assert.Empty(zdos.DestroyQueue); // saved before its destruction was processed
    }

    [Fact] public void AReloadForgetsRegisteredSessionHashes()
    {
        using var world = new ValheimWorldScope().WithZdos();
        var zdos = ZDOMan.instance!;
        var zdo = zdos.CreateNewZDO(new Vector3(1, 30, 1), 1); zdo.Persistent = true;
        int anim = "mymod_anim".GetStableHashCode();
        zdo.AddSessionHash(anim); zdo.Set(anim, 1);
        zdos.RoundTripThroughSave();
        Assert.False(zdo.GetInt(anim, out _)); Assert.DoesNotContain(anim, zdos.SessionOnlyHashes);
        zdo.Set(anim, 2); // written again but not registered again
        zdos.RoundTripThroughSave();
        Assert.Equal(2, zdo.GetInt(anim));
        Assert.Contains("support".GetStableHashCode(), zdos.SessionOnlyHashes);
        using (new ValheimWorldScope()) { ZDOMan.instance = null; Assert.Throws<InvalidOperationException>(() => zdo.AddSessionHash(anim)); }
    }

    [Fact] public void TheFirstLoadOfAnOldWorldStripsLegacyKeysAndOldFormatEmptyValues()
    {
        using var world = new ValheimWorldScope().WithZdos();
        ZDO Old()
        {
            var zdo = ZDOMan.instance!.CreateNewZDO(Vector3.zero, 1); zdo.Persistent = true;
            zdo.Set("mymod_marker", ""); zdo.Set("mymod_name", "kept"); zdo.Set("mymod_bytes", Array.Empty<byte>());
            zdo.Set("mymod_rot", Quaternion.Euler(0, 360, 0)); zdo.Set("mymod_turn", Quaternion.Euler(0, 90, 0));
            zdo.Set("support", 750f); zdo.Set("InUse", true); zdo.Set("burnt3", 1); zdo.Set("burnt3", 1f); zdo.Set("room7_seed", 5);
            zdo.Set("0_crafterName", "Someone"); zdo.Set("health", new byte[] { 1 }); zdo.Set("health", 100f); zdo.Set("SpawnTime", 123L);
            return zdo;
        }
        var preChunked = Old(); preChunked.RoundTripThroughSave(ZDO.SavedWorld.BeforeChunkedSave);
        Assert.True(preChunked.GetString("mymod_marker", out _)); Assert.True(preChunked.GetByteArray("mymod_bytes", out _)); Assert.True(preChunked.GetQuaternion("mymod_rot", out _));
        Assert.False(preChunked.GetFloat("support", out _)); Assert.True(preChunked.GetBool("InUse")); // not a legacy key; that save's own filter is not modelled
        Assert.False(preChunked.GetInt("burnt3", out _)); Assert.False(preChunked.GetFloat("burnt3", out _)); Assert.False(preChunked.GetInt("room7_seed", out _));
        Assert.False(preChunked.GetString("0_crafterName", out _)); Assert.Null(preChunked.GetByteArray("health")); Assert.Equal(100f, preChunked.GetFloat("health"));
        Assert.Equal(123L, preChunked.GetLong("SpawnTime"));

        var oldFormat = Old(); oldFormat.RoundTripThroughSave(ZDO.SavedWorld.BeforeNewSaveFormat);
        Assert.False(oldFormat.GetString("mymod_marker", out _)); Assert.Equal("kept", oldFormat.GetString("mymod_name"));
        Assert.False(oldFormat.GetByteArray("mymod_bytes", out _));
        Assert.False(oldFormat.GetQuaternion("mymod_rot", out _)); Assert.Equal(90f, oldFormat.GetQuaternion("mymod_turn", default).EulerY);
        Assert.Equal(0L, oldFormat.GetLong("SpawnTime")); Assert.Equal(123L, oldFormat.GetLong("spawntime"));
    }

    [Fact] public void AZoneCompilersDataSurvivesAReloadButItHasNoOwnerUntilClaimed()
    {
        using var world = new ValheimWorldScope().WithTerrain(new Valheim.Testing.PlaneTerrain(30f)).WithZdos();
        var comp = world.RegisterHeightmap(new Vector2s(0, 0)).m_terrainComp!;
        comp.Save();
        ZDOMan.instance!.RoundTripThroughSave();
        Assert.Equal(new byte[] { 1 }, comp.m_nview.GetZDO().GetByteArray("TCData"));
        comp.Save(); Assert.Equal(1, comp.SaveCount);
        comp.m_nview.ClaimOwnership(); comp.Save(); Assert.Equal(2, comp.SaveCount);
    }
}
