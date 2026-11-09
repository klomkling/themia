# Second-factor reset runbook

When a user has lost every second factor and cannot sign in. Two paths: the in-app admin reset (preferred) and the database break-glass reset (no app admin available).

Both wipe the user's methods, unused recovery codes, pending enrolments and user state in one transaction and write a `second_factor_audit` row. The user then signs in with their password and enrols again.

## 1. In-app admin reset

Call `ISecondFactorAdminService.ResetAsync(actor, actorProof, targetUserId, reason)`.

- The actor must hold a fresh strong proof (re-verify if `StepUpRequired`), cannot reset themselves and must outrank the target (`NotAllowed` otherwise).
- `reason` is required, at most 500 characters, and goes only into the audit row. **Do not put personal data in it.**
- `SessionsNotRevoked` means the reset was applied but a session observer failed. Repeat the call; it is idempotent.

## 2. Break-glass (SQL)

Run as a named operator role, never as the application role.

```sql
-- once, by a DBA: grant the function to a named role (it is revoked from PUBLIC)
GRANT EXECUTE ON FUNCTION second_factor_break_glass_reset(text, text, text) TO ops_break_glass;

-- as that role (tenant NULL for a platform user)
SELECT second_factor_break_glass_reset('<tenant-id>', '<user-id>', 'ticket 1234: lost phone and codes');
```

The audit actor is `db:<session_user>`. A blank reason raises. **Do not put personal data in the reason.**

### Then revoke the user's sessions (same procedure, do not skip)

The SQL path cannot reach the application's token store. Revoke the user's refresh tokens right after, with the application's own tooling (an admin endpoint that calls `IRefreshTokenService.RevokeAllForUserAsync(userId)`), or directly:

```sql
UPDATE identity.refresh_tokens SET revoked_at = now()
WHERE user_id = '<user-id>' AND revoked_at IS NULL;
```

Check the table and column names against your deployed schema before running it.

## Limits

Access tokens already issued stay valid until they expire (the configured access-token lifetime); only renewal is cut. Keep that lifetime short where it matters.
