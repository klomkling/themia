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
    /// <c>AddThemiaDataProtection()</c>, mirroring the built-in <c>PersistKeysToDbContext</c>: the application
    /// still calls <c>AddDataProtection()</c> itself and keeps control of the rest of the configuration.
    /// Calling this more than once is last-wins, as with the built-in <c>PersistKeysTo*</c> methods.
    ///
    /// <para><strong>Two applications must not share one table.</strong> Everything using this repository
    /// shares the whole key ring: each holds the raw key material able to decrypt the other's payloads once it
    /// names the same <c>SetApplicationName</c>, and a revocation or expiry in one applies to the other.
    /// <c>SetApplicationName</c> only sets the discriminator folded into the purpose chain — it is <em>not</em>
    /// an isolation boundary. Give separate applications separate tables or separate databases.</para>
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
    /// <param name="connectionString">Connection string for the migration runner.</param>
    /// <param name="runMigration">Whether to apply the schema migration during registration. Defaults to true.</param>
    /// <param name="migrationOptions">
    /// Migration-lock settings. Supply a <see cref="ThemiaMigrationOptions.Logger"/> whenever more than one
    /// instance can boot at once: the migration runs here, during service registration, before the host has
    /// built any logging provider, so without it an instance waiting on another instance's migration lock
    /// blocks with no output at all and is eventually killed by its startup probe.
    /// </param>
    public static IDataProtectionBuilder PersistKeysToThemia(
        this IDataProtectionBuilder builder,
        IDataProtectionKeyDialect dialect,
        MigrationEngine engine,
        string connectionString,
        bool runMigration = true,
        ThemiaMigrationOptions? migrationOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // AddSingleton, not TryAddSingleton: the built-in PersistKeysTo* methods are last-wins, and a
        // first-wins registration here would silently keep an earlier dialect (a shared bootstrap default,
        // say) while an explicit later call appeared to take effect — the key ring would quietly live in the
        // wrong database. The store and repository resolve the dialect, so they follow whichever wins.
        builder.Services.AddSingleton(dialect);
        builder.Services.TryAddSingleton<IDataProtectionKeyStore, DataProtectionKeyStore>();
        builder.Services.TryAddSingleton<IXmlRepository, ThemiaXmlRepository>();

        // The documented way to supply an IXmlRepository: configure KeyManagementOptions from DI so the
        // repository is resolved lazily. Assigning it eagerly here would force the store (and its dialect) to
        // be built before the container is finished.
        builder.Services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
            new ConfigureOptions<KeyManagementOptions>(
                options => options.XmlRepository = sp.GetRequiredService<IXmlRepository>()));

        if (runMigration)
            ThemiaMigrations.Run(engine, connectionString, migrationOptions, [typeof(DataProtectionKeysMigration).Assembly]);

        return builder;
    }
}
