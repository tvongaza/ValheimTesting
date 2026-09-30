using System.Text.Json;
using System.Text.Json.Nodes;
using MyMod.SystemTests;
using Xunit;

namespace MyMod.IntegrationTests;

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
            ["pins"] = new JsonObject { ["valheimCLI.valheimCLI"] = Cli, [LifecyclePlan.ModPlugin] = mod, [LifecyclePlan.AdapterPlugin] = Adapter },
        };
        if (crossplay) client["crossplay"] = true; else client["join"] = "127.0.0.1:2456";
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
            ["pins"] = new JsonObject { ["worlduid"] = "4242", ["valheimCLI.valheimCLI"] = Cli, [LifecyclePlan.ModPlugin] = Mod, [LifecyclePlan.AdapterPlugin] = Adapter },
            ["runtimePins"] = new JsonObject { ["game"] = new string('c', 64), ["bepinexCore"] = new string('d', 64), ["patchers"] = new string('e', 64) },
            ["client"] = Client(),
        };
        if (scenario is LifecyclePlan.WorldScenario or LifecyclePlan.VanillaClientScenario or LifecyclePlan.CrossplayScenario)
        {
            plan["drySite"] = new JsonObject { ["x"] = 100, ["z"] = -40, ["ground"] = 42.5 };
            plan["wetSite"] = new JsonObject { ["x"] = 400, ["z"] = 300, ["ground"] = 22 };
            plan["arrival"] = new JsonObject { ["x"] = 105, ["z"] = -40, ["ground"] = 42.3 };
        }
        switch (scenario)
        {
            case LifecyclePlan.WorldScenario:
                plan["environment"] = new JsonObject { [LifecyclePlan.FixturesVariable] = "1" };
                plan["away"] = new JsonObject { ["x"] = 420, ["z"] = -40, ["ground"] = 36 };
                plan["globalKey"] = "defeated_eikthyr";
                plan["dungeon"] = new JsonObject { ["x"] = 150, ["z"] = -40 };
                plan["logout"] = new JsonObject { ["charactersDirectory"] = Path.Combine(_directory, "characters_local") };
                break;
            case LifecyclePlan.VanillaClientScenario: plan["client"]!["pins"]![LifecyclePlan.ModPlugin] = "absent"; break;
            case LifecyclePlan.SyncedConfigScenario: plan["newGreeting"] = "goodbye"; break;
            case LifecyclePlan.RefusedJoinScenario: plan["refusedClient"] = Client(CampaignWorld.Md5Mismatched, port: 5557); break;
            case LifecyclePlan.CrossplayScenario:
                plan["crossplay"] = true;
                plan["client"] = Client("absent", crossplay: true);
                break;
        }
        return plan;
    }

    private LifecyclePlan Read(JsonObject plan)
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, plan.ToJsonString());
        return LifecyclePlan.ReadValidated(path);
    }
    private void Refused(JsonObject plan, string because)
    {
        var error = Assert.Throws<ArgumentException>(() => Read(plan));
        Assert.Contains(because, error.Message);
    }

    [Theory]
    [InlineData(LifecyclePlan.WorldScenario)] [InlineData(LifecyclePlan.VanillaClientScenario)] [InlineData(LifecyclePlan.SyncedConfigScenario)]
    [InlineData(LifecyclePlan.RefusedJoinScenario)] [InlineData(LifecyclePlan.CrossplayScenario)]
    public void EachScenariosValidPlanIsRead(string scenario) => Assert.Equal(scenario, Read(Plan(scenario)).Scenario);

    [Fact] public void TheSamplePlansAreValidPlans()
    {
        // The samples beside sample-plan.json have placeholders for hashes and paths; with those filled in, each reads.
        string samples = Path.Combine(AppContext.BaseDirectory, "samples");
        var files = Directory.GetFiles(samples, "sample-plan-*.json").Where(file => !file.EndsWith("-hosted.json", StringComparison.Ordinal)).ToArray();
        Assert.Equal(5, files.Length);
        foreach (string file in files)
        {
            var plan = JsonNode.Parse(Fill(File.ReadAllText(file)))!.AsObject();
            Assert.Equal(plan["scenario"]!.GetValue<string>(), Read(plan).Scenario);
        }
        string hosted = Path.Combine(_directory, "hosted.json");
        File.WriteAllText(hosted, Fill(File.ReadAllText(Path.Combine(samples, "sample-plan-hosted.json"))));
        Assert.Equal(HostedPlan.HostedScenarioName, HostedPlan.ReadValidated(hosted).Scenario);
    }

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

    [Fact] public void FieldsOfAnotherScenarioAreRefused()
    {
        var plan = Plan(LifecyclePlan.SyncedConfigScenario); plan["globalKey"] = "defeated_eikthyr";
        Refused(plan, "are for the lifecycle-world scenario");
        plan = Plan(LifecyclePlan.WorldScenario); plan["newGreeting"] = "goodbye";
        Refused(plan, "newGreeting are for the synced-config scenario");
        plan = Plan(LifecyclePlan.VanillaClientScenario); plan["crossplay"] = true;
        Refused(plan, "crossplay are for the crossplay scenario");
        plan = Plan(LifecyclePlan.SyncedConfigScenario); plan["drySite"] = new JsonObject { ["x"] = 100, ["z"] = -40, ["ground"] = 42.5 };
        Refused(plan, "marks nothing");
    }

    [Fact] public void TheWorldScenarioRefusesWhatWouldMakeItProveNothing()
    {
        var plan = Plan(LifecyclePlan.WorldScenario); plan.Remove("environment");
        Refused(plan, LifecyclePlan.FixturesVariable);
        plan = Plan(LifecyclePlan.WorldScenario); plan["away"] = new JsonObject { ["x"] = 356, ["z"] = -40, ["ground"] = 36 }; // Four zones: still loaded.
        Refused(plan, "at least 5 zones (320 m");
        plan = Plan(LifecyclePlan.WorldScenario); plan["client"]!["pins"]![LifecyclePlan.ModPlugin] = "absent";
        Refused(plan, "the check needs the server's MyMod on the client");
        plan = Plan(LifecyclePlan.WorldScenario); plan["globalKey"] = "Defeated Eikthyr";
        Refused(plan, "one lower-case key");
        plan = Plan(LifecyclePlan.WorldScenario); plan["dungeon"] = new JsonObject { ["x"] = 400, ["z"] = -40 };
        Refused(plan, "within two zones");
        plan = Plan(LifecyclePlan.WorldScenario); plan["logout"] = new JsonObject { ["charactersDirectory"] = Path.Combine(_directory, "characters") };
        Refused(plan, "characters_local");
        plan = Plan(LifecyclePlan.WorldScenario); plan.Remove("client");
        Refused(plan, "add the client section");
    }

    [Fact] public void TheVanillaClientMustLackMyModAndHaveTheServersAdapter()
    {
        var plan = Plan(LifecyclePlan.VanillaClientScenario); plan["client"]!["pins"]![LifecyclePlan.ModPlugin] = Mod;
        Refused(plan, "must pin example.mymod=absent");
        plan = Plan(LifecyclePlan.VanillaClientScenario); plan["client"]!["pins"]![LifecyclePlan.AdapterPlugin] = new string('9', 32);
        Refused(plan, "with the server's MD5");
    }

    [Fact] public void TheSyncedConfigScenarioNeedsOneNewWord()
    {
        var plan = Plan(LifecyclePlan.SyncedConfigScenario); plan.Remove("newGreeting");
        Refused(plan, "Set newGreeting");
        plan = Plan(LifecyclePlan.SyncedConfigScenario); plan["newGreeting"] = "good bye";
        Refused(plan, "Set newGreeting");
    }

    [Fact] public void TheRefusedClientMustRunAnotherBuildThanTheServer()
    {
        var plan = Plan(LifecyclePlan.RefusedJoinScenario); plan["refusedClient"] = Client(Mod, port: 5557);
        Refused(plan, "pins the server's own MyMod build");
        plan = Plan(LifecyclePlan.RefusedJoinScenario); plan["refusedClient"] = Client("absent", port: 5557);
        Refused(plan, "runs another MyMod build");
        plan = Plan(LifecyclePlan.RefusedJoinScenario); plan["refusedClient"] = Client(CampaignWorld.Md5Mismatched);
        Refused(plan, "different ValheimCLI ports");
        plan = Plan(LifecyclePlan.RefusedJoinScenario); plan["expectedRefusal"] = "Connected";
        Refused(plan, "one of the game's Error statuses");
        plan = Plan(LifecyclePlan.RefusedJoinScenario); plan["expectedRefusal"] = "ErrorSomethingNew";
        Refused(plan, "expectedRefusal: Unknown connection status");
        plan = Plan(LifecyclePlan.RefusedJoinScenario); plan["expectedRefusal"] = "ErrorDisconnected";
        Assert.Equal(Valheim.Testing.Game.GameConnectionStatus.ErrorDisconnected, Read(plan).RefusalStatus);
    }

    [Fact] public void TheCrossplayScenarioNeedsCrossplayOnBothSidesAndNoPassword()
    {
        var plan = Plan(LifecyclePlan.CrossplayScenario); plan["client"] = Client("absent");
        Refused(plan, "in its client section");
        plan = Plan(LifecyclePlan.CrossplayScenario); plan["arguments"]!.AsArray().Add("-password"); plan["arguments"]!.AsArray().Add("secret1");
        Refused(plan, "without -password");
        plan = Plan(LifecyclePlan.CrossplayScenario); plan["arguments"] = new JsonArray("-batchmode", "-nographics", "-savedir", "{world}");
        Assert.Throws<ArgumentException>(() => Read(plan)); // The toolkit's rule: a crossplay server names its port.
    }

    [Fact] public void AControlPluginIsOptInAndBelongsToOneScenarioAndSide()
    {
        const string md5 = "55555555555555555555555555555555";
        // Pinned without a control run: refused, naming what to write.
        var plan = Plan(LifecyclePlan.WorldScenario); plan["pins"]!["example.mymod.control.missingtarget"] = md5;
        Refused(plan, "Name it in expectFailure (\"missing-harmony-target\")");
        // Named without being installed.
        plan = Plan(LifecyclePlan.WorldScenario); plan["expectFailure"] = ControlPlugins.FieldOnlyState;
        Refused(plan, "pin example.mymod.control.fieldonlystate there");
        // The wrong side, the wrong scenario, an unknown name.
        plan = Plan(LifecyclePlan.WorldScenario); plan["expectFailure"] = ControlPlugins.FieldOnlyState; plan["pins"]!["example.mymod.control.fieldonlystate"] = md5;
        Refused(plan, "on the client");
        plan = Plan(LifecyclePlan.VanillaClientScenario); plan["expectFailure"] = ControlPlugins.FieldOnlyState; plan["client"]!["pins"]!["example.mymod.control.fieldonlystate"] = md5;
        Refused(plan, "belongs to the lifecycle-world scenario");
        plan = Plan(LifecyclePlan.WorldScenario); plan["expectFailure"] = "some-control";
        Refused(plan, "expectFailure names no control");
        // Two controls at once.
        plan = Plan(LifecyclePlan.WorldScenario); plan["expectFailure"] = ControlPlugins.FieldOnlyState;
        plan["client"]!["pins"]!["example.mymod.control.fieldonlystate"] = md5; plan["client"]!["pins"]!["example.mymod.control.suppressedsave"] = md5;
        Refused(plan, "exactly the control it names");
        // The missing-harmony-target run reclassifies its own log line, with a reason.
        plan = Plan(LifecyclePlan.WorldScenario); plan["expectFailure"] = ControlPlugins.MissingHarmonyTarget; plan["pins"]!["example.mymod.control.missingtarget"] = md5;
        Refused(plan, "classifies harmony-undefined-target as Warning");
        plan["logScan"] = new JsonObject { ["harmony-undefined-target"] = new JsonObject { ["severity"] = "Warning", ["reason"] = "The control plants it; the scenario requires it." } };
        Assert.Equal(ControlPlugins.MissingHarmonyTarget, Read(plan).Control!.Name);
        // A valid client-side control run.
        plan = Plan(LifecyclePlan.WorldScenario); plan["expectFailure"] = ControlPlugins.SuppressedProfileSave; plan["client"]!["pins"]!["example.mymod.control.suppressedsave"] = md5;
        Assert.Equal(ControlPlugins.SuppressedProfileSave, Read(plan).Control!.Name);
        // An explicit absence is not an install.
        plan = Plan(LifecyclePlan.WorldScenario); plan["client"]!["pins"]!["example.mymod.control.suppressedsave"] = "absent";
        Assert.Null(Read(plan).Control);
    }

    [Fact] public void TheTwoDrySiteScenariosAreUnchanged()
    {
        var plan = Plan(LifecyclePlan.CrossplayScenario);
        plan["scenario"] = LifecyclePlan.LifecycleScenario; plan.Remove("crossplay"); plan["client"] = Client("absent");
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
        plan = Hosted(); plan["client"]!["pins"]![LifecyclePlan.ModPlugin] = "absent";
        Assert.Contains("The host runs MyMod", Assert.Throws<ArgumentException>(() => ReadHosted(plan)).Message);
        plan = Hosted(); plan["client"]!["pins"]!["example.mymod.control.suppressedsave"] = new string('5', 32);
        Assert.Contains("no control run", Assert.Throws<ArgumentException>(() => ReadHosted(plan)).Message);
        plan = Hosted(); plan["scenario"] = LifecyclePlan.WorldScenario;
        Assert.Throws<ArgumentException>(() => ReadHosted(plan));
    }
}
