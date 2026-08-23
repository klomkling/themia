namespace Themia.Data.Probes;

/// <summary>
/// Thrown when a table a Themia store addresses without a schema does not resolve through the
/// connection's <c>search_path</c>.
/// </summary>
public sealed class SchemaVisibilityException : Exception
{
    /// <summary>Creates the exception with a diagnostic message.</summary>
    /// <param name="message">Message naming the component, the identifier and the remedy.</param>
    public SchemaVisibilityException(string message) : base(message)
    {
    }
}
