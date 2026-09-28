# Valheim.Testing.Cli

ValheimCLI's external client and YAML test-plan runner, packaged by [ValheimTesting](https://github.com/tvongaza/ValheimTesting) so external Valheim test tools can talk to a running game. It connects to the in-game ValheimCLI plugin over TCP, sends commands, reads replies and runs YAML test plans with strict environment pins. It contains no game, Unity or BepInEx assemblies.

The code is ValheimCLI's own `Valheim.Cli.Testing` project, built unchanged from the commit named in this package's repository metadata. It is not an official ValheimCLI release; ValheimCLI and its license (MIT, copyright warp) are at [github.com/jneb802/valheimCLI](https://github.com/jneb802/valheimCLI).

Most test projects do not reference this package directly: use [Valheim.Testing.Game](https://www.nuget.org/packages/Valheim.Testing.Game), which depends on it. See the [getting-started guide](https://github.com/tvongaza/ValheimTesting/blob/main/docs/getting-started.md).
