# Writing the docs

Every page follows these rules. A reviewer can check each one.


## Voice

1. **Second person, active voice, present tense.** "You run", "the tool refuses". No "we", no "the user", no "will be". Check: grep `we |will be|previously|now |used to|was replaced|no longer`.
2. **Short sentences.** No sentence over 30 words. Paragraphs have at most four sentences and one idea.
3. **Condition first, then instruction.** "If your Steam library is elsewhere, set `VALHEIM_PATH`." Never the reverse.
4. **Say what the reader does, then what they see.** Every command block is followed by its result (an output block, or one sentence naming the file it writes).
5. **No history.** No issue numbers, PR numbers, "previously", "formerly", "renamed from", "removed in", measurement anecdotes or dates in body text. Issue numbers appear only inside `<!-- TODO(docs): ... -->`.
6. **No filler.** Delete "simply", "just", "easy", "note that", "it is worth", "in order to", and any sentence that restates the heading.
7. **One home per fact.** A fact is written on one page; every other page links to it. If you are about to paraphrase another page, link instead.
8. **Honest scope, no hedging prose.** State a limit in one sentence under **Limits**; do not weave caveats through every paragraph.

## Page templates and length

| Type | Folder | Title form | Required sections, in order | Limit |
|---|---|---|---|---|
| Tutorial | `docs/tutorials/` | "Your first …" | What you build · You need · numbered steps (command → output) · What you have now · Next | 1,000 words, one path, no options |
| How-to | `docs/guides/` | Verb phrase: "Check that a mod loads on a dedicated server" | When to use this · Steps · Check the result · Problems (table) · Related | 800 words; variants in a table, not prose |
| Reference | `docs/packages/`, `docs/package-versions.md`, `docs/reference/` | The thing's name | Purpose (one paragraph) · Install or pin · tables of types/commands/fields → does → fails when · Limits | 1,500 words; over that, split by concept |
| Concept | `docs/concepts/` | A noun phrase or a question | The question · the answer · how it shows up in practice · Related | 700 words; commands only to illustrate |

A page is exactly one type. If a reference page starts giving advice, move the advice to a how-to and link it.

## Examples

9. **Complete and copy-pasteable.** State the directory to run from. Use the pinned versions `toolkit-versions.json` records, in the shapes `pins.cs write` rewrites (the package table, `PackageReference` snippets, `dotnet tool install … --version`). Check: `dotnet run scripts/pins.cs -- write` produces no diff after your change.
10. **Show the output.** Put expected output in its own fenced block right after the command. Only output you captured or that the source quotes verbatim; otherwise `<!-- TODO(docs): capture output -->`.
11. **Tag every fence** (`sh`, `csharp`, `json`, `xml`, `text`).
12. **Placeholders as the source spells them.** `SESSION`, `FILE`, `ID`, `<name>`, `YourMod.dll`: copy the parser's or page's spelling; never invent a new placeholder style.
13. **Illustrative values are labelled.** Coordinates, UIDs and hashes that are not real say so in the line before the block.

## Tables

14. Use a table when three or more items share two or more attributes. Never for prose, never for one row, never more than four columns. Cells have at most 15 words. Options, flags, fields, patterns, pins, states and "what each layer cannot prove" are always tables.

## What to cut from the current pages

- Per-change notes, issue archaeology, "the one exception is…" chains: state the rule; put the exception in one clause.
- Paragraphs that explain why a design is right. One sentence of rationale at most; the rest goes to a concept page or is deleted.
- Repeated safety reminders. They live once, on the [native-run checklist](../guides/before-a-native-run.md) and in AGENTS.md.
- Native measurement narratives ("ran it ten times…", "median 10.35 s"). Keep the claim as behaviour.
- Any behaviour an open release issue removes, such as Linux game clients or the Doorstop 3/4 pairing rules.

## Exactness

15. **Copy, never retype.** Every command, option, type, member, file path, URL, GUID and version is pasted from the source and set in backticks. Do not re-case, pluralise or abbreviate inside backticks.
16. **Product names.** *ValheimCLI* is the product; `valheim-cli` is its executable; `valheimCLI.dll` the plugin; `valheim-test` the toolkit's tool. *ValheimTesting* is this repository.
17. **If the source does not say it, mark it.** `<!-- TODO(docs): … -->` for a gap, `<!-- TODO(docs): verify after #N -->` for an unmerged behaviour an issue states exactly, `<!-- TODO(docs): #N -->` for a slot an issue will fill.
18. **Headings carry no version or date**; they name the concept. Anchors you link exist (`dotnet run scripts/pins.cs -- docs` fails otherwise).
19. **Identifier check.** Every backticked token on the page is found by `git grep` in `src/`, `scripts/`, `examples/`, `tools/`, `tests/` or an open issue body.

## Before and after

**1. From `docs/getting-started.md`, intro.**
Before: "The unit tests need only the .NET 10 SDK, on any OS; no game. The usual setup for the native checks is the **Windows PC where Valheim is installed through Steam**: an owned dedicated server and an owned client run there, both started from test copies of the Steam installs."
After: "Unit tests need the .NET 10 SDK and nothing else. They run on any OS. Native checks run on the Windows PC where Steam installed Valheim: `valheim-test` starts a dedicated server and a client from disposable profiles and leaves the install untouched."

**2. From `docs/packages/Valheim.Testing.Game.md`, "Pins".**
Before: "**Computing the pins.** Every strict plan needs `runtimePins`, and an owned client section needs `installPins`; `ValidateServerPlan` and `ClientRunPlan.Validate` refuse a plan without them. Compute the values once with `InstallPins.Of(<runtime>)` and `InstallPins.Of(<client install>)` (or the shell listing above), review them, and add them to the plan: … `bepinexCore`, which pinned `BepInEx/core` alone, was replaced by `loader` (#295); a plan that still names it is refused with that instruction."
After: "A standalone runner pins omitted values from what it stages and records them as observed. To require reviewed values, compute the pins with `InstallPins.Of(<runtime>)` and add them to the plan:
```json
"runtimePins": { "game": "...", "loader": "...", "patchers": "..." }
```
A disposable owned client may omit `"installPins"`; the runner records and enforces the staged profile's observed values. An in-place client still supplies the expected object."

**3. From `CONTRIBUTING.md`, "Set up and validate locally".**
Before: "A pull request carries code, tests and docs only. It changes no package `<Version>`, no Cli `packageVersion`, no dependency pin between the packages, no `toolkit-versions.json` entry or copyable pin, and no list in `docs/reference/public-api/`: cutting a release does all of that in one step (see "Release packages"). The one exception is a pull request that moves the ValheimCLI fork pin: it marks the transport a candidate …"
After: "A pull request changes code, tests and docs. Leave versions, pins, `toolkit-versions.json` and `docs/reference/public-api/` alone; the release PR changes them. Exception: a PR that moves the ValheimCLI fork pin (see the ValheimCLI release contract)."

---


## Project decisions

- **Simpler still.** Cut anything a newcomer doesn't need to finish the task. Prefer one more short example over one more paragraph. Code and examples are welcome; prose is the cost.
- **Keep entry pages short.** `docs/getting-started.md` names the one or two packages a reader needs first and links the full package table, which lives on `docs/package-versions.md`.
- **The site publishes `docs/index.md`, `getting-started.md`, `package-versions.md` and every page in `guides/`, `concepts/`, `tutorials/`, `packages/` and `reference/`.** To publish a new page, put it in one of those folders and add it to `docs/toc.yml`; `dotnet run scripts/api-docs.cs` fails when a published page has no entry. `maintainers/` and the agent pages stay on GitHub. Write links as relative paths: the build keeps links to published pages and points every other link, such as `examples/` or `CONTRIBUTING.md`, at the same file in the build's source revision.
- **No bylines** (`*Assistant-written (Claude).*` and similar) on any page.
- **No inbound-link protection.** Nobody uses the docs yet. Move, merge, rename and delete pages freely, and fix every link in the same change.
- **No dated validation records.** State a durable fact as behaviour on the page that needs it, for example: a mod that reads a private game member at run time needs `AllowUnsafeBlocks=true`, and `IgnoresAccessChecksTo` alone throws `FieldAccessException`. Don't add "a native campaign proved X" claims.
- **ValheimCLI problems** go to the pinned ValheimCLI fork, or to this repository's issues with the `valheimcli` label. Toolkit bugs go to this repository's issues.
