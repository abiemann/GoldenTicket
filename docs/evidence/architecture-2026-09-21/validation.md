# Architecture review validation — 2026-09-21

This record covers the architecture changes described in [architecture.md](../../architecture.md):
read-only state boundaries, storage contract parity, transactional turn timing, injectable camera
capture, portable core tests and dependency guards. It records local evidence, not release approval.

## Results

| Check | Result |
|---|---|
| Locked solution package restore | Passed; existing package locks unchanged, new core suite lock added |
| Full Debug solution build | Passed; 0 errors, 166 warnings (not a warning-free build) |
| Portable core suite, run on Windows | 320 passed; 0 failed or skipped |
| Windows integration suite | 903 passed; 0 failed or skipped |
| Production WPF smoke views | 122 cases; 0 binding errors or warnings |
| Four-seat Standard simulator, seeds 1–20 | 20/20 complete; invariants held and every journal replayed to the same state |
| Documentation links and diff whitespace | Passed |

The final test assemblies were built together into the ignored
`artifacts/architecture-20260921/bin/` directory to avoid replacing the running application's
outputs. Final TRX files, build/test logs and synthetic UI evidence are in the sibling artifact
directories. These local generated artifacts are not part of the source distribution.

The full integration run used an ordinary Windows profile. An initial restricted-profile run
failed while importing a test HTTPS certificate; the focused transport check and final full suite
passed with normal profile access. No live camera or real saved match was used by these checks.

## Reproduction

Use the SDK pinned by `global.json` and the commands in
[CONTRIBUTING.md](../../../CONTRIBUTING.md). The suite split moves existing core tests; shared
fixture helpers do not duplicate test cases. Architecture guards execute as part of the core suite.

## Limits

The new Ubuntu CI job is configured but has not yet run remotely. Local execution of a portable
target on Windows does not establish Linux runtime compatibility. The existing browser suite was
not rerun for this change; no phone-client implementation changed. Hardware detection quality,
phone acceptance, power-loss recovery and public-release licensing remain separate acceptance
work in [TODO.md](../../../TODO.md). Large desktop workflow coordinators remain documented design
debt; the review does not claim that all architectural work is finished.
