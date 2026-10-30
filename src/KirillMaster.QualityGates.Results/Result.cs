using System.Diagnostics.CodeAnalysis;

namespace KirillMaster.QualityGates.Results;

public sealed class Result
{
    public static readonly Result Success = new(null);

    private Result(Error? error) => Error = error;

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    public Error? Error { get; }

    public static implicit operator Result(Error error) => new(error ?? throw new ArgumentNullException(nameof(error)));
}

public sealed class Result<T>
{
    private Result(T? value, Error? error)
    {
        Value = value;
        Error = error;
    }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    [MemberNotNullWhen(false, nameof(Value))]
    public bool IsFailure => Error is not null;

    public T? Value { get; }

    public Error? Error { get; }

    public static implicit operator Result<T>(T value) => new(value ?? throw new ArgumentNullException(nameof(value)), null);

    public static implicit operator Result<T>(Error error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));
}
