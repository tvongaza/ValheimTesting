using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

// The math doubles copy the game's UnityEngine.CoreModule (issue #997): its AggressiveInlining marks, IEquatable<T>, an exact
// Equals and GetHashCode, and Unity's approximate ==. The expectations here were read from the game's own CoreModule
// metadata (MethodImplAttributes and the IL of op_Equality, Equals and GetHashCode).
public sealed class UnityMathTests
{
    // 3e-6 apart: a squared distance of 9e-12, under Unity's kEpsilon * kEpsilon (9.99999944E-11f), so == says equal.
    private const float Near = 3e-6f;
    // 2e-5 apart: a squared distance of 4e-10, over it.
    private const float Far = 2e-5f;

    [Fact] public void Vector2EqualityIsApproximateAndEqualsIsExact()
    {
        var a = new Vector2(10f, 20f);
        var near = new Vector2(10f + Near, 20f);
        Assert.True(a == near);
        Assert.False(a != near);
        Assert.False(a.Equals(near));
        Assert.False(a.Equals((object)near));
        Assert.False(a == new Vector2(10f + Far, 20f));
        Assert.True(a != new Vector2(10f, 20f + Far));
        Assert.True(a.Equals(new Vector2(10f, 20f)));
    }

    [Fact] public void Vector3EqualityIsApproximateAndEqualsIsExact()
    {
        var a = new Vector3(10f, 20f, 30f);
        var near = new Vector3(10f, 20f, 30f + Near);
        Assert.True(a == near);
        Assert.False(a != near);
        Assert.False(a.Equals(near));
        Assert.False(a.Equals((object)near));
        Assert.False(a == new Vector3(10f, 20f + Far, 30f));
        Assert.True(a != new Vector3(10f + Far, 20f, 30f));
        Assert.True(a.Equals(new Vector3(10f, 20f, 30f)));
    }

    [Fact] public void TheThresholdIsUnitysSquaredEpsilon()
    {
        Assert.Equal(1e-5f, Vector2.kEpsilon);
        Assert.Equal(1e-5f, Vector3.kEpsilon);
        Assert.Equal(1e-6f, Quaternion.kEpsilon);
        // Unity compiles kEpsilon * kEpsilon to the float 9.99999944E-11 (0x2EDBE6FE in the game's op_Equality).
        Assert.Equal(0x2EDBE6FE, BitConverter.SingleToInt32Bits(Vector2.kEpsilon * Vector2.kEpsilon));
    }

    // The operators themselves at the threshold: a squared distance of exactly kEpsilon * kEpsilon (float) is not equal
    // (strict <, computed in float), one ulp of distance less is; Quaternion == needs a dot strictly above 0.999999f.
    [Fact] public void TheOperatorsCompareStrictlyBelowTheThresholdInFloat()
    {
        float at = Vector2.kEpsilon, below = MathF.BitDecrement(Vector2.kEpsilon);
        Assert.False(new Vector2(0f, 0f) == new Vector2(at, 0f));
        Assert.True(new Vector2(0f, 0f) == new Vector2(below, 0f));
        Assert.False(new Vector3(0f, 0f, 0f) == new Vector3(0f, 0f, at));
        Assert.True(new Vector3(0f, 0f, 0f) == new Vector3(0f, 0f, below));
        Assert.Equal(0x3F7FFFEF, BitConverter.SingleToInt32Bits(1f - Quaternion.kEpsilon));
        float dotAt = 1f - Quaternion.kEpsilon;
        Assert.False(Quaternion.identity == new Quaternion(0f, 0f, 0f, dotAt));
        Assert.True(Quaternion.identity == new Quaternion(0f, 0f, 0f, MathF.BitIncrement(dotAt)));
    }

    [Fact] public void HashCodesAreUnitys()
    {
        var v2 = new Vector2(1.5f, -2.25f);
        Assert.Equal(v2.x.GetHashCode() ^ (v2.y.GetHashCode() << 2), v2.GetHashCode());
        var v3 = new Vector3(1.5f, -2.25f, 7f);
        Assert.Equal(v3.x.GetHashCode() ^ (v3.y.GetHashCode() << 2) ^ (v3.z.GetHashCode() >> 2), v3.GetHashCode());
        var i2 = new Vector2Int(3, -4);
        Assert.Equal(unchecked(3 * 73856093 ^ -4 * 83492791), i2.GetHashCode());
        var q = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f);
        Assert.Equal(q.x.GetHashCode() ^ (q.y.GetHashCode() << 2) ^ (q.z.GetHashCode() >> 2) ^ (q.w.GetHashCode() >> 1), q.GetHashCode());
    }

    [Fact] public void TheMathDoublesAreEquatableAsInTheGame()
    {
        Assert.True(typeof(IEquatable<Vector2>).IsAssignableFrom(typeof(Vector2)));
        Assert.True(typeof(IEquatable<Vector3>).IsAssignableFrom(typeof(Vector3)));
        Assert.True(typeof(IEquatable<Vector2Int>).IsAssignableFrom(typeof(Vector2Int)));
        Assert.True(typeof(IEquatable<Quaternion>).IsAssignableFrom(typeof(Quaternion)));
        Assert.True(typeof(IEquatable<Color>).IsAssignableFrom(typeof(Color)));
        // A HashSet keys on Equals (exact), not ==: two points == calls equal are still two keys, as in the game.
        var set = new HashSet<Vector2> { new(10f, 20f), new(10f + Near, 20f) };
        Assert.Equal(2, set.Count);
    }

    [Fact] public void Vector2IntEqualityIsExact()
    {
        Assert.True(new Vector2Int(3, 4) == new Vector2Int(3, 4));
        Assert.True(new Vector2Int(3, 4) != new Vector2Int(3, 5));
        Assert.True(new Vector2Int(3, 4).Equals(new Vector2Int(3, 4)));
        Assert.False(new Vector2Int(3, 4).Equals((object)new Vector2Int(4, 3)));
    }

    [Fact] public void QuaternionEqualityUsesTheDotProductAndEqualsTheComponents()
    {
        var a = Quaternion.Euler(0f, 90f, 0f);
        var b = Quaternion.Euler(0f, 90.01f, 0f);
        // Unity: == when Dot(a, b) > 1 - kEpsilon; a 0.01 degree turn has a dot of about 1 - 4e-9.
        Assert.True(a == b);
        Assert.False(a.Equals(b));
        Assert.False(a == Quaternion.Euler(0f, 91f, 0f));
        Assert.True(a != Quaternion.Euler(0f, 91f, 0f));
        // Equals compares the components only, as the game's does: the test-only Euler fields do not count.
        Assert.True(a.Equals(new Quaternion(a.x, a.y, a.z, a.w)));
        Assert.Equal(a.GetHashCode(), new Quaternion(a.x, a.y, a.z, a.w).GetHashCode());
    }

    [Fact] public void ColorEqualsIsExact()
    {
        var c = new Color(0.1f, 0.2f, 0.3f, 0.4f);
        Assert.True(c.Equals(new Color(0.1f, 0.2f, 0.3f, 0.4f)));
        Assert.False(c.Equals(new Color(0.1f, 0.2f, 0.3f, 0.4f + Near)));
        Assert.True(c == new Color(0.1f, 0.2f, 0.3f, 0.4f + Near));
    }

    // The members the game's CoreModule does NOT mark AggressiveInlining, among those the doubles define; every other public
    // member of these types is marked in the game, so it must be marked here.
    private static readonly Dictionary<Type, string[]> NotInlinedInTheGame = new()
    {
        [typeof(Vector2)] = Array.Empty<string>(),
        [typeof(Vector3)] = Array.Empty<string>(),
        [typeof(Vector2Int)] = Array.Empty<string>(),
        [typeof(Quaternion)] = new[] { "Inverse(UnityEngine.Quaternion)", "op_Multiply(UnityEngine.Quaternion, UnityEngine.Vector3)" },
        // The game's Color marks ToString (and colour constants the doubles lack) only.
        [typeof(Color)] = new[]
        {
            ".ctor(System.Single, System.Single, System.Single, System.Single)", "Equals(System.Object)", "Equals(UnityEngine.Color)",
            "GetHashCode()", "Lerp(UnityEngine.Color, UnityEngine.Color, System.Single)",
            "op_Equality(UnityEngine.Color, UnityEngine.Color)", "op_Inequality(UnityEngine.Color, UnityEngine.Color)",
        },
    };

    private static string Signature(MethodBase m) => m.Name + "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.FullName)) + ")";

    [Fact] public void TheMathDoublesInlineWhatTheGameInlines()
    {
        var wrong = new List<string>();
        foreach (var (type, notInlined) in NotInlinedInTheGame)
        {
            var members = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>().Concat(type.GetConstructors()).ToList();
            Assert.NotEmpty(members);
            foreach (var m in members)
            {
                bool inlined = m.MethodImplementationFlags.HasFlag(MethodImplAttributes.AggressiveInlining);
                bool game = !notInlined.Contains(Signature(m));
                if (inlined != game) wrong.Add(type.Name + "." + Signature(m) + (game ? " is AggressiveInlining in the game" : " is not AggressiveInlining in the game"));
            }
            wrong.AddRange(notInlined.Where(s => members.All(m => Signature(m) != s)).Select(s => type.Name + "." + s + " is in the game but not doubled"));
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
