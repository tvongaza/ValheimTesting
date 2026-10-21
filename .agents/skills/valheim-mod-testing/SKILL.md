---
name: valheim-mod-testing
description: Add or run ValheimTesting tests in a Valheim mod repository, from source-linked unit tests to disposable native game checks. Use for mod adoption, regression scenarios, and test evidence; not for developing ValheimTesting itself.
---

# Test a Valheim mod with ValheimTesting

Read the mod repository's instructions first. Keep its tests, adapter and fixtures there; testing packages stay out of the production plugin. Consume published packages and `valheim-test` without cloning or building ValheimTesting.

## Choose one source revision

Resolve the latest stable [GitHub release](https://api.github.com/repos/tvongaza/ValheimTesting/releases/latest) once. Record its tag and full commit SHA. Read the workflow and download source files at that SHA, using `https://raw.githubusercontent.com/tvongaza/ValheimTesting/SOURCE_SHA/FILE` with those two path components replaced.

Before copying an example, check that every file in its documented map exists at that revision. If required files are absent, use one explicitly chosen `docsSourceSHA` for the entire map and its instructions. Report that source revision separately from the published package versions. Never silently fall back to moving `main` or mix files from different revisions.

Read `docs/agent-guide.md` at the chosen source revision. Take package IDs and exact versions from the chosen release's attached `release-contract.json` (`packages[].id` and `packages[].version`), then verify a public restore. The tag can precede the release-pin record: use `docs/package-versions.md` and example pins only when they agree with that contract. If the contract or matching source pins are unavailable, report the missing input rather than guessing or hand-bumping the example. Candidate source is not evidence of published behavior.

## Run the smallest useful check

| Task | Read at the chosen source revision |
|---|---|
| Test a production decision without the game | `docs/getting-started.md` |
| Adapt code that calls game types | `docs/guides/test-logic-that-uses-game-types.md` |
| Test terrain decisions | `docs/guides/test-terrain-decisions.md` |
| Exercise driver failures without a game | `examples/FullLifecycle/consumer/README.md` |
| Assert gameplay in one hosted game | `docs/tutorials/first-gameplay-assertion.md` |
| Compare the same assertion on two builds | `docs/guides/run-an-ab-regression.md` |
| Save, restart and rejoin | `docs/guides/test-save-restart-rejoin.md` |
| Observe a change from two clients | `docs/guides/run-a-session-with-several-clients.md` |
| Check startup only | `docs/guides/check-a-mod-loads.md` |

Run the mod's test command. Stop if it answers the question. For an existing failure, preserve the failing output and verify the matching correction with the same assertion.

A native run needs authorization and a disposable fixture. If authorization is already provided, continue within that scope. Otherwise obtain it before launching. Read `docs/guides/before-a-native-run.md`; check local readiness with `valheim-test env preflight`, or all session hosts with `valheim-test session check SESSION --hosts`. Follow the guide's supported entry and normal lifecycle; do not bypass a refusal with raw transport.

Read the actual report, named assertions, complete observations and cleanup. A connected ValheimCLI or successful mutation reply does not prove gameplay; incomplete is not zero.

Afterwards, `valheim-test env status` must list nothing your run left. Recover only the named interrupted run with `valheim-test env recover --run ID`; follow its reported inspection/teardown state.

Report the mod build, package versions, source revision, checks passed/failed/not run, private evidence paths and cleanup status. Use `docs/agent-guide.md#completion-and-handoff`. Keep secrets, account identifiers and saves out of public output. Report toolkit or ValheimCLI gaps with the evidence shape in `docs/agent-guide.md#report-a-problem`.
