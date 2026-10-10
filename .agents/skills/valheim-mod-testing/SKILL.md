---
name: valheim-mod-testing
description: Add or run ValheimTesting tests in a Valheim mod repository, from source-linked unit tests to disposable native game checks. Use for mod adoption, regression scenarios, and test evidence; not for developing ValheimTesting itself.
---

# Test a Valheim mod

Read the mod repository's own instructions first. Keep the mod's tests, adapters, fixtures, and scenario expectations in that repository; do not add ValheimTesting dependencies to the production plugin.

1. Choose the smallest layer that can observe the behavior. For production logic using game types, start with the source-linked [ModWithTests example](https://github.com/tvongaza/ValheimTesting/tree/main/examples/ModWithTests) and published `Valheim.Testing.Doubles`. For controlled process/transport behavior, use `Valheim.Testing.Game` fakes. Use a native run only for game lifecycle, loader, physics, replication, save, or visual behavior. The [adoption guide](https://github.com/tvongaza/ValheimTesting/blob/main/docs/adopting.md) maps these layers to examples.
2. Pin exact published package versions from the [package table](https://github.com/tvongaza/ValheimTesting/blob/main/docs/getting-started.md). A consumer does not bootstrap or build ValheimTesting. Run the mod's focused test project first; build the plugin only where game references are installed.
3. For a disposable native load check, run `valheim-test env list` and `valheim-test env preflight`, then use `valheim-test start` for a hosted client or `valheim-test server-load` for a dedicated server. Use the mod-owned scenario when a plugin-load check cannot prove the behavior. Follow the [one-shot guide](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.NativeSmoke.md) for supported options and environment setup; let the tool stage its pinned ValheimCLI bundle and owned fixture.
4. Check the result and evidence, including the exact mod/build identity, failed or unexecuted steps, and teardown. After a native run, `valheim-test env status` must show nothing your run left; use the tool's recovery command for an interrupted owned run. Never treat a connected CLI, a synthetic pass, or an incomplete observation as native behavior evidence.

For a fuller native example with save, restart, and rejoin, use [FullLifecycle](https://github.com/tvongaza/ValheimTesting/tree/main/examples/FullLifecycle). For mod-load and mod-conflict diagnosis, use [Debugging mods](https://github.com/tvongaza/ValheimTesting/blob/main/docs/debugging-mods.md). Read the current toolkit docs before copying commands from an older preview.
