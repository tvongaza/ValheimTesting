# Test runner for both target frameworks

`Valheim.TestRunners.targets` runs one test project on modern .NET and on .NET Framework, reports each, and fails if either fails. It is an MSBuild file for your repository, not a package: copy it in and import it from the test project.

## Why both

The game runs mods on its own Mono, which implements .NET Framework 4.x; mods target `net48`. The fast everyday loop is modern .NET (`net10.0`). The two differ in ways a test can hit: string and float formatting, collection and LINQ behaviour, reflection over generics, missing APIs that only fail when compiled for `net48`, and the Mono runtime's own bugs. A suite that targets both (`<TargetFrameworks>net48;net10.0</TargetFrameworks>`) catches those before the game does. `Valheim.Testing` and `Valheim.Testing.Doubles` compile for both.

Plain `dotnet test` on such a project also tries the `net48` leg, which on macOS aborts. The target runs each framework the way that works on each system:

| Framework | macOS / Linux | Windows |
|---|---|---|
| `net10.0` (and any other modern TFM) | `dotnet test -f net10.0` | `dotnet test -f net10.0` |
| `net48` (and any other .NET Framework TFM) | `dotnet build -f net48`, then the xunit v2 console runner under Mono | `dotnet build -f net48`, then the xunit v2 console runner on .NET Framework |

Both OSes use the same console runner for `net48`, so a filter selects the same tests everywhere.

## Use it

Copy `Valheim.TestRunners.targets` into your repository, for example into `build/`, and import it at the end of the test project:

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

Then, on every OS:

```sh
dotnet build MyMod.Tests/MyMod.Tests.csproj -t:RunTestsOnBothFrameworks
dotnet build MyMod.Tests/MyMod.Tests.csproj -t:RunTestsOnBothFrameworks -p:VSTestTestCaseFilter=FullyQualifiedName~DrySite
dotnet build MyMod.Tests/MyMod.Tests.csproj -t:RunTestsOnBothFrameworks -f net48      # one framework
dotnet test MyMod.Tests/MyMod.Tests.csproj -f net10.0                                  # the fast loop alone
```

| Setting | Meaning |
|---|---|
| `-f TFM` | Run only this target framework. Without it, every framework the project targets, in the project's order |
| `-c C` | Build configuration, default `Debug` |
| `-p:VSTestTestCaseFilter=EXPR` | `dotnet test`'s own filter property, for every framework; translated for the console runner (below) |

Other `-p:` properties apply to the target, not to the builds and test runs it starts; set what those need in the project or the environment, or run `dotnet test -f net10.0` with its own options for the modern leg.

Every selected framework runs even if an earlier one failed. The output ends with one line per framework, and the build fails if any line is not `passed`:

```text
== summary
net48: FAILED (tests, exit 1)
net10.0: passed
```

The console runner writes an XML report (`obj/<configuration>/net48/xunit-console.xml`), and a .NET Framework leg that ran no tests fails (`net48: FAILED (no tests ran)`) even though the runner exited 0: a filter that matches nothing, or tests the runner cannot discover, must not pass silently. A passing leg shows its count, `net48: passed (5 tests)`. A build failure shows as `net48: FAILED (build, exit 1)`, a project without the console runner as `net48: not run (no xunit.runner.console)`.

Nothing runs, and the build fails with an error saying why, when the project does not target a .NET Framework and a modern framework in `<TargetFrameworks>` (with `-f`, one of them), when `-f` names a framework the project does not target, when the filter is one the console runner cannot express, or when a .NET Framework leg needs Mono and `mono` is not on PATH.

## What the project needs

- `<TargetFrameworks>` with a .NET Framework TFM and a modern one, and xunit v2.
- The console runner, as a package reference: `<PackageReference Include="xunit.runner.console" Version="2.8.1" PrivateAssets="all" />` (use your xunit version), in the project before the import or in `Directory.Build.props`. The targets file asks restore for its folder (`GeneratePathProperty`) and runs that version's `tools/net48/xunit.console.exe`.
- On macOS and Linux, Mono on PATH: `brew install mono` on macOS, `sudo apt-get install --no-install-recommends mono-devel` on Debian or Ubuntu (`mono-runtime` alone lacks the `System.Runtime` and `netstandard` facades the console runner loads; `mono-complete` works too, but adds about 28 MB of documentation and tools). `dotnet test -f net10.0` runs the modern leg without it. Windows needs nothing extra: .NET Framework 4.8 is part of the system.

[ModWithTests](../../examples/ModWithTests/README.md#also-test-on-net-framework) shows the project changes.

## Filters

`dotnet test` takes the filter as written. The console runner has no filter expressions: it ORs options of one kind and ANDs different kinds. So only filters whose meaning survives translate, terms of one kind joined by `|`:

| Filter term | Console option |
|---|---|
| `FullyQualifiedName~text`, or a bare `text` | `-method "*text*"` |
| `FullyQualifiedName=Namespace.Class.Method` | `-method "Namespace.Class.Method"` |
| `Trait=value` (any trait name, such as `Category=Slow`) | `-trait "Trait=value"` |

Property names compare case-insensitively, as in `dotnet test`. The values are quoted for the shell, so characters such as a generic class's backtick reach the runner as written. Anything else stops the build before anything runs: `&`, `!=`, parentheses, a method term OR'd with a trait term, and the test properties the console cannot select by (`DisplayName`, `Name`, `ClassName`, `TestCategory`, `Priority`, `Id`), which as a trait would match no test. Simplify the filter, or run `dotnet test -f net10.0` alone.

## Mono and parallel tests

Tests that share process-wide state must not run in parallel on any runtime: the game doubles are singletons, as the game's are, so a suite that uses them sets `[assembly: CollectionBehavior(DisableTestParallelization = true)]` (as [ModWithTests](../../examples/ModWithTests/MyMod.Tests/DrySiteTests.cs) does).

Mono adds a limit of its own. A mod's suite whose test collections ran in parallel under Mono aborted in native assembly-loading code (`mono_assembly_names_equal_flags`, under `mono_domain_assembly_search`) in 3 of 8 runs; with parallelization disabled it ran clean 6 times of 6, and either half of the suite alone was always clean. That is a race, so those counts are the evidence rather than a proof. The same suite takes well under a second, so serialising it costs nothing. The target therefore passes `-parallel none` to the console runner under Mono. On Windows the assembly's own setting applies. If a Mono run dies with a native stack trace rather than a test failure, check that it ran serially before looking elsewhere.

## In GitHub Actions

Hosted runners have no Mono: install it in the job (`sudo apt-get install -y --no-install-recommends mono-devel` on `ubuntu-latest`, `brew install mono` on `macos-latest`), or run the .NET Framework leg on `windows-latest`, which needs nothing. The command is the same on all three. This repository's `test-runners` job does all three with the ModWithTests example; see [test.yml](../../.github/workflows/test.yml).

## Tests

`TestRunnersTargetsTests` in `tests/Valheim.Testing.Tests` imports the targets file into fixture test projects that target `net10.0` and `net48`. On every OS: the filter translation table, and the refusals (an untranslatable filter, a project with one framework, a `-f` framework the project does not target, and on macOS and Linux missing Mono) stop before anything builds. On Windows, and elsewhere when Mono is on PATH: real runs where both frameworks pass, a filter with shell characters reaches both runners, a failure only on .NET Framework fails the run, a modern failure still runs .NET Framework, a filter that selects no test fails the `net48` leg, a missing console runner and a build failure are reported. The `test-runners` CI job runs the target for real on all three systems, on a copy of ModWithTests that targets both frameworks, and then with an added test that fails only on .NET Framework, which must fail the run while `-f net10.0` still passes, and with a filter that selects no test, which must fail the `net48` leg.
