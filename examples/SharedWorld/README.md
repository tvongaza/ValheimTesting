# SharedWorld

Run `dotnet run --project examples/SharedWorld -c Release` from the repository root. No Valheim, Steam, ValheimCLI or native binaries are needed. Exit 0 and `PASS` mean the declared fixture expectations passed.

Builds a sloped background and a rectangular terrace, samples two adjacent zones with 65×65 height vertices and independently positioned 32×32 paint texels, takes an isolated in-memory snapshot, and deliberately updates each copy of their shared edge. Expected heights are declared by the fixture, not read from a mod's output.

The stand-in assignment illustrates the boundary; real mods should call their own writer. See the [fixture guide](../../docs/shared-world.md) and its Roads/MWL examples. This is not a Valheim save, native paint layout or physics simulation.
