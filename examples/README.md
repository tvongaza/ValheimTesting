# Choose an example

Bringing an existing mod? Start with [ModWithTests](ModWithTests/README.md), then the [adoption guide](../docs/adopting.md). Use [getting started](../docs/getting-started.md) for package availability and feeds. Run commands from the repository root. All examples are built by `dotnet run scripts/validate.cs`; validation runs SharedWorld and the ModWithTests tests and never starts Valheim.

| Example | Concept | Audience | Prerequisites |
|---|---|---|---|
| [ModWithTests](ModWithTests/README.md) | Unit-test the mod's real source with shared game doubles | Mod author | .NET 10 SDK; no game or ValheimCLI |
| [SharedWorld](SharedWorld/README.md) | The pure terrain library: composed terrain, zone seams, snapshots, grid-dump parity | Mod author | .NET 10 SDK; no game |
| [FullLifecycle](FullLifecycle/README.md) | One feature at every layer, from unit tests to an owned server and clients through save, restart and rejoin | Mod author | Integration tests: .NET 10 SDK. System tests: a game install, Steam and ValheimCLI; the runner owns the copies it starts |
| [TargetedRegression](TargetedRegression/README.md) | A/B regression: a parent and a candidate build of one mod in the real game | Mod author | `preflight`: none. `run`: a prepared game install, from which it copies a disposable one |
| [ObserveCheck](ObserveCheck/README.md) | Attach to a prepared game, verify strict pins, run one probe (heights, client surface, paint, capture, walk, session), keep the report | Mod author | A running disposable game with ValheimCLI (World Tools for the terrain probes); it never launches one |
| [NativeSmoke](NativeSmoke/README.md) | Can selected mods load in a disposable client or dedicated server, and does an A/B run isolate one of them? Moves to `src/` with #291 | Mod author, toolkit maintainer | A prepared game install; copies it and starts only owned processes |
| [RegressionBundle](RegressionBundle/README.md) | Share a targeted regression's source and result without private paths. Moves to `tools/` with #290 | Toolkit maintainer | TargetedRegression evidence |

Not examples: the Linux image's boot check is [`docker/linux-server/smoke`](../docker/linux-server/smoke/README.md), the ScriptEngine extension reload check is [`tools/reload-check`](../tools/reload-check/README.md), and a mod's edit-build-test loop is [`tools/dev-loop`](../tools/dev-loop/README.md). The Roads repository owns examples of complete [server lifecycle and mod scenarios](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.SystemTests/README.md).

Coordinates in sample JSON are illustrative, not known Valheim sites. A captured value can be replayed as input; comparing it with itself is not independent correctness evidence. Use a new output directory for each attempt, keep failed results, and do not commit private logs or credentials.

Want to contribute an example or missing shared helper? See the [contribution guide](../CONTRIBUTING.md), including the worked terrain recipe and validation expectations.
