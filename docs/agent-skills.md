# Give a mod's coding agent the ValheimTesting workflow

A repository skill lets an agent discover the testing workflow when you ask it to add or run mod tests. Keep the skill in **your mod repository**, beside the tests it describes. The example in this repository is [valheim-mod-testing/SKILL.md](../.agents/skills/valheim-mod-testing/SKILL.md); copy it and adjust the first paragraph or links if your mod has its own test commands and fixtures.

| Agent | Skill path in your mod repository | How to use it |
|---|---|---|
| Codex | `.agents/skills/valheim-mod-testing/SKILL.md` | Ask for a mod test normally, or name `$valheim-mod-testing` explicitly. |
| Claude Code | `.claude/skills/valheim-mod-testing/SKILL.md` | Ask for a mod test normally, or invoke `/valheim-mod-testing`. |

Copy the same `SKILL.md` into both locations when your team uses both agents. Commit those copies with the mod; they are instructions, not game plugins. Each copy needs YAML frontmatter with a short `name` and a `description` that says when to use it. Codex and Claude Code both discover project skills from these respective directories ([Codex skill guide](https://learn.chatgpt.com/docs/build-skills), [Claude Code skill guide](https://code.claude.com/docs/en/skills)).

In the mod repository, also keep a short `AGENTS.md` (and `CLAUDE.md` if used) for facts that apply to **every** task: the test project path, how to build the plugin, which files are generated, and local safety rules. Use the skill for the **testing task**: choosing a layer, locating the right ValheimTesting example, running the disposable game check, and reporting evidence. Do not paste the full toolkit guide into every agent instruction file; link to [Bring your mod](adopting.md) and the [agent workflow](agent-guide.md).

For example, after copying the skill, ask: “Using the Valheim mod testing skill, add one source-linked unit test for the recipe rule and run its test project.” For a real-game boundary: “Using the Valheim mod testing skill, check that this build loads in a disposable hosted game, and report the plugin identity, result and cleanup.” The second request requires a game environment; the first does not.

The copy is a starting point, not a frozen package pin. Keep your mod's version and fixture choices in its own project and test plan. Update the skill when its links or the mod's local workflow change; the current CLI and package contract lives in the toolkit docs, not in the copied text.
