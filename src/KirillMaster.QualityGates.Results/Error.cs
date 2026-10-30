namespace KirillMaster.QualityGates.Results;

public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    Unexpected,
}

public sealed record Error(string Code, string Message, ErrorType Type, IReadOnlyList<Error>? Details = null)
{
    public static Error Validation(string code, string message, IReadOnlyList<Error>? details = null) => new(code, message, ErrorType.Validation, details);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error Unexpected(string code, string message) => new(code, message, ErrorType.Unexpected);
}
