using FluentMigrator;
using Themia.ETax.Data;

namespace Themia.ETax.Withholding.Data;

/// <summary>
/// The database guards an app's withholding certificate table must have, as FluentMigrator helpers (PostgreSQL). Call all three from the
/// migration that creates or adopts the table, after the table exists. All three are safe to run again on a database that already has the objects.
/// </summary>
/// <example>
/// <code>
/// public override void Up()
/// {
///     // ... create the table ...
///     var map = new WithholdingCertificateTableMap { Table = "withholding_certificates", IssuedStateValue = 1, CancelledStateValue = 2 };
///     WithholdingCertificateGuards.ApplyImmutability(this, map);
///     WithholdingCertificateGuards.ApplySamePayerReplacementKey(this, map);
///     WithholdingCertificateGuards.ApplyIssueKey(this, map);
/// }
/// </code>
/// </example>
public static class WithholdingCertificateGuards
{
    /// <summary>
    /// Adds the trigger that makes an issued certificate immutable: the listed columns never change, no delete, no truncate, Cancelled is final,
    /// the replacement is write-once, and Issued only leaves by being cancelled with a replacement. Rows without a snapshot (legacy rows) are
    /// guarded against delete only.
    /// </summary>
    /// <param name="migration">The migration.</param>
    /// <param name="map">The table.</param>
    /// <exception cref="NotSupportedException">The database is not PostgreSQL.</exception>
    public static void ApplyImmutability(MigrationBase migration, WithholdingCertificateTableMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        TaxDocumentGuards.ApplyImmutability(migration, map.ToTaxDocumentTableMap());
    }

    /// <summary>
    /// Adds a composite foreign key (replaced-by, payer) to (id, payer) on the same table, so a replacement must belong to the replaced
    /// certificate's payer. Deferred to commit, because the old certificate is cancelled before its replacement is inserted.
    /// </summary>
    /// <param name="migration">The migration.</param>
    /// <param name="map">The table.</param>
    /// <exception cref="NotSupportedException">The database is not PostgreSQL.</exception>
    public static void ApplySamePayerReplacementKey(MigrationBase migration, WithholdingCertificateTableMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        TaxDocumentGuards.ApplySameSellerReplacementKey(migration, map.ToTaxDocumentTableMap());
    }

    /// <summary>
    /// Makes the idempotency key a database fact: a unique index on (source event, revision), so two certificates can never exist for one key
    /// even if issuers race past the store's lookup. Created only if it does not exist; an app that already has an equivalent unique constraint
    /// under another name gets a second, redundant index and can drop either.
    /// </summary>
    /// <param name="migration">The migration.</param>
    /// <param name="map">The table.</param>
    /// <exception cref="NotSupportedException">The database is not PostgreSQL.</exception>
    public static void ApplyIssueKey(MigrationBase migration, WithholdingCertificateTableMap map)
    {
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(map);
        map.ValidateIssueKey();

        migration.IfDatabase("postgres", "postgresql").Execute.Sql(
            $"CREATE UNIQUE INDEX IF NOT EXISTS {WithholdingCertificateTableMap.Quote(map.IssueKeyIndexName)} " +
            $"ON {map.QualifiedTable} ({WithholdingCertificateTableMap.Quote(map.SourceEventIdColumn)}, {WithholdingCertificateTableMap.Quote(map.RevisionColumn)})");

        migration.IfDatabase(provider => !provider.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase)).Delegate(() =>
            throw new NotSupportedException(
                "Themia.ETax.Withholding.Data guards are implemented for PostgreSQL only. Refusing to leave issued withholding certificates unprotected on another engine."));
    }
}
