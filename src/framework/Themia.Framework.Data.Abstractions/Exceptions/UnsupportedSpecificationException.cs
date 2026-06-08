namespace Themia.Framework.Data.Abstractions.Exceptions;

/// <summary>
/// Thrown when a specification's expression tree uses a construct the Dapper translator does not support
/// (e.g. nested navigation, joins, projections, or an unsupported method). Drop to provider-native
/// (tier-2 SqlKata) for such queries.
/// </summary>
public sealed class UnsupportedSpecificationException : Exception
{
    /// <summary>Creates the exception with a descriptive message.</summary>
    public UnsupportedSpecificationException(string message) : base(message) { }
}
