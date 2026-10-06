using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using MyMod.IntegrationTests;
using MyMod.SystemTests;
using Valheim.Testing.NativeAcceptance;
using Xunit;
using Valheim.Testing.GameSessions;

namespace Valheim.Testing.NativeAcceptance.Tests;

/// <summary>
/// The native campaign's plan rules, from plan files as an operator writes them: each scenario's valid plan is read, and
/// each of its refusals happens before anything is copied or launched. The pinned sources need not exist to validate a plan.
/// </summary>
public sealed class CampaignPlanTests : IDisposable
{
    private const string Mod = CampaignWorld.Md5Mod, Adapter = CampaignWorld.Md5Adapter, Cli = CampaignWorld.Md5Cli;
    private readonly string _directory = Directory.CreateTempSubdirectory("mymod-campaign-plans-").FullName;
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static JsonObject Client(string mod = Mod, int port = 5556, bool crossplay = false)
    {
        var client = new JsonObject
        {
            ["mode"] = "attach", ["port"] = port, ["character"] = "Tester",
            ["pins"] = new JsonObject { ["valheimCLI.valheimCLI"] = Cli, [AcceptancePlan.ModPlugin] = mod, [AcceptancePlan.AdapterPlugin] = Adapter },
        };
        if (crossplay) client["crossplay"] = true; else client["join"] = "127.0.0.1:2456";
        return client;
    }

    private JsonObject Owned(JsonObject client, string name)
    {
        client["mode"] = "owned";
        client["character"] = name;
        client["install"] = Path.Combine(_directory, "install-" + name);
        client["installPins"] = new JsonObject { ["game"] = new string('a', 64), ["loader"] = new string('b', 64), ["patchers"] = new string('c', 64) };
        return client;
    }

    // A valid plan of the scenario, as the sample plans have it; tests change one thing each.
    private JsonObject Plan(string scenario)
    {
        string hash = new('a', 64);
        var plan = new JsonObject
        {
            ["scenario"] = scenario,
            ["runtime"] = new JsonObject { ["source"] = Path.Combine(_directory, "runtime"), ["sha256"] = new JsonObject { ["valheim_server.exe"] = hash } },
            ["world"] = new JsonObject { ["source"] = Path.Combine(_directory, "world"), ["sha256"] = new JsonObject { ["adminlist.txt"] = hash } },
            ["arguments"] = new JsonArray("-batchmode", "-nographics", "-port", "2466", "-savedir", "{world}", "-public", "0"),
            ["pins"] = new JsonObject { ["worlduid"] = "4242", ["valheimCLI.valheimCLI"] = Cli, [AcceptancePlan.ModPlugin] = Mod, [AcceptancePlan.AdapterPlugin] = Adapter },
            ["runtimePins"] = new JsonObject { ["game"] = new string('c', 64), ["loader"] = new string('d', 64), ["patchers"] = new string('e', 64) },
            ["client"] = Client(),
        };
        if (scenario is AcceptancePlan.WorldScenario or AcceptancePlan.VanillaClientScenario or AcceptancePlan.CrossplayScenario or AcceptancePlan.OwnershipHandoffScenario)
        {
            plan["drySite"] = new JsonObject { ["x"] = 100, ["z"] = -40, ["ground"] = 42.5 };
            plan["wetSite"] = new JsonObject { ["x"] = 400, ["z"] = 300, ["ground"] = 22 };
            plan["arrival"] = new JsonObject { ["x"] = 105, ["z"] = -40, ["ground"] = 42.3 };
        }
        if (scenario == AcceptancePlan.AreaObjectsScenarioName)
            plan["arrival"] = new JsonObject { ["x"] = 105, ["z"] = -40, ["ground"] = 42.3 };
        switch (scenario)
        {
            case AcceptancePlan.WorldScenario:
                plan["environment"] = new JsonObject { [AcceptancePlan.FixturesVariable] = "1" };
                plan["away"] = new JsonObject { ["x"] = 420, ["z"] = -40, ["ground"] = 36 };
                plan["globalKey"] = "defeated_eikthyr";
                plan["dungeon"] = new JsonObject { ["x"] = 150, ["z"] = -40 };
                plan["logout"] = new JsonObject { ["charactersDirectory"] = Path.Combine(_directory, "characters_local") };
                break;
            case AcceptancePlan.VanillaClientScenario: plan["client"]!["pins"]![AcceptancePlan.ModPlugin] = "absent"; break;
            case AcceptancePlan.SyncedConfigScenario: plan["newGreeting"] = "goodbye"; break;
            case AcceptancePlan.RefusedJoinScenario: plan["refusedClient"] = Client(CampaignWorld.Md5Mismatched, port: 5557); break;
            case AcceptancePlan.CrossplayScenario:
                plan["crossplay"] = true;
                plan["client"] = Client("absent", crossplay: true);
                break;
            case AcceptancePlan.OwnershipHandoffScenario:
                plan["client"] = Owned(Client(port: 5556), "ClientA");
                plan["secondClient"] = Owned(Client(port: 5557), "ClientB");
                foreach (var entry in new[] { plan["client"]!, plan["secondClient"]! })
                    entry["capabilities"] = new JsonArray(Capabilities.Markers, Capabilities.MarkerOwner,
                        Capabilities.MarkerOwnerWait, Capabilities.MarkerOwnerClaim, "valheim.world/terrain", "valheim.world/player-support-wait", "valheim.world/player-support", Valheim.Testing.Game.CliCapabilities.TeleportSignals);
                plan["secondArrival"] = new JsonObject { ["x"] = 100, ["z"] = -32, ["ground"] = 42.4 };
                break;
            case AcceptancePlan.ThreeActorScenario:
                plan["client"] = Owned(Client(port: 5556), "ClientA");
                plan["secondClient"] = Owned(Client(port: 5557), "ClientB");
                plan["client"]!["capabilities"] = new JsonArray(Capabilities.Markers);
                plan["secondClient"]!["capabilities"] = new JsonArray(Capabilities.Markers);
                break;
        }
        return plan;
    }

    private AcceptancePlan Read(JsonObject plan)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, plan.ToJsonString());
        return AcceptancePlan.ReadValidated(path);
    }
    private void Refused(JsonObject plan, string because)
    {
        var error = Assert.Throws<ArgumentException>(() => Read(plan));
        Assert.Contains(because, error.Message);
    }

    [Theory]
    [InlineData(AcceptancePlan.WorldScenario)] [InlineData(AcceptancePlan.VanillaClientScenario)] [InlineData(AcceptancePlan.SyncedConfigScenario)]
    [InlineData(AcceptancePlan.RefusedJoinScenario)] [InlineData(AcceptancePlan.CrossplayScenario)] [InlineData(AcceptancePlan.ContentCensusScenario)]
    [InlineData(AcceptancePlan.AreaObjectsScenarioName)] [InlineData(AcceptancePlan.OwnershipHandoffScenario)] [InlineData(AcceptancePlan.ThreeActorScenario)]
    public void EachScenariosValidPlanIsRead(string scenario) => Assert.Equal(scenario, Read(Plan(scenario)).Scenario);

    // Every refusal of the suite's plan rules: a valid plan of the scenario, one change, and words the refusal must contain.
    // Each is refused when the plan is read, before anything is copied or launched.
    private const string ControlMd5 = "55555555555555555555555555555555";
    private static readonly Dictionary<string, (Func<CampaignPlanTests, JsonObject> Start, Action<JsonObject, CampaignPlanTests> Change, string Because)> Refusals = BuildRefusals();
    public static TheoryData<string> RefusalNames => new(Refusals.Keys);
    // A valid plan of the scenario (Plan), the start of a row.
    private static Func<CampaignPlanTests, JsonObject> Valid(string scenario) => tests => tests.Plan(scenario);

    private static Dictionary<string, (Func<CampaignPlanTests, JsonObject>, Action<JsonObject, CampaignPlanTests>, string)> BuildRefusals()
    {
        // Add, never the indexer: a row name used twice throws instead of dropping a case.
        var rows = new Dictionary<string, (Func<CampaignPlanTests, JsonObject>, Action<JsonObject, CampaignPlanTests>, string)>
        {
            // ownership-handoff: players that could stack at the landing points, ambiguous clients.
            { "handoff: second arrival on the first (105)", (Valid(AcceptancePlan.OwnershipHandoffScenario), (plan, _) => plan["secondArrival"] = new JsonObject { ["x"] = 105, ["z"] = -40, ["ground"] = 42.3 }, "at least 3 m apart") },
            { "handoff: second arrival 1 m from the first (106)", (Valid(AcceptancePlan.OwnershipHandoffScenario), (plan, _) => plan["secondArrival"] = new JsonObject { ["x"] = 106, ["z"] = -40, ["ground"] = 42.3 }, "at least 3 m apart") },
            // Separate hosts, distinct signed-in Steam identities and CLI ports are the inventory's assignment (EnvironmentInventoryTests).
            { "handoff: one character twice", (Valid(AcceptancePlan.OwnershipHandoffScenario), (plan, _) => plan["secondClient"]!["character"] = "ClientA", "distinct disposable character") },
            { "handoff: second client without the adapter", (Valid(AcceptancePlan.OwnershipHandoffScenario), (plan, _) => plan["secondClient"]!["pins"]!.AsObject().Remove(AcceptancePlan.AdapterPlugin), "Pin " + AcceptancePlan.AdapterPlugin) },
            // area-objects: an ambiguous centre.
            { "area-objects: half-metre arrival", (Valid(AcceptancePlan.AreaObjectsScenarioName), (plan, _) => plan["arrival"]!["x"] = 105.5f, "whole-metre coordinates") },
            // dry-site-server's patch reload (#30): what would make it prove nothing.
            { "patch reload: ScriptEngine not pinned", (tests => tests.PatchReloadPlan(), (plan, _) => plan["pins"]!.AsObject().Remove(PatchReloadSettings.ScriptEngine), "pin " + PatchReloadSettings.ScriptEngine) },
            { "patch reload: the probe pinned", (tests => tests.PatchReloadPlan(), (plan, _) => plan["pins"]![PatchReloadScenario.Probe] = new string('f', 32), "Do not pin " + PatchReloadScenario.Probe) },
            { "patch reload: one build twice", (tests => tests.PatchReloadPlan(), (plan, _) => plan["patchReload"]!["revisionB"] = plan["patchReload"]!["revisionA"]!.GetValue<string>(), "the same build") },
            { "patch reload: a relative probe path", (tests => tests.PatchReloadPlan(), (plan, _) => plan["patchReload"]!["revisionA"] = "probe-a.dll", "patchReload.revisionA is the full path") },
            // Fields of another scenario.
            { "synced-config: patchReload", (Valid(AcceptancePlan.SyncedConfigScenario), (plan, tests) => plan["patchReload"] = tests.PatchReloadPlan()["patchReload"]!.DeepClone(), "patchReload are for the dry-site-server scenario") },
            { "synced-config: globalKey", (Valid(AcceptancePlan.SyncedConfigScenario), (plan, _) => plan["globalKey"] = "defeated_eikthyr", "are for the lifecycle-world scenario") },
            { "lifecycle-world: newGreeting", (Valid(AcceptancePlan.WorldScenario), (plan, _) => plan["newGreeting"] = "goodbye", "newGreeting are for the synced-config scenario") },
            { "vanilla-client: crossplay", (Valid(AcceptancePlan.VanillaClientScenario), (plan, _) => plan["crossplay"] = true, "crossplay are for the crossplay scenario") },
            { "synced-config: drySite", (Valid(AcceptancePlan.SyncedConfigScenario), (plan, _) => plan["drySite"] = new JsonObject { ["x"] = 100, ["z"] = -40, ["ground"] = 42.5 }, "marks nothing") },
            // lifecycle-world: what would make it prove nothing.
            { "lifecycle-world: no fixtures variable", (Valid(AcceptancePlan.WorldScenario), (plan, _) => plan.Remove("environment"), AcceptancePlan.FixturesVariable) },
            { "lifecycle-world: away four zones off", (Valid(AcceptancePlan.WorldScenario), (plan, _) => plan["away"] = new JsonObject { ["x"] = 356, ["z"] = -40, ["ground"] = 36 }, "at least 5 zones (320 m") }, // Four zones: still loaded.
            { "lifecycle-world: client without MyMod", (Valid(AcceptancePlan.WorldScenario), (plan, _) => plan["client"]!["pins"]![AcceptancePlan.ModPlugin] = "absent", "the check needs the server's MyMod on the client") },
            { "lifecycle-world: a key with spaces", (Valid(AcceptancePlan.WorldScenario), (plan, _) => plan["globalKey"] = "Defeated Eikthyr", "one lower-case key") },
            { "lifecycle-world: a dungeon out of reach", (Valid(AcceptancePlan.WorldScenario), (plan, _) => plan["dungeon"] = new JsonObject { ["x"] = 400, ["z"] = -40 }, "within two zones") },
            { "lifecycle-world: a cloud characters folder", (Valid(AcceptancePlan.WorldScenario), (plan, tests) => plan["logout"] = new JsonObject { ["charactersDirectory"] = Path.Combine(tests._directory, "characters") }, "characters_local") },
            { "lifecycle-world: no client", (Valid(AcceptancePlan.WorldScenario), (plan, _) => plan.Remove("client"), "add the client section") },
            // vanilla-client: the client lacks MyMod and has the server's adapter.
            { "vanilla-client: client with MyMod", (Valid(AcceptancePlan.VanillaClientScenario), (plan, _) => plan["client"]!["pins"]![AcceptancePlan.ModPlugin] = Mod, "must pin example.mymod=absent") },
            { "vanilla-client: another adapter build", (Valid(AcceptancePlan.VanillaClientScenario), (plan, _) => plan["client"]!["pins"]![AcceptancePlan.AdapterPlugin] = new string('9', 32), "with the server's MD5") },
            // synced-config: one new word.
            { "synced-config: no newGreeting", (Valid(AcceptancePlan.SyncedConfigScenario), (plan, _) => plan.Remove("newGreeting"), "Set newGreeting") },
            { "synced-config: two words", (Valid(AcceptancePlan.SyncedConfigScenario), (plan, _) => plan["newGreeting"] = "good bye", "Set newGreeting") },
            // refused-join: the refused client runs another build than the server.
            { "refused-join: the server's own build", (Valid(AcceptancePlan.RefusedJoinScenario), (plan, _) => plan["refusedClient"] = Client(Mod, port: 5557), "pins the server's own MyMod build") },
            { "refused-join: no MyMod", (Valid(AcceptancePlan.RefusedJoinScenario), (plan, _) => plan["refusedClient"] = Client("absent", port: 5557), "runs another MyMod build") },
            { "refused-join: one CLI port for both", (Valid(AcceptancePlan.RefusedJoinScenario), (plan, _) => plan["refusedClient"] = Client(CampaignWorld.Md5Mismatched), "different ValheimCLI ports") },
            { "refused-join: a status that is no refusal", (Valid(AcceptancePlan.RefusedJoinScenario), (plan, _) => plan["expectedRefusal"] = "Connected", "one of the game's Error statuses") },
            { "refused-join: an unknown status", (Valid(AcceptancePlan.RefusedJoinScenario), (plan, _) => plan["expectedRefusal"] = "ErrorSomethingNew", "expectedRefusal: Unknown connection status") },
            // crossplay: on both sides, and no password.
            { "crossplay: client joins by address", (Valid(AcceptancePlan.CrossplayScenario), (plan, _) => plan["client"] = Client("absent"), "in its client section") },
            { "crossplay: a password", (Valid(AcceptancePlan.CrossplayScenario), (plan, _) => { plan["arguments"]!.AsArray().Add("-password"); plan["arguments"]!.AsArray().Add("secret1"); }, "without -password") },
            { "crossplay: no port argument", (Valid(AcceptancePlan.CrossplayScenario), (plan, _) => plan["arguments"] = new JsonArray("-batchmode", "-nographics", "-savedir", "{world}"), "names its game port") }, // The toolkit's rule.
            // Controls: opt-in, one scenario and side each.
            { "control: pinned without a control run", (Valid(AcceptancePlan.WorldScenario), (plan, _) => plan["pins"]!["example.mymod.control.missingtarget"] = ControlMd5, "Name it in expectFailure (\"missing-harmony-target\")") },
            { "control: named, not installed", (Valid(AcceptancePlan.WorldScenario), (plan, _) => plan["expectFailure"] = ControlPlugins.FieldOnlyState, "pin example.mymod.control.fieldonlystate there") },
            { "control: on the wrong side", (Valid(AcceptancePlan.WorldScenario), (plan, _) => { plan["expectFailure"] = ControlPlugins.FieldOnlyState; plan["pins"]!["example.mymod.control.fieldonlystate"] = ControlMd5; }, "on the client") },
            { "control: in the wrong scenario", (Valid(AcceptancePlan.VanillaClientScenario), (plan, _) => { plan["expectFailure"] = ControlPlugins.FieldOnlyState; plan["client"]!["pins"]!["example.mymod.control.fieldonlystate"] = ControlMd5; }, "belongs to the lifecycle-world scenario") },
            { "control: an unknown name", (Valid(AcceptancePlan.WorldScenario), (plan, _) => plan["expectFailure"] = "some-control", "expectFailure names no control") },
            { "control: two at once", (Valid(AcceptancePlan.WorldScenario), (plan, _) => { plan["expectFailure"] = ControlPlugins.FieldOnlyState; plan["client"]!["pins"]!["example.mymod.control.fieldonlystate"] = ControlMd5; plan["client"]!["pins"]!["example.mymod.control.suppressedsave"] = ControlMd5; }, "exactly the control it names") },
            { "control: on a dry-site lifecycle", (tests => tests.DrySitePlan(), (plan, _) => plan["expectFailure"] = ControlPlugins.FieldOnlyState, "belongs to the lifecycle-world scenario") },
            // content-census: the client's registries are its own, so a client without MyMod, or with another build, cannot stand in.
            { "content-census: client without MyMod", (Valid(AcceptancePlan.ContentCensusScenario), (plan, _) => plan["client"]!["pins"]![AcceptancePlan.ModPlugin] = "absent", "the check needs the server's MyMod on the client") },
            { "content-census: client with another build", (Valid(AcceptancePlan.ContentCensusScenario), (plan, _) => plan["client"]!["pins"]![AcceptancePlan.ModPlugin] = CampaignWorld.Md5Mismatched, "the check needs the server's MyMod on the client") },
            { "content-census: no client", (Valid(AcceptancePlan.ContentCensusScenario), (plan, _) => plan.Remove("client"), "add the client section") },
            { "content-census: drySite", (Valid(AcceptancePlan.ContentCensusScenario), (plan, _) => plan["drySite"] = new JsonObject { ["x"] = 100, ["z"] = -40, ["ground"] = 42.5 }, "marks nothing") },
            { "content-census: its build control in another scenario", (Valid(AcceptancePlan.SyncedConfigScenario), (plan, _) => plan["expectFailure"] = ControlPlugins.OmittedRecipe, "belongs to the content-census scenario") },
            { "content-census: a control plugin beside its build control", (Valid(AcceptancePlan.ContentCensusScenario), (plan, _) => { plan["expectFailure"] = ControlPlugins.OmittedRecipe; plan["pins"]!["example.mymod.control.missingtarget"] = new string('5', 32); }, "remove the control plugin example.mymod.control.missingtarget") },
            // Every client section runs with strict pins, the suite's two extra ones included (LifecyclePlan's rule over AcceptancePlan's sections).
            { "strict pins: second client unpinned", (Valid(AcceptancePlan.OwnershipHandoffScenario), (plan, _) => plan["secondClient"]!["pinning"] = "none", "strict pins only") },
            { "strict pins: refused client unpinned", (Valid(AcceptancePlan.RefusedJoinScenario), (plan, _) => plan["refusedClient"]!["pinning"] = "none", "strict pins only") },
        };
        // ownership-handoff: every capability the toolkit's arrival needs, and the loaded-ground reading, before launch.
        foreach (string capability in Valheim.Testing.Game.PlayerPlacement.ArrivalCapabilities.Append("valheim.world/terrain"))
            rows.Add("handoff: second client without " + capability, (Valid(AcceptancePlan.OwnershipHandoffScenario), (plan, _) =>
            {
                var capabilities = plan["secondClient"]!["capabilities"]!.AsArray();
                capabilities.Remove(capabilities.Single(c => c!.GetValue<string>() == capability));
            }, capability));
        foreach (string removed in new[] { "eventDrivenArrival", "fastTestTeleports" })
            rows.Add("handoff: removed client field " + removed, (Valid(AcceptancePlan.OwnershipHandoffScenario), (plan, _) => plan["client"]![removed] = true, removed + " was removed (ValheimTesting #299)"));
        return rows;
    }

    [Theory, MemberData(nameof(RefusalNames))]
    public void ARefusedPlanSaysWhy(string refusal)
    {
        var (start, change, because) = Refusals[refusal];
        var plan = start(this);
        Read(plan.DeepClone().AsObject()); // The row starts from a valid plan, so the refusal comes from its change.
        change(plan, this);
        Refused(plan, because);
    }

    [Fact] public void PlansThatPassTheirScenariosRules()
    {
        Assert.NotNull(Read(PatchReloadPlan()).PatchReload);
        Assert.True(Read(PatchReloadPlan(control: true)).PatchReload!.ExpectOthersRemoved);
        var plan = Plan(AcceptancePlan.RefusedJoinScenario); plan["expectedRefusal"] = "ErrorDisconnected";
        Assert.Equal(Valheim.Testing.Game.GameConnectionStatus.ErrorDisconnected, Read(plan).RefusalStatus);
        // A client-side control run; an explicit absence is not an install.
        plan = Plan(AcceptancePlan.WorldScenario); plan["expectFailure"] = ControlPlugins.SuppressedProfileSave; plan["client"]!["pins"]!["example.mymod.control.suppressedsave"] = ControlMd5;
        Assert.Equal(ControlPlugins.SuppressedProfileSave, Read(plan).Control!.Name);
        plan = Plan(AcceptancePlan.WorldScenario); plan["client"]!["pins"]!["example.mymod.control.suppressedsave"] = "absent";
        Assert.Null(Read(plan).Control);
        // The dry-site lifecycle is no campaign scenario.
        Assert.False(Read(DrySitePlan()).IsCampaign);
    }

    // A server-side control run names its lines as expected: they fail the teardown scan by default.
    [Fact] public void AServerControlRunNamesItsLogLinesAsExpected()
    {
        var plan = Plan(AcceptancePlan.WorldScenario); plan["expectFailure"] = ControlPlugins.MissingHarmonyTarget; plan["pins"]!["example.mymod.control.missingtarget"] = ControlMd5;
        Refused(plan, "names the control's lines as expected");
        plan["logScan"] = new JsonObject { ["accesstools-not-found"] = new JsonObject { ["expected"] = new JsonArray("name OtherMethod"), ["reason"] = "the control plants it" } };
        Refused(plan, "names the control's lines as expected");
        plan["logScan"] = new JsonObject { ["accesstools-not-found"] = new JsonObject { ["expected"] = new JsonArray(ControlPlugins.MissingMethodName), ["reason"] = "the control plants it" } };
        Refused(plan, "names the control's lines as expected"); // PatchAll's error in Unity's log is not named yet
        plan["logScan"]!["harmony-undefined-target"] = new JsonObject { ["expected"] = new JsonArray(ControlPlugins.MissingPatchClass), ["reason"] = "the control plants it" };
        Assert.Equal(ControlPlugins.MissingHarmonyTarget, Read(plan).Control!.Name);
        // Making every lookup a warning would hide the rest: refused.
        plan["logScan"]!["accesstools-not-found"]!["severity"] = "Warning";
        Refused(plan, "keeps accesstools-not-found a failure");
    }

    // The omitted-recipe control is MyMod's own build, pinned where MyMod is; it belongs to the content census alone and
    // runs without any control plugin.
    [Fact] public void TheContentCensusControlIsABuild()
    {
        var plan = Plan(AcceptancePlan.ContentCensusScenario); plan["expectFailure"] = ControlPlugins.OmittedRecipe;
        var control = Read(plan).Control!;
        Assert.True(control.Build);
        Assert.Equal(ControlPlugins.OmittedRecipe, control.Name);
        plan = Plan(AcceptancePlan.ContentCensusScenario); plan["expectFailure"] = ControlPlugins.OmittedRecipe; plan["pins"]![AcceptancePlan.ModPlugin] = "any";
        Assert.ThrowsAny<ArgumentException>(() => Read(plan));
        // A normal content-census plan names no control, although MyMod is pinned.
        Assert.Null(Read(Plan(AcceptancePlan.ContentCensusScenario)).Control);
    }

    [Fact] public void TheSamplePlansAreValidPlans()
    {
        // The sample plans have placeholders for hashes and paths; with those filled in, each reads.
        string samples = Path.Combine(AppContext.BaseDirectory, "samples");
        // Discover the source inventory, not arbitrary files left in bin/ from an older build.
        var names = Directory.GetFiles(SourceSamples(), "sample-plan-*.json")
            .Select(path => Path.GetFileName(path)!).Where(name => name != "sample-plan-hosted.json").ToArray();
        Assert.Equal(10, names.Length);
        foreach (string name in names)
        {
            string file = Path.Combine(samples, name);
            var plan = JsonNode.Parse(Fill(File.ReadAllText(file)))!.AsObject();
            Assert.Equal(plan["scenario"]!.GetValue<string>(), Read(plan).Scenario);
        }
        string hosted = Path.Combine(_directory, "hosted.json");
        File.WriteAllText(hosted, Fill(File.ReadAllText(Path.Combine(samples, "sample-plan-hosted.json"))));
        Assert.Equal(HostedPlan.HostedScenarioName, HostedPlan.ReadValidated(hosted).Scenario);
    }

    [Fact] public void AReviewCapturePlanIsRefusedByTheLibrarysCaptureRuleBeforeLaunch()
    {
        // The example keeps no copy of the capture ranges: ReviewCapture.Validate refuses them when the plan is read.
        JsonObject Sample() => JsonNode.Parse(Fill(File.ReadAllText(Path.Combine(SourceSamples(), "sample-plan-review-capture.json"))))!.AsObject();
        Assert.Equal(AcceptancePlan.ReviewCaptureScenarioName, Read(Sample()).Scenario);
        var plan = Sample(); plan["capture"]!["cameraAzimuthDegrees"] = 360;
        Assert.Contains("azimuth", Assert.ThrowsAny<ArgumentException>(() => Read(plan)).Message);
        plan = Sample(); plan["capture"]!["supersize"] = 5;
        Assert.ThrowsAny<ArgumentException>(() => Read(plan));
        plan = Sample(); plan["capture"]!["weather"] = "Clear sky";
        Assert.Contains("Weather", Assert.ThrowsAny<ArgumentException>(() => Read(plan)).Message);
        plan = Sample(); plan["capture"]!["weather"] = new string('a', 65);
        Assert.Contains("Weather", Assert.ThrowsAny<ArgumentException>(() => Read(plan)).Message);
        plan = Sample(); plan["capture"]!["gameBuild"] = new string('1', 65);
        Assert.Contains("provenance", Assert.ThrowsAny<ArgumentException>(() => Read(plan)).Message);
        plan = Sample(); plan["capture"]!["gameBuild"] = "";
        Assert.Contains("provenance", Assert.ThrowsAny<ArgumentException>(() => Read(plan)).Message);
    }

    private static string SourceSamples([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "../Valheim.Testing.NativeAcceptance"));

    // Puts full paths, hashes and MD5s where the samples have placeholders ("<...>"); the builds the client runs are the server's.
    private string Fill(string text) => System.Text.RegularExpressions.Regex.Replace(text, "\"<([^\"]*)>\"", match =>
    {
        string v = match.Groups[1].Value;
        string value =
            v.Contains("characters_local", StringComparison.Ordinal) ? Path.Combine(_directory, "characters_local")
            : v.StartsWith("full path", StringComparison.Ordinal) ? Path.Combine(_directory, System.Text.RegularExpressions.Regex.Replace(v, "[^A-Za-z0-9]+", "-"))
            : v.Contains("sha256", StringComparison.OrdinalIgnoreCase) || v.Contains("InstallPins", StringComparison.Ordinal) ? new string('a', 64)
            : v.Contains("mismatched", StringComparison.Ordinal) ? CampaignWorld.Md5Mismatched
            : v.Contains("md5 of MyMod.dll", StringComparison.Ordinal) ? Mod
            : v.Contains("md5 of MyMod.TestAdapter.dll", StringComparison.Ordinal) ? Adapter
            : v.Contains("md5", StringComparison.Ordinal) ? Cli
            : v.Contains("UID", StringComparison.Ordinal) ? "4242"
            : v.Contains("password", StringComparison.Ordinal) ? "fixture-password"
            : throw new InvalidOperationException("A sample placeholder this test does not know: " + v);
        return JsonSerializer.Serialize(value);
    });

    // A dry-site-lifecycle plan: the crossplay plan's sites and arrival, joined by address with a client without MyMod.
    private JsonObject DrySitePlan()
    {
        var plan = Plan(AcceptancePlan.CrossplayScenario);
        plan["scenario"] = AcceptancePlan.LifecycleScenario; plan.Remove("crossplay"); plan["client"] = Client("absent");
        return plan;
    }

    // A dry-site-server plan with the patch reload (#30): ScriptEngine pinned, two probe builds on disk.
    private JsonObject PatchReloadPlan(bool control = false)
    {
        var plan = Plan(AcceptancePlan.ServerScenario);
        plan.Remove("client");
        plan["drySite"] = new JsonObject { ["x"] = 100, ["z"] = -40, ["ground"] = 42.5 };
        plan["wetSite"] = new JsonObject { ["x"] = 400, ["z"] = 300, ["ground"] = 22 };
        plan["pins"]![PatchReloadSettings.ScriptEngine] = new string('b', 32);
        string a = Path.Combine(_directory, "probe-a.dll"), b = Path.Combine(_directory, "probe-b.dll");
        File.WriteAllText(a, "A"); File.WriteAllText(b, "B");
        plan["patchReload"] = new JsonObject { ["revisionA"] = a, ["revisionB"] = b, ["expectOthersRemoved"] = control };
        return plan;
    }

    [Fact] public void AHostedPlanNeedsItsWorldTheModAndNoControl()
    {
        string path = Path.Combine(_directory, "hosted.json");
        JsonObject Hosted()
        {
            var client = Client(); client.Remove("join");
            client["hostWorld"] = new JsonObject
            {
                ["world"] = new JsonObject { ["source"] = Path.Combine(_directory, "host-world"), ["sha256"] = new JsonObject { ["HostFixture.fwl"] = new string('a', 64), ["HostFixture.db"] = new string('b', 64) } },
                ["worldUid"] = "4242",
            };
            return new JsonObject
            {
                ["scenario"] = HostedPlan.HostedScenarioName, ["client"] = client, ["newGreeting"] = "goodbye",
                ["drySite"] = new JsonObject { ["x"] = 100, ["z"] = -40, ["ground"] = 42.5 }, ["wetSite"] = new JsonObject { ["x"] = 400, ["z"] = 300, ["ground"] = 22 },
            };
        }
        HostedPlan ReadHosted(JsonObject plan) { File.WriteAllText(path, plan.ToJsonString()); return HostedPlan.ReadValidated(path); }
        Assert.Equal(HostedPlan.HostedScenarioName, ReadHosted(Hosted()).Scenario);
        var plan = Hosted(); plan["client"]!.AsObject().Remove("hostWorld");
        Assert.Contains("hostWorld", Assert.Throws<ArgumentException>(() => ReadHosted(plan)).Message);
        plan = Hosted(); plan["client"]!["pins"]![AcceptancePlan.ModPlugin] = "absent";
        Assert.Contains("The host runs MyMod", Assert.Throws<ArgumentException>(() => ReadHosted(plan)).Message);
        plan = Hosted(); plan["client"]!["pins"]!["example.mymod.control.suppressedsave"] = new string('5', 32);
        Assert.Contains("no control run", Assert.Throws<ArgumentException>(() => ReadHosted(plan)).Message);
        plan = Hosted(); plan["scenario"] = AcceptancePlan.WorldScenario;
        Assert.Throws<ArgumentException>(() => ReadHosted(plan));
        // A peer (#258 step 8b) joins from another machine: only as a campaign, as a joinsHost client with MyMod and its adapter.
        JsonObject Peer() { var peer = Client(); peer.Remove("join"); peer["port"] = 5557; peer["joinsHost"] = true; return peer; }
        plan = Hosted(); plan["peer"] = Peer();
        Assert.Contains("run this plan as a campaign", Assert.Throws<ArgumentException>(() => ReadHosted(plan)).Message);
        File.WriteAllText(path, plan.ToJsonString());
        var withPeer = HostedPlan.Validated(HostedPlan.Read(path));
        Assert.Equal(new[] { HostedPlan.HostClient, HostedPlan.PeerClient }, HostedScenario.CampaignClients(withPeer).Keys.Order(StringComparer.Ordinal));
        plan = Hosted(); var notJoining = Peer(); notJoining.Remove("joinsHost"); notJoining["join"] = "127.0.0.1:2456"; plan["peer"] = notJoining;
        File.WriteAllText(path, plan.ToJsonString());
        Assert.Contains("set its joinsHost", Assert.Throws<ArgumentException>(() => HostedPlan.Validated(HostedPlan.Read(path))).Message);
        plan = Hosted(); var vanilla = Peer(); vanilla["pins"]![AcceptancePlan.AdapterPlugin] = "absent"; plan["peer"] = vanilla;
        File.WriteAllText(path, plan.ToJsonString());
        Assert.Contains("The peer runs MyMod", Assert.Throws<ArgumentException>(() => HostedPlan.Validated(HostedPlan.Read(path))).Message);
    }
}
