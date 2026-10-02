# Pinned ValheimCLI world dumps

`cli_world_dump` samples the **world generator**, not the loaded heightmap, paint, object state or a player's footing. A bounded CSV is useful as an offline input for terrain fixtures and maps. It is not native evidence of a road or another mod's edits.

On a disposable world, install a coherent ValheimCLI core and Standard pack, use strict expectations for every command, and record the exact game build, `cli_world` UID and seed. Then issue one coarse dump and a smaller fine window in that same run. For example, with a reviewed pins file, an owned server listening on port 5558 and a private output directory:

```sh
valheim-cli --port 5558 --expect-strict pins.txt cli_world
valheim-cli --port 5558 --expect-strict pins.txt cli_world_dump 128 /private/dumps --window 0,0,1024
valheim-cli --port 5558 --expect-strict pins.txt cli_world_dump 5 /private/dumps --window -950,-1080,50
```

The numerical windows are examples, not preferred gameplay sites. Wait for each complete `OK: WORLD_DUMP` reply before reading its file. Preserve the full command and reply, header, game build, world UID/seed, step, min/max x/z, sample count and file SHA-256. Keep the complete CSV private. Use a new output directory for each attempt. `WorldDumpContract.Verify` rejects a changed file, wrong step or bounds, missing native layer, incomplete grid, or reply inconsistent with its CSV. `RequireSameWorld` checks declared identity when combining coarse and fine layers. The CSV itself has no world UID: the same-session strict observation and retained evidence establish that association.

The native header currently checked is `x,z,height,biome,river,river_width,base_height`. Coordinates are horizontal x/z in metres, on ValheimCLI's lattice `-10000 + i*step`; increasing x and z follow their respective world axes. `height` is `WorldGenerator.GetHeight` rounded to 0.1 m. `river` is weight rounded to 0.01; `river_width` is rounded to 0.1. `base_height` is `WorldGenerator.GetBaseHeight(x,z,false)` rounded to 0.00001: a **unitless generator value**, not a second ground elevation. Node values are exact to the file's precision. `GridDumpTerrain` uses bilinear interpolation between nodes as an approximation, never as a native replay. A native field that disappears or changes meaning is a contract change to investigate, not a default value to invent.

The small checked rows in `WorldDumpContractTests` came from a bounded disposable Valheim 1.0.16 capture. Its full files and world save are not distributed. When updating for a new build, repeat the strict bounded capture, inspect the header and representative raw rows, compare against the game-side exporter, update the checked fixture and contract as needed, and run the malformed/truncated and mixed-world negative tests. A new field or changed units needs an explicit API decision before layered offline readers use it.
