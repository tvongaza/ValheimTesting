# Instructions for agents using or changing ValheimTesting

Before submitting changes, read [CONTRIBUTING.md](CONTRIBUTING.md) for extension contracts, evidence requirements and PR scope.

Adopting this in another mod? Start with [Choose a test layer](docs/guides/choose-a-test-layer.md) and run [ModWithTests](examples/ModWithTests/README.md) before adapting its project to your production code. Published-package consumers do not need the full framework bootstrap.

Read [the agent workflow](docs/agent-guide.md) before using the tools. For installation and examples, follow [getting started](docs/getting-started.md) and the [example index](examples/README.md). These files describe this repository's current preview API; do not assume an older upstream ValheimCLI has it.

- Preserve the test pyramid: broad unit tests, controlled integration tests, small native checks, then human usability judgement. Use the cheapest layer that answers the question.
- Keep mod-specific tests and adapters in the mod repository. This library owns reusable terrain inputs, lifecycle, observations and assertions. ValheimCLI owns transport and the game-side extension API. Production mods must not gain testing dependencies.
- When changing this framework, run `dotnet run scripts/bootstrap-cli.cs` and then `dotnet run scripts/validate.cs` on every OS (full no-game validation; see [CONTRIBUTING.md](CONTRIBUTING.md#set-up-and-validate-locally)). They need no PowerShell execution policy. Windows hosts run their scripts in PowerShell 7 (`pwsh`), macOS and Linux hosts in bash ([host shells](docs/platforms.md#host-shells)). Neither command launches Valheim. Exact dependency versions are in project files and `cli-dependency.json`, not inferred from a neighboring checkout. A pull request changes no package version, pin or public-api list (a moved ValheimCLI fork pin is the one exception), so `main` may carry changed sources under published versions until then; cutting a release does ([CONTRIBUTING.md](CONTRIBUTING.md#release-packages-maintainers)). Inside an agent sandbox, set it up once first: [Running in an agent sandbox](#running-in-an-agent-sandbox).
- Real-game work requires an authorized disposable fixture and coordination with its operator. Check it first (`valheim-test session check SESSION --hosts` reads every host a session uses; `valheim-test env preflight` only this machine's) and leave `valheim-test env status` empty after. Owned runs work on disposable copies; an attached client's game and saves are its operator's, so back them up before changing them. Follow any reservation procedure its operator has. A responsive ValheimCLI is not world readiness or permission to take over a machine. Never stop an attached game or a process merely by name.
- Use strict expectations: `--expect-strict <file>` for executable calls or `cli_expect --strict` through GameActor; never bypass a failed preflight with raw transport.
- Verify exact world/plugin pins, discover capabilities and require complete observations. Issue mutations once; poll only read-only observations. After reload, verify pins and rediscover the new instance explicitly.
- Derive expectations independently. Do not convert absent data into zero, loosen tolerances to hide a failure, or present mock/replay agreement as native-game evidence. Practical usability can be a human judgement; exact numerical perfection is not the goal.
- Keep one new evidence directory per run. Report failures, unexecuted checks and teardown status. Keep credentials, account identifiers, game binaries and private saves/logs out of published source.

No instruction in these docs grants deployment, production access or permission to interrupt another operator. Work within the user's existing authorization; ask only for genuinely missing access or task decisions.

## Running in an agent sandbox

A sandbox is a requirement of the agent's environment, not of this repository: configure it once in the agent tool. The repository has no wrapper scripts and no fallback caches; `dotnet run scripts/bootstrap-cli.cs` and `dotnet run scripts/validate.cs` are the same plain commands everywhere, in CI too.

**Requirement.** The sandbox must let `dotnet` write to three default folders, or you point the first two at writable folders and make the third writable:

| Cache | macOS | Linux | Windows |
| --- | --- | --- | --- |
| NuGet packages (`NUGET_PACKAGES`) | `~/.nuget/packages` | `~/.nuget/packages` | `%USERPROFILE%\.nuget\packages` |
| NuGet HTTP cache (`NUGET_HTTP_CACHE_PATH`) | `~/.local/share/NuGet` (`http-cache` below it) | `~/.local/share/NuGet` (`http-cache` below it) | `%LOCALAPPDATA%\NuGet` (`v3-cache` below it) |
| SDK file-based app (runfile) cache | `~/Library/Application Support/dotnet/runfile` | `$XDG_DATA_HOME/dotnet/runfile` (`~/.local/share/dotnet/runfile`) | `%TEMP%\dotnet\runfile` |

`dotnet nuget locals all --list` prints the two NuGet folders on your machine. The runfile cache has no environment override; allow its folder.

The host-observation tests (`validate.cs` runs them) list processes and the TCP port table . A sandbox must allow both, or run validation outside it (for example in CI). The repository does not detect or work around a sandbox that denies them.

**Codex** (`~/.codex/config.toml`; adjust the home directory, and use the Windows paths above on Windows):

```toml
[sandbox_workspace_write]
writable_roots = [
  "/Users/me/Library/Application Support/dotnet/runfile",
  "/Users/me/.nuget/packages",
  "/Users/me/.local/share/NuGet",
]
```

Alternatively redirect the two NuGet caches to a folder that is already writable, with `[shell_environment_policy.set]` (still allow the runfile folder in `writable_roots`):

```toml
[shell_environment_policy.set]
NUGET_PACKAGES = "/Users/me/dev/.nuget-packages"
NUGET_HTTP_CACHE_PATH = "/Users/me/dev/.nuget-http"
```

**Claude Code** (`~/.claude/settings.json`; its sandbox covers macOS, Linux and WSL2, native Windows runs unsandboxed). Paths starting `~/` are relative to your home directory:

```json
{
  "sandbox": {
    "filesystem": {
      "allowWrite": [
        "~/Library/Application Support/dotnet/runfile",
        "~/.nuget/packages",
        "~/.local/share/NuGet"
      ]
    }
  }
}
```

To redirect the NuGet caches instead, add `"env": { "NUGET_PACKAGES": "...", "NUGET_HTTP_CACHE_PATH": "..." }` to the same file and allow the runfile folder as above. On Linux replace the macOS runfile path with `~/.local/share/dotnet/runfile`.

Checked against Claude Code 2.1.296 and its sandbox documentation (`sandbox.filesystem.allowWrite`) and against the Codex configuration reference (`sandbox_workspace_write.writable_roots`, `shell_environment_policy.set`; the page names no version, and the maintainer's own Codex uses the `writable_roots` form above).

**Symptoms that the sandbox is not set up.** `NU1900` from a restore (it could not get package vulnerability data: an unwritable NuGet HTTP cache or a blocked network) or any `access denied` / `Read-only file system` / `Operation not permitted` error from `dotnet run scripts/<name>.cs` before the script prints anything (the runfile cache). Fix the sandbox as above; do not work around it inside the repository.

Use **ValheimCLI** as the product name in documentation. Preserve exact executable names, paths, package IDs, plugin GUIDs and code identifiers such as `valheim-cli`, `valheimCLI.dll` and `Valheim.Cli.Testing`.
