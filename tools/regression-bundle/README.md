# Regression bundle

Turn a targeted native regression into a small, scrubbed directory that you review and then share, for example in a gist or a PR comment.

This is a maintainer tool, not part of any package. Run it from a checkout of this repository. First run `dotnet run scripts/bootstrap-cli.cs` once, on any OS, as in [CONTRIBUTING.md](../../CONTRIBUTING.md#set-up-and-validate-locally). The input is a regression made from the [targeted regression template](../../examples/TargetedRegression/README.md).

The tool publishes nothing. You decide whether to share the bundle after you read every file.

## Describe the bundle

Copy [bundle.sample.json](bundle.sample.json) next to your private `regression.json` and fill it in. Relative paths are relative to the spec. The spec stays private. Share only what it produces.

| Field | What it is |
|---|---|
| `title`, `issue`, `summary` | README heading, public issue URL, two sentences on the scenario |
| `environment` | The run's environment manifest. Read only; the tool generates a placeholder template from it |
| `template` | Instead of `environment`: a manifest with every machine value a placeholder, bundled as is |
| `runner`, `sources` | The bundled project's name and its C# files, copied exactly |
| `probe` | Optional `{ "directory", "project" }`, copied into `Probe/` without `bin` and `obj` |
| `toolkit` | The toolkit pin: a published package version or a source commit |
| `plugins` | Plugin IDs the issue is about. Every other plugin name is refused |
| `deny` | Further names that must not appear, such as a character or host name |
| `modPlugin` | The mod's plugin GUID, when the evidence has no `run-manifest.json` |
| `arms` | Each arm's evidence directory and declared result |
| `native` | For a port of a hand-written native runner: `runner`, `ranWith`, `excerpts`, `differences` |
| `limitations` | What the result does not establish |

To pin a published package, write:

```json
"toolkit": { "packageId": "Valheim.Testing.GameSessions", "package": "<version>" }
```

To pin an exact source commit, write:

```json
"toolkit": { "packageId": "Valheim.Testing.GameSessions", "commit": "<40 hex>" }
```

Set `packageId` to the package that provided `TargetedRegression` for the recorded run. A run that used `Valheim.Testing.Game` can omit `packageId`.

Each arm declares `expect` (`pass` or `fail`) and, for `fail`, its `failingStep`:

```json
"arms": {
  "parent": { "evidence": "<new-evidence-parent>", "expect": "fail", "failingStep": "<the step the fix changes>" },
  "candidate": { "evidence": "<new-evidence-candidate>", "expect": "pass" }
}
```

If the evidence has no `run-manifest.json`, add the arm's `commit` and `md5`, and optionally `sha256`.

In `native`, each entry of `excerpts` names lines `from`..`to` that must appear verbatim `in` a bundled file. `differences` lists every difference, all environment-only.

## Make, verify and build it

Run from the repository root:

```sh
dotnet run --project tools/regression-bundle -c Release -- bundle /absolute/path/to/bundle.json /absolute/path/to/new-bundle
dotnet run --project tools/regression-bundle -c Release -- build /absolute/path/to/new-bundle
dotnet run --project tools/regression-bundle -c Release -- verify /absolute/path/to/bundle.json /absolute/path/to/new-bundle
```

Any other arguments print the usage line:

```text
Usage: regression-bundle bundle <bundle.json> <new-bundle-directory> | verify <bundle.json> <bundle-directory> | build <bundle-directory>
```

### bundle

`bundle` checks, before it writes anything:

1. **The toolkit pin is available.** A package version must be on NuGet.org and not older than the run recorded. A commit must exist in the repository.
2. **Each arm's result comes from its evidence.** `result.json`, `junit.xml`, the command trace, `run-manifest.json`, the environment manifest and the spec must agree. A result is never taken from an exit code.
3. **Only the mod differs.** The arms must pin the same world UID and the same build of every other plugin. Two arms of one build are refused unless the environment declares a repeatability run.
4. **A port holds the assertions that ran.** With `native`, each excerpt must appear verbatim (whitespace aside) in its bundled file.

It then scrubs every file. Logs, traces, saves, binaries, machine paths, account IDs, email addresses, the environment's names and UIDs, and unlisted plugin names are refused with file and line. If anything is refused, nothing is written.

The bundle holds:

- the sources, exactly as they ran
- `<Runner>.csproj`, pinned to the toolkit package version or source commit
- `regression.template.json`
- a README with an A/B table generated from the checked results, and the build-and-run recipe
- `BUNDLE-MANIFEST.json`: each file's origin and SHA256, what was left out and why, and the checks made

On success it prints one `checked: ` line per check, then a line that starts with `BUNDLE WRITTEN to`.

The tool builds against this checkout's Game and GameSessions packages. To write the template in the shape of the published package a run used, build against that package:

```sh
dotnet run --project tools/regression-bundle -c Release -p:ToolkitPackageVersion=<version> -- bundle ...
```

### build

`build` copies the bundle to a fresh directory and builds the runner. NuGet.org is the only package source, with new `NUGET_PACKAGES` and HTTP cache directories. A commit-pinned bundle first clones the toolkit at that commit. A missing package or commit, or source that does not compile, fails it. The probe needs game assemblies and is not built.

### verify

`verify` repeats the evidence checks without the network. It then checks the files and SHA256s against `BUNDLE-MANIFEST.json`, the README's file count, the template's placeholders and the scrub. Run it after any edit and right before sharing.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Done |
| `2` | Usage error |
| `3` | Refused. The `REFUSED: ` message lists each problem |

## Where the clean build is checked

`RegressionBundleTests` in `tests/Valheim.Testing.Tests` make bundles from fixtures and refuse each known mistake. One test builds a small package-pinned bundle with only NuGet.org and an isolated cache. It needs the network. It is skipped when NuGet.org does not answer or with `VALHEIM_TESTING_OFFLINE=1`. Build each real bundle with `build` before you share it.

## Limits

- The scrub knows plugin names from `plugins`, the runs' strict pins, the prepared game's plugins and `deny`. Other private details, such as a server name or a person, need your review.
- A ported runner's harness has not run in the game. The tool checks the assertion lines, not that every difference is environment-only.
- Hashes are the ones that ran. A rebuild of the mod from the same commits gives other hashes. The template's preflight then names them for review.
