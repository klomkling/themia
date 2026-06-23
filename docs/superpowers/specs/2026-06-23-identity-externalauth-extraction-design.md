# Themia Identity external-auth + token extraction — Design

**Status:** Approved (brainstorming) — revised after a /scrutinize pass — ready for implementation plan
**Date:** 2026-06-23
**Coord:** #0011 (ezy-assets → Themia.Modules.Identity.AspNetCore), accepted
**Target version:** 0.6.6 (PATCH within the 0.6 Phase-2 milestone — maintainer's call; the breaking
namespace move is flagged in MIGRATION).

**Scrutiny revision (2026-06-23):** the first draft assumed a pure file-move ("no logic changes"). Tracing
the DI showed the external login flow's registration lives in `AddThemiaIdentityAspNetCore` behind an
`IUserService` guard, and the flow issues tokens via `IAccessTokenService`/`IRefreshTokenService`/
`IClaimsPrincipalFactory` (the bundled access-token impl `AccessTokenService` is persistence-free but its
`JwtOptions`/signing are shared with the staying JwtBearer scheme). So "make the full flow usable
bring-your-own-user-store" requires (a) re-homing the flow registration off the `IUserService` guard and
(b) shipping the access-token issuance as a persistence-free package. This design reflects that: **two new
packages.**

---

## Goal

Let an app with its own mature user store (ezy-assets) adopt Themia's hardened external-auth flow —
server-side code→token exchange, PKCE, token-bound nonce, id-token issuer/audience/signature/expiry
checks, JWKS RS256 + HS256, `ConfigurationManager` auto-refresh incl. the 0.6.5 rotation handling, and the
shared access-token issuance — **without** taking on Themia Identity's persistence (`IUserService`,
refresh-token storage, the `identity.*` schema). The consumer supplies its own `IExternalLoginService`
(and the persistence-backed seams it owns) and reuses everything else.

Same shape as coord #0001/#0002: an existing consumer adopts framework value without replacing its types.

## Scope

### In scope — two new net10 packages + a re-wire of `Identity.AspNetCore`

1. **`Themia.Modules.Identity.Tokens.AspNetCore`** (new, persistence-free): the JWT access-token issuance
   stack — `AccessTokenService` (default `IAccessTokenService`), `IJwtSigningCredentialsProvider` +
   `SymmetricSigningCredentialsProvider`, `JwtOptions`, `JwtClaimNames`, and the shared `AuthTokenIssuer`
   helper. Depends only on `Themia.Modules.Identity.Abstractions` + the `Microsoft.IdentityModel.*` family.
2. **`Themia.Modules.Identity.ExternalAuth.AspNetCore`** (new): the external-auth protocol + flow —
   `OidcExternalAuthProvider`, `OidcProviderConfig`, `ExternalAuthProviderRegistry`,
   `ExternalAuthenticationFlow`, `ExternalAuthenticationHooksBase`, the `AddThemiaExternalAuth` builder,
   `IdentityExternalAuthEndpoints`, `ExternalAuthOptions`. Depends on `Themia.Modules.Identity.Abstractions`
   + the neutral `Themia.AspNetCore` (typed exceptions) + `Themia.Modules.Identity.Tokens.AspNetCore`.
3. **`Themia.Modules.Identity.AspNetCore`** (changed): drops the moved code, `ProjectReference`s both new
   packages, keeps the local/password flow + the JwtBearer validation scheme + `IdentityAuthEndpoints`, and
   re-wires its DI to delegate token + external-auth registration to the new packages.
4. New test projects for the moved tests + a BYO (no-persistence) DI test. CHANGELOG + MIGRATION (0.6.6)
   documenting the two packages and the `(breaking)` namespace move.

### Out of scope (YAGNI)
- No change to the external-auth/validation behavior (0.6.5 already hardened it). The only logic changes are
  DI wiring: re-homing registrations and replacing the bundled `IUserService` guard with an external-only
  one. The protocol/flow/token-issuance code is moved verbatim.
- No persistence-free default for `IRefreshTokenService` or `IClaimsPrincipalFactory` — they stay in
  `Themia.Modules.Identity`; a BYO consumer supplies its own (see DI contract). Shipping no-op/default
  versions is deliberately not done (a silent no-op refresh service would be a footgun).
- No Serenity/other adapters; PowerACC is not a design driver.

### Scope-guard check
External-auth protocol, provider abstraction, the login orchestration over a BYO user-store seam, and the
JWT access-token issuance are cross-cutting auth infrastructure ✅. The user store, refresh-token
persistence, claims-principal construction over the user store, and the `identity.*` schema stay in
`Themia.Modules.Identity` — app/identity domain, out of both new packages.

### Naming note (for review)
`Themia.Modules.Identity.Tokens.AspNetCore` carries **no hard ASP.NET Core dependency** (it is JWT issuance
over `Microsoft.IdentityModel.*`). It could be named `Themia.Modules.Identity.Tokens`. Kept the
`.AspNetCore` suffix to match the family/selection; rename at review if preferred.

---

## Architecture

```
Themia.Modules.Identity.Abstractions            net10  (unchanged: contracts, no EF/Dapper)
        ▲                       ▲
        │                       │
Themia.Modules.Identity.Tokens.AspNetCore       net10  (NEW, persistence-free)
  Tokens/ AccessTokenService (IAccessTokenService), JwtClaimNames
  Signing/ IJwtSigningCredentialsProvider, SymmetricSigningCredentialsProvider
  Options/ JwtOptions
  Authentication/ AuthTokenIssuer  (internal; InternalsVisibleTo ExternalAuth + Identity.AspNetCore)
  DependencyInjection/ AddThemiaIdentityTokens(configureJwt)
        ▲                       ▲
        │                       │
Themia.Modules.Identity.ExternalAuth.AspNetCore net10  (NEW)            Themia.Modules.Identity (persistence)
  External/ OidcExternalAuthProvider, OidcProviderConfig,                 RefreshTokenService (IRefreshTokenService)
            ExternalAuthProviderRegistry,                                 ClaimsPrincipalFactory (IClaimsPrincipalFactory)
            ExternalAuthenticationFlow, ExternalAuthenticationHooksBase   bundled IExternalLoginService / IUserService
  DependencyInjection/ AddThemiaExternalAuth + builder
  Endpoints/ MapIdentityExternalAuthEndpoints
  Options/ ExternalAuthOptions
  → deps: Abstractions, Themia.AspNetCore (neutral), Tokens.AspNetCore
        ▲
        │
Themia.Modules.Identity.AspNetCore              net10  (CHANGED)
  Authentication/ AuthenticationFlow, AuthenticationHooksBase (local/password — STAYS)
  Endpoints/ IdentityAuthEndpoints (STAYS)
  the JwtBearer validation scheme (STAYS; reads JwtOptions/signing from Tokens.AspNetCore)
  DependencyInjection/ AddThemiaIdentityAspNetCore (re-wired; delegates token+external registration)
  → ProjectReference: + Tokens.AspNetCore + ExternalAuth.AspNetCore
```

**Namespaces (Q1 — clean):** moved types take new namespaces matching their package
(`Themia.Modules.Identity.Tokens.AspNetCore.*`, `Themia.Modules.Identity.ExternalAuth.AspNetCore.*`).
Dependency direction is acyclic; neither new package references `Themia.Modules.Identity` (persistence).

---

## Components — what moves where

**To `Themia.Modules.Identity.Tokens.AspNetCore`** (JWT access-token issuance, persistence-free):
`AccessTokenService` (default `IAccessTokenService`), `IJwtSigningCredentialsProvider` +
`SymmetricSigningCredentialsProvider`, `JwtOptions`, `JwtClaimNames`, `AuthTokenIssuer`.
- `AuthTokenIssuer` stays `internal static` (the shared access+refresh issuer used by both the external and
  local flows) and the package declares
  `[assembly: InternalsVisibleTo("Themia.Modules.Identity.ExternalAuth.AspNetCore")]` and
  `[assembly: InternalsVisibleTo("Themia.Modules.Identity.AspNetCore")]` so both flows call the one shared
  issuer. Not new public API.

**To `Themia.Modules.Identity.ExternalAuth.AspNetCore`** (external-auth protocol + flow):
`OidcExternalAuthProvider`, `OidcProviderConfig`, `ExternalAuthProviderRegistry` (impl of
`IExternalAuthProviderRegistry`), `ExternalAuthenticationFlow` (impl of `IExternalAuthenticationFlow`),
`ExternalAuthenticationHooksBase`, `ExternalAuthServiceCollectionExtensions.AddThemiaExternalAuth` +
`ExternalAuthBuilder` (`.AddGoogle/.AddLine/.AddOidc/.AddProvider`), `IdentityExternalAuthEndpoints`
(`MapIdentityExternalAuthEndpoints`, depends only on `IExternalAuthenticationFlow`), `ExternalAuthOptions`
(+ the `GoogleOptions`/`LineOptions` config types in the builder).
- All reference only Abstractions, the neutral `Themia.AspNetCore` (for `Themia.AspNetCore.Exceptions`), and
  Tokens.AspNetCore (for `AuthTokenIssuer` + the default `IAccessTokenService`).

**Stays in `Themia.Modules.Identity.AspNetCore`:** the local/password `AuthenticationFlow` +
`AuthenticationHooksBase`, `IdentityAuthEndpoints`, the JwtBearer validation scheme (`AddThemiaJwtBearer`),
and the `AddThemiaIdentityAspNetCore` orchestrator (re-wired). It references the moved `JwtOptions`/signing
from Tokens.AspNetCore.

**Stays in `Themia.Modules.Identity`:** `RefreshTokenService` (persistence-backed `IRefreshTokenService`),
`ClaimsPrincipalFactory` (`IClaimsPrincipalFactory`), and the bundled Identity `IExternalLoginService` over
`identity.external_logins`.

---

## DI contract & the bring-your-own-user-store path

Three composable registration entry points:

- **Tokens.AspNetCore — `AddThemiaIdentityTokens(Action<JwtOptions> configure)`**: validates + registers
  `JwtOptions`, `IJwtSigningCredentialsProvider` → `SymmetricSigningCredentialsProvider`,
  `IAccessTokenService` → `AccessTokenService`, and `TimeProvider` (all `TryAdd`). (`AuthTokenIssuer` is a
  static helper — no registration.)
- **ExternalAuth.AspNetCore — `AddThemiaExternalAuth()` + `.AddGoogle/.AddLine/.AddOidc/.AddProvider`**:
  registers the provider registry, the named HttpClients + providers, `TimeProvider`, **and** the external
  flow + hooks (`IExternalAuthenticationFlow` → `ExternalAuthenticationFlow`, `IExternalAuthenticationHooks`
  → `ExternalAuthenticationHooksBase`, both `TryAddScoped` — *re-homed here from the old Identity extension*).
  Adds an **external-only fail-fast** that requires `IAccessTokenService`, `IRefreshTokenService`,
  `IClaimsPrincipalFactory`, and `IExternalLoginService` to be registered — **not** `IUserService` (the
  external flow never uses it). The check is at app-build time, not per-request.
- **Identity.AspNetCore — `AddThemiaIdentityAspNetCore(Action<JwtOptions> configure)`** (re-wired): calls
  `AddThemiaIdentityTokens(configure)`, registers the local/password flow + hooks + the JwtBearer scheme,
  and keeps its own guard requiring the persistence seams for the **local** path (`IUserService`,
  `IRefreshTokenService`, `IExternalLoginService`, `IClaimsPrincipalFactory`). The external flow/hooks come
  from `AddThemiaExternalAuth()` (bundled consumers already call it to register providers), so the bundled
  end state is unchanged.

**Bundled consumer (today's behavior, preserved):**
```csharp
services.AddThemiaIdentityServices(...);          // IUserService, IRefreshTokenService, IExternalLoginService, IClaimsPrincipalFactory
services.AddThemiaIdentityAspNetCore(jwt => ...); // -> AddThemiaIdentityTokens + local flow + JwtBearer scheme
services.AddThemiaExternalAuth().AddGoogle(...).AddLine(...);
```

**Bring-your-own consumer (ezy):**
```csharp
services.AddThemiaIdentityTokens(jwt => ...);     // Themia access-token issuance (or register your own IAccessTokenService)
services.AddThemiaExternalAuth().AddLine(...).AddGoogle(...);
services.AddScoped<IExternalLoginService, EzyExternalLoginService>();   // maps ExternalIdentity -> ezy user/tenant/link
services.AddScoped<IRefreshTokenService, EzyRefreshTokenService>();     // ezy's session/refresh model
services.AddScoped<IClaimsPrincipalFactory, EzyClaimsPrincipalFactory>(); // ezy User -> ClaimsPrincipal
// No AddThemiaIdentity, no IUserService, no identity.* schema. Map endpoints OR call the flow from your own controller.
```
The MIGRATION note documents both paths and is explicit that the BYO full-flow requires the three seams
above (only `IAccessTokenService` is defaulted). A consumer that wants none of Themia's token issuance can
instead use `OidcExternalAuthProvider` / `IExternalAuthProviderRegistry` directly for a validated
`ExternalIdentity` and skip the flow.

---

## Error handling & conventions
- Moved code keeps its behavior, including the 0.6.5 rotation handling (guarded one-shot JWKS refetch on
  `SecurityTokenSignatureKeyNotFoundException`, token-based cancellation, clean `id_token_invalid` on
  transient fetch failure). Logic changes are confined to DI wiring (re-homed registrations + the
  external-only guard).
- `System.Text.Json` only; `ILogger<T>` only; THEMIA101 (no log-and-rethrow) preserved. PublicAPI analyzer
  tracks each new package's surface; clean under `TreatWarningsAsErrors`.

## Backward compatibility (breaking, flagged)
- The external-auth types **and** the JWT-issuance stack (`AccessTokenService`, `JwtOptions`, signing,
  `JwtClaimNames`, `AuthTokenIssuer`) leave `Themia.Modules.Identity.AspNetCore`'s public API and change
  namespace. `Identity.AspNetCore` re-references both new packages so bundled consumers keep the types in
  their dependency closure, but `using` directives change (notably `JwtOptions`’s namespace, used wherever
  JWT is configured).
- In-repo impact is contained (the only consumers are `Identity.AspNetCore` itself + its tests, which move);
  ezy adopts the new packages directly. No shipped external consumer breaks.
- Handle `RS0017` (removed shipped API) on `Identity.AspNetCore` by moving those PublicAPI lines into the
  new packages' `PublicAPI.*.txt`. Flag `(breaking)` in CHANGELOG; give the full old→new namespace map in
  MIGRATION 0.6.6.

## Testing strategy
- **Move** the external-auth tests (the ~24 OIDC provider/flow tests) into
  `tests/Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests`, and the access-token/signing tests into
  `tests/Themia.Modules.Identity.Tokens.AspNetCore.Tests` (same fixtures).
- **Add** a BYO DI/integration test: register `AddThemiaIdentityTokens(...)` + `AddThemiaExternalAuth()` +
  stub `IExternalLoginService` + `IRefreshTokenService` + `IClaimsPrincipalFactory` — **no `AddThemiaIdentity`,
  no `IUserService`, no persistence** — and assert (a) the external-only guard passes, (b)
  `IExternalAuthenticationFlow` resolves, and (c) an external login runs end-to-end and issues an access
  token via the defaulted `IAccessTokenService`.
- **Add** a negative test: `AddThemiaExternalAuth()` without `IExternalLoginService` (or token seams)
  fails fast with the external-only guard message (and does **not** mention `IUserService`).
- `Themia.Modules.Identity.AspNetCore.Tests` keeps the local/password + JwtBearer tests, green via the
  re-references. Clean `--no-incremental` solution build to surface `RS0016`/`RS0017`.

## Versioning, changelog, coord
- Bump `Directory.Build.props` `<Version>` `0.6.5 → 0.6.6`.
- CHANGELOG **Added**: `Themia.Modules.Identity.Tokens.AspNetCore` (JWT access-token issuance) and
  `Themia.Modules.Identity.ExternalAuth.AspNetCore` (external-auth protocol + flow + builder + endpoints),
  both persistence-free, for bring-your-own-user-store adoption.
  CHANGELOG **Changed (breaking)**: external-auth + JWT-issuance types moved out of
  `Themia.Modules.Identity.AspNetCore` (namespace change) — see MIGRATION.
- MIGRATION `## 0.6.6`: old→new namespace map (external-auth + token stack); the BYO path and the three
  seams it requires; note that bundled consumers update `using` directives only.
- Coord #0011 → released on publish; ezy implements `IExternalLoginService` (+ its refresh/principal seams)
  and marks it consumed.

## Future improvements (not v1)
- A persistence-free default `IClaimsPrincipalFactory` over the Abstractions `User` (if a BYO consumer can
  reuse it) — only if a driver appears.
- Optional Serenity/other adapters (YAGNI).
