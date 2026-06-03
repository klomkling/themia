namespace Themia.Framework.Core.Abstractions.Tenancy;

/// <summary>
/// Strongly typed tenant identifier.
/// </summary>
public readonly record struct TenantId
{
    /// <summary>
    /// Maximum allowed length for a tenant identifier.
    /// </summary>
    public const int MaxLength = 100;

    /// <summary>
    /// Minimum allowed length for a tenant identifier.
    /// </summary>
    public const int MinLength = 1;

    /// <summary>
    /// Creates a tenant identifier from the provided value.
    /// </summary>
    /// <param name="value">Tenant id value.</param>
    /// <exception cref="ArgumentException">Thrown when the value is invalid.</exception>
    public TenantId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(value));

        if (value.Length > MaxLength)
        {
            throw new ArgumentException(
                $"Tenant identifier cannot exceed {MaxLength} characters. Provided: {value.Length}",
                nameof(value));
        }

        if (value.Length < MinLength)
        {
            throw new ArgumentException(
                $"Tenant identifier must be at least {MinLength} character. Provided: {value.Length}",
                nameof(value));
        }

        // Validate characters (alphanumeric, hyphens, underscores)
        if (!IsValidFormat(value))
        {
            throw new ArgumentException(
                "Tenant identifier can only contain alphanumeric characters, hyphens, and underscores.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>
    /// Gets the tenant identifier value.
    /// </summary>
    public string Value { get; } = string.Empty;

    /// <summary>
    /// Validates the format of a tenant identifier.
    /// </summary>
    /// <param name="value">The value to validate.</param>
    /// <returns>True if the format is valid; otherwise, false.</returns>
    private static bool IsValidFormat(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>
    /// Creates a tenant identifier from string input, returning null when empty.
    /// </summary>
    /// <param name="value">Tenant id value.</param>
    /// <returns>A <see cref="TenantId"/> when provided, otherwise null.</returns>
    public static TenantId? From(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new TenantId(value);
}
