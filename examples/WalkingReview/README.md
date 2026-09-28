# Record a human-driven walking review

`WalkingReview <host> <port> <pins-file> <route.json> <seconds 5..600> <new-output-directory>`

From the repository root, after [bootstrap and pin setup](../../docs/getting-started.md):

```sh
dotnet run --project examples/WalkingReview -c Release -- \
  127.0.0.1 5555 /absolute/pins.txt /absolute/route.json 120 /new/walk-result
```

Attach to a disposable, correctly pinned client with CLI World Tools installed.
Arrange arrival and character protection separately; verify protection before
walking. The observer issues no movement, teleport, cheats or save commands.
A person drives normally while it samples local position, motion and support.
Do not use this as an unattended survival test.

The route is an ordered array of checkpoints with x/y/z, horizontal `radius` and
`heightTolerance`. Adjacent checkpoint areas must not overlap. Choose visible
landmarks at the approach, turn entry/apex/exit and destination, in each direction.
Do not place checkpoints so densely that ordinary sampling skips them.

```json
[{"x":0,"y":10,"z":0,"radius":2,"heightTolerance":1},
 {"x":20,"y":12,"z":0,"radius":2,"heightTolerance":1}]
```

This is an illustrative route, not an instruction to move to those coordinates.

The recorder writes command replies, every sample, plan/pin hashes, JSON/JUnit
capture results and a separate `review.json`. Exit 0 means the trace qualifies
for review: checkpoints were reached in order without large gaps, teleport-like
position changes, flying, attachment or death, with sufficient grounded samples.
It does **not** mean the road is usable. The human verdict starts `not-reviewed`.
An unflagged small teleport or assisted movement cannot always be identified by
telemetry; the human reviewer must confirm the run was ordinary walking.

Review checklist, keeping the trace unchanged:
- Did the character actually walk the road in both directions?
- Was climbing/jumping required where an ordinary walk was intended?
- Did a switchback merge into the other leg or require an awkward turn?
- Were rocks, stairs, edges, bridge supports or water obstructive?
- Did the approach meet the POI at a usable height?
- Record accepted / needs-work / not-reviewed, the issue's coordinates, and a
  screenshot or short clip for any issue. A small cosmetic bump may be accepted.

Append notes in a separate human-review document (or fill `HumanVerdict`/`Notes`
in review.json while preserving samples and the original generated report).
Telemetry issues invalidate the observation claim, not automatically the road.
If a capture fails partway, partial samples remain but cannot count as a pass.
No human walking acceptance has been performed for this follow-up yet.
