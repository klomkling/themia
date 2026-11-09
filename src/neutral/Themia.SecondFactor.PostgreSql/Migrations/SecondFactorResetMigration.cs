using FluentMigrator;

namespace Themia.SecondFactor.PostgreSql.Migrations;

/// <summary>
/// The user reset as database functions, so the in-app admin reset and the operator's break-glass path run the same code.
/// <c>second_factor_reset_user</c> takes the methods lock then the recovery lock (the keys the stores use, byte for byte, with a null
/// tenant as an empty string) and deletes the user's methods, unused recovery codes, ceremonies and counters, then appends the audit row,
/// all in the caller's transaction. <c>second_factor_break_glass_reset</c> is the operator entry point: the actor is
/// <c>db:&lt;session_user&gt;</c>. Neither is <c>SECURITY DEFINER</c>; execute is revoked from <c>PUBLIC</c> and granted by name.
/// </summary>
[Migration(202611100001, "Themia.SecondFactor: add the user reset and break-glass functions")]
public sealed class SecondFactorResetMigration : Migration
{
    private const string Sql = """
        CREATE OR REPLACE FUNCTION second_factor_reset_user(
            p_tenant text, p_user text, p_actor text, p_action text, p_reason text, p_at timestamptz)
        RETURNS integer
        LANGUAGE plpgsql
        SET search_path FROM CURRENT
        AS $fn$
        DECLARE
            removed integer;
        BEGIN
            IF coalesce(btrim(p_reason), '') = '' THEN
                RAISE EXCEPTION 'a reset needs a reason' USING ERRCODE = '22023';
            END IF;
            IF coalesce(btrim(p_user), '') = '' OR coalesce(btrim(p_actor), '') = '' OR coalesce(btrim(p_action), '') = '' THEN
                RAISE EXCEPTION 'a reset needs a user, an actor and an action' USING ERRCODE = '22023';
            END IF;

            PERFORM pg_advisory_xact_lock(hashtextextended('second-factor-methods:' || coalesce(p_tenant, '') || E'\n' || p_user, 0));
            PERFORM pg_advisory_xact_lock(hashtextextended('second-factor-recovery:' || coalesce(p_tenant, '') || E'\n' || p_user, 0));

            DELETE FROM second_factor_methods WHERE user_id = p_user AND tenant_id IS NOT DISTINCT FROM p_tenant;
            GET DIAGNOSTICS removed = ROW_COUNT;
            DELETE FROM second_factor_recovery_codes WHERE used_at IS NULL AND user_id = p_user AND tenant_id IS NOT DISTINCT FROM p_tenant;
            DELETE FROM second_factor_webauthn_ceremonies WHERE user_id = p_user AND tenant_id IS NOT DISTINCT FROM p_tenant;
            DELETE FROM second_factor_user_state WHERE user_id = p_user AND tenant_id IS NOT DISTINCT FROM p_tenant;

            INSERT INTO second_factor_audit (at, tenant_id, target_user_id, actor, action, method_kind, reason)
            VALUES (p_at, p_tenant, p_user, p_actor, p_action, NULL, p_reason);

            RETURN removed;
        END;
        $fn$;

        CREATE OR REPLACE FUNCTION second_factor_break_glass_reset(p_tenant text, p_user text, p_reason text)
        RETURNS integer
        LANGUAGE sql
        SET search_path FROM CURRENT
        AS $fn$
            SELECT second_factor_reset_user(p_tenant, p_user, 'db:' || session_user, 'break_glass.reset', p_reason, now());
        $fn$;

        REVOKE ALL ON FUNCTION second_factor_reset_user(text, text, text, text, text, timestamptz) FROM PUBLIC;
        REVOKE ALL ON FUNCTION second_factor_break_glass_reset(text, text, text) FROM PUBLIC;
        """;

    /// <inheritdoc />
    public override void Up() => Execute.Sql(Sql);

    /// <inheritdoc />
    public override void Down() => Execute.Sql("""
        DROP FUNCTION IF EXISTS second_factor_break_glass_reset(text, text, text);
        DROP FUNCTION IF EXISTS second_factor_reset_user(text, text, text, text, text, timestamptz);
        """);
}
