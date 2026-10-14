# ValheimCLI fork and release contract

The [ValheimCLI fork](https://github.com/tvongaza/valheimCLI) is a release dependency of ValheimTesting. You can publish ValheimTesting without an upstream merge. The fork owns three things: the transport protocol, the default game-side plugin with all built-in commands, and the extension API. This repository builds the fork's `Valheim.Cli.Testing` source unchanged into `Valheim.Testing.Cli`. It also owns that NuGet package's version and publication. [`cli-dependency.json`](../../cli-dependency.json) is the single source pin for the transport build and the game-side plugin bundle. [`loader-dependency.json`](../../loader-dependency.json) pins the loader shipped for disposable installs.

## What the toolkit uses from the fork

| Toolkit part | Uses |
|---|---|
| Host-side `Game` package | `ValheimClient`: connection, completion support, command timeout and state-change subscription |
| Host-side `Game` package | `CommandResult` fields: `Ok`, output and error |
| `GameActor` | `cli_expect` before every action, and `cli_extensions` to discover schema-1 commands |
| `CliCapabilities.Toolkit` | Every built-in command the toolkit calls, checked against the pinned `valheimCLI.dll` |
| A scenario's plan | Extra commands from its own adapter, checked before launch and live before use |

`CliCapabilities.Toolkit` covers joining and hosted rounds, teleport signals, and support readings and waits. It also covers terrain, surface, paint and grid probes.

A run stops before its action in three cases:

- A required command is missing.
- A result version is unsupported.
- A strict pin fails.

## Move the fork pin

1. **Update the pin.** Review the fork commit and its single-plugin bundle. Update the commit, bundle URL and SHA-256 together in `cli-dependency.json`.
   - If the toolkit now needs a new built-in capability, record it in its owning `Game` API. Do not keep a separate list in the release manifest.
   - Update the pinned loader only if you reviewed a changed BepInExPack.
2. **Mark the transport as a local build.** In `cli-dependency.json`, set the Cli `packageVersion` to the current version plus `-candidate.<first 7 hex of the fork commit>`. Game's exact Cli reference reads this value.
   - The suffix keeps a local feed or NuGet cache from serving the old version's bytes.
   - Release preparation (`dotnet run scripts/pins.cs -- bump`, see [Contributing](../../CONTRIBUTING.md#release-packages-maintainers)) gives each affected package a version NuGet.org has never served. This covers the Cli, Game and every package that embeds or references them.
   - `release.yml` refuses a candidate version. It also refuses a transport version that NuGet.org already serves from another fork commit.
   - Leave the published-version snippets alone until NuGet.org serves the new versions.
3. **Build and validate.** Run the bootstrap command in [Contributing](../../CONTRIBUTING.md#set-up-and-validate-locally). It is the same on every OS. Then run:

   ```sh
   dotnet run scripts/validate.cs
   ```

   - The bootstrap exports only the pinned fork commit, builds the transport and checks the plugin and loader hashes.
   - Validation builds all packages and runs a fresh local-feed consumer.
   - Review the diff and the fork's license and provenance.
   - If an in-game command changed, run one bounded native smoke with strict pins. It must prove three things. The exact default plugin loads. Its capability schema holds. A missing or mismatched capability is refused before the scenario changes the world. Check [runtime hygiene](../runtime-hygiene.md) first.
4. **Merge and release.** Merge the reviewed change, then release from a tag on `main`. `release.yml` publishes the packages and waits for the NuGet.org-only consumer. It then writes `release-contract.json` from the **NuGet.org repository-signed package bytes**.
   - NuGet.org signs packages after upload. These hashes therefore differ from the unsigned GitHub release files hashed in `SHA256SUMS`. Both sets are correct for the bytes they name.
   - The contract refuses a transport whose published nuspec names another fork commit.
   - It also refuses a `Game`/`GameSessions` pair with the wrong exact dependency.
   - If a NuGet.org download fails after publication, re-run the failed workflow jobs. The publish job does not run again.
5. **Record the published versions.** Run the following, as described in [Contributing](../../CONTRIBUTING.md#release-packages-maintainers):

   ```sh
   dotnet run scripts/pins.cs -- record
   ```

   It moves copyable docs and examples to the published versions in one PR.

The GitHub release includes `release-contract.json`. It records:

| Entry | Contents |
|---|---|
| Packages | Every package version and its signed SHA-256 |
| Fork | Fork commit and bundle hash |
| Loader | Loader version and hash |
| Commands | Default commands and required built-in capabilities |
| Compatibility | Compatible `Game`, `GameSessions` and `NativeSmoke` versions |

## What the release contract is not

`release-contract.json` is a release record. It does not give permission to trust an arbitrary installed plugin.

- **Owned runs still check the plugin.** They compare the staged default plugin with its capability manifest and strict pin. They then check the live `cli_extensions` reply.
- **Attached clients need explicit pins.** An attached client must be pinned as well.
- **Earlier fork builds are allowed in tests.** A test may use an earlier coherent fork build if its required commands and schemas hold.
- **A release never mixes fork commits.** It does not silently combine a transport from one fork commit with a game-side plugin from another.
