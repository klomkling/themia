using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Themia.Framework.Data.Sequences;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the Themia sequence allocator.</summary>
public static class SequenceServiceCollectionExtensions
{
    /// <summary>Adds <see cref="ISequenceProvider"/> to the container.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the connection string and engine.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The resulting options are not usable.</exception>
    /// <remarks>
    /// Options are validated HERE rather than at the first allocation, so a connection-string typo stops
    /// the deployment instead of surfacing as a failed invoice hours later.
    /// <para>
    /// Scoped, because it reads the ambient <c>ITenantContext</c>. The provider holds no connection
    /// between calls — it opens one per allocation, by design.
    /// </para>
    /// <para>
    /// The container gets a frozen COPY of the configured options, not the instance <paramref
    /// name="configure"/> populated. <see cref="SequenceOptions"/> is a mutable class with public
    /// setters; registering the caller's own instance as a singleton would let anyone who later resolves
    /// <see cref="SequenceOptions"/> from the container mutate it after startup — e.g. changing
    /// <c>ConnectionString</c> without ever re-running <see cref="SequenceOptions.Validate"/>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddThemiaSequences(
        this IServiceCollection services, Action<SequenceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SequenceOptions();
        configure(options);
        options.Validate();

        var frozen = new SequenceOptions
        {
            ConnectionString = options.ConnectionString,
            Engine = options.Engine,
            Dialect = options.Dialect,
        };

        services.TryAddSingleton(frozen);
        services.TryAddScoped<ISequenceProvider, SequenceProvider>();
        return services;
    }
}
