# Analyzers

All rules default to warning (QG0031 and QG0032 are disabled by default). Override with `dotnet_diagnostic.QGxxxx.severity`.

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

## Testing (xUnit, NUnit, MSTest compilations)
### qg0040
Skipped/ignored test without an issue reference.
### qg0041
`DateTime.Now`, `Thread.Sleep`, `new Random()` in tests. Thresholded by nothing; use `TimeProvider`/seeds.
### qg0042
Too many asserts. `qg_max_asserts` (5).
