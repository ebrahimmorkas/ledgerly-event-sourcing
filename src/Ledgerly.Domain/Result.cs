namespace Ledgerly.Domain;

public enum ErrorType
{
    Validation,
    NotFound,
    Conflict,
    FailedPrecondition
}

public sealed record Error(string Code, string Message, ErrorType Type);

public sealed class Result<T>
{
    private readonly T? _value;

    private Result(T value) => _value = value;

    private Result(Error error) => Error = error;

    public Error? Error { get; }

    public bool IsSuccess => Error is null;

    public bool IsFailure => !IsSuccess;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException("A failed result has no value.");

    public static implicit operator Result<T>(T value) => new(value);

    public static implicit operator Result<T>(Error error) => new(error);
}
