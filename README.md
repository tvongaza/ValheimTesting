# ValheimTesting

Reusable test inputs, fixtures and assertions for Valheim mods. Most tests run without Valheim, Unity, Steam or a dedicated server. Optional system tests use the real game through ValheimCLI.

> Unofficial community tooling; not affiliated with or endorsed by Iron Gate or Coffee Stain. Valheim is a trademark of Iron Gate AB.

Bringing your own mod? Start with [the adoption guide](docs/adopting.md) and [a complete first mod test](examples/ModWithTests/README.md). Use [package setup](docs/getting-started.md) and the [example index](examples/README.md) for the next layer. To investigate a load failure or mod conflict, use the [debugging guide](docs/debugging-mods.md). Existing mod tests stay in their own repository; shared helpers and lifecycle code are adopted gradually. Before a native run, read the [runtime hygiene and evidence checklist](docs/runtime-hygiene.md).

Browse the [candidate API reference](https://tvongaza.github.io/ValheimTesting/) on GitHub Pages. It is generated from `main` and remains an inventory for the [stable-contract review](docs/reference/compatibility.md), not yet a 1.0 API promise.

New to Valheim plugins? The community wiki's [BepInEx and Harmony overview](https://github.com/Valheim-Modding/Wiki/wiki/Best-Practices) explains the modding concepts used by these examples. Its older code samples are background, not a substitute for this toolkit's pinned game-version checks; see [wiki version caveats](docs/runtime-hygiene.md#wiki-pages-that-predate-10).

## Contribute

Mod developers and coding agents are welcome to help extend the synthetic world, capture/replay, observations and lifecycle tools. Read [CONTRIBUTING.md](CONTRIBUTING.md) for repository ownership, a worked fixture example, local checks and PR expectations. Useful local contributions can be reviewed before native testing; mark that evidence as not run.

## AI agent entry point

Start with [AGENTS.md](AGENTS.md) and the [agent workflow](docs/agent-guide.md). A [CLAUDE.md](CLAUDE.md) pointer keeps Claude-based assistants on the same instructions. The guide covers tool discovery, strict pins, one-shot mutations, result interpretation, test-layer limits and restoration/handoff.

## Packages and ownership

| Package | Purpose | Runtime |
|---|---|---|
| `Valheim.Testing` | Composable terrain, multi-zone height/paint fixtures, exact recorded-input replay, grid dumps, terrain rendering and parity checks; no ValheimCLI dependency | netstandard2.0 |
| `Valheim.Testing.Game` | Typed observations, fixtures, owned server sessions, comparisons and JSON/JUnit reports | net10.0 |
| `Valheim.Testing.Cli` | ValheimCLI's client transport and YAML runner, packaged unchanged from pinned ValheimCLI source (MIT, warp) | net10.0 |
| `Valheim.Testing.Doubles` | Source-only doubles of the Unity, Valheim, BepInEx and Jotunn types a mod's pure-logic sources use, compiled into your test project; every type is partial | source (C# 10) |
| `Valheim.Testing.Bindings` | Offline check that a built mod's references into the game assemblies still bind (Mono.Cecil); missing members fail, access changes are reported separately | netstandard2.0 |
| `Valheim.Testing.Bindings.Tool` | The same check as the `valheim-bindings` .NET tool, for a mod's CI before any native run | net10.0 |
| `Valheim.Testing.NativeSmoke` | `valheim-test`: one-command disposable mod-load checks and an editable NuGet-only consumer project | .NET 10 tool |

The dependency goes **ValheimTesting → ValheimCLI**, never the reverse. Game-side extension API and observers stay in ValheimCLI. Roads and MWL own their optional adapters and scenarios. No test package belongs in an ordinary player's plugin folder.

Until upstream merges the required transport/API changes, `cli-dependency.json` pins our ValheimCLI fork by full commit and exact package version. The packages, including the [native-smoke tool](https://www.nuget.org/packages/Valheim.Testing.NativeSmoke), are published on NuGet.org as releases become available. ValheimTesting publishes the transport as `Valheim.Testing.Cli` so it is clearly this toolkit's packaging, not an official ValheimCLI release; upstream's own project is `Valheim.Cli.Testing`. Bootstrap builds it from tracked source at the pinned commit into an ignored local feed; it does not compile or launch the game plugin.

## Validate or develop the framework (no game)

Consumers of published packages can restore directly from NuGet.org; see [the adoption guide](docs/adopting.md). The commands below validate this entire repository.

Requires only the .NET 10 SDK and Git; the bootstrap and validation scripts are .NET file-based C# programs. The pure library targets netstandard2.0; the game library and examples target net10.0 and allow a newer runtime. The ValheimCLI transport package is built for net10.0 from the pinned ValheimCLI source, so .NET 10 is the only modern runtime needed.

```sh
dotnet run scripts/bootstrap-cli.cs
# Or use a local CLI clone; the script exports only the pinned commit:
# dotnet run scripts/bootstrap-cli.cs -- --source /path/to/valheimCLI
dotnet run scripts/validate.cs
dotnet run --project examples/NoGameTerrain -c Release
```

`validate.cs` runs the local library tests, builds all external examples and packs the libraries to `.packages`. It never starts Valheim. The ValheimCLI transport and its tests remain upstream-owned, not copied here. Choose the packages in [getting started](docs/getting-started.md#package-versions-and-feeds): its exact versions are known-good example pins, while the linked NuGet pages show current releases. Pin the versions your mod tests actually use; a newly published package does not silently change a passing test. Use the local feed only to try a build that is not yet published.

The same commands work in any shell on Windows, macOS and Linux; there is no per-shell launcher. A sandbox that blocks the NuGet caches or the SDK's file-based app directory needs the sandbox recipe in [AGENTS.md](AGENTS.md).

## Platforms

| Host | Toolkit and tests | Game client (`ClientLaunch`, driven by ValheimCLI) | Dedicated server (native) | Server in a container | Remote server host |
|---|---|---|---|---|---|
| Windows | Yes; CI | Yes, `valheim.exe` | Yes, `valheim_server.exe` | Linux image; not tested on Windows | SSH game host and environment profile; a client starts in its desktop session, while a headless dedicated server starts from a disposable runtime copy without Steam or a desktop session |
| Linux | Yes; CI | Yes, `valheim.x86_64`; needs a display ([docker/linux-client](docker/linux-client/README.md) on an NVIDIA GPU host) | Yes, `valheim_server.x86_64` | Yes, [docker/linux-server](docker/linux-server/README.md); verified | SSH game host and environment profile (preview 13); natively, a server-only run on a Linux VM over SSH |
| macOS | Yes; CI | Yes, `Valheim.app`, modded either way: x86_64 under Rosetta (the default, BepInExPack_Valheim as installed) or native arm64 with `"architecture": "arm64"`, a Doorstop with an arm64 slice and a BepInEx core on MonoMod 25 ([native client](docs/getting-started.md#native-apple-silicon-client); the owned native launch passed a join, save and rejoin check on an Apple M2 with Valheim 1.0.16) | Yes (Game preview 15), `valheim_server/Valheim` from Steam app 896660's macOS build, native arm64; modded needs a Doorstop with an arm64 slice and a natively running BepInEx core ([getting started](docs/getting-started.md)) | Experimental: x86-64 emulation on Apple Silicon | Recommended: SSH game host and environment profile (preview 13); natively, a server-only run on a Linux VM over SSH from macOS |

The toolkit needs only the .NET 10 SDK on every host, and CI runs bootstrap and validation on all three. `ClientLaunch` builds a BepInEx game-client launch (Doorstop loader checks and variables, `-console`, `SteamAppId`) from a client install on its own OS, and `ServerLaunch` does the same for a dedicated server; each refuses the other's install and names the right one. `ClientLaunch` only builds the launch: a client needs an interactive desktop session with a display and a running Steam client, so `InteractiveClient` starts it inside a Windows or Linux host's existing desktop session (see [the toolkit notes](docs/testing-toolkit.md#clients-in-a-hosts-desktop-session)). On a Mac, an owned game client runs under Rosetta by default or natively on Apple Silicon when its plan asks for `arm64` ([native client](docs/getting-started.md#native-apple-silicon-client)), and the native macOS dedicated server can run locally with the BepInEx setup in [getting started](docs/getting-started.md); `ServerLaunch` still refuses a `Valheim.app` client passed as a server runtime and a Windows or Linux server on macOS. Remote macOS server hosts are not supported yet.

## Linux and containers

Owned dedicated-server checks can run on Linux as well as Windows. `ServerLaunch` detects a copied server runtime's platform from its executable (`valheim_server.exe` or `valheim_server.x86_64`) and builds the direct launch that `DirectServerProcess` and `OwnedServerSession` own, including BepInEx's Doorstop variables on Linux. It is new in Game preview.11. [docker/linux-server](docker/linux-server/README.md) builds a local x86-64 image with the server, BepInEx and .NET 10, and the manual [Linux workflow](.github/workflows/native-linux.yml) boots it once with [LinuxServerSmoke](examples/LinuxServerSmoke/README.md). The [scheduled server checks](docker/linux-server/README.md#scheduled-checks-in-ci) run that smoke and the [FullLifecycle](examples/FullLifecycle/README.md#the-server-half-alone) example's server half every night and after each new public server build, and keep one tracking issue open while they fail. The image and smoke were verified on a Linux x86-64 Docker host on 28 September 2026 (server build 25527701); on Apple Silicon the image runs only under emulation and remains experimental.

Linux game clients can run on a remote NVIDIA GPU host using the [client container](docker/linux-client/README.md), which supplies a display and Steam setup but no game files. Game preview.13 adds an [environment profile and game hosts](docs/testing-toolkit.md#game-hosts-and-environment-profiles-preview-13) (local, SSH, container): run scripts, take a host lock, ship a revision, follow a log, fetch evidence and reach a loopback-only ValheimCLI through an SSH tunnel. `InteractiveClient` starts a client in a host's desktop session and `SteamAccountPool` leases Steam accounts to runs by name. `PinnedServerRun --profile` runs a plan's dedicated server on a Linux host over SSH or in a container, from any OS ([a server-only plan on a remote host](docs/testing-toolkit.md#a-server-only-plan-on-a-remote-host)). A server-only run from macOS over SSH to a Linux VM has passed natively, and a Windows client was started in its desktop session through a profile client over SSH during a crossplay run; provisioning the host and signing in its Steam client stays with the operator.

Server images contain Valheim's game files. Build them locally or in the CI job that uses them; never push, upload or publish an image, layer or server directory.

## Test pyramid

1. **Unit/synthetic:** real mod decisions against small explicit inputs. Broad and fast.
2. **Integration:** transport, adapters, fixture handling and orchestration with controlled doubles.
3. **Bounded game checks:** a few zones on a disposable server and ValheimCLI-only client; save/restart and replication when those boundaries change.
4. **Human judgement:** short visual/walking checks when usability matters.

Synthetic inputs are not Valheim's generator. Replaying captured inputs is not independent proof of the game's physics. Incomplete observations fail explicitly. Session ownership does not own Steam accounts, machine reservations or another operator's running game.

## Evidence and limits

Local validation covers synthetic inputs, observation contracts and lifecycle failure handling. Roads supplies the first native scenarios: a 100-sample declared terrain/collider calibration, followed by a persistent two-zone native fixture. On Valheim 1.0.16, a ValheimCLI-only client matched 15 height/collider samples and three stationary grounded observations before and after confirmed server save/restart/rejoin. The later paint arm matched all 16 paved-core/verge RGBA samples across the same lifecycle. An unchanged-paint negative expectation failed exactly the eight painted samples and passed the untouched eight.

The paint fixture starts from explicit saved RGBA, preserves alpha and samples both sides of a zone seam. The [follow-up campaign](docs/native-validation-20260927.md) adds bounded dirt/fading-edge coverage. These fixtures do not establish arbitrary terrain, rendered appearance or human walking usability. Mod-specific acceptance status belongs in the mod's scenario report. See [the detailed guide](docs/testing-toolkit.md), [source provenance](PROVENANCE.md), and examples.

Preview APIs may change. No release or upstream merge is implied by this repository.

## Developer-loop scripts

[`tools/dev-loop`](tools/dev-loop/README.md) holds `dev-loop.cs`, one .NET file-based script for a mod's edit-build-test loop (build, install into a game copy you own, launch, run a strict plan, summarise the log), and a strict smoke plan to start from. It drives a `valheim-cli` executable taken from a ValheimCLI release or build; see [Developer loop](docs/getting-started.md#developer-loop).

Two more tools are for a mod's build: [`tools/test-runners`](tools/test-runners/README.md) runs a test project on `net10.0` and on `net48` (Mono on macOS and Linux, .NET Framework on Windows) and fails if either fails, and [`tools/game-references`](tools/game-references/README.md) gives the mod's game-side projects their Valheim, Unity and BepInEx references from one `ValheimPath`, with an error naming whatever is missing.

Follow-up examples: [PaintCheck](examples/PaintCheck/README.md) compares loaded raw paint channels; [WalkingReview](examples/WalkingReview/README.md) records a human-driven traversal without moving the character.
Both build locally; the bounded paved paint/reload check passed, while human walking acceptance remains pending.

## License

MIT, copyright © 2026 Tys von Gaza. See [LICENSE](LICENSE). Attribution for imported and adapted code is retained in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); both files are included in the library packages.

Shared-world fixtures: [guide and mod consumers](docs/shared-world.md), [runnable example](examples/SharedWorld/README.md). Height and paint grids are independent; snapshots are in-memory test state, not native saves.

The [27 September native follow-up](docs/native-validation-20260927.md) covers strict reloads, terrain capture/replay, session controls, controlled mutation draining and dirt/fade paint persistence. It records the remaining limits explicitly.
