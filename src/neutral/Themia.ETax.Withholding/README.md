# Themia.ETax.Withholding

Neutral core for issuing Thai withholding tax certificates (หนังสือรับรองการหักภาษี ณ ที่จ่าย, form 50 ทวิ). Targets `net8.0` and
`net10.0`. No database, no HTTP. Builds on `Themia.ETax` (tax id and branch validation, party and replacement records, the
snapshot exception types). Phase 2 of coord #0158 / request #0164.

**Not adoptable yet**: this release holds the snapshot, validator, math, Thai baht text, the catalogue structure and the issue
service with its ports. The renderer, the database guards and the real catalogue entries are separate PRs.

- **`WithholdingCertificateSnapshot`**: everything the certificate prints, frozen at issue (money in satang, rates in basis
  points). `WithholdingSnapshotSerializer` writes deterministic, ASCII-only versioned JSON and hashes the exact text. Store it in a
  byte-preserving column, **never `jsonb` or MySQL `JSON`**.
- **`WithholdingSnapshotValidator`**: checks NEW input against an `IncomeTypeCatalogue`. Stored snapshots are never re-validated.
  Errors carry codes and field paths only (names and tax ids are never echoed).
- **`WithholdingMath.Withhold(baseSatang, rateBasisPoints)`**: half-up to the satang, the same rule as `VatMath`. The rounding rule
  and the base the rate applies to are pending accountant confirmation (#0164 Q8, Q10). Which rate applies to a payment is the
  caller's business rule; this package only multiplies.
- **`IncomeTypeCatalogue`**: a versioned, immutable set of `IncomeTypeEntry` (section, sub-item, printed label, form row, allowed
  filing forms and payee kinds) the app selects from by `Code`. A certificate stores a copy of what is printed, so it renders the
  same after the catalogue changes. `ContentHash` is independent of entry order: pin it in a test for every version you ship, and
  publish a change as a new `Version`, never an edit. **No real entries are shipped yet** (#0164 Q1/Q2).
- **`ThaiBahtText.Format(satang)`**: the amount in Thai words ("หนึ่งพันสองร้อยสามสิบสี่บาทห้าสิบหกสตางค์", "…บาทถ้วน").

Dates are supplied by the caller (the Bangkok business date). Never log snapshot contents.

## Issuing

`WithholdingCertificateService.IssueAsync(tx, request)` issues inside **your** transaction (READ COMMITTED) and commits nothing.
**Adopter checklist: roll back on every outcome except `Issued` / `AlreadyIssued`; a refused replacement may already have written in
your transaction** (the replaced certificate is cancelled before the new number and the final validation, so a later refusal leaves that
cancel pending until you roll back).

- **Key:** `WithholdingCertificateKey(SourceEventId, Revision)`. The source event is the payout the tax was withheld from and must be
  unique across payers (a key found for another payer answers `SourceConflict`, never that payer's id). A duplicate is answered
  before a number is consumed.
- **Ports you implement:** `IWithholdingCertificateStore` (your tables; payer-scoped reads that fail closed; `FindByKeyForIssueAsync`
  must serialise racing issuers of one key) and `IWithholdingNumberSource` (gapless, inside the transaction). Run the store through
  `Themia.ETax.Withholding.Testing`. On PostgreSQL, the table's immutability trigger, same-payer replacement key and unique issue key come from
  `Themia.ETax.Withholding.Data` (`WithholdingCertificateGuards`), together with a gapless `IWithholdingNumberSource`.
- **Replace, never edit:** a wrong certificate is cancelled by issuing revision N+1 with `Replacement` set; the old one becomes
  `Cancelled` (final) and points at its replacement. Revision 1 may not carry a replacement; later revisions must.
- **Same snapshot in, same snapshot stored:** the service copies the snapshot through the persisted format first, so changes to your
  lists while it runs cannot reach what was validated.
- Register with `services.AddThemiaETaxWithholding(catalogue)`. Logs hold ids and states only.
