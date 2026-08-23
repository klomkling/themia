using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Themia.Data.Probes;

/// <summary>
/// Runs the schema probe once at host startup. Follows the advisory pattern used by
/// Themia.Scheduling, with one difference: this one refuses rather than advises, so a table that
/// does not resolve stops the host instead of surfacing on a user's first request.
/// </summary>
internal sealed class PostgresSchemaProbeHostedService(
    IServiceProvider rootProvider,
    ILogger<PostgresSchemaProbeHostedService> logger,
    PostgresSchemaProbeRegistration registration) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        List<ProbeResult> results;
        try
        {
            using var scope = rootProvider.CreateScope();
            var provider = scope.ServiceProvider;

            if (registration.AppliesTo is not null && !registration.AppliesTo(provider))
            {
                return Task.CompletedTask;
            }

            using var connection = registration.ConnectionFactory(provider);
            results = registration.Tables
                .Select(table => PostgresSchemaProbe.Probe(connection, table))
                .ToList();
        }
        catch (Exception ex)
        {
            // Availability/configuration faults here -- unreachable database, a faulty appliesTo
            // predicate, a bad connection factory -- are not evidence of a schema problem. Throwing
            // would newly couple host startup to database uptime for consumers that do not migrate on
            // boot. Only the SchemaVisibilityException thrown below, from a probe that actually ran, is
            // allowed to stop the host.
            logger.LogWarning(
                ex,
                "{Component}: could not run the schema probe, so schema agreement was not verified. "
                + "This is not evidence of a schema problem.",
                registration.ComponentName);
            return Task.CompletedTask;
        }

        for (var i = 0; i < results.Count; i++)
        {
            var table = registration.Tables[i];
            var result = results[i];

            if (result.ResolvedSchema is null)
            {
                throw new SchemaVisibilityException(
                    $"{registration.ComponentName}: table {table} does not resolve through this "
                    + $"connection's search_path"
                    + (result.PublicCopyExists
                        ? ", although a table of that name exists in 'public', which is where Themia's "
                          + "migrations create it. Put 'public' on the search_path, or point the "
                          + "connection at the schema that holds the table."
                        : " and no table of that name exists in 'public' either. Run the migrations, "
                          + "or point the connection at the schema that holds the table."));
            }

            if (!string.Equals(result.ResolvedSchema, "public", StringComparison.Ordinal)
                && result.PublicCopyExists)
            {
                logger.LogWarning(
                    "{Component}: this connection resolves {Table} in schema '{ResolvedSchema}', but a "
                    + "table of that name also exists in 'public', which is where Themia's migrations "
                    + "write. A later Themia migration would alter the copy this store does not read. "
                    + "The match is by name, so an unrelated table of the same name in 'public' "
                    + "produces this warning too.",
                    registration.ComponentName,
                    table,
                    result.ResolvedSchema);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
