# Themia.Exceptional dashboard — StackExchange.Exceptional parity (0.6.1) Design

**Status:** Approved (brainstorming) — ready for implementation plan
**Date:** 2026-06-22
**Origin:** ezy-assets — the shipped dashboard (coord #0005, 0.5.8) is too bare next to
StackExchange.Exceptional (raw-JSON detail, unstyled list). Driver: usable production debugging.
**Target version:** 0.6.1 (additive features + additive migration → PATCH under the pre-1.0 policy).

---

## 1. Goal

Bring the self-rendered `Themia.Exceptional` dashboard up to a StackExchange.Exceptional-grade
experience on two fronts: **(1)** a richer UI over data already captured (formatted stack trace,
polished list, protect/delete actions), and **(2)** capturing + displaying the **request context**
(headers, cookies, query, form, server variables) that SE's detail page shows — gated and redacted,
because today the store deliberately stores none of it.

This supersedes the read-only v1 design (`2026-06-20-themia-exceptional-dashboard-design.md`): it adds
request-context capture and state-changing (protect/delete) actions, both explicitly deferred there.

## 2. Scope

Two packages move; single monorepo version → **0.6.1**.

- **`Themia.Exceptional`** (capture/store core) — new `RequestContext` column + migration, enricher
  capture, `CaptureRequestContext` flag + `Redactor`, dialect SQL + factory updates.
- **`Themia.Exceptional.AspNetCore`** (dashboard) — SE-style list + detail, embedded CSS, protect/delete
  POST endpoints with same-origin CSRF.

The three dialect packages (`Themia.Exceptional.SqlServer` / `.MySql` / `.PostgreSql`) get SQL updates
for the new column.

### Out of scope (YAGNI)

Charts/graphs, real-time updates, JSON API, full theming/custom templates, `Purge` from the UI
(destructive). The Quartz dashboard's embedded-Handlebars + Semantic-UI stack is **not** adopted —
overkill and a wider asset/attack surface; the hand-rendered approach is kept.

---

## 3. Capture expansion (`Themia.Exceptional`)

### 3.1 Schema — one new column, new forward-only migration

A **new** FluentMigrator migration (the deployed `ExceptionLogMigration` is never edited — forward-only
per `database.md`) adds **one nullable column `RequestContext`** (large text:
`NVARCHAR(MAX)` / `LONGTEXT` / `TEXT`) to the `Exceptions` table, via the existing `IfDatabase` lockstep
across SQL Server / MySQL / PostgreSQL. Nullable + additive → safe on existing rows and existing engines.

One column (a JSON document) rather than five — mirrors the existing `Detail` JSON convention and keeps
the migration + dialect changes minimal. Shape:

```json
{
  "headers":          { "User-Agent": "...", "Accept": "..." },
  "cookies":          { "session": "***", "theme": "dark" },
  "queryString":      { "id": "42" },
  "form":             { "email": "x@y.com", "password": "***" },
  "serverVariables":  { "REMOTE_ADDR": "::1", "SERVER_NAME": "..." }
}
```

### 3.2 Entity + store wiring

- `ExceptionEntry` gains `string? RequestContext`.
- `ExceptionEntryFactory` populates it (the serialized, redacted document).
- `IExceptionalSqlDialect.InsertSql` / `ListSql` / detail-select and the row mapping in
  `ExceptionStoreEngine` include `RequestContext` (updated in all three dialect packages). `ListSql` may
  omit the column for list performance and fetch it only on detail `GetAsync` — decided in the plan;
  default is to select it on detail only.

### 3.3 Capture — `HttpContextEnricher`

When `ExceptionalOptions.CaptureRequestContext` is true, the enricher reads from `HttpContext.Request`:
request **headers**, **cookies**, **query**, **form** (only when the content type is form-encoded and
already buffered — never force-reads/rewinds the body), and a small set of **server variables**
(`REMOTE_ADDR`, `SERVER_NAME`, `SERVER_PORT`, `REQUEST_METHOD`, protocol). Each key/value passes through
the `Redactor` before serialization. Result serialized with `System.Text.Json` into `RequestContext`.

### 3.4 Options — where it's set

On the existing `ExceptionalOptions` (sibling to `CaptureQueryString`), configured in the `configure`
lambda of `AddThemiaExceptionalPostgres` / `…SqlServer` / `…MySql` — i.e. the **capture/store
registration**, process-wide, once at startup. Not on the dashboard mount.

```csharp
public bool CaptureRequestContext { get; set; } = false;   // default OFF — backwards-compatible
public Func<string, string, string?>? Redactor { get; set; } = DefaultRedactor;
```

- **Default `false`**: existing consumers (PowerACC, ezy) are byte-for-byte unchanged until they opt in,
  matching how `CaptureQueryString` defaults off.
- **`Redactor`** `(key, value) -> masked|null`: returns the value to store, a mask, or `null` to drop the
  entry. **`DefaultRedactor`** masks only the categorical secrets — `Authorization` header,
  `Cookie`/`Set-Cookie` values, and keys matching `password|secret|token|apikey|authorization`
  (case-insensitive) → `"***"` — and stores **everything else verbatim** (all bad-data payload).
  Set `Redactor = null` to capture everything raw (the consumer's explicit choice); the host owns that
  data-protection trade-off.

**Security note (accepted trade-off):** storing request context — even redacted — widens what lives in
the error DB. The default keeps live session cookies / `Authorization` tokens out (account-takeover
guardrail; the global security rule against storing secrets holds *by default*), while leaving the
offending request data visible for debugging. The consumer can widen (own `Redactor`) or disable
(`Redactor = null`) capture redaction, and capture is off entirely unless `CaptureRequestContext` is set.

ezy: `o.CaptureRequestContext = true;` in its `AddThemiaExceptionalPostgres(...)` call.

---

## 4. Dashboard (`Themia.Exceptional.AspNetCore`)

### 4.1 Rendering approach

Keep the **hand-rendered HTML** (no Razor, no Handlebars/Semantic-UI dependency) and add **one embedded
CSS file** (`EmbeddedResource`) served from a dashboard route for the SE look. **CSP-friendly:** no
CDN/external assets, no inline `<style>`/`<script>` beyond the linked CSS; any interactivity
(confirm-on-delete) is a tiny embedded JS file or progressive-enhancement, not inline. Every dynamic
value still routes through the mandatory `Enc()` = `WebUtility.HtmlEncode` (the primary security
control).

### 4.2 Detail view (the big win)

Parse the stored `Detail` JSON and render, SE-style:

- **Title** = exception type + message.
- **Stack trace** — the `StackTrace` rendered with **real line breaks**, monospaced, frames legible
  (no more raw escaped-JSON blob). `Inner` and `Data` shown when present.
- **Meta table** — the existing fields (Guid, Application, Machine, Tenant, Status, Method, Url, Host,
  IP, Source, Count, Created, Last log, Protected).
- **Request-context sections** — Server Variables / Request Headers / Cookies / QueryString / Form /
  Request Body, each a clean key/value table, populated from `RequestContext` (and the existing
  `RequestBody`). Sections render only when data is present; absent when `CaptureRequestContext` was off.
- **Action buttons** — Protect / Unprotect, Delete (§4.4).

### 4.3 List view

- **Relative time** ("14 secs ago", "2 days ago") from `LastLogDate`, with the absolute time on hover.
- **Per-type color/severity** accent on the type cell.
- **Summary header** — "**N errors** (last: …)" from the total + most-recent.
- Keep Themia's **App / Tenant / Count** columns (an SE-over advantage).
- **Row actions** — protect / delete (§4.4).
- Existing filter/search/paging (GET) unchanged.

### 4.4 State-changing actions (new CSRF surface)

Protect / Unprotect / Delete / HardDelete become **POST** endpoints on the dashboard route group (the
store already exposes these ops). They re-introduce the minimal CSRF surface the read-only v1 avoided,
guarded **self-contained** (no dependency on the host's antiforgery wiring):

- **Double-submit token:** each rendered page sets a random token in a `SameSite=Strict` cookie and a
  hidden form field; the POST handler requires the two to match.
- **Same-origin check:** `Origin`/`Referer` must match the dashboard host.
- The existing **fail-closed `Authorize`** gate runs first on every POST (404 on deny), same as GET.
- `Purge` (bulk destructive) stays out of the UI.

### 4.5 Dashboard options

`ExceptionalDashboardOptions` keeps `Authorize`, `DefaultPageSize`, `MaxPageSize`, `Title`,
`ShowRequestBody`; add `bool EnableActions { get; set; } = true` (protect/delete togglable) and
`bool ShowRequestContext { get; set; } = true` (render the new sections when present).

---

## 5. Public API additions (PublicAPI analyzer)

- `Themia.Exceptional`: `ExceptionalOptions.CaptureRequestContext`, `ExceptionalOptions.Redactor`,
  `ExceptionEntry.RequestContext` (and the `DefaultRedactor` if exposed).
- `Themia.Exceptional.AspNetCore`: `ExceptionalDashboardOptions.EnableActions`,
  `ExceptionalDashboardOptions.ShowRequestContext`. New POST routes are not public API (runtime routes).
- All XML-documented; `PublicAPI.Unshipped.txt` updated; clean under `TreatWarningsAsErrors`.

---

## 6. Error handling & security

- Encoding via `Enc()` is mandatory on every dynamic value (extended to the new request-context
  sections) — the primary XSS control and the primary test.
- POST without a valid double-submit token or failing the same-origin check → rejected (400/404).
- `Authorize` unset/denied → 404 (unchanged, fail-closed).
- `OperationCanceledException` propagates; store exceptions propagate to the host handler. `ILogger<T>`
  only; `System.Text.Json` only.
- Redaction default keeps categorical secrets out of the store (§3.4).

---

## 7. Testing

**`Themia.Exceptional` (capture/store):**

- Enricher records headers/cookies/query/form/server-vars into `RequestContext` when
  `CaptureRequestContext = true`; nothing when false.
- **Redaction:** `Authorization` header, `Cookie` value, and `password`/`token`-named fields masked by
  default; other data verbatim; `Redactor = null` captures raw; custom `Redactor` honored.
- Additive migration runs across SQL Server / MySQL / PostgreSQL (Testcontainers); existing rows get
  `NULL` `RequestContext`; insert + detail round-trips the column on all three dialects.

**`Themia.Exceptional.AspNetCore` (dashboard, `WebApplicationFactory` + fake store):**

- Detail: stack trace rendered with line breaks (not raw JSON); request-context sections render when
  present and are absent when null/`ShowRequestContext = false`.
- List: relative-time + summary header rendered; App/Tenant/Count present.
- **XSS:** a `<script>` in message / a header value / a form value is HTML-encoded in every section.
- **CSRF:** POST protect/delete without a matching token or wrong `Origin` → rejected; with a valid
  token → store op invoked; `Authorize` denied → 404 even with a valid token; `EnableActions = false`
  hides the actions and rejects the POSTs.

---

## 8. Versioning, changelog, coord

- Bump `Directory.Build.props` `<Version>` `0.6.0 → 0.6.1`.
- CHANGELOG **Added**: request-context capture (`CaptureRequestContext` + `Redactor`) in
  `Themia.Exceptional`; SE-style dashboard (formatted trace, request-context sections, relative time,
  protect/delete) in `Themia.Exceptional.AspNetCore`. **Changed/Security:** note the opt-in capture +
  default redaction.
- Coord: advance the existing #0005 thread (or a follow-on) for ezy to enable `CaptureRequestContext`
  and adopt the upgraded dashboard.

---

## 9. Future improvements (not v1)

- Per-type/grouped rollup views, charts, real-time tail.
- A read JSON API for external tooling.
- Configurable column capture (pick which request-context groups to store).
