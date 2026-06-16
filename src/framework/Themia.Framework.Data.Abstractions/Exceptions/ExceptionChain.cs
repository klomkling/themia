namespace Themia.Framework.Data.Abstractions.Exceptions;

/// <summary>
/// Helper for walking an exception and its inner-exception chain. Provider-specific
/// <see cref="ISqlExceptionInterpreter"/> implementations live in separate assemblies but share the same
/// "does any node in the chain match this predicate?" logic; this keeps that single walk in one place.
/// </summary>
public static class ExceptionChain
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="exception"/> or any exception in its
    /// inner-exception chain satisfies <paramref name="predicate"/>.
    /// </summary>
    /// <param name="exception">The exception to inspect; may be <see langword="null"/>.</param>
    /// <param name="predicate">The per-node test applied to each exception in the chain.</param>
    public static bool Any(Exception? exception, Func<Exception, bool> predicate)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (predicate(current))
            {
                return true;
            }
        }

        return false;
    }
}
