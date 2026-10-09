# Get started

*Assistant-written (Claude).*

The unit tests need only the .NET 10 SDK, on any OS; no game. The usual setup for the native checks is the **Windows PC where Valheim is installed through Steam**: an owned dedicated server and an owned client run there, both started from test copies of the Steam installs. Other hosts, Linux servers and containers, macOS and several machines are in [platforms](platforms.md); none of them is needed to start.

1. **Install the .NET 10 SDK** (Visual Studio 2026 with the .NET desktop development workload brings it, and the .NET Framework 4.8 targeting pack for `net48` test legs).
2. **Add a test project** beside your mod, reference one package from the table below, and link the mod's source files. [ModWithTests](../examples/ModWithTests/README.md) is a complete project to copy; [Bring your mod](adopting.md) helps choose the layer.
3. **Run it** with `dotnet test` or the IDE's test runner. No game, Steam or ValheimCLI is needed for this layer, and the packages restore from NuGet.org.
4. **For a native check**, follow [one Windows PC](packages/Valheim.Testing.Game.md#on-one-windows-pc): Valheim and the Valheim Dedicated Server tool from Steam, test copies with BepInEx and ValheimCLI, and FullLifecycle's native session test.

Test packages belong in test projects, never in a production mod or a player's plugins folder. Existing xUnit tests stay where they are. To investigate a plugin that will not load, or a failure only two mods together show, use [Debug a mod load or mod conflict](debugging-mods.md). For the edit-build-test loop against your own game copy, see [tools/dev-loop](../tools/dev-loop/README.md). To develop this framework itself, follow [the contributor bootstrap](../CONTRIBUTING.md#set-up-and-validate-locally).

## Package versions and feeds

The toolkit packages are all on NuGet.org. The versions below are the **newest releases**, and every copyable snippet and example in these docs pins exactly them: they come from [`toolkit-versions.json`](../toolkit-versions.json), the one record of what is released, and follow each release (CI fails while a documented pin is older). Pin exact versions in your own test project and move them when you choose; a newly published package does not change a passing test. The release workflow separately tests every newly published version from NuGet.org.

| Package | Released version | Use |
|---|---|---|
| [Valheim.Testing](https://www.nuget.org/packages/Valheim.Testing) | `0.1.0-preview.13` | Composable terrain, zone state, recorded-input replay and scoped static overrides; no ValheimCLI dependency |
| [Valheim.Testing.Game](https://www.nuget.org/packages/Valheim.Testing.Game) | `0.1.0-preview.52` | External game observations, owned sessions, comparisons and reports |
| [Valheim.Testing.GameSessions](https://www.nuget.org/packages/Valheim.Testing.GameSessions) | `0.1.0-preview.12` | Game sessions: a dedicated server, clients and hosted worlds as one run (`GameSession`, `PinnedServerRun`), on this PC or on other machines, with Steam-account leases and recovery ([page](packages/Valheim.Testing.GameSessions.md)); depends on exactly the Game it was built with |
| [Valheim.Testing.Cli](https://www.nuget.org/packages/Valheim.Testing.Cli) | `0.1.0-preview.14` | ValheimCLI's client transport, packaged from pinned ValheimCLI source; consumed by the Game package |
| [Valheim.Testing.Adapter](https://www.nuget.org/packages/Valheim.Testing.Adapter) | `0.1.0-preview.7` | Source for a mod's game-side test adapter plugin: registration with ValheimCLI and the owned-session identity ([page](packages/Valheim.Testing.Adapter.md)) |
| [Valheim.Testing.Doubles](https://www.nuget.org/packages/Valheim.Testing.Doubles) | `0.1.0-preview.14` | Unity/Valheim/BepInEx/Jotunn doubles as source, so a unit-test project compiles the mod's pure-logic files without the game ([page](packages/Valheim.Testing.Doubles.md)) |
| [Valheim.Testing.Bindings](https://www.nuget.org/packages/Valheim.Testing.Bindings) | `0.1.0-preview.2` | Library: checks offline that a built mod's references into the game assemblies still bind, and names the mod methods that use each missing member ([page](packages/Valheim.Testing.Bindings.md)) |
| [Valheim.Testing.Bindings.Tool](https://www.nuget.org/packages/Valheim.Testing.Bindings.Tool) | `0.1.0-preview.2` | The same check as the `valheim-bindings` .NET tool, for a mod's CI; not a project reference |

Versions need not match each other. They restore from NuGet.org with no extra setup. To try an unpublished build instead, add the local `.packages` feed alongside NuGet.org, which still supplies xUnit and ordinary dependencies, and reference the exact version `validate.cs` packed: `<Version>-candidate.<id>`, as the file names in `.packages` show (the id is a hash of the package inputs, so a build of other sources never shares it). For example, from your mod checkout:

```sh
dotnet restore path/to/MyMod.Tests.csproj -p:RestoreAdditionalProjectSources=/absolute/path/ValheimTesting/.packages
```

`RestoreAdditionalProjectSources` adds the feed and keeps your configured NuGet.org source. Avoid passing NuGet.org as a second `--source`: with .NET SDK 10.0.401 on Windows, restore treated that URL as a local folder and failed.

Pin only the package your test project needs:

```xml
<!-- Pure test project; not the production mod project. -->
<PackageReference Include="Valheim.Testing" Version="[0.1.0-preview.13]" />
<!-- A separate external system-test project instead uses: -->
<PackageReference Include="Valheim.Testing.Game" Version="[0.1.0-preview.52]" />
<!-- and, to run a game session (a server and clients as one run): -->
<PackageReference Include="Valheim.Testing.GameSessions" Version="[0.1.0-preview.12]" />
<!-- A game-side test adapter plugin compiles the adapter source: -->
<PackageReference Include="Valheim.Testing.Adapter" Version="[0.1.0-preview.7]" PrivateAssets="all" />
<!-- A test that checks a built mod DLL against the game's assemblies in code: -->
<PackageReference Include="Valheim.Testing.Bindings" Version="[0.1.0-preview.2]" />
```

Brackets mean an exact NuGet version. Pure helpers target netstandard2.0; external game tools and examples target net10.0. Keep the game-side plugin's existing target framework.

The binding check needs no project reference in most mods. Install the tool in the CI job that builds the plugin and run it on the built DLL against the game's `Managed` directory ([CI step](packages/Valheim.Testing.Bindings.md#in-ci)):

```sh
dotnet tool install Valheim.Testing.Bindings.Tool --version 0.1.0-preview.2 --tool-path .tools
.tools/valheim-bindings MyMod/bin/Release/MyMod.dll --game-dir "path/to/valheim_Data/Managed"
```

Reference the `Valheim.Testing.Bindings` library only from a test that calls `BindingCheck.Check` itself, for example to assert on the report. Neither belongs in the production plugin project, and both need the game's assemblies, so neither runs in a CI job without them.

## Using Visual Studio, Rider or VS Code

The test projects are ordinary SDK-style xUnit projects, so IDE test runners discover them with no extra setup. The external tools and tests target net10.0, which needs an IDE that supports .NET 10:

| IDE | Where tests appear | Notes |
|---|---|---|
| Visual Studio 2026 (Windows) | Test Explorer | Install the **.NET desktop development** workload; it includes the .NET Framework 4.8 targeting pack used by net48 test legs, which run natively on Windows. |
| JetBrains Rider (Windows, macOS, Linux) | Unit Tests window | net48 test legs need Mono on macOS/Linux. |
| VS Code with C# Dev Kit | Testing panel | Same .NET 10 SDK requirement. |

Open your test project directly or add it to your own solution. A separate testing solution is optional. Published packages restore from NuGet.org; unpublished candidates need the local feed above.

Two things the command line passes explicitly are set another way in an IDE:

- **Game install path.** Mod projects read Valheim's assemblies from the default Steam folder. If your Steam library is elsewhere, set a `VALHEIM_INSTALL` environment variable before starting the IDE, where the project supports it (the test adapters do), or follow the mod's own build instructions.
- **The game-side test adapter** (`*.TestAdapter`) is deliberately not in the testing solution: it must compile against the exact ValheimCLI build you install. Build it from a terminal with `-p:CliDll=...`, or set a `CliDll` environment variable before starting the IDE.

External runners are console programs. To run one from the IDE, set its command-line arguments in the project's debug or run settings (Visual Studio: project **Properties → Debug → Open debug launch profiles UI**; Rider: **Run → Edit Configurations**). Native runs still need a disposable game install, world and character.

## Package pages

Each package's contract, failure semantics and limits are on its page: [Valheim.Testing](packages/Valheim.Testing.md), [Valheim.Testing.Doubles](packages/Valheim.Testing.Doubles.md), [Valheim.Testing.Game](packages/Valheim.Testing.Game.md), [Valheim.Testing.GameSessions](packages/Valheim.Testing.GameSessions.md), [Valheim.Testing.Adapter](packages/Valheim.Testing.Adapter.md), [Valheim.Testing.Bindings](packages/Valheim.Testing.Bindings.md), [Valheim.Testing.Bindings.Tool](packages/Valheim.Testing.Bindings.Tool.md), [Valheim.Testing.Cli](packages/Valheim.Testing.Cli.md) and [Valheim.Testing.NativeSmoke](packages/Valheim.Testing.NativeSmoke.md). The [candidate API reference](https://tvongaza.github.io/ValheimTesting/) inventories the public declarations; the [compatibility-policy draft](reference/compatibility.md) records the decisions still needed before stable packages.
