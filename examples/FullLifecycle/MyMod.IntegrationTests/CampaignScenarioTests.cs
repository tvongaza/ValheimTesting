using MyMod.SystemTests;
using Valheim.Testing.Game;
using Xunit;

namespace MyMod.IntegrationTests;

/// <summary>
/// The native campaign's scenarios against the scripted <see cref="CampaignWorld"/>: each passes on a working mod, fails
/// on the failure it exists to catch, and each control run passes only when its control's check fails for the named
/// reason (a control whose defect the check cannot see fails the run). Nothing here starts Valheim.
/// </summary>
public sealed class CampaignScenarioTests : IDisposable
{
    private readonly CampaignWorld _world = new();
    public void Dispose() => _world.Dispose();

    private ScenarioReport Run(LifecyclePlan plan, bool clientLog = false)
    {
        var report = new ScenarioReport("mymod-system-test");
        try { CampaignScenarios.Run(_world.Run(plan, report, clientLog)); }
        catch (Exception) { Assert.False(report.Passed); }
        return report;
    }
    private static string Explain(ScenarioReport report) => string.Join("; ", report.Steps.Where(s => !s.Passed).Select(s => s.Name + ": " + s.Error));
    private static string[] Failed(ScenarioReport report) => report.Steps.Where(s => !s.Passed).Select(s => s.Name).ToArray();
    private static StepResult Step(ScenarioReport report, string name) => report.Steps.Single(s => s.Name == name);
    private bool Evidence(string name) => File.Exists(Path.Combine(_world.Output, name));
    private int ClientCount(string command) => _world.Clients.Sum(client => client.Count(command));
    private int ServerCount(string command) => _world.Servers.Sum(server => server.Count(command));

    // lifecycle-world

    [Fact] public void TheWorldLifecyclePassesEveryStepOnAWorkingMod()
    {
        var report = Run(_world.Plan(LifecyclePlan.WorldScenario));
        Assert.True(report.Passed, Explain(report));
        Assert.Equal(2, _world.MarkCommands); // One per site, never repeated.
        Assert.Equal(1, _world.Restarts);
        var names = report.Steps.Select(s => s.Name).ToList();
        foreach (string step in new[]
        {
            "server: the mod's Harmony patches are applied", "first: the marker carries MyMod's saved label on the client",
            "first: set defeated_eikthyr on the server; the client lists the server's keys", "first: the dungeon's saved rooms lie in its location's zone",
            "first: the client unloads the zones", "first: after the zone reload the client has the marker again, with its saved label",
            "after-restart: the server kept defeated_eikthyr through the save and restart, and the client lists it",
            "after-restart: the client leaves to its menu and the profile file is rewritten", "after-restart: the custom data came back",
            "after-restart: the note that came back is this run's",
        }) Assert.Contains(step, names);
        Assert.True(names.IndexOf("first: the client unloads the zones") < names.IndexOf("first: after the zone reload the client has the marker again, with its saved label"));
        foreach (string file in new[] { "first-global-keys.json", "first-dungeon-rooms.json", "first-zone-cycle.json", "after-restart-logout.json" }) Assert.True(Evidence(file), file);
        Assert.Contains(report.Provenance["logoutNote"], File.ReadAllText(_world.ProfileFile)); // This run's note is in the saved character.
        Assert.Equal("DG_SunkenCrypt (0, 5000, 0)", report.Provenance["dungeonInteriorOffset"]);
        Assert.Equal(1, ServerCount("cli_extension mymod.testing/globalkey")); // The key is set once; only the lists are re-read.
        Assert.Equal(0, ClientCount("cli_extension mymodcontrol.fieldstate/set")); // No control command without a control.
        Assert.False(report.Provenance.ContainsKey("control"));
    }

    [Fact] public void AKeyTheFixtureAlreadyHasIsRefusedBeforeItIsSet()
    {
        _world.PresetKey("defeated_eikthyr");
        var report = Run(_world.Plan(LifecyclePlan.WorldScenario));
        Assert.Equal(new[] { "first: the fixture world does not have defeated_eikthyr yet" }, Failed(report).Take(1));
        Assert.Equal(0, ServerCount("cli_extension mymod.testing/globalkey"));
    }

    [Fact] public void ARoomReachingPastItsZoneFailsNamingTheZone()
    {
        _world.OversizedRoom = true;
        var report = Run(_world.Plan(LifecyclePlan.WorldScenario));
        Assert.Equal("first: the dungeon's saved rooms lie in its location's zone", Failed(report).First());
        Assert.Contains("past zone (2, -1)", Step(report, "first: the dungeon's saved rooms lie in its location's zone").Error);
        Assert.True(Evidence("first-dungeon-rooms.json")); // Written before the check.
    }

    [Fact] public void ADungeonThatNeverAppearsTimesOutNamingWhereToLook()
    {
        _world.NoDungeon = true; // The zone was never generated, or no dungeon stands at the declared position.
        var report = Run(_world.Plan(LifecyclePlan.WorldScenario));
        Assert.Equal("first: the dungeon's saved rooms lie in its location's zone", Failed(report).First());
        Assert.Contains("cli_world_dump", report.Steps.First(s => !s.Passed).Error);
        Assert.False(Evidence("first-zone-cycle.json")); // Nothing after the failure ran.
    }

    [Fact] public void ACharacterThatIsNotSavedAtLogoutFailsTheRun()
    {
        _world.SuppressSave = true; // A broken save without a control run: the logout check must catch it.
        var report = Run(_world.Plan(LifecyclePlan.WorldScenario));
        Assert.Equal("after-restart: the client leaves to its menu and the profile file is rewritten", Failed(report).First());
        Assert.True(Evidence("after-restart-logout.json"));
    }

    [Fact] public void AMissingHarmonyTargetControlRunPassesOnlyOnItsExpectedFailures()
    {
        _world.ControlMissingTarget = true;
        var report = Run(_world.Plan(LifecyclePlan.WorldScenario, ControlPlugins.MissingHarmonyTarget));
        Assert.True(report.Passed, Explain(report));
        Assert.True(Step(report, "control missing-harmony-target: the control's Harmony patch is applied fails for the named reason").Passed);
        Assert.True(Step(report, "control missing-harmony-target: the server's log scan fails on harmony-undefined-target").Passed);
        Assert.Contains("not applied: postfix (any method) on Player::MyModControlMethodThatDoesNotExist", report.Provenance["controlFailure"]);
        Assert.Contains("Undefined target method", report.Provenance["controlLogLine"]);
        Assert.StartsWith("missing-harmony-target: failed its check as expected", report.Provenance["control"]);
        Assert.True(Evidence("control-log-scan.json"));
        Assert.Equal(0, _world.MarkCommands); // The run ends at the control's checks.
        Assert.Empty(_world.Clients);
    }

    [Fact] public void AMissingHarmonyTargetControlWhosePatchAppliesFailsTheRun()
    {
        _world.ControlMissingTarget = true; _world.ControlPatchApplied = true; // The census cannot see a defect that is not there.
        var report = Run(_world.Plan(LifecyclePlan.WorldScenario, ControlPlugins.MissingHarmonyTarget));
        Assert.Equal(new[] { "control missing-harmony-target: the control's Harmony patch is applied fails for the named reason" }, Failed(report));
        Assert.Contains("passed: the check cannot see the defect", report.Steps.Single(s => !s.Passed).Error);
    }

    [Fact] public void AFieldOnlyStateControlRunPassesWhenTheValueIsLostAcrossTheZoneReload()
    {
        var report = Run(_world.Plan(LifecyclePlan.WorldScenario, ControlPlugins.FieldOnlyState));
        Assert.True(report.Passed, Explain(report));
        Assert.Equal(1, _world.FieldSets);
        Assert.True(Step(report, "first: control field-only-state: the field-only value survives the zone reload fails for the named reason").Passed);
        Assert.Contains(report.Provenance["fieldOnlyValue"], report.Provenance["controlFailure"]);
        // MyMod's own saved label came back where the field did not; the run ends before the restart and the client is closed.
        Assert.True(Step(report, "first: after the zone reload the client has the marker again, with its saved label").Passed);
        Assert.Equal(0, _world.Restarts);
        Assert.True(Step(report, "detach from the operator's client").Passed);
        Assert.True(Evidence("first-field-only-state.json"));
    }

    [Fact] public void AFieldOnlyStateControlWhoseValueSurvivesFailsTheRun()
    {
        _world.KeepFieldAcrossReload = true; // As if the zones never really unloaded the object.
        var report = Run(_world.Plan(LifecyclePlan.WorldScenario, ControlPlugins.FieldOnlyState));
        Assert.Equal(new[] { "first: control field-only-state: the field-only value survives the zone reload fails for the named reason" }, Failed(report));
    }

    [Fact] public void ASuppressedProfileSaveControlRunPassesWhenTheLogoutWritesNothing()
    {
        _world.SuppressSave = true;
        var report = Run(_world.Plan(LifecyclePlan.WorldScenario, ControlPlugins.SuppressedProfileSave));
        Assert.True(report.Passed, Explain(report));
        Assert.True(Step(report, "after-restart: control suppressed-profile-save: the logout rewrites the character file fails for the named reason").Passed);
        Assert.Contains("did not save the character on logout", report.Provenance["controlFailure"]);
        Assert.Equal("profile as the last session left it", File.ReadAllText(_world.ProfileFile));
        Assert.Equal(2, ClientCount("cli_extension valheim.session/leave")); // The first round's leave and the logout; nothing after it.
        Assert.Equal(2, ClientCount("cli_extension valheim.session/join")); // No rejoin after the failed logout.
        Assert.True(Step(report, "detach from the operator's client").Passed);
    }

    [Fact] public void AControlCheckThatFailsForAnotherReasonFailsTheRun()
    {
        _world.SuppressSave = true; _world.CloudCharacter = true; // The logout check refuses a cloud character before any logout.
        var report = Run(_world.Plan(LifecyclePlan.WorldScenario, ControlPlugins.SuppressedProfileSave));
        Assert.Equal(new[] { "after-restart: control suppressed-profile-save: the logout rewrites the character file fails for the named reason" }, Failed(report));
        Assert.Contains("not for its reason", report.Steps.Single(s => !s.Passed).Error);
        Assert.Contains("not Local", report.Steps.Single(s => !s.Passed).Error);
        Assert.False(report.Provenance.ContainsKey("controlFailure"));
    }

    [Fact] public void ASuppressedProfileSaveControlWhoseSaveHappensFailsTheRun()
    {
        var report = Run(_world.Plan(LifecyclePlan.WorldScenario, ControlPlugins.SuppressedProfileSave)); // The control is named but saves go through.
        Assert.Equal(new[] { "after-restart: control suppressed-profile-save: the logout rewrites the character file fails for the named reason" }, Failed(report));
    }

    // vanilla-client

    [Fact] public void AVanillaClientSeesTheMarkerAndResolvesEveryPrefabInBothRounds()
    {
        var report = Run(_world.Plan(LifecyclePlan.VanillaClientScenario), clientLog: true);
        Assert.True(report.Passed, Explain(report));
        foreach (string round in new[] { "first", "after-restart" })
        {
            Assert.True(Step(report, $"{round}: the client resolves every prefab hash where the player stands").Passed);
            Assert.True(Step(report, $"{round}: the client's logs show no missing prefabs, missing RPC handlers or RemoveObjects errors").Passed);
            Assert.True(Evidence($"{round}-vanilla-client-1.json"));
        }
        Assert.Equal(0, _world.SpawnCommands);
        Assert.Equal("the owned client's live BepInEx log", report.Provenance["vanillaClientLogScan"]);
    }

    [Fact] public void AServerOnlyObjectFailsTheVanillaClientNamingItsHash()
    {
        _world.PlaceServerOnlyObject(103, -40); // A server-side mod's own prefab near the marker, without any control.
        var report = Run(_world.Plan(LifecyclePlan.VanillaClientScenario));
        Assert.Equal("first: the client resolves every prefab hash where the player stands", Failed(report).First());
        Assert.Contains($"{StableHash.Of(ControlPlugins.ServerOnlyPrefabName)} ({ControlPlugins.ServerOnlyPrefabName})", report.Steps.First(s => !s.Passed).Error);
        Assert.Equal(0, _world.Restarts);
    }

    [Fact] public void AMissingPrefabLineInTheClientsLogFailsTheVanillaClient()
    {
        File.AppendAllText(_world.ClientLog, "[Warning: Unity Log] Missing prefab hash: 123456\n");
        var report = Run(_world.Plan(LifecyclePlan.VanillaClientScenario), clientLog: true);
        Assert.Equal("first: the client's logs show no missing prefabs, missing RPC handlers or RemoveObjects errors", Failed(report).First());
        Assert.Contains("missing-prefab-hash x1", report.Steps.First(s => !s.Passed).Error);
    }

    [Fact] public void AServerOnlyPrefabControlRunPassesWhenTheCensusNamesItsHash()
    {
        _world.ControlServerOnlyPrefab = true;
        var report = Run(_world.Plan(LifecyclePlan.VanillaClientScenario, ControlPlugins.ServerOnlyPrefab));
        Assert.True(report.Passed, Explain(report));
        Assert.Equal(1, _world.SpawnCommands);
        Assert.True(Step(report, "first: control server-only-prefab: the vanilla client resolves every prefab hash where the player stands fails for the named reason").Passed);
        Assert.Contains(ControlPlugins.ServerOnlyPrefabName, report.Provenance["controlFailure"]);
        Assert.True(Evidence("first-vanilla-client-1.json"));
        Assert.Equal(0, _world.Restarts);
    }

    [Fact] public void AServerOnlyPrefabControlThatIsNotInstalledFailsBeforeTheClientOpens()
    {
        var report = Run(_world.Plan(LifecyclePlan.VanillaClientScenario, ControlPlugins.ServerOnlyPrefab)); // The server has no such command.
        Assert.Equal(new[] { "control server-only-prefab: the server spawns its server-only object beside the dry site" }, Failed(report));
        Assert.Empty(_world.Clients);
    }

    // synced-config

    [Fact] public void TheServersGreetingReachesTheClientAfterTheChangeAndAfterTheRestart()
    {
        var report = Run(_world.Plan(LifecyclePlan.SyncedConfigScenario), clientLog: true);
        Assert.True(report.Passed, Explain(report));
        Assert.Equal(1, _world.GreetingCommands); // Changed once, never repeated.
        Assert.Equal(1, _world.Restarts);
        Assert.True(Step(report, "first: the client reads the server's new greeting within the wait").Passed);
        Assert.True(Step(report, "after-restart: after the restart both sides hold the server's new greeting").Passed);
        foreach (string file in new[] { "first-greeting-joined.json", "first-greeting-changed.json", "after-restart-greeting-after-restart.json" }) Assert.True(Evidence(file), file);
        Assert.Equal(0, _world.MarkCommands); // This scenario marks nothing.
    }

    [Fact] public void AGreetingThatNeverReachesTheClientTimesOutWithTheLastValue()
    {
        _world.SyncBroken = true;
        var report = Run(_world.Plan(LifecyclePlan.SyncedConfigScenario));
        Assert.Equal(new[] { "first: the client reads the server's new greeting within the wait" }, Failed(report));
        Assert.Contains("= hello", report.Steps.Single(s => !s.Passed).Error);
        Assert.Equal(1, _world.GreetingCommands);
        Assert.Equal(0, _world.Restarts);
    }

    // refused-join

    [Fact] public void AMismatchedClientIsRefusedAndAMatchingOneThenJoins()
    {
        var report = Run(_world.Plan(LifecyclePlan.RefusedJoinScenario));
        Assert.True(report.Passed, Explain(report));
        Assert.Equal(new[]
        {
            "server: the mod's Harmony patches are applied",
            "attach to the operator's mismatched client at its menu, plugins pinned", "the server accepts game connections",
            "the mismatched client is refused with ErrorVersion (3)", "detach from the operator's mismatched client",
            "attach to the operator's matching client at its menu, plugins pinned", "matching: the server accepts game connections",
            "matching: join the owned server with the disposable character, protected", "matching: the server keeps the matching client connected as its one player",
            "matching: the client leaves to its menu", "detach from the operator's client",
        }, report.Steps.Select(s => s.Name));
        Assert.StartsWith("ErrorVersion (3)", report.Provenance["refusal"]);
        Assert.Equal(1, _world.Clients[0].Count("cli_extension valheim.session/join")); // Refused once, never retried.
        Assert.True(Evidence(Path.Combine(RefusedJoinScenario.RefusedDirectory, "client-commands.jsonl")));
        Assert.True(Evidence(Path.Combine(RefusedJoinScenario.MatchingDirectory, "client-commands.jsonl")));
    }

    [Fact] public void AMismatchedClientThatGetsInFailsAndTheMatchingClientNeverOpens()
    {
        _world.RefusalSucceeds = true; // Negative control: a server whose handshake refuses nothing.
        var report = Run(_world.Plan(LifecyclePlan.RefusedJoinScenario));
        Assert.Equal(new[] { "the mismatched client is refused with ErrorVersion (3)" }, Failed(report));
        Assert.Contains("The join succeeded", report.Steps.Single(s => !s.Passed).Error);
        Assert.Single(_world.Clients);
        Assert.True(Step(report, "detach from the operator's mismatched client").Passed);
    }

    [Fact] public void ARefusalWithAnotherStatusFailsNamingBoth()
    {
        _world.RefusalStatus = "ErrorDisconnected";
        var report = Run(_world.Plan(LifecyclePlan.RefusedJoinScenario));
        Assert.Equal(new[] { "the mismatched client is refused with ErrorVersion (3)" }, Failed(report));
        Assert.Contains("ErrorDisconnected (4)", report.Steps.Single(s => !s.Passed).Error);
    }

    // crossplay

    [Fact] public void TheLifecycleRunsOverACrossplayServersLobby()
    {
        _world.Crossplay = true;
        var report = Run(_world.Plan(LifecyclePlan.CrossplayScenario));
        Assert.True(report.Passed, Explain(report));
        Assert.Equal("crossplay", report.Provenance["clientJoin"]);
        Assert.Equal(2, ClientCount("cli_connect_playfab_user " + CampaignWorld.PlayFabId)); // Once per round, each boot's lobby.
        Assert.Equal(0, ClientCount("cli_extension valheim.session/join"));
        Assert.True(Step(report, "after-restart: the client sees the marker at the dry site").Passed);
    }

    [Fact] public void ACrossplayRunOnASteamServerFailsAtTheLobby()
    {
        var report = Run(_world.Plan(LifecyclePlan.CrossplayScenario)); // The server runs Steamworks.
        Assert.Equal(new[] { "first: the server's crossplay lobby is open" }, Failed(report));
        Assert.Contains("not PlayFab", report.Steps.Single(s => !s.Passed).Error);
        Assert.Equal(0, ClientCount("cli_connect_playfab_user"));
    }
}
