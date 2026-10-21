# Run your mod's tests in GitHub Actions

## When to use this

Use this guide when your mod has a test project that builds without the game, and you want it to run on pushes to `main`, on every pull request and by hand. A hosted GitHub runner has no Valheim, Unity or BepInEx assemblies, no Steam login and no secrets. Your tests need none of them.

If your test project still references the plugin or the game, set it up first: see [Test logic that uses game types](test-logic-that-uses-game-types.md).

## Steps

### 1. Copy the workflow

Copy [`tests.yml`](../../examples/ModWithTests/.github/workflows/tests.yml) from the ModWithTests example to `.github/workflows/tests.yml` in your mod's repository. Its triggers and test step are:

```yaml
on:
  push:
    branches: [main]
  pull_request:
  workflow_dispatch:
permissions:
  contents: read
# ... checkout and setup-dotnet with dotnet-version: '10.0.x' ...
      # Change the path to your test project.
      - run: dotnet test MyMod.Tests/MyMod.Tests.csproj -c Release
```

Change two things:

- Replace `MyMod.Tests/MyMod.Tests.csproj` with the path to your test project.
- If your default branch is not `main`, change it under `branches`.

The workflow has read-only repository permissions and uses no secrets.

### 2. Build and test only the test project

Pass `dotnet test` the test project, or a solution that holds only test projects. Never pass a solution that includes the plugin. The plugin build needs game assemblies, and the runner has none.

Do not commit game or publicized assemblies to make the build pass. Do not upload them as secrets or artifacts either. They are not yours to redistribute.

### 3. Pin exact package versions

Pin an exact toolkit version, as [MyMod.Tests.csproj](../../examples/ModWithTests/MyMod.Tests/MyMod.Tests.csproj) does. A new preview then cannot change a passing build. Move the pin deliberately, in its own change. The [package table](../package-versions.md) lists released versions.

### 4. Optional: add a .NET Framework leg

The game runs mods on Mono, a .NET Framework runtime. To run your tests on `net48` too, copy [`Valheim.TestRunners.targets`](../../tools/test-runners/README.md) into your repository and change three lines of the test project:

```xml
<TargetFrameworks>net48;net10.0</TargetFrameworks>   <!-- instead of <TargetFramework>net10.0</TargetFramework> -->
<PackageReference Include="xunit.runner.console" Version="2.8.1" PrivateAssets="all" />
<Import Project="../Valheim.TestRunners.targets" />   <!-- last in the project -->
```

Then make two workflow changes.

First, keep the plain job on one framework. Plain `dotnet test` on a project that targets both also tries `net48`, and that aborts on macOS:

```yaml
      - run: dotnet test MyMod.Tests/MyMod.Tests.csproj -c Release -f net10.0
```

Second, add a job that runs both frameworks with the [test runner targets](../../tools/test-runners/README.md). The command is the same on every runner image. Only the setup differs:

| Runner image | Setup step |
|---|---|
| `windows-latest` | None |
| `ubuntu-latest` | `sudo apt-get install -y --no-install-recommends mono-devel` |
| `macos-latest` | `brew install mono` |

```yaml
      - run: dotnet build MyMod.Tests/MyMod.Tests.csproj -t:RunTestsOnBothFrameworks
```

The output ends with one line per framework. The build fails if any line is not `passed`:

```text
== summary
net48: FAILED (tests, exit 1)
net10.0: passed
```

Add a `strategy.matrix` over runner images only for targets you ship tests for.

### 5. Optional: check game references where the plugin builds

A job that builds your plugin has the game's assemblies. That job can also run the [offline binding check](../packages/Valheim.Testing.Bindings.md). It fails on a field or method that a game update removed, renamed or changed. Run it after the plugin build and before anything launches the game:

```yaml
      # After the step that builds the plugin.
      - name: Check that game references still bind
        shell: bash
        run: |
          dotnet tool install Valheim.Testing.Bindings.Tool --version 0.2.0 --tool-path .tools
          .tools/valheim-bindings MyMod/bin/Release/MyMod.dll --game-dir "$VALHEIM_MANAGED"
```

Set `VALHEIM_MANAGED` to wherever that job finds the game's `Managed` directory. On a self-hosted runner, that is the install's directory. Elsewhere, use the dedicated server from anonymous SteamCMD (app 896660) and its `valheim_server_Data/Managed`. If you can, check a client mod against the client's assemblies.

Exit code 0 means every checked reference binds, and 1 means some are missing. Exit code 2 means the check is incomplete. See [Exit codes](../packages/Valheim.Testing.Bindings.md#exit-codes).

### 6. Keep the results

To keep result files, add a logger and a results directory to the test step:

```yaml
      - run: dotnet test MyMod.Tests/MyMod.Tests.csproj -c Release --logger trx --results-directory TestResults
```

Then upload the directory:

```yaml
      - uses: actions/upload-artifact@v7
        if: always()
        with:
          name: test-results
          path: TestResults/
```

## What stays off hosted runners

Some checks launch Valheim or a server. They need a game install, a Steam login and a machine you own. Run them where you run the game, and follow the [native-run checklist](before-a-native-run.md). A self-hosted runner with game access must never run workflows from untrusted pull requests.

## Check the result

A failed test fails the job, and the job log shows the test output. A green job proves that your tested decisions pass on their declared inputs, on each framework the job ran. It does not prove that the mod loads or behaves correctly in the game. See [Choose a test layer](choose-a-test-layer.md) for what each layer proves.

## Problems

| Symptom | Fix |
|---|---|
| Unresolved Valheim, Unity or BepInEx types | Pass the test project to `dotnet test`, not the solution. |
| `Valheim is not installed at ...` or `Game assemblies not found in ...` | A game-side project was built. Build only test projects on hosted runners. |
| The build refuses a project with game references and the doubles | Keep the doubles in the test project only. See [game references](../../tools/game-references/README.md). |
| A package does not restore | Check the exact version against [the package table](../package-versions.md). Do not substitute another preview. |
| Plain `dotnet test` aborts on macOS | Add `-f net10.0`, and run `net48` through `RunTestsOnBothFrameworks`. |
| Build error: `mono` is not on PATH | Install Mono in the job (step 4). |
| `net48: not run (no xunit.runner.console)` | Add `<PackageReference Include="xunit.runner.console" Version="2.8.1" PrivateAssets="all" />`. |
| `net48: FAILED (no tests ran)` | Your filter matches nothing. See [Filters](../../tools/test-runners/README.md#filters). |
| A Mono run dies with a native stack trace | Check it ran serially. See [Mono and parallel tests](../../tools/test-runners/README.md#mono-and-parallel-tests). |
| `valheim-bindings` exits 2 | Check `VALHEIM_MANAGED`. `assembly_valheim` must be in the supplied directory. |

## Related

- [ModWithTests](../../examples/ModWithTests/README.md): the example this workflow comes from
- [Test logic that uses game types](test-logic-that-uses-game-types.md): the project layout that builds without the game
- [Test runner for both target frameworks](../../tools/test-runners/README.md)
- [Game references for mod projects](../../tools/game-references/README.md)
- [Valheim.Testing.Bindings](../packages/Valheim.Testing.Bindings.md)
- [Before a native run](before-a-native-run.md)
