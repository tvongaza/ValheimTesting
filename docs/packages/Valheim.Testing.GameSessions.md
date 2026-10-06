# Valheim.Testing.GameSessions

*Assistant-written (Claude).*

`Valheim.Testing.GameSessions` (net10.0) runs game sessions for mod tests: one or more real game processes in roles (a dedicated server, clients, a client that hosts its own world) on this machine or on others reached over SSH or a container, prepared, started, joined and torn down as one run. It is built on [`Valheim.Testing.Game`](Valheim.Testing.Game.md), which drives one game process; this package adds what a run of several processes, or a run on another machine, needs. Unofficial community tooling; not affiliated with or endorsed by Iron Gate or Coffee Stain. Valheim is a trademark of Iron Gate AB.

A test project that only observes or drives one game through `GameActor`, `OwnedServerSession`, `ClientSession`, `ClientRounds` or a `TargetedRegression` needs `Valheim.Testing.Game` alone. Add this package when the test runs a session: a dedicated server with its clients, a hosted world with peers, a server-only load check, or any actor on another machine.

## What it holds

- **The session:** `GameSession` (its server, clients by name, a hosting client, the scenario's `GameActor` handles, named barriers), the role actors `ServerActor`, `ClientActor` and `HostingClientActor`, and `ModDeclaration`. The xUnit fixture, `GameSessionFixture`, is source a mod copies from the [FullLifecycle example](../../examples/FullLifecycle/README.md).
- **The pinned runner:** `PinnedServerRun` (`validate`, `run`, and `--inventory` for a server on another host) and `RunCampaignAsync` for a session manifest.
- **Hosts:** `IGameHost` with `LocalGameHost`, `SshGameHost` and `ContainerGameHost`, the host lock, CLI tunnels, installs, server and interactive-client starts on a host, macOS app-bundle checks.
- **Where actors run:** `EnvironmentRuns` places a session's actors on an `EnvironmentInventory` (the inventory itself, the description of the machines, is `Valheim.Testing.Game`'s) and reports, recovers and tears down what earlier runs left on its hosts (`valheim-test env status|recover|teardown`).
- **Steam accounts:** `SteamAccountHold` and its lease, so two clients never run on one account.
- **Evidence from a host:** `WorldDump`, `ReviewCapture` and `ReviewClip` fetch what the game wrote on its machine into the run's evidence.

The types keep the `Valheim.Testing.Game` namespace for now, so moving to this package needs no `using` change. The guides for these parts are still on the [Valheim.Testing.Game page](Valheim.Testing.Game.md): [game sessions](Valheim.Testing.Game.md#game-sessions), [the pinned server runner](Valheim.Testing.Game.md#pinned-server-runner), [game hosts and the environment inventory](Valheim.Testing.Game.md#game-hosts-and-the-environment-inventory).

## Versions

This package depends on exactly the `Valheim.Testing.Game` version it was built with: it uses Game's internals, so the two always restore as the pair that was tested together.
