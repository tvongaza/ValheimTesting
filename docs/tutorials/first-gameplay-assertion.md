# Your first gameplay assertion

## What you build

ExampleMod places a wooden pole on dry ground. Your test proves the site starts empty, asks the mod to mark it, then independently counts the saved objects. A successful command reply alone cannot pass the test.

This excerpt from [Scenario.cs](../../examples/TargetedRegression/Scenario.cs) shows the action and independent assertion. The full source records the position and requires an empty starting site first.

```csharp
round.Step("the mod marks the ground where the host stands", () =>
{
    var reply = host.Execute("examplemod_mark " + At());
    round.Write("mark", new { reply = reply.Output });
    if (reply.Output.Count(line => line.StartsWith("OK: marked ", StringComparison.Ordinal)) != 1)
        throw new InvalidOperationException("ExampleMod did not confirm one marker: " + string.Join(" | ", reply.Output));
});
round.Step("exactly one marker stands there in the host's saved objects", () => RequireMarkers(host, x, z, ExpectedMarkers));
```

`ExpectedMarkers` is `1`. `RequireMarkers` rejects incomplete observations, then counts `wood_pole2` objects near the recorded position. The captured `result.json` records **"first: exactly one marker stands there in the host's saved objects"** with **`Passed: true`**.

[MarkerOneShot.cs](../../examples/TargetedRegression/MarkerOneShot.cs) connects this assertion to the tool's owned hosted world.

## You need

- The .NET 10 SDK and the [Windows native prerequisites](../reference/platforms.md#windows).
- Valheim and the free Valheim Dedicated Server installed through Steam, with the Steam client signed in. `env preflight` checks both installs.
- Permission to launch Valheim with a disposable fixture and character.
- A new directory outside any ValheimTesting checkout.

## 1. Save the example files

Download the [seven-file consumer](../../examples/TargetedRegression/consumer/README.md) from one repository revision into your new directory. Each row names its destination. You need no toolkit clone, bootstrap or local package feed.

Your directory contains `NuGet.Config`, `MarkerMod` and `MarkerScenario`. The mod source is the existing FullLifecycle marker; its decision is the ModWithTests rule. The scenario is also the TargetedRegression assertion.

## 2. Install the pinned tool

From that directory, in PowerShell:

```powershell
dotnet tool install Valheim.Testing.NativeSmoke --version 0.1.0 --tool-path tools --configfile NuGet.Config
```

The tool writes `tools/valheim-test.exe`.

Check the machine before launching:

```powershell
.\tools\valheim-test.exe env preflight
```

Continue when it prints `ELIGIBLE`. If it refuses, follow [Debug a native run](../guides/debug-a-native-run.md).

## 3. Build and run the assertion

From the same directory:

```powershell
.\tools\valheim-test.exe start --project MarkerMod/MarkerMod.csproj --scenario-project MarkerScenario/MarkerScenario.csproj --output run-pass
```

The tool builds both projects, stages the pinned loader and ValheimCLI, hosts its disposable fixture, then removes its owned client, character and profile. It writes `run-pass/evidence/smoke/result.json` and `junit.xml`.

The run exits 0 when gameplay and cleanup pass. The result separates game readiness, gameplay and cleanup:

```text
  "Passed": true,
  "PreflightPassed": true,
  "RuntimeReady": true,
  "ScenarioPassed": true,
  "CleanupVerified": true
}
```

Read the named scenario steps too. They check the Harmony patch, player position, empty starting site, marker action and complete saved-object census.

## 4. Make the assertion fail

In `MarkerScenario/Scenario.cs`, change `ExpectedMarkers = 1` to `ExpectedMarkers = 2`. Run the same command with a new directory:

```powershell
.\tools\valheim-test.exe start --project MarkerMod/MarkerMod.csproj --scenario-project MarkerScenario/MarkerScenario.csproj --output run-wrong-count
```

The run exits 1. In `result.json`, `RuntimeReady` and `CleanupVerified` remain true, while `ScenarioPassed` is false. The final marker-count step contains this captured error:

```text
1 wood_pole2 marker(s) stand at (-2.1 -2.2); expected 2.
```

Restore the expected count. Check what your runs left:

```powershell
.\tools\valheim-test.exe env status
```

It lists no unfinished run. A cleanup failure is a failed run even when the gameplay assertion passes.

## What you have now

You exercised production mod code and independently observed its effect in Valheim. The [deliberately buggy parent](../../examples/TargetedRegression/consumer/README.md#deliberately-buggy-parent) shows how the unchanged assertion catches a command that replies successfully without placing a pole.

For your own mod, keep its existing build project. Edit the plugin identity, declared patch, action and observation in `Scenario.cs` to describe your feature. Omit `--output` when running from that project directory; the tool chooses a private output outside its source tree.

## Next

Use the same scenario with [TargetedRegression](../../examples/TargetedRegression/README.md) to compare parent and candidate builds. Use [FullLifecycle](../../examples/FullLifecycle/README.md) for save, restart and client visibility.
