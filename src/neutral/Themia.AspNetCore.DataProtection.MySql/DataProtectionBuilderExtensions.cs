using Microsoft.AspNetCore.DataProtection;
using Themia.AspNetCore.DataProtection.Migrations;
using Themia.Data.Migrations;
using Themia.Data.Migrations.MySql;

namespace Themia.AspNetCore.DataProtection.MySql;

/// <summary>DI entry point for the MySQL-backed Data Protection key store.</summary>
public static class DataProtectionBuilderExtensions
{
    /// <summary>
    /// Persists the Data Protection key ring to MySQL and applies the schema migration so the
    /// <c>data_protection_keys</c> table exists.
    /// </summary>
    /// <remarks>
    /// <code>
    /// services.AddDataProtection()
    ///         .SetApplicationName("my-app")
    ///         .PersistKeysToThemiaMySql(connectionString);
    /// </code>
    /// <para><strong>Two applications must not share one table</strong>, and the keys are stored
    /// <strong>unencrypted</strong> — see
    /// <see cref="Themia.AspNetCore.DataProtection.DataProtectionBuilderExtensions.PersistKeysToThemia"/> for
    /// both, including why <c>SetApplicationName</c> is not an isolation boundary.</para>
    /// </remarks>
    /// <param name="builder">The Data Protection builder.</param>
    /// <param name="connectionString">MySQL connection string.</param>
    /// <param name="runMigration">Whether to apply the schema migration during registration. Defaults to true.</param>
    /// <param name="migrationOptions">
    /// Migration-lock settings. Supply a <see cref="ThemiaMigrationOptions.Logger"/> whenever more than one
    /// instance can boot at once — the migration runs during service registration, before the host has built
    /// any logging provider.
    /// </param>
    /// <remarks>
    /// The migration runs here, against <see cref="MySqlMigrationEngine.Adapter"/> directly, rather than
    /// inside the shared <c>PersistKeysToThemia</c> (called below with <c>runMigration: false</c>) — this
    /// engine package already knows its engine at compile time, so it does not need
    /// <c>MigrationEngineRegistry</c> to resolve one (design spec §5.3).
    /// </remarks>
    public static IDataProtectionBuilder PersistKeysToThemiaMySql(
        this IDataProtectionBuilder builder,
        string connectionString,
        bool runMigration = true,
        ThemiaMigrationOptions? migrationOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var result = builder.PersistKeysToThemia(
            new MySqlDataProtectionKeyDialect(connectionString),
            MigrationEngine.MySql,
            connectionString,
            runMigration: false,
            migrationOptions);

        // Register on use, so an adopter who calls only engine-specific methods still has the enum path
        // (a module, SchedulingSchema.Migrate) resolvable — see MigrationEngineRegistry.Add (coord #0126).
        MigrationEngineRegistry.Add(MySqlMigrationEngine.Adapter);

        if (runMigration)
            ThemiaMigrations.Run(MySqlMigrationEngine.Adapter, connectionString, migrationOptions, [typeof(DataProtectionKeysMigration).Assembly]);

        return result;
    }
}
