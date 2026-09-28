# ScriptEngine extension reload check

This is a bounded real-game check of the optional extension lifecycle. It attaches
through the shipped ValheimCLI transport; it does not launch or stop Valheim, create a
world, or select a character. It works at the main menu.

Keep the ValheimCLI core in `BepInEx/plugins`. Enable ScriptEngine's `LoadOnStart` and
`EnableFileSystemWatcher`. Use an **empty, dedicated** `BepInEx/scripts` directory:
ScriptEngine reloads every script when any DLL changes. Do not run this against a
session whose scripts or game state belong to somebody else.

Build the core and two revisions of the same example plugin. The probe embeds
symbols because ScriptEngine expects symbols when loading an assembly. The
outputs contain the probe only; do not copy a second ValheimCLI assembly into scripts.

```sh
# In the pinned CLI checkout (not this repository):
dotnet build valheimCLI.csproj -c Release
dotnet build examples/ReloadProbe/ReloadProbe.csproj -c Release -o artifacts/probe-a
dotnet build examples/ReloadProbe/ReloadProbe.csproj -c Release -p:ProbeRevision=B -o artifacts/probe-b
# Back in ValheimTesting; use absolute paths to the two built probe DLLs:
dotnet build examples/ReloadCheck/ReloadCheck.csproj -c Release

dotnet examples/ReloadCheck/bin/Release/net10.0/ReloadCheck.dll \
  5555 artifacts/probe-a/ReloadProbe.dll artifacts/probe-b/ReloadProbe.dll \
  /absolute/disposable-game/BepInEx/scripts /absolute/results/reload.json /private/menu-pins.txt
```

Use the actual ValheimCLI port instead of `5555`. This example is local: DLL replacement
must reach the same game as the loopback connection. Supply a strict pins file listing the installed core, ScriptEngine and all other loaded plugins (and an exact world UID if run in a world). The driver requires the probe absent initially, then pins its A/B MD5 from the supplied artifacts and requires absence again after removal. It never trusts a running-game snapshot to invent an expected hash.

Every command, including expected refusals and the second waiting connection, gets a `cli_expect --strict` preflight. During replacement only the explicit new pins are polled until they hold; no gameplay command bypasses a mismatch. If the game's persistent `[Expectations]` file is enabled, the fixture owner must keep its staged expectations consistent as well; this driver does not disable or overwrite that guard.

The check keeps one control connection open throughout. It installs A, verifies
its command and owned GameObject, starts a bounded waiting command on a second
connection, then atomically replaces A with B. It requires:

- A new extension instance and revision B responding on the original connection.
- A's active request ending with `extension_unloaded` and A's instance token.
- A's removed command returning `no_extension_command` rather than executing old code.
- Exactly one probe resource after replacement: A's resource was destroyed.
- An unchanged ValheimCLI core build identity and load time.
- No registered probe after deleting its file, with ValheimCLI still responding.

The probe logs iterator disposal and registered cleanup separately, including the
number of active waits. The runner writes structured evidence and exits nonzero
on failure. It removes its deployed probe in `finally`; the session owner remains
responsible for shutdown and checking restoration. Reports can contain local
paths from `cli_build`; review before publishing.

## Earlier native evidence — 26 September 2026

Passed on real native ARM64 Mac Valheim (1.0.16, Unity 6000.0.75f1), launched
headlessly at the main menu with BepInEx 5.4.23.5 and ScriptEngine 11.1. Both
cleanup callbacks ran with zero active waits. The ValheimCLI core was unchanged.

This establishes read-only active-command cancellation, replacement, owned
GameObject cleanup, command removal, and connection continuity. It does not test
world mutations, delayed quiescence after a mutation, queued mutation cancellation,
client rendering, or actual assembly memory reclamation. Those are not implied
by a successful ScriptEngine replacement. The pure lifecycle tests cover the
additional gate and quiescence cases; Roads save/restart scenarios remain separate.

No BepInEx warning/error was logged. Unity emitted Apple native-library and
headless resource-upload errors; this is not a claim of a clean graphics startup.

The required pins-file argument and strict per-command/reload preflights were added afterward. They compile and have controlled-transport tests; the revised driver then passed the strict A/B/absent reload campaign recorded in [the native validation report](../../docs/native-validation-20260927.md).
