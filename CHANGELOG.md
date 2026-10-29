# Changelog

## Unreleased

- .NET 10 SDK, test projects and sample on net10.0; Architecture targets net8.0 and net10.0; CI uses SDK 10.
- Build: `QualityGatesMode` (`adopt`|`refactor`, env `QUALITYGATES_MODE`) and `QualityGatesRefactorScope`; refactor skips grandfathered suppression files and forces `TreatWarningsAsErrors`.
- Build: `AnalysisLevel` pinned (`10.0-all` strict, `10.0-recommended` legacy-ratchet) instead of `latest-*`. README documents NuGet audit settings for consumers.
- Build: props behaviour tests via `dotnet msbuild -getProperty`.
- Architecture: `Types_have_no_new_cycles` (type-level SCC detection with a baseline JSON), `TypeCycles`, `TypeCycleBaseline`.

## 0.1.0

- KirillMaster.QualityGates.Build: buildTransitive props/targets, quality.globalconfig, BannedSymbols, editorconfig template, generic Roslyn analyzers QG0001-QG0042, third-party analyzer set.
- KirillMaster.QualityGates.Architecture: ArchUnitNET hexagonal layering test base, config-driven rules, project-graph meta-tests, suppression ratchet.
