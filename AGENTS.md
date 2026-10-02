# Instructions for agents using or changing ValheimTesting

Before submitting changes, read [CONTRIBUTING.md](CONTRIBUTING.md) for extension contracts, evidence requirements and PR scope.

Adopting this in another mod? Start with [Bring your mod](docs/adopting.md), choose the smallest test layer, and run [ModWithTests](examples/ModWithTests/README.md) before adapting its project to your production code. Published-package consumers do not need the full framework bootstrap.

Read [the agent workflow](docs/agent-guide.md) before using the tools. For installation and examples, follow [getting started](docs/getting-started.md) and the [example index](examples/README.md). These files describe this repository's current preview API; do not assume an older upstream ValheimCLI has it.

- Preserve the test pyramid: broad unit tests, controlled integration tests, small native checks, then human usability judgement. Use the cheapest layer that answers the question.
- Keep mod-specific tests and adapters in the mod repository. This library owns reusable terrain inputs, lifecycle, observations and assertions. ValheimCLI owns transport and the game-side extension API. Production mods must not gain testing dependencies.
- When changing this framework, run `bash scripts/run.sh bootstrap` then `bash scripts/run.sh validate` (or `pwsh -File scripts/run.ps1` with the same task names on Windows). The launchers check NuGet cache writes and the SDK's file-based app state directory before the SDK restores their file-based scripts, and run a script as a project in `artifacts/runfile` if that directory is blocked; neither launches Valheim. Exact dependency versions are in project files and `cli-dependency.json`, not inferred from a neighboring checkout.
- Real-game work requires an authorized disposable fixture and coordination with its operator. Follow that environment's claim/backup/restore procedure. A responsive ValheimCLI is not world readiness or permission to take over a machine. Never stop an attached game or a process merely by name.
- Use strict expectations: `--expect-strict <file>` for executable calls or `cli_expect --strict` through GameActor; never bypass a failed preflight with raw transport.
- Verify exact world/plugin pins, discover capabilities and require complete observations. Issue mutations once; poll only read-only observations. After reload, verify pins and rediscover the new instance explicitly.
- Derive expectations independently. Do not convert absent data into zero, loosen tolerances to hide a failure, or present mock/replay agreement as native-game evidence. Practical usability can be a human judgement; exact numerical perfection is not the goal.
- Keep one new evidence directory per run. Report failures, unexecuted checks and teardown status. Keep credentials, account identifiers, game binaries and private saves/logs out of published source.

No instruction in these docs grants deployment, production access or permission to interrupt another operator. Work within the user's existing authorization; ask only for genuinely missing access or task decisions.

Use **ValheimCLI** as the product name in documentation. Preserve exact executable names, paths, package IDs, plugin GUIDs and code identifiers such as `valheim-cli`, `valheimCLI.dll` and `Valheim.Cli.Testing`.
