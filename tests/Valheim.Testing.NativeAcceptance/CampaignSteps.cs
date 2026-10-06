using System.Globalization;
using Valheim.Testing.Game;

namespace Valheim.Testing.NativeAcceptance;

/// <summary>The test adapter's capabilities (AcceptanceMod.Adapter), by path.</summary>
public static class Capabilities
{
    public const string Harmony = LifecyclePlan.HarmonyCapability, Zones = "acceptancemod.testing/zones", CustomData = "acceptancemod.testing/custom-data",
        GlobalKeys = "acceptancemod.testing/globalkeys", GlobalKeyChange = "acceptancemod.testing/globalkey", Config = "acceptancemod.testing/config",
        UnresolvedPrefabs = "acceptancemod.testing/unresolved-prefabs", DungeonRooms = "acceptancemod.testing/dungeon-rooms", Markers = "acceptancemod.testing/markers",
        ContentCensus = "acceptancemod.testing/content-census";
    public const string MarkerOwner = "acceptancemod.testing/marker-owner", MarkerOwnerWait = "acceptancemod.testing/marker-owner-wait",
        MarkerOwnerClaim = "acceptancemod.testing/marker-owner-claim";
    /// <summary>The ghost-protection scenario's commands (AcceptanceMod.Adapter's AiWatch, #261).</summary>
    public const string AiWatch = "acceptancemod.testing/ai-watch", CreatureSpawn = "acceptancemod.testing/creature-spawn",
        CreatureRemove = "acceptancemod.testing/creature-remove", GhostMode = "acceptancemod.testing/ghost-mode";
    /// <summary>The field-only-state control's own commands (Controls/FieldOnlyState).</summary>
    public const string FieldStateSet = "acceptancemodcontrol.fieldstate/set", FieldStateRead = "acceptancemodcontrol.fieldstate/read";
}

/// <summary>Steps the campaign scenarios share, with the same names as <see cref="DrySiteScenario"/>'s.</summary>
public static class CampaignSteps
{
    /// <summary>No marker before; the mod marks the dry site and refuses the wet one, each asked once; the server shows it.</summary>
    public static void MarkSites(AcceptancePlan plan, GameActor server, ScenarioReport report, string side = "server")
    {
        report.Step("no marker at either site before the mod acts", () => { DrySiteScenario.RequireServerMarkers(server, plan.DrySite, 0); DrySiteScenario.RequireServerMarkers(server, plan.WetSite, 0); });
        report.Step("the mod marks the dry site", () => server.Execute(DrySiteScenario.Mark(plan.DrySite)).RequireLine("OK: marked ", "AcceptanceMod did not mark the dry site"));
        report.Step("the mod refuses the wet site", () => server.Execute(DrySiteScenario.Mark(plan.WetSite)).RequireLine("REFUSED: ", "AcceptanceMod did not refuse the wet site"));
        report.Step($"{side}: one marker at the dry site, none at the wet site", () => RequireMarkers(server, plan, dry: 1));
    }

    /// <summary>The server lists exactly <paramref name="expected"/> connected peers (<see cref="PlayerPlacement.PeerCount"/>).</summary>
    public static void RequirePeers(GameActor server, int expected)
    {
        int connected = PlayerPlacement.PeerCount(server);
        if (connected != expected) throw new InvalidOperationException($"Expected {expected} connected peer(s); the server lists {connected}.");
    }

    public static void RequireMarkers(GameActor server, AcceptancePlan plan, int dry)
    {
        DrySiteScenario.RequireServerMarkers(server, plan.DrySite, dry);
        DrySiteScenario.RequireServerMarkers(server, plan.WetSite, 0);
    }

    /// <summary>
    /// The adapter's marker observation: exactly one marker at the site, instantiated here, carrying AcceptanceMod's saved label.
    /// </summary>
    public static void RequireLabelledMarker(GameActor actor, Site site)
    {
        var data = actor.ObserveComplete(actor.RequireCapability(Capabilities.Markers), "acceptancemod-markers", Number(site.X), Number(site.Z),
            Number(DrySiteScenario.MarkerRadius)).Data;
        var markers = data.GetProperty("markers").EnumerateArray().ToArray();
        if (markers.Length != 1) throw new InvalidOperationException($"The {actor.Name} knows {markers.Length} marker(s) at ({site.X}, {site.Z}); expected 1.");
        if (!markers[0].GetProperty("instance").GetBoolean()) throw new InvalidOperationException($"The marker at ({site.X}, {site.Z}) has no instance on the {actor.Name}.");
        string label = markers[0].GetProperty("label").GetString() ?? "";
        if (label != DryLabel) throw new InvalidOperationException($"The marker at ({site.X}, {site.Z}) carries the label \"{label}\", not AcceptanceMod's \"{DryLabel}\".");
    }

    /// <summary>The label AcceptanceMod saves on its marker (<c>Plugin.DryLabel</c> in AcceptanceMod).</summary>
    public const string DryLabel = "dry-site";
    public static HeightExpectation At(Site site) => new(site.X, site.Z, site.Ground);
    public static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    /// <summary>A short random word for a value only this run writes, so what comes back cannot be left from an earlier run.</summary>
    public static string RunWord(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];
}
