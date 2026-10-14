# Themia.ETax

Neutral core for Thai tax invoices, credit/debit notes and e-Tax (route A, e-Tax by Email). Targets `net8.0` and `net10.0`.
No database, no HTTP: it holds the parts every issuing app must agree on, and it prints PDFs through `Themia.Pdf`.

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

## Rendering

- **`TaxDocumentRenderer`** renders a hash-verified snapshot with the template version it was issued under
  (`TaxDocumentTemplates.CurrentVersion` = `"1"`; a template change ships as a NEW version, never an edit) and returns
  a `TaxDocumentRender`: normalised PDF, its SHA-256 and `RendererVersion` (`<browser version>|n<normaliser version>`).
  Register with `services.AddThemiaETaxRendering()`; it needs the Puppeteer renderer, so `IVersionedPdfRenderer` resolves to it even when an adopter registers its own `IPdfRenderer`. Rendering never validates and never logs.
- **Dates:** stored Gregorian/ISO, printed with the Buddhist-era year (`14/10/2569`), computed as year + 543 with invariant
  formatting, never from a Thai culture or calendar (their data can differ between hosts and would break byte-stable renders).
- **Marker:** `TaxDocumentMarker.Original` only on the send path, `Copy` for every other render, `CancelledCopy` for a
  cancelled document (adds a CANCELLED/ยกเลิก watermark).
- **Reproducible bytes:** the PDF is printed untagged (a tagged PDF embeds a per-browser id counter) and its two date
  fields are pinned to the issue date. `TaxDocumentEvidence.Check` is the resend rule: the same renderer version with a
  different hash throws `TaxDocumentRenderMismatchException` (no exceptions to the rule); an unseen renderer version is
  `NewRendererVersion` (record new evidence, warn, resend).
- **Fonts:** Sarabun Regular and Bold are embedded (Thai, basic Latin, Latin Extended), so output does not depend on the
  host's fonts. The SIL Open Font License 1.1 text ships in the package (`licenses/Sarabun-OFL.txt`) and as an embedded
  resource. `TaxDocumentSnapshotValidator` rejects any text with a character the faces do not cover
  (`TaxDocumentValidationCode.UnsupportedCharacter`, field path only, never the value). CJK and other scripts are out of
  scope for template `"1"`; supporting them means a new template version with added fonts.
- **Proof:** `tests/Themia.ETax.IntegrationTests` (Category=Integration) re-renders on Chromium across time, fresh
  browsers, multiple pages, images and concurrency. An app that points `ThemiaPdfOptions.ExecutablePath` at its own
  Chromium runs a build CI does not cover; `RendererVersion` records which one rendered.
