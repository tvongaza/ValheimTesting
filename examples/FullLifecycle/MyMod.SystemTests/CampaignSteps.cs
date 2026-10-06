using System.Globalization;
using Valheim.Testing.Game;

namespace MyMod.SystemTests;

/// <summary>The test adapter's capabilities (MyMod.TestAdapter), by path.</summary>
public static class Capabilities
{
    public const string Harmony = "mymod.testing/harmony", Zones = "mymod.testing/zones", CustomData = "mymod.testing/custom-data",
        GlobalKeys = "mymod.testing/globalkeys", GlobalKeyChange = "mymod.testing/globalkey", Config = "mymod.testing/config",
        UnresolvedPrefabs = "mymod.testing/unresolved-prefabs", DungeonRooms = "mymod.testing/dungeon-rooms", Markers = "mymod.testing/markers",
        ContentCensus = "mymod.testing/content-census";
    public const string MarkerOwner = "mymod.testing/marker-owner", MarkerOwnerWait = "mymod.testing/marker-owner-wait",
        MarkerOwnerClaim = "mymod.testing/marker-owner-claim";
    /// <summary>The field-only-state control's own commands (Controls/FieldOnlyState).</summary>
    public const string FieldStateSet = "mymodcontrol.fieldstate/set", FieldStateRead = "mymodcontrol.fieldstate/read";
}

/// <summary>Steps the campaign scenarios share, with the same names as <see cref="DrySiteScenario"/>'s.</summary>
public static class CampaignSteps
{
    /// <summary>No marker before; the mod marks the dry site and refuses the wet one, each asked once; the server shows it.</summary>
    public static void MarkSites(LifecyclePlan plan, GameActor server, ScenarioReport report, string side = "server")
    {
        report.Step("no marker at either site before the mod acts", () => { DrySiteScenario.RequireServerMarkers(server, plan.DrySite, 0); DrySiteScenario.RequireServerMarkers(server, plan.WetSite, 0); });
        report.Step("the mod marks the dry site", () => server.Execute(DrySiteScenario.Mark(plan.DrySite)).RequireLine("OK: marked ", "MyMod did not mark the dry site"));
        report.Step("the mod refuses the wet site", () => server.Execute(DrySiteScenario.Mark(plan.WetSite)).RequireLine("REFUSED: ", "MyMod did not refuse the wet site"));
        report.Step($"{side}: one marker at the dry site, none at the wet site", () => RequireMarkers(server, plan, dry: 1));
    }

    /// <summary>The server lists exactly <paramref name="expected"/> connected peers (<see cref="PlayerPlacement.PeerCount"/>).</summary>
    public static void RequirePeers(GameActor server, int expected)
    {
        int connected = PlayerPlacement.PeerCount(server);
        if (connected != expected) throw new InvalidOperationException($"Expected {expected} connected peer(s); the server lists {connected}.");
    }

    public static void RequireMarkers(GameActor server, LifecyclePlan plan, int dry)
    {
        DrySiteScenario.RequireServerMarkers(server, plan.DrySite, dry);
        DrySiteScenario.RequireServerMarkers(server, plan.WetSite, 0);
    }

    /// <summary>
    /// The adapter's marker observation: exactly one marker at the site, instantiated here, carrying MyMod's saved label.
    /// </summary>
    public static void RequireLabelledMarker(GameActor actor, Site site)
    {
        var data = actor.ObserveComplete(actor.RequireCapability(Capabilities.Markers), "mymod-markers", Number(site.X), Number(site.Z),
            Number(DrySiteScenario.MarkerRadius)).Data;
        var markers = data.GetProperty("markers").EnumerateArray().ToArray();
        if (markers.Length != 1) throw new InvalidOperationException($"The {actor.Name} knows {markers.Length} marker(s) at ({site.X}, {site.Z}); expected 1.");
        if (!markers[0].GetProperty("instance").GetBoolean()) throw new InvalidOperationException($"The marker at ({site.X}, {site.Z}) has no instance on the {actor.Name}.");
        string label = markers[0].GetProperty("label").GetString() ?? "";
        if (label != DryLabel) throw new InvalidOperationException($"The marker at ({site.X}, {site.Z}) carries the label \"{label}\", not MyMod's \"{DryLabel}\".");
    }

    /// <summary>The label MyMod saves on its marker (<c>Plugin.DryLabel</c> in MyMod).</summary>
    public const string DryLabel = "dry-site";
    public static HeightExpectation At(Site site) => new(site.X, site.Z, site.Ground);
    public static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    /// <summary>A short random word for a value only this run writes, so what comes back cannot be left from an earlier run.</summary>
    public static string RunWord(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];
}
