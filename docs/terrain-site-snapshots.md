# Read-only terrain site snapshots

`TerrainSiteSnapshot` records loaded ground, terrain collider height and paint at a small, named site. It waits with ValheimCLI's structured `valheim.observe/zones` observation, then reads each point through the World Tools capabilities the probes use (`valheim.world/terrain` with `loaded-ground`, `valheim.world/terrain-surface`, `valheim.world/terrain-paint`), with the same readers as `TerrainProbe`, `SurfaceProbe` and `PaintProbe`: a snapshot is their readings without expectations. It retains every command and its exact reply. It does not change terrain, place objects or inspect ZDO contents. Every snapshot is linked from `result.json` (see [evidence](evidence.md)). Use it to compare a site before and after a mod action or after a save and restart; use separate assertions for gameplay behavior.

The client must be joined with a ready local player at the site. `GameActor` must have strict plugin and world pins. Pass a separately pinned server actor when the server's world and area readiness matter. The server never supplies the paint or collider readings: those are client-loaded layers. Move the player and verify arrival before taking the snapshot; the capture does not teleport.

```csharp
// client and server are already connected, strictly pinned GameActor instances.
// The client has arrived at the site and its player is ready.
var points = new[] {
    new TerrainSitePoint(16, -8),
    new TerrainSitePoint(20, -8),
};
var before = TerrainSiteSnapshot.Capture(
    client, "cave approach", expectedWorldUid, points,
    TimeSpan.FromSeconds(30), server);

// Perform the mod action, confirm the save, restart and rejoin with fresh pins.
var after = TerrainSiteSnapshot.Capture(
    clientAfterRejoin, "cave approach", expectedWorldUid, points,
    TimeSpan.FromSeconds(30), serverAfterRestart);

foreach (var change in TerrainSiteSnapshot.Compare(before, after))
    Console.WriteLine($"{change.Point}: ground {change.GroundHeight:+0.000;-0.000;0.000} m");

report.Attach(before);
report.Attach(after);
report.Write(outputDirectory); // result.json's Evidence links evidence/terrain-site-001.json and -002.json
```

Each reading is `(Point, GroundHeight, ColliderHeight, R, G, B, A)`: the loaded ground height, the height of the terrain vertex's own collider, and the raw paint texel in `PaintProbe`'s channels (R dirt, G cultivated, B paved, A vegetation: 1 where it may grow, 0 where cleared). For a failure-only capture, use `report.StepWithEvidenceOnFailure("check cave", assertion, () => TerrainSiteSnapshot.Capture(...))`. It rethrows the original assertion failure; a failed capture is also recorded as a failed report step.

Capture is intentionally bounded to 1–16 distinct integer x/z points and a readiness wait of at most two minutes. A reply that is incomplete, from another layer, in other units or for another point fails rather than inventing a sample; a refused command is recorded with its refusal before the capture fails. A timeout names the last area state. The report stores raw replies with role, command and UTC time; review them for private world or plugin details before publishing. `Compare` omits timestamps and raw replies from the numeric delta, but it requires the same site, world and ordered points.

This is a **loaded client observation**, not a generator-height oracle. For generator terrain, use [world dumps](world-dump-contract.md) or [ObserveCheck `capture`](../examples/ObserveCheck/README.md#capture-record-a-bounded-grid-for-exact-replay) with its layer stated explicitly.
