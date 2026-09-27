# ValheimTesting

Reusable test inputs, fixtures and assertions for Valheim mods. Most tests run without Valheim, Unity, Steam or a dedicated server. Optional system tests use the real game through the CLI.

## Packages and ownership

| Package | Purpose | Runtime |
|---|---|---|
| `Valheim.Testing` | Synthetic terrain and exact recorded-input replay; no CLI dependency | netstandard2.0 |
| `Valheim.Testing.Game` | Typed observations, fixtures, owned server sessions, comparisons and JSON/JUnit reports | net9.0 |
| `Valheim.Cli.Testing` | Transport and YAML runner, maintained in the CLI repository | net9.0 |

The dependency goes **ValheimTesting → CLI**, never the reverse. Game-side extension API and observers stay in CLI. Roads and MWL own their optional adapters and scenarios. No test package belongs in an ordinary player's plugin folder.

Until upstream merges the required transport/API changes, `cli-dependency.json` pins our CLI fork by full commit and exact package version. This is not a claim that preview packages are on NuGet. Bootstrap builds that one dependency from tracked source into an ignored local feed; it does not compile or launch the game plugin.

## Start without a game

Requires .NET 10 SDK (tests) and Python 3.12+ (bootstrap). Libraries remain net9/netstandard2.0; examples allow a newer runtime.

```sh
python3 scripts/bootstrap-cli.py
# Or use a local CLI clone; the script exports only the pinned commit:
# python3 scripts/bootstrap-cli.py --source /path/to/valheimCLI
python3 scripts/validate.py
dotnet run --project examples/NoGameTerrain -c Release
```

`validate.py` runs the local library tests, builds all external examples and packs the two libraries to `.packages`. It never starts Valheim. The CLI transport and its tests remain upstream-owned, not copied here. To test a mod, add this local feed plus nuget.org, then pin `Valheim.Testing` to `0.1.0-preview.4` and `Valheim.Testing.Game` to
`0.1.0-preview.5` (adds paint and human-walk evidence). CLI remains pinned at preview.4.

## Test pyramid

1. **Unit/synthetic:** real mod decisions against small explicit inputs. Broad and fast.
2. **Integration:** transport, adapters, fixture handling and orchestration with controlled doubles.
3. **Bounded game checks:** a few zones on a disposable server and CLI-only client; save/restart and replication when those boundaries change.
4. **Human judgement:** short visual/walking checks when usability matters.

Synthetic inputs are not Valheim's generator. Replaying captured inputs is not independent proof of the game's physics. Incomplete observations fail explicitly. Session ownership does not own Steam accounts, station claims or another operator's running game.

## Evidence and limits

Before extraction, the library passed 63 tests; Roads' adapter verified 100 declared terrain/collider samples, and a CLI-only client verified 15 native terrain/collider samples plus grounded stationary support before and after server save/restart/rejoin. A wrong unchanged-ground expectation failed eight samples. Extraction is verified locally and does not imply another game run.

This is not arbitrary terrain/paint coverage, human walking acceptance, or MWL port gameplay validation. MWL's full-mode payment/delivery/ownership gate remains pending. See [the detailed guide](docs/testing-toolkit.md), [source provenance](PROVENANCE.md), and examples.

Preview APIs may change. No release or upstream merge is implied by this repository.

Follow-up examples: `PaintCheck` compares loaded raw paint channels;
`WalkingReview` records a human-driven traversal without moving the character.
Both build locally; native paint and human walking acceptance remain pending.
