# Changelog

## Unreleased

- Analyzers: QG0007 cognitive complexity, QG0008 method fan-out, QG0033 pass-through method, QG0034 shallow class, QG0035 partial class, QG0036 service locator, QG0050 banned name suffix, QG0051 abbreviation token. See docs/analyzers.md for `.editorconfig` keys.

## 0.1.0

- KirillMaster.QualityGates.Build: buildTransitive props/targets, quality.globalconfig, BannedSymbols, editorconfig template, generic Roslyn analyzers QG0001-QG0042, third-party analyzer set.
- KirillMaster.QualityGates.Architecture: ArchUnitNET hexagonal layering test base, config-driven rules, project-graph meta-tests, suppression ratchet.
