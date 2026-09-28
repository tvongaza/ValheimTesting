# Source provenance

Extracted from [tvongaza/valheimCLI](https://github.com/tvongaza/valheimCLI/tree/4c4bdc916db49682db855e55f361df47267c4152) at `4c4bdc916db49682db855e55f361df47267c4152`. The initial import accidentally reused the ValheimCLI repository’s license as this new project’s top-level license. ValheimTesting’s own MIT copyright is Tys von Gaza (2026). Original import and adapted Roads notices are preserved separately in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and included in both NuGet packages.

The transport stays ValheimCLI code: `scripts/bootstrap-cli.cs` packs ValheimCLI's `Valheim.Cli.Testing` project unchanged, at the commit pinned in `cli-dependency.json`, and ValheimTesting publishes it as `Valheim.Testing.Cli` with ValheimCLI's MIT license. No transport source is copied into this repository. Game and mod adapters remain in their respective repositories. No Valheim binaries, world saves or private run evidence are included.
