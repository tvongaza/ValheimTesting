# Packages

Every ValheimTesting package, its newest released version and what it is for. The versions come from [`toolkit-versions.json`](../toolkit-versions.json), and every snippet in these docs pins exactly them.

## Pin

| Package | Version | Use |
|---|---|---|
| [Valheim.Testing.Doubles](https://www.nuget.org/packages/Valheim.Testing.Doubles) | `0.1.0-preview.15` | Unity, Valheim, BepInEx and Jotunn doubles as source for a unit-test project ([page](packages/Valheim.Testing.Doubles.md)) |
| [Valheim.Testing](https://www.nuget.org/packages/Valheim.Testing) | `0.1.0-preview.14` | Declared terrain, zone state, recorded-input replay and `StaticOverride`; no game ([page](packages/Valheim.Testing.md)) |
| [Valheim.Testing.Game](https://www.nuget.org/packages/Valheim.Testing.Game) | `0.1.0-preview.62` | One game process from outside: observations, owned sessions, reports ([page](packages/Valheim.Testing.Game.md)) |
| [Valheim.Testing.GameSessions](https://www.nuget.org/packages/Valheim.Testing.GameSessions) | `0.1.0-preview.24` | A dedicated server, clients and hosted worlds as one run ([page](packages/Valheim.Testing.GameSessions.md)) |
| [Valheim.Testing.Adapter](https://www.nuget.org/packages/Valheim.Testing.Adapter) | `0.1.0-preview.9` | Source for a mod's game-side test adapter plugin ([page](packages/Valheim.Testing.Adapter.md)) |
| [Valheim.Testing.Bindings](https://www.nuget.org/packages/Valheim.Testing.Bindings) | `0.1.0-preview.3` | Offline check that a built mod still binds to the game assemblies ([page](packages/Valheim.Testing.Bindings.md)) |
| [Valheim.Testing.Bindings.Tool](https://www.nuget.org/packages/Valheim.Testing.Bindings.Tool) | `0.1.0-preview.3` | The same check as the `valheim-bindings` .NET tool ([page](packages/Valheim.Testing.Bindings.Tool.md)) |
| [Valheim.Testing.Cli](https://www.nuget.org/packages/Valheim.Testing.Cli) | `0.1.0-preview.18` | ValheimCLI's client transport; the Game package depends on it ([page](packages/Valheim.Testing.Cli.md)) |
| [Valheim.Testing.NativeSmoke](https://www.nuget.org/packages/Valheim.Testing.NativeSmoke) | `0.1.0-preview.31` | `valheim-test`: one-command native load checks ([page](packages/Valheim.Testing.NativeSmoke.md)) |

A unit-test project that links the mod's sources:

```xml
<PackageReference Include="Valheim.Testing.Doubles" Version="[0.1.0-preview.15]" PrivateAssets="all" />
```

A unit-test project for code already free of Unity types:

```xml
<PackageReference Include="Valheim.Testing" Version="[0.1.0-preview.14]" />
```

A separate system-test project that drives the real game, and a game session (a server and clients as one run):

```xml
<PackageReference Include="Valheim.Testing.Game" Version="[0.1.0-preview.62]" />
<PackageReference Include="Valheim.Testing.GameSessions" Version="[0.1.0-preview.24]" />
```

A game-side test adapter plugin compiles the adapter source:

```xml
<PackageReference Include="Valheim.Testing.Adapter" Version="[0.1.0-preview.9]" PrivateAssets="all" />
```

A test that checks a built mod DLL against the game's assemblies in code:

```xml
<PackageReference Include="Valheim.Testing.Bindings" Version="[0.1.0-preview.3]" />
```

The two tools, for a mod's CI job and for native checks:

```sh
dotnet tool install Valheim.Testing.Bindings.Tool --version 0.1.0-preview.3 --tool-path .tools
dotnet tool install Valheim.Testing.NativeSmoke --version 0.1.0-preview.31 --global
```

Brackets mean an exact NuGet version: pin exact versions so a new release cannot change a passing build. Move a pin deliberately, in its own change.

## Limits

Test packages belong in test projects, never in a production mod or a player's plugins folder. Pure helpers target netstandard2.0; external game tools and examples target net10.0. To test against an unpublished build, see [Try an unpublished build in a mod](../CONTRIBUTING.md#try-an-unpublished-build-in-a-mod).

