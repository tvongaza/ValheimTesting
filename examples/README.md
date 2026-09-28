# Choose an example

Start with the [getting-started guide](../docs/getting-started.md) to build the local package feed. Run commands from the repository root. All examples are built by `python3 scripts/validate.py`; validation executes only NoGameTerrain and SharedWorld and never starts Valheim.

| Example | Question it answers | Game effects |
|---|---|---|
| [NoGameTerrain](NoGameTerrain/README.md) | How do independent expectations and exact replay work? | None; no game needed |
| [SharedWorld](SharedWorld/README.md) | How do composed terrain, zone seams and isolated snapshots work? | None; no game needed |
| [GameObserve](GameObserve/README.md) | Can I verify pins and read a generator sample through ValheimCLI? | Read-only attachment |
| [TerrainCheck](TerrainCheck/README.md) | Do declared generator or loaded-ground heights match? | Read-only attachment |
| [ClientSurfaceCheck](ClientSurfaceCheck/README.md) | Does a client have the expected heightmap, collider and stationary support? | Read-only; arrange arrival separately |
| [PaintCheck](PaintCheck/README.md) | Do loaded paint RGBA channels match an independent plan? | Read-only; does not load terrain |
| [ReloadCheck](ReloadCheck/README.md) | Does optional extension replacement clean up and preserve the connection? | Replaces a disposable probe DLL; creates/removes its test resource |
| [WalkingReview](WalkingReview/README.md) | Is there a usable trace for a human walking verdict? | Read-only recorder; a person drives |

Observation tools attach to an already prepared game and never launch it. ReloadCheck requires an owned scripts directory on the same machine as its ValheimCLI connection. The Roads repository owns examples of complete [server lifecycle and mod scenarios](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.SystemTests/README.md).

Coordinates in sample JSON are illustrative, not known Valheim sites. A captured value can be replayed as input; comparing it with itself is not independent correctness evidence. Use a new output directory for each attempt, keep failed results, and do not commit private logs or credentials.
