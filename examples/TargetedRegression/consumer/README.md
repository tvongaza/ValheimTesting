# Standalone marker consumer

Copy these seven files into a new directory outside the ValheimTesting checkout. Select the latest release tag on the repository's Releases page before downloading the files, so they match the pinned packages. The relative source links follow that revision.

| Source | Destination | Purpose |
|---|---|---|
| [Plugin.cs](../../FullLifecycle/ExampleMod/Plugin.cs) | `MarkerMod/Plugin.cs` | The production marker command |
| [DrySiteRule.cs](../../ModWithTests/MyMod/DrySiteRule.cs) | `MarkerMod/DrySiteRule.cs` | The production dry-ground decision |
| [MarkerMod.csproj](MarkerMod/MarkerMod.csproj) | `MarkerMod/MarkerMod.csproj` | Game references supplied by the tool |
| [Scenario.cs](../Scenario.cs) | `MarkerScenario/Scenario.cs` | Action, complete observations and assertions |
| [MarkerOneShot.cs](../MarkerOneShot.cs) | `MarkerScenario/MarkerOneShot.cs` | The one-shot entry point |
| [MarkerScenario.csproj](MarkerScenario/MarkerScenario.csproj) | `MarkerScenario/MarkerScenario.csproj` | The exact published scenario package |
| [NuGet.Config](NuGet.Config) | `NuGet.Config` | NuGet.org as the only package source |

The consumer imports no repository props, targets or source projects. The mod project reads the game-reference properties that `valheim-test` supplies. The scenario needs no mod-specific test adapter: ValheimCLI supplies the player, Harmony and saved-object observations.

Follow [Your first gameplay assertion](../../../docs/tutorials/first-gameplay-assertion.md) for the command and expected result. If you already build your own mod, retain its project and replace the marker-specific code in `Scenario.cs`.

## Deliberately buggy parent

In your copied `MarkerMod/Plugin.cs`, omit this production line and retain the following `OK: marked` reply:

```csharp
UnityEngine.Object.Instantiate(prefab, new Vector3(x, ground - 0.25f, z), Quaternion.identity);
```

Run the tutorial's same command with a new output directory. Keep `Scenario.cs`, the fixture and the expected count unchanged. The zero-marker baseline passes, the command replies `OK: marked`, and the final assertion fails because no pole exists. Restore the line before running the candidate.

## Capture the result

Keep each run's `result.json`, `junit.xml`, `client-commands.jsonl`, `build-inputs.json`, `dependencies.lock.json` and scenario source together privately. Record the source revision and the exact command. `Toolkit` records the loaded package versions; the build inputs pin the game and mod assemblies.

For a public excerpt, copy the scenario step names, pass flags and the assertion's `Error` from `result.json`. Remove machine paths and account data. Keep full logs, saves and plans private. A documentation build never starts the game.
