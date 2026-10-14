using FluentMigrator;

namespace Themia.ETax.Data;

/// <summary>
/// The REQUIRED database guards for an app's own tax document table. Call them from the app's migration, after the table exists:
/// <code>
/// public override void Up()
/// {
///     // ... create or alter the table ...
///     TaxDocumentGuards.ApplyImmutability(this, map);
///     TaxDocumentGuards.ApplySameSellerReplacementKey(this, map);
/// }
/// </code>
/// PostgreSQL only for now: on any other engine the migration FAILS rather than silently leaving issued tax documents editable.
/// Both calls are idempotent, so they adopt a database that already has some of the objects and may be repeated.
/// </summary>
public static class TaxDocumentGuards
{
    /// <summary>
    /// Creates the immutability trigger: once a row has a snapshot, its snapshot, number, kind, date, seller and key can never change
    /// (including by setting the snapshot to NULL); nothing is ever deleted or truncated; and a Sent document can only become Cancelled together with
    /// a replacement id; a Cancelled document never changes state again; and once set, the replacement id cannot be changed. Rows without a snapshot (legacy, issued before Themia.ETax) are not guarded against update, but are never deletable.
    /// </summary>
    /// <param name="migration">The migration being run.</param>
    /// <param name="map">The table and its column names.</param>
    public static void ApplyImmutability(MigrationBase migration, TaxDocumentTableMap map) =>
        Run(migration, map, PostgresTaxDocumentGuardSql.Immutability);

    /// <summary>
    /// Creates the composite foreign key (replaced-by, seller) to (id, seller), so a document can only be replaced by one of the same seller,
    /// enforced by the database. It is deferred to commit, because the replaced document is cancelled before its replacement is inserted.
    /// A constraint of the same name that already exists is left as it is.
    /// </summary>
    /// <param name="migration">The migration being run.</param>
    /// <param name="map">The table and its column names.</param>
    public static void ApplySameSellerReplacementKey(MigrationBase migration, TaxDocumentTableMap map) =>
        Run(migration, map, PostgresTaxDocumentGuardSql.SameSellerKey);

    private static void Run(MigrationBase migration, TaxDocumentTableMap map, Func<TaxDocumentTableMap, IReadOnlyList<string>> statements)
    {
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(map);
        map.Validate();

        var sql = statements(map);
        foreach (var statement in sql)
            migration.IfDatabase("postgres", "postgresql").Execute.Sql(statement);

        migration.IfDatabase(provider => !provider.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase)).Delegate(() =>
            throw new NotSupportedException(
                "Themia.ETax.Data guards are implemented for PostgreSQL only. Refusing to leave issued tax documents unprotected on another engine."));
    }
}
