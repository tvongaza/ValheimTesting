using System;
using System.Collections.Generic;
using System.Linq;
using Valheim.Testing.Doubles;
using Xunit;

// The HitData double against the game's 1.0.16 package layout. Expected bytes are written out by hand from the game's
// HitData serialization and the package encoding (little-endian numbers, a char as UTF-8), not produced by the double.
public sealed class HitDataTests
{
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes);
    private static string Written(HitData hit) { var pkg = new ZPackage(); hit.Serialize(ref pkg); return Hex(pkg.GetArray()); }
    private static object[] Fields(HitData h) => new object[]
    {
        h.m_damage, h.m_dodgeable, h.m_blockable, h.m_ranged, h.m_ignorePVP, h.m_toolTier, h.m_pushForce, h.m_backstabBonus,
        h.m_staggerMultiplier, h.m_point, h.m_dir, h.m_statusEffectHash, h.m_attacker, h.m_skill, h.m_skillRaiseAmount, h.m_skillLevel,
        h.m_itemLevel, h.m_itemWorldLevel, h.m_hitType, h.m_healthReturn, h.m_eitrAdd, h.m_radius, h.m_weakSpot, h.m_variant,
    };

    /// <summary>
    /// Blunt, fire and non-player damage, a push force, a stagger multiplier, an attacker and a skill raise amount differ
    /// from their defaults; damage, slash (-0 equals the default 0), the other damage types and the backstab bonus do not.
    /// </summary>
    private static HitData Known() => new()
    {
        m_damage = new HitData.DamageTypes { m_blunt = 10f, m_slash = -0f, m_fire = 2f, m_nonPlayer = 4f },
        m_toolTier = 2, m_pushForce = 3f, m_staggerMultiplier = 2f,
        m_dodgeable = true, m_blockable = true, m_ignorePVP = true,
        m_point = new UnityEngine.Vector3(1, 2, 3), m_dir = new UnityEngine.Vector3(0, 0, 1), m_statusEffectHash = 0x01020304,
        m_attacker = new ZDOID(2, 3), m_skill = Skills.SkillType.Clubs, m_skillRaiseAmount = 0.5f, m_weakSpot = 2, m_skillLevel = 1.5f,
        m_itemLevel = 3, m_itemWorldLevel = 1, m_hitType = HitData.HitType.PlayerHit, m_healthReturn = 1f, m_radius = 0.25f, m_variant = 1,
    };

    // The layout of Known(), field by field, as the game writes it.
    private const string KnownBytes =
        "42-E8-01-00"                                    // flags: blunt 0x2 | fire 0x40 | push 0x800 | stagger 0x2000 | attacker 0x4000 | skill raise 0x8000 | non-player 0x10000
        + "-00-00-20-41" + "-00-00-00-40" + "-00-00-80-40" // the flagged damage in order: blunt 10, fire 2, then non-player 4
        + "-02-00"                                       // tool tier 2 (short)
        + "-00-00-40-40" + "-00-00-00-40"                // push force 3, stagger multiplier 2 (backstab bonus 1 is the default: absent)
        + "-0B"                                          // dodgeable 1 | blockable 2 | ignore PvP 8
        + "-00-00-80-3F-00-00-00-40-00-00-40-40"         // point (1, 2, 3)
        + "-00-00-00-00-00-00-00-00-00-00-80-3F"         // direction (0, 0, 1)
        + "-04-03-02-01"                                 // status effect hash 0x01020304
        + "-02-00-00-00-00-00-00-00-03-00-00-00"         // attacker: user id 2 (long), id 3 (uint)
        + "-03-00"                                       // skill Clubs = 3 (short)
        + "-00-00-00-3F"                                 // skill raise amount 0.5
        + "-02"                                          // weak spot 2 as a UTF-8 char
        + "-00-00-C0-3F"                                 // skill level 1.5
        + "-03-00" + "-01" + "-02"                       // item level 3 (short), world level 1 (byte), hit type PlayerHit = 2 (byte)
        + "-00-00-80-3F" + "-00-00-00-00" + "-00-00-80-3E" // health return 1, eitr 0, radius 0.25
        + "-01-00";                                      // variant 1 (short)

    [Fact] public void AHitIsWrittenWithTheGamesLayout()
    {
        Assert.Equal(KnownBytes, Written(Known()));
        // Only the always-written values of a default hit: 62 bytes. Its weak spot -1 is the char U+FFFF, three bytes in UTF-8.
        Assert.Equal("00-00-00-00" + "-00-00" + "-00" + string.Concat(Enumerable.Repeat("-00", 24)) + "-00-00-00-00" + "-00-00"
            + "-EF-BF-BF" + "-00-00-00-00" + "-00-00" + "-00" + "-00" + string.Concat(Enumerable.Repeat("-00", 12)) + "-00-00",
            Written(new HitData()));
        Assert.Equal("01-00-00-00-00-00-A0-40", Written(new HitData(5f)).Substring(0, 23)); // the damage flag, then 5
    }

    [Fact] public void AHitRoundTripsThroughAPackage()
    {
        var hit = Known();
        hit.m_damage = new HitData.DamageTypes
        {
            m_damage = 1, m_blunt = 2, m_slash = 3, m_pierce = 4, m_chop = 5, m_pickaxe = 6, m_fire = 7, m_frost = 8, m_lightning = 9,
            m_poison = 10, m_spirit = 11, m_nonPlayer = 12,
        };
        hit.m_backstabBonus = 3f; hit.m_ranged = true; hit.m_eitrAdd = 2f; hit.m_weakSpot = 200; hit.m_hitType = HitData.HitType.DrawBridge;
        var pkg = new ZPackage(); hit.Serialize(ref pkg); pkg.Write(7); // something after it, to check the read ends where the hit ends
        pkg.SetPos(0);
        var read = new HitData(); read.Deserialize(ref pkg);
        Assert.Equal(Fields(hit), Fields(read)); Assert.Equal(7, pkg.ReadInt());

        // A value left out of the package reads as its default, whatever the hit held before.
        var reused = hit.Clone();
        pkg = new ZPackage(); new HitData().Serialize(ref pkg); pkg.SetPos(0);
        reused.Deserialize(ref pkg);
        Assert.Equal(Fields(new HitData()), Fields(reused));
        Assert.Equal((1f, 1f, 1f, (short)-1), (reused.m_backstabBonus, reused.m_staggerMultiplier, reused.m_skillRaiseAmount, reused.m_weakSpot));
        Assert.True(reused.m_attacker.IsNone());
    }

    [Fact] public void ARoutedCallCarriesAHitAsTheGameDoes()
    {
        using (new ValheimWorldScope().WithNetwork(server: false).WithZdos())
        {
            ZNet.instance.Peers.Add(5, new ZNetPeer());
            ZRoutedRpc.instance.InvokeRoutedRPC(5, "Mod_Hit", Known(), 7);
            Assert.Equal(KnownBytes + "-07-00-00-00", Hex(Assert.Single(ZRoutedRpc.instance.Sent).Data.m_parameters.GetArray()));
        }

        using (new ValheimWorldScope().WithNetwork(server: true).WithZdos())
        {
            var sent = Known(); var got = new List<HitData>();
            ZRoutedRpc.instance.Register<HitData, int>("Mod_Hit", (s, hit, n) => got.Add(hit));
            ZRoutedRpc.instance.Register<int>("Mod_Int", (s, n) => { });
            ZRoutedRpc.instance.Deliver(5, "Mod_Hit", sent, 7);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, "Mod_Hit", sent, 8);
            Assert.Equal(2, got.Count);
            Assert.All(got, hit => { Assert.NotSame(sent, hit); Assert.Equal(Fields(sent), Fields(hit)); });
            Assert.Contains("argument 1 was sent as HitData but the handler reads Int32",
                Assert.Throws<InvalidOperationException>(() => ZRoutedRpc.instance.Deliver(5, "Mod_Int", sent)).Message);

            // A per-object damage call, as the game sends a hit to a piece or a character.
            var view = new ZNetView(ZDOMan.instance!.CreateNewZDO(UnityEngine.Vector3.zero, 1));
            var damage = new List<float>();
            view.Register<HitData>("RPC_Damage", (s, hit) => damage.Add(hit.m_damage.m_blunt));
            view.InvokeRPC(ZNetView.Everybody, "RPC_Damage", sent);
            Assert.Equal(new[] { 10f }, damage);
        }
    }
}
