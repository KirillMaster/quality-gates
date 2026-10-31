# PR-mode fixtures

Each fixture holds the coverage and SARIF inputs plus quality/gates.json and quality/baseline.json.
`setup.sh <name> <dest>` builds a git repo in `<dest>`: branch `develop` from `_base`, ref `origin/develop`
at that commit, then branch `feature/x` with the fixture's `change/` overlay committed on top.
`NO_BASELINE=1` drops quality/baseline.json, `BAD_CONFIG=1` writes `"diffCoverage": {"core": "ninety"}` to gates.json.

Run: `quality-gates pr --repo <dest> --target origin/develop --source feature/x --coverage "<name>/coverage/*.xml" --sarif "<name>/sarif/*.sarif" --out <out>`

| Fixture | Change | Uncovered changed lines | Expected exit |
|---|---|---|---|
| uncovered-core | adds Calc.Mul | src/Sample.Domain/Calc.cs:8 | 1 |
| clean | adds Calc.Mul and Repo.Id, fully covered | none | 0 |
| weak-adapter | adds Repo.Id, gates.json integrationTestsEnabled=true | src/Sample.Adapters/Repo.cs:8 | 1; with `--integration-enabled false` 2 |
| docs-only | README.md only | none (no coverable lines) | 0 |
| uncovered-and-new-warning | as uncovered-core plus new CA1822 warning in Sample.Domain | src/Sample.Domain/Calc.cs:8 | 1 |
| baseline-growth | quality/baseline.json CA1822 count 1 -> 2 | none | 1 (BaselineGrowth fails; `--growth-approved-by <CODEOWNER> --pr-author <other>` makes it 2) |
| arch-delta | adds a ProjectReference Sample.Adapters -> Sample.Domain | none | 0 (ArchDelta lists `+ Sample.Adapters -> Sample.Domain`) |
| new-warning-on-legacy-line | as clean, with baseline already holding one CA1822 in Sample.Domain and a CA1822 reported on the added line | none | 1 (new-code-violations fails, static-ratchet passes) |
