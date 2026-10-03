# Read-only object and structure snapshots

`AreaObjectSnapshot` adds optional object evidence beside a terrain snapshot. It reads saved ZDOs and, if requested, container contents from the **server**. It reads instantiated prefabs and, if requested, the game's current piece-support values from the **client**. It never settles support or changes the world. The two actors must be strictly pinned to the same world UID, with the client player and its area ready before capture.

```csharp
// server and client are pinned GameActor instances; the client has arrived at the site.
var objects = AreaObjectSnapshot.Capture(
    server, client, "bridge footing", expectedWorldUid,
    x: 16, z: -8, radius: 8, readinessTimeout: TimeSpan.FromSeconds(30),
    includeContainers: true, includeSupport: true);
report.AttachAreaObjectSnapshot(objects);
report.Write(outputDirectory);
// result.json links to area-object-snapshots/site-001.json with a SHA-256 hash.
```

The capture checks every list's final count against its rows and keeps the complete ValheimCLI replies, role, command and UTC time. Missing or contradictory summaries, unreadable inventories, wrong world/role and a stale world after sampling fail the capture. A support row can be `pending`, `remote` or `unowned`; it is evidence of what the client knew then, not proof the structure had settled. If the scenario needs settled support, wait for a separately observed transition before capturing.

This uses World Tools' `cli_area_ready`, `cli_ground_height` and `cli_piece_support`, plus the server's `cli_zdos_at`/`cli_containers_at` and the client's `cli_prefabs_at`. It requires a ValheimCLI build with the read-only coordinate-taking `cli_piece_support <x> <z> [radius]` form. The earlier no-coordinate form recomputed support and is **not** suitable for a read-only snapshot. `includeSupport: false` does not call it. Keep the radius small (1–64 m) and review raw replies for private world or container data before sharing evidence.

Terrain height and paint remain in [terrain site snapshots](terrain-site-snapshots.md). Use a mod-owned assertion to decide what objects should exist; a complete census alone is not a correctness verdict.
