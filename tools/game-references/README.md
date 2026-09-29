# Game references for mod projects

`Valheim.GameReferences.props` and `Valheim.GameReferences.targets` let a game-side project (the mod itself, or its test adapter) reference a local Valheim install's managed assemblies and BepInEx with one property, and stop the build with an error that names what is missing. They are MSBuild source files for your repository, not a package.

Unit-test projects do not use them. A test project that links your sources against `Valheim.Testing.Doubles` builds without the game, on any machine and on hosted CI; see [a layout that builds on hosted CI](../../docs/adopting.md#a-layout-that-builds-on-hosted-ci). The targets refuse a project that references both the game and the doubles, whose types have the same names.

## Use them

Copy both files into your repository, for example into `build/`, and import them in the mod's project file: the props at the top, the targets at the bottom.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <Import Project="../build/Valheim.GameReferences.props" />
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <AssemblyName>MyMod</AssemblyName>
  </PropertyGroup>
  <Import Project="../build/Valheim.GameReferences.targets" />
</Project>
```

Several game-side projects can import them once from `Directory.Build.props` and `Directory.Build.targets` instead. Then build with the game folder:

```sh
dotnet build MyMod/MyMod.csproj -p:ValheimPath="/path/to/Valheim"
# or once per shell, the variable tools/dev-loop uses too:
export VALHEIM_PATH="/path/to/Valheim"
```

Without either, the Steam folder of the operating system is tried: `C:\Program Files (x86)\Steam\steamapps\common\Valheim` on Windows, `~/Library/Application Support/Steam/steamapps/common/Valheim` on macOS, `~/.steam/steam/steamapps/common/Valheim` on Linux (the link Steam keeps to its install, whichever way it was installed). [FullLifecycle](../../examples/FullLifecycle/README.md)'s mod and adapter import them from this folder.

| Property | Default | Meaning |
|---|---|---|
| `ValheimPath` | `VALHEIM_PATH`, else the Steam folder | The game folder. Usually the only one you set. |
| `ValheimManaged` | the first of `valheim_Data/Managed`, `Valheim.app/Contents/Resources/Data/Managed`, `valheim_server_Data/Managed` under `ValheimPath` | The folder holding `assembly_valheim.dll`: a client on Windows or Linux, on macOS, or a dedicated server |
| `BepInExCore` | `ValheimPath/BepInEx/core` | The folder holding `BepInEx.dll` |
| `UseValheimCli` | not set | `true` in a project that compiles against ValheimCLI, such as a test adapter |
| `CliDll` | none | With `UseValheimCli`: the ValheimCLI core (`valheimCLI.dll`) installed in the test runtime. There is deliberately no default, so an adapter compiles against the exact core it runs with |

Properties can be set anywhere in the project, on the command line or in the environment: the targets file reads them after the project.

## Which assemblies

By default `assembly_valheim`, `assembly_utils`, `UnityEngine` and `UnityEngine.CoreModule` from the Managed folder, and `BepInEx` and `0Harmony` from BepInEx/core, all with `Private=false`, so no game assembly is copied into your build output. Change the list in your project, after the props import:

```xml
<ItemGroup>
  <ValheimReference Include="UnityEngine.PhysicsModule" />   <!-- another DLL from the Managed folder -->
  <ValheimReference Remove="assembly_utils" />               <!-- one you do not use -->
  <!-- a different file for a reference, relative to the Managed folder (BepInEx/core for BepInExReference) -->
  <ValheimReference Update="assembly_valheim" File="publicized_assemblies/assembly_valheim_publicized.dll" />
</ItemGroup>
```

A DLL outside those two folders (Jötunn in `BepInEx/plugins`, say) is an ordinary `<Reference>` with a `HintPath`, or its NuGet package.

## When something is missing

Before assembly references are resolved, every listed file is checked. The build stops with one error that says what was looked for, where, and which property to set:

```text
error : Valheim is not installed at '/home/me/.steam/steam/steamapps/common/Valheim' (the default Steam folder; neither ValheimPath nor VALHEIM_PATH is set). Set ValheimPath to the game folder, ...
error : Game assemblies not found in '<game>/valheim_Data/Managed' (from ValheimPath): assembly_utils (<game>/valheim_Data/Managed/assembly_utils.dll). ...
error : BepInEx assemblies not found in '<game>/BepInEx/core' (from ValheimPath): BepInEx (...), 0Harmony (...). Install BepInEx in the game folder, or set BepInExCore ...
error : MyMod.TestAdapter.csproj sets UseValheimCli: pass -p:CliDll=<path> with the ValheimCLI core (valheimCLI.dll) installed in the test runtime. ...
```

On a hosted CI runner, a project that imports these files therefore fails at once with the first error; build only your test projects there.

## Why source files and not a package

- **The production mod imports them.** A package would make the mod's own build depend on a testing-toolkit package; the toolkit's rule is that production mods gain no testing dependencies. Two small files in your repository are yours.
- **They work before restore.** A package's build files are imported only after a restore, so a fresh checkout's first evaluation (and an IDE's) would see no references. An `Import` of a file in the repository applies at once.
- **Nothing to publish or pin.** Each file carries a version line (`version 1`); compare it with this folder's to see whether your copy is behind. Keep your changes in your project (properties and the item lists above) rather than in the copies, so updating is a plain copy.

## Tests

`GameReferencesBuildTests` and `GameReferencesErrorTests` in `tests/Valheim.Testing.Tests` build small net48 projects that import these files against a fake game folder of generated stand-in assemblies (no game files): a Windows/Linux and a macOS layout, `VALHEIM_PATH`, the default Steam folder under `HOME` (macOS and Linux), added and removed references, a reference to another file (`File`), and `UseValheimCli`, each asserting that no game assembly is copied. Each missing piece (no game folder, nothing in the default Steam folder, a folder that is not the game, one missing assembly, no BepInEx, `UseValheimCli` without `CliDll` or with a wrong path, the props without the targets, game references together with the doubles) must fail with its error. `dotnet run scripts/validate.cs` runs them on every OS.
