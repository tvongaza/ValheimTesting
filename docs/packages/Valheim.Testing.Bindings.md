# Valheim.Testing.Bindings

[API reference](https://tvongaza.github.io/ValheimTesting/reference/). This page covers the library and its `valheim-bindings` tool.

Check offline that a built mod's references into the game still bind.

## Purpose

A game update can remove, rename or retype a member your mod uses. The mod then throws `MissingFieldException`, `MissingMethodException` or `TypeLoadException`. It throws only when the runtime first compiles a method that uses the member, possibly hours into play.

This check reads your mod DLL with [Mono.Cecil](https://github.com/jbevain/cecil). It resolves every type, field and method the mod references in the game assemblies you supply. For each reference that no longer binds, it names the mod methods that use it. It loads and runs nothing, and needs no game process.

- `Valheim.Testing.Bindings` (netstandard2.0) is the library. `BindingCheck.Check(modPath, options)` returns a `BindingReport`.
- `Valheim.Testing.Bindings.Tool` (net10.0) is the `valheim-bindings` .NET tool for CI.

## Install

Install the tool into `.tools`. Then point it at your built mod and the game's `Managed` directory:

```sh
dotnet tool install Valheim.Testing.Bindings.Tool --version 0.1.0-rc.1 --tool-path .tools
.tools/valheim-bindings MyMod/bin/Release/MyMod.dll --game-dir "path/to/valheim_Data/Managed"
```

The tool prints one report per mod. Each report's last line starts with `PASS:`, `FAIL:` or `INCOMPLETE:`. The tool then exits with one of the [exit codes](#exit-codes).

<!-- TODO(docs): capture output -->

| Report line starts with | Meaning |
|---|---|
| `Checked:` | A supplied assembly, its reference count and its file |
| `Not checked (not supplied):` | A referenced assembly you did not supply |
| `INCOMPLETE: the mod references` | A required assembly was not supplied |
| `MISSING` | A reference that does not bind. `instead:` gives a clue |
| `used by` | A mod method, field, type or attribute that uses it |
| `ACCESS info` or `ACCESS warning` | A member that binds but the mod cannot access |
| `Note:` | A part of the mod that could not be read completely |

## Options

```text
valheim-bindings <mod.dll>... (--game-dir <dir> | --game-file <file>)... [options]
```

Name one or more mod DLLs. Supply at least one `--game-dir` or `--game-file`.

| Option | Does |
|---|---|
| `--game-dir <dir>` | Looks up referenced assemblies here as `<name>.dll`. Repeatable. |
| `--game-file <file>` | Checks against this file, matched by the assembly name inside it. Repeatable. |
| `--only <name>` | Checks only these assemblies from `--game-dir` directories. Files are always checked. Repeatable. |
| `--require <name>` | Also requires this assembly if the mod references it. Repeatable. |
| `--fail-on-access` | Also fails on an access finding without `IgnoresAccessChecksTo` for its assembly. |
| `--max-users <n>` | Lists at most `n` users per finding. Default 10. |
| `-h`, `--help` | Prints usage and exits 0. |

The client's `valheim_Data/Managed` holds `assembly_valheim`, `assembly_utils`, the Unity modules and the framework the game runs on. Add `BepInEx/core` for BepInEx and Harmony. `assembly_valheim` is always required when the mod references it. If your path is wrong, the check therefore fails instead of checking nothing.

## Exit codes

| Exit code | Meaning |
|---|---|
| 0 | Every checked reference binds (access findings may be listed) |
| 1 | At least one missing reference; with `--fail-on-access`, also an access finding without `IgnoresAccessChecksTo` |
| 2 | Bad arguments, unreadable or malformed input, or a required assembly not supplied: the check is incomplete |

A mod that cannot be read gives exit 2 with the reason, never an unhandled crash. The tool still checks the other mods. With several mods, the tool exits with the highest code among them.

## What is checked

The check looks up references the way the runtime binds them:

| What is looked up | How it must match | Outcome |
|---|---|---|
| Type | Declared in the assembly, or forwarded to one that declares it | Missing |
| Nested type | Resolved through its declaring types | Missing |
| Field | Name and field type, on the type or a base type | Missing |
| Method | Name, static or instance, generic arity, return and parameter types | Missing |
| Property or event | Its accessor methods (`get_Level`) | Missing |
| Bound member or type | Not private, internal, or protected used outside a derived type | Access finding |
| Reference into an assembly you did not supply | Not looked up | Not checked, counted per assembly |

References come from method bodies, signatures, locals and catch clauses. They also come from base types, interfaces, generic constraints and explicit overrides. Attributes count, including `typeof(...)` arguments, as do the module's reference tables. Byref, arrays and their rank, pointers, generic instances and modifiers must match exactly.

Each missing reference appears once, with every mod member that uses it. When it helps, the finding also shows what the assembly has instead: other overloads, the field's new type, or a property that became a field. Suppose a lookup reaches a base type in an assembly you did not supply, such as a game type's `MonoBehaviour` base. The member is then still reported missing, with a note that it was not searched there.

## Publicized assemblies and access

Mods often compile against publicized game assemblies, where every member is public. Checked against the real assemblies, each private reference is an access finding. So access findings never fail the check by default. An access finding is **info** if the mod carries `[assembly: IgnoresAccessChecksTo("<assembly>")]` for that assembly, and a **warning** otherwise. `--fail-on-access` fails on warnings only.

If you check against the publicized copy you built with, the check still finds removals. It reports no access findings.

A mod compiled against a publicized assembly that reads a private game member at run time needs `AllowUnsafeBlocks=true` in its project. `[assembly: IgnoresAccessChecksTo("assembly_valheim")]` alone passes `--fail-on-access` but throws `FieldAccessException` at run time. `--fail-on-access` can also warn on a mod whose private access works:

| Build | `valheim-bindings --fail-on-access` | Run-time read of a private member |
|---|---|---|
| Ordinary | warning, exit 1 | `FieldAccessException` |
| `AllowUnsafeBlocks=true` | warning, exit 1 | Succeeds |
| `IgnoresAccessChecksTo` only | info, exit 0 | `FieldAccessException` |
| Both | info, exit 0 | Succeeds |

The check classifies metadata and does not test run-time permissions. Before you rely on private access, make a real call against the original game assembly.

## From a test

```csharp
var options = new BindingCheckOptions();
options.GameDirectories.Add(managedDirectory);
BindingReport report = BindingCheck.Check(modDll, options);
Assert.Empty(report.MissingRequired);
Assert.True(report.Binds, string.Join("\n", report.Missing.Select(f => $"{f.Member}: {string.Join(", ", f.UsedBy)}")));
```

To get the tool's exit code and report text instead, call `BindingCheckCommand.Run(string[] args, TextWriter output, TextWriter error)` with the same arguments the tool takes.

| `BindingReport` member | Holds |
|---|---|
| `Binds` | True when `Missing` is empty. Says nothing about `NotChecked` assemblies |
| `Missing` | References that would not bind at run time |
| `Access` | References that bind but are not accessible from the mod |
| `UndeclaredAccess` | Access findings without `IgnoresAccessChecksTo` for their assembly |
| `MissingRequired` | Required assemblies the mod references that were not supplied |
| `Checked`, `NotChecked` | Referenced assemblies, supplied or not, with reference counts |
| `Notes` | Parts of the mod that could not be read completely |
| `WriteText(output, maxUsers)` | Writes the report for a person or a CI log |

## In CI

Run the check in the job that builds the plugin, since both need the game's assemblies. Run it before anything launches the game. See [Run tests in CI](../guides/run-tests-in-ci.md) for the workflow step.

## Limits

- The check covers metadata references only. It misses name lookups at run time: Harmony's `[HarmonyPatch(typeof(T), "Method")]`, `AccessTools` (`nameof` compiles to a string), reflection, and Jötunn or prefab names.
- It misses type-shape changes that fail when a mod type loads, such as a new interface member, a sealed base or a new abstract member.
- It misses generic constraint changes.
- If an attribute argument names an enum from an assembly it cannot find, the check notes it and skips the types in that attribute's arguments.
- A pass means the checked references bind. It does not mean the mod behaves correctly with the new game version.

Mono.Cecil is MIT-licensed (Jb Evain, Novell); see THIRD-PARTY-NOTICES.md in the package.

Unofficial community tooling; not affiliated with or endorsed by Iron Gate or Coffee Stain. Valheim is a trademark of Iron Gate AB.
