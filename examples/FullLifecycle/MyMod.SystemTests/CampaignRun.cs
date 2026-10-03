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

/// <summary>
/// What a campaign scenario gets from the runner: the plan, the started owned server, and how to restart it, open a
/// client, wait for the server's socket and read the live logs. <c>Program.cs</c> builds it from the pinned runner's
/// context; the integration tests build it from scripted replies.
/// </summary>
public sealed class CampaignRun
{
    public required LifecyclePlan Plan { get; init; }
    public required GameActor Server { get; init; }
    /// <summary>Restarts only the owned server and returns its new actor.</summary>
    public required Func<GameActor> RestartServer { get; init; }
    /// <summary>
    /// Opens a client of the plan. With a subdirectory, that client's command record and logs go there, so a second client
    /// in one run never overwrites the first one's evidence.
    /// </summary>
    public required Func<ClientRunPlan, string?, ClientSession> OpenClient { get; init; }
    /// <summary>Opens one named profile client on its own host and Steam lease; used while both clients remain connected.</summary>
    public Func<ClientRunPlan, string, ClientSession> OpenProfileClient { get; init; } = (_, _) =>
        throw new ArgumentException("This run has no named client profile.");
    /// <summary>Waits until the given server accepts game connections.</summary>
    public required Action<GameActor> WaitUntilJoinable { get; init; }
    public required ScenarioReport Report { get; init; }
    public required string Output { get; init; }
    /// <summary>The pinned runner's environment profile, when a client is hosted elsewhere.</summary>
    public EnvironmentProfile? Profile { get; init; }
    /// <summary>The owned server's live BepInEx log for this boot, or null when this runner cannot read it.</summary>
    public Func<string?> ServerLog { get; init; } = () => null;
    /// <summary>A client's live BepInEx log, when it is owned on this machine; null for an attached client.</summary>
    public Func<ClientRunPlan, string?> ClientLog { get; init; } = _ => null;
    /// <summary>The crossplay server's lobby (<see cref="CrossplayServer.WaitForLobby"/>), for the crossplay scenario.</summary>
    public Func<GameActor, CrossplayLobby>? Lobby { get; init; }
    public CancellationToken Cancellation { get; init; }
    /// <summary>How long the player must stand still before a teleport; tests pass zero.</summary>
    public TimeSpan? SettleFor { get; init; }
    /// <summary>How often the scenarios' observation waits re-read; tests pass a few milliseconds.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>Runs a campaign plan's scenario. A control run that failed its check as expected ends here, passing.</summary>
public static class CampaignScenarios
{
    public static void Run(CampaignRun run)
    {
        var plan = run.Plan;
        try
        {
            switch (plan.Scenario)
            {
                case LifecyclePlan.WorldScenario: LifecycleWorldScenario.Run(run); break;
                case LifecyclePlan.VanillaClientScenario: VanillaClientScenario.Run(run); break;
                case LifecyclePlan.SyncedConfigScenario: SyncedConfigScenario.Run(run); break;
                case LifecyclePlan.RefusedJoinScenario: RefusedJoinScenario.Run(run); break;
                case LifecyclePlan.ContentCensusScenario: ContentCensusScenario.Run(run); break;
                case LifecyclePlan.ReviewCaptureScenarioName: ReviewCaptureScenario.Run(run); break;
                case LifecyclePlan.AreaObjectsScenarioName: AreaObjectsScenario.Run(run); break;
                case LifecyclePlan.OwnershipHandoffScenario: OwnershipHandoffScenario.Run(run); break;
                case LifecyclePlan.CrossplayScenario:
                    // The dry-site lifecycle, joined through each boot's crossplay lobby instead of the server's address.
                    DrySiteScenario.Run(plan, run.Server, run.RestartServer, () => run.OpenClient(plan.Client!, null), run.WaitUntilJoinable,
                        run.Report, run.Output, run.Cancellation, run.SettleFor, run.Lobby ?? throw new ArgumentException("The crossplay scenario needs the server's lobby."));
                    break;
                default: throw new ArgumentException($"{plan.Scenario} is not a campaign scenario.");
            }
        }
        catch (ControlConcluded concluded) when (concluded.Control == plan.Control)
        {
            run.Report.Provenance["control"] = concluded.Control.Name + ": failed its check as expected";
        }
    }
}

/// <summary>Steps the campaign scenarios share, with the same names as <see cref="DrySiteScenario"/>'s.</summary>
public static class CampaignSteps
{
    /// <summary>Mark only a staged disposable local character as cheated before CLI cheat-classified checks.</summary>
    public static void AcknowledgeLocalCheats(GameActor client)
    {
        var reply = client.Execute("cli_acknowledge_local_cheats");
        if (!reply.Output.Contains("OK: localCharacterCheated=True"))
            throw new InvalidOperationException("The disposable client's cheat acknowledgement did not take effect: " + string.Join(" | ", reply.Output));
    }

    /// <summary>Every patch MyMod declares is applied on the server (the adapter's census).</summary>
    public static void ModPatchesApplied(GameActor server, ScenarioReport report, string side = "server") =>
        report.Step($"{side}: the mod's Harmony patches are applied", () =>
            HarmonyCensus.Read(server, Capabilities.Harmony, LifecyclePlan.ModPlugin).Check(LifecyclePlan.ModPlugin, DrySiteScenario.Patches).RequireApplied());

    /// <summary>No marker before; the mod marks the dry site and refuses the wet one, each asked once; the server shows it.</summary>
    public static void MarkSites(LifecyclePlan plan, GameActor server, ScenarioReport report, string side = "server")
    {
        report.Step("no marker at either site before the mod acts", () => { DrySiteScenario.RequireServerMarkers(server, plan.DrySite, 0); DrySiteScenario.RequireServerMarkers(server, plan.WetSite, 0); });
        report.Step("the mod marks the dry site", () => DrySiteScenario.RequireReply(server.Execute(DrySiteScenario.Mark(plan.DrySite)), "OK: marked "));
        report.Step("the mod refuses the wet site", () => DrySiteScenario.RequireReply(server.Execute(DrySiteScenario.Mark(plan.WetSite)), "REFUSED: "));
        report.Step($"{side}: one marker at the dry site, none at the wet site", () => RequireMarkers(server, plan, dry: 1));
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
