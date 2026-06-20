# Themia.Exceptional dashboard (0.5.8) Design

**Status:** Draft (2026-06-20)
**Scope:** A mountable, self-rendered, **read-only** exceptions dashboard (StackExchange.Exceptional-style
`/exceptions` UI) in a new `Themia.Exceptional.AspNetCore` package, backed by the existing
`IExceptionStore`. From coord #0005 (ezy-assets). Companion to
[`themia-architecture-overview.md`](../../themia-architecture-overview.md).

---

## 1. Milestone context

coord #0005: `Themia.Exceptional` was ported from StackExchange.Exceptional, which ships its own
`/exceptions` web UI. The store + dashboard-oriented query API are already present
(`IExceptionStore.ListAsync/GetAsync/CountAsync`, `ExceptionFilter`, `PagedResult`), but the web
rendering handler is missing, so every consumer hand-builds a bespoke viewer. ezy currently has **no**
exception viewer. The dashboard belongs in the framework.

**Resolved decisions (do not relitigate):**
- **New package `Themia.Exceptional.AspNetCore`** (`net8.0;net10.0`), not in the neutral store core —
  keeps HTML/UI rendering out of `Themia.Exceptional`, matching the repo's split-by-dependency habit.
- **Auth = a custom `Authorize` predicate** on the options (the coord request's shape), **fail-closed**:
  unset or returns false ⇒ the dashboard denies. Not ASP.NET `.RequireAuthorization` (self-contained,
  no dependency on the consumer's authorization wiring).
- **Read-only v1:** list + detail only. State-changing store ops (`Protect`/`Delete`/`HardDelete`/
  `Purge`) are deferred (they need POST + CSRF), so v1 has no CSRF surface.

Ships at **0.5.8** (a thin adapter completing an existing Phase-1 feature → PATCH under the pre-1.0
milestone policy; same class as the `Themia.MultiTenancy.Mediator` bridge shipped at 0.5.7). Single
shared monorepo version.

## 2. Package

`src/neutral/Themia.Exceptional.AspNetCore/Themia.Exceptional.AspNetCore.csproj`:
- `TargetFrameworks` = `net8.0;net10.0` (mirrors `Themia.Exceptional`).
- `FrameworkReference Include="Microsoft.AspNetCore.App"` (minimal-API endpoints, `HttpContext`).
- `ProjectReference` → `Themia.Exceptional` (the store + models).
- PublicAPI analyzer + `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt`.
- Added to `Themia.sln` via `dotnet sln add`.

No new NuGet runtime dependencies (HTML is hand-rendered; encoding via `System.Net.WebUtility`).

## 3. Public API

```csharp
namespace Themia.Exceptional.AspNetCore;

public sealed class ExceptionalDashboardOptions
{
    /// Gate for every dashboard request. Null ⇒ all requests are denied (fail-closed).
    public Func<HttpContext, ValueTask<bool>>? Authorize { get; set; }

    /// Default rows per page when the request omits pageSize. Default 50.
    public int DefaultPageSize { get; set; } = 50;

    /// Hard upper bound on rows per page (clamps the pageSize query param). Default 200.
    public int MaxPageSize { get; set; } = 200;

    /// Page heading / <title>. Default "Exceptions".
    public string Title { get; set; } = "Exceptions";

    /// Whether the detail view renders the captured request body (sensitive). Default true;
    /// only ever shown behind Authorize.
    public bool ShowRequestBody { get; set; } = true;
}

public static class ExceptionalDashboardEndpoints
{
    /// Mounts the read-only exceptions dashboard at <paramref name="path"/> (default "/exceptions")
    /// and returns the route group for further configuration.
    public static RouteGroupBuilder MapThemiaExceptional(
        this IEndpointRouteBuilder endpoints,
        string path = "/exceptions",
        Action<ExceptionalDashboardOptions>? configure = null);
}
```

Consumer usage (ezy):
```csharp
app.MapThemiaExceptional("/exceptions", o => o.Authorize = ctx => ValueTask.FromResult(ctx.User.IsInRole("SaaSAdmin")));
```

## 4. Endpoints (read-only, minimal API on the route group)

Both endpoints resolve `IExceptionStore` from request services and run the auth gate first.

- **`GET {path}`** — list. Parses query → `ExceptionFilter`:
  - `page` (≥1, default 1), `pageSize` (1..`MaxPageSize`, default `DefaultPageSize`),
  - `from`/`to` (UTC `DateTime?`), `app` → `ApplicationName`, `tenant` → `TenantId`,
  - `q` → `Search`, `includeDeleted` (bool).
  - Calls `ListAsync(filter)`; renders a filter form (GET), a table of rows, and prev/next paging
    derived from `PagedResult.Total` + page/pageSize.
- **`GET {path}/{guid:guid}`** — detail. `GetAsync(guid)`; **404** when null; otherwise renders all
  `ExceptionEntry` fields, the `Detail` JSON in a `<pre>`, and (when `ShowRequestBody`) `RequestBody`
  in a `<pre>`.

Rendered columns (list): `LastLogDate`, `CreationDate`, `ApplicationName`, `MachineName`, `Type`,
`Message` (already truncated by the store), `StatusCode`, `DuplicateCount`, `TenantId`, with the row
linking to `{path}/{Guid}`.

## 5. Authorization (fail-closed)

A single internal gate runs before any rendering:
```
if (options.Authorize is null) { warn-once; return 404; }
if (!await options.Authorize(httpContext)) return 404;
```
- **404, not 403**, for both unset and denied — hides the panel's existence from non-admins.
- When `Authorize` is null, log a **one-time WARN** ("dashboard mounted without an Authorize predicate;
  all requests denied") via `ILogger`, so the misconfiguration is visible without exposing data.
- The gate is the only auth mechanism; consumers can still layer `.RequireAuthorization(...)` on the
  returned route group if they prefer, but it is not required by this design.

## 6. Rendering & security

- **Server-rendered HTML**, built by a small internal helper; `Content-Type: text/html; charset=utf-8`.
- **Mandatory encoding:** every dynamic value passes through one `Enc(string?)` =
  `System.Net.WebUtility.HtmlEncode`. Exception data (`Message`, `Type`, `Detail`, `RequestBody`, `Url`,
  `Host`, `Source`, …) is attacker-influenceable and MUST be encoded — this is the primary security
  control and the primary test.
- **Self-contained:** a small inline `<style>` block; **no external/CDN assets, no inline scripts**
  (CSP-friendly). The filter/search/paging forms use `GET` only — no state change, no CSRF surface.
- `System.Text.Json` only (the `Detail` field is already JSON text; it is rendered encoded as-is, not
  re-serialized). Logging via `ILogger<T>` only.

## 7. Error handling

- Unauthorized / `Authorize` unset → 404 (§5).
- Detail `guid` not found → 404.
- Query param hygiene: `page` clamped to ≥1; `pageSize` clamped to 1..`MaxPageSize`; unparseable
  `from`/`to`/`includeDeleted` ignored (treated as unset) rather than erroring.
- Exceptions from `IExceptionStore` propagate to the host's error handling (e.g. the app's
  `ProblemDetailsMiddleware`); the dashboard does not render a partial/garbled page.

## 8. Out of scope (deferred / YAGNI)

- **State-changing actions** (`Protect`/`Delete`/`HardDelete`/`Purge`) — need POST + CSRF + confirm UI;
  deferred to a future slice.
- **JSON API**, charts/graphs, real-time updates, theming/customizable templates.
- **`.RequireAuthorization` integration** as the primary auth — rejected in favor of the self-contained
  predicate (§1); the route group is still returned so consumers may add it themselves.

## 9. Testing

`tests/Themia.Exceptional.AspNetCore.Tests` using `WebApplicationFactory` (repo standard) with an
in-memory `IExceptionStore` fake (returns seeded entries; records filter args):

- **Auth:** no `Authorize` configured → `GET {path}` returns 404; `Authorize` returns false → 404;
  `Authorize` returns true → 200.
- **List:** authorized → 200, body contains seeded rows and detail links; filter/paging query params
  flow into the `ExceptionFilter` passed to `ListAsync`; `pageSize` above `MaxPageSize` is clamped.
- **Detail:** existing `guid` → 200 with fields; unknown `guid` → 404; `ShowRequestBody=false` → body
  text absent.
- **XSS (key security test):** seed an entry whose `Message` (and `RequestBody`) is
  `<script>alert(1)</script>`; assert the response contains the HTML-encoded form
  (`&lt;script&gt;…`) and does **not** contain the raw `<script>` substring.
- New package builds on both TFMs with PublicAPI tracked; clean build (TWAE).
