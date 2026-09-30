using System.Text.Json;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

// The runner side of the content census against scripted adapter replies shaped like Valheim.Testing.Adapter's
// ContentCensus: identity by name and the game's hash, per side, with the owner's build.
public class ContentCensusTests
{
    private const string Owner = "example.mymod", Path = "mymod.testing/content-census", Md5 = "0123456789abcdef0123456789abcdef", OtherMd5 = "fedcba9876543210fedcba9876543210";
    private const string Item = "MyMod_Stake", RecipeName = "Recipe_MyMod_Stake";

    private static ContentExpectations Expectations(string extra = "") => ContentExpectations.Parse($$"""
        { "owner": "{{Owner}}", "scope": ["MyMod_", "Recipe_MyMod_"],
          "items": [{ "name": "{{Item}}", "sides": ["server", "client"] }],
          "prefabs": [{ "name": "{{Item}}", "sides": ["server", "client"] }],
          "recipes": [{ "name": "{{RecipeName}}", "sides": ["server", "client"], "item": "{{Item}}", "station": "piece_workbench", "resources": ["Wood"] }]{{extra}} }
        """);

    private static object Entry(string name, int listed = 1, string? resolves = "") =>
        new { name, hash = StableHash.Of(name), listed, resolves = resolves == "" ? name : resolves };
    private static object Reference(string? name, string lookup, int amount = 0) => new { name, lookup, amount };
    private static object Recipe(string name = RecipeName, string? item = Item, string itemLookup = "resolved", string? station = "piece_workbench", string stationLookup = "resolved", object[]? resources = null) => new
    {
        name, enabled = true, amount = 1, item = Reference(item, itemLookup), station = Reference(station, stationLookup), minStationLevel = 1,
        resources = resources ?? [Reference("Wood", "resolved", 2)],
    };

    // A complete census as the adapter replies; each test changes what it is about.
    private static Dictionary<string, object?> Census(string side = "server", string? md5 = Md5, object[]? items = null, object[]? prefabs = null, object[]? recipes = null,
        object[]? collisions = null, int totalItems = 900, string[]? scope = null) => new()
    {
        ["source"] = "content-census", ["complete"] = true, ["ready"] = true, ["reason"] = null, ["side"] = side, ["dedicated"] = side == "server",
        ["owner"] = new { guid = Owner, installed = md5 != null, version = md5 == null ? null : "0.1.0", md5 },
        ["scope"] = scope ?? ["MyMod_", "Recipe_MyMod_"],
        ["totals"] = new { items = totalItems, itemIndex = totalItems, recipes = 400, prefabs = totalItems == 0 ? 0 : 3000, prefabIndex = totalItems == 0 ? 0 : 3000 },
        ["items"] = items ?? [Entry(Item)], ["prefabs"] = prefabs ?? [Entry(Item)], ["recipes"] = recipes ?? [Recipe()],
        ["collisions"] = collisions ?? [],
    };

    private static ContentObservation Observed(Dictionary<string, object?> census) => ContentCensus.Parse(JsonSerializer.SerializeToElement(census));
    private static ContentCensusReport Reconcile(ContentExpectations expectations, ContentObservation? server, ContentObservation? client, string serverPin = Md5, string clientPin = Md5) =>
        ContentCensus.Reconcile(expectations, new SideObservation(CensusSide.Server, serverPin, server), new SideObservation(CensusSide.Client, clientPin, client));
    private static CensusEntry Line(ContentCensusReport report, CensusSide side, string kind, string name) =>
        report.Entries.Single(e => e.Side == side && e.Kind == kind && e.Name == name);

    [Fact] public void DeclaredContentPresentOnBothSidesPasses()
    {
        var transport = new ScriptedTransport()
            .Extension("mymod.testing", "content-census", args => { Assert.Equal(new[] { Owner, "MyMod_", "Recipe_MyMod_" }, args); return Census(); });
        var clientTransport = new ScriptedTransport().Extension("mymod.testing", "content-census", _ => Census("client"));
        using var server = transport.Actor("server");
        using var client = clientTransport.Actor("client");
        var expectations = Expectations();
        var report = Reconcile(expectations, ContentCensus.Read(server, Path, expectations), ContentCensus.Read(client, Path, expectations));
        report.RequirePassed();
        // Each side judged on its own: item, prefab, recipe and its three dependencies, on both.
        Assert.Equal(12, report.Entries.Count);
        Assert.All(report.Entries, e => Assert.Equal(CensusState.Present, e.State));
        Assert.Equal(StableHash.Of(Item), Line(report, CensusSide.Client, "item", Item).Hash);
        Assert.Equal(new[] { CensusSide.Server, CensusSide.Client }, report.Sides.Select(s => s.Side));
        Assert.All(report.Sides, s => Assert.Equal(Md5, s.ObservedBuild));
        // The report is evidence: states and sides are written as words.
        string json = JsonSerializer.Serialize(report);
        Assert.Contains("\"state\":\"present\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"side\":\"client\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] public void AHashSharedWithAnotherNameIsADuplicateNamingBoth()
    {
        // Another mod's prefab under a name whose hash is the item's: the index holds only one of them.
        var collision = new { registry = "prefabs", hash = StableHash.Of(Item), names = new[] { Item, "OtherMod_Thing" }, indexed = "OtherMod_Thing" };
        var server = Observed(Census(prefabs: [Entry(Item, resolves: "OtherMod_Thing")], collisions: [collision]));
        var report = Reconcile(Expectations(), server, Observed(Census("client")));
        var line = Line(report, CensusSide.Server, "prefab", Item);
        Assert.Equal(CensusState.Duplicate, line.State);
        Assert.Contains("shared with OtherMod_Thing; the index holds OtherMod_Thing", line.Detail);
        // Without the collision record the lookup alone still shows another name answering for the hash.
        var alone = Reconcile(Expectations(), Observed(Census(prefabs: [Entry(Item, resolves: "OtherMod_Thing")])), Observed(Census("client")));
        Assert.Equal(CensusState.Duplicate, Line(alone, CensusSide.Server, "prefab", Item).State);
        Assert.Equal(CensusState.Present, Line(report, CensusSide.Server, "item", Item).State); // The item registry has no such collision.
        var error = Assert.Throws<InvalidOperationException>(report.RequirePassed);
        Assert.Contains("server: duplicate prefab MyMod_Stake", error.Message);
        Assert.DoesNotContain("client:", error.Message);
    }

    [Fact] public void ContentRegisteredTwiceIsADuplicate()
    {
        var twice = Reconcile(Expectations(), Observed(Census(items: [Entry(Item, listed: 2)])), Observed(Census("client")));
        Assert.Equal(CensusState.Duplicate, Line(twice, CensusSide.Server, "item", Item).State);
        Assert.Contains("lists it 2 times", Line(twice, CensusSide.Server, "item", Item).Detail);

        // Two recipes of one name: which one crafts depends on list order, so its dependencies cannot be judged.
        var recipes = Reconcile(Expectations(), Observed(Census(recipes: [Recipe(), Recipe(station: "forge")])), Observed(Census("client")));
        Assert.Equal(CensusState.Duplicate, Line(recipes, CensusSide.Server, "recipe", RecipeName).State);
        Assert.Equal(CensusState.Unsupported, Line(recipes, CensusSide.Server, "recipe-station", RecipeName + "/piece_workbench").State);
        Assert.False(recipes.Passed);
    }

    [Fact] public void AMissingDependencyIsUnresolved()
    {
        // The station reference is not a registered station, the resource's item is not registered, and one resource is extra.
        var recipe = Recipe(stationLookup: "unresolved", resources: [Reference("Wood", "unresolved", 2), Reference("Resin", "resolved", 1)]);
        var report = Reconcile(Expectations(), Observed(Census(recipes: [recipe])), Observed(Census("client")));
        Assert.Equal(CensusState.Present, Line(report, CensusSide.Server, "recipe", RecipeName).State);
        Assert.Equal(CensusState.Unresolved, Line(report, CensusSide.Server, "recipe-station", RecipeName + "/piece_workbench").State);
        Assert.Equal(CensusState.Unresolved, Line(report, CensusSide.Server, "recipe-resource", RecipeName + "/Wood").State);
        Assert.Equal(CensusState.Unexpected, Line(report, CensusSide.Server, "recipe-resource", RecipeName + "/Resin").State);
        Assert.Equal(CensusState.Present, Line(report, CensusSide.Server, "recipe-item", RecipeName + "/" + Item).State);

        // A recipe that crafts another item, or needs no station where one is declared, is unresolved too.
        var other = Reconcile(Expectations(), Observed(Census(recipes: [Recipe(item: "MyMod_Other", station: null, stationLookup: "none")])), Observed(Census("client")));
        Assert.Contains("crafts MyMod_Other, not the declared MyMod_Stake", Line(other, CensusSide.Server, "recipe-item", RecipeName + "/" + Item).Detail);
        Assert.Contains("crafted by hand, declared to need piece_workbench", Line(other, CensusSide.Server, "recipe-station", RecipeName + "/piece_workbench").Detail);
    }

    [Fact] public void AMissingClientObservationFailsAndTheServersNeverStandsForIt()
    {
        var server = Observed(Census());
        var report = Reconcile(Expectations(), server, null);
        Assert.False(report.Passed);
        Assert.Contains("no client observation", report.Sides.Single(s => s.Side == CensusSide.Client).Problem);
        Assert.All(report.Entries.Where(e => e.Side == CensusSide.Client), e => Assert.Equal(CensusState.Missing, e.State));
        Assert.All(report.Entries.Where(e => e.Side == CensusSide.Server), e => Assert.Equal(CensusState.Present, e.State));
        // Not supplying the client at all is the same failure.
        Assert.False(ContentCensus.Reconcile(Expectations(), new SideObservation(CensusSide.Server, Md5, server)).Passed);

        // The server's census handed in as the client's: refused by the side it reports.
        var swapped = Reconcile(Expectations(), server, server);
        Assert.Contains("reports being the server", swapped.Sides.Single(s => s.Side == CensusSide.Client).Problem);
        Assert.DoesNotContain(swapped.Entries, e => e.Side == CensusSide.Client && e.State == CensusState.Present);
    }

    [Fact] public void AnEmptyOrUnreadyReplyIsNeverAnEmptyCensus()
    {
        var expectations = Expectations();
        var empty = new ScriptedTransport().Extension("mymod.testing", "content-census", _ => Census(items: [], prefabs: [], recipes: [], totalItems: 0));
        using (var actor = empty.Actor())
            Assert.Contains("empty registry", Assert.Throws<InvalidOperationException>(() => ContentCensus.Read(actor, Path, expectations)).Message);

        var unready = new ScriptedTransport().Extension("mymod.testing", "content-census",
            _ => new { source = "content-census", complete = false, ready = false, reason = "ObjectDB is not populated yet." });
        using (var actor = unready.Actor("client"))
        {
            var error = Assert.Throws<InvalidOperationException>(() => ContentCensus.Read(actor, Path, expectations));
            Assert.Equal("The client's content census is not ready: ObjectDB is not populated yet.", error.Message);
        }

        // Registries with content but nothing in scope: every expected entry is missing, on the side that said so.
        var none = Reconcile(expectations, Observed(Census(items: [], prefabs: [], recipes: [])), Observed(Census("client")));
        Assert.Equal(3, none.Entries.Count(e => e.Side == CensusSide.Server && e.State == CensusState.Missing));
        Assert.False(none.Passed);

        Assert.Throws<InvalidOperationException>(() => Observed(new() { ["source"] = "content-census", ["complete"] = true })); // Fields missing.
        var wrongHash = Census(items: [new { name = Item, hash = 12345, listed = 1, resolves = Item }]);
        Assert.Contains("The game hashes MyMod_Stake to 12345", Assert.Throws<InvalidOperationException>(() => Observed(wrongHash)).Message);
    }

    [Fact] public void AnObservationFromAnotherBuildThanPinnedIsRefused()
    {
        var report = Reconcile(Expectations(), Observed(Census(md5: OtherMd5)), Observed(Census("client")));
        var server = report.Sides.Single(s => s.Side == CensusSide.Server);
        Assert.Contains($"stale build: the server's example.mymod is md5 {OtherMd5}, pinned {Md5}", server.Problem);
        Assert.DoesNotContain(report.Entries, e => e.Side == CensusSide.Server && e.State == CensusState.Present);
        Assert.Contains("stale build", Assert.Throws<InvalidOperationException>(report.RequirePassed).Message);

        // A pin may be a prefix of at least 8 hex characters, as strict pins take it; the owner missing on a side is refused too.
        Assert.True(Reconcile(Expectations(), Observed(Census()), Observed(Census("client")), serverPin: Md5[..8].ToUpperInvariant()).Passed);
        Assert.Contains("is not loaded on the client", Reconcile(Expectations(), Observed(Census()), Observed(Census("client", md5: null))).Sides[1].Problem);
        Assert.Contains("pinned absent on the client", Reconcile(Expectations(), Observed(Census()), Observed(Census("client")), clientPin: "absent").Sides[1].Problem);
    }

    [Fact] public void AnUnsupportedCheckIsNeverAPass()
    {
        // Pieces are declared in the same format but not observed by this census yet.
        var expectations = Expectations(""", "pieces": [{ "name": "MyMod_Post", "sides": ["server"] }]""");
        var report = Reconcile(expectations, Observed(Census()), Observed(Census("client")));
        var piece = Line(report, CensusSide.Server, "piece", "MyMod_Post");
        Assert.Equal(CensusState.Unsupported, piece.State);
        Assert.Equal(1, report.Entries.Count(e => e.State != CensusState.Present));
        Assert.False(report.Passed);
        Assert.Contains("server: unsupported piece MyMod_Post", Assert.Throws<InvalidOperationException>(report.RequirePassed).Message);

        // A reference the game cannot name is unsupported, not resolved.
        var unnamed = Reconcile(Expectations(), Observed(Census(recipes: [Recipe(resources: [Reference("Wood", "unsupported", 2)])])), Observed(Census("client")));
        Assert.Equal(CensusState.Unsupported, Line(unnamed, CensusSide.Server, "recipe-resource", RecipeName + "/Wood").State);
        Assert.False(unnamed.Passed);
    }

    [Fact] public void OnlyContentInTheDeclaredScopeCanBeUnexpected()
    {
        // A second item of the mod's that nobody declared, and on the client only what the server should have.
        var server = Observed(Census(items: [Entry(Item), Entry("MyMod_Extra")]));
        var report = Reconcile(Expectations(), server, Observed(Census("client")));
        Assert.Equal(CensusState.Unexpected, Line(report, CensusSide.Server, "item", "MyMod_Extra").State);
        Assert.DoesNotContain(report.Entries, e => e.Name == "Wood"); // A vanilla resource is a dependency, never content in scope.

        var serverOnly = ContentExpectations.Parse("""{ "owner": "example.mymod", "scope": ["MyMod_"], "prefabs": [{ "name": "MyMod_Stake", "sides": ["server"] }] }""");
        string[] narrow = ["MyMod_"];
        var sided = ContentCensus.Reconcile(serverOnly,
            new SideObservation(CensusSide.Server, Md5, Observed(Census(items: [], recipes: [], scope: narrow))),
            new SideObservation(CensusSide.Client, Md5, Observed(Census("client", items: [], recipes: [], scope: narrow))));
        Assert.Equal(CensusState.Present, Line(sided, CensusSide.Server, "prefab", Item).State);
        Assert.Equal(CensusState.Unexpected, Line(sided, CensusSide.Client, "prefab", Item).State);
        Assert.False(sided.Passed);
    }

    [Fact] public void ExpectationsAreRefusedRatherThanGuessed()
    {
        void Refused(string json, string because) => Assert.Contains(because, Assert.Throws<ArgumentException>(() => ContentExpectations.Parse(json)).Message);
        Refused("""{ "owner": "m", "scope": ["MyMod_"], "items": [{ "name": "Wood", "sides": ["server"] }] }""", "outside the scope");
        Refused("""{ "owner": "m", "scope": ["MyMod_"], "items": [{ "name": "MyMod_A", "sides": ["host"] }] }""", "\"server\" or \"client\"");
        Refused("""{ "owner": "m", "scope": ["MyMod_"], "items": [{ "name": "MyMod_A" }] }""", "Say which sides");
        Refused("""{ "owner": "m", "scope": ["MyMod_"], "items": [] }""", "at least one expected entry");
        Refused("""{ "owner": "m", "scope": ["MyMod_"], "items": [{ "name": "MyMod_A", "sides": ["server"], "station": "forge" }] }""", "Unknown key \"station\" in a item");
        Refused("""{ "owner": "m", "scope": ["MyMod_"], "items": [{ "name": "MyMod_A", "sides": ["server"] }, { "name": "MyMod_A", "sides": ["client"] }] }""", "declared twice");
        Refused("""{ "owner": "m", "scope": ["MyMod_"], "things": [] }""", "Unknown key \"things\"");
        Refused("""{ "scope": ["MyMod_"], "items": [{ "name": "MyMod_A", "sides": ["server"] }] }""", "owner");
        Refused("""{ "owner": "m", "scope": ["My Mod"], "items": [{ "name": "MyMod_A", "sides": ["server"] }] }""", "each one word");
        Refused("""{ "owner": "m", "scope": ["MyMod_"], "recipes": [{ "name": "MyMod_R", "sides": ["server"], "resources": ["Wood", "Wood"] }] }""", "each resource once");
        var parsed = Expectations();
        Assert.Equal(3, parsed.Entries.Count);
        Assert.Equal(new[] { "Wood" }, parsed.Entries.Single(e => e.Kind == "recipe").Resources!);
    }
}
