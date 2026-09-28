# Loaded paint checks

`PaintCheck <host> <port> <pins-file> <plan.json> <new-output-directory>`

Requires the ValheimCLI World Tools pack with `valheim.world/terrain-paint`. It reads the
loaded heightmap's raw paint texture, not a compiler stamp or a screenshot. No
terrain is generated, loaded or edited by the observer. Missing/unreadable maps
and out-of-range texels are incomplete, never successful black/unpainted readings.
Integer x/z coordinates select native one-metre texels; RGBA are normalized
channels. Alpha is measured separately; it can carry vegetation/lava information,
so this tool does not label it universally as transparency or cleared grass.

Example schema (coordinates/colors are illustrative, not a known game fixture):

```json
{"expectedFrom":"independently declared fixture mask","tolerance":0.01,
 "samples":[{"x":32,"z":0,"r":1,"g":0,"b":0,"a":0.25}]}
```

Declare the paint profile before writing terrain. Include core, fading edge,
untouched ground with nonzero pre-existing paint, and both sides of a zone seam.
Run before/after a confirmed save and restart with the same pins and plan; repeat
on a ValheimCLI-only client. Raw mask agreement does not establish rendered appearance.
The bounded native paved-core/verge check passed on Valheim 1.0.16: 16 saved RGBA samples across two zones, alpha preserved, on a ValheimCLI-only client before and after confirmed save/server restart/rejoin. The unchanged-paint negative expectation failed exactly eight painted samples. Native dirt and fading-edge coverage remain follow-ups; a raw mask match is not a visual verdict.

From the repository root, after [bootstrap and pin setup](../../docs/getting-started.md):

```sh
dotnet run --project examples/PaintCheck -c Release -- \
  127.0.0.1 5555 /absolute/pins.txt /absolute/paint-plan.json /new/paint-first
# After the owner confirms save, restart, rejoin and arrival, repeat the same plan:
dotnet run --project examples/PaintCheck -c Release -- \
  127.0.0.1 5555 /absolute/pins.txt /absolute/paint-plan.json /new/paint-reload
```

Outputs include `result.json`, `junit.xml`, command transcripts and `paint.json` with expected/actual channels and residuals. Existing output directories are refused. Exit 0 means this plan passed; exit 1 means failure (including incomplete measurements), and exit 2 means invalid invocation. The tool does not save or restart the game for you. See [ClientSurfaceCheck](../ClientSurfaceCheck/README.md) to pair paint with independent height/collision observations.
