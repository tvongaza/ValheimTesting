# Session capabilities

This example attaches to an existing ValheimCLI connection with the Standard pack. It does not own or launch the game process. `state` is read-only; `join`, `leave` and `save` are explicit mutations. They preserve ValheimCLI's client restrictions. ValheimCLI refuses the join until devcommands is on, so `Join` turns the client's devcommands on first and requires the game's reply to confirm it; pass `enableDevcommands: false` when the operator manages that flag and the join should be refused instead. Leave and save do not change it.

```sh
# Read facts on an already pinned disposable server:
dotnet run --project examples/SessionControl -- localhost 5555 /private/server-pins.txt state
# Confirm a new world save (replace the illustrative UID):
dotnet run --project examples/SessionControl -- localhost 5555 /private/server-pins.txt save 12345
# On a prepared client at its menu, choose an EXISTING disposable character:
dotnet run --project examples/SessionControl -- localhost 5556 /private/menu-pins.txt join localhost:2456 Tester TEST_PASSWORD
# Leave that client, saving its character:
dotnet run --project examples/SessionControl -- localhost 5556 /private/client-pins.txt leave
```

`TEST_PASSWORD` names an environment variable in the **game's process**, not this driver's. Launch the fixture with that variable already set using your private configuration. Passwords are never passed as command arguments or emitted in results. Omitting it clears the previous join password. Menu pins cover loaded plugin builds; once joined, pin the exact destination world and rediscover capabilities. Character names and addresses must be single tokens in the current extension protocol.

For a larger scenario:

```csharp
var session = new SessionControl(actor);
session.Join("localhost:2456", "Tester", "TEST_PASSWORD"); // issued exactly once
actor.VerifyEnvironment(destinationPins);                  // mandatory after ANY transition attempt
session.WaitForWorld(expectedWorldUid, TimeSpan.FromSeconds(120)); // then protects the joined player, read back
// Check your mod's own readiness capability next.
```

Once the world is ready, `WaitForWorld` protects the local player by default (`PlayerPlacement.Protect`: god, ghost and debug mode, read back; fly stays off) and fails if the game does not confirm it. Ghost and god mode change how monsters, eggs and loot behave, so pass `protectPlayer: false` for combat, aggro, taming, hatching and loot checks (see [the toolkit notes](../../docs/testing-toolkit.md#native-terrain-replicated-to-a-valheimcli-only-client-preview-3)). A dedicated server has no local player to protect; a hosting game's wait continues until its player spawns. Readiness here means native world/player objects are available without a load error. It does not mean a mod has finished generation, terrain is loaded at a remote site, or the road is usable. Joining waits for a connected, available local player; leaving waits for the menu. A stale error from a previous network instance is not the new join's result. Session transitions invalidate `GameActor`'s environment verification even when the reply is lost, because the game may still have acted. Never retry a mutation merely because its reply timed out.

Server save waits for any earlier save, checks vanilla refusal reasons, issues one save, and confirms that its thread ended and the save number advanced in the same world. A successful client logout is not proof that the remote server saved. Existing owned-process lifecycle (`OwnedServerSession`) remains separate and must prove process identity before restart/stop.

An issued effect keeps the game-side operation gate while it settles, including after timeout, cancellation or pack retirement. Read-only state remains available. If the game never settles, the owner remains draining and a controlled process restart may be needed; a timeout is not permission to unload/reissue the operation.

Local tests and the [bounded native campaign](../../docs/native-validation-20260927.md) pass for join/leave/save, stale-password recovery, strict repinning and a controlled in-flight owner retirement. The retirement fixture is not proof of cancellation timing for every real native operation.
