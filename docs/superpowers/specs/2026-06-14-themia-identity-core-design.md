# Themia.Modules.Identity — Core slice (0.5.0) Design

**Status:** Accepted (2026-06-14)
**Scope:** The first slice of the full Identity provider — the tenant-aware user/role/claim store,
password + token + lockout logic, the current-user principal, and ASP.NET Core authorization
integration. Companion to [`themia-architecture-overview.md`](../../themia-architecture-overview.md)
(module catalog row `Themia.Modules.Identity`) and
[`2026-06-01-themia-release-strategy-design.md`](2026-06-01-themia-release-strategy-design.md)
(0.5.0 = remaining Phase-1 modules).

---

## 1. Milestone context — Identity is built as a sequence of slices

The user-approved target is a **full auth provider**. That is too large for one spec, so it is
decomposed into dependency-ordered slices, each a shippable PATCH within the 0.5.x line, each with
its own spec → plan → implement cycle:

| Slice | Deliverable | Depends on |
|---|---|---|
| **0.5.0 — Identity core** *(this spec)* | Tenant-aware user/role/claim store, password hashing, account-lifecycle tokens + lockout, current-user principal, ASP.NET Core authorization integration | framework only |
| **0.5.1 — JWT** | Access-token issuance from the principal, revocable refresh tokens, validation middleware, login endpoints | Identity core |
| **0.5.2 — External login** | Pluggable external-login abstraction + LINE (OAuth) implementation, external-identity linking | core + JWT |

This document specifies **0.5.0 only**. The two later slices are named for ordering, not detailed here.

## 2. Resolved decisions (do not relitigate)

These were settled during brainstorming:

1. **Full auth provider** is the target (largest scope), delivered as the slices above.
2. **Hybrid tenancy:** tenant-scoped users by default **plus** platform/host users that span all
   tenants (super-admins).
3. **Full account lifecycle** in the core: users, credentials with **lockout**, email/phone
   **confirmation tokens**, **password-reset tokens**, a **2FA hook**, roles, claims, plus the
   principal + policy integration.
4. **Both data-access peers** via `Themia.Framework.Data.Abstractions` — one store implementation
   runs on whichever layer (EF Core or Dapper) the adopter chose. FluentMigrator owns one schema.
5. **Approach B — fully custom** identity services (no ASP.NET Core *Identity* dependency); we
   integrate ASP.NET Core *authorization*.
6. **argon2id** default password hasher (`Konscious.Security.Cryptography`), behind a pluggable
   `IPasswordHasher`.
7. **Adopter extension = Option C** — Themia's `users` table is sealed to auth columns; adopters add
   their own 1:1 profile table (FK to `users.id`) in their own migration. Services stay non-generic.
   Generic `TUser` is a documented future option, not built now.
8. **PG + SQL Server** at 0.5.0; **MySQL deferred** (consistent with EF MySQL blocked on Pomelo's
   EF Core 10 build).

## 3. Package layout & boundaries

```
src/modules/
  Themia.Modules.Identity.Abstractions   (net10)  — entities, service interfaces,
       │                                             ICurrentUser principal, IPasswordHasher,
       │                                             token/option contracts. No EF/Dapper dependency.
  Themia.Modules.Identity                (net10)  — IdentityModule : ThemiaModuleBase,
                                                     service implementations over Data.Abstractions,
                                                     Argon2idPasswordHasher, FluentMigrator schema,
                                                     EF model-contribution + Dapper mapping,
                                                     ASP.NET Core authorization integration.
```

- **One implementation, both peers.** Services depend only on `IRepository<T>`/`IReadRepository<T>`/
  `ISpecification<T>`/`IUnitOfWork` + `IDataFilterScope` — no EF or Dapper reference. The adopter has
  already chosen and registered a layer; Identity runs on it.
- **Abstractions split** mirrors `Themia.Framework.Data.Abstractions` so the later JWT / external-login
  slices depend only on `Identity.Abstractions`, not the implementation.
- **Slice boundary:** store + services + principal + authorization + the real `ICurrentUserAccessor`.
  **No HTTP endpoints and no token issuance** in 0.5.0 — login/JWT is 0.5.1. The module exposes
  services the host (or the 0.5.1 slice) calls.

## 4. Domain model & schema

FluentMigrator, one schema (`IfDatabase("postgres","sqlserver")`), all tables in an `identity` schema,
snake_case columns, with the framework's audit + soft-delete + concurrency columns applied via the
existing conventions.

`tenant_id` is a `varchar(100)` string (the framework's `TenantId` is a validated string, **not** a
Guid). A `NULL` `tenant_id` is the framework's existing **"global record"** marker.

| Table | Key columns / notes |
|---|---|
| **users** | `id` (Guid PK), `tenant_id` (varchar(100), NULL ⇒ platform user), `user_name` + `normalized_user_name`, `email` + `normalized_email`, `email_confirmed`, `phone_number` + `phone_number_confirmed`, `password_hash`, `security_stamp`, `is_active`, lockout: `access_failed_count`, `lockout_end`, `lockout_enabled`, 2FA hook: `two_factor_enabled`, + audit/soft-delete/concurrency |
| **roles** | `id`, `tenant_id` (NULL ⇒ platform role), `name` + `normalized_name`, `description` |
| **user_roles** | (`user_id`, `role_id`) composite PK |
| **user_claims** | `id`, `user_id`, `claim_type`, `claim_value` |
| **role_claims** | `id`, `role_id`, `claim_type`, `claim_value` |
| **user_tokens** | `id`, `user_id`, `purpose` (`EmailConfirm`\|`PhoneConfirm`\|`PasswordReset`\|`TwoFactor`), `token_hash`, `expires_at`, `consumed_at` — persisted, single-use, expiring; token **hashes** stored, never raw |

**Platform vs tenant users use the framework's global-record convention: `tenant_id IS NULL` = platform
(spans all tenants), non-null = tenant-scoped.** There is no separate `scope` column — platform ⇔
`TenantId is null`, matching how the framework already models cross-tenant rows (cf.
`DapperDataOptions.IncludeGlobalRecordsForTenants`). Platform users/roles are read by bypassing the
tenant filter (`Specification.WithoutTenantFilter()` / `IDataFilterScope`).

**Indexes / uniqueness** — a composite unique index on a *nullable* `tenant_id` behaves differently per
engine (PostgreSQL treats NULLs as distinct → would allow duplicate platform logins; SQL Server allows
only one NULL row total → too restrictive), so uniqueness is split into **two filtered unique indexes**
per table, emitted per-engine via `IfDatabase("postgres"|"sqlserver").Execute.Sql(...)` (both engines
support `WHERE` on a unique index):
- `users`: `unique(tenant_id, normalized_user_name) WHERE tenant_id IS NOT NULL` and
  `unique(normalized_user_name) WHERE tenant_id IS NULL`; likewise for `normalized_email`.
- `roles`: `unique(tenant_id, normalized_name) WHERE tenant_id IS NOT NULL` and
  `unique(normalized_name) WHERE tenant_id IS NULL`.
- plain index `user_tokens(user_id, purpose)`.

**Per-tenant uniqueness:** the same username/email may exist in different tenants. Normalization
(upper-invariant) is stored so lookups are index-friendly and case-insensitive without depending on DB
collation.

**Adopter extension (Option C):** the `users` table is sealed to auth columns. Adopters add a 1:1
profile entity/table (FK to `users.id`) in their own FluentMigrator migration, loaded by user id.
Themia never touches it. This honours the framework/app scope-guard boundary (identity ≠ app profile
data) and lets `users` evolve without colliding with adopter columns.

## 5. Services & logic

Interfaces in `Identity.Abstractions`; implementations in `Themia.Modules.Identity` over
`Data.Abstractions`. Services are **non-generic** (per decision #7).

- **`IUserService`** — `CreateAsync`, `FindByIdAsync`, `FindByUserNameAsync`, `FindByEmailAsync`,
  `SetPasswordAsync`, `VerifyPasswordAsync` (drives lockout), `SetActiveAsync`, `UpdateAsync`,
  `DeleteAsync` (soft). Username/email normalization (upper-invariant) happens here.
- **`IRoleService`** — role CRUD, `AssignRoleAsync` / `RemoveRoleAsync(userId, roleId)`.
- **`IClaimService`** — add/remove user claims and role claims; `GetEffectiveClaimsAsync(userId)`
  returns the union of user claims + claims from assigned roles. Feeds the principal builder.
- **`IPasswordHasher`** — `Hash(password)` / `Verify(hash, password)`; default `Argon2idPasswordHasher`
  (Konscious), swappable via DI. The hash format embeds algorithm parameters so future re-tuning is
  detectable (rehash-on-verify hook).
- **`IUserTokenService`** — `GenerateAsync(userId, purpose, ttl)` returns a raw token **once** while
  persisting only its **hash**; `ConsumeAsync(userId, purpose, rawToken)` validates hash + expiry +
  single-use (`consumed_at`). Powers email/phone confirmation and password reset. The **2FA "hook"** is
  `two_factor_enabled` + a `TwoFactor` token purpose — not a full TOTP implementation (later slice / app
  concern).

**Lockout logic** lives in `VerifyPasswordAsync`: on failure increment `access_failed_count`; at the
configured threshold set `lockout_end`; on success reset the counter. Thresholds/lockout window come
from `IdentityModuleOptions`.

**Results vs exceptions.** Expected outcomes are typed results, not exceptions:
`VerifyPasswordAsync` returns `PasswordVerificationResult` (`Success` | `Failed` | `LockedOut` |
`Inactive` | `NotFound`). Genuine faults (DB down, concurrency conflict) still throw. No
exception-as-control-flow.

## 6. Tenancy, current-user principal, authorization

**Platform vs tenant resolution.** Tenant users are found within the ambient tenant (resolved from
path/header before any login attempt) — the framework's row-level filter scopes the query
automatically. Platform users (`tenant_id IS NULL`, global records) are looked up by bypassing the
tenant filter (`Specification.WithoutTenantFilter()`). `IUserService.FindByUserNameAsync` tries the
ambient-tenant row first, then optionally the platform (global) scope
(`IdentityModuleOptions.AllowPlatformLogin`, default `true`) so a super-admin can sign in against any
tenant's entry point.

**Current-user principal — two layers:**
- **`ICurrentUserAccessor`** (the existing `Data.Abstractions` seam, currently a null stub) gets a real
  implementation returning the authenticated user's id — so audit stamping (`created_by`/`modified_by`)
  reflects the real user.
- **`ICurrentUser`** (new, in `Identity.Abstractions`) — richer ambient principal: `UserId`, `TenantId`
  (null ⇒ platform), `IsPlatform` (⇔ `TenantId is null`), `UserName`, `Roles`, `Claims`,
  `IsAuthenticated`, `IsInRole(...)`. Backed by a scoped accessor populated from the `ClaimsPrincipal`
  on `HttpContext.User`. Application code injects this.

**`ClaimsPrincipalFactory`** turns a `User` into claims: subject id, tenant id (omitted for platform
users), user name, a `role` claim per assigned role, plus the effective claims from
`IClaimService.GetEffectiveClaimsAsync`.
The 0.5.1 JWT slice uses this factory to mint token claims; in 0.5.0 it is available for hosts already
authenticating by cookie. It is the **single source of "what's in the principal"** across slices.

**Authorization** integrates with **ASP.NET Core authorization** (not ASP.NET Core Identity): roles flow
as standard `role` claims (`[Authorize(Roles=...)]` works); claims flow for `[Authorize(Policy=...)]`
with stock policy registration. The module ships `AddThemiaIdentityAuthorization()` registering
`ICurrentUser` and the principal-population middleware/handler. This lets `Themia.Modules.Scheduling`
replace its "authenticated-only" dashboard gate with a real admin-role check.

## 7. EF / Dapper integration (the one framework touch)

Identity is the first module to ride the shared `IRepository<T>`, so its entities must reach both
backends. The adopter has already registered a layer; Identity contributes its entity metadata to
whichever is present.

- **EF Core.** Identity ships `IEntityTypeConfiguration<>` classes (table names in the `identity`
  schema, the filtered unique indexes, key types, relationships). The adopter applies them in their
  `ThemiaDbContext`-derived `OnModelCreating` via a one-liner: `modelBuilder.ApplyThemiaIdentity();`
  (applies the module's `IEntityTypeConfiguration<>` set). `EfRepository<User>` then resolves through the app's
  context, and the framework's tenant-filter/soft-delete/audit conventions apply automatically because
  the entities implement `ITenantEntity` etc.
- **Dapper.** `AddThemiaIdentity()` registers the Identity entity mappings into the
  `EntityMappingRegistry` (table + column names), so `DapperRepository<User>` resolves the same schema.
  No adopter `OnModelCreating` step (Dapper has no model).
- **Loud failure.** EF needs the configs *inside the adopter's own `DbContext`* (Themia does not own
  their context type), so it cannot be fully hidden — but it is a single documented call. The module
  detects at startup whether `User` is present in the EF model and throws a clear configuration error if
  the module is registered but `ApplyThemiaIdentity()` was not called — not a confusing runtime
  "no DbSet" error.

This contribution mechanism is **reusable** — ExceptionLogging and future modules that use the shared
repositories follow the same `ApplyThemiaX()` + registry pattern. It is a small, general framework
addition, not an Identity special case.

## 8. Error handling & security

- Expected outcomes are typed results (§5); genuine faults throw. Concurrency conflicts surface as the
  framework's existing `ConcurrencyException`.
- Token verification uses a **constant-time** hash compare and never reveals whether the user or the
  token was wrong (no user enumeration). Password verify returns a uniform `Failed` regardless of
  whether the user exists.
- Token **hashes** are stored, never raw tokens; raw tokens are returned once at generation.
- The "module registered but EF configs not applied" guard fails loudly at startup (§7).

## 9. Testing

- **Unit:** password hasher (hash ≠ plaintext, verify round-trip, wrong-password fails, rehash
  detection), normalization, lockout state machine (threshold → lockout → reset), token
  generate/consume (single-use, expiry, wrong-token), `ClaimsPrincipalFactory` (roles + effective
  claims), `GetEffectiveClaimsAsync` union.
- **Integration (Testcontainers):** the full store against **both** EF and Dapper × **both** PG and SQL
  Server — create user, find-by-username within tenant, platform-user bypass lookup, per-tenant
  uniqueness collision, role/claim assignment, soft-delete, audit-stamp via the real
  `ICurrentUserAccessor`. The DECISION #6 parity proof for Identity.
- **Migration:** FluentMigrator apply/idempotent test per engine.

## 10. Out of scope for 0.5.0

Deferred to later slices or the adopter:
- JWT / token issuance + refresh tokens + login HTTP endpoints → **0.5.1**.
- External / LINE login → **0.5.2**.
- Full TOTP 2FA (only the `two_factor_enabled` flag + `TwoFactor` token purpose exist now).
- A management UI.
- Generic `TUser` extension (documented future option per §4 / decision #7).
- MySQL (deferred, per decision #8).
