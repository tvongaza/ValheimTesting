# Evidence linked from result.json

*Assistant-written (Claude).*

A run's `result.json` lists every piece of evidence the scenario attached in its `Evidence` array: what it is, where it was taken, in which world, the file and the file's SHA-256. There is one record, `EvidenceReference(Kind, Site, WorldUid, File, Sha256)`, and one way in, `ScenarioReport.Attach`. A file inside the report directory is listed by its path relative to that directory; one outside it keeps its full path.

| Evidence | Kind | Site | File | Attached by |
|---|---|---|---|---|
| [Terrain site snapshot](terrain-site-snapshots.md) | `terrain-site` | the site name | `evidence/terrain-site-NNN.json`, written by `ScenarioReport.Write` | `report.Attach(snapshot)` |
| [Area object snapshot](area-object-snapshots.md) | `area-objects` | the site name | `evidence/area-objects-NNN.json`, written by `ScenarioReport.Write` | `report.Attach(snapshot)` |
| Review still (`ReviewCapture.Capture`) | `review-still` | the capture id | the JSON sidecar, which records the PNG's SHA-256 | `report.Attach(receipt.Evidence)` |
| Review clip (`ReviewClip.Capture`) | `review-clip` | the clip id | the JSON sidecar, which records the SHA-256 of `frames.csv` and of every frame | `report.Attach(receipt.Evidence)` |
| A round's JSON (`ClientRound.Write`), such as `arrival`, `zone-cycle` or `logout` | the name given to `Write` | the round name | `{round}-{name}.json` | `ClientRound.Write` itself |
| Anything else written by the scenario, such as [WalkingReview](../examples/WalkingReview/README.md)'s `review.json` | the scenario's own (`walking-review`) | the scenario's own | that file | `report.Attach(new EvidenceReference(...))` |

A kind is 1 to 40 lower-case letters, digits or hyphens. `Attach` refuses a reference without a file or a lower-case SHA-256.

To capture evidence only when an assertion fails, use `report.StepWithEvidenceOnFailure(name, assertion, () => TerrainSiteSnapshot.Capture(...))` (or any other snapshot). It rethrows the assertion's own failure; a capture that fails as well is recorded as a separate failed step.

## Review stills and clips are not evidence of correctness

`ReviewCapture` and `ReviewClip` make reproducible pictures for a person to look at. They prove that the picture was taken in the pinned world, with the declared conditions, and that its bytes are the ones the game wrote. They never assert that the scene looks right: each sidecar records `visualVerdict: "not asserted"`. Look at the image or the frames yourself and record the verdict separately, for example in the FullLifecycle example's `review.json`.

Both work the same way. The id is 1 to 64 letters, digits or hyphens. The adapter's review state is begun before and restored after the capture, including when the capture or the transfer fails or is canceled; a failed restore fails the capture. Evidence is written to a staging directory beside the evidence directory and moved into place whole, or deleted: a failed or canceled capture publishes nothing. The sidecar records the world UID, game build and plugin pins. See the [FullLifecycle example](../examples/FullLifecycle/README.md#repeatable-review-stills) for a complete run and the adapter commands each one needs.
