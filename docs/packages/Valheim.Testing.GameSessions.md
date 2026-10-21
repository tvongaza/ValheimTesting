# Valheim.Testing.GameSessions

[API reference](https://tvongaza.github.io/ValheimTesting/reference/). This page is the package's README; each type's members are in the reference.

`Valheim.Testing.GameSessions` runs game sessions for mod tests: a dedicated server with its clients, a client hosting a world with its peers, or a server on another machine, prepared, started, joined and torn down as one run. It is built on [`Valheim.Testing.Game`](Valheim.Testing.Game.md), which drives one game process. Unofficial community tooling; not affiliated with or endorsed by Iron Gate or Coffee Stain. Valheim is a trademark of Iron Gate AB.

| You want | Use |
|---|---|
| Observe or drive one game, or a joining client's rounds | [`Valheim.Testing.Game`](Valheim.Testing.Game.md) alone |
| A dedicated server and a client, with a save and restart | This package: [`PinnedServerRun`](../reference/sessions-pinned-server-run.md) |
| A hosting client's rounds, driven by your code | This package: `HostedClientRounds.Run` ([game sessions](../reference/sessions-game-session.md#hosted-runs)) |
| Two builds of your mod in one scenario | This package: [`TargetedRegression`](../reference/sessions-targeted-regression.md) |
| A server on another machine, or several clients | This package: an [inventory file](../reference/sessions-inventory.md) and a [campaign](../reference/sessions-campaigns.md) |

## Install

Add both packages to a separate system-test project, never to the mod. It targets net10.0.

```xml
<PackageReference Include="Valheim.Testing.Game" Version="[0.1.0]" />
<PackageReference Include="Valheim.Testing.GameSessions" Version="[0.1.0]" />
```

Brackets pin an exact version; the released versions are in the [package table](../package-versions.md). This package depends on exactly the `Valheim.Testing.Game` version it was built with, so the two always restore as the pair that was tested together.

```csharp
using Valheim.Testing.Game;
using Valheim.Testing.GameSessions;
```

## A first session

Copy the [FullLifecycle example](../../examples/FullLifecycle/README.md): one dedicated server and one client on this machine, with a save, a restart and a rejoin. [Test that a change survives a save, restart and rejoin](../guides/test-save-restart-rejoin.md) walks through it. Your scenario receives the started session:

```csharp
public static void Run(GameSession session, MarkerPlan plan) =>
    Run(plan, session.Server!.Game, session.Server!, () => session.OpenClient(plan.Client!), session.Report, session.Output, session.Cancellation);
```

For xUnit, copy [GameSessionFixture.cs](../../examples/FullLifecycle/ExampleMod.Tests/GameSessionFixture.cs) and the skip pattern in [ExampleSession.cs](../../examples/FullLifecycle/ExampleMod.Tests/ExampleSession.cs). Without its private session files, the native test is skipped with the reason, so `dotnet test` stays offline.

## What every run does

| Behaviour | Rule |
|---|---|
| Profiles | Each owned process runs from a disposable loader profile; the install is only read, and its game files are hard-linked into the profile (a macOS client runs its bundle in place), never copied ([profiles](../reference/sessions-profiles.md)) |
| Hosts | Each machine is a host with one shell; Linux hosts run the dedicated server only ([game hosts](../reference/sessions-hosts.md)) |
| Claims | A run takes each host's lock and leases each client's Steam account ([locks and leases](../reference/sessions-leases-and-recovery.md)) |
| Journal | Every profile, copy, character and process is journalled before it is made, for `valheim-test env status` and `env recover` |
| User files | A client's recent-servers list is moved aside before launch and restored after ([the user's files](../reference/sessions-leases-and-recovery.md#the-users-files)) |
| Report | `result.json` and `junit.xml`, with every step's phase and four states ([phases and states](../reference/sessions-pinned-server-run.md#phases-and-states)) |

## Reference pages

| Types | Page |
|---|---|
| `GameSession`, `ServerActor`, `ClientActor`, `HostingClientActor`, `HostedClientRounds` | [Game sessions](../reference/sessions-game-session.md) |
| `PinnedServerRun`, `PinnedServerRunOptions<TPlan>`, `ModDeclaration` | [Pinned server runner](../reference/sessions-pinned-server-run.md) |
| `IGameHost`, `HostServer`, `InteractiveClient` | [Game hosts](../reference/sessions-hosts.md) |
| `EnvironmentInventory` with a file | [Inventory file](../reference/sessions-inventory.md) |
| The profile contract, `--in-place` | [Disposable profiles](../reference/sessions-profiles.md) |
| `SteamAccountHold`, `EnvironmentRuns`, `OwnedCopies`, `RunCancellation` | [Locks, leases and recovery](../reference/sessions-leases-and-recovery.md) |
| `HostedCampaignManifest`, `HostedCampaignPreparation` | [Campaigns](../reference/sessions-campaigns.md) |
| `TargetedRegression`, `StagedArm` | [Targeted regression](../reference/sessions-targeted-regression.md) |
| `WorldDump` | [Pinned world dumps](../reference/world-dump-contract.md) |
| `ReviewCapture`, `ReviewClip` | [Evidence](../reference/evidence.md) |

[Profiles and hosts](../concepts/profiles-and-hosts.md) explains the ideas behind these pages.

## Test fakes

`FakeGameSession.Create` and `FakeGameSession.Hosted`, in `Valheim.Testing.GameSessions.Fakes`, build a session over scripted clients, with no game. The [`Valheim.Testing.Game` fakes](Valheim.Testing.Game.md#test-fakes) work with them.

## Limits

- A pass with fakes is an orchestration test, not an in-game pass.
- A client runs only in a signed-in desktop session with Steam; the toolkit never signs in to Steam.
- The output holds local paths, account identifiers and a copy of the user's recent-servers list. Keep it private.
