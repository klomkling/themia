# Themia.SecondFactor

Multi-method second factor for an app's sign-in: passkey, TOTP, recovery codes, email OTP and SMS OTP, with a per-app and per-role policy
(`MfaPolicyOptions`), method strength (passkey, TOTP and recovery codes are strong; email and SMS are weak and never claim `mfa` alone),
an admin reset with a role-level act-on rule, and a break-glass reset kept in the database. Storage is yours to choose through
`IMfaMethodStore`; `Themia.SecondFactor.PostgreSql` is the shipped store. Wire it with `AddThemiaSecondFactor(...)` and give it an
`ISecondFactorSecretProtector` (Data Protection or a KMS) and an `ISecondFactorContactResolver`.

## Importing existing TOTP secrets (no re-enrol)

An app that already holds TOTP secrets can move its users over without making them scan a new QR code. Copy each secret into the store
once, under the protection scheme it has today, and let the protector read that scheme:

1. **Implement `ISecondFactorSecretProtector`** so `Unprotect(scheme, protectedSecret)` handles *every* scheme still in the data: the old one
   and the one `Protect` writes now (`Scheme`). Return `null` for a scheme it does not know; verification then fails closed
   (`SecondFactorOutcome.NotAvailable`) instead of guessing.
2. **Add one method per user** with `IMfaMethodStore.AddAsync(new NewMfaMethod { ... })`:
   `Kind = MfaMethodKind.Totp`, `SecretProtected` = the stored value **as it is** (do not decrypt and re-encrypt), `ProtectionScheme` = the
   scheme that protects it, `Label`, `EnrolledAt`.
3. **Carry the replay position:** set `LastUsedStep` to the last accepted time step (Unix seconds / 30) if the old system recorded one;
   leave it at `-1` only when it did not. A code of that step or earlier is then refused as `Replayed`.
4. **Keep the old scheme readable** for as long as any imported row uses it; the scheme is stored per row, so old and new coexist.

Limits to check before running the import:

- **Secret length:** Themia refuses a secret that decodes to fewer than 16 bytes (RFC 4226 §4, 128 bits). Older systems often issued 80-bit
  secrets (16 base32 characters); those cannot be imported as they are, and those users must re-enrol.
- **Digits, period and algorithm:** one `TotpOptions` applies to every user (defaults: 6 digits, 30-second step, SHA-1, what authenticator apps use).
  Set them to match the old system before importing; secrets issued with different parameters cannot be mixed.
- **The method id is new.** The replay record is keyed on the new method id, so import before the first sign-in, not during it.

`tests/Themia.SecondFactor.IntegrationTests/LegacyTotpImportTests.cs` runs this recipe against Postgres: a legacy-scheme secret verifies,
the imported `LastUsedStep` blocks a replay, and an unreadable scheme fails closed.
