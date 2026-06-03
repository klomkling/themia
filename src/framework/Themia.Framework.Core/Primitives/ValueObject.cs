namespace Themia.Framework.Core.Primitives;

/// <summary>
/// Base type for value objects that compare by their component values.
/// </summary>
/// <remarks>
/// Value objects are immutable objects that are defined by their attribute values rather than identity.
/// Two value objects with the same component values are considered equal.
///
/// Null component handling:
/// - Null values in components are supported and handled correctly
/// - Two value objects with null components in the same positions are considered equal
/// - GetHashCode properly handles null components without throwing exceptions
/// </remarks>
public abstract class ValueObject : IEquatable<ValueObject>
{
    /// <summary>
    /// Provides the sequence of components that participate in equality.
    /// </summary>
    /// <returns>Enumerable of components to compare. Null values are permitted.</returns>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ValueObject other && Equals(other);

    /// <inheritdoc />
    public bool Equals(ValueObject? other)
    {
        if (other is null || other.GetType() != GetType())
        {
            return false;
        }

        return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();

        foreach (var component in GetEqualityComponents())
        {
            hash.Add(component);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Determines whether two value objects are equal.
    /// </summary>
    /// <param name="left">Left value object.</param>
    /// <param name="right">Right value object.</param>
    /// <returns>True when equal.</returns>
    public static bool operator ==(ValueObject? left, ValueObject? right) =>
        Equals(left, right);

    /// <summary>
    /// Determines whether two value objects are not equal.
    /// </summary>
    /// <param name="left">Left value object.</param>
    /// <param name="right">Right value object.</param>
    /// <returns>True when not equal.</returns>
    public static bool operator !=(ValueObject? left, ValueObject? right) =>
        !Equals(left, right);
}
