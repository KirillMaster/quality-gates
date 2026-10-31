# qg CLI

`dotnet tool install KirillMaster.QualityGates.Cli` installs the `qg` command (net10).

| Command | Purpose |
|---|---|
| `qg pr` | Pull request gates: new-code violations, static ratchet, diff coverage, format, duplication, baseline growth, PR size, change coupling, architecture and cognitive-complexity deltas, fan-out, dead public API, toggle expiry, branch age, build trend; writes a Markdown/JSON report |
| `qg mutation-diff` | Mutation testing on the changed lines (Stryker) with a score threshold and time budget |
| `qg mutation-ratchet` | Mutation score ratchet against a baseline report |
| `qg nightly` | Whole-repository run for scheduled builds |
| `qg audit` | Debt report (HTML) |
| `qg grandfather` | Generates `GlobalSuppressions` for existing banned call sites |
| `qg baseline` | Creates, merges and fans out baselines |

Every command prints its options with `--help`. Exit codes: `0` pass, `1` gate failed, `2` unstable (warning), `3` input error. Inputs are plain files (SARIF from the build, Cobertura coverage, Stryker JSON, optional build-history JSON), so any CI system can run it. Configuration lives in `quality/gates.json` (baselines in `quality/baseline.json`) of the analysed repository.
