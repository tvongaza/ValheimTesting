# SharedWorld

This example tests terrain logic against the terrain doubles and declared expectations, with no game.

## Files

| File | What it holds |
|---|---|
| `Program.cs` | The fixture and every check. |
| `SharedWorld.csproj` | The project. The terrain doubles from `Valheim.Testing.Doubles` compile into it. |

## What it checks

1. It loads a sloped background with a terrace as two adjacent zones through `ValheimWorldScope`.
2. It raises the shared column in the west zone only. `TerrainAssert.SeamAgrees` must fail, and a `TerrainSnapshot` shows the east zone untouched.
3. It applies the same write to the east zone. Then the seam check passes.
4. It reads the same ground as a grid dump with `GridDumpTerrain`. It checks the dump with `TerrainParity` at every node.
5. It checks that the same dump, shifted one node east, fails.

The fixture declares the expected values. They are not read from a mod's output.

## Run

You need only the .NET 10 SDK. From the repository root, run:

```sh
dotnet run --project examples/SharedWorld -c Release
```

If every check passes, the run exits 0 and prints this line:

```text
PASS: composed terrain, a one-sided seam write caught by the terrain doubles and then fixed, grid dump parity with a shifted-grid control. No game used.
```

If you also want a PNG of the dump with the terrace outlined, pass a file path:

```sh
dotnet run --project examples/SharedWorld -c Release -- terrace.png
```

The run writes the PNG to that path.

## Limits

This is not a Valheim save or a physics simulation. The stand-in writer only shows the seam boundary. Your mod calls its own writer against the same doubles.

## Related

- [Test terrain decisions](../../docs/guides/test-terrain-decisions.md)
- [`Valheim.Testing` package](../../docs/packages/Valheim.Testing.md)
