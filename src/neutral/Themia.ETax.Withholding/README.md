# Themia.ETax.Withholding

Neutral core for issuing Thai withholding tax certificates (หนังสือรับรองการหักภาษี ณ ที่จ่าย, form 50 ทวิ). Targets `net8.0` and
`net10.0`. No database, no HTTP. Builds on `Themia.ETax` (tax id and branch validation, party and replacement records, the
snapshot exception types). Phase 2 of coord #0158 / request #0164.

**This release holds the core only**: snapshot, validator, math, Thai baht text and the catalogue structure. The renderer, issue
service, store port and database guards are separate PRs; there is nothing to adopt yet.

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
