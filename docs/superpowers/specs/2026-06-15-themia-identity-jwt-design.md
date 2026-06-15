# Themia.Modules.Identity — JWT slice (0.5.1) Design

**Status:** Accepted (2026-06-15)
**Scope:** The second slice of the full Identity provider — access-token issuance from the
current-user principal, revocable refresh tokens, JWT validation middleware, and login/refresh/logout
HTTP endpoints. Builds on
[`2026-06-14-themia-identity-core-design.md`](2026-06-14-themia-identity-core-design.md) (0.5.0 core)
and companion to [`themia-architecture-overview.md`](../../themia-architecture-overview.md).

---

## 1. Milestone context

Identity ships as dependency-ordered slices, each a shippable PATCH in the 0.5.x line with its own
spec → plan → implement cycle (see the 0.5.0 spec, §1):

| Slice | Deliverable | Depends on |
|---|---|---|
| **0.5.0 — Identity core** | Tenant-aware user/role/claim store, password hashing, account-lifecycle tokens + lockout, current-user principal, ASP.NET Core authorization integration | framework only |
| **0.5.1 — JWT** *(this spec)* | Access-token issuance from the principal, revocable refresh tokens, validation middleware, login endpoints | Identity core |
| **0.5.2 — External login** | Pluggable external-login abstraction + LINE (OAuth) implementation, external-identity linking | core + JWT |

This document specifies **0.5.1 only**.

## 2. Resolved decisions (do not relitigate)

Settled during brainstorming on 2026-06-15:

1. **New HTTP-facing package `Themia.Modules.Identity.AspNetCore`** (net10) holds token issuance, the
   JwtBearer validation scheme, and the login endpoints. The service core
   (`Themia.Modules.Identity`) and `Identity.Abstractions` stay HTTP-free and peer-neutral — the
   abstractions split done in 0.5.0 exists for exactly this.
2. **Revocable refresh tokens in a dedicated `identity.refresh_tokens` table** with rotation,
   token-family reuse-detection, and per-token + per-user revocation. Not folded into `user_tokens`
   (that store is single-use/expiry only and has no family/revoke-all concept).
3. **HS256 symmetric signing**, key from validated options, behind a minimal
   `IJwtSigningCredentialsProvider` seam so RS256/ES256 + JWKS can be added later without touching
   callers. (YAGNI: asymmetric/JWKS only when external services must validate without the secret.)
4. **Login/refresh/logout delivered as a `MapIdentityAuthEndpoints()` minimal-API extension** — opt-in,
   host owns the route prefix. Working endpoints, per the roadmap. The endpoints are **thin**: they
   parse the DTO and delegate to a DI-replaceable orchestrator (`IAuthenticationFlow`) that owns the
   security-critical sequence.
5. **Adopter extensibility via before/after hooks** (`IAuthenticationHooks`) invoked by the default
   orchestrator at fixed points, so adopters inject code (audit, last-login stamp, post-auth gating)
   without replacing the flow and without losing the timing/anti-enumeration/rotation logic. Three
   escalating levels of control overall: customize claims via `IClaimsPrincipalFactory` → implement
   hooks → replace `IAuthenticationFlow` or skip the mapper and hand-roll on the public services.
6. **Principal plumbing is reused, not rebuilt.** 0.5.0 already ships `ClaimsPrincipalFactory`,
   `ICurrentUser`/`CurrentUser`, and `IdentityCurrentUserAccessor`, all sourced from
   `HttpContext.User`. 0.5.1 only adds the JwtBearer scheme that *populates* `HttpContext.User`.
7. **Anti-enumeration + timing mitigation land here** (the 0.5.0 follow-ups assigned to the login
   boundary): all credential failures — including any hook `Deny()` — return a single generic `401`;
   the not-found/inactive paths perform throwaway argon2id work to equalize latency.

## 3. Package layout & boundaries

```
src/modules/
  Themia.Modules.Identity.AspNetCore   (net10)  — IAuthenticationFlow orchestrator,
       │                                           IAccessTokenService + JWT minting,
       │                                           IRefreshTokenService + refresh_tokens store,
       │                                           default (no-op) AuthenticationHooksBase,
       │                                           AddThemiaJwtBearer() validation scheme,
       │                                           MapIdentityAuthEndpoints() login/refresh/logout,
       │                                           JwtOptions (validated on start).
       └── depends on: Identity.Abstractions, Identity (services at runtime),
                       Themia.Framework.AspNetCore, Themia.AspNetCore (ProblemDetails),
                       Microsoft.AspNetCore.Authentication.JwtBearer (new managed package, 10.0.x).
```

New abstractions (`IAuthenticationFlow`, `IAuthenticationHooks`, `IAccessTokenService`,
`IRefreshTokenService`, `IJwtSigningCredentialsProvider`, hook-context / result / option types) live in
`Themia.Modules.Identity.Abstractions` so 0.5.2 (external login) depends only on contracts.
Implementations live in the new `.AspNetCore` package. All are registered with `TryAdd` so any can be
replaced via DI.

The refresh-token store depends only on `IRepository<T>`/`IReadRepository<T>`/`ISpecification<T>`/
`IUnitOfWork` + `IDataFilterScope` — it runs on whichever data peer (EF Core or Dapper) the adopter
chose, exactly like the 0.5.0 services. EF model-contribution + Dapper mapping for the new entity are
provided the same way the core does it.

## 4. Schema — `identity.refresh_tokens`

One FluentMigrator migration (`IfDatabase("postgres","sqlserver")`), snake_case, in the existing
`identity` schema, with the framework's audit/soft-delete/concurrency conventions.

| Column | Notes |
|---|---|
| `id` | Guid PK |
| `user_id` | FK → `users.id` |
| `tenant_id` | `varchar(100)`, NULL ⇒ platform user (framework global-record convention) |
| `token_hash` | SHA-256 of the opaque refresh token; raw token returned once, never stored |
| `family_id` | Guid — groups a rotation chain for reuse-detection |
| `expires_at` | absolute expiry |
| `consumed_at` | set when the token is rotated (single redemption) |
| `revoked_at` | set on logout / revoke-all / reuse-detection |
| `replaced_by_id` | successor row in the rotation chain (nullable) |
| + audit/concurrency | per framework conventions |

**Indexes:** `(user_id)` for revoke-all and `(token_hash)` for redemption lookup.

**Semantics:**
- **Rotation** — a successful `refresh` consumes the presented row (`consumed_at`) and issues a
  successor, linked via `replaced_by_id`, sharing the same `family_id`.
- **Reuse-detection** — presenting an already-consumed or revoked token is treated as theft: revoke
  the entire `family_id` and reject. (Defends against a stolen refresh token replayed after the
  legitimate client already rotated.)
- **Revocation** — `logout` revokes the presented token's family; `logout?all=true` revokes all
  non-expired tokens for the user.

Refresh tokens are tenant-scoped rows; platform-user tokens carry `tenant_id IS NULL`. Lookups honor
the framework's tenant filter (platform path bypasses it), consistent with the 0.5.0 store.

## 5. Token services & abstractions

- **`IAccessTokenService.Issue(ClaimsPrincipal) → AccessToken`** — builds a signed JWT from the claims
  produced by `IClaimsPrincipalFactory` (subject, tenant, user name, roles, effective claims), stamping
  issuer/audience/expiry from `JwtOptions`. The factory remains the single source of "what's in the
  principal" across cookie and JWT.
- **`IRefreshTokenService`** —
  - `IssueAsync(userId, tenantId, familyId?) → (rawToken, RefreshToken)` — new high-entropy opaque
    token; persists only the hash.
  - `RotateAsync(rawToken) → RefreshRotationResult` — validates hash + expiry + not-consumed +
    not-revoked; on success consumes + issues successor; on reuse revokes the family.
  - `RevokeAsync(rawToken, allForUser: bool)` — logout / logout-everywhere.
- **`IJwtSigningCredentialsProvider`** — returns the `SigningCredentials` + token-validation key
  material; default `SymmetricSigningCredentialsProvider` (HS256 from `JwtOptions.SigningKey`).
- **Result types** (typed, not exceptions for expected outcomes): `LoginResult`
  (`Success(tokens)` | `InvalidCredentials` | `LockedOut` | `Denied`), `RefreshRotationResult`
  (`Success(tokens)` | `Invalid` | `ReuseDetected` | `Denied`). Genuine faults still throw.
- **`IAuthenticationFlow`** — the orchestrator the thin endpoints delegate to. Owns the
  security-critical sequence (verify → timing mitigation → principal build → issue) and invokes the
  hooks (§6.1). `LoginAsync` / `RefreshAsync` / `LogoutAsync` return the result types above. Registered
  via `TryAdd`; adopters needing total control replace it (or skip the mapper and call the building-block
  services directly).
- **`IAuthenticationHooks`** — before/after extension points the default flow calls; default
  `AuthenticationHooksBase` is all no-ops, registered via `TryAdd` so adopters override only what they
  need. Hook contexts carry the relevant request/user/principal and a `Deny(reason?)` that
  short-circuits the flow to a generic `401` (the internal reason is available to hooks for audit but
  never surfaced to the client). See §6.1 for the methods and call order.

## 6. Endpoints — `MapIdentityAuthEndpoints()`

Opt-in minimal-API extension; host chooses the prefix (e.g. `app.MapGroup("/auth")`). Each endpoint is
**thin** — it binds the DTO and delegates to `IAuthenticationFlow`; no auth logic lives in the endpoint.

- **`POST login`** `{ userName, password }` → `IAuthenticationFlow.LoginAsync`, which verifies via
  `IUserService.VerifyPasswordAsync` (drives lockout; tries ambient tenant then platform per
  `IdentityModuleOptions.AllowPlatformLogin`), builds the principal via `ClaimsPrincipalFactory`, and
  issues an access + refresh pair. Any failure (incl. hook `Deny`): generic `401` (see §7).
- **`POST refresh`** `{ refreshToken }` → `IAuthenticationFlow.RefreshAsync` (→
  `IRefreshTokenService.RotateAsync`). Success: new pair. `Invalid`/`ReuseDetected`/`Denied`: generic
  `401`.
- **`POST logout`** `{ refreshToken }`, optional `?all=true` → `IAuthenticationFlow.LogoutAsync` →
  revoke. Always `204` (idempotent; no existence signal).

Responses are a small DTO (`{ accessToken, expiresIn, refreshToken }`); errors flow through the
existing `Themia.AspNetCore` ProblemDetails middleware.

### 6.1 Hook lifecycle (`IAuthenticationHooks`)

The default `IAuthenticationFlow` invokes hooks at fixed points. All are `Task`-returning and receive a
context with a `Deny(reason?)`; a deny collapses to the generic `401`/failure result.

| Operation | Before | After (success) | After (failure) |
|---|---|---|---|
| **Login** | `OnBeforeLoginAsync(BeforeLoginContext)` — early gate (rate-limit, IP allowlist), runs before credential verification | `OnLoginSucceededAsync(LoginSucceededContext)` — runs after verification, **before** tokens are issued; for audit, last-login stamp, and post-auth gating (e.g. "subscription active?") via `Deny()` | `OnLoginFailedAsync(LoginFailedContext)` — receives the real internal reason (`NotFound`/`Wrong`/`Inactive`/`LockedOut`) for security audit; client still gets the uniform `401` |
| **Refresh** | `OnBeforeRefreshAsync(BeforeRefreshContext)` | `OnRefreshSucceededAsync(RefreshSucceededContext)` — before the new pair is returned | — |
| **Logout** | — | `OnLogoutAsync(LogoutContext)` — after revocation | — |

Order within login: `OnBeforeLogin` → verify (timing-equalized) → on success `OnLoginSucceeded` →
issue tokens; on failure `OnLoginFailed`. A `Deny()` from `OnBeforeLogin`/`OnLoginSucceeded` also
triggers `OnLoginFailed` (with a `Denied` reason) so audit sees every rejection. Hooks are the
recommended customization path; replacing `IAuthenticationFlow` is the escape hatch for wholesale
changes.

## 7. Security

- **Timing side-channel.** On `NotFound`/`Inactive`, the login path runs a throwaway argon2id hash so
  response latency does not distinguish existing from non-existing accounts (closes the 0.5.0
  `VerifyPasswordAsync` follow-up at the auth boundary).
- **Anti-enumeration.** `NotFound`, wrong password, `Inactive`, `LockedOut`, and any hook `Deny()` all
  return the **same** generic `401` with a uniform message. Lockout is still enforced server-side; it is
  not surfaced as a distinct status, so the response leaks no account state. Hooks see the real reason
  for audit but cannot change the client-facing response shape.
- **Token handling.** Access tokens are short-lived and stateless (no per-request DB hit); refresh
  tokens are opaque, stored as hashes, and revocable. **Documented tradeoff:** a still-valid access
  token remains usable until its (short) expiry even after logout — acceptable for the default TTLs;
  callers needing instant kill-switch use short access TTLs and rely on refresh revocation.
- **Constant-time** comparison for refresh-token hash matching, mirroring the 0.5.0 token store.

## 8. Options & configuration

`JwtOptions`, bound and **validated on start** (`ValidateOnStart`), missing/short key fails fast:

| Option | Default | Notes |
|---|---|---|
| `SigningKey` | *(required)* | symmetric secret; minimum length enforced |
| `Issuer` / `Audience` | *(required)* | standard JWT validation |
| `AccessTokenLifetime` | 15 min | short-lived |
| `RefreshTokenLifetime` | 14 days | revocable window |
| `ClockSkew` | 30 s | validation tolerance |

## 9. Testing

- **Unit:** access-token claim shape (subject/tenant/roles/effective claims; tenant omitted for
  platform); refresh rotation consumes + chains; reuse-detection revokes the family; expiry rejected;
  generic-401 uniformity across NotFound/wrong/inactive/locked; throwaway-hash path executes on
  not-found; `JwtOptions` validation fails fast.
- **Hooks:** `OnBeforeLogin`/`OnLoginSucceeded` `Deny()` → generic `401` (and `OnLoginFailed` still
  fires with a `Denied` reason); `OnLoginSucceeded` runs before tokens are issued; `OnLoginFailed`
  receives the real internal reason on a uniform-401 response; replacing `IAuthenticationFlow` overrides
  the flow; default `AuthenticationHooksBase` no-ops don't alter the happy path.
- **Integration (PG + SQL Server, `WebApplicationFactory` + Testcontainers, both data peers):**
  login → refresh → logout happy path; refresh replay after rotation → 401 + family revoked;
  `logout?all=true` revokes all sessions; platform-user login when `AllowPlatformLogin = true`;
  JwtBearer middleware populates `ICurrentUser` on an authenticated request.

## 10. Out of scope (deferred)

- **2FA challenge in the login flow** — only the `two_factor_enabled` flag + `TwoFactor` token purpose
  exist; the login flow ignores the flag in 0.5.1 (documented gap). Full TOTP/challenge is a later slice.
- **External / LINE login** → 0.5.2.
- **Asymmetric signing (RS256/ES256) + JWKS endpoint** → added behind the existing
  `IJwtSigningCredentialsProvider` seam when multi-service validation is needed.
- **`user_tokens` / refresh-token pruning job** — periodic cleanup of consumed/expired rows is a known
  follow-up (low impact at current scale).
