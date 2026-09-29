// Valheim.Testing.Doubles: compile-time stand-ins for the Unity, Valheim, BepInEx and Jotunn types a mod's
// pure-logic sources use, so those sources compile and run in an ordinary test project without the game.
// Source package: these files are compiled into the consuming test project. Every type is partial; add the
// members your mod needs in your own files. Behaviour mirrors the game where mod code depends on it.
#nullable enable
// ReSharper disable InconsistentNaming
// Valheim's HitData: the fields a hit carries and its package encoding, as the game (1.0.16) writes it into a
// routed RPC. Damage calculation (resistances, armour, blocking) is not doubled.

/// <summary>
/// A hit, with the game's 1.0.16 fields and defaults (backstab bonus, stagger multiplier and skill raise amount 1, no
/// attacker, weak spot -1). <see cref="Serialize"/> and <see cref="Deserialize"/> use the game's byte layout, so a
/// routed RPC carries a HitData argument as it does in the game. <c>m_hitCollider</c> is not written.
/// </summary>
public partial class HitData
{
    /// <summary>The damage of each type, in the order the game declares (and writes) them.</summary>
    public partial struct DamageTypes
    {
        public float m_damage;
        public float m_blunt;
        public float m_slash;
        public float m_pierce;
        public float m_chop;
        public float m_pickaxe;
        public float m_fire;
        public float m_frost;
        public float m_lightning;
        public float m_poison;
        public float m_spirit;
        public float m_nonPlayer;
    }

    /// <summary>What caused the hit. Numbered from 0 in this order and written as one byte, as in the game.</summary>
    public enum HitType : byte
    {
        Undefined, EnemyHit, PlayerHit, Fall, Drowning, Burning, Freezing, Poisoned, Water, Smoke, EdgeOfWorld, Impact, Cart,
        Tree, Self, Structural, Turret, Boat, Stalagtite, Catapult, CinderFire, AshlandsOcean, AshlandsLava, Incinerator, DrawBridge,
    }

    public DamageTypes m_damage;
    public bool m_dodgeable;
    public bool m_blockable;
    public bool m_ranged;
    public bool m_ignorePVP;
    public short m_toolTier;
    public float m_pushForce;
    public float m_backstabBonus = 1f;
    public float m_staggerMultiplier = 1f;
    public UnityEngine.Vector3 m_point = UnityEngine.Vector3.zero;
    public UnityEngine.Vector3 m_dir = UnityEngine.Vector3.zero;
    public int m_statusEffectHash;
    public ZDOID m_attacker = ZDOID.None;
    public Skills.SkillType m_skill;
    public float m_skillRaiseAmount = 1f;
    public float m_skillLevel;
    public short m_itemLevel;
    public byte m_itemWorldLevel;
    public HitType m_hitType;
    public float m_healthReturn;
    public float m_eitrAdd;
    public float m_radius;
    public short m_weakSpot = -1;
    public short m_variant;
    public UnityEngine.Collider? m_hitCollider;

    public HitData() { }
    public HitData(float damage) => m_damage.m_damage = damage;
    /// <summary>A shallow copy, as the game's.</summary>
    public HitData Clone() => (HitData)MemberwiseClone();

    // Bits of the leading flags word: which optional values follow. A value is left out when it equals its default
    // (float.Equals, so -0 counts as 0 and NaN is written).
    private const uint FlagDamage = 1u, FlagBlunt = 1u << 1, FlagSlash = 1u << 2, FlagPierce = 1u << 3, FlagChop = 1u << 4,
        FlagPickaxe = 1u << 5, FlagFire = 1u << 6, FlagFrost = 1u << 7, FlagLightning = 1u << 8, FlagPoison = 1u << 9,
        FlagSpirit = 1u << 10, FlagPushForce = 1u << 11, FlagBackstabBonus = 1u << 12, FlagStaggerMultiplier = 1u << 13,
        FlagAttacker = 1u << 14, FlagSkillRaiseAmount = 1u << 15, FlagNonPlayer = 1u << 16;

    /// <summary>
    /// Writes the hit as the game does in 1.0.16:
    /// <list type="number">
    /// <item>a uint of flags, one bit for each optional value that differs from its default;</item>
    /// <item>the flagged damage floats: damage, blunt, slash, pierce, chop, pickaxe, fire, frost, lightning, poison,
    /// spirit (bits 0 to 10), then non-player (bit 16);</item>
    /// <item>the tool tier (short), then the flagged push force (bit 11), backstab bonus (bit 12) and stagger multiplier
    /// (bit 13) floats;</item>
    /// <item>one byte of switches: dodgeable 1, blockable 2, ranged 4, ignore PvP 8;</item>
    /// <item>the point and the direction (three floats each), the status effect hash (int), the flagged attacker (bit 14,
    /// a ZDOID: long then uint), the skill (short) and the flagged skill raise amount (bit 15, float);</item>
    /// <item>the weak spot as a char (the package's UTF-8 char: the default -1 is U+FFFF, three bytes), the skill level
    /// (float), the item level (short), the item's world level and the hit type (one byte each), the health return, eitr
    /// and radius (floats) and the variant (short).</item>
    /// </list>
    /// </summary>
    public void Serialize(ref ZPackage pkg)
    {
        uint flags = Flag(m_damage.m_damage, 0f, FlagDamage) | Flag(m_damage.m_blunt, 0f, FlagBlunt) | Flag(m_damage.m_slash, 0f, FlagSlash)
            | Flag(m_damage.m_pierce, 0f, FlagPierce) | Flag(m_damage.m_chop, 0f, FlagChop) | Flag(m_damage.m_pickaxe, 0f, FlagPickaxe)
            | Flag(m_damage.m_fire, 0f, FlagFire) | Flag(m_damage.m_frost, 0f, FlagFrost) | Flag(m_damage.m_lightning, 0f, FlagLightning)
            | Flag(m_damage.m_poison, 0f, FlagPoison) | Flag(m_damage.m_spirit, 0f, FlagSpirit) | Flag(m_damage.m_nonPlayer, 0f, FlagNonPlayer)
            | Flag(m_pushForce, 0f, FlagPushForce) | Flag(m_backstabBonus, 1f, FlagBackstabBonus)
            | Flag(m_staggerMultiplier, 1f, FlagStaggerMultiplier) | Flag(m_skillRaiseAmount, 1f, FlagSkillRaiseAmount)
            | (m_attacker != ZDOID.None ? FlagAttacker : 0u);
        pkg.Write(flags);
        WriteIf(pkg, flags, FlagDamage, m_damage.m_damage);
        WriteIf(pkg, flags, FlagBlunt, m_damage.m_blunt);
        WriteIf(pkg, flags, FlagSlash, m_damage.m_slash);
        WriteIf(pkg, flags, FlagPierce, m_damage.m_pierce);
        WriteIf(pkg, flags, FlagChop, m_damage.m_chop);
        WriteIf(pkg, flags, FlagPickaxe, m_damage.m_pickaxe);
        WriteIf(pkg, flags, FlagFire, m_damage.m_fire);
        WriteIf(pkg, flags, FlagFrost, m_damage.m_frost);
        WriteIf(pkg, flags, FlagLightning, m_damage.m_lightning);
        WriteIf(pkg, flags, FlagPoison, m_damage.m_poison);
        WriteIf(pkg, flags, FlagSpirit, m_damage.m_spirit);
        WriteIf(pkg, flags, FlagNonPlayer, m_damage.m_nonPlayer);
        pkg.Write(m_toolTier);
        WriteIf(pkg, flags, FlagPushForce, m_pushForce);
        WriteIf(pkg, flags, FlagBackstabBonus, m_backstabBonus);
        WriteIf(pkg, flags, FlagStaggerMultiplier, m_staggerMultiplier);
        pkg.Write((byte)((m_dodgeable ? 1 : 0) | (m_blockable ? 2 : 0) | (m_ranged ? 4 : 0) | (m_ignorePVP ? 8 : 0)));
        pkg.Write(m_point);
        pkg.Write(m_dir);
        pkg.Write(m_statusEffectHash);
        if ((flags & FlagAttacker) != 0) pkg.Write(m_attacker);
        pkg.Write((short)m_skill);
        WriteIf(pkg, flags, FlagSkillRaiseAmount, m_skillRaiseAmount);
        pkg.Write(unchecked((char)m_weakSpot));
        pkg.Write(m_skillLevel);
        pkg.Write(m_itemLevel);
        pkg.Write(m_itemWorldLevel);
        pkg.Write((byte)m_hitType);
        pkg.Write(m_healthReturn);
        pkg.Write(m_eitrAdd);
        pkg.Write(m_radius);
        pkg.Write(m_variant);
    }

    /// <summary>
    /// Reads what <see cref="Serialize"/> wrote, overwriting every field but <c>m_hitCollider</c>; an optional value
    /// that is absent gets its default, as in the game.
    /// </summary>
    public void Deserialize(ref ZPackage pkg)
    {
        uint flags = pkg.ReadUInt();
        m_damage.m_damage = ReadIf(pkg, flags, FlagDamage, 0f);
        m_damage.m_blunt = ReadIf(pkg, flags, FlagBlunt, 0f);
        m_damage.m_slash = ReadIf(pkg, flags, FlagSlash, 0f);
        m_damage.m_pierce = ReadIf(pkg, flags, FlagPierce, 0f);
        m_damage.m_chop = ReadIf(pkg, flags, FlagChop, 0f);
        m_damage.m_pickaxe = ReadIf(pkg, flags, FlagPickaxe, 0f);
        m_damage.m_fire = ReadIf(pkg, flags, FlagFire, 0f);
        m_damage.m_frost = ReadIf(pkg, flags, FlagFrost, 0f);
        m_damage.m_lightning = ReadIf(pkg, flags, FlagLightning, 0f);
        m_damage.m_poison = ReadIf(pkg, flags, FlagPoison, 0f);
        m_damage.m_spirit = ReadIf(pkg, flags, FlagSpirit, 0f);
        m_damage.m_nonPlayer = ReadIf(pkg, flags, FlagNonPlayer, 0f);
        m_toolTier = pkg.ReadShort();
        m_pushForce = ReadIf(pkg, flags, FlagPushForce, 0f);
        m_backstabBonus = ReadIf(pkg, flags, FlagBackstabBonus, 1f);
        m_staggerMultiplier = ReadIf(pkg, flags, FlagStaggerMultiplier, 1f);
        byte switches = pkg.ReadByte();
        m_dodgeable = (switches & 1) != 0;
        m_blockable = (switches & 2) != 0;
        m_ranged = (switches & 4) != 0;
        m_ignorePVP = (switches & 8) != 0;
        m_point = pkg.ReadVector3();
        m_dir = pkg.ReadVector3();
        m_statusEffectHash = pkg.ReadInt();
        m_attacker = (flags & FlagAttacker) != 0 ? pkg.ReadZDOID() : ZDOID.None;
        m_skill = (Skills.SkillType)pkg.ReadShort();
        m_skillRaiseAmount = ReadIf(pkg, flags, FlagSkillRaiseAmount, 1f);
        m_weakSpot = unchecked((short)pkg.ReadChar());
        m_skillLevel = pkg.ReadSingle();
        m_itemLevel = pkg.ReadShort();
        m_itemWorldLevel = pkg.ReadByte();
        m_hitType = (HitType)pkg.ReadByte();
        m_healthReturn = pkg.ReadSingle();
        m_eitrAdd = pkg.ReadSingle();
        m_radius = pkg.ReadSingle();
        m_variant = pkg.ReadShort();
    }

    private static uint Flag(float value, float fallback, uint bit) => value.Equals(fallback) ? 0u : bit;
    private static void WriteIf(ZPackage pkg, uint flags, uint bit, float value) { if ((flags & bit) != 0) pkg.Write(value); }
    private static float ReadIf(ZPackage pkg, uint flags, uint bit, float fallback) => (flags & bit) != 0 ? pkg.ReadSingle() : fallback;
}

/// <summary>The game's skills component; only its skill ids are doubled, with the game's 1.0.16 numbers.</summary>
public partial class Skills : UnityEngine.MonoBehaviour
{
    public enum SkillType
    {
        None = 0, Swords = 1, Knives = 2, Clubs = 3, Polearms = 4, Spears = 5, Blocking = 6, Axes = 7, Bows = 8, ElementalMagic = 9,
        BloodMagic = 10, Unarmed = 11, Pickaxes = 12, WoodCutting = 13, Crossbows = 14, Jump = 100, Sneak = 101, Run = 102, Swim = 103,
        Fishing = 104, Cooking = 105, Farming = 106, Crafting = 107, Dodge = 108, Ride = 110, All = 999,
    }
}
