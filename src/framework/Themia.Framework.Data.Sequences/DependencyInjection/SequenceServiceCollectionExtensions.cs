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
    /// The container gets a COPY of the configured options, not the instance <paramref
    /// name="configure"/> populated, so a reference the caller kept hold of cannot steer the container's
    /// registration afterwards.
    /// </para>
    /// <para>
    /// Be clear about what that does NOT buy: <see cref="SequenceOptions"/> is a mutable class with
    /// public setters, so the copy is resolvable and mutable too. What makes runtime mutation harmless
    /// is that <c>SequenceProvider</c> snapshots the connection string and the dialect together in its
    /// constructor — an already-built provider is unaffected. A provider built LATER, in a new scope,
    /// would pick up the mutated value, and <see cref="SequenceOptions.Validate"/> would not re-run. An
    /// immutable holder type would close that; it is deliberately not done here.
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
