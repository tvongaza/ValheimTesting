# Valheim.Testing.Bindings

An offline check that a built mod's references into the game still bind, from [ValheimTesting](https://github.com/tvongaza/ValheimTesting). A game update that removes, renames or retypes a member breaks a mod with `MissingFieldException`, `MissingMethodException` or `TypeLoadException`, but only when the runtime first compiles a method that uses it, which can be hours into play. This check reads the mod DLL with [Mono.Cecil](https://github.com/jbevain/cecil), resolves every type, field and method it references in the game assemblies you supply, and names the mod methods that use each one that no longer binds. It loads and runs nothing, and needs no game process.

- `Valheim.Testing.Bindings` (netstandard2.0) is the library: `BindingCheck.Check(modPath, options)` returns a `BindingReport`.
- `Valheim.Testing.Bindings.Tool` (net10.0) is the `valheim-bindings` .NET tool for CI.

```sh
dotnet tool install Valheim.Testing.Bindings.Tool --version 0.1.0-preview.1 --tool-path .tools
.tools/valheim-bindings MyMod/bin/Release/MyMod.dll --game-dir "path/to/valheim_Data/Managed"
```

Exit code 0 means every checked reference binds, 1 that some are missing, and 2 bad arguments, an unreadable file or `assembly_valheim` not supplied. Members that exist but are not accessible from the mod (a mod built against publicized assemblies references them on purpose) are reported separately and fail only with `--fail-on-access`, and then only without an `IgnoresAccessChecksTo` attribute for their assembly. References into assemblies you did not supply are counted as not checked.

See [the offline binding check](https://github.com/tvongaza/ValheimTesting/blob/main/docs/testing-toolkit.md#offline-binding-check-valheimtestingbindings-preview-1) for what it covers, a CI step and its limits. Mono.Cecil is MIT-licensed (Jb Evain, Novell); see THIRD-PARTY-NOTICES.md in the package.

Unofficial community tooling; not affiliated with or endorsed by Iron Gate or Coffee Stain. Valheim is a trademark of Iron Gate AB.
