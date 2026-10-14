# Valheim.Testing.Cli

[API reference](https://tvongaza.github.io/ValheimTesting/reference/) and [pinned ValheimCLI fork](https://github.com/tvongaza/valheimCLI). The fork owns the transport's source and game-side command contracts.

## Purpose

This package is ValheimCLI's external client and YAML test-plan runner, packaged by [ValheimTesting](https://github.com/tvongaza/ValheimTesting). It connects to the in-game ValheimCLI plugin over TCP. It sends commands, reads replies and runs YAML test plans with strict environment pins. It contains no game, Unity or BepInEx assemblies.

## Use

Most test projects reference [Valheim.Testing.Game](https://www.nuget.org/packages/Valheim.Testing.Game) instead, which depends on this package. See the [package versions](https://github.com/tvongaza/ValheimTesting/blob/main/docs/package-versions.md).

## Where the contract lives

The client, plan and reply contracts are in the [transport guide at the exact pinned fork commit](https://github.com/tvongaza/valheimCLI/blob/e596a7d984feaaa695f5c2999964b64e23fbfd92/docs/client-library.md). The fork also owns the game-side commands and capability manifest. `cli-dependency.json` names the pinned commit, and a newer fork head may describe behaviour this revision lacks.

## Provenance

The code is ValheimCLI's own `Valheim.Cli.Testing` project. It is built unchanged from the commit named in this package's repository metadata. It is not an official ValheimCLI release. ValheimCLI and its licence (MIT, copyright warp) are at [github.com/jneb802/valheimCLI](https://github.com/jneb802/valheimCLI).

If ValheimCLI misbehaves, report it to the pinned fork or to [ValheimTesting](https://github.com/tvongaza/ValheimTesting) issues with the valheimcli label.

Unofficial community tooling; not affiliated with or endorsed by Iron Gate or Coffee Stain. Valheim is a trademark of Iron Gate AB.
