# Valheim.Testing.Cli

[Current preview API reference](https://tvongaza.github.io/ValheimTesting/) and [pinned ValheimCLI fork](https://github.com/tvongaza/valheimCLI). The fork owns the transport's source and game-side command contracts.

ValheimCLI's external client and YAML test-plan runner, packaged by [ValheimTesting](https://github.com/tvongaza/ValheimTesting) so external Valheim test tools can talk to a running game. It connects to the in-game ValheimCLI plugin over TCP, sends commands, reads replies and runs YAML test plans with strict environment pins. It contains no game, Unity or BepInEx assemblies.

The code is ValheimCLI's own `Valheim.Cli.Testing` project, built unchanged from the commit named in this package's repository metadata. It is not an official ValheimCLI release; ValheimCLI and its license (MIT, copyright warp) are at [github.com/jneb802/valheimCLI](https://github.com/jneb802/valheimCLI).

Read the [transport guide at the exact pinned fork commit](https://github.com/tvongaza/valheimCLI/blob/9e8ca679298e559e995ab5b04ef84b782b15a0ad/docs/client-library.md) for its client, plan and reply contracts. The game-side commands and capability manifest also belong to that fork. A newer fork head may describe behavior this packaged revision does not have; check `cli-dependency.json` before using a newer example.

Most test projects do not reference this package directly: use [Valheim.Testing.Game](https://www.nuget.org/packages/Valheim.Testing.Game), which depends on it. See the [getting-started guide](https://github.com/tvongaza/ValheimTesting/blob/main/docs/getting-started.md).

Unofficial community tooling; not affiliated with or endorsed by Iron Gate or Coffee Stain. Valheim is a trademark of Iron Gate AB.
