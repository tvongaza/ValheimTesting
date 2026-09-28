# First read-only game observation

After [bootstrap and fixture/pin setup](../../docs/getting-started.md), attach to an already loaded disposable game with ValheimCLI core and World Tools:

```sh
# From the ValheimTesting root; replace the example coordinates with your own.
dotnet run --project examples/GameObserve -c Release -- \
  127.0.0.1 5555 /absolute/pins.txt 8 10
```

[Program.cs](Program.cs) verifies strict world/plugin pins, discovers `valheim.world/terrain`, reads one **generator** height at horizontal x/z coordinates, and requires a complete observation. It prints the JSON observation and demonstrates replaying that same input. Exit 0 means the pin/observation/replay checks succeeded; exit 1 is a failure and exit 2 is invalid usage.

It never starts/stops the game, moves the character, loads a zone, creates terrain compilers or edits ground. Generator height is not loaded ground, collision or paint. For an independently specified expected result, use [TerrainCheck](../TerrainCheck/README.md); for client height/collision use [ClientSurfaceCheck](../ClientSurfaceCheck/README.md). A round trip of one measured value is not a mod-correctness test.

This small example writes to stdout rather than a scenario output directory. Retain the input pins and loaded build information alongside a capture if you use it later; inspect them for private data before sharing.
