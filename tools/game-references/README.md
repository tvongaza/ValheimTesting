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
# or once per shell:
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

### Publicized references at run time

The `File` override above changes what the compiler sees. It does **not** make
the member public in Valheim's original assembly, and `Private=false` keeps the
publicized copy out of the mod's output. Compile against a publicized copy made
from the same game build you will run; install only your mod and its normal
dependencies beside the original game assemblies.

If your mod directly calls a member that is private in the original assembly,
verify that access in the game. In a Windows dedicated-server check with Valheim
build 25527701, BepInExPack 5.4.2202 and Unity 6000.0.75.2503836, a net48
test plugin compiled against a publicized `assembly_valheim.dll` read the
original private `Game.m_timeScale` field as follows:

| Plugin build | `valheim-bindings --fail-on-access` against original assembly | Native read |
|---|---|---|
| Ordinary build | Access warning; exit 1 | `FieldAccessException` |
| `AllowUnsafeBlocks=true` | Access warning; exit 1 | Succeeded, even without an unsafe expression in the source |
| `[assembly: IgnoresAccessChecksTo("assembly_valheim")]` only | Declared-access info; exit 0 | `FieldAccessException` |
| Both settings | Declared-access info; exit 0 | Succeeded |

The successful flag-only variant contained no unsafe expression; its test
method simply returned `Game.m_timeScale`. The failed and passing builds used
the same publicized compile reference and the same original server DLL. To
apply that build setting to a mod project:

```xml
<PropertyGroup>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>
```

The build flag's observed effect matches the [AssemblyPublicizer guidance](https://github.com/CabbageCrow/AssemblyPublicizer), but this is a result for this pinned game/Mono setup, not a promise for every runtime or private member. In particular, the assembly attribute marks *intent* for our offline checker; it did not grant access in this native run. The converse matters too: `--fail-on-access` rejected the working unsafe-enabled plugin because it lacked that declaration. Use the checker to detect missing or changed references and report access findings, then make a small native call to verify the access mode your mod ships with. Do not copy the publicized game DLL into a plugin release.

For example, after building the plugin against the publicized reference, check
it against the **original** assembly and exercise the method in a disposable
game session:

```sh
valheim-bindings MyMod.dll \
  --game-dir /path/to/original/valheim_server_Data/Managed \
  --game-dir /path/to/original/BepInEx/core \
  --fail-on-access
# A finding here needs review; either exit code alone is not proof of runtime access.
```

See [the binding check on access](../../docs/packages/Valheim.Testing.Bindings.md#publicized-assemblies-and-access) for the distinction between binding, declared access and actual execution.

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
