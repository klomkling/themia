using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Themia.AspNetCore.DataProtection.Migrations;
using Themia.Data.Migrations;

namespace Themia.AspNetCore.DataProtection;

/// <summary>Shared registration used by the per-engine packages.</summary>
public static class DataProtectionBuilderExtensions
{
    /// <summary>
    /// Persists the Data Protection key ring through <paramref name="dialect"/> and, unless
    /// <paramref name="runMigration"/> is false, applies the schema migration immediately so the table exists.
    /// </summary>
    /// <remarks>
    /// Deliberately an <see cref="IDataProtectionBuilder"/> extension rather than a standalone
    /// <c>AddThemiaDataProtection()</c>: the application still calls
    /// <c>AddDataProtection().SetApplicationName(...)</c> itself, so the application name — which is what
    /// keeps two applications sharing one table from reading each other's keys — stays where the application
    /// controls it, exactly as with the built-in <c>PersistKeysToDbContext</c>.
    ///
    /// <para><strong>The keys are stored unencrypted.</strong> A key element is live key material: anything
    /// that can read this table can decrypt that application's auth cookies and antiforgery tokens. This
    /// matches what ASP.NET Core's own EF Core and Redis providers do, but a database spreads the material
    /// further than a per-instance filesystem does — into backups, read replicas, and any DBA's reach. Treat
    /// the table as a secret, and add <c>ProtectKeysWith*</c> if the deployment needs encryption at rest.</para>
    /// </remarks>
    /// <param name="builder">The Data Protection builder.</param>
    /// <param name="dialect">Per-engine SQL and connection factory.</param>
    /// <param name="engine">Engine whose FluentMigrator processor applies the schema.</param>
    /// <param name="connectionString">Connection string for the migration runner. Required when <paramref name="runMigration"/> is true.</param>
    /// <param name="runMigration">Whether to apply the schema migration during registration. Defaults to true.</param>
    public static IDataProtectionBuilder PersistKeysToThemia(
        this IDataProtectionBuilder builder,
        IDataProtectionKeyDialect dialect,
        MigrationEngine engine,
        string? connectionString = null,
        bool runMigration = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dialect);

        builder.Services.TryAddSingleton(dialect);
        builder.Services.TryAddSingleton<IDataProtectionKeyStore, DataProtectionKeyStore>();
        builder.Services.TryAddSingleton<IXmlRepository, ThemiaXmlRepository>();

        // The documented way to supply an IXmlRepository: configure KeyManagementOptions from DI so the
        // repository is resolved lazily. Assigning it eagerly here would force the store (and its dialect) to
        // be built before the container is finished.
        builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
            new ConfigureOptions<KeyManagementOptions>(
                options => options.XmlRepository = sp.GetRequiredService<IXmlRepository>()));

        if (runMigration)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            ThemiaMigrations.Run(engine, connectionString, typeof(DataProtectionKeysMigration).Assembly);
        }

        return builder;
    }
}
