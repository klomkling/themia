# Themia.MultiTenancy — Typed TenantId + claims resolution (0.5.6) Design

**Status:** Draft (2026-06-18)
**Scope:** Additive, **non-breaking** enhancements to the tenancy core so JWT/int/guid apps adopt
Themia tenancy with low friction: a `TenantId` that constructs from and extracts to `int`/`long`/`Guid`/
`string` over a canonical string key, and a built-in `ClaimsTenantResolutionStrategy`. From coord #0003
(ezy-assets). Companion to [`themia-architecture-overview.md`](../../themia-architecture-overview.md).

---

## 1. Milestone context

coord #0003 asked for: (1) a claims-based tenant strategy, (2) int/long/guid TenantId support, (3) a
tenant+user identity context, (4) resolution without an `ITenantStore` catalog. The driver is **forward
looking** — ezy's real need is a future DB-per-tenant model; today's shared-DB int-claim model has no
hard requirement, so this is deliberately the **smallest additive change** that removes the adoption
friction, not a rewrite.

**Resolved decision (do not relitigate):** support int/long/guid/string as a **typed value over a
canonical string key**, NOT a generic `TTenantId` threaded through the framework, and NOT a configurable
column type. Rationale: `tenant_id` is a `string` column in every module's FM schema + EF/Dapper filters
+ filtered-unique indexes + `StorageScope`/Identity platform-null. Generic columns would make
`ITenantEntity<TTenantId>` / `IRepository<T,TKey,TTenantId>` / every module schema generic and breaking
— huge blast radius. And the future **DB-per-tenant** model makes the *connection* the tenant, so
row-level `tenant_id` typing matters **less**, not more. A canonical string key (an int/guid stored as
its string form) covers all four representations at the storage boundary; the consumer works in its
native type at its own boundary via typed construct/extract.

Ships at **0.5.6** (a backwards-compatible additive feature — same class as 0.5.4/0.5.5; under the
project's pre-1.0 policy MINOR is reserved for new modules / phase boundaries, so this is a PATCH).
Single shared monorepo version.

## 2. `TenantId` — typed construct/extract (Core, additive)

In `Themia.Framework.Core.Abstractions.Tenancy.TenantId` (a `readonly record struct` over a validated
`string Value`, 1–100 chars, alphanumeric + `-`/`_`). All additions are new members — the existing
`string` ctor, `From(string?)`, validation, and `TenantId?` nullability are unchanged.

**Construct** (canonical string encoding, invariant culture):
- `static TenantId From(int value)` → `value.ToString(InvariantCulture)` (decimal digits).
- `static TenantId From(long value)` → invariant decimal.
- `static TenantId From(Guid value)` → `value.ToString("D")` (hyphenated hex; `"D"` is already
  lowercase in .NET — no extra lowercasing).

All three encodings fall within the existing charset (digits; `D`-format = hex + `-`), so **the existing
validation is unchanged** and always passes for these factories.

**Extract** (round-trips the canonical encoding):
- `int AsInt32()` / `long AsInt64()` / `Guid AsGuid()` — throw `FormatException` when `Value` isn't that
  representation.
- `bool TryAsInt32(out int)` / `TryAsInt64(out long)` / `TryAsGuid(out Guid)` — no-throw.
- `string Value` (already present) is the string accessor.

Explicit per-type methods are used over a generic `As<T>()` to avoid a runtime type switch.

**Why not on the consumer side only:** putting `From`/`As` on `TenantId` means the consumer adapts at
exactly one place (claim read / repo boundary) instead of hand-formatting `ToString()`/`int.Parse()` at
every call site.

## 3. `ClaimsTenantResolutionStrategy` (MultiTenancy)

A built-in `ITenantResolutionStrategy` (the interface is already public; this is the natural fit for JWT
apps) in `Themia.MultiTenancy/Strategies/`:

- `ClaimsTenantResolutionStrategy` reads `TenantResolutionContext.Claims[claimType]` (the `Claims` dict
  is already populated by `TenantResolutionMiddleware` from `HttpContext.User.Claims`; the strategy is
  host-agnostic, exactly like `HeaderTenantResolutionStrategy` reads `context.Headers`). The claim value
  is used verbatim as the canonical string identifier (the consumer's int/guid is its own concern via
  the typed `TenantId.From`/`As` members in §2).
- **Returns `Resolved`, not `Identified` — this is the no-catalog mechanism** (covers coord items 1 & 4).
  On a present/non-blank claim it returns `TenantResolutionResult.Resolved(new TenantInfo(Id: value,
  Identifier: value), claimType)`; absent/blank → `TenantResolutionResult.NotFound(claimType, …)`.
  Rationale (traced through `DefaultTenantResolver`): a result carrying a non-null `Tenant` is returned
  **directly, bypassing `ITenantStore`**; an `Identified` result (identifier only) instead routes through
  `ITenantStore.FindByIdentifierAsync`, which yields *nothing* against an empty store — defeating the
  no-catalog guarantee. So the claim *is* the tenant: build a minimal `TenantInfo` from it (both `Id` and
  `Identifier` are required non-null on `TenantInfo`, so both take the claim value) and skip the catalog.
  **Single-mode by design:** this strategy never consults a store. An app that wants catalog
  enrichment/validation keeps using the Header/Path strategies + a populated `ITenantStore` (no hard
  catalog requirement exists for the JWT case today — see §1). No `validateAgainstStore` toggle.
- **Configuration** mirrors the options-driven shape of the existing strategies: add a `ClaimType`
  property to `MultiTenancyOptions`, expose an **arg-less** `MultiTenancyBuilder.UseClaimsStrategy()`
  that registers via `AddStrategy<ClaimsTenantResolutionStrategy>()` (singleton — the strategy is
  stateless), and have the ctor read `IOptions<MultiTenancyOptions>` for the claim type, exactly like
  `HeaderTenantResolutionStrategy` reads `HeaderName`. (A parameterized `UseClaimsStrategy(claimType)`
  would need a factory registration and would *not* match the existing `AddStrategy<T>()` shape.)

## 4. Tenant + user identity (docs only — no new framework API)

coord item 3 wants tenant + user (UserId/Role/IsSaaSAdmin) from one seam. The framework already provides
`ITenantAccessor` (tenant) and, in the Identity module, `ICurrentUser` (user id / roles / is-platform).
`Themia.MultiTenancy` (framework) **must not** depend on `Themia.Modules.Identity` (a module) — layering.
So the "one seam" is a thin **app-level composition** of `ITenantAccessor` + `ICurrentUser`, which is
what a consumer like ezy keeps as its own `ITenantContext`. This slice adds **documentation/guidance**
for that composition, not a combined framework accessor.

## 5. Out of scope (deferred / YAGNI)

- **Generic `TTenantId` / typed `tenant_id` columns** — rejected fork (§1). The string key stays.
- **A combined framework identity accessor** — layering (§4); compose existing seams.
- **DB-per-tenant connection routing** — this is ezy's *real* future driver and a substantial cycle of
  its own (per-tenant connection resolution, migration fan-out, store of tenant→connection). Explicitly
  **not** in this slice; flag for a future MultiTenancy cycle.

## 6. Testing

- `TenantId` units: `From(int/long/Guid)` → expected `Value`; `AsInt32/AsInt64/AsGuid` round-trip;
  `TryAs*` true/false paths; `As*` throws `FormatException` on a mismatched value; a `Guid`-from-`From`
  round-trips through `AsGuid`.
- `ClaimsTenantResolutionStrategy` units: claim present → `Resolved` with a `TenantInfo` whose `Id` and
  `Identifier` both equal the claim value; claim absent/blank → `NotFound`; configured `ClaimType`
  honored. (Drive it with a `TenantResolutionContext` carrying a `Claims` dict — no host/`HttpContext`
  needed.)
- No-catalog resolution: `DefaultTenantResolver` + claims strategy resolves a tenant with an **empty
  `ITenantStore`** (the design's core coord-item-4 guarantee — asserts the `Resolved` result bypasses
  the store).
- Neutral-core/MultiTenancy TFMs + PublicAPI tracked; build clean (TWAE).
