# Changelog

## 0.4.2

- Build: fix warning `InvalidGlobalSectionName` in every consumer build. `quality.globalconfig` no longer contains glob sections (global analyzer configs allow absolute paths only, so the X1 runner relaxation was ignored). The CA1031 relaxation for runners now lives in `quality.runners.globalconfig` (global_level 10), added by the props when the project name is `Runner*` or `*.Runner*`. Fixture build tests cover both.

## 0.5.0

- Cli: new `KirillMaster.QualityGates.Cli` dotnet tool (`qg`), the generic gates CLI moved into core: commands `pr`, `mutation-diff`, `mutation-ratchet`, `nightly`, `audit`, `grandfather`, `baseline`. Domain, application and adapter layers live in one assembly (`Domain`, `Application`, `Adapters`, `Commands` namespaces). Organisation-specific analyzers, MSBuild package and CI pipeline scripts are not part of it. Tests ported (unit, fixtures, git integration).

## 0.4.1

- Analyzers: QG0190 (Z5 ExhaustiveClosed): a catch-all arm on a `switch` over `bool`, bool tuples or a closed sealed hierarchy must throw. Enums stay with QG0022 (distinct case, no duplicate reports). Warning by default; added to the strict and legacy-ratchet globalconfigs.
- Docs: QG0187 (Demeter) stays a warning; QG0022/QG0190 scope documented.

## 0.4.0

- Analyzers: QG0188 (`lock` in Core) and QG0189 (static non-readonly field or static readonly mutable collection in Core), Z1 NoSharedMutableState; warning by default, Core layer only, test/excluded assemblies skipped, no diagnostics on symbols without source location.
- Analyzers: `DesignAnalyzer` (QG0032 and the bool-flag rule) no longer indexes `Locations[0]` of parameters without a source location (AD0001 on synthesized entry points); top-level statements test added.
- Build: B1 mirror of the TG bans, opt-in with `QualityGatesDesignBans=true` (RS0030 is an error, consumers grandfather legacy hits first): `BannedSymbols.All.txt` (MediatR, AutoMapper, Scrutor, LanguageExt, `Debug.Assert`; core, adapter and test layers), `BannedSymbols.Core.txt` (DateTime/DateTimeOffset clock, Console, HttpClient, DbContext, AWS/Npgsql/Newtonsoft; core), `BannedSymbols.CoreDesign.txt` (service locator, IServiceProvider/IServiceCollection, Guid.NewGuid, Random.Shared, Interlocked/Monitor/Thread/Task.Run, AspNetCore/MassTransit; core), `BannedSymbols.Model.txt` (Options/Logging/Configuration; core projects ending with `.Model` or `QualityGatesCoreModel=true`), `BannedSymbols.Test.txt` (real clock and delays in tests). The always-on `BannedSymbols.txt` is unchanged.
- Build: `quality.globalconfig` adds as warnings S3776, S2386, S134, S126, S1659, S1244, S2139, S108, S2486, S2221, CA1002, CA2227, CA1051, CA1008, CA2254, CA1727, CA1031 (off in `Runner*` paths), CS8509, xUnit1004, IDE0051, IDE0052, IDE0072, IDE0011, IDE1006, IDE0079; QG0188/QG0189 are explicit warnings in the strict and legacy-ratchet profiles.

## 0.3.1

- Analyzers: fix AD0001 `IndexOutOfRangeException` in `UsageAnalyzer` (QG0143 money-as-float check) on projects with top-level statements (ASP.NET Core `Program.cs`): the synthesized entry point has a parameter (`args`) without a source location; such parameters, properties and fields are now skipped.

## 0.3.0

- Analyzers (B5): QG0130-QG0147 shape/hexagonal rules (primitive ids/money in ports, public Core types, unsealed classes, public setters, hybrid model types, leaky ports, Options without ValidateOnStart, port methods without CancellationToken, enum switch outside owning feature, value object shape, contract records, technology words, persistence attributes, technology types in ports, float money, Round without MidpointRounding, port type whitelist, command/query separation) and QG0150-QG0152 no-hardcode rules (numbers, TimeSpan.From*, URLs/hosts) with a source-only `[Invariant(InvariantKind.X)]`; `QualityGatesLayer` is compiler-visible; globalconfig enables CA1852, S109, S1075, S2360. See docs/analyzers.md.
- Architecture: opt-in hexagonal slice rules via `HexagonalProfile.ExtraRules` (FlowDirection, NoLevelSkipping, HandlerIsolation, ResultAtBoundary, OnePortPerUseCase, FolderAllowlist, FeatureMirror, HandlerReferenced), module rule StableDependencies (warning until `EnforceStableDependencies`), helpers `ErrorConventions` (X1 single exception handler, X9 unique error codes), `EfConcurrency` (Z13), `OpenApiSnapshot` / `MessageContractSnapshot` with breaking-change diff (H7, Y7, Y8). New package KirillMaster.QualityGates.Testing.AspNetCore (host-start smoke, route coverage, single exception handler registered; Z14, X1).
- Analyzers: readability batch QG0170-QG0187 (warning by default): test hygiene QG0170-QG0173 (no logic, mock own ports, arrange size, behavior names), N3 unknown word QG0174 (glossary AdditionalFile), #region QG0175, bylines QG0176, suppression justification QG0177, why-comment QG0178, temp markers QG0179, naming extensions QG0180-QG0183, abstraction heuristics QG0184-QG0187 (composer vs worker, handlers, stepdown, Demeter). QG0050 gains `qg_banned_name_suffix_allowlist`. Build exposes `QualityGatesLayer` as a compiler-visible property. See docs/analyzers.md.
- Results: new packages `KirillMaster.QualityGates.Results` (`Result`, `Result<T>`, `Error`, `ErrorType`; netstandard2.0 and net10.0; implicit conversions, `MemberNotNullWhen`, no fluent API) and `KirillMaster.QualityGates.Results.AspNetCore` (`ToHttpResult()` ProblemDetails mapping, `ProblemExceptionHandler` base that never exposes exception details); both packed and published by the release workflow.
- Results rules: types are matched by full name (`KirillMaster.QualityGates.Results.*`, extra via `qg_result_type_names`); QG0106 and QG0108 are errors in the strict profile, all QG0100-QG0111 are at least warnings in every profile (quality.globalconfig, quality.legacy.globalconfig).
- Analyzers: QG0100-QG0111 (endpoint returns Result, catch in core, catch-all in adapter, manual retry, caught cancellation, catch in consumer, unused Result, literal error code, throwing Result method, Result without `MemberNotNullWhen`, exception text in ProblemDetails, fluent Result chains). `QualityGatesLayer` is now a compiler-visible property.
- Build: every QG rule is at least a warning in every profile (legacy-ratchet no longer demotes QG0033-QG0036, QG0050, QG0051 to suggestion), so the CI new-code gate sees violations on changed lines; old code is covered by grandfathered suppressions.
- Build: base `AnalysisLevel` is 9.0-* when MSBuild < 18 (SDK 9), 10.0-* on SDK 10 (fixes IDE0055/IDE0005 errors on SDK 9 consumers).
- Build: `QualityGatesLayer=core` is also derived for `*.Core`, `*.Ports.Input`, `*.Ports.Output` projects.

## 0.2.0

- Architecture: generic module engine (`ModulesConfig`, `ModuleRules`: FeatureIsolation soft/strict, SharedPurity, SharedCap, CompositionCap, ModelIsLeaf, ModuleFanOut, OrchestrationThin) and hexagonal profile (`HexagonalProfile`, `HexagonalRules`: AdapterReferencesAdapter, AdapterDirection, RunnerHostReferences, CoreNoContainer, PortWidth, NoMirrorTypes, PortNoTechnologyTypes), both opt in on `HexagonalArchitectureTests` with a shared `RuleBaseline` JSON (new violations fail, `FailOnStaleBaseline` makes it shrink-only).
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
