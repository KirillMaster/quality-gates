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

## Results and error flow (QG0100-QG0111)
Rules for the `KirillMaster.QualityGates.Results` style (early returns, no fluent railways). All default to warning. Layer comes from namespace/assembly segments (rightmost match wins), falling back to the `QualityGatesLayer` MSBuild property (compiler-visible as `build_property.QualityGatesLayer`); the `test` layer is skipped. Keys: `qg_core_namespace_segments` (`Core,Domain,Application,Ports`), `qg_adapter_namespace_segments` (`Adapters,Adapter,Runner,Runners,Infrastructure`), `qg_test_namespace_segments`, `qg_result_type_names` / `qg_error_type_names` (comma list of full type names ADDED to the defaults `KirillMaster.QualityGates.Results.Result`/`Result<T>` and `...Error`; same-named foreign types are ignored). Severity: strict profile makes QG0106 and QG0108 errors, everything else in this section is a warning in every profile (legacy-ratchet: all warnings).
### qg0100
X2. A public endpoint method returns a Result (also inside `Task`): any public method of a `ControllerBase`/`[ApiController]` type, a method of a type ending with `qg_endpoint_type_suffixes` (`Controller,Endpoint,Endpoints`) that has `[Route]`/`[Http*]`, or a lambda or method group passed to `MapGet/MapPost/MapPut/MapDelete/MapPatch/MapMethods`. Return `result.ToHttpResult()` instead.
### qg0101
C29 NoCatchInCore. Any `catch` in a core-layer type, except exception-handler boundaries (`IExceptionHandler`, `*ExceptionHandler`, `*Middleware`, `*ExceptionFilter` via `qg_exception_boundary_suffixes`, composition root).
### qg0102
X3. Adapter layer only (not unknown layer; hosted services are boundaries): unfiltered `catch`, `catch (Exception)` or `catch (SystemException)` without a `throw;`. Catch the specific exception and convert it to an `Error`.
### qg0103
X4. `Task.Delay` / `Thread.Sleep` inside a catch block or inside a loop whose body has a `try/catch` (retry shape). Plain polling or throttling loops, `IHostedService`/`BackgroundService` types and the composition root are not reported.
### qg0104
X6. `OperationCanceledException`/`TaskCanceledException` caught outside a boundary, hosted service or a `throw;` rethrow.
### qg0105
X7. Any `catch` in a type implementing a consumer interface (`qg_consumer_interfaces`, default `MassTransit.IConsumer`; simple or namespace-qualified names).
### qg0106
X8. A returned Result is discarded: expression statement (also `await`), `_ = Call()`, void-lambda body, un-awaited `Task<Result>`/`ValueTask<Result>` statements, or a Result local (declaration, `out var`, pattern) that is never read; assignments alone do not count.
### qg0107
X9. `new Error(...)` or a static `Error` factory called with a literal or interpolated `code` outside a static class ending with `qg_error_catalog_suffixes` (`Errors`).
### qg0108
X10. A method returning a Result (or `Task<Result>`) throws. Also local functions and getters. Allowed: any `ArgumentException` subclass, `qg_result_allowed_throws` (default `ArgumentNullException,ArgumentException,ArgumentOutOfRangeException,UnreachableException`), `ThrowIf*` guards, bare `throw;`, lambdas.
### qg0109
Z4. A source-declared Result type has `IsSuccess`/`IsFailure` without `[MemberNotNullWhen]` while exposing `Value`/`Error`; unchecked access then compiles silently.
### qg0110
Z4. `ProblemDetails.Detail` or `Problem(detail: ...)` is built from an exception (`Message`, `StackTrace`, `ToString()`, interpolation). `qg_problem_details_safe_exceptions` lists exception types whose text is user-safe.
### qg0111
NoFluentResultChains. `Bind/Map/Then/MapError/Tap/Ensure/OnSuccess/OnFailure` (+`Async`; `qg_fluent_result_methods`) when the receiver (or the first argument of an extension method) is a Result, and LINQ query syntax over a Result.
