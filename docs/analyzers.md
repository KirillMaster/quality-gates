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

## Readability (QG0170-QG0187)
All rules default to warning; the consumer escalates. Role detection: `QualityGatesLayer` (`core`/`adapter`/`test`, exposed as the compiler-visible property `build_property.QualityGatesLayer`) or, when it is not `core`/`test`, namespace segments from `qg_core_namespace_segments` (`Core,Domain,Application`; namespaces with `Ports`, `Adapters`, `Runner` or `Tests` segments never count as core).

### qg0170
T1: `if`/`for`/`foreach`/`while`/`do`/`switch` inside a test method (reported once, at the first construct). Use `[Theory]` data or a helper. `qg_test_logic_exempt_assemblies` (`ArchitectureTests,IntegrationTests`, substring match on the assembly name).
### qg0171
T2: `Substitute.For<T>`, `A.Fake<T>`, `Mock.Of<T>`, `new Mock<T>` where `T` is not an interface from an own assembly inside a `Ports` namespace. `qg_port_namespace_segments` (`Ports`), `qg_own_assembly_prefixes` (first dotted segment of the test assembly name), `qg_allowed_mock_types` (`Microsoft.Extensions.Logging.ILogger,Microsoft.Extensions.Options.IOptions`, prefix match on `namespace.Name`).
### qg0172
T3: arrange section longer than `qg_max_arrange_statements` (15). Arrange = top-level statements before the `// Act` marker, or, without a marker, before the statement preceding the first assert (`Assert.*`, `.Should()`, `.Received()`, `Verify`). Tests without an assert and without a marker are skipped.
### qg0173
T5: test method name does not match `qg_test_name_pattern` (default `^[A-Z]\w*_\w+_\w+$`, i.e. `Subject_Scenario_Expectation`). An invalid regex falls back to the default.
### qg0174
N3: identifier token (types, members, parameters, locals; acronyms and tokens up to two letters ignored) is not in the built-in base word list or the glossary. Active only when an AdditionalFile named `glossary.txt` is present (one word per line or comma-separated, `#` comments); simple inflections (`-s`, `-ed`, `-ing`, `-er`, `-ion`, ...) are resolved to the stem. Overrides and interface implementations are skipped. Restrict to new code with the CI changed-lines SARIF filter.
### qg0175
R4: `#region` directive.
### qg0176
Z8: author byline or change-log comment (`Author:`, `created by`, `@author`, `JD 2019-03-04`). XmlDoc is not inspected. TODO hygiene stays in QG0013 and QG0014.
### qg0177
Z15: `#pragma warning disable` without a trailing comment, or `[SuppressMessage]` without a non-empty `Justification` (the `<Pending>` placeholder counts as empty).
### qg0178
G4: method with cognitive complexity above `qg_why_comment_cognitive_threshold` (10, below the QG0007 limit so the comment is demanded before the method becomes an error) and no plain (non-XmlDoc) comment before the method, after its opening brace or before its first statement. Test methods are skipped.
### qg0179
K7: temporary-fix marker in a comment (`qg_temp_markers`: `FIXME,HACK,XXX,temporary,temporarily,workaround,for now,remove after,remove once,remove when,quick fix,kludge`) or any `#pragma warning disable`. `TODO` stays in QG0013. The rule is static; making it an error only for bug-fix pull requests is done by the CI changed-lines filter with the PR kind.
### qg0180
Z10: boolean property, field or method whose first name token is not in `qg_bool_name_prefixes` (`Is,Has,Can,Should,Are,Was,Will,Did,Does,Must,Allow,Allows,Supports,Contains,Exists,Equals,Matches,Try,Starts,Ends,Needs,Requires`; setting replaces the list). Test methods, overrides and interface implementations are skipped.
### qg0181
Z10: identifier ending in a digit after a letter (`user1`). `qg_digit_suffix_allowed` lists legitimate endings (`Sha256`, `Utf8`, `Base64`, ...). Test method names are skipped.
### qg0182
Z10: two parameters/locals of one method (names of at least six characters) that differ only by case or by one character. Singular/plural pairs and digit-suffixed pairs are not reported.
### qg0183
Z10: name lies about its type: `...List/Array/Collection/Set/Queue/Stack` on a non-collection, `...Dictionary` on a non-dictionary, `Is/Has/Can...` on a non-boolean.
### qg0184
L6: composer-versus-worker mix in a Core method. Effect statements are counted after flattening `if`/`try`/`using`/`lock`; guard clauses (`if (...) return/throw`) are neutral; a loop or `switch` is one low-level statement. A statement is low-level when it has a loop, `switch`, indexer, arithmetic, string operation, regex or LINQ with a lambda; it is a composer step when it calls own-assembly code and has no low-level element. Reported when own steps >= `qg_min_own_statements` (3), low-level steps >= `qg_min_low_statements` (3) and the composer share is strictly between `qg_worker_percent` (20) and `qg_composer_percent` (80). Heuristic; measure before escalating.
### qg0185
L7: public method of a type that implements an `I*Handler` interface from `Ports.Input` (`qg_input_port_namespace` is fixed at `Ports.Input`) and contains a low-level statement (see QG0184). Guard clauses and own calls are fine.
### qg0186
L8: stepdown. Private helper declared above the first method that calls it (same type declaration, same file). Types containing test methods are skipped.
### qg0187
R1 (Law of Demeter): chain of more than two member accesses in Core code where at least two intermediate results are behavioral own-assembly classes or interfaces (types with ordinary methods; records, enums, data-only classes, framework types, extension/LINQ steps, fluent steps returning the receiver type and `*Builder` types exempt the chain).
