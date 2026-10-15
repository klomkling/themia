# Themia.ETax.Withholding

Neutral core for issuing Thai withholding tax certificates (หนังสือรับรองการหักภาษี ณ ที่จ่าย, form 50 ทวิ). Targets `net8.0` and
`net10.0`. No database, no HTTP. Builds on `Themia.ETax` (tax id and branch validation, party and replacement records, the
snapshot exception types). Phase 2 of coord #0158 / request #0164.

The package holds the snapshot, validator, math, Thai baht text, the catalogue structure, the issue service with its ports and the
renderer for the official layout.

- **`WithholdingCertificateSnapshot`**: everything the certificate prints, frozen at issue (money in satang, rates in basis
  points). `WithholdingSnapshotSerializer` writes deterministic, ASCII-only versioned JSON and hashes the exact text. Store it in a
  byte-preserving column, **never `jsonb` or MySQL `JSON`**.
- **`WithholdingSnapshotValidator`**: checks NEW input against an `IncomeTypeCatalogue`. Stored snapshots are never re-validated.
  Errors carry codes and field paths only (names and tax ids are never echoed).
- **`WithholdingMath.Withhold(baseSatang, rateBasisPoints)`**: ceiling to the satang, looking at every digit after the second decimal
  (the Revenue Department's rule: 10.115, 10.114 and 10.1101 all become 10.12; an exact 10.11 stays). This is NOT the rule of `VatMath`.
  Which base the rate applies to and which rate applies to a payment are the caller's business rules (the base is still an open accountant
  question, #0164 Q10); this package only multiplies.
- **`IncomeTypeCatalogue`**: a versioned, immutable set of `IncomeTypeEntry` (section, sub-item, printed label, form row, allowed
  filing forms and payee kinds) the app selects from by `Code`. A certificate stores a copy of what is printed, so it renders the
  same after the catalogue changes. `ContentHash` is independent of entry order: pin it in a test for every version you ship, and
  publish a change as a new `Version`, never an edit. **Themia ships no catalogue content and no rates**: which income types an app uses,
  how each maps to a line of the form (`WithholdingFormRow`, the numbered lines of the official certificate) and at what rate it is withheld
  are the adopter's, supplied through `AddThemiaETaxWithholding(catalogue)` and the rate passed per payment.
- **Amount in words:** the total tax is printed in Thai words with `Themia.ETax.ThaiBahtText` (see that package).

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
  `Cancelled` (final) and points at its replacement. Cancel-and-reissue under a new number that cites the old one is acceptable (a 50 ทวิ is not a
  tax invoice, so no rule fixes how it is corrected; the corrected certificate only has to carry what Director-General Notice on income tax No. 62
  requires). If the tax was already filed and the filed data is wrong too, the filing is corrected separately with the Revenue Department's form
  ป.ป.01, and the certificate and the filed data must agree: nothing here amends a filing automatically, so the app uses the cancelled and
  replacement certificates of a period to raise it. (Secondary sources; an accountant confirms.) Revision 1 may not carry a replacement; later revisions must.
- **Same snapshot in, same snapshot stored:** the service copies the snapshot through the persisted format first, so changes to your
  lists while it runs cannot reach what was validated.
- Register with `services.AddThemiaETaxWithholding(catalogue)`. Logs hold ids and states only.

## Rendering (`WithholdingCertificateRenderer`)

`services.AddThemiaETaxWithholdingRendering()` registers the renderer and the Puppeteer PDF renderer it prints through. It prints the
official layout (หนังสือรับรองการหักภาษี ณ ที่จ่าย, form 50 ทวิ) on one A4 page with the embedded Sarabun, in template version `"1"` (frozen by
golden-hash tests: a layout change ships as template `"2"`, never an edit). The same stored certificate and the same markings give the same
bytes: no clock, no culture, PDF dates pinned to the issue date, dates printed in the Buddhist era (`15/10/2569`).

- **Markings are chosen when rendering and never stored:** `WithholdingCopy.Copy1` / `Copy2` (both copy lines are printed as on the form and the
  rendered copy is marked), `WithholdingRenderMode.ReplacementCopy` ("ใบแทน" at the top: a copy of the same certificate and number for a lost
  or damaged one, not a new certificate; the certifying signature is the issuer's, outside this document) and `Cancelled` (watermark).
- The tax in words uses `Themia.ETax.ThaiBahtText` and always reads a units digit of 1 as "หนึ่ง" (หนึ่งร้อยหนึ่ง). It is fixed by the template
  version, not an option, so a re-render of an issued certificate never changes its words.
- `RenderStoredAsync(record, copy, mode)` verifies the stored snapshot against its hash first and prints a cancelled certificate as
  cancelled whatever mode is asked.
- The book number (เล่มที่) prints empty unless the adopter supplies `BookNumber`; the certificate number is the adopter's number source's
  (default `WHT-2569-000123`; a shape such as `ES-2026-0001` is the adopter's choice, see `Themia.ETax.Withholding.Data`).
- Each income goes on the line of its `WithholdingFormRow`; the lines that say "(ระบุ)" (the section 3 เตรส line, "other", and the two
  other-dividend lines) print the row's `Description`, or the catalogue label when there is none. A certificate has at most one income per
  line. The form's two tax id boxes are the 13-digit id and the old 10-digit id (`PayerLegacyTaxId` / `PayeeLegacyTaxId`); the form has
  no branch box, so the branch is kept in the snapshot (the monthly return attachments ask for it) but not printed.
- No signature is printed: the form asks for the payer's signature and an optional stamp, and whether an electronic copy needs either is
  an open accountant question (#0164 Q9).
