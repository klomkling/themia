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
- **`ThaiBahtText.Format(satang, etMode)`**: the amount in Thai words ("หนึ่งพันสองร้อยสามสิบสี่บาทห้าสิบหกสตางค์", "…บาทถ้วน"; under one baht
  omits the baht part). Both conventions for "เอ็ด" are in use, so the adopter chooses with `ThaiBahtTextEtMode`: `Always` (default:
  หนึ่งร้อยเอ็ด, หนึ่งพันเอ็ดล้าน) or `TensOnly` (เอ็ดเฉพาะหลังหลักสิบ: สิบเอ็ด but หนึ่งร้อยหนึ่ง, หนึ่งพันหนึ่งล้าน). Pinned by the documented
  examples of the ThaiBahtText package (11, 101, 211, 1001, 1,001,000,000 in both modes); that package is not a dependency. Tax documents
  print it from template version `"2"` (below).
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

## Issuing and sending

Apps own their tables, migrations and the persistence port; Themia owns the rules.

- **Ports the app implements:** `ITaxDocumentStore` (every transition is one conditional write), `ITaxDocumentNumberSource`
  (gapless number inside the caller's transaction), `ITaxDocumentMailer` (route A transport, returns a code, never a message),
  and optionally `ITaxDocumentFailureHandler` (alert when a document enters Failed). Prove your store with
  `Themia.ETax.Testing`: inherit `TaxDocumentStoreConformanceTests`, override `CreateHarnessAsync`, done.
- **`TaxDocumentService.IssueAsync(tx, request)`** runs in the caller's READ COMMITTED transaction: a duplicate key answers
  `AlreadyIssued` before any number is allocated (on PostgreSQL the store's `FindByKeyForIssueAsync` must take `pg_advisory_xact_lock`
  and run its SELECT as SEPARATE statements under READ COMMITTED: one statement keeps its pre-wait snapshot and misses the row the lock
  holder just committed); validation, the credit/debit-note precondition (the original was prepared, is not
  cancelled, same seller, same number) and a replacement's cancel all run before the number is taken. Only `Issued` and
  `AlreadyIssued` mean commit; every other outcome means roll back. `SourceEventId` is the triggering event and must be globally unique across sellers: the key carries no seller, so a second seller reusing a key gets `SourceConflict`, never the other seller's document id.
- **`TaxDocumentSender.RunOnceAsync()`** (call it on a schedule; it is also the sweeper) claims due documents under a lease
  (`Issued` or `OriginalPrepared`, no live lease, backoff elapsed), renders the ORIGINAL from the stored snapshot, records its
  evidence, **re-checks in a conditional write that the document is still sendable (and extends the lease)** and only then sends.
  A cancel (`CancelForReplacementAsync`) is refused while a live lease is held, so a cancelled document is never sent as ORIGINAL.
  The mailer runs under `SendTimeout` (default 60 s, must be shorter than the 2-minute lease) and must honour its cancellation token; a timeout is a transient `send_timeout` and the lease is deliberately NOT released (the next attempt is due no earlier than the lease's end), because a mailer that ignored its token may still be delivering and the lease is what keeps a cancel out; and an abandoned send that later faults or finishes is observed and logged (type and outcome only). A replacement must be the next revision of the same source event and kind. Delivery is at-least-once with exactly one ORIGINAL: if the process dies after sending, the lease lapses and the same ORIGINAL is
  re-rendered, checked against the evidence history, and resent with the same Message-ID (the document id).
- **Seller scope, fail closed:** everything an app can expose by document id (`ITaxDocumentStore.GetAsync`, `RequeueAsync`, `UpdateBuyerEmailAsync`, and the service's `RenderCopyAsync` / `RequeueAsync`) takes the caller's `sellerId`; the store looks the document up by (id, seller) and another seller's id answers exactly like a missing one. The app still authorizes first; this is the second lock.
- **Failure:** permanent errors (and snapshot or template problems) fail at once; transient ones back off exponentially with jitter
  and fail at `MaxAttempts` (default 5). Failed is terminal for automation; `RequeueAsync` is the only way back and logs the actor
  id. One ERROR log per entry into Failed, then `ITaxDocumentFailureHandler`.
- **Logging:** ids, kinds, states, attempt counts and error codes only. A capturing-logger test proves no snapshot value, address or
  transport message reaches a log or an exception.
- **Register:** `services.AddThemiaETaxDocuments(o => o.RevenueDepartmentAddress = "...")` after registering your store, number source
  and mailer. `TaxDocumentService` and `TaxDocumentSender` are **scoped** (your store and number source normally are): resolve them from a
  scope, and in a scheduled job create one scope per `RunOnceAsync`.

## Template version 2: the amount in Thai words

Template `"1"` stays frozen and renders every document issued under it byte-identically. Template `"2"` is template 1 plus a line under the totals,
`ตัวอักษร / In words: หนึ่งพันเจ็ดสิบบาทถ้วน`. The words are the document total: the grand total including VAT on a tax invoice/receipt, and the
credited (or debited) amount on a credit (debit) note. A document records the template version it was issued under, as before, and is always
rendered with it.

The adopter opts in for NEW documents; the version also fixes the "เอ็ด" convention, so it is frozen with each document: `"2"` reads a units digit of 1 as
"เอ็ด" (หนึ่งร้อยเอ็ด), `"3"` as "หนึ่ง" (หนึ่งร้อยหนึ่ง). The layout is the same.

```csharp
services.AddThemiaETaxDocuments(...);
services.ConfigureThemiaETaxTemplates(o =>
{
    o.TemplateVersion = TaxDocumentTemplates.LatestVersion;     // "3" (หนึ่ง); "2" is เอ็ด; the default stays "1", so upgrading changes nothing by itself
});
```

`ConfigureThemiaETaxTemplates` may be called before or after the other `AddThemiaETax*` methods and validates at registration.
Changing `TemplateVersion` later only affects documents issued after the change: an issued document is always rendered under the version recorded on it,
so its words never change.
