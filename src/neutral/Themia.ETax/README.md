# Themia.ETax

Neutral core for Thai tax invoices, credit/debit notes and e-Tax (route A, e-Tax by Email). Targets `net8.0` and `net10.0`.
No database, no HTTP, no PDF engine: this release holds the parts every issuing app must agree on.

- **`TaxDocumentSnapshot`**: everything a document prints, frozen at issue (money in satang, rates in basis points).
  `TaxDocumentSnapshotSerializer` writes it as deterministic, ASCII-only versioned JSON and hashes the exact text
  (SHA-256). Store the text in a byte-preserving column (PostgreSQL `text`, SQL Server `nvarchar(max)`, MySQL
  `longtext`), **never `jsonb` or MySQL `JSON`**: they reorder keys and the hash would no longer match.
- **`TaxDocumentSnapshotValidator`**: checks NEW input (totals, VAT arithmetic, withholding, credit/debit note and
  replacement references). Stored snapshots are never re-validated.
- **`TaxDocumentKey`**: idempotency key `(Kind, SourceEventId, Revision)`. `SourceEventId` is the *triggering event*:
  payment id for a tax invoice/receipt, refund id for a credit note, the debit note's own event id for a debit note. Keying a credit note by the payment would make
  a second partial refund look like a duplicate.
- **`VatMath`**: `AddVat` / `Split` in satang and basis points, midpoints away from zero, identical to the decimal-baht
  algorithm it was promoted from. `tests/Themia.ETax.Tests/Vectors/vatmath-parity.json` holds parity vectors
  (midpoints included) that any decimal-baht adapter can run.
- **`ThaiTaxId` / `ThaiBranchCode`**: 13-digit id with optional mod-11 check digit, 5-digit branch.

The `issue_date` is always supplied by the caller (the Bangkok business date of the payment or refund), never derived from UTC.
Never log snapshot contents: they hold names and tax ids. Errors from this package carry codes and field paths only.
