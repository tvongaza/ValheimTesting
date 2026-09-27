# Loaded paint checks

`PaintCheck <host> <port> <pins-file> <plan.json> <new-output-directory>`

Requires the CLI World Tools pack with `valheim.world/terrain-paint`. It reads the
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
on a CLI-only client. Raw mask agreement does not establish rendered appearance.
No new native paint run has been performed for this follow-up yet.
