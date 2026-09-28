# First example: no game required

From the repository root, after [bootstrap](../../docs/getting-started.md#1-build-the-preview-packages-without-valheim):

```sh
dotnet run --project examples/NoGameTerrain -c Release
```

[Program.cs](Program.cs) declares a plane with `height = 40 + x/4 - z/2`. Two expected values (37 and 36 metres) are calculated independently, not queried from the model. It then demonstrates exact recorded-input replay and refuses a biome lookup when no biome was captured. Success prints `PASS` and exits 0; an assertion failure exits nonzero.

No ValheimCLI connection, Valheim, Unity, Steam, game assets, process launch or fixture files are used. This example uses the Game package's comparison/report types, but pure mod tests can depend only on `Valheim.Testing` and pass `ITerrain` or a small adapter into their real code. See [Roads' adapter](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.Tests/SyntheticWorld.cs).

This establishes the declared synthetic/replay contracts. It does not emulate native noise, terrain compilation, colliders or save/load. Keep broad unit coverage here, then use a small game check for those boundaries.
