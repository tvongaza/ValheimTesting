# Read-only client terrain and support check

Attach to an already prepared client with the stable CLI extension host. This
example never starts/stops a game, moves a player, changes cheats, or edits ground.
The owner of the test arranges arrival separately and backs up the test character.

```sh
dotnet run --project ClientSurfaceCheck.csproj -- localhost 5555 pins.txt plan.json /new/output
```

`pins.txt` must pin the world UID and exact loaded plugins. For a vanilla-client
replication test include the tested mod as `absent`, alongside the CLI's exact MD5.
Use only CLI on that client; the server may have the mod and its optional adapter.

```json
{
  "expectedFrom": "independent declared profile applied to captured pre-write native vertices",
  "tolerance": 0.05,
  "samples": [{"x":32,"z":0,"height":65}],
  "support": {"x":32,"z":0,"height":65}
}
```

These example coordinates are hypothetical. Use a real fixture's independent
expectations. Surface samples must be integer coordinates on Valheim's native
one-metre grid: `Heightmap.GetWorldHeight` reads the nearest vertex, whereas a
collider ray between vertices interpolates a triangle. Missing/unloaded ground
fails; it is never replaced by the generator.

Each sample requires both the loaded heightmap vertex and **that heightmap's own
mesh collider**, rather than an arbitrary floor/rock hit. Support is three
observations, half a second apart: within 2 m horizontally and 0.3 m vertically,
grounded, speed <=0.15 m/s, alive, not flying/attached/teleporting. These practical
tolerances test stationary support, not walking usability. God/ghost protection
is compatible with this check; flying is not.

JSON/JUnit, every surface residual, support observations and the command transcript
are written to a new directory. Wrong expectations are retained with a failed
result. Run the same plan again after a confirmed save, server restart and rejoin
for persistence evidence; compare world/build pins in both runs.

Package-only usage follows `TerrainCheck`: use a pinned `ToolkitPackageVersion`
and a NuGet feed containing all three matching toolkit packages. The example has
no game or mod binary dependencies.
