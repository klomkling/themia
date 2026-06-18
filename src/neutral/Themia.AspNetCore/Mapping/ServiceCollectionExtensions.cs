using Microsoft.Extensions.DependencyInjection;
using Themia.AspNetCore.Mapping;

namespace Themia.AspNetCore;

/// <summary>DI helpers for Themia's ProblemDetails consumer-exception mapping.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Maps a consumer exception type to a ProblemDetails response, so
    /// <c>ProblemDetailsMiddleware</c> surfaces exceptions the consumer owns. Call once per type.</summary>
    /// <typeparam name="TException">The consumer exception type to map.</typeparam>
    /// <param name="services">The service collection to register the mapping into.</param>
    /// <param name="mapper">Produces a <see cref="ProblemMapping"/> from an instance of the exception.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddThemiaProblemMapping<TException>(
        this IServiceCollection services, Func<TException, ProblemMapping> mapper) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(mapper);
        var registry = (services.LastOrDefault(d => d.ServiceType == typeof(ProblemMappingRegistry))?.ImplementationInstance as ProblemMappingRegistry)
            ?? AddNew(services);
        registry.Register(mapper);
        return services;

        static ProblemMappingRegistry AddNew(IServiceCollection s)
        {
            var r = new ProblemMappingRegistry();
            s.AddSingleton(r);
            return r;
        }
    }
}
