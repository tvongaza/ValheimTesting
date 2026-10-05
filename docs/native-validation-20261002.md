# Native validation — 2 October 2026: publicized references at run time

*Assistant-written (Claude). Moved from `tools/game-references/README.md` (#278); the README keeps the conclusion.*

In a Windows dedicated-server check with Valheim
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
the same publicized compile reference and the same original server DLL.

The build flag's observed effect matches the [AssemblyPublicizer guidance](https://github.com/CabbageCrow/AssemblyPublicizer), but this is a result for this pinned game/Mono setup, not a promise for every runtime or private member. In particular, the assembly attribute marks *intent* for our offline checker; it did not grant access in this native run. The converse matters too: `--fail-on-access` rejected the working unsafe-enabled plugin because it lacked that declaration. Use the checker to detect missing or changed references and report access findings, then make a small native call to verify the access mode your mod ships with. Do not copy the publicized game DLL into a plugin release.

