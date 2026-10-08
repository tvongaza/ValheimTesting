# ValheimTesting

Reusable test inputs, fixtures and assertions for Valheim mods. Most tests run without Valheim, Unity, Steam or a dedicated server; the few that need the game drive it through ValheimCLI, on the same Windows PC where you play.

> Unofficial community tooling; not affiliated with or endorsed by Iron Gate or Coffee Stain. Valheim is a trademark of Iron Gate AB.

## First test in three steps

1. Install the .NET 10 SDK (Visual Studio 2026 brings it) on the PC where you build your mod.
2. Copy [ModWithTests](examples/ModWithTests/README.md) beside your mod: a test project that links your mod's real source files and references `Valheim.Testing.Doubles` from NuGet.org.
3. Run `dotnet test MyMod.Tests/MyMod.Tests.csproj`. No game, Steam or ValheimCLI is needed.

Then [Bring your mod](docs/adopting.md) helps choose the layer for each behaviour, [Get started](docs/getting-started.md) has the package versions and IDE setup, and the [examples](examples/README.md) go up to native checks with an owned server and client on one Windows PC. Other hosts are in [platforms](docs/platforms.md). New to BepInEx and Harmony? The community wiki's [overview](https://github.com/Valheim-Modding/Wiki/wiki/Best-Practices) explains the concepts; check its older samples against 1.0 ([wiki caveats](docs/runtime-hygiene.md#wiki-pages-that-predate-10)). A plugin that will not load, or two mods that conflict: [debugging guide](docs/debugging-mods.md).

## Packages

Each package's page is its reference: what it is for, the types a test calls, how it fails and its limits.

| Package (source and reference) | Purpose | Runtime |
|---|---|---|
| [`Valheim.Testing`](src/Valheim.Testing) ([reference](docs/packages/Valheim.Testing.md)) | Composable terrain, exact recorded-input replay, pinned grid dumps, terrain rendering with contours, parity checks and `StaticOverride` (scoped static and environment overrides for any test); no ValheimCLI dependency | netstandard2.0 |
| [`Valheim.Testing.Game`](src/Valheim.Testing.Game) ([reference](docs/packages/Valheim.Testing.Game.md)) | One game process from outside: typed observations, fixtures, owned server and client sessions, comparisons and JSON/JUnit reports | net10.0 |
| [`Valheim.Testing.GameSessions`](src/Valheim.Testing.GameSessions) ([reference](docs/packages/Valheim.Testing.GameSessions.md)) | Game sessions: a dedicated server, clients and hosted worlds as one run (`GameSession`, `PinnedServerRun`), on this PC or on other machines over SSH or a container, with host locks, Steam-account leases and recovery of what a run left; built on `Valheim.Testing.Game` | net10.0 |
| [`Valheim.Testing.Cli`](cli-dependency.json) ([reference](docs/packages/Valheim.Testing.Cli.md)) | ValheimCLI's client transport and YAML runner, packaged unchanged from pinned ValheimCLI source (MIT, warp) | net10.0 |
| [`Valheim.Testing.Adapter`](src/Valheim.Testing.Adapter) ([reference](docs/packages/Valheim.Testing.Adapter.md)) | Source compiled into a mod's game-side test adapter plugin: registration with ValheimCLI, the owned-session identity the runners check, and read-only observations | source (net48, C# 10) |
| [`Valheim.Testing.Doubles`](src/Valheim.Testing.Doubles) ([reference](docs/packages/Valheim.Testing.Doubles.md)) | Source-only doubles of the Unity, Valheim, BepInEx and Jotunn types a mod's pure-logic sources use, compiled into your test project; every type is partial; `ValheimWorldScope` puts back every static they keep (a mod scopes its own with `StaticOverride`) | source (C# 10); [member index](docs/packages/Valheim.Testing.Doubles.members.txt) |
| [`Valheim.Testing.Bindings`](src/Valheim.Testing.Bindings) ([reference](docs/packages/Valheim.Testing.Bindings.md)) | Offline check that a built mod's references into the game assemblies still bind (Mono.Cecil); missing members fail, access changes are reported separately | netstandard2.0 |
| [`Valheim.Testing.Bindings.Tool`](src/Valheim.Testing.Bindings.Tool) ([reference](docs/packages/Valheim.Testing.Bindings.md#in-ci)) | The same check as the `valheim-bindings` .NET tool, for a mod's CI before any native run | net10.0 |
| [`Valheim.Testing.NativeSmoke`](src/Valheim.Testing.NativeSmoke) ([reference](docs/packages/Valheim.Testing.NativeSmoke.md)) | `valheim-test`: one-command disposable mod-load checks, A/B mod isolation, an editable NuGet-only consumer project, and the environment commands (`env`, `session check`) | .NET 10 tool |

The dependency goes **ValheimTesting → ValheimCLI**, never the reverse: the game-side extension API stays in ValheimCLI, and a mod keeps its own adapter and scenarios. No test package belongs in a production mod or a player's plugin folder. `Valheim.Testing.Cli` is ValheimCLI's transport packaged by this repository from the fork commit `cli-dependency.json` pins, not an official ValheimCLI release. Preview APIs may change; the [candidate API reference](https://tvongaza.github.io/ValheimTesting/) inventories them for the [stable-contract review](docs/reference/compatibility.md).

## Test layers

1. **Unit:** real mod decisions against small declared inputs and the doubles. Broad and fast.
2. **Integration:** scenario drivers, adapters and fixtures against scripted replies and fake processes.
3. **Bounded game checks:** a few zones on a disposable server and client; save, restart and replication when those boundaries change.
4. **Human judgement:** a short look or walk when appearance or usability matters.

Synthetic inputs are not Valheim's generator, a replay of a capture is not independent evidence of the game, and an incomplete observation fails rather than reading as zero.

## Contribute

Mod developers and coding agents are welcome. [CONTRIBUTING.md](CONTRIBUTING.md) has repository ownership, a worked fixture example, local checks (`dotnet run scripts/bootstrap-cli.cs`, then `dotnet run scripts/validate.cs`; no game) and PR expectations. Agents start with [AGENTS.md](AGENTS.md) and the [agent workflow](docs/agent-guide.md). Before a native run, read the [runtime hygiene checklist](docs/runtime-hygiene.md).

## License

MIT, copyright © 2026 Tys von Gaza. See [LICENSE](LICENSE). Attribution for imported and adapted code is retained in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and [PROVENANCE.md](PROVENANCE.md); both license files are included in the library packages.
