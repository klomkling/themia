using Microsoft.AspNetCore.DataProtection;
using Themia.Data.Migrations;

namespace Themia.AspNetCore.DataProtection.SqlServer;

/// <summary>DI entry point for the SQL Server-backed Data Protection key store.</summary>
public static class DataProtectionBuilderExtensions
{
    /// <summary>
    /// Persists the Data Protection key ring to SQL Server and applies the schema migration so the
    /// <c>data_protection_keys</c> table exists.
    /// </summary>
    /// <remarks>
    /// Chain after <c>SetApplicationName</c> so the application keeps control of the discriminator that stops
    /// two applications sharing one table from reading each other's keys:
    /// <code>
    /// services.AddDataProtection()
    ///         .SetApplicationName("my-app")
    ///         .PersistKeysToThemiaSqlServer(connectionString);
    /// </code>
    /// The stored key material is <strong>not encrypted at rest</strong> — see
    /// <see cref="Themia.AspNetCore.DataProtection.DataProtectionBuilderExtensions.PersistKeysToThemia"/>.
    /// </remarks>
    /// <param name="builder">The Data Protection builder.</param>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <param name="runMigration">Whether to apply the schema migration during registration. Defaults to true.</param>
    public static IDataProtectionBuilder PersistKeysToThemiaSqlServer(
        this IDataProtectionBuilder builder, string connectionString, bool runMigration = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return builder.PersistKeysToThemia(
            new SqlServerDataProtectionKeyDialect(connectionString),
            MigrationEngine.SqlServer,
            connectionString,
            runMigration);
    }
}
