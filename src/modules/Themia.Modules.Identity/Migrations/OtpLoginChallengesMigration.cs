using System;
using FluentMigrator;

namespace Themia.Modules.Identity.Migrations;

/// <summary>Creates <c>identity.otp_login_challenges</c>, the short-lived store behind the passwordless phone-code
/// login, on PostgreSQL and SQL Server. Additive: nothing else reads or writes it unless the adopter calls
/// <c>AddThemiaIdentityOtpLogin</c>. No foreign key to <c>users</c>: a decoy challenge for a phone with no account has
/// no user.</summary>
[Migration(202610170001, "Themia.Identity: create identity.otp_login_challenges")]
public sealed class OtpLoginChallengesMigration : Migration
{
    private const string SchemaName = "identity";

    /// <inheritdoc />
    public override void Up()
    {
        // Replay-safe (coord #0078): see RefreshTokensMigration.
        if (Schema.Schema(SchemaName).Table("otp_login_challenges").Exists())
        {
            return;
        }

        IfDatabase("postgresql", "sqlserver").Delegate(CreateChallenges);

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Identity supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    private void CreateChallenges()
    {
        Create.Table("otp_login_challenges").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("user_id").AsGuid().Nullable()
            .WithColumn("normalized_phone_number").AsString(64).NotNullable()
            .WithColumn("code_hash").AsString(64).NotNullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("expires_at").AsDateTimeOffset().NotNullable()
            .WithColumn("attempt_count").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("closed_at").AsDateTimeOffset().Nullable();

        // Every query is by phone within a time window (throttles, failure cap, supersede).
        Create.Index("ix_otp_login_challenges_phone_created").OnTable("otp_login_challenges").InSchema(SchemaName)
            .OnColumn("normalized_phone_number").Ascending()
            .OnColumn("created_at").Descending();

        // The purge reads rows by expiry.
        Create.Index("ix_otp_login_challenges_expires").OnTable("otp_login_challenges").InSchema(SchemaName)
            .OnColumn("expires_at").Ascending();
    }

    /// <inheritdoc />
    public override void Down() => Delete.Table("otp_login_challenges").InSchema(SchemaName);
}
