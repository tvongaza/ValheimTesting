# Game references for mod projects

Reference a local Valheim install's game assemblies and BepInEx from a mod project with one property.

`Valheim.GameReferences.props` and `Valheim.GameReferences.targets` are MSBuild source files that you copy into your repository. A game-side project imports them: either the mod or its test adapter. If a file is missing, the build stops with one error that names it. The props also pin `Microsoft.NETFramework.ReferenceAssemblies` as a build-only dependency. The .NET SDK can then compile `net48` plugins on macOS and Linux without Mono or a .NET Framework targeting pack.

Unit-test projects do not use these files. A test project that links your sources against `Valheim.Testing.Doubles` builds without the game. See [Test mod logic that uses game types](../../docs/guides/test-logic-that-uses-game-types.md). The targets refuse a project that references both the game and the doubles, because their types have the same names.

## Use them

1. Copy both files into your repository, for example into `build/`.
2. Import the props at the top of the mod's project file and the targets at the bottom:

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

   If you have several game-side projects, import the files once from `Directory.Build.props` and `Directory.Build.targets` instead.

3. Build with the game folder:

   ```sh
   dotnet build MyMod/MyMod.csproj -p:ValheimPath="/path/to/Valheim"
   # or once per shell:
   export VALHEIM_PATH="/path/to/Valheim"
   ```

   The mod compiles against the game's assemblies. If something is missing, the build stops with one error. See [When something is missing](#when-something-is-missing).

If you set neither `ValheimPath` nor `VALHEIM_PATH`, the build tries the Steam folder for your OS:

| OS | Default game folder |
|---|---|
| Windows | `C:\Program Files (x86)\Steam\steamapps\common\Valheim` |
| macOS | `~/Library/Application Support/Steam/steamapps/common/Valheim` |
| Linux | `~/.steam/steam/steamapps/common/Valheim`, the link Steam keeps to its install |

The mod and adapter in [FullLifecycle](../../examples/FullLifecycle/README.md) import these files from this folder.

Keep your changes in your project, not in the copied files. Updating is then a plain copy. Each file carries a version line (`version 1`). Compare it with this folder's copy to see whether yours is behind.

## Properties

You can set these properties in the project, on the command line or in the environment. The targets file reads them after the project.

| Property | Default | Meaning |
|---|---|---|
| `ValheimPath` | `VALHEIM_PATH`, else the Steam folder | The game folder. Usually the only one you set. |
| `ValheimManaged` | The first folder below that exists under `ValheimPath` | The folder holding `assembly_valheim.dll` |
| `BepInExCore` | `ValheimPath/BepInEx/core` | The folder holding `BepInEx.dll` |
| `UseValheimCli` | not set | `true` in a project that compiles against ValheimCLI, such as a test adapter |
| `CliDll` | none | With `UseValheimCli`: the `valheimCLI.dll` installed in the test runtime |

The targets search for `ValheimManaged` in this order:

- `valheim_Data/Managed`: a client on Windows or Linux
- `Valheim.app/Contents/Resources/Data/Managed`: a client on macOS
- `valheim_server_Data/Managed` or `valheim_server/Data/Managed`: a dedicated server

`CliDll` has no default on purpose. An adapter then compiles against the exact ValheimCLI core it runs with.

## Which assemblies

By default, the targets reference these assemblies:

- From the Managed folder: `assembly_valheim`, `assembly_utils`, `UnityEngine` and `UnityEngine.CoreModule`.
- From `BepInEx/core`: `BepInEx` and `0Harmony`.

Every reference uses `Private=false`, so no game assembly is copied into your build output. To change the list, add items after the props import:

```xml
<ItemGroup>
  <ValheimReference Include="UnityEngine.PhysicsModule" />   <!-- another DLL from the Managed folder -->
  <ValheimReference Remove="assembly_utils" />               <!-- one you do not use -->
  <!-- a different file for a reference, relative to the Managed folder (BepInEx/core for BepInExReference) -->
  <ValheimReference Update="assembly_valheim" File="publicized_assemblies/assembly_valheim_publicized.dll" />
</ItemGroup>
```

If you need a DLL outside those two folders, such as Jötunn in `BepInEx/plugins`, use an ordinary `<Reference>` with a `HintPath`. You can also use its NuGet package.

### Publicized references at run time

A publicized reference changes only what the compiler sees. It does not make the member public in Valheim's original assembly. `Private=false` keeps the publicized copy out of your output. Compile against a publicized copy of the same game build you run, and do not ship the publicized DLL.

If your mod reads a private game member at run time, add `AllowUnsafeBlocks=true` to its project:

```xml
<PropertyGroup>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>
```

`[assembly: IgnoresAccessChecksTo("assembly_valheim")]` alone is not enough. It passes `valheim-bindings --fail-on-access`, but the game throws `FieldAccessException`.

After you build against the publicized reference, check the plugin against the **original** assemblies:

```sh
valheim-bindings MyMod.dll \
  --game-dir /path/to/original/valheim_server_Data/Managed \
  --game-dir /path/to/original/BepInEx/core \
  --fail-on-access
# A finding here needs review; either exit code alone is not proof of runtime access.
```

The tool lists the references that do not bind and the access findings. Review each finding. Then call the method in a disposable game session. [The binding check on access](../../docs/packages/Valheim.Testing.Bindings.md#publicized-assemblies-and-access) explains how binding, declared access and actual execution differ.

## When something is missing

Before assembly references are resolved, the targets check every listed file. If one is missing, the build stops with one error. The error says what the targets looked for, where, and which property to set:

```text
error : Valheim is not installed at '/home/me/.steam/steam/steamapps/common/Valheim' (the default Steam folder; neither ValheimPath nor VALHEIM_PATH is set). Set ValheimPath to the game folder, ...
error : Game assemblies not found in '<game>/valheim_Data/Managed' (from ValheimPath): assembly_utils (<game>/valheim_Data/Managed/assembly_utils.dll). ...
error : BepInEx assemblies not found in '<game>/BepInEx/core' (from ValheimPath): BepInEx (...), 0Harmony (...). Install BepInEx in the game folder, or set BepInExCore ...
error : MyMod.TestAdapter.csproj sets UseValheimCli: pass -p:CliDll=<path> with the ValheimCLI core (valheimCLI.dll) installed in the test runtime. ...
```

The build also stops in two other cases:

- A project imports the props without the targets.
- A project references both the game and `Valheim.Testing.Doubles`.

## Why source files and not a package

Your production mod imports these files, and the toolkit's rule is that production mods gain no testing dependencies. An `Import` of a repository file also applies before restore, so a fresh checkout and your IDE see the references at once.

## Limits

- The files need a local Valheim install. On a hosted CI runner, a project that imports them fails with the first error, so build only your test projects there. See [Run your mod's tests in GitHub Actions](../../docs/guides/run-tests-in-ci.md).
- The private-access behaviour above was observed on one pinned game and Mono setup. Verify each private member your mod uses in the game.
