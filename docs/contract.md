# Shared contract with TenenGroup.QualityGates

The two packages share no code. They share only:

1. MSBuild property names: `QualityGatesEnabled`, `QualityGatesLayer` (`core|adapter|test|excluded`), `QualityGatesProfile`.
   This package adds `QualityGatesMode` (`adopt|refactor`, env `QUALITYGATES_MODE`) and `QualityGatesRefactorScope`; the company package may ignore them.
2. The `quality-report.json` format, so one orchestration plugin can read both.

## quality-report.json

```json
{
  "schema": 1,
  "tool": "KirillMaster.QualityGates",
  "gates": [
    { "id": "build", "status": "passed", "blocking": true },
    { "id": "format", "status": "passed", "blocking": true },
    { "id": "architecture", "status": "passed", "blocking": true },
    { "id": "tests", "status": "passed", "blocking": true },
    { "id": "diff-coverage", "status": "failed", "blocking": true, "value": 74.2, "threshold": 80 },
    { "id": "mutation", "status": "passed", "blocking": false, "value": 61.0 }
  ]
}
```

`status` is `passed|failed|skipped`. A report is green when no gate with `blocking: true` has status `failed`. The CI workflow in this repository emits the file as an artifact.
