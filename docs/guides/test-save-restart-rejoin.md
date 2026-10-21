# Test that a change survives a save, restart and rejoin

Drive your mod once, observe its saved objects, then check them from a clean client before and after a confirmed save and server restart.

Excerpt from the canonical [marker scenario](../../examples/FullLifecycle/ExampleMod.Tests/MarkerScenario.cs):

```csharp
report.Step("the mod marks the dry site", () => server.Execute(Mark(plan.DrySite)).RequireLine("OK: marked ", "ExampleMod did not mark the dry site"));
report.Step("server: one marker at the dry site, none at the wet site", () => { RequireServerMarkers(server, plan.DrySite, 1); RequireServerMarkers(server, plan.WetSite, 0); });
```

The scenario also requires zero markers before acting, a wet-site refusal, and the marker on the client in both rounds. Captured result fields:

```json
{
  "Name": "after-restart: the client sees the marker at the dry site",
  "Passed": true
}
```

## When to use this

Use this for a placed object, terrain change or stored value that must survive a world save. For load checks, use [Check that a mod loads](check-a-mod-loads.md). See [Platforms](../reference/platforms.md) for host requirements and the [native-run checklist](before-a-native-run.md) before running.

## Steps

### 1. Prepare the mod and adapter

Start with the independent mod folder from [Your first gameplay assertion](../tutorials/first-gameplay-assertion.md). Add the two-file [adapter map](../../examples/FullLifecycle/consumer/README.md#mod-and-adapter). The adapter supplies `examplemod.testing/session` with `EXAMPLEMOD_TEST_SESSION_TOKEN`; generic observations alone do not identify your owned session.

From that independent folder:

```sh
valheim-test server-load --project MarkerMod/MarkerMod.csproj --adapter-project MarkerAdapter/MarkerAdapter.csproj --session-capability examplemod.testing/session --session-token-variable EXAMPLEMOD_TEST_SESSION_TOKEN --preflight-only
```

Captured output prefix:

```text
PREFLIGHT PASSED: the derived campaign is
```

The printed directory holds `campaign.json`, `environments.json`, role dependency locks, fixture and character sources, `plan.json`, and `client-plan.json`. Nothing launches. Keep those files together; the server plan contains a private join password.

### 2. Add the canonical assertions

From the same independent folder:

```sh
valheim-test init server --output MarkerPersistence
```

The tool builds `MarkerPersistence/consumer/SmokeCheck.csproj` from published packages. Replace its entry and add the canonical plan/scenario using the three-file [native persistence map](../../examples/FullLifecycle/consumer/README.md#native-persistence-entry). Keep the generated project and NuGet configuration.

The unchanged generated entry checks a load replay. The replacement delegates save/restart/rejoin to the canonical scenario and toolkit session lifecycle.

### 3. Write the rounds

Keep the canonical `first` and `after-restart` rounds for this example. They check the client before and after the confirmed save and server restart. Declare their fixture expectations in the plan.

Beside the emitted `plan.json`, copy it to `marker-plan.json`. Preserve its inherited fields. Change `Scenario` to `dry-site-lifecycle` and add `DrySite`, `WetSite`, and `Arrival`.

Read generator heights independently of your mod through [terrain observations and site search](../reference/game-observations.md). Keep the capture's world identity with the plan. These values describe default fixture UID `3010640879`, measured with published toolkit `0.1.0` on macOS:

```json
"DrySite": { "x": -32, "z": -32, "ground": 34.463486 },
"WetSite": { "x": -32, "z": 32, "ground": 24.37543 },
"Arrival": { "x": -27, "z": -27, "ground": 33.848846 }
```

For another fixture, declare its measured expectations. `MarkerPlan.CheckSites` requires separated dry/wet sites and a dry arrival 3–20 metres from the marker. The native client still proves loaded-ground readiness and marker visibility.

### 4. Run the scenario

From `MarkerPersistence/consumer`, with the printed campaign path and a new result directory:

```sh
dotnet run --project SmokeCheck.csproj -c Release -- RUN_OUTPUT/campaign.json NEW_RESULT_DIRECTORY
```

Captured final line:

```text
PASS
```

The entry reads `marker-plan.json` and the emitted client plan beside the campaign. It retains that client's password-variable binding. The toolkit binds actor paths and pins, starts the dedicated server, then opens the clean client. On Windows, use an interactive desktop terminal for this direct consumer.

## Check the result

Read `result.json` and `junit.xml` in the result directory. Require the initial zero-marker baseline, dry/wet server counts, both client observations, confirmed save, server restart, and successful cleanup. The restarted server must report the same world UID.

`boot-N/`, `connection-N.jsonl`, and `client-commands.jsonl` retain each boot and command. Finish with `valheim-test env status`; it must list nothing your run left.

## Problems

| Symptom | Fix |
|---|---|
| A declared patch is absent | Check the production mod's declared Harmony patch |
| Client is missing | Keep the emitted `client-plan.json` beside the campaign |
| Client sees no marker | Inspect arrival evidence and complete prefab observations |
| Save is unconfirmed | Treat the run as failed; inspect the save trace |
| Cleanup fails | Follow [recovery](../reference/sessions-leases-and-recovery.md) |

## Limits

One server and one clean client prove persistence and that client's observation. They do not prove simultaneous replication to two clients. Published `0.2` verification remains pending.

## Related

- [Standalone lifecycle consumers and scripted controls](../../examples/FullLifecycle/consumer/README.md)
- [FullLifecycle example](../../examples/FullLifecycle/README.md)
- [Run a session with several clients](run-a-session-with-several-clients.md)
