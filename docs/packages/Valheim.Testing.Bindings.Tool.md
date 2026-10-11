# Valheim.Testing.Bindings.Tool

[Current preview API reference](https://tvongaza.github.io/ValheimTesting/). This command-line tool's supported entry points are the commands documented below, rather than a public .NET library surface.

`Valheim.Testing.Bindings.Tool` is the `valheim-bindings` .NET command-line tool (net10.0). It checks a built mod's references to the game assemblies without launching Valheim, and is intended for CI before the smaller set of native tests. Its library counterpart, [`Valheim.Testing.Bindings`](Valheim.Testing.Bindings.md), exposes `BindingCheck.Check` and the report types for a custom test runner.

Install and run the tool with the game assemblies that match the mod build:

```sh
dotnet tool install Valheim.Testing.Bindings.Tool --version 0.1.0-preview.3 --tool-path .tools
.tools/valheim-bindings path/to/MyMod.dll --game-dir path/to/valheim_Data/Managed
```

See [the binding-check guide](Valheim.Testing.Bindings.md) for options, exit codes, access findings, and its limits. This command checks metadata compatibility; it does not prove the game executes a private-member access or that a mod loads.
