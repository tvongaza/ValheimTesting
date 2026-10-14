# Give your coding agent the testing workflow

Install the ValheimTesting skill in your mod's repository, so Codex or Claude Code finds the testing workflow when you ask for a test.

## When to use this

Use this when a coding agent works in your mod's repository and you want it to add or run tests the same way every time. The skill tells the agent how to choose a test layer, which guide to follow, how to run a disposable game check, and what to report. It is plain instructions, not a plugin, and it changes nothing in your build.

## Steps

### 1. Copy the skill into your mod repository

Run from your mod repository's root:

```sh
mkdir -p .agents/skills/valheim-mod-testing .claude/skills/valheim-mod-testing
curl -fsSL -o .agents/skills/valheim-mod-testing/SKILL.md \
  https://raw.githubusercontent.com/tvongaza/ValheimTesting/main/.agents/skills/valheim-mod-testing/SKILL.md
cp .agents/skills/valheim-mod-testing/SKILL.md .claude/skills/valheim-mod-testing/SKILL.md
```

This writes one `SKILL.md` for each agent. If you use only one agent, keep only its copy:

| Agent | Path in your mod repository | Ask for it by name |
|---|---|---|
| Codex | `.agents/skills/valheim-mod-testing/SKILL.md` | `$valheim-mod-testing` |
| Claude Code | `.claude/skills/valheim-mod-testing/SKILL.md` | `/valheim-mod-testing` |

The source is [valheim-mod-testing/SKILL.md](../../.agents/skills/valheim-mod-testing/SKILL.md). Its links open the current docs on this repository's `main`. Commit the copies with the mod, and copy the file again to update it.

### 2. Put your mod's own facts in its AGENTS.md

The skill covers the testing task. Facts that apply to every task belong in your mod's `AGENTS.md` (and `CLAUDE.md`, if you use one). Add a short section like this one; the paths are illustrative:

```markdown
## Tests

- Test project: `MyMod.Tests/MyMod.Tests.csproj`. Run `dotnet test MyMod.Tests/MyMod.Tests.csproj -c Release`.
- Build the plugin with `dotnet build -c Release`. It needs the game installed.
- Generated files: `MyMod/Generated/`. Do not edit them by hand.
- Native checks run only on my Windows PC, and only after I say yes.
```

Link the toolkit's pages rather than copying them: [Choose a test layer](choose-a-test-layer.md) and the [agent workflow](../agent-guide.md).

### 3. Ask for a test

Name the skill in the request, or let the agent pick it from the request:

```text
Using the Valheim mod testing skill, add one source-linked unit test for the recipe rule and run its test project.
```

```text
Using the Valheim mod testing skill, check that this build loads in a disposable hosted game, and report the plugin identity, result and cleanup.
```

The first request needs only the .NET SDK. The second starts the game, so the agent asks you first.

## Check the result

- The agent names the skill, or you can invoke it by name from the table above.
- Its report lists what it tested, the versions, the checks run and not run, the evidence paths and the teardown status ([report shape](../agent-guide.md#completion-and-handoff)).

## Problems

| Problem | Fix |
|---|---|
| The agent does not use the skill | Check the path from the table above, and that the file starts with its `name` and `description` frontmatter. Then ask for it by name |
| The agent clones or builds ValheimTesting | Point it at the skill. A mod uses the published packages and needs nothing built |
| The agent pins a version that does not restore | Pin an exact version from [package versions](../package-versions.md) |
| Your mod has its own test commands or fixtures | Put them in your mod's `AGENTS.md`, not in the copied skill |

## Related

- [Agent workflow](../agent-guide.md): the loop, the interpretation table and the report shape
- [Choose a test layer](choose-a-test-layer.md)
- [Codex skills](https://developers.openai.com/codex/skills) and [Claude Code skills](https://code.claude.com/docs/en/skills): how each agent finds a project skill
