# Valheim.Testing.Bindings

[Current preview API reference](https://tvongaza.github.io/ValheimTesting/). The package's README is this guide; use its versioned source commit from the package metadata when checking an older preview.

*Assistant-written (Claude).*

An offline check that a built mod's references into the game still bind, from [ValheimTesting](https://github.com/tvongaza/ValheimTesting). A game update that removes, renames or retypes a member breaks a mod with `MissingFieldException`, `MissingMethodException` or `TypeLoadException`, but only when the runtime first compiles a method that uses it, which can be hours into play. This check reads the mod DLL with [Mono.Cecil](https://github.com/jbevain/cecil), resolves every type, field and method it references in the game assemblies you supply, and names the mod methods that use each one that no longer binds. (The modding wiki's examples of such breaks are `Terminal.m_input` in 0.217.14 and `SEMan.HaveStatusEffect` in 0.218.15; building against stale publicized assemblies hides the same break until the method runs.) It loads and runs nothing, and needs no game process.

- `Valheim.Testing.Bindings` (netstandard2.0) is the library: `BindingCheck.Check(modPath, options)` returns a `BindingReport`.
- `Valheim.Testing.Bindings.Tool` (net10.0) is the `valheim-bindings` .NET tool for CI.

```sh
dotnet tool install Valheim.Testing.Bindings.Tool --version 0.1.0-preview.2 --tool-path .tools
.tools/valheim-bindings MyMod/bin/Release/MyMod.dll --game-dir "path/to/valheim_Data/Managed"
```

Exit code 0 means every checked reference binds, 1 that some are missing, and 2 bad arguments, an unreadable file or `assembly_valheim` not supplied. Members that exist but are not accessible from the mod (a mod built against publicized assemblies references them on purpose) are reported separately and fail only with `--fail-on-access`, and then only without an `IgnoresAccessChecksTo` attribute for their assembly. That attribute is a declaration for this checker, **not proof that the original game's runtime grants access**. Conversely, `--fail-on-access` can warn on a mod whose private call works at run time. [Our pinned native check](https://github.com/tvongaza/ValheimTesting/blob/main/docs/native-validation-20261002.md) shows both outcomes. References into assemblies you did not supply are counted as not checked.

Mono.Cecil is MIT-licensed (Jb Evain, Novell); see THIRD-PARTY-NOTICES.md in the package.

## What is checked

What is checked, the way the runtime binds it:

- Every type, field and method the mod names in method bodies, signatures, locals, catch clauses, base types, interfaces, generic constraints, explicit overrides, attributes (including `typeof(...)` arguments) and the module's reference tables. Properties and events are their accessor methods (`get_Level`).
- A field matches by name and field type; a method by name, static or instance, generic arity, return type and parameter types, with byref, arrays and their rank, pointers, generic instances and modifiers compared exactly. Members are looked for in the type and then its base types, so a member moved to a base class still binds. Nested types are resolved through their declaring types and type forwarders are followed into the target assembly.
- A reference that does not bind is **missing**, listed once with every mod method (or type, field or attribute) that uses it, and with what the assembly has instead when that is a clue: the other overloads, the field's new type, a property that became a field.
- A member or type that binds but is not accessible from the mod (private, internal, or protected used outside a derived type) is an **access** finding, a separate list. See below.
- References into an assembly you did not supply are counted per assembly as **not checked**, never reported as missing. If the lookup of a member reaches a base type in an assembly you did not supply (a game type's `MonoBehaviour` base, say), the member is still reported missing, with a note that it was not searched there.

## The game's assemblies

Supply the game's managed directory with `--game-dir` (the client's `valheim_Data/Managed` holds `assembly_valheim`, `assembly_utils`, the Unity modules and the framework the game runs on; add `BepInEx/core` for BepInEx and Harmony) or individual files with `--game-file`, which are matched by the assembly name inside them, so a pinned copy may have any file name. `--only <name>` limits which assemblies from the directories are checked. `assembly_valheim` must be supplied whenever the mod references it, so a wrong path fails instead of checking nothing; `--require <name>` adds others.

## Publicized assemblies and access

Mods commonly compile against publicized copies of the game assemblies, where every member is public, and reach private members at run time. Checked against the real assemblies, each such reference is an access finding even if nothing changed, so access findings never fail the check by default: missing references are what break. Each access finding says whether the mod carries `[assembly: IgnoresAccessChecksTo("<assembly>")]` for that assembly (some publicizer build tasks add it), which declares the access as intended: those are **info**, the rest **warning**. `--fail-on-access` fails on warnings only. Checking against the publicized copy the mod was built with still finds removals, but reports no access findings. This is metadata classification, not a runtime permission check: [a pinned native test](https://github.com/tvongaza/ValheimTesting/blob/main/docs/native-validation-20261002.md) found that the attribute alone passed `--fail-on-access` yet threw `FieldAccessException`, while a plugin built with `AllowUnsafeBlocks=true` executed the access despite a checker warning. Run an actual call with the original game assembly before relying on private access.

## Exit codes

| Exit code | Meaning |
|---|---|
| 0 | Every checked reference binds (access findings may be listed) |
| 1 | At least one missing reference; with `--fail-on-access`, also an access finding without `IgnoresAccessChecksTo` |
| 2 | Bad arguments, an unreadable, malformed or missing file or directory, or a required assembly not supplied: the check is incomplete. Any failure to read a mod (a malformed method body, say) is exit 2 with the reason, never an unhandled crash with another code. |

## In CI

Run it in the job that builds the plugin, since both need the game's assemblies, and before anything launches the game:

```yaml
      # After the step that builds the plugin.
      - name: Check that game references still bind
        shell: bash
        run: |
          dotnet tool install Valheim.Testing.Bindings.Tool --version 0.1.0-preview.2 --tool-path .tools
          .tools/valheim-bindings MyMod/bin/Release/MyMod.dll --game-dir "$VALHEIM_MANAGED"
```

`VALHEIM_MANAGED` is wherever that job finds the game's `Managed` directory: a self-hosted runner's install, or the dedicated server from anonymous SteamCMD (app 896660), whose `valheim_server_Data/Managed` holds the server's own copies of the game assemblies; check a client mod against the client's where you can. As for any build, do not commit game assemblies or upload them as artifacts or public caches. The version shown is the newest release; pin the one you choose.

## From a test

```csharp
var options = new BindingCheckOptions();
options.GameDirectories.Add(managedDirectory);
BindingReport report = BindingCheck.Check(modDll, options);
Assert.Empty(report.MissingRequired);
Assert.True(report.Binds, string.Join("\n", report.Missing.Select(f => $"{f.Member}: {string.Join(", ", f.UsedBy)}")));
```

## Limits

Only metadata references are checked. Members found by name at run time are not: Harmony's `[HarmonyPatch(typeof(T), "Method")]` and `AccessTools` lookups (`nameof` compiles to a string too), reflection, and Jötunn or prefab names. Neither are type-shape changes that fail when a mod type loads (a game interface gaining a member the mod's class must implement, a base class becoming sealed or gaining an abstract member) or generic constraint changes. An attribute whose arguments name an enum from an assembly that cannot be found is noted, and the types in its arguments are not checked. A pass says the checked references bind, not that the mod behaves correctly with the new game version. The tests build a miniature game in three versions and two mods at test time; the tool has not been run against a real Valheim update in this repository.

Unofficial community tooling; not affiliated with or endorsed by Iron Gate or Coffee Stain. Valheim is a trademark of Iron Gate AB.
