using System.Text.Json;
using Valheim.Testing.NativeAcceptance;
using Valheim.Testing.Game;
using Valheim.Testing.Game.Fakes;
using Xunit;

namespace Valheim.Testing.NativeAcceptance.Tests;

public sealed class OwnershipHandoffTests
{
    [Fact] public void NamedClientSetupsOverlapAndAFailedSetupClosesTheOtherActor()
    {
        using var world = new CampaignWorld();
        var plan = world.Plan(AcceptancePlan.ThreeActorScenario);
        var report = new ScenarioReport("parallel-client-setup");
        using var bothStarted = new CountdownEvent(2);
        ClientSession? first = null;
        var run = world.Run(plan, report, campaignClient: (requested, name) =>
        {
            bothStarted.Signal();
            if (!bothStarted.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Named actors did not start concurrently.");
            if (name == "client-b") throw new InvalidOperationException("B's loader failed");
            return first = ClientSession.Attach(requested, Directory.CreateDirectory(Path.Combine(world.Output, name)).FullName,
                new ScriptedTransport());
        });
        var failure = Assert.Throws<InvalidOperationException>(() => run.OpenClientsAsync(new Dictionary<string, ClientRunPlan>
        {
            ["client-a"] = CampaignWorld.ClientPlan(), ["client-b"] = CampaignWorld.ClientPlan(port: 5557),
        }).GetAwaiter().GetResult());
        Assert.Contains("loader failed", failure.Message);
        Assert.NotNull(first);
        Assert.True(first.Closed);
    }

    private static JsonElement Data(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact] public void ACompleteReadingNamesOneExpectedOwnerAndOneLocalVerdict()
    {
        var reading = Data("""{"source":"acceptancemod-marker-owner","complete":true,"owner":"101","self":"202","ownedHere":false,"instance":true}""");
        OwnershipHandoffScenario.RequireOwnerReading(reading, "101", ownedHere: false);
        Assert.Throws<InvalidOperationException>(() => OwnershipHandoffScenario.RequireOwnerReading(reading, "202", ownedHere: false));
        Assert.Throws<InvalidOperationException>(() => OwnershipHandoffScenario.RequireOwnerReading(reading, "101", ownedHere: true));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"source\":\"acceptancemod-marker-owner\",\"complete\":false,\"owner\":\"101\",\"ownedHere\":false,\"instance\":true}")]
    [InlineData("{\"source\":\"acceptancemod-marker-owner\",\"complete\":true,\"ownedHere\":false,\"instance\":true}")]
    [InlineData("{\"source\":\"acceptancemod-marker-owner\",\"complete\":true,\"owner\":\"101\",\"instance\":true}")]
    [InlineData("{\"source\":\"acceptancemod-marker-owner\",\"complete\":true,\"owner\":\"101\",\"ownedHere\":false,\"instance\":false}")]
    public void IncompleteOwnershipCannotPass(string json) =>
        Assert.Throws<InvalidOperationException>(() => OwnershipHandoffScenario.RequireOwnerReading(Data(json), "101", ownedHere: false));

    [Fact] public void FailureOpeningSecondClientDisposesFirstWithoutRetryingActions()
    {
        using var world = new CampaignWorld();
        var plan = world.Plan(AcceptancePlan.OwnershipHandoffScenario);
        plan.SecondClient = CampaignWorld.ClientPlan(port: 5557);
        plan.SecondArrival = new Site { X = 100, Z = -32, Ground = 42.4f };
        var report = new ScenarioReport("ownership-startup-failure");
        var client = ReadyClient(world, plan);
        var opened = new List<string>();
        var run = world.Run(plan, report, campaignClient: (requested, name) =>
        {
            opened.Add(name);
            if (name == "client-b") throw new InvalidOperationException("Second account lease lost during startup.");
            return ClientSession.Attach(CampaignWorld.ClientPlan(), world.Output, client);
        });
        ServerOwnership(world);

        Assert.Contains("Second account lease lost", Assert.Throws<InvalidOperationException>(() => OwnershipHandoffScenario.Run(run, plan)).Message);
        Assert.Equal(new[] { "client-a", "client-b" }, opened);
        Assert.True(client.Disposed);
        Assert.Equal(1, client.Count("cli_extension valheim.session/join"));
        Assert.Equal(1, client.Count("cli_teleport"));
        Assert.Equal(1, client.Count("cli_extension valheim.world/player-support-wait"));
        Assert.Contains(report.Steps, step => step.Name == "stop only owned client A before lease teardown" && step.Passed);
    }

    [Fact] public void FailedSecondStartupDoesNotHideAnUnprovenFirstClientStop()
    {
        using var world = new CampaignWorld();
        var plan = world.Plan(AcceptancePlan.OwnershipHandoffScenario);
        plan.Client!.Mode = "owned";
        plan.Client.Install = Path.Combine(world.Root, "client-a-install");
        plan.SecondClient = CampaignWorld.ClientPlan(port: 5557);
        plan.SecondArrival = new Site { X = 100, Z = -32, Ground = 42.4f };
        var report = new ScenarioReport("ownership-unknown-stop");
        var client = ReadyClient(world, plan);
        var process = new FakeOwnedProcess(101) { StopFailure = () => new IOException("A process stop was unproven") };
        var run = world.Run(plan, report, campaignClient: (requested, name) => name == "client-b"
            ? throw new InvalidOperationException("B startup failed")
            : ClientSession.Launch(requested, world.Output, () => process, () => client, (_, _) => Task.CompletedTask));
        ServerOwnership(world);

        var error = Assert.Throws<AggregateException>(() => OwnershipHandoffScenario.Run(run, plan));
        Assert.Contains(error.InnerExceptions, inner => inner.Message == "B startup failed");
        Assert.Contains(error.InnerExceptions, inner => inner.Message == "A process stop was unproven");
        Assert.Contains(report.Steps, step => step.Name == "stop only owned client A before lease teardown" && !step.Passed);
        Assert.Equal(1, process.Stops);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedFirstClientObservationClosesItBeforeSecondClientOpens(bool loadedGroundIsWet)
    {
        using var world = new CampaignWorld();
        var plan = world.Plan(AcceptancePlan.OwnershipHandoffScenario);
        plan.SecondClient = CampaignWorld.ClientPlan(port: 5557);
        plan.SecondArrival = new Site { X = 100, Z = -32, Ground = 42.4f };
        var report = new ScenarioReport("ownership-first-failure");
        var first = ReadyClient(world, plan);
        // This is the client's own settled support reading, not a server-side inference.
        if (!loadedGroundIsWet)
            first.OnPrefix("cli_extension valheim.world/player-support-wait ", _ => ScriptedTransport.Ok(ScriptedTransport.ExtensionResult("valheim.world", new
            {
                source = "local-player-support", complete = false, x = plan.Arrival.X, y = plan.Arrival.Ground, z = plan.Arrival.Z,
                speed = 0f, grounded = false, flying = false, attached = false, dead = false, teleporting = false, units = "metres",
            })));
        else
        {
            // The loaded ground is 0.5 m above the water but inside AcceptanceMod's 1.5 m clearance: the player stands on it, so the
            // arrival succeeds, and the mod's dry rule refuses it. (Deeper ground leaves the player swimming, which the
            // arrival's support wait already fails as not settled.)
            first.OnPrefix("cli_extension valheim.world/terrain ", _ => ScriptedTransport.Ok(ScriptedTransport.ExtensionResult("valheim.world", new
            {
                source = "loaded-ground", complete = true, units = "metres", x = plan.Arrival.X, z = plan.Arrival.Z, height = 30.5f,
            })));
            first.OnPrefix("cli_extension valheim.world/player-support-wait ", _ => ScriptedTransport.Ok(ScriptedTransport.ExtensionResult("valheim.world", new
            {
                source = "local-player-support", complete = true, x = plan.Arrival.X, y = 30.5f, z = plan.Arrival.Z,
                speed = 0f, grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres",
            })));
        }
        bool openedB = false;
        var run = world.Run(plan, report, campaignClient: (_, name) =>
        {
            if (name == "client-b") { openedB = true; throw new InvalidOperationException("B must not open"); }
            return ClientSession.Attach(CampaignWorld.ClientPlan(), world.Output, first);
        });

        var error = Assert.Throws<InvalidOperationException>(() => OwnershipHandoffScenario.Run(run, plan));
        Assert.Equal(loadedGroundIsWet ? "Loaded arrival ground is not dry." : "Incomplete observation or wrong observation layer.", error.Message);
        Assert.Equal(1, first.Count("cli_extension valheim.world/player-support-wait"));
        Assert.Equal(1, first.Count("cli_teleport")); // Never repeated.
        Assert.Contains("arrival-A-ground", report.Provenance.Keys);
        // A failed arrival leaves read-only diagnostics; a refused dry rule after a supported arrival has the measured ground.
        Assert.Equal(!loadedGroundIsWet, report.Provenance.ContainsKey("arrival-A-support"));
        Assert.False(openedB);
        Assert.True(first.Disposed);
        Assert.Contains(report.Steps, step => step.Name == "stop only owned client A before lease teardown" && step.Passed);
    }

    [Theory]
    [InlineData(false, 0f)]
    [InlineData(true, 0f)]
    [InlineData(false, -1f)]
    public void TwoClientsHandoffOnceAndRejectTheWrongObservedOwner(bool wrongOwner, float loadedOffset)
    {
        using var world = new CampaignWorld();
        var plan = world.Plan(AcceptancePlan.OwnershipHandoffScenario);
        plan.SecondClient = CampaignWorld.ClientPlan(port: 5557);
        plan.SecondArrival = new Site { X = 100, Z = -32, Ground = 42.4f };
        var report = new ScenarioReport("ownership-handoff");
        var first = ReadyClient(world, plan);
        var second = ReadySecond(plan, wrongOwner, loadedOffset);
        var run = world.Run(plan, report, campaignClient: (_, name) =>
            ClientSession.Attach(CampaignWorld.ClientPlan(port: name == "client-a" ? 5556 : 5557), world.Output,
                name == "client-a" ? first : second));
        ServerOwnership(world);

        if (wrongOwner)
        {
            var error = Assert.Throws<InvalidOperationException>(() => OwnershipHandoffScenario.Run(run, plan));
            Assert.Contains("wrong session", error.Message);
            Assert.False(report.Passed);
        }
        else
        {
            OwnershipHandoffScenario.Run(run, plan);
            Assert.True(report.Passed, string.Join("; ", report.Steps.Where(step => !step.Passed).Select(step => step.Error)));
            Assert.Equal("202", JsonDocument.Parse(report.Provenance["server-owner-b"]).RootElement.GetProperty("owner").GetString());
            // B's landing was judged at the loaded ground it measured, not at the generator's height.
            string loaded = (plan.SecondArrival.Ground + loadedOffset).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains(second.Commands, c => c.StartsWith("cli_extension valheim.world/player-support-wait 100 " + loaded + " -32 ", StringComparison.Ordinal));
        }
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.Equal(1, first.Count("cli_extension valheim.session/join"));
        Assert.Equal(1, second.Count("cli_extension valheim.session/join"));
        Assert.Equal(wrongOwner ? 0 : 1, second.Count("cli_extension acceptancemod.testing/marker-owner-claim"));
    }

    // CampaignWorld's client answers the arrival waits (ScriptedTransport.ArrivalSignals); this one also teleports itself.
    internal static ScriptedTransport ReadyClient(CampaignWorld world, AcceptancePlan plan) => world.Client()
            .OnPrefix("cli_teleport ", _ => { world.MoveClient(plan.Arrival.X, plan.Arrival.Ground, plan.Arrival.Z); return ScriptedTransport.Ok("OK: Teleported to test point"); })
            .Extension("valheim.world", "terrain", _ => new
            {
                source = "loaded-ground", complete = true, units = "metres", x = plan.Arrival.X, z = plan.Arrival.Z, height = plan.Arrival.Ground,
            })
            .Extension("acceptancemod.testing", "marker-owner-claim", _ => new
            {
                source = "acceptancemod-marker-owner", complete = true, id = "1:2", owner = "101", self = "101", ownedHere = true, instance = true,
            }, readOnly: false);

    internal static ScriptedTransport ReadySecond(AcceptancePlan plan, bool wrongOwner, float loadedOffset = 0)
    {
        bool joined = false, devcommands = false, claimed = false, acknowledged = false;
        return new ScriptedTransport()
            .On("devcommands", _ => ScriptedTransport.Ok("Dev commands: " + (devcommands = !devcommands)))
            .Extension("valheim.session", "join", _ => { joined = true; return new { source = "session-join", complete = true, action = "join" }; }, readOnly: false)
            .Extension("valheim.session", "state", _ => new
            {
                source = "session-state", complete = true, phase = joined ? "world-present" : "menu", worldUid = joined ? CampaignWorld.WorldUid : null,
                worldPresent = joined, worldReady = joined, server = false, dedicated = false, localPlayer = joined, playerReady = joined,
                saving = false, loadError = false, connectionStatus = joined ? "Connected" : "None",
            })
            .On("cli_set_player_safety true", _ => ScriptedTransport.Ok("OK: playerSafety enabled=True god=True ghost=True debugMode=True cheats=True ghostReplicated=True"))
            .On("cli_acknowledge_local_cheats", _ => { acknowledged = true; return ScriptedTransport.Ok("OK: localCharacterCheated=True"); })
            .On("cli_access", _ => ScriptedTransport.Ok("ACCESS " + JsonSerializer.Serialize(new { schemaVersion = 1, complete = true, devcommands, cheatsAcknowledged = acknowledged, allowOnServerClients = true, server = false, dedicated = false, joinedClient = joined, localPlayer = joined, profileAvailable = joined })))
            .OnPrefix("cli_skip_intro", _ => ScriptedTransport.Ok("OK: skipped=False profileFirstSpawn=False position=0,40,0 ms=3"))
            .OnPrefix("cli_teleport ", _ => ScriptedTransport.Ok("OK: Teleported to test point"))
            // The toolkit's arrival signals; the landing is supported at the loaded ground (the generator's height plus loadedOffset).
            .ArrivalSignals(() => new
            {
                source = "local-player-support", complete = true, x = plan.SecondArrival!.X, y = plan.SecondArrival.Ground + loadedOffset, z = plan.SecondArrival.Z,
                speed = 0f, grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres",
            })
            .Extension("valheim.world", "player-support", _ => new
            {
                source = "local-player-support", complete = true, x = 0f, y = 40f, z = 0f, speed = 0f,
                grounded = true, flying = false, attached = false, dead = false, teleporting = false, units = "metres",
            })
            .Extension("valheim.world", "terrain", _ => new
            {
                source = "loaded-ground", complete = true, units = "metres", x = plan.SecondArrival!.X, z = plan.SecondArrival.Z, height = plan.SecondArrival.Ground + loadedOffset,
            })
            .Extension("acceptancemod.testing", "markers", _ => new
            {
                source = "acceptancemod-markers", complete = true,
                markers = new[] { new { x = plan.DrySite.X, z = plan.DrySite.Z, label = CampaignSteps.DryLabel, instance = true } },
            })
            .Extension("acceptancemod.testing", "marker-owner-wait", _ => new
            {
                source = "acceptancemod-marker-owner", complete = true, owner = wrongOwner ? "102" : "101", self = "202", ownedHere = false, instance = true,
            })
            .Extension("acceptancemod.testing", "marker-owner-claim", _ =>
            {
                claimed = true;
                return new { source = "acceptancemod-marker-owner", complete = true, owner = "202", self = "202", ownedHere = true, instance = true };
            }, readOnly: false)
            .Extension("acceptancemod.testing", "marker-owner", _ => new
            {
                source = "acceptancemod-marker-owner", complete = true, owner = claimed ? "202" : "101", self = "202", ownedHere = claimed, instance = true,
            });
    }

    private static void ServerOwnership(CampaignWorld world)
    {
        string Owner() => world.Leaves == 0 ? "101" : "202";
        world.Servers.Last()
        .On("cli_peers", _ => world.Leaves == 0
            ? ScriptedTransport.Ok("OK: 2 peer(s)", "PEER 1 character A", "PEER 2 character B")
            : ScriptedTransport.Ok("OK: 1 peer(s)", "PEER 1 character B"))
        .Extension("acceptancemod.testing", "marker-owner-wait", _ => new
        {
            source = "acceptancemod-marker-owner", complete = true, id = "1:2", owner = Owner(), self = "99", ownedHere = false, instance = false,
        })
        .Extension("acceptancemod.testing", "marker-owner", _ => new
        {
            source = "acceptancemod-marker-owner", complete = true, id = "1:2", owner = Owner(), self = "99", ownedHere = false, instance = false,
        });
    }

}
