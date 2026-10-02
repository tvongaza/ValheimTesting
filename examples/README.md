# Choose an example

Bringing an existing mod? Start with [ModWithTests](ModWithTests/README.md), then the [adoption guide](../docs/adopting.md). Use [getting started](../docs/getting-started.md) for package availability and feeds. Run commands from the repository root. All examples are built by `dotnet run scripts/validate.cs`; validation executes NoGameTerrain, SharedWorld and the five ModWithTests cases and never starts Valheim.

| Example | Question it answers | Game effects |
|---|---|---|
| [ModWithTests](ModWithTests/README.md) | How do I test my actual mod source with shared game doubles? | None; no game or ValheimCLI needed |
| [FullLifecycle](FullLifecycle/README.md) | How does one mod test one feature at every layer, from unit tests to an owned server and client through save, restart and rejoin? | Integration tests: none. The runner owns a dedicated server copy and, in owned mode, the client; it stops only what it started |
| [NativeSmoke](NativeSmoke/README.md) | Can one or several selected mods load in a disposable client or dedicated server, optionally joined by a clean client? Can an A/B run remove one mod while keeping the loader and other inputs fixed? | Copies installs, starts only owned game processes, and records private load/join evidence; it does not test gameplay behavior |
| [TargetedRegression](TargetedRegression/README.md) | How do I check a parent and a candidate build of one mod in the real game, with a preflight that needs no game? | `preflight` none; `run` creates and owns a disposable install copied from a prepared game, then hosts a disposable world in one owned client |
| [RegressionBundle](RegressionBundle/README.md) | How do I share a targeted regression's source and result without my machine's paths, names or unrelated plugins? | None in the game; reads private evidence and writes a new directory for review; publishes nothing |
| [NoGameTerrain](NoGameTerrain/README.md) | How do independent expectations and exact replay work? | None; no game needed |
| [SharedWorld](SharedWorld/README.md) | How do composed terrain, zone seams, isolated snapshots and grid-dump parity work? | None; no game needed; writes a PNG only when given a path |
| [SessionControl](SessionControl/README.md) | How do I inspect readiness and explicitly join, leave or confirm a save? | Explicit session/save mutations; no process ownership |
| [GameObserve](GameObserve/README.md) | Can I verify pins and read a generator sample through ValheimCLI? | Read-only attachment |
| [TerrainCapture](TerrainCapture/README.md) | How do I save a bounded native terrain grid for exact replay? | Read-only attachment; writes a new local file |
| [TerrainCheck](TerrainCheck/README.md) | Do declared generator or loaded-ground heights match? | Read-only attachment |
| [ClientSurfaceCheck](ClientSurfaceCheck/README.md) | Does a client have the expected heightmap, collider and stationary support? | Read-only; arrange arrival separately |
| [PaintCheck](PaintCheck/README.md) | Do loaded paint RGBA channels match an independent plan? | Read-only; does not load terrain |
| [ReloadCheck](ReloadCheck/README.md) | Does optional extension replacement clean up and preserve the connection? | Replaces a disposable probe DLL; creates/removes its test resource |
| [WalkingReview](WalkingReview/README.md) | Is there a usable trace for a human walking verdict? | Read-only recorder; a person drives |
| [LinuxServerSmoke](LinuxServerSmoke/README.md) | Does a server runtime start with BepInEx and load a new world on this host? | Starts and stops one owned dedicated server; BepInEx writes into the disposable runtime |

Observation tools attach to an already prepared game and never launch it. LinuxServerSmoke is the exception: it owns and stops the one server it starts, usually inside the [Linux image](../docker/linux-server/README.md). ReloadCheck requires an owned scripts directory on the same machine as its ValheimCLI connection. Its game-side probe, [`ReloadCheck/ReloadProbe`](ReloadCheck/ReloadProbe/Plugin.cs), builds against a game install like FullLifecycle's mod, so validation does not build it. The Roads repository owns examples of complete [server lifecycle and mod scenarios](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.SystemTests/README.md).

Coordinates in sample JSON are illustrative, not known Valheim sites. A captured value can be replayed as input; comparing it with itself is not independent correctness evidence. Use a new output directory for each attempt, keep failed results, and do not commit private logs or credentials.

For a mod's edit-build-test loop (build, install, launch, run a strict plan, summarise the log) use the scripts in [`tools/dev-loop`](../tools/dev-loop/README.md) rather than an example project; they drive the `valheim-cli` executable.

Want to contribute an example or missing shared helper? See the [contribution guide](../CONTRIBUTING.md), including the worked terrain recipe and validation expectations.
