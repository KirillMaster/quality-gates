# Changelog

## Unreleased

- .NET 10 SDK, test projects and sample on net10.0; Architecture targets net8.0 and net10.0; CI uses SDK 10.
- Build: `QualityGatesMode` (`adopt`|`refactor`, env `QUALITYGATES_MODE`) and `QualityGatesRefactorScope`; refactor skips grandfathered suppression files, forces `TreatWarningsAsErrors` and adds `quality.refactor.globalconfig` raising all `QualityGates.*` categories to error. Mode and scope are resolved in the targets (project-body values count); scope is an item list of prefixes.
- Build: `AnalysisLevel` pinned (`10.0-all` strict, `10.0-recommended` legacy-ratchet) instead of `latest-*`. README documents NuGet audit settings for consumers.
- Build: props behaviour tests via `dotnet msbuild -getProperty`.
- Build: severities for QG0007, QG0008, QG0033-QG0036, QG0050, QG0051 (warning in strict; `quality.legacy.globalconfig` for legacy-ratchet makes QG0033-QG0036, QG0050, QG0051 suggestions); editorconfig template lists the new thresholds.
- Architecture: `Types_have_no_new_cycles` (type-level SCC detection with a baseline JSON; async state machines and lambdas count towards their declaring type), `TypeCycles`, `TypeCycleBaseline`.
- Analyzers: QG0007 cognitive complexity, QG0008 method fan-out, QG0033 pass-through method, QG0034 shallow class, QG0035 partial class, QG0036 service locator, QG0050 banned name suffix, QG0051 abbreviation token. See docs/analyzers.md for `.editorconfig` keys.

## 0.1.0

- KirillMaster.QualityGates.Build: buildTransitive props/targets, quality.globalconfig, BannedSymbols, editorconfig template, generic Roslyn analyzers QG0001-QG0042, third-party analyzer set.
- KirillMaster.QualityGates.Architecture: ArchUnitNET hexagonal layering test base, config-driven rules, project-graph meta-tests, suppression ratchet.
