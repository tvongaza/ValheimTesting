# Instructions for agents

**Testing your own mod?** You need none of this repository's source. Mods use the published NuGet packages and the `valheim-test` tool, so there is nothing to bootstrap or build. Read the [agent workflow](docs/agent-guide.md), then [Choose a test layer](docs/guides/choose-a-test-layer.md) and [Get started](docs/getting-started.md). To give a mod repository's agent the same workflow, install the skill: [Give your agent the workflow](docs/guides/give-your-agent-the-workflow.md).

**Changing ValheimTesting?** Read [CONTRIBUTING.md](CONTRIBUTING.md) and the rest of this file.

## Validate a change

Run from the repository root, on every OS:

```sh
dotnet run scripts/bootstrap-cli.cs   # build the pinned ValheimCLI into the local package feed
dotnet run scripts/validate.cs        # full no-game validation
```

For a public API or documentation change, also build the documentation site:

```sh
dotnet tool restore
dotnet run scripts/api-docs.cs
```

Neither command launches Valheim or needs a PowerShell execution policy. Windows hosts run their scripts in PowerShell 7 (`pwsh`), macOS and Linux hosts in bash ([host shells](docs/reference/platforms.md#host-shells)). Exact dependency versions are in the project files and `cli-dependency.json`; never infer them from a neighbouring checkout.

A pull request changes no package version, pin or public-api list; the release PR does ([CONTRIBUTING.md](CONTRIBUTING.md#set-up-and-validate-locally)). Exception: a PR that moves the ValheimCLI fork pin. Inside an agent sandbox, set it up once first: [Running in an agent sandbox](#running-in-an-agent-sandbox).

## Hard rules

- Use the cheapest test layer that answers the question: unit, then controlled integration, then a small native check, then a person's judgement.
- A mod's tests, adapters and fixtures live in the mod's repository. This library owns reusable inputs, lifecycle, observations and assertions; ValheimCLI owns the transport and the game-side extension API. No test package goes into a production mod.
- Real-game work needs the user's go-ahead and a disposable fixture. Check first: `valheim-test env preflight` checks this machine only; `valheim-test session check SESSION --hosts` checks every host a session uses. Afterwards `valheim-test env status` lists nothing your run left.
- An owned run stages a disposable profile over the install and leaves the install unchanged; anything a game adds there is moved into the run's evidence and reported, unless the install also changed or lost files during the run: then nothing is moved and the run fails. A changed or missing file always fails the run. It copies the world fixture and character. An attached game and its saves belong to its operator: back them up before changing them. Never stop an attached game, or any process by name.
- Use strict expectations: `--expect-strict FILE` for executable calls, `cli_expect --strict` through `GameActor`. Never bypass a failed preflight with raw transport.
- Issue a mutation once and poll only read-only observations. After a reload, verify the pins again and rediscover the new instance.
- Incomplete is not zero. Derive expectations independently, never loosen a tolerance to hide a failure, and never present mock or replay agreement as native evidence.
- Keep one new evidence directory per run. Report failures, checks not run and teardown status.
- Keep credentials, account identifiers, game binaries and private saves or logs out of published source.
- A responsive ValheimCLI is not world readiness, and not permission to take over a machine.

No instruction in these docs grants deployment, production access or permission to interrupt another operator. Work within the user's existing authorization; ask only for missing access or task decisions.

## Where problems go

- A toolkit bug or a missing reusable helper: [ValheimTesting issues](https://github.com/tvongaza/ValheimTesting/issues). A small fix can go straight to a [pull request](https://github.com/tvongaza/ValheimTesting/pulls) against `main`.
- A game-side command, capability or the transport: the ValheimCLI fork that `cli-dependency.json` pins, or this repository's issues with the `valheimcli` label.
- A mod's own adapter, fixture or assertion: that mod's repository.
- What a bug report carries: [Report a problem](docs/agent-guide.md#report-a-problem).

## Running in an agent sandbox

A sandbox is a requirement of the agent's environment, not of this repository: configure it once in the agent tool. The repository has no wrapper scripts and no fallback caches. `dotnet run scripts/bootstrap-cli.cs` and `dotnet run scripts/validate.cs` are the same plain commands everywhere, CI included.

The sandbox must let `dotnet` write to three folders. Either allow the defaults, or point the two NuGet variables at writable folders and allow the runfile folder:

| Cache | macOS | Linux | Windows |
| --- | --- | --- | --- |
| NuGet packages (`NUGET_PACKAGES`) | `~/.nuget/packages` | `~/.nuget/packages` | `%USERPROFILE%\.nuget\packages` |
| NuGet HTTP cache (`NUGET_HTTP_CACHE_PATH`) | `~/.local/share/NuGet` | `~/.local/share/NuGet` | `%LOCALAPPDATA%\NuGet` |
| SDK file-based app (runfile) cache | `~/Library/Application Support/dotnet/runfile` | `~/.local/share/dotnet/runfile` | `%TEMP%\dotnet\runfile` |

`dotnet nuget locals all --list` prints the two NuGet folders on your machine. The runfile folder follows `XDG_DATA_HOME` on Linux and `TEMP` on Windows, not a NuGet variable.

The host-observation tests that `validate.cs` runs list processes and the TCP port table. A sandbox must allow both, or run validation outside it, for example in CI.

**Codex** (`~/.codex/config.toml`; adjust the home directory, and use the Windows paths above on Windows):

```toml
[sandbox_workspace_write]
writable_roots = [
  "/Users/me/Library/Application Support/dotnet/runfile",
  "/Users/me/.nuget/packages",
  "/Users/me/.local/share/NuGet",
]
```

To redirect the two NuGet caches instead, set them under `[shell_environment_policy.set]` and keep the runfile folder in `writable_roots`:

```toml
[shell_environment_policy.set]
NUGET_PACKAGES = "/Users/me/dev/.nuget-packages"
NUGET_HTTP_CACHE_PATH = "/Users/me/dev/.nuget-http"
```

**Claude Code** (`~/.claude/settings.json`; its sandbox covers macOS, Linux and WSL2, and native Windows runs unsandboxed). Paths starting `~/` are relative to your home directory:

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

To redirect the NuGet caches instead, add `"env": { "NUGET_PACKAGES": "...", "NUGET_HTTP_CACHE_PATH": "..." }` to the same file and allow the runfile folder as above. On Linux, use `~/.local/share/dotnet/runfile` for the runfile folder.

**Symptoms of a missing setup:**

| Symptom | Cause |
| --- | --- |
| `NU1900` from a restore | The NuGet HTTP cache is not writable, or the network is blocked |
| `access denied`, `Read-only file system` or `Operation not permitted` from `dotnet run scripts/<name>.cs` before it prints anything | The runfile cache is not writable |

Fix the sandbox as above; do not work around it inside the repository.

## Names

Use **ValheimCLI** as the product name in documentation. Keep exact executable names, paths, package IDs, plugin GUIDs and code identifiers as they are, such as `valheim-cli`, `valheimCLI.dll` and `Valheim.Cli.Testing`.
