# ValheimTesting

Reusable test inputs, fixtures and assertions for Valheim mods. Most tests run without Valheim, Unity, Steam or a dedicated server. Optional system tests use the real game through ValheimCLI.

New to the framework? Follow [Add testing to a mod](docs/getting-started.md), then choose a runnable example from the [example index](examples/README.md). Existing mod tests stay in their own repository; shared helpers and lifecycle code are adopted gradually.

## Contribute

Mod developers and coding agents are welcome to help extend the synthetic world, capture/replay, observations and lifecycle tools. Read [CONTRIBUTING.md](CONTRIBUTING.md) for repository ownership, a worked fixture example, local checks and PR expectations. Useful local contributions can be reviewed before native testing; mark that evidence as not run.

## AI agent entry point

Start with [AGENTS.md](AGENTS.md) and the [agent workflow](docs/agent-guide.md). A [CLAUDE.md](CLAUDE.md) pointer keeps Claude-based assistants on the same instructions. The guide covers tool discovery, strict pins, one-shot mutations, result interpretation, test-layer limits and restoration/handoff.

## Packages and ownership

| Package | Purpose | Runtime |
|---|---|---|
| `Valheim.Testing` | Composable terrain, multi-zone height/paint fixtures and exact recorded-input replay; no ValheimCLI dependency | netstandard2.0 |
| `Valheim.Testing.Game` | Typed observations, fixtures, owned server sessions, comparisons and JSON/JUnit reports | net9.0 |
| `Valheim.Cli.Testing` | Transport and YAML runner, maintained in the ValheimCLI repository | net9.0 |

The dependency goes **ValheimTesting → ValheimCLI**, never the reverse. Game-side extension API and observers stay in ValheimCLI. Roads and MWL own their optional adapters and scenarios. No test package belongs in an ordinary player's plugin folder.

Until upstream merges the required transport/API changes, `cli-dependency.json` pins our ValheimCLI fork by full commit and exact package version. This is not a claim that preview packages are on NuGet. Bootstrap builds that one dependency from tracked source into an ignored local feed; it does not compile or launch the game plugin.

## Start without a game

Requires .NET 10 SDK (tests) and Python 3.12+ (bootstrap). Libraries remain net9/netstandard2.0; examples allow a newer runtime.

```sh
python3 scripts/bootstrap-cli.py
# Or use a local CLI clone; the script exports only the pinned commit:
# python3 scripts/bootstrap-cli.py --source /path/to/valheimCLI
python3 scripts/validate.py
dotnet run --project examples/NoGameTerrain -c Release
```

`validate.py` runs the local library tests, builds all external examples and packs the two libraries to `.packages`. It never starts Valheim. The ValheimCLI transport and its tests remain upstream-owned, not copied here. To test a mod, add this local feed plus nuget.org, then pin `Valheim.Testing` to `0.1.0-preview.5` and `Valheim.Testing.Game` to
`0.1.0-preview.8` (uses the new pure fixture package). ValheimCLI remains pinned at preview.4.

## Test pyramid

1. **Unit/synthetic:** real mod decisions against small explicit inputs. Broad and fast.
2. **Integration:** transport, adapters, fixture handling and orchestration with controlled doubles.
3. **Bounded game checks:** a few zones on a disposable server and ValheimCLI-only client; save/restart and replication when those boundaries change.
4. **Human judgement:** short visual/walking checks when usability matters.

Synthetic inputs are not Valheim's generator. Replaying captured inputs is not independent proof of the game's physics. Incomplete observations fail explicitly. Session ownership does not own Steam accounts, station claims or another operator's running game.

## Evidence and limits

Local validation covers synthetic inputs, observation contracts and lifecycle failure handling. Roads supplies the first native scenarios: a 100-sample declared terrain/collider calibration, followed by a persistent two-zone native fixture. On Valheim 1.0.16, a ValheimCLI-only client matched 15 height/collider samples and three stationary grounded observations before and after confirmed server save/restart/rejoin. The later paint arm matched all 16 paved-core/verge RGBA samples across the same lifecycle. An unchanged-paint negative expectation failed exactly the eight painted samples and passed the untouched eight.

The paint fixture starts from explicit saved RGBA, preserves alpha and samples both sides of a zone seam. It does not establish arbitrary terrain, native dirt/fading-edge behavior, rendered appearance or human walking usability. MWL's full-mode payment/delivery/ownership gate remains pending. See [the detailed guide](docs/testing-toolkit.md), [source provenance](PROVENANCE.md), and examples.

Preview APIs may change. No release or upstream merge is implied by this repository.

Follow-up examples: [PaintCheck](examples/PaintCheck/README.md) compares loaded raw paint channels; [WalkingReview](examples/WalkingReview/README.md) records a human-driven traversal without moving the character.
Both build locally; the bounded paved paint/reload check passed, while human walking acceptance remains pending.

## License

MIT, copyright © 2026 Tys von Gaza. See [LICENSE](LICENSE). Attribution for imported and adapted code is retained in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); both files are included in the library packages.

Shared-world fixtures: [guide and mod consumers](docs/shared-world.md), [runnable example](examples/SharedWorld/README.md). Height and paint grids are independent; snapshots are in-memory test state, not native saves.
