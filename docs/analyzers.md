# Analyzers

Integer thresholds must be non-negative; invalid or negative values fall back to the default. An empty list value disables that list-based rule. All rules default to warning (QG0031 and QG0032 are disabled by default). Override with `dotnet_diagnostic.QGxxxx.severity`.

## Size
### qg0001
Type too long. `qg_max_type_lines` (300).
### qg0002
Method too long. `qg_max_method_lines` (60).
### qg0003
Cyclomatic complexity. `qg_max_complexity` (10).
### qg0004
Nesting depth. `qg_max_nesting` (3).
### qg0005
Too many parameters. `qg_max_parameters` (5).
### qg0006
Too many constructor dependencies. `qg_max_dependencies` (7).
### qg0007
Cognitive complexity (SonarSource): nesting-weighted, flat `switch` counts once. `qg_max_cognitive_complexity` (15).
### qg0008
Method fan-out: distinct methods called on other types within one method. Calls into `System`/`Microsoft`/`mscorlib`/`netstandard`, data types (records, enums, delegates, types without methods) and composition roots are ignored. `qg_max_method_fan_out` (10), `qg_fan_out_ignored_assemblies` (`System,Microsoft,mscorlib,netstandard`).

## Hygiene
### qg0010
Empty catch block.
### qg0011
`throw ex;` instead of `throw;`.
### qg0012
Interpolated string passed to a log method (use message templates).
### qg0013
`TODO` without `#123` or URL.
### qg0014
Banner separator comment.

## Errors
### qg0020
Catch-all that neither rethrows nor logs.
### qg0021
Null returned from a catch block.
### qg0022
Switch over a non-flags enum without a throwing default.
### qg0023
Cast between different enums.
### qg0024
`async void` (event handlers excluded).

## Design
### qg0030
Null returned for a non-nullable collection type.
### qg0031
Boolean flag parameter (opt-in).
### qg0032
out/ref parameter outside `Try*` methods (opt-in).

### qg0033
Pass-through method: public instance method whose body is a single call to a field/constructor-parameter of a type from the same assembly, with simple arguments (at least one and at least half are the method's own parameters; zero-argument forwards are not reported). The receiver may be a field, a constructor or primary-constructor parameter, or an instance property. Exempt: decorators (the target type is an interface the class implements), calls to external types, overrides, members implementing external interfaces, `[Obsolete]`, composition roots, folders in `qg_pass_through_exempt_folders` (`Controllers,Endpoints,Handlers`).
### qg0034
Shallow class: at least `qg_min_shallow_methods` (3) public instance methods and at least `qg_max_pass_through_percent` (75) percent of them are pass-throughs (QG0033).
### qg0035
`partial` class. Not reported for generated files, `[GeneratedCode]`, or classes using source-generator attributes (`GeneratedRegex`, `LoggerMessage`, `JsonSerializable`, `JsonSourceGenerationOptions`, `LibraryImport`).
### qg0036
Service locator: `IServiceProvider.GetService`/`GetRequiredService`/`GetServices`/`GetKeyedService(s)`/`GetRequiredKeyedService`. Allowed in `Program.cs`, top-level statements and assemblies whose name ends with one of `qg_composition_root_suffixes` (`.CompositionRoot`, comma-separated). The same exemption applies to QG0008, QG0033 and QG0034.

## Naming
### qg0050
Type name ends with a vague suffix (last PascalCase token only, so `ExperienceManagerContext` is fine). `qg_banned_name_suffixes` (`Helper,Helpers,Util,Utils,Utility,Utilities,Manager,Misc,Stuff,Thing,Impl,Holder,Common`; setting replaces the list).
### qg0051
Abbreviation token in a type, member, parameter or local name: locals, `for`/`foreach`/`using` variables, deconstruction, `out var`, pattern designations, lambda and local-function parameters and `catch` variables are covered (overrides and interface implementations skipped). `qg_abbreviation_tokens` (`svc,cmd,evt,src,mgr,cfg,tmp,num,req,res,opt,str,cb`; setting replaces the list).

## Testing (xUnit, NUnit, MSTest compilations)
### qg0040
Skipped/ignored test without an issue reference.
### qg0041
`DateTime.Now`, `Thread.Sleep`, `new Random()` in tests. Thresholded by nothing; use `TimeProvider`/seeds.
### qg0042
Too many asserts. `qg_max_asserts` (5).

## Shape, hexagonal and no-hardcode (QG0130-QG0152, default severity warning)
Role detection: a type's namespace segments are matched against configurable lists (comma separated, a setting replaces the default). The `QualityGatesLayer` MSBuild property is compiler-visible (`build_property.QualityGatesLayer`): `test`/`excluded` silences every rule below, `core` treats all types as Core. `QualityGatesLayer` is derived from the project name only when the props are imported; set it in `Directory.Build.props` (or the project) when your layout differs. Assemblies named `*.Tests`/`*.Test` are skipped too.

| Key | Default | Meaning |
|---|---|---|
| `qg_port_namespace_segments` | `Ports` | port interfaces (`Ports.Input`, `Ports.Output`) |
| `qg_core_namespace_segments` | `Core,Domain,Application` | Core (ports excluded) |
| `qg_model_namespace_segments` | `ValueObjects,Model,Domain.Model` | domain model types (dotted entries match a namespace subsequence) |
| `qg_contract_namespace_segments` | `Contracts,Messages,Dtos` | contracts/DTO/messages |
| `qg_adapter_namespace_segments` | `Adapters,Runner` | adapters (exempt from QG0138) |
| `qg_result_type_names` | `Result` | Result type names for QG0139 and QG0147 |

### qg0130
Port method parameter that is a primitive id (`string/int/long/Guid` named `id`, `*Id`, `*Ids`, `*Guid`; `qg_id_name_pattern`) or primitive money (`decimal/double/float` named `amount`, `*Price`, `*Total`...; `qg_money_name_pattern`). Collections of such primitives count. Use a value object (H1).
### qg0131
Only with `QualityGatesLayer=core`: public type (outside ports and contracts) that is not reachable from a port signature (transitively through public members, base types, interfaces). DI extension classes (extension on `IServiceCollection`) and `Program` are exempt (H3).
### qg0132
Non-public class (or any class when `QualityGatesLayer=core`) that is not sealed/abstract/static, has no derived type in the compilation and no virtual/abstract/protected members. Public classes elsewhere are not reported because cross-assembly inheritance is unknown (H3).
### qg0133
Model type with a public setter (`set`, not `init`) or a public non-readonly field (H4).
### qg0134
Hybrid model type: public mutable property plus a public method with a decision point (Z17).
### qg0135
Port exposes `IQueryable`, an EF Core type, or (returns only) a mutable collection (`List`, `Dictionary`, `HashSet`, `IList`, `ICollection`, `IDictionary`, `ISet`, `Collection`, arrays except `byte[]`) (H6).
### qg0136
`services.AddOptions<T>()` without `.ValidateOnStart()` in the chain (or on the variable it is assigned to), or `services.Configure<T>(...)` (cannot validate), where `T` is declared in the current assembly (H8).
### qg0137
Port method returning `Task`/`ValueTask`/`IAsyncEnumerable` without a `CancellationToken` parameter (H9). Propagation is covered by CA2016 (error in `quality.globalconfig`).
### qg0138
`switch` statement/expression over an enum declared in this assembly, written outside the owning feature: the first namespace segment under the assembly root namespace (fallback: the enum namespace without a trailing `Model`/`Contracts`). Enums in `qg_enum_shared_segments` (`Enums,Common,Shared`) and code in `qg_enum_switch_exempt_segments` (`Adapters,Mappers,Mapping,Runner,Controllers,Api`) or the global namespace are exempt (H2 owning feature).
### qg0139
Value object in the model (struct, sealed class or record with exactly one immutable data member) that has a public constructor or no static `Create` (`qg_value_object_factory_names`) returning `Result<T>` (X5).
### qg0140
Type in a contracts namespace that is not a record, has a public `set`, or an `init` property that is neither `required`, nullable nor initialized. Positional records are fine (Y12).
### qg0141
Technology word in a Core/port type or member name (`qg_technology_words`, token match; entries with digits such as `S3` match anywhere). "Entity" is intentionally not in the default dictionary (L1).
### qg0142
Persistence/serialization attribute on a model type or member (`qg_persistence_attributes`: `Column,Key,Table,JsonIgnore,JsonPropertyName,...`) (L2).
### qg0143
Port signature uses a technology type (`qg_port_technology_prefixes`: `System.Net.Http`, `System.IO`, `System.Data`, `System.Text.Json`, `Npgsql`, `Amazon`, `Microsoft.AspNetCore`, ...). IQueryable/EF types are reported as QG0135 (L3).
### qg0144
Property, field or parameter named like money (`qg_money_name_pattern`) typed `double`/`float` (Z11).
### qg0145
`Math.Round`/`MathF.Round`/`decimal.Round` without a `MidpointRounding` argument (Z11). Float equality is covered by S1244.
### qg0146
Port signature type that is not a primitive/enum, not declared in the same assembly, not from an assembly whose name contains an entry of `qg_port_allowed_assemblies` (`Contracts,Results`) and not in `qg_port_type_whitelist` (Task, ValueTask, CancellationToken, IAsyncEnumerable, read-only collections, Guid, date/time types, Nullable, ValueTuple, Uri) (Z18, CN-9).
### qg0147
Non-void instance method that assigns/increments a field or property of its own type or calls a mutating collection method (`Add`, `Remove`, `Clear`, `Push`, `Enqueue`...) on one. Only in Core/Model types. Exempt: `Try*`/`Pop*`/`Dequeue*`/`Next*`/`Increment*`/`Generate*`/`Add*`/`Create*` (`qg_cqs_exempt_prefixes`), `*OrCreate*`/`*OrAdd*`, `Interlocked` bodies, cache fields, methods returning `Result`/`Result<T>`, fluent methods returning their own type, `??=` lazy init, overrides, interface implementations (C28 heuristic).
### qg0150
Numeric literal other than 0, 1, -1 in non-test, non-composition code. Not reported in attributes, enum member values, default parameter values, local `const`, case/pattern labels, indices 0..2 or direct `TimeSpan.From*` arguments (QG0151). A literal in a `const`/`static readonly` member is reported unless the member carries `[Invariant(InvariantKind.X)]`: kinds `SchemaLimit`, `Protocol`, `Math` are always exempt; `Identifier`/`Metric` are exempt only when the member name does not match `qg_tunable_name_regex` (`timeout|ttl|limit|max|min|threshold|retry|interval|delay|duration|size|count|capacity|batch|period|attempt`, case-insensitive).
### qg0151
`TimeSpan.From*(literal)`; same `[Invariant]` exemption.
### qg0152
String literal that is a URL (`scheme://...`), IPv4 address, `localhost` or a lowercase host ending in a suffix from `qg_url_host_suffixes` (`com,net,org,io,ru,dev,cloud,local,internal,aws`); same `[Invariant]` exemption (use `Protocol`). Not reported for `==`/`!=`, `Equals`/`StartsWith`/`EndsWith`/`Contains` operands, `Version` arguments, and IPv4 octets above 255.

The `Invariant` attribute is shipped source-only by the Build package (internal `KirillMaster.QualityGates.InvariantAttribute` and `InvariantKind`, `using KirillMaster.QualityGates;`); set `QualityGatesInvariantAttribute=false` to supply your own (matched by name `Invariant`/`InvariantAttribute`).

`quality.globalconfig` also enables CA1852 (seal internal types), S109 (magic numbers), S1075 (hardcoded URIs) and S2360 (optional parameters, H10) as warnings; `quality.legacy.globalconfig` keeps them (and QG0130-QG0152) at warning; `quality.refactor.globalconfig` raises the `Shape` and `NoHardcode` categories to error.
