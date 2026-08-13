using FluentMigrator;

namespace Themia.Modules.Identity.Migrations;

/// <summary>
/// Adds <c>identity.users.normalized_phone_number</c> and the two filtered unique indexes that make a
/// phone number a usable login identifier, mirroring the pair that already exists for
/// <c>normalized_email</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the column exists at all.</b> <c>phone_number</c> shipped with the original schema, was never
/// written by anything, and had no normalized form, no uniqueness and no index. A login path over it
/// would therefore have been a full table scan resolving to an arbitrary one of possibly several users
/// holding the same number — so it was storable and unusable, which is what coord #0054 reported.
/// </para>
/// <para>
/// <b>THIS MIGRATION FAILS IF DUPLICATE PHONE NUMBERS ALREADY EXIST</b>, in a tenant or across the
/// platform scope. That is deliberate: creating the index is the first moment the database can tell you
/// two accounts claim one number, and silently permitting it would leave the ambiguity that makes phone
/// login unsafe in the first place. Resolve the duplicates and re-run. See MIGRATION.md for the query
/// that finds them.
/// </para>
/// <para>
/// <b>Backfill is deliberately absent.</b> Existing <c>phone_number</c> values are left with a
/// <see langword="null"/> normalized form, so they are not login identifiers until re-set through
/// <c>IUserService.SetPhoneNumberAsync</c>. Normalizing them here is impossible: the rule lives in the
/// adopter's <c>IPhoneNumberNormalizer</c> and cannot be reached from a migration, and applying some
/// other rule would write values the running application would then disagree with — a row findable by
/// the index and not by the code, or vice versa. An unusable-until-re-set number is the safe direction,
/// and the numbers were unusable before this migration anyway.
/// </para>
/// </remarks>
[Migration(202608050001, "Themia.Identity: add users.normalized_phone_number with filtered unique indexes")]
public sealed class NormalizedPhoneNumberMigration : Migration
{
    private const string SchemaName = "identity";

    /// <inheritdoc />
    public override void Up()
    {
        // Replay-safe (coord #0078). The column and its two filtered indexes are created together, so the
        // column's presence is the whole migration's presence.
        if (Schema.Schema(SchemaName).Table("users").Column("normalized_phone_number").Exists())
        {
            return;
        }

        Alter.Table("users").InSchema(SchemaName)
            .AddColumn("normalized_phone_number").AsString(64).Nullable();

        // Filtered, and split tenant/platform exactly like the email pair: a plain unique index would
        // treat every NULL as distinct on some engines and collapse them on others, and a tenant's user
        // must never collide with a platform user's number.
        //
        // The schema qualifier is escaped per engine because IDENTITY is a reserved keyword on SQL
        // Server — an unbracketed identity.users is a parse error there, and only there, so it passes
        // PostgreSQL and fails the whole migration on the other engine. Same reason
        // IdentitySchemaMigration.CreateFilteredIndexes takes the qualifier as a parameter.
        IfDatabase("postgresql").Delegate(() => CreatePhoneIndexes(SchemaName));
        IfDatabase("sqlserver").Delegate(() => CreatePhoneIndexes($"[{SchemaName}]"));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Identity supports only PostgreSQL and SQL Server. The active database provider "
                + "is not supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down()
    {
        // DROP INDEX diverges in shape, not just in quoting: PostgreSQL names the index in the schema,
        // SQL Server names the index and then its table. One form is a parse error on the other engine.
        IfDatabase("postgresql").Delegate(() =>
        {
            Execute.Sql($"DROP INDEX {SchemaName}.ux_users_platform_phone;");
            Execute.Sql($"DROP INDEX {SchemaName}.ux_users_tenant_phone;");
        });
        IfDatabase("sqlserver").Delegate(() =>
        {
            Execute.Sql($"DROP INDEX ux_users_platform_phone ON [{SchemaName}].users;");
            Execute.Sql($"DROP INDEX ux_users_tenant_phone ON [{SchemaName}].users;");
        });

        Delete.Column("normalized_phone_number").FromTable("users").InSchema(SchemaName);
    }

    /// <param name="schema">The schema qualifier, already escaped for the active engine.</param>
    private void CreatePhoneIndexes(string schema)
    {
        Execute.Sql(
            $"CREATE UNIQUE INDEX ux_users_tenant_phone ON {schema}.users (tenant_id, normalized_phone_number) "
            + "WHERE tenant_id IS NOT NULL AND normalized_phone_number IS NOT NULL;");
        Execute.Sql(
            $"CREATE UNIQUE INDEX ux_users_platform_phone ON {schema}.users (normalized_phone_number) "
            + "WHERE tenant_id IS NULL AND normalized_phone_number IS NOT NULL;");
    }

}
