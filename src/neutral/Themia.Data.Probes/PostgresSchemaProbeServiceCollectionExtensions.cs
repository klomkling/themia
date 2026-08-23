using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Themia.Data.Probes;

/// <summary>Registers the boot-time PostgreSQL schema probe.</summary>
public static class PostgresSchemaProbeServiceCollectionExtensions
{
    /// <summary>
    /// Verifies at host startup that every named table resolves through the connection's
    /// <c>search_path</c>. A table that does not resolve stops the host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="componentName">Names the component in messages, for example <c>Themia.Exceptional</c>.</param>
    /// <param name="connectionFactory">Opens a short-lived connection for the probe.</param>
    /// <param name="tables">
    /// Identifiers exactly as the store's own SQL writes them -- unqualified, quoting included.
    /// </param>
    /// <param name="appliesTo">
    /// Optional predicate deciding whether the probe runs at all. Used by packages that serve more
    /// than one engine and only learn which one at runtime.
    /// </param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddPostgresSchemaProbe(
        this IServiceCollection services,
        string componentName,
        Func<IServiceProvider, IDbConnection> connectionFactory,
        string[] tables,
        Func<IServiceProvider, bool>? appliesTo = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(tables);

        var registration = new PostgresSchemaProbeRegistration(
            componentName, connectionFactory, tables, appliesTo);

        // Deliberately NOT AddHostedService<T>: it registers through TryAddEnumerable, which
        // de-duplicates by implementation type, so a second package's probe would be dropped.
        services.AddSingleton<IHostedService>(sp => new PostgresSchemaProbeHostedService(
            sp,
            sp.GetRequiredService<ILogger<PostgresSchemaProbeHostedService>>(),
            registration));

        return services;
    }
}
