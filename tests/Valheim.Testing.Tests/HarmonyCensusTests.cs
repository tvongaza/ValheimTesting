using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// The runner side of the Harmony census against scripted adapter replies shaped like Valheim.Testing.Adapter's.
public class HarmonyCensusTests
{
    private const string Mod = "example.mymod", Path = "mymod.testing/harmony";
    private static readonly DeclaredPatch[] Declared =
    [
        new("Terminal::InitTerminal", "postfix", "MyMod.Plugin+RegisterCommands::Postfix"),
        new("ZNet::Awake", "prefix"),
    ];

    private static object Patch(string owner, string kind, string patch, int priority = 400, int index = 0, string[]? before = null) =>
        new { owner, kind, priority, index, before = before ?? Array.Empty<string>(), after = Array.Empty<string>(), patch };
    private static object Census(params (string Method, object[] Patches)[] methods) =>
        new { source = "harmony-patches", complete = true, owner = (string?)null, methods = methods.Select(m => new { method = m.Method, patches = m.Patches }).ToArray() };
    private static object Applied() => Census(
        ("Terminal::InitTerminal()", new[] { Patch("other.mod", "prefix", "Other.Hooks::Before", 800), Patch(Mod, "postfix", "MyMod.Plugin+RegisterCommands::Postfix()", index: 0) }),
        ("ZNet::Awake()", new[] { Patch(Mod, "prefix", "MyMod.Plugin+NetHooks::Prefix()") }));

    [Fact] public void EveryDeclaredPatchIsAppliedAndOtherOwnersOnTheSameMethodsAreReported()
    {
        var transport = new ScriptedTransport().Extension("mymod.testing", "harmony", args => { Assert.Equal(Mod, Assert.Single(args)); return Applied(); });
        using var actor = transport.Actor();
        var check = HarmonyCensus.Read(actor, Path, Mod).Check(Mod, Declared);
        check.RequireApplied();
        Assert.Empty(check.Missing); Assert.Empty(check.Undeclared);
        var other = Assert.Single(check.OtherOwners);
        Assert.Equal(("other.mod", "prefix", 800, "Terminal::InitTerminal()"), (other.Owner, other.Kind, other.Priority, other.Method));
        Assert.Equal(1, transport.Count("cli_extension mymod.testing/harmony"));
    }

    [Fact] public void AMissingTargetFailsWithThePatchNamed()
    {
        // Negative control: the mod's InitTerminal target no longer exists, so HarmonyX applied nothing there.
        var census = HarmonyCensus.Parse(Json(Census(("ZNet::Awake()", new[] { Patch(Mod, "prefix", "MyMod.Plugin+NetHooks::Prefix()") }))));
        var check = census.Check(Mod, Declared);
        Assert.Equal(Declared[0], Assert.Single(check.Missing));
        var error = Assert.Throws<InvalidOperationException>(check.RequireApplied);
        Assert.Contains("postfix MyMod.Plugin+RegisterCommands::Postfix on Terminal::InitTerminal", error.Message);
        Assert.DoesNotContain("ZNet::Awake", error.Message);
    }

    [Fact] public void KindOverloadAndPatchMethodMustAllMatch()
    {
        var census = HarmonyCensus.Parse(Json(Applied()));
        Assert.Single(census.Check(Mod, [new DeclaredPatch("Terminal::InitTerminal", "prefix")]).Missing); // Another owner's prefix is not ours.
        Assert.Single(census.Check(Mod, [new DeclaredPatch("Terminal::InitTerminal(System.String)", "postfix")]).Missing);
        Assert.Empty(census.Check(Mod, [new DeclaredPatch("Terminal::InitTerminal()", "postfix")]).Missing);
        Assert.Single(census.Check(Mod, [new DeclaredPatch("Terminal::InitTerminal", "postfix", "MyMod.Plugin+Other::Postfix")]).Missing);
        var partial = census.Check(Mod, [Declared[0]]);
        Assert.Equal("ZNet::Awake()", Assert.Single(partial.Undeclared).Method); // Applied but not in the list: reported, not failed.
        partial.RequireApplied();
        Assert.Throws<ArgumentException>(() => census.Check(Mod, [new DeclaredPatch("Terminal::InitTerminal", "Postfix")]));
    }

    [Fact] public void AnIncompleteOrMalformedCensusIsNeverAnEmptyOne()
    {
        var transport = new ScriptedTransport().Extension("mymod.testing", "harmony", _ => new { source = "harmony-patches", complete = false, methods = Array.Empty<object>() });
        using var actor = transport.Actor();
        Assert.Throws<InvalidOperationException>(() => HarmonyCensus.Read(actor, Path));
        Assert.Throws<InvalidOperationException>(() => HarmonyCensus.Parse(Json(new { source = "other", complete = true, methods = Array.Empty<object>() })));
        Assert.Throws<InvalidOperationException>(() => HarmonyCensus.Parse(Json(Census(("A::B()", new[] { Patch(Mod, "detour", "X::Y()") })))));
        Assert.Throws<InvalidOperationException>(() => HarmonyCensus.Parse(Json(Census(("A::B()", new[] { Patch("", "prefix", "X::Y()") })))));
    }

    [Fact] public void AnUnloadThatRemovesOnlyItsOwnPatchesLeavesOthersUnchanged()
    {
        var before = HarmonyCensus.Parse(Json(Applied()));
        // After the owner unpatches itself the other prefix is still there; its index may shift, which is not a change.
        var after = HarmonyCensus.Parse(Json(Census(("Terminal::InitTerminal()", new[] { Patch("other.mod", "prefix", "Other.Hooks::Before", 800, index: 3) }))));
        Assert.Empty(HarmonyCensus.OthersChanged(before, after, Mod));
        // Negative control: an unload that also removed another owner's patch (UnpatchAll without an ID).
        var stripped = HarmonyCensus.Parse(Json(Census()));
        Assert.Equal("removed: other.mod prefix Other.Hooks::Before on Terminal::InitTerminal() priority=800 before=[] after=[]", Assert.Single(HarmonyCensus.OthersChanged(before, stripped, Mod)));
        var added = HarmonyCensus.Parse(Json(Census(("Terminal::InitTerminal()", new[] { Patch("other.mod", "prefix", "Other.Hooks::Before", 800), Patch("third.mod", "finalizer", "T::F()") }))));
        Assert.Equal("added: third.mod finalizer T::F() on Terminal::InitTerminal() priority=400 before=[] after=[]", Assert.Single(HarmonyCensus.OthersChanged(before, added, Mod)));
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
}
