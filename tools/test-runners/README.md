# Test runner for both target frameworks

`run-tests.sh` (macOS, Linux) and its PowerShell twin `run-tests.ps1` (Windows) run one test project for modern .NET and for .NET Framework, report each, and fail if either fails. Copy the one you need into your mod's repository, or call it from a ValheimTesting checkout.

## Why both

The game runs mods on its own Mono, which implements .NET Framework 4.x; mods target `net48`. The fast everyday loop is modern .NET (`net10.0`). The two differ in ways a test can hit: string and float formatting, collection and LINQ behaviour, reflection over generics, missing APIs that only fail when compiled for `net48`, and the Mono runtime's own bugs. A suite that targets both (`<TargetFrameworks>net48;net10.0</TargetFrameworks>`) catches those before the game does. `Valheim.Testing` and `Valheim.Testing.Doubles` compile for both.

Plain `dotnet test` on such a project also tries the `net48` leg, which on macOS aborts. The script runs each framework the way that works on each system:

| Framework | macOS / Linux | Windows |
|---|---|---|
| `net10.0` (and any other modern TFM) | `dotnet test -f net10.0` | `dotnet test -f net10.0` |
| `net48` (and any other .NET Framework TFM) | `dotnet build -f net48`, then the xunit v2 console runner under Mono | `dotnet build -f net48`, then the xunit v2 console runner on .NET Framework |

Both OSes use the same console runner for `net48`, so a filter selects the same tests everywhere.

## Run it

```sh
tools/test-runners/run-tests.sh MyMod.Tests/MyMod.Tests.csproj
tools/test-runners/run-tests.sh --filter "FullyQualifiedName~DrySite" MyMod.Tests/MyMod.Tests.csproj
tools/test-runners/run-tests.sh --framework net10.0 MyMod.Tests/MyMod.Tests.csproj   # the fast loop alone
```

```powershell
powershell -ExecutionPolicy Bypass -File tools\test-runners\run-tests.ps1 MyMod.Tests\MyMod.Tests.csproj
```

| Option | Meaning |
|---|---|
| `-f`, `--framework TFM` | Run only this framework; repeat for more. Default: `net10.0` and `net48` |
| `-c`, `--configuration C` | Build configuration, default `Debug` |
| `--filter EXPR` | A `dotnet test` filter for every framework; translated for the console runner (below) |
| `--netfx-parallel MODE` | The console runner's `-parallel` for .NET Framework: `none`, `collections`, `assemblies`, `all`, or `default` for the assembly's own setting. Default: `none` under Mono, `default` on Windows |
| anything after the project, or after `--` | Passed to `dotnet test` only, for example `--logger trx` |

Every selected framework runs even if an earlier one failed. The output ends with one line per framework:

```text
== summary
net10.0: passed
net48: FAILED (tests, exit 1)
```

The console runner writes an XML report, and a .NET Framework leg that ran no tests fails (`net48: FAILED (no tests ran)`) even though the runner exited 0: a filter that matches nothing, or tests the runner cannot discover, must not pass silently. A passing leg shows its count, `net48: passed (5 tests)`.

Exit codes: `0` every selected framework passed; `1` a build or test run failed, or the console runner ran no tests; `2` usage, a filter the console runner cannot express, or a framework the project does not target (nothing runs); `3` a prerequisite is missing (Mono, or the console runner) and nothing failed. Environment: `MONO` names the Mono executable (default `mono`), `XUNIT_CONSOLE` a specific `xunit.console.exe`.

## What the project needs

- `<TargetFrameworks>net48;net10.0</TargetFrameworks>` and xunit v2.
- The console runner, as a package reference: `<PackageReference Include="xunit.runner.console" Version="2.8.1" PrivateAssets="all" />` (use your xunit version). The script runs the `tools/net48/xunit.console.exe` of the version the project restored.
- On macOS and Linux, Mono: `brew install mono` on macOS, `sudo apt-get install --no-install-recommends mono-devel` on Debian or Ubuntu (`mono-runtime` alone lacks the `System.Runtime` and `netstandard` facades the console runner loads; `mono-complete` works too, but adds about 28 MB of documentation and tools). Without it the script stops before running anything (exit 3) and says so; `--framework net10.0` runs the modern leg alone. Windows needs nothing extra: .NET Framework 4.8 is part of the system.

[ModWithTests](../../examples/ModWithTests/README.md#also-test-on-net-framework) shows the project changes.

## Filters

`dotnet test` takes the filter as written. The console runner has no filter expressions: it ORs options of one kind and ANDs different kinds. So the script translates only filters whose meaning survives, terms of one kind joined by `|`:

| Filter term | Console option |
|---|---|
| `FullyQualifiedName~text`, or a bare `text` | `-method "*text*"` |
| `FullyQualifiedName=Namespace.Class.Method` | `-method "Namespace.Class.Method"` |
| `Trait=value` (any trait name, such as `Category=Slow`) | `-trait "Trait=value"` |

Property names compare case-insensitively, as in `dotnet test`. Anything else stops the script before it runs anything, with exit 2: `&`, `!=`, parentheses, a method term OR'd with a trait term, and the test properties the console cannot select by (`DisplayName`, `Name`, `ClassName`, `TestCategory`, `Priority`, `Id`), which as a trait would match no test. Simplify the filter, or run `--framework net10.0` alone.

## Mono and parallel tests

Tests that share process-wide state must not run in parallel on any runtime: the game doubles are singletons, as the game's are, so a suite that uses them sets `[assembly: CollectionBehavior(DisableTestParallelization = true)]` (as [ModWithTests](../../examples/ModWithTests/MyMod.Tests/DrySiteTests.cs) does).

Mono adds a limit of its own. A mod's suite whose test collections ran in parallel under Mono aborted in native assembly-loading code (`mono_assembly_names_equal_flags`, under `mono_domain_assembly_search`) in 3 of 8 runs; with parallelization disabled it ran clean 6 times of 6, and either half of the suite alone was always clean. That is a race, so those counts are the evidence rather than a proof. The same suite takes well under a second, so serialising it costs nothing. The script therefore passes `-parallel none` to the console runner under Mono unless you choose otherwise with `--netfx-parallel`. On Windows the assembly's own setting applies. If a Mono run dies with a native stack trace rather than a test failure, check that it ran serially before looking elsewhere.

## In GitHub Actions

Hosted runners have no Mono: install it in the job (`sudo apt-get install -y --no-install-recommends mono-devel` on `ubuntu-latest`, `brew install mono` on `macos-latest`), or run the .NET Framework leg on `windows-latest`, which needs nothing. This repository's `test-runners` job does all three with the ModWithTests example; see [test.yml](../../.github/workflows/test.yml).

## Tests

`RunTestsScriptTests` (bash, on macOS and Linux) and `RunTestsPowerShellTests` (Windows PowerShell, on Windows) in `tests/Valheim.Testing.Tests` run the real scripts against fake `dotnet`, `mono` and console-runner tools that record their arguments: both frameworks passing, a failure on only one framework (a `net48`-only failure must fail the run), a `net48` leg that ran no tests, a failed build, filter translation (property names in any case) and refusal (including `DisplayName=`, `Name=` and `ClassName=`), a project without `net48`, a missing console runner, missing Mono, the parallel option and passed-through arguments. `dotnet run scripts/validate.cs` runs them. The `test-runners` CI job runs the scripts for real, with Mono on Ubuntu and macOS and .NET Framework on Windows, on a copy of ModWithTests that targets both frameworks, and then with an added test that fails only on .NET Framework, which must make the script exit 1 while `--framework net10.0` still passes, and with a filter that selects no test, which must fail the `net48` leg.
