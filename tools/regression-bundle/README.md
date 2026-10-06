# Regression bundle

A maintainer tool, not a package or an example: it runs from a checkout of this repository (`dotnet run scripts/bootstrap-cli.cs` once, then the commands below). Until `Valheim.Testing.Game` 0.1.0-preview.37 the same code was package API (`RegressionBundle.Create`, `Verify`, `Build`, `BundleSpec`); code that called it runs this tool instead. Turns a [targeted native regression](../../examples/TargetedRegression/README.md) into a small directory you can **review and then share** (a gist, a PR comment): the scenario source exactly as it ran, a project pinned to the toolkit, a manifest template with placeholders, an A/B table generated from the checked evidence, artifact hashes, limitations and a manifest of every file's origin. It never publishes anything; sharing stays your decision after you read every file.

A shared regression should be the code that ran, without the machine it ran on. Earlier public copies were rewritten by hand after the run: one kept a reference to an unreleased API, one carried two machine-only plugin pins until after publication, one README said four files when there were five. This tool makes those mistakes refusals.

## Describe the bundle

Copy [bundle.sample.json](bundle.sample.json) next to your private `regression.json` and fill it in. Relative paths are relative to the spec. The spec stays private; only what it produces is meant for sharing.

| Field | What it is |
|---|---|
| `title`, `issue`, `summary` | The README's heading, the public issue's URL and two sentences on what the scenario does |
| `environment` | The run's environment manifest. Only read: its template, with every machine-specific value a `<...>` placeholder, is generated |
| `template` | Instead of `environment`, for a run made before environment manifests: a manifest whose machine-specific values are all placeholders, bundled as is |
| `runner`, `sources` | The bundled project's name and its C# files, copied exactly |
| `probe` | Optional: `{ "directory", "project" }` of a game-side probe project, copied exactly into `Probe/` (without `bin` and `obj`) |
| `toolkit` | `{ "package": "<version>" }`, a published `Valheim.Testing.Game` version, or `{ "commit": "<40 hex>" }`, an exact commit of this repository |
| `plugins` | The plugin IDs the issue is about: the mod, its dependencies, the probe. Every other plugin name is refused |
| `deny` | Further names that must not appear (a character, a host name) |
| `modPlugin` | The mod's plugin GUID, when the evidence has no `run-manifest.json` |
| `arms` | Each arm's evidence directory, the result you declare (`pass` or `fail`, with `failingStep`) and, when the evidence has no `run-manifest.json`, its `commit` and `md5` (optionally `sha256`) |
| `native` | For a runner ported from a hand-written native one: `runner` (its source), `ranWith` (the toolkit it ran with), `excerpts` (lines `from`..`to` that must appear verbatim `in` a bundled file) and `differences` (every difference, all environment-only) |
| `limitations` | What the result does not establish |

## Make, verify and build it

```sh
dotnet run --project tools/regression-bundle -c Release -- bundle /absolute/path/to/bundle.json /absolute/path/to/new-bundle
dotnet run --project tools/regression-bundle -c Release -- build /absolute/path/to/new-bundle
dotnet run --project tools/regression-bundle -c Release -- verify /absolute/path/to/bundle.json /absolute/path/to/new-bundle
```

`bundle` checks, before it writes anything:

1. **The toolkit pin is available.** A package version must be on NuGet.org and not older than the version the run recorded; a commit must exist in the repository. An unreleased API pinned by a version that does not have it is refused here.
2. **Each arm's result comes from its evidence.** `result.json` must be a strictly pinned run whose summary agrees with its own steps and with `junit.xml`; every strict pin of the mod in the command trace must be the arm's MD5, and the trace must pin the world; `run-manifest.json`, the environment manifest and the spec must agree on the commit, MD5 and SHA256; the declared `pass` or `fail` (and failing step) must be what the evidence says. A result is never taken from an exit code.
3. **Only the mod differs.** The arms' traces must pin the same world UID and the same build of every other plugin, and two arms of one build are refused unless the environment declares a repeatability run.
4. **A port holds the assertions that ran.** With `native`, each excerpt must appear verbatim (whitespace aside) in its bundled file.

It then writes the bundle and scrubs every file: anything but source or text (logs, traces, saves, binaries), machine paths, user directories, Steam and platform account IDs, email addresses, the environment's paths, character, world name and UID, and any plugin name outside `plugins` (as a pin, a `BepIn*` attribute, or a name the run's traces pinned or the prepared game holds, including in comments and the README) are refused with file and line, and nothing is written.

The bundle holds the sources exactly, `<Runner>.csproj` pinned to `Valheim.Testing.Game` at the package version (or to a source checkout at the commit, with a target that refuses any other commit), `regression.template.json`, a README with the A/B table generated from the checked results and the build-and-run recipe, and `BUNDLE-MANIFEST.json`: each file's origin and SHA256, what was left out and why, the placeholder fields, the native runner and the evidence by SHA256, and the checks made. The toolkit check compares against the toolkit version `TargetedRegression.Run` recorded in each arm's provenance.

The tool builds against this checkout's `Valheim.Testing.Game`. To write the template in the shape of the published package a run used, build it against that package: `dotnet run --project tools/regression-bundle -c Release -p:ToolkitPackageVersion=<version> -- bundle ...`. That package must have the API the tool calls: `GameLaunch.DetectClient` is in `Valheim.Testing.Game` 0.1.0-preview.42 and later (earlier versions named it `ClientLaunch.Detect`).

`build` copies the bundle to a fresh directory and builds the runner with NuGet.org as its only package source and new `NUGET_PACKAGES` and HTTP cache directories; a commit-pinned bundle first clones the toolkit at that commit. A package or commit that is not available, or source that does not compile, fails it. The probe needs game assemblies and is not built.

`verify` repeats the evidence checks without the network, then checks the directory holds exactly the files `BUNDLE-MANIFEST.json` lists with their SHA256, the README's file count, the template's placeholders and the scrub. Run it after any edit and right before sharing.

Exit 0 done, 3 refused (the message lists each problem), 2 usage.

## Where the clean build is checked

`RegressionBundleTests` in `tests/Valheim.Testing.Tests` (which references this project) make bundles from fixtures and refuse each known mistake: an unrelated plugin pin, a machine path or private name, an unpublished or older toolkit version, a wrong file count, a tampered result, a trace of another build, more than the mod changed, a port without the native lines. One test also builds a small package-pinned bundle with only NuGet.org and an isolated cache, and shows the same project fails with an unpublished version; it needs the network, so it runs in validation and CI and is skipped when NuGet.org does not answer or with `VALHEIM_TESTING_OFFLINE=1`. Build each real bundle with `build` before sharing it.

## Limits

- The scrub knows plugin names from your allowlist, the runs' strict pins, the prepared game's plugins and `deny`. Another private detail (a server name, a person) still needs your review: read every file.
- A ported runner's harness has not been run in the game; the README says so and lists the differences you declared. The bundler checks the assertion lines, not that every difference is environment-only.
- Hashes are the ones that ran. Someone rebuilding the mod from the same commits gets other hashes; the template's preflight then names them for review.
