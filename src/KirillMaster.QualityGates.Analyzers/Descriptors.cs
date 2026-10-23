namespace KirillMaster.QualityGates.Analyzers;

public static class Descriptors
{
    public const int MaxTypeLines = 300;
    public const int MaxMethodLines = 60;
    public const int MaxComplexity = 10;
    public const int MaxNesting = 3;
    public const int MaxParameters = 5;
    public const int MaxDependencies = 7;
    public const int MaxAsserts = 5;

    public static readonly DiagnosticDescriptor TypeTooLong = Create("QG0001", "Type is too long", "Type '{0}' has {1} lines (max {2})", "Size");
    public static readonly DiagnosticDescriptor MethodTooLong = Create("QG0002", "Method is too long", "Member '{0}' has {1} lines (max {2})", "Size");
    public static readonly DiagnosticDescriptor Complexity = Create("QG0003", "Cyclomatic complexity is too high", "Member '{0}' has cyclomatic complexity {1} (max {2})", "Size");
    public static readonly DiagnosticDescriptor Nesting = Create("QG0004", "Nesting is too deep", "Member '{0}' has nesting depth {1} (max {2})", "Size");
    public static readonly DiagnosticDescriptor TooManyParameters = Create("QG0005", "Too many parameters", "Method '{0}' has {1} parameters (max {2})", "Size");
    public static readonly DiagnosticDescriptor TooManyDependencies = Create("QG0006", "Too many constructor dependencies", "Constructor of '{0}' has {1} parameters (max {2})", "Size");
    public static readonly DiagnosticDescriptor EmptyCatch = Create("QG0010", "Empty catch block", "Empty catch block swallows the exception", "Hygiene");
    public static readonly DiagnosticDescriptor ThrowCaught = Create("QG0011", "Rethrow loses stack trace", "Use 'throw;' instead of 'throw {0};'", "Hygiene");
    public static readonly DiagnosticDescriptor InterpolatedLog = Create("QG0012", "Interpolated log message", "Log call '{0}' uses an interpolated string; use a message template", "Hygiene");
    public static readonly DiagnosticDescriptor TodoWithoutIssue = Create("QG0013", "TODO without issue reference", "TODO must reference an issue (#123 or URL)", "Hygiene");
    public static readonly DiagnosticDescriptor BannerComment = Create("QG0014", "Banner comment", "Banner separator comments are not allowed", "Hygiene");
    public static readonly DiagnosticDescriptor SwallowedException = Create("QG0020", "Swallowed exception", "Catch-all block neither rethrows nor logs the exception", "Errors");
    public static readonly DiagnosticDescriptor NullFromCatch = Create("QG0021", "Null returned from catch", "Catch block returns '{0}'; rethrow or return a result type", "Errors");
    public static readonly DiagnosticDescriptor EnumSwitchWithoutThrow = Create("QG0022", "Enum switch without throwing default", "Switch over enum '{0}' must end with a default branch that throws", "Errors");
    public static readonly DiagnosticDescriptor EnumToEnumCast = Create("QG0023", "Enum cast to another enum", "Cast from '{0}' to '{1}' relies on numeric values; map explicitly", "Errors");
    public static readonly DiagnosticDescriptor AsyncVoid = Create("QG0024", "async void", "Method '{0}' is async void; return Task", "Errors");
    public static readonly DiagnosticDescriptor NullCollection = Create("QG0030", "Null collection returned", "Member returning '{0}' returns null; return an empty collection", "Design");
    public static readonly DiagnosticDescriptor BoolFlagParameter = Create("QG0031", "Boolean flag parameter", "Parameter '{0}' is a boolean flag; split the method or pass an enum", "Design", enabled: false);
    public static readonly DiagnosticDescriptor OutRefOutsideTry = Create("QG0032", "out/ref parameter outside Try pattern", "Parameter '{0}' is '{1}'; return a result instead (Try* methods are allowed)", "Design", enabled: false);
    public static readonly DiagnosticDescriptor SkipWithoutIssue = Create("QG0040", "Skipped test without issue", "Skip reason must reference an issue (#123 or URL)", "Testing");
    public static readonly DiagnosticDescriptor NondeterministicTest = Create("QG0041", "Nondeterministic test", "Test code uses '{0}'; inject a clock or seed instead", "Testing");
    public static readonly DiagnosticDescriptor TooManyAsserts = Create("QG0042", "Too many asserts", "Test '{0}' has {1} asserts (max {2})", "Testing");

    private static DiagnosticDescriptor Create(string id, string title, string message, string category, bool enabled = true) =>
        new(id, title, message, "QualityGates." + category, DiagnosticSeverity.Warning, enabled,
            helpLinkUri: "https://github.com/KirillMaster/quality-gates/blob/main/docs/analyzers.md#" + id.ToLowerInvariant());
}
