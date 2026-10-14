# Your first server load check

Check that your mod loads on a real Valheim dedicated server, and that a client without the mod can join it.

## What you build

One command that starts a disposable dedicated server with your mod, joins a clean client, and writes a private result you can read. Nothing in your Steam installs or saves changes.

## You need

- A Windows PC with Valheim and the free Valheim Dedicated Server installed through Steam.
- The Steam client running and signed in on that PC's desktop.
- The .NET 10 SDK.
- A BepInEx mod with exactly one project in its folder, `MyServerMod` here.

Read [Before a native run](../guides/before-a-native-run.md) once before your first run.

## Steps

1. Install the tool:

   ```sh
   dotnet tool install Valheim.Testing.NativeSmoke --version 0.1.0-rc.1 --global
   ```

   This installs `valheim-test`.

2. Build your mod from its project folder:

   ```sh
   cd MyServerMod
   dotnet build -c Release
   ```

   The build writes your plugin DLL under `bin/Release/<target framework>/`.

3. Check the machine:

   ```sh
   valheim-test env preflight
   ```

   It prints what it detected, one line per environment, and a verdict. The last line starts with:

   ```text
   ELIGIBLE: the inventory has a server and a client environment, and this machine's journal holds no run going or left unrecovered.
   ```

   If it starts with `REFUSED`, fix the reason it names and run it again.

4. Run the load check from the same folder:

   ```sh
   valheim-test server-load
   ```

   With no `--mod`, the tool picks the one plugin under `bin/Debug` or `bin/Release` and prints why. It refuses a DLL older than a source file, so rebuild after every edit. It prints the server and client it chose, `PREFLIGHT PASSED`, then starts the server and the client.

   The run ends like this; the run folder is shortened to `<output>`:

   ```text
   Output: <size> in <output>\evidence
   PASS
   log scan: 4/4 logs present; failure=0 warning=69 expected=0 required-missing=0; most frequent: boot-1 BepInEx log/headless-server-graphics x14, boot-1 Unity log/headless-server-graphics x14, boot-1 Unity log/missing-script x13
   SERVER_JOIN_PASS: 1 selected mod(s), 106.5s. Clean client joined with selected server mods absent. Private evidence (5 MB) in <output>
   ```

   Warnings like these are normal and do not fail a run. `headless-server-graphics` is the dedicated server's own errors about its missing graphics device. `missing-script` is Unity reporting an asset-bundle script it did not load. A line suggesting `valheim-test init server` follows.

5. Read the result. Open `evidence/result.json` in the folder the `SERVER_JOIN_PASS` line names. Each step has `Passed` and a `Phase`, and these four flags are all `true`:

   ```json
   "PreflightPassed": true,
   "RuntimeReady": true,
   "ScenarioPassed": true,
   "CleanupVerified": true
   ```

6. Check that the run left nothing:

   ```sh
   valheim-test env status
   ```

   The last line reads `N runs ended and left nothing. Nothing was changed.`, and the command exits 0.

## What you have now

- Proof that your mod's dependencies resolve and its plugins load on a dedicated server.
- Proof that a client without the mod enters the world.
- A private evidence folder: `result.json`, `junit.xml`, the kept logs and `dependencies.lock.json`. Keep it private; it holds a server password and machine paths.

A pass says nothing about your mod's gameplay.

## Next

- [Check that a mod loads](../guides/check-a-mod-loads.md): a client-side mod, a set of mods, and which mod breaks a set.
- [Debug a native run](../guides/debug-a-native-run.md): when a run refuses or fails.
- [`valheim-test`](../packages/Valheim.Testing.NativeSmoke.md#your-own-scenario): add your own assertions to the same run.
