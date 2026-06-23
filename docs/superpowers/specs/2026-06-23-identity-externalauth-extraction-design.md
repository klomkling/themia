# Themia.Modules.Identity.ExternalAuth.AspNetCore — Design

**Status:** Approved (brainstorming) — ready for implementation plan
**Date:** 2026-06-23
**Coord:** #0011 (ezy-assets → Themia.Modules.Identity.AspNetCore), accepted
**Target version:** 0.6.6 (PATCH within the 0.6 Phase-2 milestone — maintainer's call, overriding the
"new package ⇒ MINOR" policy; the breaking namespace move is flagged in MIGRATION).

---

## Goal

Extract Themia's hardened external-auth protocol **and** login flow into a standalone package that
depends only on `Themia.Modules.Identity.Abstractions` — **no EF/Dapper, no `identity.*` schema** — so a
consumer with its own mature user store (ezy-assets) can adopt the mechanics (server-side
authorization-code→token exchange, PKCE, token-bound nonce validation, id-token issuer/audience/
signature/expiry checks, JWKS RS256 + HS256, `ConfigurationManager` auto-refresh with the 8.19
rotation handling) by supplying its own `IExternalLoginService` — without migrating onto Themia
Identity's persistence.

This is the same shape as coord #0001/#0002: let an existing consumer adopt framework value without
replacing its own types. **The blocker is purely packaging** — the protocol code already targets clean
abstractions; it just lives in `Themia.Modules.Identity.AspNetCore`, which `ProjectReference`s the full
`Themia.Modules.Identity` persistence.

## Scope

### In scope
- New package `Themia.Modules.Identity.ExternalAuth.AspNetCore` (net10) containing the full external-auth
  machinery, depending only on `Themia.Modules.Identity.Abstractions` + the neutral `Themia.AspNetCore`.
- `Themia.Modules.Identity.AspNetCore` re-references the new package so its bundled behavior is unchanged.
- A new test project for the moved tests + a BYO (no-`AddThemiaIdentity`) DI test.
- CHANGELOG + MIGRATION (0.6.6) documenting the new package and the `(breaking)` namespace move.

### Out of scope (YAGNI)
- No logic changes to the external-auth code — this is a move + repackage, not a rewrite.
- No new providers, no changes to the OIDC validation behavior (0.6.5 already hardened it).
- No extraction of the local/password auth or JWT-issuance impls (they stay; only the shared
  `AuthTokenIssuer` helper moves, see below).
- ezy's own `IExternalLoginService` implementation is ezy's work, not Themia's.

### Scope-guard check
External-auth protocol + provider abstraction + the login orchestration over a BYO user-store seam are
cross-cutting auth infrastructure (any app doing OIDC external login needs them) ✅. The concrete user
store, refresh-token persistence, and `identity.*` schema stay in `Themia.Modules.Identity` — app/identity
domain, out of the new package.

---

## Architecture

```
src/modules/Themia.Modules.Identity.ExternalAuth.AspNetCore/   net10.0   (NEW)
  External/ OidcExternalAuthProvider, OidcProviderConfig, ExternalAuthProviderRegistry,
            ExternalAuthenticationFlow, ExternalAuthenticationHooksBase
  Authentication/ AuthTokenIssuer            (moved; internal, shared via InternalsVisibleTo)
  DependencyInjection/ ExternalAuthBuilder   (AddThemiaExternalAuth + AddGoogle/.AddLine/.AddOidc/.AddProvider)
  Endpoints/ IdentityExternalAuthEndpoints   (MapIdentityExternalAuthEndpoints)
  Options/ ExternalAuthOptions
  PublicAPI.*.txt
  → ProjectReference: Themia.Modules.Identity.Abstractions, Themia.AspNetCore (neutral)
  → FrameworkReference: Microsoft.AspNetCore.App
  → PackageReference: the Microsoft.IdentityModel.* family (already pinned at 8.19.1)

src/modules/Themia.Modules.Identity.AspNetCore/               net10.0   (CHANGED)
  Authentication/ AuthenticationFlow, AuthenticationHooksBase   (local/password — STAYS)
  Tokens/ AccessTokenService, JwtClaimNames                     (STAYS)
  Signing/ *                                                    (STAYS)
  Endpoints/ IdentityAuthEndpoints                             (STAYS)
  Options/ JwtOptions                                          (STAYS)
  DependencyInjection/ IdentityAspNetCoreServiceCollectionExtensions  (STAYS; using-updated)
  → ProjectReference: + Themia.Modules.Identity.ExternalAuth.AspNetCore (re-expose bundled external auth)
```

**Namespaces** (Q1 — clean): moved types live under `Themia.Modules.Identity.ExternalAuth.AspNetCore.*`
(`.External`, `.Authentication`, `.DependencyInjection`, `.Endpoints`, `.Options`).

**Dependency direction:** new package → Abstractions (+ neutral AspNetCore); `Identity.AspNetCore` →
new package + `Themia.Modules.Identity` (persistence). No cycles. The new package never references the
persistence module.

---

## Components & what moves

**Verified before design:** every moving file imports only `Microsoft.*`, `Themia.AspNetCore.Exceptions`
(neutral), and `Themia.Modules.Identity.Abstractions(.Authentication/.Entities)` — plus the intra-set
`.External`/`.Options`/`.Authentication` namespaces that move with it. No moving file uses a
`Themia.Modules.Identity` (persistence) type.

**Moves to the new package:**

| Type | Role | Dependencies (all abstractions/neutral/framework) |
|---|---|---|
| `OidcExternalAuthProvider` | code→token exchange + id-token validation → `ExternalIdentity` | `IHttpClientFactory`, `ConfigurationManager`, IdentityModel |
| `OidcProviderConfig` | per-provider config | — |
| `ExternalAuthProviderRegistry` / `IExternalAuthProviderRegistry` impl | name→provider lookup | `IExternalAuthProvider` (abstraction) |
| `ExternalAuthenticationFlow` | orchestration: provider → `IExternalLoginService.ResolveOrProvisionAsync` → hooks → token issuance | `IExternalAuthProviderRegistry`, `IExternalLoginService`, `IAccessTokenService`, `IRefreshTokenService`, `IClaimsPrincipalFactory`, `IExternalAuthenticationHooks` (all in Abstractions) + `AuthTokenIssuer` |
| `ExternalAuthenticationHooksBase` | default `IExternalAuthenticationHooks` | abstraction |
| `ExternalAuthBuilder` | `AddThemiaExternalAuth()` + `.AddGoogle/.AddLine/.AddOidc/.AddProvider` | DI; registers `IExternalAuthProviderRegistry`, providers, `TimeProvider` |
| `IdentityExternalAuthEndpoints` | `MapIdentityExternalAuthEndpoints` | only `IExternalAuthenticationFlow` |
| `ExternalAuthOptions` | options | — |
| `AuthTokenIssuer` | **shared** access+refresh issuer helper | `IClaimsPrincipalFactory`, `IAccessTokenService`, `IRefreshTokenService`, `User` (Abstractions), `TimeProvider` |

**`AuthTokenIssuer` — the one wrinkle.** It is `internal static` and shared by the moving
`ExternalAuthenticationFlow` and the staying local `AuthenticationFlow`. It moves to the new package and
the new package declares `[assembly: InternalsVisibleTo("Themia.Modules.Identity.AspNetCore")]` so the
local flow keeps calling the single shared issuer. It stays `internal` (not new public API) — both flows
mint structurally identical tokens from one place.

**Stays put:**
- `Themia.Modules.Identity.AspNetCore`: `AuthenticationFlow`/`AuthenticationHooksBase` (local/password),
  `AccessTokenService` (concrete `IAccessTokenService`), signing, `IdentityAuthEndpoints`, `JwtOptions`.
- `Themia.Modules.Identity` (persistence): `RefreshTokenService` (persistence-backed `IRefreshTokenService`)
  and the bundled Identity `IExternalLoginService` over `identity.external_logins`.

---

## DI & the bring-your-own-user-store contract

- `AddThemiaExternalAuth()` is self-contained: it never calls `AddThemiaIdentity`, registers
  `IExternalAuthProviderRegistry` + the configured providers + `TimeProvider`, and the flow resolves
  `IExternalLoginService` and the token-service abstractions from DI.
- **BYO consumer (ezy):**
  ```csharp
  services.AddThemiaExternalAuth()
          .AddLine(o => { o.ChannelId = …; o.ChannelSecret = …; })
          .AddGoogle(o => { … });
  services.AddScoped<IExternalLoginService, EzyExternalLoginService>(); // maps ExternalIdentity → ezy user/tenant/link
  // To reuse the full flow's token issuance, also register IAccessTokenService / IRefreshTokenService /
  // IClaimsPrincipalFactory impls; OR call OidcExternalAuthProvider / IExternalAuthProviderRegistry
  // directly to get a validated ExternalIdentity and issue tokens in your own AuthController.
  ```
- **Bundled consumer:** `AddThemiaIdentity…` + `Identity.AspNetCore` register the persistence-backed
  `IExternalLoginService` + `IRefreshTokenService` + the `IAccessTokenService`/`IClaimsPrincipalFactory`
  exactly as today; the re-referenced `AddThemiaExternalAuth` wires the rest. No behavior change.

The MIGRATION note documents both paths (BYO and bundled).

---

## Error handling & conventions

- No logic changes: the moved code keeps its current behavior, including the 0.6.5 rotation handling
  (guarded one-shot JWKS refetch on `SecurityTokenSignatureKeyNotFoundException`, token-based
  cancellation, clean `id_token_invalid` on transient fetch failure).
- `System.Text.Json` only; `ILogger<T>` only; THEMIA101 (no log-and-rethrow) — all preserved.
- PublicAPI analyzer tracks the new package's surface; clean under `TreatWarningsAsErrors`.

---

## Backward compatibility (breaking, flagged)

- The external-auth types are **removed from `Themia.Modules.Identity.AspNetCore`'s public API** (now
  declared in the new assembly) and their **namespace changes** to
  `Themia.Modules.Identity.ExternalAuth.AspNetCore.*`.
- In-repo impact is contained: `Identity.AspNetCore` re-references the new package (so the types are still
  in its dependency closure for bundled consumers) and its tests move. No shipped external consumer is
  affected — ezy has not adopted yet and adopts the new package directly.
- Handle the `RS0017` "removed shipped API" on `Identity.AspNetCore` by moving those PublicAPI lines into
  the new package's `PublicAPI.*.txt`. Flag `(breaking)` in CHANGELOG and give the old→new namespace map
  in MIGRATION 0.6.6.

---

## Testing strategy

- **Move** the existing external-auth tests (the ~24 OIDC provider/flow tests in
  `OidcExternalAuthProviderTests` + any `ExternalAuthenticationFlow` tests) into a new
  `tests/Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests` project (same fixtures/helpers).
- **Add** a DI test proving the BYO path: `AddThemiaExternalAuth()` + a stub `IExternalLoginService`
  (and stub token services) resolves `IExternalAuthenticationFlow` and runs an external login **without
  `AddThemiaIdentity`** — i.e. no persistence registered.
- `Themia.Modules.Identity.AspNetCore.Tests` keeps the local/password + JWT tests and stays green via the
  re-reference.
- Build the solution clean (`--no-incremental`) to surface any `RS0016`/`RS0017`.

---

## Versioning, changelog, coord

- Bump `Directory.Build.props` `<Version>` `0.6.5 → 0.6.6`.
- CHANGELOG **Added**: `Themia.Modules.Identity.ExternalAuth.AspNetCore` — the standalone external-auth
  package (provider + registry + `AddThemiaExternalAuth` builder + flow + hooks + endpoints), depending
  only on `Themia.Modules.Identity.Abstractions`, for bring-your-own-user-store adoption.
  CHANGELOG **Changed (breaking)**: external-auth types moved out of `Themia.Modules.Identity.AspNetCore`
  (namespace change) — see MIGRATION.
- MIGRATION `## 0.6.6`: old→new namespace map; BYO adoption path; note that bundled `Identity.AspNetCore`
  consumers update `using` directives only (the re-reference keeps the API available).
- Coord #0011 → released on publish; ezy implements `IExternalLoginService` and marks it consumed.

---

## Future improvements (not v1)
- Optional deferred Serenity/other adapters — only if a driver appears (PowerACC is not a design driver).
- A reusable default `IAccessTokenService` shipped in the new package (symmetric JWT) so BYO consumers
  needn't implement it — only if a consumer actually asks (YAGNI; ezy issues its own).
