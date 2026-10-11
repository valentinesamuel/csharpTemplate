using System.Diagnostics.CodeAnalysis;

namespace Acme.Service.BuildingBlocks.Blob;

/// <summary>
/// A validated blob key such as <c>orders/0190.../invoice.pdf</c>. Keys are relative paths of
/// <c>/</c>-separated segments using only <c>[A-Za-z0-9._-]</c>. A leading or trailing <c>/</c>,
/// empty segments, <c>.</c> segments and any <c>..</c> are rejected so that no provider can be
/// tricked into path traversal.
/// </summary>
public sealed record BlobKey
{
    /// <summary>Maximum key length in characters.</summary>
    public const int MaxLength = 1024;

    private BlobKey(string value) => Value = value;

    /// <summary>The key text.</summary>
    public string Value { get; }

    /// <summary>Creates a key, throwing when it is invalid.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a valid key.</exception>
    public static BlobKey Create(string value)
    {
        if (!TryCreate(value, out var key, out var reason))
        {
            throw new ArgumentException($"Invalid blob key: {reason}", nameof(value));
        }

        return key;
    }

    /// <summary>Creates a key when <paramref name="value"/> is valid.</summary>
    public static bool TryCreate(string? value, [NotNullWhen(true)] out BlobKey? key) =>
        TryCreate(value, out key, out _);

    /// <inheritdoc />
    public override string ToString() => Value;

    private static bool TryCreate(string? value, [NotNullWhen(true)] out BlobKey? key, out string reason)
    {
        key = null;

        if (string.IsNullOrEmpty(value))
        {
            reason = "the key is empty.";
            return false;
        }

        if (value.Length > MaxLength)
        {
            reason = $"the key exceeds {MaxLength} characters.";
            return false;
        }

        if (value.StartsWith('/'))
        {
            reason = "the key must not start with '/'.";
            return false;
        }

        if (value.Contains("..", StringComparison.Ordinal))
        {
            reason = "the key must not contain '..'.";
            return false;
        }

        foreach (var c in value)
        {
            if (!IsAllowed(c))
            {
                reason = "the key contains a disallowed character; allowed are A-Z, a-z, 0-9, '.', '_', '-' and '/'.";
                return false;
            }
        }

        foreach (var segment in value.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                reason = "the key must not contain empty or '.' segments.";
                return false;
            }
        }

        reason = string.Empty;
        key = new BlobKey(value);
        return true;
    }

    private static bool IsAllowed(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/';
}
