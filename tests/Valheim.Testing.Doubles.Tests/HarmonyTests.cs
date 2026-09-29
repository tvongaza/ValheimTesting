using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Xunit;

// Patch classes in the shapes mods write them; the doubles only record them.
[HarmonyPatch(typeof(Terminal), "TryRunCommand")]
[HarmonyPriority(Priority.High)]
[HarmonyAfter("other.mod")]
internal static class RecordedPatch { private static void Postfix() { } }

[HarmonyPatch(typeof(ZNetScene))]
[HarmonyPatch("GetPrefab", typeof(string))]
internal static class MergedPatch { private static void Prefix() { } }

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.TryGetItemPrefab), new[] { typeof(string), typeof(UnityEngine.GameObject) }, new[] { ArgumentType.Normal, ArgumentType.Out })]
internal static class ByRefPatch { private static void Postfix() { } }

[HarmonyPatch(typeof(UnityEngine.Behaviour), nameof(UnityEngine.Behaviour.enabled), MethodType.Setter)]
[HarmonyBefore("first.mod", "second.mod")]
internal static class SetterPatch { private static void Prefix() { } }

internal sealed class Hidden
{
    private int m_count = 3;
    internal float m_health = 1.5f;
    internal static string s_label = "static";
    private string Name { get; set; } = "hidden";
    private Hidden? m_next;
    private int Add(int a, int b) => a + b + m_count;
    private int Add(int a) => a + m_count;
    private sealed class Inner { }
    public Hidden Link(Hidden next) { m_next = next; return this; }
}

public sealed class HarmonyTests
{
    [Fact] public void PatchAttributesRecordTheirTargetAndOrdering()
    {
        var recorded = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(typeof(RecordedPatch)));
        Assert.Equal(typeof(Terminal), recorded.declaringType); Assert.Equal("TryRunCommand", recorded.methodName);
        Assert.Equal(Priority.High, recorded.priority); Assert.Equal(new[] { "other.mod" }, recorded.after);
        var merged = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(typeof(MergedPatch)));
        Assert.Equal(typeof(ZNetScene), merged.declaringType); Assert.Equal("GetPrefab", merged.methodName);
        Assert.Equal(new[] { typeof(string) }, merged.argumentTypes); Assert.Equal(-1, merged.priority);
        var byRef = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(typeof(ByRefPatch)));
        Assert.Equal(new[] { typeof(string), typeof(UnityEngine.GameObject).MakeByRefType() }, byRef.argumentTypes);
        var setter = HarmonyMethod.Merge(HarmonyMethodExtensions.GetFromType(typeof(SetterPatch)));
        Assert.Equal(MethodType.Setter, setter.methodType); Assert.Equal(new[] { "first.mod", "second.mod" }, setter.before);
        Assert.Throws<ArgumentException>(() => new HarmonyPatch(typeof(Terminal), "M", new[] { typeof(int) }, new[] { ArgumentType.Ref, ArgumentType.Out }));
        var named = new HarmonyPatch("Not.A.Type, Nowhere", "Method").info; // recorded, resolved only when patching
        Assert.Null(named.declaringType); Assert.Equal("Method", named.methodName); Assert.Null(named.methodType);
        Assert.Equal(Priority.High, HarmonyMethod.Merge(new List<HarmonyMethod> { new HarmonyPriority(Priority.High).info, new HarmonyPatch("M").info }).priority); // -1 does not overwrite
    }

    [Fact] public void PatchAllFindsEveryPatchClassButPatchesNothing()
    {
        var harmony = new Harmony("test.doubles");
        try
        {
            harmony.PatchAll(typeof(HarmonyTests).Assembly);
            var classes = harmony.Patches.Select(p => p.PatchClass).ToList();
            Assert.Contains(typeof(RecordedPatch), classes); Assert.Contains(typeof(MergedPatch), classes); Assert.Contains(typeof(SetterPatch), classes);
            Assert.DoesNotContain(typeof(Hidden), classes);
            Assert.Equal("GetPrefab", harmony.Patches.Single(p => p.PatchClass == typeof(MergedPatch)).Target.methodName);
            Assert.Equal(MethodType.Normal, harmony.Patches.Single(p => p.PatchClass == typeof(RecordedPatch)).Target.methodType); // Normal when unset, as HarmonyX
            Assert.Equal(MethodType.Setter, harmony.Patches.Single(p => p.PatchClass == typeof(SetterPatch)).Target.methodType);
            Assert.True(Harmony.HasAnyPatches("test.doubles"));
            harmony.PatchAll(typeof(HarmonyTests).Assembly);
            Assert.Equal(classes.Count, harmony.Patches.Count); // patching twice records each class once
        }
        finally { harmony.UnpatchSelf(); }
        Assert.False(Harmony.HasAnyPatches("test.doubles"));
        var single = Harmony.CreateAndPatchAll(typeof(Hidden), "test.single"); // PatchAll(Type) takes a class without attributes
        try { Assert.Equal(typeof(Hidden), single.Patches.Single().PatchClass); } finally { single.UnpatchSelf(); }
        Assert.Throws<ArgumentException>(() => new Harmony(""));
    }

    [Fact] public void TraverseReadsAndWritesPrivateMembersAndCallsMethods()
    {
        var first = new Hidden(); var second = new Hidden();
        first.Link(second);
        var t = Traverse.Create(first);
        Assert.Equal(3, t.Field("m_count").GetValue<int>());
        t.Field("m_count").SetValue(10);
        Assert.Equal(10, t.Field<int>("m_count").Value);
        Assert.Equal("hidden", t.Property("Name").GetValue<string>());
        t.Property<string>("Name").Value = "renamed"; Assert.Equal("renamed", t.Property("Name").GetValue());
        Assert.Equal(15, t.Method("Add", 2, 3).GetValue<int>()); Assert.Equal(11, t.Method("Add", 1).GetValue<int>());
        Assert.Equal(13, t.Method("Add", new[] { typeof(int) }).GetValue<int>(3));
        Assert.Same(second, t.Field("m_next").GetValue()); Assert.Equal(3, t.Field("m_next").Field("m_count").GetValue<int>()); // chained
        Assert.Equal("static", Traverse.Create<Hidden>().Field("s_label").GetValue<string>());
        Assert.True(t.Type("Inner").TypeExists()); Assert.Contains("m_count", t.Fields()); Assert.Contains("Name", t.Properties());
    }

    [Fact] public void AMissingMemberGivesAnEmptyTraverseAndAMethodValueCannotBeSet()
    {
        var t = Traverse.Create(new Hidden());
        Assert.False(t.Field("nope").FieldExists()); Assert.Null(t.Field("nope").GetValue());
        Assert.Null(t.Field("nope").Field("deeper").GetValue()); t.Field("nope").SetValue(1); // no throw, as in Harmony
        Assert.Null(Traverse.Create<Hidden>().Field("m_count").GetValue()); // an instance field without an instance
        Assert.Throws<Exception>(() => t.Method("Add", 1).SetValue(2)); // HarmonyX throws a plain Exception
        Assert.Throws<Exception>(() => t.Field("m_count").GetValue(1));
        Assert.Throws<InvalidCastException>(() => t.Field<int>("m_health").Value); // a typed read casts, as HarmonyX's
        Assert.Equal(typeof(float), t.Field("m_health").GetValueType()); Assert.Null(t.Field("nope").ToString());
        Assert.Throws<System.Reflection.AmbiguousMatchException>(() => AccessTools.Method(typeof(Hidden), "Add"));
        Assert.NotNull(AccessTools.Method(typeof(Hidden), "Add", new[] { typeof(int) }));
        Assert.Equal(typeof(Hidden), AccessTools.TypeByName("Hidden"));
        Assert.NotNull(AccessTools.Field(typeof(UnityEngine.MonoBehaviour), "m_behaviourEnabled")); // found on a base type
    }
}
