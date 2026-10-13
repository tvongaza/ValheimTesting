# Test runner for both target frameworks

`Valheim.TestRunners.targets` runs one test project on modern .NET and on .NET Framework. It reports each run and fails if either fails. It is an MSBuild file, not a package: copy it into your repository and import it from the test project.

The game runs mods on its own Mono, which implements .NET Framework 4.x, so mods target `net48`. Your fast everyday loop is `net10.0`. A suite that targets both catches formatting, LINQ, reflection, missing-API and Mono differences before the game does. `Valheim.Testing` and `Valheim.Testing.Doubles` compile for both.

Plain `dotnet test` on such a project also tries the `net48` leg, and that aborts on macOS. The target runs each framework in the way that works on each system:

| Framework | macOS / Linux | Windows |
|---|---|---|
| `net10.0` (and any other modern TFM) | `dotnet test -f net10.0` | `dotnet test -f net10.0` |
| `net48` (and any other .NET Framework TFM) | `dotnet build -f net48`, then the xunit v2 console runner under Mono | `dotnet build -f net48`, then the xunit v2 console runner on .NET Framework |

Both systems use the same console runner for `net48`, so a filter selects the same tests everywhere.

## Use it

Copy `Valheim.TestRunners.targets` into your repository, for example into `build/`. Then import it at the end of the test project:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net48;net10.0</TargetFrameworks>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.10.0" />
    <PackageReference Include="xunit" Version="2.8.1" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.1" PrivateAssets="all" />
    <PackageReference Include="xunit.runner.console" Version="2.8.1" PrivateAssets="all" />
  </ItemGroup>
  <Import Project="../build/Valheim.TestRunners.targets" />
</Project>
```

Run these commands from the folder that contains `MyMod.Tests`. They are the same on every OS:

```sh
dotnet build MyMod.Tests/MyMod.Tests.csproj -t:RunTestsOnBothFrameworks
dotnet build MyMod.Tests/MyMod.Tests.csproj -t:RunTestsOnBothFrameworks -p:VSTestTestCaseFilter=FullyQualifiedName~DrySite
dotnet build MyMod.Tests/MyMod.Tests.csproj -t:RunTestsOnBothFrameworks -f net48      # one framework
dotnet test MyMod.Tests/MyMod.Tests.csproj -f net10.0                                  # the fast loop alone
```

The output ends with one line per framework:

```text
== summary
net48: FAILED (tests, exit 1)
net10.0: passed
```

Every selected framework runs, even after an earlier one fails. The build fails if any line is not `passed`.

### Settings

| Setting | Meaning |
|---|---|
| `-f TFM` | Run only this framework. Without it, every framework the project targets, in the project's order |
| `-c C` | Build configuration, default `Debug` |
| `-p:VSTestTestCaseFilter=EXPR` | `dotnet test`'s own filter, for every framework; translated for the console runner ([Filters](#filters)) |

### Summary lines

| Output line | Meaning |
|---|---|
| `net10.0: passed` | The modern leg passed. |
| `net48: passed (5 tests)` | The .NET Framework leg passed, with its test count. |
| `net48: FAILED (tests, exit 1)` | Tests failed. |
| `net48: FAILED (build, exit 1)` | The `net48` build failed. |
| `net48: FAILED (no tests ran)` | The filter matched nothing, or the runner found no tests. |
| `net48: not run (no xunit.runner.console)` | The project has no console runner reference. |

A .NET Framework leg that ran no tests fails even when the runner exits 0. The console runner writes its report to `obj/<configuration>/net48/xunit-console.xml`.

### Refusals

In these cases nothing runs, and the build fails with an error that says why:

| Condition | What to do |
|---|---|
| `<TargetFrameworks>` lacks a .NET Framework or a modern framework | Target both, such as `net48;net10.0`, or use `dotnet test` |
| `-f` names a framework the project does not target | Choose one of the project's frameworks with `-f` |
| The console runner cannot express the filter | Simplify the filter, or run `-f net10.0` alone |
| A .NET Framework leg on macOS or Linux, and `mono` is not on PATH | Install Mono, or run `-f net10.0` alone |

## What the project needs

- `<TargetFrameworks>` with a .NET Framework TFM and a modern one, and xunit v2.
- The console runner as a package reference, in the project before the import or in `Directory.Build.props`. Use your xunit version: `<PackageReference Include="xunit.runner.console" Version="2.8.1" PrivateAssets="all" />`. The targets file asks restore for its folder (`GeneratePathProperty`) and runs that version's `tools/net48/xunit.console.exe`.
- On macOS and Linux, Mono on PATH:
  - macOS: `brew install mono`
  - Debian or Ubuntu: `sudo apt-get install --no-install-recommends mono-devel`. `mono-runtime` alone lacks the `System.Runtime` and `netstandard` facades the console runner loads. `mono-complete` also works.
- On Windows, nothing extra. .NET Framework 4.8 is part of the system.

To run both legs in GitHub Actions, see [Run tests in CI](../../docs/guides/run-tests-in-ci.md).

## Filters

`dotnet test` takes the filter as written. The console runner has no filter expressions: it ORs options of one kind and ANDs different kinds. The target translates only terms of one kind joined by `|`:

| Filter term | Console option |
|---|---|
| `FullyQualifiedName~text`, or a bare `text` | `-method "*text*"` |
| `FullyQualifiedName=Namespace.Class.Method` | `-method "Namespace.Class.Method"` |
| `Trait=value` (any trait name, such as `Category=Slow`) | `-trait "Trait=value"` |

Property names compare case-insensitively, as in `dotnet test`. The target quotes values for the shell, so a generic class's backtick reaches the runner as written.

The target refuses these filters before anything runs:

- `&`, `!=` and parentheses.
- A method term OR'd with a trait term.
- `DisplayName`, `Name`, `ClassName`, `TestCategory`, `Priority` and `Id`. As a trait, they would match no test.

## Mono and parallel tests

Tests that share process-wide state must not run in parallel on any runtime. The game doubles are singletons, so a suite that uses them sets `[assembly: CollectionBehavior(DisableTestParallelization = true)]`, as `DrySiteTests.cs` in [ModWithTests](../../examples/ModWithTests/README.md) does.

Under Mono, the target passes `-parallel none` to the console runner. On Windows, the assembly's own setting applies. If a Mono run dies with a native stack trace instead of a test failure, check that it ran serially.

## In GitHub Actions

Hosted runners have no Mono. The command is the same on every runner image; only the setup differs:

| Runner image | Setup step |
|---|---|
| `windows-latest` | None |
| `ubuntu-latest` | `sudo apt-get install -y --no-install-recommends mono-devel` |
| `macos-latest` | `brew install mono` |

## Limits

- Supports xunit v2 projects only.
- Other `-p:` properties apply to the target, not to the builds and test runs it starts. Set what those need in the project or the environment. Alternatively, run `dotnet test -f net10.0` with its own options.
- The console runner limits filters to the forms in [Filters](#filters).
- On macOS and Linux, the .NET Framework leg needs Mono.
