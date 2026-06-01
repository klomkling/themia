# Themia Scheduling (Quartz Dashboard) — Design

> **Naming note:** target **Themia.\*** brand (rebrand of in-repo `zenity`/`zenity-v2`; the
> framework rename is a separate later task). `Themia.*` is NuGet-clean & reservable. See the
> architecture overview for full rationale. **Spec 1 of 2** (spec 2 = exception logging).

## Goal

Ship a **Quartz.NET scheduling + dashboard** capability for the new Themia framework (and its
planned apps), as two layers:

- `Themia.Quartz` — **framework-neutral** core: vendored SilkierQuartz dashboard + Quartz wiring
  + execution-history store contract. Depends on Quartz + ASP.NET Core only (no Themia.Framework,
  no Serenity).
- `Themia.Modules.Scheduling` — a Themia module (`IThemiaModule`) for the Themia apps; EF-backed
  tenant-aware history store via `Themia.Framework.Data.EFCore`.

**PowerACC is not a driver.** Themia is a new package built for itself + the new apps. PowerACC
(Serenity, net8) *may* reuse `Themia.Quartz` later via a thin Serenity adapter — that adapter is
**deferred / optional**, built only if/when PowerACC actually migrates (YAGNI). The neutral core
is kept Serenity-free purely to preserve that option (cheap here — SilkierQuartz is already
framework-neutral).

## Vendoring rationale (corrected)

We **vendor SilkierQuartz's source** for **full ownership/control** — *not* because it is
unmaintained (it is a maintained fork: `maikebing/SilkierQuartz`, MIT, ©2018–2026, net8/net10).
Ownership lets us re-namespace, trim, fix, and evolve it on our schedule. License = MIT → retain
the upstream copyright/permission notice (`THIRD-PARTY-NOTICES/SilkierQuartz-LICENSE.txt`) and
record the vendored commit (v10.0.0 `82f4eaa…`).

## Target frameworks (layered policy)

- `Themia.Quartz` (neutral core) → **`net8.0;net10.0`** — **must include net8** so PowerACC (net8)
  can reuse it. Quartz 3.18 supports net8.
- `Themia.Modules.Scheduling` → **`net10.0`** — consumers are the new (net10) Themia apps; PowerACC
  doesn't touch this layer.
- net12 added later when released (LTS pair net8+net10 for now).

## Architecture

```
Themia.Quartz                       # NEUTRAL — net8.0;net10.0 — Quartz + ASP.NET Core + Handlebars
  ├─ Dashboard (vendored SilkierQuartz: controllers, .hbs templates, embedded content)
  ├─ vendored RecentHistory plugin + its IExecutionHistoryStore (re-namespaced to Themia.Quartz)
  ├─ ThemiaQuartzOptions (branding, virtual path, time format, AUTH DELEGATE)
  └─ AddThemiaQuartz() / MapThemiaQuartz()

Themia.Modules.Scheduling           # net10.0 — depends on Themia.Quartz + Themia.Framework.{Core,Data.EFCore}
  ├─ SchedulingModule : ThemiaModuleBase   (IThemiaModule, ADR-0003)
  ├─ EfExecutionHistoryStore : <vendored IExecutionHistoryStore>   (EF, GLOBAL/admin — not tenant-scoped)
  └─ InitializeAsync → EF migration for the history table

[DEFERRED] Idevs.Net.CoreLib.Quartz  # Serenity adapter — built ONLY when PowerACC migrates
  └─ SqlExecutionHistoryStore (ISqlConnections) + Serenity branding/auth, on Themia.Quartz
```

Rule: `Themia.Quartz` references neither `Themia.Framework.*` nor `Serenity.*`.

## `Themia.Quartz` (neutral core)

### Dependencies
`Quartz` (3.18.x), `Microsoft.AspNetCore.App` (FrameworkReference), `Handlebars.Net`,
`JsonSubTypes`, `Newtonsoft.Json` — the last three are transitive from vendored SilkierQuartz.

### Vendoring (what to copy / change)
Copy upstream into `Themia.Quartz`, re-namespaced `SilkierQuartz` → `Themia.Quartz.Dashboard`:
- Controllers: Scheduler, Jobs, Triggers, Calendars, History, Executions, JobDataMap. **Drop
  `AuthenticateController` + its login views** — auth is host-supplied via the delegate (below).
- ViewModels/models; `.hbs` templates + JS/CSS/fonts as **embedded resources** (the labor-heavy
  part — preserve logical resource names; update the resource root namespace).
- Replace `SilkierQuartzOptions` → `ThemiaQuartzOptions` (Scheduler, VirtualPathRoot `/jobs`,
  UseLocalTime, date/time formats, `CronExpressionOptions.DayOfWeekStartIndexZero = false`,
  Logo, ProductName, **`Authorize` delegate**).

### History store — reuse the vendored plugin's seam (MAJOR 4 fix)
Since we own the full source, **also vendor the `RecentHistory` plugin** and re-namespace its
`IExecutionHistoryStore` as `Themia.Quartz`'s store contract. The vendored dashboard
History/Executions controllers already read from this plugin store via
`scheduler.Context.SetExecutionHistoryStore(...)` — so **do not invent a parallel interface**.
The default in-core implementation is an in-memory ring buffer; adapters provide durable stores.
(PowerACC's `CsiExecutionHistoryStore` already implements this exact shape — proof the contract
is the right seam.)

### Auth seam (framework-neutral)
`ThemiaQuartzOptions.Authorize : Func<HttpContext, Task<bool>>` (default-deny when unset). Each
host supplies its own (Themia claims / future Serenity claims). With `AuthenticateController`
removed, the dashboard relies entirely on this delegate + the host's auth pipeline.

### Newtonsoft.Json caveat (MINOR 6)
Vendored SilkierQuartz uses Handlebars.Net + Newtonsoft.Json internally, so `Themia.Quartz`
drags Newtonsoft into consumers even though Themia is System.Text.Json-first. Accepted trade-off
of vendoring; **keep Newtonsoft out of `Themia.Quartz`'s public API** so it stays an internal
implementation detail.

### Public API
```csharp
services.AddThemiaQuartz(o => { o.Scheduler = scheduler; o.VirtualPathRoot = "/jobs";
                                o.Authorize = ctx => ...; });
app.MapThemiaQuartz();
```

## `Themia.Modules.Scheduling` (Themia adapter)

- `SchedulingModule : ThemiaModuleBase`, deps `Themia.Framework.Core` + `.Data.EFCore`.
- `ConfigureServices`: register Quartz (host owns scheduler), `AddThemiaQuartz(...)` with a
  Themia-claims `Authorize`, and `EfExecutionHistoryStore` as the (vendored) `IExecutionHistoryStore`.
- `InitializeAsync`: run the EF migration. Uses `Themia.Framework.Data.EFCore` for persistence only.
- **Execution history is GLOBAL/admin, not tenant-scoped (MAJOR 2):** the Quartz scheduler is
  process-wide, jobs are infrastructure (not tenant-owned), and the RecentHistory dashboard shows
  scheduler-level aggregates — there is no useful tenant dimension. No `TenantId` column; the
  dashboard is a platform-admin surface, not per-tenant.
- Registered via `AddThemiaModule<SchedulingModule>()` + `InitializeThemiaModulesAsync()`.
- **Hard dependency:** this module cannot build until the Zenity→Themia framework rename (P0)
  lands. The neutral core has no such dependency and can start immediately.
- **Admin-UI note:** the dashboard should align with Themia's admin shell (year-1 phase-4) so the
  Quartz (Handlebars) and exception surfaces don't diverge in look/stack. Not blocking; flagged.

## PowerACC reuse path (deferred / optional)

If PowerACC later adopts `Themia.Quartz`: add `Idevs.Net.CoreLib.Quartz` (net8;net10) with a
`SqlExecutionHistoryStore` over `ISqlConnections` (its existing `QuartzExecutionHistoryRepository`
pattern) + Serenity branding/auth, then drop the `SilkierQuartz` NuGet refs. **Not built now.**

## Repo / build placement

- `Themia.Quartz` + tests → `Packages/themia/` (own solution; framework-neutral).
- `Themia.Modules.Scheduling` → Themia framework repo `src/modules/` (currently
  `Packages/nuget/zenity-v2/src/modules/` — rename pending).

## Standards

One concern per package; `PublicAPI.Shipped/Unshipped.txt`; full XML docs (no `CS1591`
suppression); analyzer-clean; nullable; warnings-as-errors. SemVer per package from `0.1.0`.

## Testing

- `Themia.Quartz`: in-memory store behavior; options/auth-delegate default-deny; dashboard smoke
  via `WebApplicationFactory` (routes resolve, embedded content served, unauthorized→403);
  **run on both net8 and net10**.
- `Themia.Modules.Scheduling`: module lifecycle (migration runs); `EfExecutionHistoryStore`
  round-trip + tenant isolation (Testcontainers).

## Verification
`dotnet build` (net8.0 + net10.0 for the core) · `dotnet test` (Docker for Testcontainers) ·
`dotnet list package --vulnerable`.

## Open items
1. Embedded-resource re-namespacing spike (validate logical names remap cleanly).
2. Quartz version float vs pin in `Themia.Quartz`.
3. Module naming `Themia.Modules.Scheduling` (recommended) vs `.Quartz`.

## Decomposition
Spec 2 = exception logging, same neutral-core + module pattern, Serenity adapter likewise deferred.
