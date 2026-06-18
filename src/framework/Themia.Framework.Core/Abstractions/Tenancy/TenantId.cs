using System.Globalization;

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

    // Unchecked construction for callers that have already validated the value (e.g. TryFrom),
    // avoiding a second validation pass. Private so every public construction path stays validating.
    private TenantId(string value, bool alreadyValidated) => Value = value;

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

    /// <summary>
    /// Attempts to create a tenant identifier from string input without throwing.
    /// </summary>
    /// <param name="value">Tenant id value.</param>
    /// <param name="tenantId">The created identifier when valid; otherwise <c>default</c>.</param>
    /// <returns><c>true</c> when <paramref name="value"/> is a valid tenant identifier; otherwise <c>false</c>.</returns>
    public static bool TryFrom(string? value, out TenantId tenantId)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && value.Length >= MinLength
            && value.Length <= MaxLength
            && IsValidFormat(value))
        {
            tenantId = new TenantId(value, alreadyValidated: true);
            return true;
        }

        tenantId = default;
        return false;
    }

    /// <summary>
    /// Creates a tenant identifier from a 32-bit integer, encoded as its invariant decimal string.
    /// </summary>
    /// <param name="value">Tenant id as an integer.</param>
    /// <returns>A <see cref="TenantId"/> whose value is the invariant decimal encoding.</returns>
    public static TenantId From(int value) => new(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Creates a tenant identifier from a 64-bit integer, encoded as its invariant decimal string.
    /// </summary>
    /// <param name="value">Tenant id as a long.</param>
    /// <returns>A <see cref="TenantId"/> whose value is the invariant decimal encoding.</returns>
    public static TenantId From(long value) => new(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Creates a tenant identifier from a GUID, encoded as its hyphenated lowercase "D" format.
    /// </summary>
    /// <param name="value">Tenant id as a GUID.</param>
    /// <returns>A <see cref="TenantId"/> whose value is the "D"-format encoding.</returns>
    public static TenantId From(Guid value) => new(value.ToString("D"));

    /// <summary>Parses the value as a 32-bit integer.</summary>
    /// <returns>The integer value.</returns>
    /// <exception cref="FormatException">Thrown when the value is not a valid Int32.</exception>
    public int AsInt32() =>
        int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new FormatException($"Tenant identifier '{Value}' is not a valid Int32.");

    /// <summary>Parses the value as a 64-bit integer.</summary>
    /// <returns>The long value.</returns>
    /// <exception cref="FormatException">Thrown when the value is not a valid Int64.</exception>
    public long AsInt64() =>
        long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new FormatException($"Tenant identifier '{Value}' is not a valid Int64.");

    /// <summary>Parses the value as a GUID (hyphenated "D" format).</summary>
    /// <returns>The GUID value.</returns>
    /// <exception cref="FormatException">Thrown when the value is not a valid "D"-format GUID.</exception>
    public Guid AsGuid() =>
        Guid.TryParseExact(Value, "D", out var result)
            ? result
            : throw new FormatException($"Tenant identifier '{Value}' is not a valid GUID.");

    /// <summary>Attempts to parse the value as a 32-bit integer.</summary>
    /// <param name="value">The parsed integer when successful; otherwise zero.</param>
    /// <returns><c>true</c> when the value is a valid Int32; otherwise <c>false</c>.</returns>
    public bool TryAsInt32(out int value) =>
        int.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>Attempts to parse the value as a 64-bit integer.</summary>
    /// <param name="value">The parsed long when successful; otherwise zero.</param>
    /// <returns><c>true</c> when the value is a valid Int64; otherwise <c>false</c>.</returns>
    public bool TryAsInt64(out long value) =>
        long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>Attempts to parse the value as a GUID (hyphenated "D" format).</summary>
    /// <param name="value">The parsed GUID when successful; otherwise <see cref="Guid.Empty"/>.</param>
    /// <returns><c>true</c> when the value is a valid "D"-format GUID; otherwise <c>false</c>.</returns>
    public bool TryAsGuid(out Guid value) =>
        Guid.TryParseExact(Value, "D", out value);
}
