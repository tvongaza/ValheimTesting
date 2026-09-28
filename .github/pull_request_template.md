## Problem and consumer

Which mod or test needs this? What behavior changes? Link an issue or companion PR if relevant.

## Contract and scope

Describe the shared API/fixture/observation contract and an example of its use. Note repository ownership, compatibility or schema changes, and deliberate approximations. Keep mod-specific behavior in its mod repository.

## Validation

List commands/checks actually run and their results. Include a discriminating regression or negative control when useful. Distinguish synthetic/controlled tests from native evidence.

## Not run and limitations

State what remains unverified. If native validation is needed, give a small disposable-fixture plan. Do not claim game coverage from mock or replay agreement. Pure fixtures and docs do not require a station run.

## Contribution checklist

- [ ] Read [CONTRIBUTING.md](https://github.com/tvongaza/ValheimTesting/blob/main/CONTRIBUTING.md); chose the correct package/repository.
- [ ] Added or updated relevant tests/examples/docs, or explained why none are needed.
- [ ] Described meaningful failure/missing-data handling and compatibility changes, where applicable.
- [ ] Removed private data, game binaries/source, secrets and investigation artifacts; preserved source attribution.
