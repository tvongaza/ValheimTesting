using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using MyMod.IntegrationTests;
using MyMod.SystemTests;
using Valheim.Testing.NativeAcceptance;
using Xunit;

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

    [Theory]
    [InlineData(105)]
    [InlineData(106)]
    public void HandoffRefusesOverlappingLandingPoints(float secondX)
    {
        var plan = Plan(AcceptancePlan.OwnershipHandoffScenario);
        plan["secondArrival"] = new JsonObject { ["x"] = secondX, ["z"] = -40, ["ground"] = 42.3 };
        Refused(plan, "at least 3 m apart");
    }

    [Fact] public void ObjectSnapshotPlanRefusesAnAmbiguousCentreBeforeLaunch()
    {
        var plan = Plan(AcceptancePlan.AreaObjectsScenarioName);
        plan["arrival"]!["x"] = 105.5f;
        Refused(plan, "whole-metre coordinates");
    }

    [Fact] public void OwnershipHandoffRejectsAmbiguousClients()
    {
        // Separate hosts, distinct signed-in Steam identities and CLI ports are the inventory's assignment (EnvironmentInventoryTests).
        Read(Plan(AcceptancePlan.OwnershipHandoffScenario));
        var duplicateCharacter = Plan(AcceptancePlan.OwnershipHandoffScenario);
        duplicateCharacter["secondClient"]!["character"] = "ClientA";
        Refused(duplicateCharacter, "distinct disposable character");
        var missingCapability = Plan(AcceptancePlan.OwnershipHandoffScenario);
        missingCapability["secondClient"]!["pins"]!.AsObject().Remove(AcceptancePlan.AdapterPlugin);
        Refused(missingCapability, "Pin " + AcceptancePlan.AdapterPlugin);
    }

    [Fact] public void OwnershipHandoffRequiresTheArrivalSignalsBeforeGameplay()
    {
        // Every capability the toolkit's arrival needs, and the loaded-ground reading, is checked before launch.
        foreach (string capability in Valheim.Testing.Game.PlayerPlacement.ArrivalCapabilities.Append("valheim.world/terrain"))
        {
            var missing = Plan(AcceptancePlan.OwnershipHandoffScenario);
            var capabilities = missing["secondClient"]!["capabilities"]!.AsArray();
            capabilities.Remove(capabilities.Single(c => c!.GetValue<string>() == capability));
            Refused(missing, capability);
        }

        foreach (string removed in new[] { "eventDrivenArrival", "fastTestTeleports" })
        {
            var stale = Plan(AcceptancePlan.OwnershipHandoffScenario);
            stale["client"]![removed] = true;
            Refused(stale, removed + " was removed (ValheimTesting #299)");
        }
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

    [Fact] public void APatchReloadPlanIsReadAndRefusesWhatWouldMakeItProveNothing()
    {
        Assert.NotNull(Read(PatchReloadPlan()).PatchReload);
        Assert.True(Read(PatchReloadPlan(control: true)).PatchReload!.ExpectOthersRemoved);
        var plan = PatchReloadPlan(); plan["pins"]!.AsObject().Remove(PatchReloadSettings.ScriptEngine);
        Refused(plan, "pin " + PatchReloadSettings.ScriptEngine);
        plan = PatchReloadPlan(); plan["pins"]![PatchReloadScenario.Probe] = new string('f', 32);
        Refused(plan, "Do not pin " + PatchReloadScenario.Probe);
        plan = PatchReloadPlan(); plan["patchReload"]!["revisionB"] = plan["patchReload"]!["revisionA"]!.GetValue<string>();
        Refused(plan, "the same build");
        plan = PatchReloadPlan(); plan["patchReload"]!["revisionA"] = "probe-a.dll";
        Refused(plan, "patchReload.revisionA is the full path");
        plan = Plan(AcceptancePlan.SyncedConfigScenario); plan["patchReload"] = PatchReloadPlan()["patchReload"]!.DeepClone();
        Refused(plan, "patchReload are for the dry-site-server scenario");
    }

    [Fact] public void FieldsOfAnotherScenarioAreRefused()
    {
        var plan = Plan(AcceptancePlan.SyncedConfigScenario); plan["globalKey"] = "defeated_eikthyr";
        Refused(plan, "are for the lifecycle-world scenario");
        plan = Plan(AcceptancePlan.WorldScenario); plan["newGreeting"] = "goodbye";
        Refused(plan, "newGreeting are for the synced-config scenario");
        plan = Plan(AcceptancePlan.VanillaClientScenario); plan["crossplay"] = true;
        Refused(plan, "crossplay are for the crossplay scenario");
        plan = Plan(AcceptancePlan.SyncedConfigScenario); plan["drySite"] = new JsonObject { ["x"] = 100, ["z"] = -40, ["ground"] = 42.5 };
        Refused(plan, "marks nothing");
    }

    [Fact] public void TheWorldScenarioRefusesWhatWouldMakeItProveNothing()
    {
        var plan = Plan(AcceptancePlan.WorldScenario); plan.Remove("environment");
        Refused(plan, AcceptancePlan.FixturesVariable);
        plan = Plan(AcceptancePlan.WorldScenario); plan["away"] = new JsonObject { ["x"] = 356, ["z"] = -40, ["ground"] = 36 }; // Four zones: still loaded.
        Refused(plan, "at least 5 zones (320 m");
        plan = Plan(AcceptancePlan.WorldScenario); plan["client"]!["pins"]![AcceptancePlan.ModPlugin] = "absent";
        Refused(plan, "the check needs the server's MyMod on the client");
        plan = Plan(AcceptancePlan.WorldScenario); plan["globalKey"] = "Defeated Eikthyr";
        Refused(plan, "one lower-case key");
        plan = Plan(AcceptancePlan.WorldScenario); plan["dungeon"] = new JsonObject { ["x"] = 400, ["z"] = -40 };
        Refused(plan, "within two zones");
        plan = Plan(AcceptancePlan.WorldScenario); plan["logout"] = new JsonObject { ["charactersDirectory"] = Path.Combine(_directory, "characters") };
        Refused(plan, "characters_local");
        plan = Plan(AcceptancePlan.WorldScenario); plan.Remove("client");
        Refused(plan, "add the client section");
    }

    [Fact] public void TheVanillaClientMustLackMyModAndHaveTheServersAdapter()
    {
        var plan = Plan(AcceptancePlan.VanillaClientScenario); plan["client"]!["pins"]![AcceptancePlan.ModPlugin] = Mod;
        Refused(plan, "must pin example.mymod=absent");
        plan = Plan(AcceptancePlan.VanillaClientScenario); plan["client"]!["pins"]![AcceptancePlan.AdapterPlugin] = new string('9', 32);
        Refused(plan, "with the server's MD5");
    }

    [Fact] public void TheSyncedConfigScenarioNeedsOneNewWord()
    {
        var plan = Plan(AcceptancePlan.SyncedConfigScenario); plan.Remove("newGreeting");
        Refused(plan, "Set newGreeting");
        plan = Plan(AcceptancePlan.SyncedConfigScenario); plan["newGreeting"] = "good bye";
        Refused(plan, "Set newGreeting");
    }

    [Fact] public void TheRefusedClientMustRunAnotherBuildThanTheServer()
    {
        var plan = Plan(AcceptancePlan.RefusedJoinScenario); plan["refusedClient"] = Client(Mod, port: 5557);
        Refused(plan, "pins the server's own MyMod build");
        plan = Plan(AcceptancePlan.RefusedJoinScenario); plan["refusedClient"] = Client("absent", port: 5557);
        Refused(plan, "runs another MyMod build");
        plan = Plan(AcceptancePlan.RefusedJoinScenario); plan["refusedClient"] = Client(CampaignWorld.Md5Mismatched);
        Refused(plan, "different ValheimCLI ports");
        plan = Plan(AcceptancePlan.RefusedJoinScenario); plan["expectedRefusal"] = "Connected";
        Refused(plan, "one of the game's Error statuses");
        plan = Plan(AcceptancePlan.RefusedJoinScenario); plan["expectedRefusal"] = "ErrorSomethingNew";
        Refused(plan, "expectedRefusal: Unknown connection status");
        plan = Plan(AcceptancePlan.RefusedJoinScenario); plan["expectedRefusal"] = "ErrorDisconnected";
        Assert.Equal(Valheim.Testing.Game.GameConnectionStatus.ErrorDisconnected, Read(plan).RefusalStatus);
    }

    [Fact] public void TheCrossplayScenarioNeedsCrossplayOnBothSidesAndNoPassword()
    {
        var plan = Plan(AcceptancePlan.CrossplayScenario); plan["client"] = Client("absent");
        Refused(plan, "in its client section");
        plan = Plan(AcceptancePlan.CrossplayScenario); plan["arguments"]!.AsArray().Add("-password"); plan["arguments"]!.AsArray().Add("secret1");
        Refused(plan, "without -password");
        plan = Plan(AcceptancePlan.CrossplayScenario); plan["arguments"] = new JsonArray("-batchmode", "-nographics", "-savedir", "{world}");
        Assert.Throws<ArgumentException>(() => Read(plan)); // The toolkit's rule: a crossplay server names its port.
    }

    [Fact] public void AControlPluginIsOptInAndBelongsToOneScenarioAndSide()
    {
        const string md5 = "55555555555555555555555555555555";
        // Pinned without a control run: refused, naming what to write.
        var plan = Plan(AcceptancePlan.WorldScenario); plan["pins"]!["example.mymod.control.missingtarget"] = md5;
        Refused(plan, "Name it in expectFailure (\"missing-harmony-target\")");
        // Named without being installed.
        plan = Plan(AcceptancePlan.WorldScenario); plan["expectFailure"] = ControlPlugins.FieldOnlyState;
        Refused(plan, "pin example.mymod.control.fieldonlystate there");
        // The wrong side, the wrong scenario, an unknown name.
        plan = Plan(AcceptancePlan.WorldScenario); plan["expectFailure"] = ControlPlugins.FieldOnlyState; plan["pins"]!["example.mymod.control.fieldonlystate"] = md5;
        Refused(plan, "on the client");
        plan = Plan(AcceptancePlan.VanillaClientScenario); plan["expectFailure"] = ControlPlugins.FieldOnlyState; plan["client"]!["pins"]!["example.mymod.control.fieldonlystate"] = md5;
        Refused(plan, "belongs to the lifecycle-world scenario");
        plan = Plan(AcceptancePlan.WorldScenario); plan["expectFailure"] = "some-control";
        Refused(plan, "expectFailure names no control");
        // Two controls at once.
        plan = Plan(AcceptancePlan.WorldScenario); plan["expectFailure"] = ControlPlugins.FieldOnlyState;
        plan["client"]!["pins"]!["example.mymod.control.fieldonlystate"] = md5; plan["client"]!["pins"]!["example.mymod.control.suppressedsave"] = md5;
        Refused(plan, "exactly the control it names");
        // A server-side control run must name its line as expected: it fails the teardown scan by default.
        plan = Plan(AcceptancePlan.WorldScenario); plan["expectFailure"] = ControlPlugins.MissingHarmonyTarget; plan["pins"]!["example.mymod.control.missingtarget"] = md5;
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
        // A valid client-side control run.
        plan = Plan(AcceptancePlan.WorldScenario); plan["expectFailure"] = ControlPlugins.SuppressedProfileSave; plan["client"]!["pins"]!["example.mymod.control.suppressedsave"] = md5;
        Assert.Equal(ControlPlugins.SuppressedProfileSave, Read(plan).Control!.Name);
        // An explicit absence is not an install.
        plan = Plan(AcceptancePlan.WorldScenario); plan["client"]!["pins"]!["example.mymod.control.suppressedsave"] = "absent";
        Assert.Null(Read(plan).Control);
    }

    [Fact] public void TheContentCensusNeedsTheServersModOnTheClientAndItsControlIsABuild()
    {
        // The client's registries are its own: a client without MyMod, or with another build, cannot stand in.
        var plan = Plan(AcceptancePlan.ContentCensusScenario); plan["client"]!["pins"]![AcceptancePlan.ModPlugin] = "absent";
        Refused(plan, "the check needs the server's MyMod on the client");
        plan = Plan(AcceptancePlan.ContentCensusScenario); plan["client"]!["pins"]![AcceptancePlan.ModPlugin] = CampaignWorld.Md5Mismatched;
        Refused(plan, "the check needs the server's MyMod on the client");
        plan = Plan(AcceptancePlan.ContentCensusScenario); plan.Remove("client");
        Refused(plan, "add the client section");
        plan = Plan(AcceptancePlan.ContentCensusScenario); plan["drySite"] = new JsonObject { ["x"] = 100, ["z"] = -40, ["ground"] = 42.5 };
        Refused(plan, "marks nothing");

        // The omitted-recipe control is MyMod's own build, pinned where MyMod is; it belongs to this scenario alone and
        // runs without any control plugin.
        plan = Plan(AcceptancePlan.ContentCensusScenario); plan["expectFailure"] = ControlPlugins.OmittedRecipe;
        var control = Read(plan).Control!;
        Assert.True(control.Build);
        Assert.Equal(ControlPlugins.OmittedRecipe, control.Name);
        plan = Plan(AcceptancePlan.SyncedConfigScenario); plan["expectFailure"] = ControlPlugins.OmittedRecipe;
        Refused(plan, "belongs to the content-census scenario");
        plan = Plan(AcceptancePlan.ContentCensusScenario); plan["expectFailure"] = ControlPlugins.OmittedRecipe; plan["pins"]!["example.mymod.control.missingtarget"] = new string('5', 32);
        Refused(plan, "remove the control plugin example.mymod.control.missingtarget");
        plan = Plan(AcceptancePlan.ContentCensusScenario); plan["expectFailure"] = ControlPlugins.OmittedRecipe; plan["pins"]![AcceptancePlan.ModPlugin] = "any";
        Assert.ThrowsAny<ArgumentException>(() => Read(plan));
        // A normal content-census plan names no control, although MyMod is pinned.
        Assert.Null(Read(Plan(AcceptancePlan.ContentCensusScenario)).Control);
    }

    // Every client section runs with strict pins, the suite's two extra ones included (LifecyclePlan's rule over AcceptancePlan's sections).
    [Fact] public void EveryClientSectionRunsWithStrictPins()
    {
        var plan = Plan(AcceptancePlan.OwnershipHandoffScenario); plan["secondClient"]!["pinning"] = "none";
        Refused(plan, "strict pins only");
        plan = Plan(AcceptancePlan.RefusedJoinScenario); plan["refusedClient"]!["pinning"] = "none";
        Refused(plan, "strict pins only");
    }

    [Fact] public void TheTwoDrySiteScenariosAreUnchanged()
    {
        var plan = Plan(AcceptancePlan.CrossplayScenario);
        plan["scenario"] = AcceptancePlan.LifecycleScenario; plan.Remove("crossplay"); plan["client"] = Client("absent");
        Assert.False(Read(plan).IsCampaign);
        plan["expectFailure"] = ControlPlugins.FieldOnlyState;
        Refused(plan, "belongs to the lifecycle-world scenario");
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
