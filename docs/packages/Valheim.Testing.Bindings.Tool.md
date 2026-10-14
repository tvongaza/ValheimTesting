# Valheim.Testing.Bindings.Tool

[Current preview API reference](https://tvongaza.github.io/ValheimTesting/reference/). This tool's supported entry points are the commands below, not a public .NET library surface.

`Valheim.Testing.Bindings.Tool` is the `valheim-bindings` .NET command-line tool (net10.0). It checks a built mod's references into the game assemblies without launching Valheim. Run it in CI before your native tests. If you write a custom test runner, use the library [`Valheim.Testing.Bindings`](Valheim.Testing.Bindings.md) and its `BindingCheck.Check` instead.

## Install and run

Point `--game-dir` at the game assemblies that match your mod build:

```sh
dotnet tool install Valheim.Testing.Bindings.Tool --version 0.1.0-rc.1 --tool-path .tools
.tools/valheim-bindings path/to/MyMod.dll --game-dir path/to/valheim_Data/Managed
```

The first command installs the tool into `.tools`. The second names each reference that no longer binds and the mod methods that use it.

[`Valheim.Testing.Bindings`](Valheim.Testing.Bindings.md) lists the options, exit codes, access findings and limits.

## Limits

The tool checks metadata compatibility. It does not prove that the game executes a private-member access, or that a mod loads.
