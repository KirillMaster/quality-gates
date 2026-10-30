namespace KirillMaster.QualityGates.Analyzers;

public static partial class Descriptors
{
    public const string AllowedResultThrows = "ArgumentNullException,ArgumentException,ArgumentOutOfRangeException,UnreachableException";
    public const string FluentResultMethods = "Bind,Map,Then,MapError,Tap,Ensure,OnSuccess,OnFailure";
    public const string EndpointTypeSuffixes = "Controller,Endpoint,Endpoints";
    public const string ErrorCatalogSuffixes = "Errors";

    public static readonly DiagnosticDescriptor ResultReturnedFromEndpoint = Create("QG0100", "Endpoint returns Result directly", "Endpoint '{0}' returns '{1}'; return 'result.ToHttpResult()' so ErrorType maps to HTTP in one place", "Results");
    public static readonly DiagnosticDescriptor CatchInCore = Create("QG0101", "try/catch in the core layer", "Core code must not catch exceptions; return a Result or let the boundary handle it", "Results");
    public static readonly DiagnosticDescriptor CatchAllInAdapter = Create("QG0102", "Catch-all in adapter", "Catch a specific exception type and convert it to an Error; 'catch ({0})' hides unexpected failures", "Results");
    public static readonly DiagnosticDescriptor ManualRetry = Create("QG0103", "Hand-rolled retry", "'{0}' inside a {1} looks like a manual retry; use a resilience pipeline (AddStandardResilienceHandler / ResiliencePipeline)", "Results");
    public static readonly DiagnosticDescriptor CatchCancellation = Create("QG0104", "OperationCanceledException caught", "Do not catch '{0}' outside the boundary; let cancellation propagate (or rethrow)", "Results");
    public static readonly DiagnosticDescriptor ConsumerCatch = Create("QG0105", "Catch in message consumer", "Consumer '{0}' must not catch; the bus retry/dead-letter policy owns failures", "Results");
    public static readonly DiagnosticDescriptor ResultNotUsed = Create("QG0106", "Result is not used", "{0}", "Results");
    public static readonly DiagnosticDescriptor ErrorLiteralCode = Create("QG0107", "Literal error code", "Error code is a literal; declare it in a static '*Errors' catalog", "Results");
    public static readonly DiagnosticDescriptor ResultMethodThrows = Create("QG0108", "Result method throws", "Method '{0}' returns a Result and must not throw '{1}'; return an Error (only argument guards and UnreachableException are allowed)", "Results");
    public static readonly DiagnosticDescriptor ResultNotNullAnnotated = Create("QG0109", "Result flag without MemberNotNullWhen", "'{0}.{1}' must carry [MemberNotNullWhen] for Value/Error so unchecked access fails nullable analysis", "Results");
    public static readonly DiagnosticDescriptor ProblemDetailsLeak = Create("QG0110", "Exception details in ProblemDetails", "ProblemDetails detail is built from an exception; never expose Exception.Message/StackTrace to clients", "Results");
    public static readonly DiagnosticDescriptor FluentResultChain = Create("QG0111", "Fluent Result chain", "{0}; use early returns instead", "Results");
}
