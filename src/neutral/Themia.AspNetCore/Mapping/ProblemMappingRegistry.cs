namespace Themia.AspNetCore.Mapping;

/// <summary>Maps consumer exception types to <see cref="ProblemMapping"/>s, registered via
/// <c>AddThemiaProblemMapping</c>. Lookup walks an exception's type then its base types, so a consumer
/// can register one mapping for a base exception.</summary>
public sealed class ProblemMappingRegistry
{
    private readonly Dictionary<Type, Func<Exception, ProblemMapping>> map = new();

    /// <summary>Registers a mapper for <typeparamref name="TException"/> (replaces any existing one).</summary>
    /// <typeparam name="TException">The consumer exception type to map.</typeparam>
    /// <param name="mapper">Produces a <see cref="ProblemMapping"/> from an instance of the exception.</param>
    public void Register<TException>(Func<TException, ProblemMapping> mapper) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(mapper);
        map[typeof(TException)] = ex => mapper((TException)ex);
    }

    /// <summary>Finds a mapping for <paramref name="exception"/> by walking its type hierarchy.</summary>
    /// <param name="exception">The thrown exception to map.</param>
    /// <param name="mapping">The resolved mapping when a registration matches; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when a mapping was found.</returns>
    public bool TryMap(Exception exception, out ProblemMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var t = exception.GetType(); t is not null && t != typeof(object); t = t.BaseType)
        {
            if (map.TryGetValue(t, out var mapper)) { mapping = mapper(exception); return true; }
        }
        mapping = null!;
        return false;
    }
}
