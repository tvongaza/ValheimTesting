using System;
using System.Collections.Generic;

// The package encodings a mod depends on, as named cases (issue #15). This one file compiles twice: into the game-side
// capture plugin (tests/Valheim.Testing.Doubles.GameCapture) against the game's own assemblies, where each case's bytes
// are written by the game's ZPackage and ZRpc and saved, and into the doubles tests against the doubles, which must write
// the same bytes (GameCapturedBytesTests). Use only members both have, and C# both compilers accept (net48, C# 10).

/// <summary>One named write into an empty package; its bytes are the package's array afterwards.</summary>
public sealed class ZPackageCase
{
    public ZPackageCase(string name, Action<ZPackage> write) { Name = name; Write = write; }
    public string Name { get; }
    public Action<ZPackage> Write { get; }
}

public static class ZPackageCases
{
    /// <summary>A mod's own routed-RPC argument (the game's <c>ISerializableParameter</c>).</summary>
    public sealed class Stamp : ISerializableParameter
    {
        public int A; public string B = "";
        public void Serialize(ref ZPackage pkg) { pkg.Write(A); pkg.Write(B); }
        public void Deserialize(ref ZPackage pkg) { A = pkg.ReadInt(); B = pkg.ReadString(); }
    }

    /// <summary>The package nested or compressed by the cases below.</summary>
    public static ZPackage Inner() { var pkg = new ZPackage(); pkg.Write("inner"); pkg.Write(42); return pkg; }

    /// <summary>
    /// <c>WriteCompressed(Inner())</c> as the doubles wrote it on .NET 10.0.7 on macOS (gzip at CompressionLevel.Fastest;
    /// the header names the OS, so it differs on Windows). The game's reader must read it back. Gzip output differs between
    /// runtimes, so what can be compared is that each side reads the other's stream, not the compressed bytes.
    /// </summary>
    public const string DoubleCompressedInner = "1E-00-00-00-1F-8B-08-00-00-00-00-00-04-13-63-CD-CC-CB-4B-2D-D2-62-60-60-00-00-E0-73-2F-FF-0A-00-00-00";

    /// <summary>
    /// Cases whose bytes must be identical in the game and the doubles: every package write, the routed-RPC argument table
    /// (<c>ZRpc.Serialize</c>), a hit, and the game reading the doubles' compressed stream.
    /// </summary>
    public static IReadOnlyList<ZPackageCase> Exact()
    {
        var cases = new List<ZPackageCase>
        {
            new("int 1", p => p.Write(1)),
            new("int -1", p => p.Write(-1)),
            new("int min", p => p.Write(int.MinValue)),
            new("uint 0xDEADBEEF", p => p.Write(0xDEADBEEFu)),
            new("long -2", p => p.Write(-2L)),
            new("long max", p => p.Write(long.MaxValue)),
            new("ulong 1", p => p.Write(1UL)),
            new("short -2", p => p.Write((short)-2)),
            new("ushort 0xBEEF", p => p.Write((ushort)0xBEEF)),
            new("byte 7", p => p.Write((byte)7)),
            new("sbyte -1", p => p.Write((sbyte)-1)),
            new("char a", p => p.Write('a')),
            new("char e-acute", p => p.Write('é')),
            new("char U+20AC", p => p.Write('€')),
            new("char U+FFFF", p => p.Write('￿')),
            new("bool true", p => p.Write(true)),
            new("bool false", p => p.Write(false)),
            new("float 1.5", p => p.Write(1.5f)),
            new("float -0", p => p.Write(-0f)),
            new("float max", p => p.Write(float.MaxValue)),
            new("float smallest subnormal", p => p.Write(float.Epsilon)),
            new("float NaN", p => p.Write(float.NaN)),
            new("double 1", p => p.Write(1.0)),
            new("double pi", p => p.Write(Math.PI)),
            new("string empty", p => p.Write("")),
            new("string h e-acute", p => p.Write("hé")),
            new("string CJK", p => p.Write("日本")),
            new("string astral", p => p.Write("😀")),
            new("string 200 a", p => p.Write(new string('a', 200))),
            new("byte[] 1 2", p => p.Write(new byte[] { 1, 2 })),
            new("byte[] empty", p => p.Write(new byte[0])),
            new("package inner", p => p.Write(Inner())),
            new("ZDOID 2 3", p => p.Write(new ZDOID(2, 3))),
            new("ZDOID None", p => p.Write(ZDOID.None)),
            new("Vector3 1 2 3", p => p.Write(new UnityEngine.Vector3(1, 2, 3))),
            new("Vector3 -0.5 1e6 3.25", p => p.Write(new UnityEngine.Vector3(-0.5f, 1e6f, 3.25f))),
            new("Quaternion identity", p => p.Write(UnityEngine.Quaternion.identity)),
            new("Quaternion half", p => p.Write(new UnityEngine.Quaternion(0.5f, 0.5f, 0.5f, 0.5f))),
            new("Vector2i 1 -1", p => p.Write(new Vector2i(1, -1))),
            new("Vector2s 1 -1", p => p.Write(new Vector2s(1, -1))),
            new("NumItems 0", p => p.WriteNumItems(0)),
            new("NumItems 127", p => p.WriteNumItems(127)),
            new("NumItems 128", p => p.WriteNumItems(128)),
            new("NumItems 300", p => p.WriteNumItems(300)),
            new("SmallRotation 0 0 0", p => p.WriteSmallRotation(new UnityEngine.Vector3(0, 0, 0))),
            new("SmallRotation 0 90 0", p => p.WriteSmallRotation(new UnityEngine.Vector3(0, 90, 0))),
            new("SmallRotation 10 20 30", p => p.WriteSmallRotation(new UnityEngine.Vector3(10, 20, 30))),
            new("SmallRotation 359.5 180 0.5", p => p.WriteSmallRotation(new UnityEngine.Vector3(359.5f, 180, 0.5f))),
            new("routed numbers", p => Serialize(p, 1, 0xDEADBEEFu, -2L, 1.5f, 1.0, true)),
            new("routed values", p => Serialize(p, "hé", Inner(), new List<string> { "a", "b" }, new UnityEngine.Vector3(1, 2, 3),
                UnityEngine.Quaternion.identity, new ZDOID(2, 3), new Stamp { A = 7, B = "x" })),
            new("routed empty list", p => Serialize(p, new List<string>())),
            new("HitData default", p => { var hit = new HitData(); hit.Serialize(ref p); }),
            new("HitData set", p => { var hit = Hit(); hit.Serialize(ref p); }),
            new("routed HitData", p => Serialize(p, Hit())),
            new("read double-compressed inner", p => p.Write(new ZPackage(FromHex(DoubleCompressedInner)).ReadCompressedPackage())),
        };
        return cases;
    }

    /// <summary>Cases whose bytes are kept but not compared: the game's gzip output, which the doubles must be able to read.</summary>
    public static IReadOnlyList<ZPackageCase> Compressed() => new List<ZPackageCase>
    {
        new("compressed inner", p => p.WriteCompressed(Inner())),
    };

    private static void Serialize(ZPackage pkg, params object[] args) => ZRpc.Serialize(args, ref pkg);

    private static HitData Hit()
    {
        var hit = new HitData();
        hit.m_damage.m_damage = 12.5f; hit.m_damage.m_fire = 3f; hit.m_damage.m_nonPlayer = 2f;
        hit.m_toolTier = 2; hit.m_pushForce = 40f; hit.m_backstabBonus = 3f;
        hit.m_dodgeable = true; hit.m_ranged = true;
        hit.m_point = new UnityEngine.Vector3(10, 32, -5); hit.m_dir = new UnityEngine.Vector3(0, 0, 1);
        hit.m_statusEffectHash = 123456; hit.m_attacker = new ZDOID(4, 5); hit.m_skill = (Skills.SkillType)7; hit.m_skillRaiseAmount = 0.5f;
        hit.m_weakSpot = 2; hit.m_skillLevel = 15f; hit.m_itemLevel = 3; hit.m_itemWorldLevel = 1; hit.m_hitType = HitData.HitType.PlayerHit;
        hit.m_healthReturn = 1f; hit.m_eitrAdd = 0.25f; hit.m_radius = 1.5f; hit.m_variant = 1;
        return hit;
    }

    /// <summary>The package's bytes as "01-00-00-00".</summary>
    public static string Hex(byte[] bytes) => BitConverter.ToString(bytes);

    public static byte[] FromHex(string hex)
    {
        if (hex.Length == 0) return new byte[0];
        string[] parts = hex.Split('-');
        var bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++) bytes[i] = Convert.ToByte(parts[i], 16);
        return bytes;
    }
}
