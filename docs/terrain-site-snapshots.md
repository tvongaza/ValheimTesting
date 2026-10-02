# Read-only terrain site snapshots

`TerrainSiteSnapshot` records loaded ground, terrain collider height and paint at a small, named site. It uses ValheimCLI's World Tools commands (`cli_area_ready`, `cli_ground_height`, `cli_surface_at`, `cli_paint_at`) and retains their exact replies. It does not change terrain, place objects or inspect ZDO contents. Use it to compare a site before and after a mod action or after a save and restart; use separate assertions for gameplay behavior.

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

report.AttachTerrainSnapshot(before);
report.AttachTerrainSnapshot(after);
report.Write(outputDirectory); // result.json links to terrain-snapshots/site-001.json and site-002.json
```

For a failure-only capture, use `report.StepWithTerrainOnFailure("check cave", assertion, () => TerrainSiteSnapshot.Capture(...))`. It rethrows the original assertion failure; a failed capture is also recorded as a failed report step.

Capture is intentionally bounded to 1–16 distinct integer x/z points and a readiness wait of at most two minutes. A command with an incomplete, stale, wrong-coordinate or missing terrain reply fails rather than inventing a sample. A timeout names the last area state. The report stores raw replies with role, command and UTC time; review them for private world or plugin details before publishing. `Compare` omits timestamps and raw replies from the numeric delta, but it requires the same site, world and ordered points.

This is a **loaded client observation**, not a generator-height oracle. For generator terrain, use [world dumps](world-dump-contract.md) or [TerrainCapture](../examples/TerrainCapture/README.md) with its layer stated explicitly.
