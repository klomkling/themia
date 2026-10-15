# Themia.ETax.Withholding.Data

Framework side of `Themia.ETax.Withholding` (net10.0, PostgreSQL): the migration helpers for your own withholding certificate table and the
gapless certificate-number source. It is a separate package from `Themia.ETax.Data` so an app that only issues tax invoices does not pull
the withholding package.

## The required guards (`WithholdingCertificateGuards`)

Call all three from the migration that creates or adopts your table, after the table exists. Every statement is safe to run again.

```csharp
var map = new WithholdingCertificateTableMap { Table = "withholding_certificates", IssuedStateValue = 1, CancelledStateValue = 2 };
WithholdingCertificateGuards.ApplyImmutability(this, map);
WithholdingCertificateGuards.ApplySamePayerReplacementKey(this, map);
WithholdingCertificateGuards.ApplyIssueKey(this, map);
```

- **`ApplyImmutability`** reuses the tax document guard SQL (`Themia.ETax.Data`). An `Issued` certificate may leave `Issued` only by being
  cancelled with a replacement (the rule the tax document guard applies to a *sent* document), so refusal messages say "tax document" and
  "sent" for certificates too. Cancelled is final, the replacement is write-once, no delete, no truncate. Rows without a snapshot (legacy
  rows) are guarded against delete only. The default immutable list follows the id, payer, source event, revision and snapshot columns you
  configure; a table that names the others differently lists its own `ImmutableColumns` (which must contain the snapshot column).
- **`ApplySamePayerReplacementKey`** adds a unique index on (id, payer) and a composite foreign key (replaced-by, payer) to (id, payer),
  deferred to commit, so a replacement must belong to the replaced certificate's payer.
- **`ApplyIssueKey`** creates `UNIQUE (source event, revision)` as an index named `ux_<table>_source_revision` (created only if absent), so
  two certificates can never exist for one idempotency key even if issuers race past the store's lookup. An app that already has an
  equivalent unique constraint under another name ends up with a second, redundant index and can drop either.
- Identifiers are validated, then quoted; the table name is capped at 39 characters so every derived name stays within PostgreSQL's 63.
  Other engines: the migration throws `NotSupportedException` rather than leave issued certificates editable.
- **Run the application as a role that does NOT own the table**: the trigger stops application bugs, not someone with DDL rights.

## Certificate numbers (`SequenceWithholdingNumberSource`)

`services.AddThemiaETaxWithholdingSequenceNumbers()` registers the `IWithholdingNumberSource`: `WHT-{year}-{counter}`, one gapless run per
payer and year over Themia's transactional sequence allocator (key `DocNo:ETax:Withholding:{payer}:{yyyy}`, separate from every tax document
run). The printed year is the Buddhist-era year by default (`WHT-2569-000123`); the counter key always uses the Gregorian year, so changing the
style never restarts a run. Needs `AddThemiaSequences`.
