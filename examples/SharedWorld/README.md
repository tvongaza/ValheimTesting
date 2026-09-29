# SharedWorld

Run `dotnet run --project examples/SharedWorld -c Release` from the repository root. No Valheim, Steam, ValheimCLI or native binaries are needed. Exit 0 and `PASS` mean the declared fixture expectations passed.

Builds a sloped background and a rectangular terrace, samples two adjacent zones with 65×65 height vertices and independently positioned 32×32 paint texels, takes an isolated in-memory snapshot, and deliberately updates each copy of their shared edge. Expected heights are declared by the fixture, not read from a mod's output.

It then writes the same ground as a small grid dump in `cli_world_dump`'s CSV layout, reads it with `GridDumpTerrain`, checks it against the composed terrain at every node with `TerrainParity`, and checks that the same dump shifted one node east fails. Pass a file path (`dotnet run --project examples/SharedWorld -c Release -- terrace.png`) to also write a PNG of the dump with the terrace outlined.

The stand-in assignment illustrates the boundary; real mods should call their own writer. See the [fixture guide](../../docs/shared-world.md) and its Roads/MWL examples. This is not a Valheim save, native paint layout or physics simulation.
