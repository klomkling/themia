# Themia.Modules.Identity — External / OAuth login slice (0.5.2) Design

**Status:** Draft (2026-06-16)
**Scope:** The third slice of the full Identity provider — a **pluggable external-identity-provider
(OAuth2 / OIDC) login** system with two reference providers (Google + LINE), external-identity
linking, and auto-provisioning, all issuing the **same** Themia access + refresh tokens as the 0.5.1
JWT slice. Builds on
[`2026-06-14-themia-identity-core-design.md`](2026-06-14-themia-identity-core-design.md) (0.5.0 core)
and [`2026-06-15-themia-identity-jwt-design.md`](2026-06-15-themia-identity-jwt-design.md) (0.5.1 JWT),
companion to [`themia-architecture-overview.md`](../../themia-architecture-overview.md).

---

## 1. Milestone context

Identity ships as dependency-ordered slices, each a shippable PATCH in the 0.5.x line with its own
spec → plan → implement cycle:

| Slice | Deliverable | Depends on |
|---|---|---|
| **0.5.0 — Identity core** | Tenant-aware user/role/claim store, password hashing, account lifecycle, current-user principal | framework only |
| **0.5.1 — JWT** | Access-token issuance, revocable rotating refresh tokens, validation middleware, login endpoints | Identity core |
| **0.5.2 — External login** *(this spec)* | Pluggable external-provider abstraction + Google & LINE, external-identity linking + auto-provisioning, headless code-exchange endpoint | core + JWT |

This document specifies **0.5.2 only**. Facebook, Microsoft, and Telegram providers are explicitly
deferred (§11) — but the abstraction is designed so each is a small additive registration, not a
structural change.

## 2. Resolved decisions (do not relitigate)

Settled during brainstorming on 2026-06-16:

1. **Provider-agnostic, not LINE-specific.** A pluggable `IExternalAuthProvider` abstraction keyed by
   provider name (the `{provider}` route segment). 0.5.2 ships **two reference providers** chosen to
   prove both shapes — **Google** (standard OIDC, RS256 + JWKS) and **LINE** (OIDC-ish; its own
   token endpoint and id-token verification). Facebook/Microsoft/Telegram are later additive providers.
2. **Headless code-exchange flow** (no auth cookies, no redirect middleware) — matches the existing
   stateless-JWT posture. The client (SPA/mobile) obtains the provider authorization `code` and POSTs
   it to Themia; Themia performs the **server-side** code→token exchange, validates the identity, links
   or provisions the user, and returns **Themia's own** access + refresh tokens. We do **not** use
   ASP.NET Core's remote-auth handlers (`AddGoogle`/`AddFacebook`), which assume a cookie/redirect leg.
3. **First-login policy = auto-link by verified email, else create.** If the provider returns a
   **verified** email matching an existing user in the ambient tenant, link to that user; otherwise
   provision a new password-less user and link. An **unverified** provider email **never** auto-links
   (account-takeover guard) — it always creates a fresh user. This is the fixed 0.5.2 policy (not an
   option; can be made configurable later if an adopter needs `CreateOnly`/`LinkOnly`).
4. **Reuse, don't rebuild.** External login terminates in the **same** `IAccessTokenService` +
   `IRefreshTokenService` + `IClaimsPrincipalFactory` from 0.5.1. The new code adds provider exchange,
   the link table + provisioning service, and one thin endpoint — the token half is unchanged.
5. **No package explosion.** The generic OIDC machinery + Google/LINE config live in the existing
   `Themia.Modules.Identity.AspNetCore` package; the link entity + provisioning service live in the
   existing `Themia.Modules.Identity` core; contracts in `Identity.Abstractions`. A custom provider is
   just another `IExternalAuthProvider` registered by name (optionally its own package, e.g. a future
   `…External.Telegram` for the non-OAuth hash-verify shape).
6. **We never persist provider tokens.** The provider access/refresh/id tokens are used **once**, in
   the exchange, to establish identity, then discarded. Themia stores only the link `(provider,
   external_id) → user` and issues its own tokens. There is no provider-token refresh concern.

## 3. Package layout & boundaries

```
src/modules/
  Themia.Modules.Identity.Abstractions (net10) — contracts only:
       IExternalAuthProvider, ExternalAuthRequest, ExternalIdentity, ExternalAuthResult,
       ExternalLoginLink (entity), IExternalLoginService + ExternalLoginResult,
       IExternalAuthenticationFlow + result types, IExternalAuthenticationHooks,
       ExternalAuthOptions / provider option types.

  Themia.Modules.Identity            (net10) — peer-neutral core:
       ExternalLoginLink EF config + Dapper mapping, the FluentMigrator migration,
       ExternalLoginService (link lookup + auto-link-by-verified-email + provisioning),
       IUserService.CreateExternalUserAsync (password-less user creation).
       Runs on either data peer (EF Core or Dapper) via IRepository/ISpecification/IUnitOfWork.

  Themia.Modules.Identity.AspNetCore (net10) — HTTP + provider I/O:
       OidcExternalAuthProvider (generic OIDC code-exchange + id_token/JWKS validation),
       Google & LINE preconfigured providers, IExternalAuthProviderRegistry,
       ExternalAuthenticationFlow orchestrator, ExternalAuthenticationHooksBase (no-ops),
       AddThemiaExternalAuth() builder (.AddGoogle/.AddLine/.AddProvider),
       MapIdentityExternalAuthEndpoints() (POST /auth/external/{provider}).
       New deps: Microsoft.Extensions.Http (IHttpClientFactory),
                 Microsoft.IdentityModel.Protocols.OpenIdConnect (JWKS discovery).
                 (Microsoft.IdentityModel.JsonWebTokens already present from 0.5.1.)
```

All new services register via `TryAdd` so any can be replaced via DI, exactly like 0.5.0/0.5.1.

## 4. Schema — `identity.external_logins`

One FluentMigrator migration (`IfDatabase("postgresql", "sqlserver")`, snake_case, in the existing
`identity` schema; next version number after the latest applied Identity migration at implement time).

Unlike `refresh_tokens`/`user_tokens` (parent-keyed children with no `tenant_id`), `external_logins`
is a **tenant-scoped entity** (`ITenantEntity`, nullable `tenant_id`) — it is an **independent entry
point** looked up by `(provider, external_id)` **before** any user is known, so it cannot lean on
"resolve the user first" for isolation. Carrying `tenant_id` lets the framework's tenant query filter
isolate the lookup by construction, and lets the **same external account map to a different user per
tenant** (the same Google login can be an employee in tenant A and a customer in tenant B). It mirrors
how `users` itself handles tenant + platform rows.

| Column | Notes |
|---|---|
| `id` | Guid PK (`Guid.CreateVersion7()`) |
| `tenant_id` | nullable; the owning tenant (NULL = platform). Denormalized from the user so the pre-user lookup is tenant-filtered. |
| `user_id` | FK → `identity.users` |
| `provider` | registered provider key, lowercased (`"google"`, `"line"`) |
| `external_id` | the provider subject (`sub`) — stable per provider |
| `created_at` | link time (service-set via `TimeProvider`) |

**Indexes (mirroring the users tenant/platform filtered-unique pattern):**
- filtered unique `(tenant_id, provider, external_id)` where `tenant_id IS NOT NULL`
- filtered unique `(provider, external_id)` where `tenant_id IS NULL` (platform)
- `(user_id)` for listing/cascade.

Plain entity (`Entity<Guid>, ITenantEntity`) — **no soft-delete**; unlink is a hard delete (keeps the
unique index free of tombstones; unlink is rare). No audit base (consistent with the token children).

## 5. Provider abstraction

```
IExternalAuthProvider
  string Name { get; }                               // "google" / "line"; matches {provider}, case-insensitive
  Task<ExternalAuthResult> ExchangeAsync(ExternalAuthRequest req, CancellationToken ct)
```

- **`ExternalAuthRequest`** `{ Code, RedirectUri, CodeVerifier? }` — PKCE `code_verifier` optional
  (recommended for public clients); `RedirectUri` must equal the one used to obtain the code.
- **`ExternalIdentity`** `{ Provider, Subject, Email?, EmailVerified, DisplayName? }` — the normalized
  result every provider maps its payload into.
- **`ExternalAuthResult`** = `Success(ExternalIdentity)` | `Failed(reason)` (provider rejected the
  code, id-token invalid, etc.). Typed result, not exceptions, for expected outcomes; genuine faults
  (network/5xx) throw.

**`OidcExternalAuthProvider`** — one generic implementation parameterized by `OidcProviderConfig`
(token endpoint, JWKS/issuer, optional userinfo endpoint, client id/secret, scopes, id-token
signing/issuer/audience expectations). It performs: `code` → `POST token endpoint` (server-side, with
client secret + optional PKCE) → validate `id_token` (issuer, audience = client id, expiry, signature
via cached JWKS using the existing `JsonWebTokenHandler`) → map claims to `ExternalIdentity`. Google is
this provider verbatim (standard OIDC discovery). **LINE** is the same provider with LINE's token
endpoint + JWKS and the small per-provider quirks (LINE calls the client id a "channel id"; its
id-token audience/issuer differ) — proving the shape generalizes. Each provider also gets a **named
`HttpClient`** via `IHttpClientFactory`.

**`IExternalAuthProviderRegistry`** — resolves an `IExternalAuthProvider` by name (case-insensitive).
Unknown provider → a typed miss the endpoint renders as `404` ProblemDetails.

**Registration builder** — `services.AddThemiaExternalAuth().AddGoogle(o => …).AddLine(o => …)` and a
generic `.AddProvider(name, factory)` / `.AddProvider<TProvider>()` for custom providers (incl. a
future non-OIDC Telegram). Each provider's options are validated at startup (`ValidateOnStart`):
client id/secret non-blank, endpoints absolute https.

## 6. Linking & provisioning — `IExternalLoginService` (core, peer-neutral)

```
IExternalLoginService
  Task<ExternalLoginResult> ResolveOrProvisionAsync(ExternalIdentity id, CancellationToken ct)
ExternalLoginResult { User user, bool WasCreated, bool WasLinked }
```

Sequence (all within the ambient tenant scope; uses `IdentityScope`/repositories, never raw SQL):

1. **Existing link** — find `external_logins` by `(provider, external_id)` (framework tenant filter
   applies). If found, load the owning user via `IdentityScope.ResolveUserAsync(link.UserId)` →
   return `(user, created:false, linked:false)`.
2. **Auto-link by verified email** — else if `id.Email` is present **and** `id.EmailVerified` **and**
   `IUserService.FindByEmailAsync(email)` returns a user in scope → create a link row to that user →
   return `(user, created:false, linked:true)`.
3. **Provision** — else create a new **password-less** active user via
   `IUserService.CreateExternalUserAsync(...)` (username derived deterministically, §6.1;
   `EmailConfirmed = id.EmailVerified`) + a link row → return `(user, created:true, linked:true)`.

A new user and its link for a NULL-tenant platform context follow the existing platform-write path
(`IdentityScope.SaveScopedAsync`, bypassing tenant write-validation for genuine platform rows). 0.5.2
requires an **ambient tenant context** for the request; a missing/ambiguous tenant is a configuration
error surfaced by the host's tenant middleware, not handled here.

### 6.1 Username & password-less users

- **`IUserService.CreateExternalUserAsync(userName, email, emailVerified, displayName, ct)`** creates
  an **active** user with **no password hash** (external-only). `SetPasswordAsync` may later add a
  local password; until then password login simply fails verification (no special casing needed —
  there is no hash to match).
- **Username derivation** (deterministic, collision-handled): prefer the email local-part; on
  collision, suffix a short disambiguator; fall back to `"{provider}_{sub-prefix}"` when no email.
  Normalization reuses `IdentityScope.Normalize`.

## 7. Flow & endpoint

**`IExternalAuthenticationFlow.AuthenticateAsync(provider, ExternalAuthRequest, ct) →
ExternalLoginFlowResult`** (orchestrator in `.AspNetCore`, parallels `IAuthenticationFlow`):

1. Resolve provider from the registry (unknown → `ProviderNotFound`).
2. `OnBeforeExternalLoginAsync` hook (early gate; `Deny()` → uniform `401`).
3. `provider.ExchangeAsync(req)` → `ExternalIdentity` (Failed → `ProviderRejected`, uniform `401`).
4. `IExternalLoginService.ResolveOrProvisionAsync(identity)` → user.
5. `OnExternalLoginSucceededAsync` hook — after the user is resolved, **before** tokens are issued;
   for audit, last-login stamp, post-auth gating via `Deny()`.
6. Build the principal (`IClaimsPrincipalFactory`) and issue **access + refresh** tokens
   (`IAccessTokenService` + `IRefreshTokenService`) — identical to the password-login terminus.
7. On any failure, `OnExternalLoginFailedAsync` fires with the real internal reason for audit; the
   client still gets the uniform response.

Result type: `Success(tokens, wasCreated, wasLinked)` | `ProviderNotFound` | `ProviderRejected` |
`Denied`.

**Endpoint — `MapIdentityExternalAuthEndpoints()`** (opt-in minimal-API extension; host owns the
prefix, e.g. `app.MapGroup("/auth")`):

- **`POST external/{provider}`** `{ code, redirectUri, codeVerifier? }` →
  `IExternalAuthenticationFlow.AuthenticateAsync` → `200 AuthResponse { accessToken, expiresIn,
  refreshToken }` (the same DTO as login/refresh). `ProviderNotFound` → `404`; `ProviderRejected` /
  `Denied` → uniform `401`; malformed body → `400` (ValidationException). The issued refresh token
  rotates through the existing `POST /auth/refresh`; logout/revocation is unchanged.

Hooks live in a **separate `IExternalAuthenticationHooks`** (+ `ExternalAuthenticationHooksBase`
no-ops, `TryAdd`) rather than bloating `IAuthenticationHooks` — adopters implement it only if they use
external login.

## 8. Security

- **Server-side exchange only.** The client secret/channel secret never leaves the server; it is read
  from configuration/secret manager and validated at startup (never hardcoded, never logged).
- **id-token validation.** Issuer, audience (= client/channel id), expiry, and signature (cached JWKS)
  are all verified via the existing `JsonWebTokenHandler`; `nonce` is verified when the client supplies
  one. `MapInboundClaims=false` (consistent with 0.5.1) — claims are read by their wire names.
- **PKCE** `code_verifier` is forwarded when present (recommended for SPA/mobile public clients).
- **Verified-email gate.** Auto-link happens **only** on `EmailVerified == true`, preventing an
  attacker who controls an unverified provider email from hijacking a victim's local account; unverified
  emails always create a fresh, separate user.
- **No enumeration.** Both link and create return tokens (success), so external login does not reveal
  whether an account pre-existed. Provider/exchange failures return a uniform `401`.
- **CSRF / state.** In the headless model the client owns the `state` round-trip (CSRF defense); the
  server validates `nonce` inside the id-token. Documented in the endpoint XML docs + README.
- **Refresh tokens** issued here are the same opaque, hashed, rotating, revocable tokens as 0.5.1 — the
  refresh/logout/reuse-detection semantics carry over unchanged.

## 9. Options & configuration

`AddThemiaExternalAuth()` with per-provider options, each `Validate()`d at startup:

| Provider option | Required | Notes |
|---|---|---|
| Google: `ClientId` / `ClientSecret` | yes | standard OIDC; default scopes `openid email profile` |
| LINE: `ChannelId` / `ChannelSecret` | yes | LINE Login v2.1; default scopes `openid email profile` |
| (common) `Scopes`, endpoint overrides | no | sensible provider defaults; overridable for testing |

Provider options are bound to typed classes (no scattered `IConfiguration` reads) and fail fast on
missing/blank credentials, mirroring `JwtOptions.Validate()` / `ValidateOnStart`.

## 10. Testing

- **Unit:** `OidcExternalAuthProvider.ExchangeAsync` against a stub `HttpMessageHandler` returning a
  token + signed id-token (success → normalized `ExternalIdentity`; bad signature/issuer/audience/expiry
  → `Failed`); `ExternalLoginService` policy matrix — existing link → same user; verified-email match →
  link (no new user); no match → create; **unverified email → create, never link**; username derivation
  + collision; unknown provider → `ProviderNotFound`; flow issues access+refresh and fires hooks in
  order; `Deny()` on before/succeeded → uniform `401`.
- **Integration (PG + SQL Server, `WebApplicationFactory` + Testcontainers, both data peers):** wire a
  **fake in-test `IExternalAuthProvider`** (no real Google/LINE calls) and exercise
  `POST /auth/external/{fake}` → `200` tokens; second login with the same `external_id` → **same**
  user (no duplicate, link reused); verified-email auto-link onto a pre-seeded password user; the issued
  refresh token rotates via `POST /auth/refresh`; the issued access token authorizes `GET /me`. Reuse
  the existing `AuthFlowConformanceTests` harness shape (`ExternalAuthConformanceTests`).

## 11. Out of scope (deferred)

- **Facebook, Microsoft, Telegram providers.** Facebook/Microsoft are additive `OidcExternalAuthProvider`
  configs; **Telegram** is a distinct shape (Login-Widget HMAC hash verification, not OAuth code
  exchange) — a separate `IExternalAuthProvider` implementation, likely its own package, in a later slice.
- **Explicit "link a provider to my already-authenticated account"** (and **unlink** / list-links
  management endpoints). 0.5.2 does login-time auto-link/create only; authenticated link management is a
  follow-up.
- **Configurable link policy** (`CreateOnly` / `LinkOnly`). 0.5.2 ships the fixed
  auto-link-by-verified-email behavior; an option can be added behind it later without breaking callers.
- **Persisting / refreshing provider tokens** — out by design (§2.6); Themia keeps only the link.
- **Provider account de-provisioning / token-revocation webhooks** — later, per provider.
