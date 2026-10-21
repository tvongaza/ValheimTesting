# Run an A/B regression

Keep the marker assertion unchanged while replacing the mod build. Selected action and independent-count lines from [Scenario.cs](../../examples/TargetedRegression/Scenario.cs):

```csharp
var reply = host.Execute("examplemod_mark " + At());
round.Step("exactly one marker stands there in the host's saved objects", () => RequireMarkers(host, x, z, ExpectedMarkers));
```

`ExpectedMarkers` is `1`. The no-op parent replies `OK: marked` but creates nothing. Captured parent step:

```json
{
  "Name": "first: exactly one marker stands there in the host's saved objects",
  "Passed": false,
  "Error": "0 wood_pole2 marker(s) stand at (-2.1 -2.2); expected 1."
}
```

The corrected candidate passes the same step:

```json
{
  "Name": "first: exactly one marker stands there in the host's saved objects",
  "Passed": true
}
```

See [Limits](#limits) for the toolkit build used by these excerpts.

## When to use this

| You want to compare | Use |
|---|---|
| Two builds loading in a client | [Start's load comparison](../packages/Valheim.Testing.NativeSmoke.md#start) |
| A server mod set with and without one mod | [Server load A/B](../packages/Valheim.Testing.NativeSmoke.md#server-load-ab) |
| The same gameplay assertion on two builds | `TargetedRegression`, below |

Complete [Your first gameplay assertion](../tutorials/first-gameplay-assertion.md) first. Keep its completed run directory: it records the fixture, character, ValheimCLI and loader inputs.

## Steps

### 1. Copy the example

Download the [four-file package consumer](../../examples/TargetedRegression/consumer/README.md#ab-regression-consumer) into `MarkerRegression`, outside the toolkit checkout. Choose one source revision. Keep `Program.cs` and `Scenario.cs` unchanged.

The project builds with published `0.1.0`. Reliable retirement after report-write failure requires an unreleased correction. Native excerpts use candidate packages; published release verification remains pending. <!-- TODO(docs): refresh against published 0.2 after #899 -->

### 2. Fill in regression.json

From your independent mod directory, build each real source commit:

```sh
valheim-test build --project MarkerMod/MarkerMod.csproj
```

Each build prints `Built mod:` and `Build inputs:` paths. Retain both output directories. Use the [deliberately buggy parent](../../examples/TargetedRegression/consumer/README.md#deliberately-buggy-parent), then restore the production line and build the candidate. Record both source commits and each build's `ModSha256` from `build-inputs.json`.

Copy the completed `start` run's `regression.json` into `MarkerRegression`. Before moving it, resolve relative input paths against its original directory:

| Paths | Keep fixed |
|---|---|
| `fixture.root`, `client.characterStore` | Original world and disposable character sources |
| `cli.core.file`, `cli.manifest` | The same pinned ValheimCLI |
| `plugins[*].file`, `probe.file`, `patchers[*].file`, `configs` values | Every dependency and configuration |

If `environments.json` sits beside the original regression file, copy that recorded snapshot too. Resolve its relative paths against its original directory before moving it. Keep it beside your new `regression.json`; the program reads it automatically. Preserve any original client-environment selection: append `--client-env NAME` to every command below.

Preserve all non-mod fields. Replace `mod.arms` with your two artifact paths, reviewed hashes and real commits:

```json
"mod": {
  "installAs": "ExampleMod.dll",
  "arms": {
    "parent": { "file": "<parent build>/ExampleMod.dll", "sha256": "<sha256>", "commit": "<parent commit>" },
    "candidate": { "file": "<candidate build>/ExampleMod.dll", "sha256": "<sha256>", "commit": "<candidate commit>" }
  }
}
```

### 3. Preflight without the game

From `MarkerRegression`:

```sh
dotnet run --project MarkerRegression.csproj -c Release -- preflight regression.json
```

It checks both arms and retires their prepared profiles. It ends with:

```text
PREFLIGHT PASSED; nothing was launched. Review the arms' commits and hashes above before the run.
```

### 4. Run each arm

From the same directory, use new private evidence directories outside your repository:

```sh
dotnet run --project MarkerRegression.csproj -c Release -- run regression.json parent /absolute/path/to/new-evidence-parent
dotnet run --project MarkerRegression.csproj -c Release -- run regression.json candidate /absolute/path/to/new-evidence-candidate
```

The parent prints `FAIL`; the candidate prints `PASS`. Each writes `result.json`, `junit.xml`, `run-manifest.json` and command traces. Each arm gets a fresh profile and fixture copy.

## Check the result

Require the parent to fail only the named final count, and the candidate to pass every step. Both must have `CleanupVerified: true` and `disposableInstall: removed`.

Compare every non-mod input. In `run-manifest.json`, normalize only `arm`, `modCommit`, `modSha256` and the selected mod's `sha256`/`md5` under `allowlist`. The remaining fields must match. Keep the same scenario and verify unchanged fixture-source hashes.

Keep full logs, plans and saves private. [Regression bundle](../../tools/regression-bundle/README.md) prepares a public copy for review.

## Problems

| Result | Meaning or next step |
|---|---|
| Exit `1` | A report failed; inspect setup, scenario and cleanup steps |
| Exit `3` | Refusal or unknown outcome; an I/O or cleanup error can follow a launch |
| Two arms have one SHA256 | Supply distinct builds, or declare a repeatability run |
| A run was interrupted | Follow [recovery](debug-a-native-run.md) for its exact run ID |

## Limits

Native excerpts use `0.1.0-candidate.9b409d3f735a` from [this source revision](https://github.com/tvongaza/ValheimTesting/blob/364d1b63d5e52d430c5cdf736da64bbf46df5923/examples/TargetedRegression/Program.cs), on Mac.

Run the direct Windows consumer from an interactive desktop terminal. It has no SSH/session-0 desktop launch route.

## Related

- [Targeted regression contract](../reference/sessions-targeted-regression.md)
- [TargetedRegression example](../../examples/TargetedRegression/README.md)
- [Check that a mod loads](check-a-mod-loads.md)
