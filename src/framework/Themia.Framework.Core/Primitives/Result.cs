namespace Themia.Framework.Core.Primitives;

/// <summary>
/// Represents the outcome of an operation without a return value.
/// </summary>
public readonly struct Result
{
    private Result(bool isSuccess, Error? error)
    {
        IsSuccess = isSuccess;
        Error = error ?? (isSuccess ? null : throw new ArgumentNullException(nameof(error)));
    }

    /// <summary>
    /// Indicates whether the operation succeeded.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Indicates whether the operation failed.
    /// </summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// Error describing the failure when present.
    /// </summary>
    public Error? Error { get; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <returns>A successful result.</returns>
    public static Result Success() => new(true, null);

    /// <summary>
    /// Creates a failed result with an error.
    /// </summary>
    /// <param name="error">Error describing the failure.</param>
    /// <returns>A failed result.</returns>
    public static Result Failure(Error error) => new(false, error);

    /// <summary>
    /// Creates a failed result with an error code and optional message.
    /// </summary>
    /// <param name="code">Error code.</param>
    /// <param name="message">Optional message.</param>
    /// <returns>A failed result.</returns>
    public static Result Failure(string code, string? message = null) => Failure(new Error(code, message));

    /// <summary>
    /// Converts this result into a typed result when successful.
    /// </summary>
    /// <typeparam name="T">Type of the value.</typeparam>
    /// <param name="value">Value to include when successful.</param>
    /// <returns>A typed result.</returns>
    public Result<T> ToResult<T>(T value) =>
        IsSuccess
            ? Result<T>.Success(value)
            : Result<T>.Failure(Error ?? throw new InvalidOperationException("Cannot create a failed result without an error."));
}

/// <summary>
/// Represents the outcome of an operation with a return value.
/// </summary>
public readonly struct Result<T>
{
    private readonly T? value;

    private Result(bool isSuccess, T? value, Error? error)
    {
        IsSuccess = isSuccess;
        this.value = value;
        Error = error ?? (isSuccess ? null : throw new ArgumentNullException(nameof(error)));
    }

    /// <summary>
    /// Indicates whether the operation succeeded.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Indicates whether the operation failed.
    /// </summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// Error describing the failure when present.
    /// </summary>
    public Error? Error { get; }

    /// <summary>
    /// Value produced by a successful operation.
    /// </summary>
    public T Value => IsSuccess
        ? value ?? throw new InvalidOperationException("Value is not set for a successful result.")
        : throw new InvalidOperationException("Cannot access value for a failed result.");

    /// <summary>
    /// Creates a successful result with a value.
    /// </summary>
    /// <param name="value">Returned value.</param>
    /// <returns>A successful result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    public static Result<T> Success(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(true, value, null);
    }

    /// <summary>
    /// Creates a failed result with an error.
    /// </summary>
    /// <param name="error">Error describing the failure.</param>
    /// <returns>A failed result.</returns>
    public static Result<T> Failure(Error error) => new(false, default, error);

    /// <summary>
    /// Creates a failed result with an error code and optional message.
    /// </summary>
    /// <param name="code">Error code.</param>
    /// <param name="message">Optional message.</param>
    /// <returns>A failed result.</returns>
    public static Result<T> Failure(string code, string? message = null) => Failure(new Error(code, message));
}
