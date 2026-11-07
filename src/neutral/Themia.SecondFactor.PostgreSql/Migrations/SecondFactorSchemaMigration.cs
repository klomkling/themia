using FluentMigrator;

namespace Themia.SecondFactor.PostgreSql.Migrations;

/// <summary>
/// Creates the second-factor tables. PostgreSQL 15+ only: the per-user uniques use <c>NULLS NOT DISTINCT</c> so a
/// platform user (null tenant) cannot be duplicated, and every tenant predicate elsewhere is <c>IS NOT DISTINCT FROM</c>.
/// <para>
/// Replay-safe and adopt-if-exists (coord #0085/#0096): every statement is <c>IF NOT EXISTS</c> per object, so a
/// partial earlier run or a pre-existing table is adopted instead of failing, and no object is skipped just because
/// an anchor table exists.
/// </para>
/// </summary>
[Migration(202611080001, "Themia.SecondFactor: create methods, recovery codes, user state, ceremonies and audit")]
public sealed class SecondFactorSchemaMigration : Migration
{
    private const string Sql = """
        CREATE TABLE IF NOT EXISTS second_factor_methods (
            id               uuid        NOT NULL PRIMARY KEY,
            tenant_id        text        NULL,
            user_id          text        NOT NULL,
            kind             text        NOT NULL,
            label            text        NOT NULL,
            credential_id    bytea       NULL,
            public_key       bytea       NULL,
            user_handle      bytea       NULL,
            sign_count       bigint      NOT NULL DEFAULT 0,
            secret_protected text        NULL,
            protection_scheme text       NULL,
            last_used_step   bigint      NOT NULL DEFAULT -1,
            enrolled_at      timestamptz NOT NULL,
            last_used_at     timestamptz NULL,
            disabled_at      timestamptz NULL,
            updated_at       timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT ck_second_factor_methods_kind CHECK (kind IN ('Passkey', 'Totp', 'EmailOtp', 'SmsOtp')),
            CONSTRAINT ck_second_factor_methods_passkey CHECK (
                kind <> 'Passkey' OR (credential_id IS NOT NULL AND public_key IS NOT NULL AND user_handle IS NOT NULL)),
            CONSTRAINT ck_second_factor_methods_totp CHECK (
                kind <> 'Totp' OR (secret_protected IS NOT NULL AND protection_scheme IS NOT NULL))
        );

        CREATE INDEX IF NOT EXISTS ix_second_factor_methods_user
            ON second_factor_methods (user_id, tenant_id);

        -- One authenticator credential must never bind to two accounts; a discoverable sign-in looks it up by id alone.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_second_factor_methods_passkey_credential
            ON second_factor_methods (credential_id) WHERE kind = 'Passkey';

        CREATE TABLE IF NOT EXISTS second_factor_recovery_codes (
            id          uuid        NOT NULL PRIMARY KEY,
            tenant_id   text        NULL,
            user_id     text        NOT NULL,
            code_hash   text        NOT NULL,
            hash_scheme text        NOT NULL,
            created_at  timestamptz NOT NULL,
            used_at     timestamptz NULL,
            CONSTRAINT ux_second_factor_recovery_codes_hash UNIQUE NULLS NOT DISTINCT (tenant_id, user_id, code_hash)
        );

        CREATE TABLE IF NOT EXISTS second_factor_user_state (
            tenant_id             text        NULL,
            user_id               text        NOT NULL,
            failed_attempts       integer     NOT NULL DEFAULT 0,
            locked_until          timestamptz NULL,
            spare_nag_started_at  timestamptz NULL,
            updated_at            timestamptz NOT NULL,
            CONSTRAINT ux_second_factor_user_state UNIQUE NULLS NOT DISTINCT (tenant_id, user_id)
        );

        CREATE TABLE IF NOT EXISTS second_factor_webauthn_ceremonies (
            challenge_id text        NOT NULL PRIMARY KEY,
            options_json text        NOT NULL,
            tenant_id    text        NULL,
            user_id      text        NULL,
            purpose      text        NOT NULL,
            expires_at   timestamptz NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_second_factor_webauthn_ceremonies_expires
            ON second_factor_webauthn_ceremonies (expires_at);

        CREATE TABLE IF NOT EXISTS second_factor_audit (
            id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            at             timestamptz NOT NULL,
            tenant_id      text        NULL,
            target_user_id text        NOT NULL,
            actor          text        NOT NULL,
            action         text        NOT NULL,
            method_kind    text        NULL,
            reason         text        NULL
        );

        CREATE INDEX IF NOT EXISTS ix_second_factor_audit_target
            ON second_factor_audit (target_user_id, at);

        CREATE OR REPLACE FUNCTION second_factor_audit_append_only() RETURNS trigger AS $fn$
        BEGIN
            RAISE EXCEPTION 'second_factor_audit is append-only';
        END;
        $fn$ LANGUAGE plpgsql;

        DROP TRIGGER IF EXISTS tr_second_factor_audit_append_only ON second_factor_audit;
        CREATE TRIGGER tr_second_factor_audit_append_only
            BEFORE UPDATE OR DELETE ON second_factor_audit
            FOR EACH ROW EXECUTE FUNCTION second_factor_audit_append_only();

        DROP TRIGGER IF EXISTS tr_second_factor_audit_no_truncate ON second_factor_audit;
        CREATE TRIGGER tr_second_factor_audit_no_truncate
            BEFORE TRUNCATE ON second_factor_audit
            FOR EACH STATEMENT EXECUTE FUNCTION second_factor_audit_append_only();
        """;

    /// <inheritdoc />
    public override void Up() => Execute.Sql(Sql);

    /// <inheritdoc />
    public override void Down()
    {
        Execute.Sql("""
            DROP TABLE IF EXISTS second_factor_audit;
            DROP FUNCTION IF EXISTS second_factor_audit_append_only();
            DROP TABLE IF EXISTS second_factor_webauthn_ceremonies;
            DROP TABLE IF EXISTS second_factor_user_state;
            DROP TABLE IF EXISTS second_factor_recovery_codes;
            DROP TABLE IF EXISTS second_factor_methods;
            """);
    }
}
