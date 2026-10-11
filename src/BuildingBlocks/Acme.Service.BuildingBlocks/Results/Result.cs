namespace Acme.Service.BuildingBlocks.Results;

/// <summary>Outcome of an operation without a value: success, or failure with an <see cref="Results.Error"/>.</summary>
public class Result
{
    private static readonly Result _successInstance = new(true, null);

    private readonly Error? _error;

    /// <summary>Initializes a result. A failure must carry an error; a success must not.</summary>
    protected Result(bool isSuccess, Error? error)
    {
        if (isSuccess && error is not null)
        {
            throw new ArgumentException("A successful result cannot carry an error.", nameof(error));
        }

        if (!isSuccess && error is null)
        {
            throw new ArgumentNullException(nameof(error), "A failed result must carry an error.");
        }

        IsSuccess = isSuccess;
        _error = error;
    }

    /// <summary>True when the operation succeeded.</summary>
    public bool IsSuccess { get; }

    /// <summary>True when the operation failed.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>The failure. Throws when the result is a success.</summary>
    public Error Error => _error ?? throw new InvalidOperationException("A successful result has no error.");

    /// <summary>Creates a successful result.</summary>
    public static Result Success() => _successInstance;

    /// <summary>Creates a failed result.</summary>
    public static Result Failure(Error error) => new(false, error);

    /// <summary>Creates a successful result carrying <paramref name="value"/>.</summary>
    public static Result<T> Success<T>(T value) => new(value);

    /// <summary>Creates a failed result of type <typeparamref name="T"/>.</summary>
    public static Result<T> Failure<T>(Error error) => new(error);

    /// <summary>Converts an error into a failed result.</summary>
    public static implicit operator Result(Error error) => Failure(error);
}

/// <summary>Outcome of an operation that produces a <typeparamref name="T"/> on success.</summary>
/// <typeparam name="T">Success value type.</typeparam>
public sealed class Result<T> : Result
{
    private readonly T? _value;

    internal Result(T value)
        : base(true, null)
    {
        _value = value;
    }

    internal Result(Error error)
        : base(false, error)
    {
    }

    /// <summary>The success value. Throws when the result is a failure.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result has no value.");

    /// <summary>Converts a value into a successful result.</summary>
    public static implicit operator Result<T>(T value) => new(value);

    /// <summary>Converts an error into a failed result.</summary>
    public static implicit operator Result<T>(Error error) => new(error);
}
