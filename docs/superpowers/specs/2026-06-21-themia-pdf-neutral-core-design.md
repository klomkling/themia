# Themia.Pdf (Neutral Rendering Core) — Design

**Status:** Approved (brainstorming) — ready for implementation plan
**Date:** 2026-06-21
**Origin:** ezy-assets PDF path (port). Driver: `EzyAssets.Infrastructure.Pdf.ContractPdfService`.
**Target version:** 0.6.0 (Phase 2 boundary — first Phase-2 package).

---

## Goal

Port the **reusable, stateless** PDF-rendering engine out of ezy-assets into a neutral
cross-cutting core: merge an HTML template with a model, then print HTML → PDF bytes via
headless Chromium. No tenant state, no database, no persistence.

## Scope

This is **sub-project #1 of two**. The full ezy ask (decision "B") is:

1. **`Themia.Pdf`** — neutral rendering core (this spec).
2. **`Themia.Modules.Pdf`** — tenant-aware template store (schema, isolation, global fallback,
   CRUD). **Deferred to its own brainstorm → spec → plan → ship cycle.** Not covered here.

Build order is #1 then #2: the core is smaller, dependency-free, and unblocks ezy's rendering
immediately; the store module is a larger build (FluentMigrator schema across SQL Server / MySQL /
PostgreSQL, EF + Dapper peer support, tenant isolation, CRUD).

### In scope (this spec)

- `IHtmlTemplateRenderer` — template string + model → HTML (Handlebars.Net).
- `IPdfRenderer` — HTML + options → PDF bytes (PuppeteerSharp, managed browser lifecycle).
- `PdfRenderOptions` — per-render paper/margins/background.
- `ThemiaPdfOptions` — browser provisioning + launch config + custom Handlebars helpers.
- `AddThemiaPdf(...)` DI extension.

### Out of scope (this spec)

- The tenant/global **template store** (`ContractTemplate` + repository in ezy stays put for now;
  generalized into `Themia.Modules.Pdf` as sub-project #2).
- `ProposalPdfService` — ezy's hand-rolled raw-PDF writer. Near-zero neutral value; **not ported**.
  It stays in ezy (or ezy migrates proposals onto the HTML→PDF path if it wants).
- App-domain document types (`ContractPdfDocument`, `BuildContext`, contract field mapping) — these
  are ezy domain and stay in ezy. The core takes an opaque `object model`.

### Scope-guard check

Stateless HTML→PDF rendering and template merge are generic cross-cutting infrastructure (any app
needs them). The per-document field mapping is app domain and stays out. ✅

---

## Architecture

**One package:** `src/neutral/Themia.Pdf`, TFM `net8.0;net10.0` (neutral-core convention; both
dependencies support net8; keeps the PowerACC-reuse option open and matches `Themia.Quartz` /
`Themia.Exceptional`).

**Dependencies (central package management):**

- `Handlebars.Net` `2.1.6` — already pinned in `Directory.Packages.props`.
- `PuppeteerSharp` `20.0.5` — **new** pin to add (matches the ezy version).
- `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`
  — for `ILogger<T>` and the DI extension (already available in the repo).

**No dependency** on `Themia.Framework.*`, ASP.NET Core, EF, or Dapper. Pure infra.

---

## Components

### 1. `IHtmlTemplateRenderer` + `HandlebarsHtmlTemplateRenderer`

```csharp
public interface IHtmlTemplateRenderer
{
    /// <summary>Compiles <paramref name="template"/> as a Handlebars template and renders it
    /// against <paramref name="model"/>, returning the resulting HTML.</summary>
    string Render(string template, object model);
}
```

- Backed by a single `IHandlebars` engine created at construction (`Handlebars.Create()`), thread-safe
  for rendering.
- Ports the `emptyIfNull` helper as a **built-in default** (generic, no domain coupling).
- Consumers can register additional helpers via `ThemiaPdfOptions.ConfigureHandlebars` (see below).
- **v1 is port-faithful: compiles the template on every `Render` call** (matches ezy). A bounded
  compiled-template cache (keyed by template content) is a known, cheap future win — marked in code
  with a `ponytail:` comment naming the upgrade path. Not in v1 (YAGNI; no profiling yet).

### 2. `IPdfRenderer` + `PuppeteerPdfRenderer`

```csharp
public interface IPdfRenderer
{
    /// <summary>Prints <paramref name="html"/> to a PDF using headless Chromium and returns the bytes.</summary>
    Task<byte[]> RenderHtmlAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default);
}
```

- Ports `ContractPdfService`'s managed-browser pattern: a lazily-launched **singleton** `IBrowser`
  guarded by a `SemaphoreSlim`, reused across renders, reconnected if `IsConnected` is false.
- Implements `IAsyncDisposable` (closes + disposes the browser, disposes the semaphore).
- Per render: `NewPageAsync` → `SetContentAsync(html)` → `PdfDataAsync(options)` → return bytes.
  Page is `await using` (disposed per render); the browser is **not**.

### 3. `PdfRenderOptions`

Per-render output options. Defaults ported from ezy:

| Property | Default |
|---|---|
| `PaperFormat` (A4/Letter/…) | `A4` |
| `PrintBackground` | `true` |
| `MarginTop` / `MarginBottom` | `"20mm"` |
| `MarginLeft` / `MarginRight` | `"15mm"` |

All overridable per call. `null` options ⇒ all defaults.

### 4. `ThemiaPdfOptions`

Process-wide browser provisioning + engine config (bound once at DI registration):

| Property | Behavior |
|---|---|
| `ExecutablePath` (`string?`) | If set, launch this Chrome/Chromium instead of a fetched one (system/container browser). Default `null`. |
| `DisableAutoDownload` (`bool`) | When `false` (default) and no `ExecutablePath`, `BrowserFetcher().DownloadAsync()` runs on first use (faithful ezy behavior). When `true`, auto-download is skipped — an `ExecutablePath` (or an already-provisioned browser) is then required, else launch throws a clear `InvalidOperationException`. |
| `LaunchArgs` (`string[]`) | Chromium launch args. Default `["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"]` (ported). |
| `Headless` (`bool`) | Default `true`. |
| `ConfigureHandlebars` (`Action<IHandlebars>?`) | Hook to register custom helpers/partials on the template engine at construction. Default `null`. |

**Provisioning precedence at first launch:** `ExecutablePath` set → use it. Else `DisableAutoDownload == false` → fetch+launch. Else → throw `InvalidOperationException` (no browser available, auto-download disabled, no `ExecutablePath`).

### 5. DI extension

```csharp
public static IServiceCollection AddThemiaPdf(
    this IServiceCollection services,
    Action<ThemiaPdfOptions>? configure = null);
```

- Binds `ThemiaPdfOptions` (applies `configure`).
- Registers `IHtmlTemplateRenderer` → `HandlebarsHtmlTemplateRenderer` as **singleton** (stateless,
  thread-safe).
- Registers `IPdfRenderer` → `PuppeteerPdfRenderer` as **singleton** (owns the long-lived browser).
- Idempotent via `TryAdd*`.

---

## Error handling & logging

- Render failures **throw** — the consumer (or an ASP.NET Core middleware) owns any HTTP mapping.
  Exceptions stay HTTP-agnostic (project convention: no `StatusCode` on exceptions).
- `ILogger<T>` only — no `Console.*` (deliberate fix vs. the ezy source). Log browser
  download/launch at `Information`; launch/render failures at `Error`.
- `OperationCanceledException` propagates as cancellation — never caught-and-swallowed.
- `BrowserFetcher().DownloadAsync()` and `LaunchAsync` happen under the `SemaphoreSlim` with the
  double-checked `IsConnected` guard (ported), so concurrent first-renders launch exactly one browser.

---

## Public API surface (PublicAPI analyzer)

New public members (added to `PublicAPI.Unshipped.txt`, XML-documented):

- `Themia.Pdf.IHtmlTemplateRenderer` (+ `Render`)
- `Themia.Pdf.IPdfRenderer` (+ `RenderHtmlAsync`)
- `Themia.Pdf.PdfRenderOptions` (+ properties)
- `Themia.Pdf.ThemiaPdfOptions` (+ properties)
- `Microsoft.Extensions.DependencyInjection.ThemiaPdfServiceCollectionExtensions.AddThemiaPdf`

Concrete implementations (`PuppeteerPdfRenderer`, `HandlebarsHtmlTemplateRenderer`) are **`internal`** —
consumers depend on the interfaces via DI, so they stay off the public API surface.

---

## Testing strategy

**Unit (no Chromium):**

- `HandlebarsHtmlTemplateRenderer.Render` merges a template + model correctly.
- `emptyIfNull` helper renders empty string for null, value otherwise.
- `ConfigureHandlebars` hook registers a custom helper that then renders.
- `PdfRenderOptions` defaults are A4 / background / 20-20-15-15.
- `AddThemiaPdf` registers both interfaces as singletons; `configure` is applied; idempotent.
- `ThemiaPdfOptions` provisioning precedence: `DisableAutoDownload == true` with no `ExecutablePath`
  ⇒ launching throws a clear `InvalidOperationException` (assert message mentions the misconfig).

**Integration (requires Chromium — gated like other heavy suites):**

- `PuppeteerPdfRenderer.RenderHtmlAsync` on a small HTML string returns bytes beginning with the
  `%PDF-` magic header and length > 0.
- Concurrent renders reuse a single browser (no second launch) — assert via a launch counter or log.
- `DisposeAsync` closes the browser.

CI note: the integration suite needs a Chromium provision step (auto-download on first use, or a
system Chrome on the runner). Surface the requirement in the test project README / CI config; do not
silently skip.

---

## Versioning, changelog, coord

- Bump `Directory.Build.props` `<Version>` `0.5.10 → 0.6.0` (Phase 2 opens; new package).
- CHANGELOG: **Added — `Themia.Pdf` neutral HTML→PDF rendering core (Handlebars.Net + PuppeteerSharp)**.
- Log a coord request (ezy → Themia.Pdf) to track the origin before release; mark
  accepted → in_progress → released through the cycle.

---

## Future improvements (not v1)

- Bounded compiled-template cache in `HandlebarsHtmlTemplateRenderer` (keyed by template content) —
  cheap win when profiling shows compile cost matters.
- `Themia.Pdf.AspNetCore` thin helper (`IResult`/`FileContentResult` from bytes) — only if a
  consumer wants it (YAGNI).
- Sub-project #2: `Themia.Modules.Pdf` tenant/global template store.
