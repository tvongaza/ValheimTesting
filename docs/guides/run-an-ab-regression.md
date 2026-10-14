# Run an A/B regression

Prove that a change fixes a bug in the real game: run the same scenario on the parent build and the candidate build of your mod, with everything else pinned, and compare.

## When to use this

| You want to compare | Use |
|---|---|
| Two builds of a client-side mod loading | `valheim-test start --mod DLL --compare-mod DLL --compare-source COMMIT` ([start](../packages/Valheim.Testing.NativeSmoke.md#start)) |
| A server mod set with and without one mod | `valheim-test server-load-ab` ([server-load-ab](../packages/Valheim.Testing.NativeSmoke.md#server-load-ab)) |
| Your own scenario on two builds, in one hosted world | `TargetedRegression`, below |

You need a Windows or macOS PC with Valheim and Steam signed in, the two builds of your mod with their commits, and a fixture world.

## Steps

### 1. Copy the example

Copy [examples/TargetedRegression](../../examples/TargetedRegression/README.md) into your repository. Keep `Program.cs` as it is, and put your mod's steps in `Scenario.cs`. In your copy, reference the `Valheim.Testing.GameSessions` package at an exact version ([versions](../package-versions.md)).

### 2. Fill in regression.json

Copy `regression.sample.json` outside the repository and replace every `<...>`. The two arms are your builds:

```json
"mod": {
  "installAs": "ExampleMod.dll",
  "arms": {
    "parent": { "file": "<parent build>/ExampleMod.dll", "sha256": "<sha256>", "commit": "<parent commit>" },
    "candidate": { "file": "<candidate build>/ExampleMod.dll", "sha256": "<sha256>", "commit": "<candidate commit>" }
  }
}
```

Leave a `sha256` empty and the preflight prints the hash for you to review. The machine is not in the file: by default the client is this machine's detected Valheim.

### 3. Preflight without the game

```sh
dotnet run --project examples/TargetedRegression -c Release -- preflight /absolute/path/to/regression.json
```

It stages each arm's profile and checks every hash, the fixture and the plugins' metadata. It ends with:

```text
PREFLIGHT PASSED; nothing was launched. Review the arms' commits and hashes above before the run.
```

### 4. Run each arm

```sh
dotnet run --project examples/TargetedRegression -c Release -- run /absolute/path/to/regression.json parent /absolute/path/to/new-evidence-parent
dotnet run --project examples/TargetedRegression -c Release -- run /absolute/path/to/regression.json candidate /absolute/path/to/new-evidence-candidate
```

Each prints `PASS` or `FAIL` and fills its evidence directory. Each arm gets a fresh profile.

## Check the result

Compare the arms by their `result.json`, not by exit code alone:

- The parent fails at the step the change fixes.
- The candidate passes every step.
- The two `run-manifest.json` files differ only in the mod's hash.

To share a public copy, run [regression-bundle](../../tools/regression-bundle/README.md) on the two evidence directories.

## Problems

| Symptom | Fix |
|---|---|
| Two arms with one SHA256 are refused | Use two builds, or set `"repeatability": true` to run one build twice |
| A file is refused for its hash | Review the printed hash and paste it, or rebuild the arm from its commit |
| The fixture layout is refused | Give a root holding exactly one world, as the game saved it |
| A hard dependency is missing | Add it to `plugins`, or run the [dependency resolver](../reference/game-run-inputs.md#resolve-mod-dependencies) |
| A run was interrupted | `valheim-test env status`, then `valheim-test env recover --run ID` |

## Related

- [Targeted regression](../reference/sessions-targeted-regression.md): the stage checks and members.
- [TargetedRegression example](../../examples/TargetedRegression/README.md): every field of `regression.json`.
- [Check that a mod loads](check-a-mod-loads.md)
