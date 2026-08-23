using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Themia.Data.Migrations;
using Themia.Data.Probes;

namespace Themia.AspNetCore.DataProtection.PostgreSql;

/// <summary>DI entry point for the PostgreSQL-backed Data Protection key store.</summary>
public static class DataProtectionBuilderExtensions
{
    /// <summary>
    /// Persists the Data Protection key ring to PostgreSQL and applies the schema migration so the
    /// <c>data_protection_keys</c> table exists.
    /// </summary>
    /// <remarks>
    /// <code>
    /// services.AddDataProtection()
    ///         .SetApplicationName("my-app")
    ///         .PersistKeysToThemiaPostgres(connectionString);
    /// </code>
    /// <para><strong>Two applications must not share one table</strong>, and the keys are stored
    /// <strong>unencrypted</strong> — see
    /// <see cref="Themia.AspNetCore.DataProtection.DataProtectionBuilderExtensions.PersistKeysToThemia"/> for
    /// both, including why <c>SetApplicationName</c> is not an isolation boundary.</para>
    /// </remarks>
    /// <param name="builder">The Data Protection builder.</param>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="runMigration">Whether to apply the schema migration during registration. Defaults to true.</param>
    /// <param name="migrationOptions">
    /// Migration-lock settings. Supply a <see cref="ThemiaMigrationOptions.Logger"/> whenever more than one
    /// instance can boot at once — the migration runs during service registration, before the host has built
    /// any logging provider.
    /// </param>
    public static IDataProtectionBuilder PersistKeysToThemiaPostgres(
        this IDataProtectionBuilder builder,
        string connectionString,
        bool runMigration = true,
        ThemiaMigrationOptions? migrationOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        builder.PersistKeysToThemia(
            new PostgresDataProtectionKeyDialect(connectionString),
            MigrationEngine.Postgres,
            connectionString,
            runMigration,
            migrationOptions);

        // Boot-time check: the migration writes public.data_protection_keys, but this store reads
        // unqualified and follows search_path. A mismatch otherwise surfaces on the first protector,
        // which is a user request, not startup.
        builder.Services.AddPostgresSchemaProbe(
            "Themia.AspNetCore.DataProtection",
            _ =>
            {
                var connection = new NpgsqlConnection(connectionString);
                connection.Open();
                return connection;
            },
            ["data_protection_keys"]);

        return builder;
    }
}
