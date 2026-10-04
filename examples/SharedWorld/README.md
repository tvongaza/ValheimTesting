# SharedWorld

Run `dotnet run --project examples/SharedWorld -c Release` from the repository root. No Valheim, Steam, ValheimCLI or native binaries are needed; the terrain doubles from `Valheim.Testing.Doubles` compile into the example as they do into a mod's test project. Exit 0 and `PASS` mean the declared fixture expectations passed.

Builds a sloped background and a rectangular terrace and loads two adjacent zones from it as the game holds them (a `Heightmap` and `TerrainComp` each, through `ValheimWorldScope`). A stand-in writer raises the shared column in the west zone only; `TerrainAssert.SeamAgrees` must fail, a `TerrainSnapshot` shows the east zone untouched, and after the same write reaches the east zone's copy of the edge the seam check passes. Expected heights are declared by the fixture, not read from a mod's output.

It then writes the same ground as a small grid dump in `cli_world_dump`'s CSV layout, reads it with `GridDumpTerrain`, checks it against the composed terrain at every node with `TerrainParity`, and checks that the same dump shifted one node east fails. Pass a file path (`dotnet run --project examples/SharedWorld -c Release -- terrace.png`) to also write a PNG of the dump with the terrace outlined.

The stand-in writer illustrates the boundary; real mods call their own writer against the same doubles. See the [fixture guide](../../docs/shared-world.md). This is not a Valheim save, native paint layout or physics simulation.
