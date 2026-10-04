using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Valheim.Testing.Doubles;
using Xunit;

// ValheimWorldScope keeps every mutable static the doubles declare (issue #306). Each one is changed inside a scope and
// must be back after it, unless it is listed here as deliberately left alone, with the reason. The restore mechanism
// itself (reverse order, every restore runs when one throws, setup failures roll back) is StaticOverride's, tested in
// Valheim.Testing.Tests/StaticOverrideTests.cs.
public sealed class ValheimWorldScopeTests
{
    private static readonly Dictionary<string, string> Unscoped = new(StringComparer.Ordinal)
    {
        ["Heightmap.m_paintMaskDirt"] = "the game's paint colours: game data, never changed by a test",
        ["Heightmap.m_paintMaskCultivated"] = "the game's paint colours",
        ["Heightmap.m_paintMaskPaved"] = "the game's paint colours",
        ["Heightmap.m_paintMaskNothing"] = "the game's paint colours",
        ["Heightmap.m_paintMaskClearVegetation"] = "the game's paint colours",
        ["Heightmap.m_paintMaskDeepSnow"] = "the game's paint colours",
        ["ZNetView.Everybody"] = "the game's broadcast target, 0",
        ["UnityEngine.Object.s_lastInstanceID"] = "an id counter: instance ids stay unique across tests, as in one Unity session",
        ["ZDO.s_nextId"] = "an id counter: ZDO ids stay unique across tests",
        ["UnityEngine.Object.s_unityPendingParent"] = "[ThreadStatic] plumbing that lives only inside one Instantiate call",
        ["UnityEngine.Object.s_unityPendingWorldStays"] = "[ThreadStatic] plumbing that lives only inside one Instantiate call",
        ["UnityEngine.Object.s_unityLastCloneMap"] = "[ThreadStatic] plumbing read right after one Instantiate call",
        ["ZRpc.m_timeout"] = "goes with the ping and timeout model (#307)",
        ["ZNet.s_joiningKey"] = "a key counter for joining peers: keys stay unique; goes with the handshake trim (#307)",
    };

    // Readonly statics cannot be put back, only cleared; each one that holds objects says why it may outlive a test.
    private static readonly Dictionary<string, string> SharedReadonly = new(StringComparer.Ordinal)
    {
        ["BepInEx.Configuration.ConfigDefinition.s_invalid"] = "constant table",
        ["BepInEx.Configuration.TomlTypeConverter.s_converters"] = "BepInEx's converter registry is process-wide, as the real one; AddConverter refuses a second converter for a type",
        ["BepInEx.Configuration.TomlTypeConverter.s_escapes"] = "constant table",
        ["BepInEx.Logging.Logger.Internal"] = "BepInEx's own log source; its lines go to the scoped capture",
        ["DropTable.s_sharedItems"] = "the game's shared result list, cleared by every GetDropListItems",
        ["HarmonyLib.Harmony.s_patches"] = "Harmony's patches are process-wide, as the real Harmony's; a test unpatches what it patched (UnpatchSelf)",
        ["HarmonyLib.HarmonyMethod.s_fields"] = "reflection cache",
        ["HeightmapBuilder.s_disposed"] = "marker object for a disposed builder",
        ["Localization.s_endChars"] = "constant table",
        ["Splatform.PlatformUserID.s_displayPrefixesToPlatform"] = "constant table",
        ["Splatform.PlatformUserID.s_platformToDisplayPrefixes"] = "constant table",
        ["UnityEngine.MonoBehaviour.s_messages"] = "reflection cache",
        ["Utils.s_nameEnds"] = "constant table",
        ["ZDO.LegacyKeys"] = "constant table",
    };

    private static string Name(MemberInfo member) => member.DeclaringType!.FullName!.Replace('+', '.') + "." + member.Name;

    /// <summary>Every static field or settable static property the doubles' source files declare in this assembly.</summary>
    private static List<MemberInfo> MutableStatics()
    {
        var source = DoublesSource.Load(typeof(ValheimWorldScopeTests).Assembly);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var found = new List<MemberInfo>();
        foreach (var type in typeof(ValheimWorldScopeTests).Assembly.GetTypes())
        {
            if (type.Name.Contains('<') || type.ContainsGenericParameters) continue;
            if (!(MemberIndex.IsDoubledNamespace(type.Namespace) || type.Namespace == "Valheim.Testing.Doubles") || !source.Declares(type)) continue;
            found.AddRange(type.GetFields(flags).Where(f => !f.IsLiteral && !f.IsInitOnly && !f.IsDefined(typeof(CompilerGeneratedAttribute), false)));
            found.AddRange(type.GetProperties(flags).Where(p => p.GetIndexParameters().Length == 0 && p.GetGetMethod(true) != null && p.GetSetMethod(true) != null && source.Declares(p)));
        }
        return found;
    }

    private static object? Get(MemberInfo member) => member is FieldInfo f ? f.GetValue(null) : ((PropertyInfo)member).GetValue(null);
    private static void Set(MemberInfo member, object? value)
    {
        if (member is FieldInfo f) f.SetValue(null, value); else ((PropertyInfo)member).SetValue(null, value);
    }
    private static Type TypeOf(MemberInfo member) => member is FieldInfo f ? f.FieldType : ((PropertyInfo)member).PropertyType;

    /// <summary>A value other than <paramref name="current"/> that the doubles still work with (no nulls where a list is expected).</summary>
    private static object? Other(Type type, object? current)
    {
        if (type == typeof(bool)) return !(bool)current!;
        if (type == typeof(int)) return (int)current! + 1;
        if (type == typeof(long)) return (long)current! + 1;
        if (type == typeof(float)) return (float)current! + 1f;
        if (Nullable.GetUnderlyingType(type) == typeof(float) || type == typeof(string)) return current == null ? (type == typeof(string) ? "x" : (object)1f) : null;
        if (typeof(Delegate).IsAssignableFrom(type))
        {
            var invoke = type.GetMethod("Invoke")!;
            var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType)).ToArray();
            return Expression.Lambda(type, Expression.Default(invoke.ReturnType), parameters).Compile();
        }
        if (type.IsEnum) return Enum.GetValues(type).Cast<object>().First(v => !v.Equals(current));
        if (type.IsValueType) throw new InvalidOperationException($"No other value for {type.Name}: add a case here.");
        try { return Activator.CreateInstance(type, nonPublic: true); }
        catch (MissingMethodException) { return RuntimeHelpers.GetUninitializedObject(type); }
    }

    [Fact] public void TheScopePutsBackEveryStaticTheDoublesKeep()
    {
        var notRestored = new List<string>();
        foreach (var member in MutableStatics())
        {
            string name = Name(member);
            if (Unscoped.ContainsKey(name)) continue;
            object? before = Get(member);
            object? changed;
            using (new ValheimWorldScope())
            {
                changed = Other(TypeOf(member), before);
                Set(member, changed);
                Assert.True(Equals(changed, Get(member)) || ReferenceEquals(changed, Get(member)), name + " did not take the test's value.");
            }
            object? after = Get(member);
            bool restored = TypeOf(member).IsValueType || after is string ? Equals(before, after) : ReferenceEquals(before, after);
            if (!restored) { notRestored.Add(name); Set(member, before); }
        }
        Assert.True(notRestored.Count == 0, "ValheimWorldScope does not put these statics back; add them to its list in WorldDoubles.cs " +
            "(or, if they must stay process-wide, to Unscoped here with the reason): " + string.Join(", ", notRestored));
    }

    [Fact] public void EveryReadonlyStaticThatHoldsObjectsSaysWhyItMayOutliveATest()
    {
        var source = DoublesSource.Load(typeof(ValheimWorldScopeTests).Assembly);
        var found = new List<string>();
        foreach (var type in typeof(ValheimWorldScopeTests).Assembly.GetTypes())
        {
            if (type.Name.Contains('<') || type.ContainsGenericParameters) continue;
            if (!(MemberIndex.IsDoubledNamespace(type.Namespace) || type.Namespace == "Valheim.Testing.Doubles") || !source.Declares(type)) continue;
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                if (field.IsInitOnly && !field.FieldType.IsValueType && field.FieldType != typeof(string) && !field.IsDefined(typeof(CompilerGeneratedAttribute), false))
                    found.Add(Name(field));
        }
        Assert.Equal(SharedReadonly.Keys.OrderBy(n => n, StringComparer.Ordinal), found.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact] public void EveryUnscopedEntryIsStillAStaticOfTheDoubles()
    {
        var names = new HashSet<string>(MutableStatics().Select(Name), StringComparer.Ordinal);
        Assert.Empty(Unscoped.Keys.Where(name => !names.Contains(name)));
    }

    [Fact] public void ASecondDisposeChangesNothing()
    {
        var world = new PlaneWorld();
        var scope = new ValheimWorldScope().WithScene();
        scope.Dispose();
        var outerWorld = WorldGenerator.instance;
        var queued = new UnityEngine.GameObject("queued after the scope");
        try
        {
            WorldGenerator.instance = world;
            UnityEngine.Object.Destroy(queued);
            scope.Dispose();
            Assert.Same(world, WorldGenerator.instance);   // not put back a second time
            Assert.False(queued.Destroyed);                // and no second end of frame
        }
        finally { UnityEngine.Object.EndOfFrame(); WorldGenerator.instance = outerWorld; }
    }

    // A test that never disposes its scope leaves its pending destroys behind; a scene of its own (WithScene) does not see
    // them, and the outer scene gets them back when the inner scope closes.
    [Fact] public void PendingDestroysBelongToTheirScene()
    {
        var outer = new UnityEngine.GameObject("queued in the outer scene");
        using (new ValheimWorldScope())
        {
            UnityEngine.Object.Destroy(outer);
            using (new ValheimWorldScope().WithScene())
            {
                var inner = new UnityEngine.GameObject("queued in the inner scene");
                UnityEngine.Object.Destroy(inner);
                Assert.Equal(1, UnityEngine.Object.EndOfFrame());
                Assert.False(outer.Destroyed);
            }
            Assert.False(outer.Destroyed);
        }
        Assert.True(outer.Destroyed); // the outer scope ended its own frame
    }

    // A behaviour that destroys another object as it goes, and one whose OnDestroy fails.
    public sealed class Chain : UnityEngine.MonoBehaviour { public UnityEngine.GameObject? m_next; private void OnDestroy() { if (m_next != null) Destroy(m_next); } }
    public sealed class Failing : UnityEngine.MonoBehaviour { private void OnDestroy() => throw new InvalidOperationException("OnDestroy failed"); }

    [Fact] public void DisposingFinishesTheDestroysThatOnDestroyQueues()
    {
        UnityEngine.GameObject next;
        using (new ValheimWorldScope().WithScene())
        {
            var first = new UnityEngine.GameObject("first");
            next = new UnityEngine.GameObject("next");
            first.AddComponent<Chain>().m_next = next;
            UnityEngine.Object.Destroy(first);
        }
        Assert.True(next.Destroyed);
    }

    [Fact] public void AFailingEndOfFrameStillRestoresAndReachesTheTest()
    {
        var world = WorldGenerator.instance;
        var scope = new ValheimWorldScope().WithScene().WithWorld(new PlaneWorld());
        UnityEngine.Object.Destroy(new UnityEngine.GameObject("fails").AddComponent<Failing>().gameObject);
        var error = Assert.Throws<InvalidOperationException>(scope.Dispose);
        Assert.Equal("OnDestroy failed", error.Message);
        Assert.Same(world, WorldGenerator.instance);
    }

    // AtMainMenu starts from the same empty scene as WithScene, then takes the world away.
    [Fact] public void TheMainMenuIsAnEmptySceneWithoutAWorld()
    {
        var left = new UnityEngine.GameObject("left in the previous scene");
        using var scope = new ValheimWorldScope().WithScene();
        UnityEngine.Object.Destroy(left);
        scope.AtMainMenu();
        Assert.Empty(UnityEngine.Object.FindObjectsByType<UnityEngine.GameObject>(UnityEngine.FindObjectsSortMode.None));
        Assert.Equal(0, UnityEngine.Object.EndOfFrame());
        Assert.Null(ZNetScene.instance);
    }

    private sealed class PlaneWorld : WorldGenerator { public override float GetHeight(float x, float z) => 0f; }
}
