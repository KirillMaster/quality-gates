# quality-gates

Reusable quality gates for modern .NET (net8 / net9). MIT licensed.

| Package | Purpose |
|---|---|
| `KirillMaster.QualityGates.Build` | Build-only package: warnings-as-errors, `AnalysisLevel latest-all`, nullable, curated third-party analyzers (Sonar, Meziantou, Roslynator, VS.Threading, IDisposableAnalyzers, BannedApiAnalyzers), own Roslyn analyzers `QG0xxx`, `quality.globalconfig`, `BannedSymbols.txt`, `.editorconfig` template, grandfathered suppressions. |
| `KirillMaster.QualityGates.Architecture` | ArchUnitNET-based test base for hexagonal layering, config-driven layers, namespace-cycle check, repository meta-tests (locked settings, central package versions, suppression ratchet, every project covered). |

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
| `QualityGatesProfile` | `strict` | `strict` = `latest-all`; `legacy-ratchet` = `latest-recommended` plus grandfathered suppressions |
| `QualityGatesLayer` | derived | `core` (`*.Domain`, `*.Application`), `test` (`*.Tests`, relaxed test config), `adapter` |
| `QualityGatesBannedSymbols` | `true` | `false` drops `BannedSymbols.txt` |
| `QualityGatesGrandfatheredDir` | `<repo>/quality/grandfathered/` | folder with `<Project>.GlobalSuppressions.cs` files compiled into the project |

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

Bump `Version` in `Directory.Build.props`, update `CHANGELOG.md`, then:

```
dotnet pack -c Release
dotnet nuget push "artifacts/*.nupkg" --source https://api.nuget.org/v3/index.json --api-key <NUGET_API_KEY> --skip-duplicate
```

Tag the release: `git tag v0.1.0 && git push --tags`.
