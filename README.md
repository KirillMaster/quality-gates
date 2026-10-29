# quality-gates

Reusable quality gates for modern .NET (net8 / net10). MIT licensed.

| Package | Purpose |
|---|---|
| `KirillMaster.QualityGates.Build` | Build-only package: warnings-as-errors, `AnalysisLevel 10.0-all` (pinned), nullable, curated third-party analyzers (Sonar, Meziantou, Roslynator, VS.Threading, IDisposableAnalyzers, BannedApiAnalyzers), own Roslyn analyzers `QG0xxx`, `quality.globalconfig`, `BannedSymbols.txt`, `.editorconfig` template, grandfathered suppressions. |
| `KirillMaster.QualityGates.Architecture` | ArchUnitNET-based test base for hexagonal layering, config-driven layers, namespace-cycle and type-cycle (baseline) checks, repository meta-tests (locked settings, central package versions, suppression ratchet, every project covered). |

No StyleCop (Roslynator covers it). Legacy .NET Framework (net48) is not supported.

## Usage

```xml
<!-- Directory.Packages.props -->
<GlobalPackageReference Include="KirillMaster.QualityGates.Build" Version="0.1.0" />
<PackageVersion Include="KirillMaster.QualityGates.Architecture" Version="0.1.0" />
```

Without central package management add `<PackageReference Include="KirillMaster.QualityGates.Build" Version="0.1.0" PrivateAssets="all" />` to each project.

Copy `templates/editorconfig.template` from the package (or `src/KirillMaster.QualityGates.Build/templates/`) to `.editorconfig` in the repository root.

### MSBuild contract

Property names are shared with the company package so one tool can drive both; the report format is described in [docs/contract.md](docs/contract.md).

| Property | Default | Meaning |
|---|---|---|
| `QualityGatesEnabled` | `true` | `false` turns everything off (set in `Directory.Build.props` or `/p:`) |
| `QualityGatesProfile` | `strict` | `strict` = `AnalysisLevel 10.0-all`; `legacy-ratchet` = `10.0-recommended` plus grandfathered suppressions |
| `QualityGatesMode` | `adopt` (env `QUALITYGATES_MODE` when empty) | `adopt` compiles grandfathered suppressions; `refactor` does not compile them, forces `TreatWarningsAsErrors=true` and raises every `QG*` rule to error (see Refactor mode); the mode can also be set in the project body |
| `QualityGatesRefactorScope` | empty (all projects) | `;` list of project-name prefixes (`Shop.Orders*;Shop.Billing`, blanks and spaces ignored, trailing `*` optional); projects outside the scope stay in `adopt` |
| `EnforceCodeStyleInBuild`, `AnalysisLevel` | `true`, pinned by profile | defaults only; values set by the consumer win. `AnalysisLevel` is never `latest` so SDK upgrades do not change the rules |
| `QualityGatesLayer` | derived | `core` (`*.Domain`, `*.Application`), `test` (`*.Tests`, relaxed test config), `adapter` |
| `QualityGatesBannedSymbols` | `true` | `false` drops `BannedSymbols.txt` |
| `QualityGatesGrandfatheredDir` | `<repo>/quality/grandfathered/` | folder with `<Project>.GlobalSuppressions.cs` files compiled into the project |

NuGet audit is not part of the package props (restore does not see them). Set it in `Directory.Build.props`:

```xml
<NuGetAuditMode>all</NuGetAuditMode>
<WarningsNotAsErrors>$(WarningsNotAsErrors);NU1900;NU1901;NU1902;NU1903;NU1904;NU1905</WarningsNotAsErrors>
```

### Analyzers

See [docs/analyzers.md](docs/analyzers.md). Defaults are warnings, so `TreatWarningsAsErrors` makes them blocking. Thresholds are tunable in `.editorconfig` (`qg_max_method_lines = 80`). QG0031/QG0032 are opt-in.

### Architecture tests

```csharp
public sealed class Layering : HexagonalArchitectureTests
{
    protected override IEnumerable<Assembly> Assemblies => [typeof(Shop.Domain.Order).Assembly, typeof(Shop.Application.IOrders).Assembly];
    protected override ArchitectureConfig Config => ArchitectureConfig.Hexagonal("Shop");
}

public sealed class RepoRules : RepositoryRulesTests { }
```

`ArchitectureConfig.FromFile("architecture.json")` loads custom layers: `{ "layers": [ { "name": "Core", "pattern": "^X\\.Core(\\..*)?$", "mayDependOn": [], "forbiddenNamespaces": ["System.IO"] } ] }`. Tests carry `Category=Architecture` for `dotnet test --filter Category=Architecture`.

### Legacy projects (ratchet)

Set `QualityGatesProfile=legacy-ratchet`, put existing violations into `quality/grandfathered/<Project>.GlobalSuppressions.cs` and record the count in `quality/baseline.json` (`{"maxGrandfatheredSuppressions": 42}`). `Suppressions_stay_under_baseline` fails if the count grows or if the baseline is stale, so it can only go down.

### Type cycles

`HexagonalArchitectureTests.Types_have_no_new_cycles` finds cycles between types of the given assemblies (strongly connected components of the type dependency graph; compiler-generated types and nested-to-containing edges are ignored). Known cycles are tolerated through `quality/type-cycles.json` (`ArchitectureConfig.TypeCycleBaselinePath`): `{"cycles": [["Shop.A", "Shop.B"]]}`, one member set per cycle; a cycle that gains a member counts as new. `TypeCycleBaseline.From(TypeCycles.Find(...)).ToJson()` generates the file. Disable with `CheckTypeCycles = false`.

### Refactor mode

`QualityGatesMode=refactor` (property in `Directory.Build.props` or the project, `/p:`, or env `QUALITYGATES_MODE`) does three things for in-scope projects:

1. Stops compiling `quality/grandfathered/*.GlobalSuppressions.cs`, so every grandfathered violation is an error again.
2. Sets `TreatWarningsAsErrors=true`, even when the project sets `false`.
3. Adds `quality.refactor.globalconfig` (`global_level = 100`), which sets all `QualityGates.*` analyzer categories (Design, Errors, Hygiene, Naming, Size, Testing) to error. This also enables the opt-in QG0031/QG0032 and overrides the legacy-ratchet suggestions for QG0033-QG0036, QG0050, QG0051. Curated third-party analyzers and `AnalysisLevel` are not changed (they already follow the profile); an `.editorconfig` entry still wins over the globalconfig.

Use `QualityGatesRefactorScope` to work on a few projects at a time; the rest keep adopt behaviour. Mode and scope are resolved in the package `.targets`, and `TreatWarningsAsErrors` is applied by the `QualityGatesResolveMode` target before compilation.

## Gates and where they run

| Gate | Mechanism | Blocking |
|---|---|---|
| Build warnings as errors, analyzers | Build package | yes, every build |
| Format | `dotnet format --verify-no-changes` | yes (CI job `format`) |
| Architecture | Architecture package tests | yes |
| Tests | `dotnet test` | yes |
| Diff coverage >= 80 | `diff-cover` on cobertura, PR only (merge gate) | yes, merge gate only |
| Mutation | Stryker.NET | never blocks (`continue-on-error`) |

See `.github/workflows/ci.yml` and `samples/HexSample`.

## Develop

```
dotnet build -c Release
dotnet test -c Release
dotnet pack -c Release        # writes artifacts/*.nupkg
```

Sample against the local feed: `dotnet test samples/HexSample/HexSample.sln` (restores from `artifacts/`).

## Releasing

Publishing runs in `.github/workflows/release.yml` and authenticates to nuget.org with Trusted Publishing (OIDC, no API key stored anywhere). The nuget.org trusted publishing policy must match owner `KirillMaster`, repository `quality-gates`, workflow file `release.yml`.

One-time setup: add the repository variable `NUGET_USER` (Settings → Secrets and variables → Actions → Variables) set to your nuget.org profile name.

Per release: bump `Version` in `Directory.Build.props`, update `CHANGELOG.md`, then:

```
git tag v0.1.0 && git push origin v0.1.0
```

The workflow derives the package version from the tag (`v0.1.0` → `0.1.0`) and pushes with `--skip-duplicate`. Manual run: Actions → release → Run workflow, enter `version`.
