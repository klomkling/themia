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
    /// </remarks>
    public static IServiceCollection AddThemiaSequences(
        this IServiceCollection services, Action<SequenceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SequenceOptions();
        configure(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddScoped<ISequenceProvider, SequenceProvider>();
        return services;
    }
}
