# ValheimTesting

Test what your Valheim mod does in the game. Start with a small action and an independent assertion, then reuse that scenario to protect a fix.

The marker example asks the mod to place a pole, then counts its saved objects. Selected action and assertion lines from [Scenario.cs](examples/TargetedRegression/Scenario.cs):

```csharp
var reply = host.Execute("examplemod_mark " + At());
round.Step("exactly one marker stands there in the host's saved objects", () => RequireMarkers(host, x, z, ExpectedMarkers));
```

`ExpectedMarkers` is `1`. A captured run records **"first: exactly one marker stands there in the host's saved objects"** with **`Passed: true`**. The unchanged assertion also catches a command that reports success without creating a pole.

[Run your first gameplay assertion](docs/tutorials/first-gameplay-assertion.md): the complete source, files to edit, tested command and failure result.

## Choose your task

| You want to | Start here |
|---|---|
| Test your mod's gameplay in Valheim | [Your first gameplay assertion](docs/tutorials/first-gameplay-assertion.md) |
| Test a decision quickly, without the game | [Get started](docs/getting-started.md#steps) |
| Keep a fixed bug from returning | [Run an A/B regression](docs/guides/run-an-ab-regression.md) |

A [load check](docs/tutorials/first-server-load-check.md) proves that plugins start and enter a world. Gameplay checks need their own action and assertion.

## First test in three steps

Follow [Get started](docs/getting-started.md) for the offline example and its copy map. You need no toolkit clone or bootstrap.

Native runs need a supported host: game clients on Windows and macOS, dedicated servers on Windows, macOS and Linux ([platforms](docs/reference/platforms.md)); the gameplay tutorial is written for Windows. New to BepInEx and Harmony? Read the community wiki's [overview](https://github.com/Valheim-Modding/Wiki/wiki/Best-Practices). A plugin that will not load, or two mods that conflict: the [debugging guide](docs/guides/check-a-mod-loads.md).

## Packages

[Package versions](docs/package-versions.md) lists every package, exact pin and reference link. Keep test packages in test projects, outside a player's plugin folder. The dependency goes ValheimTesting → ValheimCLI, never the reverse: a mod keeps its own adapter and scenarios. Preview APIs may change; see [compatibility](docs/reference/compatibility.md).

## Test layers

[Choose a test layer](docs/guides/choose-a-test-layer.md) explains what unit tests, scripted integration, native checks and human review each prove. The [example index](examples/README.md) shows the marker at those layers.

## Contribute

Read [CONTRIBUTING.md](CONTRIBUTING.md) to change the toolkit; coding agents changing it start with [AGENTS.md](AGENTS.md). To have your coding agent test your mod, [give it the workflow](docs/guides/give-your-agent-the-workflow.md).

> Unofficial community tooling; not affiliated with or endorsed by Iron Gate or Coffee Stain. Valheim is a trademark of Iron Gate AB.

## Credits

ValheimTesting is built on [ValheimCLI](https://github.com/jneb802/valheimCLI), created by **Warpalicious** ([Thunderstore](https://thunderstore.io/c/valheim/p/warpalicious/)) and licensed MIT. Every native check talks to the game through ValheimCLI: its in-game plugin runs the commands, and its client transport, which ships as `Valheim.Testing.Cli`, sends them. This project uses a pinned fork, [tvongaza/valheimCLI](https://github.com/tvongaza/valheimCLI), which adds the commands and packs the toolkit needs. Thank you, Warpalicious, for building and sharing it.

## License

MIT, copyright © 2026 Tys von Gaza. See [LICENSE](LICENSE). Attribution for imported and adapted code is retained in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and [PROVENANCE.md](PROVENANCE.md); both license files are included in the library packages.
