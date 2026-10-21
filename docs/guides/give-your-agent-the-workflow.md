# Give your coding agent the testing workflow

Install the ValheimTesting skill in your mod's repository, so Codex or Claude Code finds the testing workflow when you ask for a test.

## When to use this

Use this when a coding agent works in your mod's repository and you want it to add or run tests the same way every time. The skill tells the agent how to choose a test layer, which guide to follow, how to run a disposable game check, and what to report. It is plain instructions, not a plugin, and it changes nothing in your build.

## Steps

### 1. Copy the skill into your mod repository

You need GitHub CLI access and `curl`. From your mod repository's root, resolve the latest stable release once and download its skill:

```sh
(
  set -eu
  vtReleaseTag=$(gh api repos/tvongaza/ValheimTesting/releases/latest --jq .tag_name)
  test -n "$vtReleaseTag"
  vtReleaseSource=$(gh api "repos/tvongaza/ValheimTesting/commits/$vtReleaseTag" --jq .sha)
  test "${#vtReleaseSource}" -eq 40
  case "$vtReleaseSource" in *[!0-9a-f]*) exit 1 ;; esac
  vtSkillDownload=$(mktemp)
  trap 'rm -f "$vtSkillDownload"' 0
  curl -fsSL -o "$vtSkillDownload" \
    "https://raw.githubusercontent.com/tvongaza/ValheimTesting/$vtReleaseSource/.agents/skills/valheim-mod-testing/SKILL.md"
  mkdir -p .agents/skills/valheim-mod-testing .claude/skills/valheim-mod-testing
  cp "$vtSkillDownload" .agents/skills/valheim-mod-testing/SKILL.md
  cp "$vtSkillDownload" .claude/skills/valheim-mod-testing/SKILL.md
  printf '%s %s\n' "$vtReleaseTag" "$vtReleaseSource"
)
```

This writes one `SKILL.md` for each agent and prints its release tag and source commit. If you use only one agent, keep only its copy:

| Agent | Path in your mod repository | Ask for it by name |
|---|---|---|
| Codex | `.agents/skills/valheim-mod-testing/SKILL.md` | `$valheim-mod-testing` |
| Claude Code | `.claude/skills/valheim-mod-testing/SKILL.md` | `/valheim-mod-testing` |

The source is [valheim-mod-testing/SKILL.md](../../.agents/skills/valheim-mod-testing/SKILL.md). Record the resolved tag and commit with your copied skill. Commit both agent copies with your mod; rerun these commands to update them.

The tagged skill may lack a needed journey. To use a newer reviewed skill before its release, explicitly replace `vtReleaseSource` with one `docsSourceSHA`. Use that revision for the skill, its instructions and the entire example map; identify it as unreleased documentation.

Before downloading an example, check that every mapped file exists at the chosen revision. Keep published package identity separate, using the release's attached `release-contract.json`; never mix source revisions or silently download from `main`.

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

The first request needs only the .NET SDK. The second starts the game, so the agent needs your authorization unless you already provided it.

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
