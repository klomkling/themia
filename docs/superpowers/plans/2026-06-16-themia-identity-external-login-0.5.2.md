# Themia Identity External/OAuth login slice (0.5.2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a pluggable external-identity-provider (OAuth2/OIDC) login system — Google + LINE reference providers, external-identity linking + auto-provisioning, and a headless `POST /auth/external/{provider}` endpoint that issues the **same** Themia access + refresh tokens as 0.5.1 — shipping Themia Identity 0.5.2.

**Architecture:** Contracts land in `Themia.Modules.Identity.Abstractions` (IdentityModel-free). The link table + provisioning service (`ExternalLoginService`) live in the **core** `Themia.Modules.Identity` beside `RefreshTokenService` (peer-neutral, reuses `IdentityScope`). The provider machinery (generic OIDC exchange + Google/LINE config + registry), the `ExternalAuthenticationFlow` orchestrator, and the `MapIdentityExternalAuthEndpoints()` minimal-API extension live in the existing `Themia.Modules.Identity.AspNetCore`. **No new projects** — new tests go in the existing `Identity.IntegrationTests`, `Identity.AspNetCore.Tests`, and `Identity.AspNetCore.IntegrationTests`.

**Tech Stack:** .NET 10, `Microsoft.IdentityModel.JsonWebTokens` (already present) + `Microsoft.IdentityModel.Protocols.OpenIdConnect` (JWKS) + `Microsoft.Extensions.Http` (IHttpClientFactory), FluentMigrator, EF Core + Dapper peers, xUnit + Testcontainers (PostgreSQL + SQL Server).

**Design spec:** [`2026-06-16-themia-identity-external-login-design.md`](../specs/2026-06-16-themia-identity-external-login-design.md).

---

## Decisions locked for this plan (deviations / clarifications)

1. **`ExternalLoginLink` is a tenant-scoped entity** (`Entity<Guid>, ITenantEntity`, nullable `tenant_id`), **not** a tenant-less parent-keyed child like `RefreshToken`. It is an independent entry point looked up by `(provider, external_id)` before any user is known, so it relies on the framework tenant filter for isolation and supports per-tenant external identities. No soft-delete (hard delete on unlink). Confirmed in spec §4.
2. **Provisioning service + link table in core**, beside `RefreshTokenService`; it uses `internal IdentityScope`. Only provider I/O + HTTP land in `.AspNetCore`.
3. **`IUserService.CreateExternalUserAsync`** adds a password-less user-creation path (no hash). Username derivation is deterministic + collision-handled (§ Task 3).
4. **Generic OIDC provider with a pluggable signing-key source.** Google = JWKS/RS256 (`iss=https://accounts.google.com`, `aud=client_id`); LINE = HS256 with the channel secret as key (`iss=https://access.line.me`, `aud=channel_id`). This single `OidcExternalAuthProvider` + `OidcProviderConfig` covers both shapes.
5. **Separate `IExternalAuthenticationHooks`** (not folded into `IAuthenticationHooks`) so adopters opt in only when using external login.
6. **New migration**, never an edit of a released one. Use a version **strictly greater** than the latest applied Identity migration; at time of writing the latest is `202606160002`, so use `202606160003` (or a later-dated value if newer migrations exist when you implement).
7. **We never persist provider tokens** — the exchange is used once to establish identity, then discarded.

## File map

**Modify — `src/modules/Themia.Modules.Identity.Abstractions/`**
- Create `Entities/ExternalLoginLink.cs`
- Create `Authentication/ExternalAuthContracts.cs` (`ExternalAuthRequest`, `ExternalIdentity`, `ExternalAuthResult`, `IExternalAuthProvider`)
- Create `Authentication/ExternalLoginContracts.cs` (`ExternalLoginResult`, `IExternalLoginService`)
- Create `Authentication/ExternalAuthFlowContracts.cs` (`ExternalLoginOutcome`, `ExternalLoginResultType`, `IExternalAuthenticationFlow`)
- Create `Authentication/ExternalAuthHooks.cs` (hook contexts + `IExternalAuthenticationHooks`)
- Modify `IUserService.cs` (add `CreateExternalUserAsync`)
- Modify `PublicAPI.Unshipped.txt`

**Modify — `src/modules/Themia.Modules.Identity/`**
- Create `Migrations/ExternalLoginsMigration.cs`
- Create `Services/ExternalLoginService.cs`
- Modify `Services/UserService.cs` (implement `CreateExternalUserAsync`)
- Modify `Specifications/IdentitySpecs.cs` (add external-login specs)
- Modify `EntityConfiguration/IdentityModelConfiguration.cs` (add `ExternalLoginLinkConfiguration` + register)
- Modify `Mapping/IdentityDapperMappings.cs` (register `ExternalLoginLink`)
- Modify `DependencyInjection/IdentityServiceCollectionExtensions.cs` (register `IExternalLoginService`)
- Modify `PublicAPI.Unshipped.txt`

**Modify — `src/modules/Themia.Modules.Identity.AspNetCore/`**
- Create `External/OidcProviderConfig.cs`, `External/OidcExternalAuthProvider.cs`
- Create `External/ExternalAuthProviderRegistry.cs` (`IExternalAuthProviderRegistry` + impl)
- Create `External/ExternalAuthenticationHooksBase.cs`, `External/ExternalAuthenticationFlow.cs`
- Create `Options/ExternalAuthOptions.cs` (+ `GoogleOptions`, `LineOptions`)
- Create `DependencyInjection/ExternalAuthBuilder.cs` (`AddThemiaExternalAuth`, `.AddGoogle`, `.AddLine`, `.AddProvider`)
- Create `Endpoints/IdentityExternalAuthEndpoints.cs` (`MapIdentityExternalAuthEndpoints`)
- Modify `PublicAPI.Unshipped.txt`

**Modify — tests**
- `tests/Themia.Modules.Identity.IntegrationTests/` — external-login store conformance + fixture truncation
- `tests/Themia.Modules.Identity.AspNetCore.Tests/` — OIDC provider unit tests (stub `HttpMessageHandler`), flow unit tests
- `tests/Themia.Modules.Identity.AspNetCore.IntegrationTests/` — `ExternalAuthConformanceTests` with a fake provider

**Modify — root**
- `Directory.Packages.props` (add `Microsoft.IdentityModel.Protocols.OpenIdConnect`, `Microsoft.Extensions.Http` if not already pinned)
- `Directory.Build.props` (`<Version>` → `0.5.2`)
- `README.md`, `CHANGELOG.md`, `docs/themia-architecture-overview.md`

---

## Task 1: Abstractions — entity + external-auth/login/flow/hook contracts

**Files:**
- Create `Entities/ExternalLoginLink.cs`
- Create `Authentication/ExternalAuthContracts.cs`, `ExternalLoginContracts.cs`, `ExternalAuthFlowContracts.cs`, `ExternalAuthHooks.cs`
- Modify `IUserService.cs`, `PublicAPI.Unshipped.txt`

- [ ] **Step 1: Entity** — `Entities/ExternalLoginLink.cs`

```csharp
using Themia.MultiTenancy; // ITenantEntity, TenantId (confirm namespace against User.cs)

namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>Links an external provider identity (provider + subject) to a Themia <see cref="User"/>.
/// A tenant-scoped entity: it is looked up by (provider, external_id) before any user is known, so the
/// framework tenant filter isolates it and the same external account can map to a different user per
/// tenant. No password, no soft-delete (unlink is a hard delete).</summary>
public sealed class ExternalLoginLink : ITenantEntity
{
    /// <summary>The link identifier (UUIDv7).</summary>
    public Guid Id { get; private set; }

    /// <summary>The owning tenant (null = platform). Denormalized from the user for the pre-user lookup.</summary>
    public string? TenantId { get; set; }   // match User.TenantId's exact CLR type/shape

    /// <summary>The linked user.</summary>
    public Guid UserId { get; set; }

    /// <summary>The registered provider key, lowercased (e.g. "google", "line").</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The provider subject (stable per provider).</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Link creation time (service-set via TimeProvider).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Assigns the client-generated identifier (UUIDv7).</summary>
    public void SetId(Guid id) => Id = id;
}
```

> **Before writing:** open `Entities/User.cs` and mirror its **exact** `ITenantEntity` shape and `TenantId` CLR type (the map shows `TenantId?` value object on `User`; the link must match how the framework filter reads it). If `User` uses `SoftDeletableEntity<Guid>` for the tenant plumbing, replicate only the `ITenantEntity` surface here — the link is **not** soft-deletable.

- [ ] **Step 2: Provider contracts** — `Authentication/ExternalAuthContracts.cs`

```csharp
namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>A headless external-login request: the authorization code the client obtained, plus the
/// redirect URI it used and an optional PKCE verifier.</summary>
public readonly record struct ExternalAuthRequest(string Code, string RedirectUri, string? CodeVerifier = null);

/// <summary>A provider identity normalized to a common shape.</summary>
/// <param name="Provider">The provider key (lowercased).</param>
/// <param name="Subject">The stable provider subject (sub).</param>
/// <param name="Email">The email, if the provider returned one.</param>
/// <param name="EmailVerified">Whether the provider asserts the email is verified.</param>
/// <param name="DisplayName">An optional display name.</param>
public readonly record struct ExternalIdentity(
    string Provider, string Subject, string? Email, bool EmailVerified, string? DisplayName);

/// <summary>The outcome of a provider exchange. Expected failures are typed (not exceptions);
/// genuine faults (network/5xx) throw.</summary>
public readonly record struct ExternalAuthResult
{
    private ExternalAuthResult(bool ok, ExternalIdentity? identity, string? failureReason)
    {
        Succeeded = ok; Identity = identity; FailureReason = failureReason;
    }

    /// <summary>Whether the exchange + validation succeeded.</summary>
    public bool Succeeded { get; }

    /// <summary>The normalized identity on success; otherwise null.</summary>
    public ExternalIdentity? Identity { get; }

    /// <summary>An internal failure reason (audit only) on failure; otherwise null.</summary>
    public string? FailureReason { get; }

    /// <summary>Creates a success result.</summary>
    public static ExternalAuthResult Success(ExternalIdentity identity) => new(true, identity, null);

    /// <summary>Creates a failure result.</summary>
    public static ExternalAuthResult Failed(string reason) => new(false, null, reason);
}

/// <summary>A pluggable external-identity provider. Implementations perform the server-side code
/// exchange and validate the resulting identity. Registered by <see cref="Name"/>.</summary>
public interface IExternalAuthProvider
{
    /// <summary>The provider key (lowercased; matches the {provider} route segment, case-insensitive).</summary>
    string Name { get; }

    /// <summary>Exchanges the authorization code for the provider identity.</summary>
    Task<ExternalAuthResult> ExchangeAsync(ExternalAuthRequest request, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: Linking-service contracts** — `Authentication/ExternalLoginContracts.cs`

```csharp
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>The result of resolving or provisioning a user from an external identity.</summary>
/// <param name="User">The resolved or created user.</param>
/// <param name="WasCreated">True if a new user was provisioned.</param>
/// <param name="WasLinked">True if a new link row was created (create or auto-link).</param>
public readonly record struct ExternalLoginResult(User User, bool WasCreated, bool WasLinked);

/// <summary>Resolves an existing external link, auto-links by verified email, or provisions a new
/// password-less user — all within the ambient tenant scope.</summary>
public interface IExternalLoginService
{
    /// <summary>Resolves the user for an external identity, creating/linking per the 0.5.2 policy
    /// (existing link → user; verified-email match → link; else create).</summary>
    Task<ExternalLoginResult> ResolveOrProvisionAsync(ExternalIdentity identity, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: Flow contracts** — `Authentication/ExternalAuthFlowContracts.cs`

```csharp
namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>The outcome of an external-login attempt. Non-success collapses to a uniform 401 at the
/// boundary (except ProviderNotFound → 404).</summary>
public enum ExternalLoginOutcome
{
    /// <summary>Authenticated; tokens issued.</summary>
    Success,
    /// <summary>No provider registered under the requested name.</summary>
    ProviderNotFound,
    /// <summary>The provider rejected the code or the identity failed validation.</summary>
    ProviderRejected,
    /// <summary>A hook denied the attempt.</summary>
    Denied,
}

/// <summary>The result of <see cref="IExternalAuthenticationFlow.AuthenticateAsync"/>.</summary>
public readonly record struct ExternalLoginResultType
{
    private ExternalLoginResultType(ExternalLoginOutcome outcome, AuthTokens? tokens, bool wasCreated, bool wasLinked)
    {
        Outcome = outcome; Tokens = tokens; WasCreated = wasCreated; WasLinked = wasLinked;
    }

    /// <summary>The outcome.</summary>
    public ExternalLoginOutcome Outcome { get; }
    /// <summary>The issued tokens on success; otherwise null. (Reuses 0.5.1 <see cref="AuthTokens"/>.)</summary>
    public AuthTokens? Tokens { get; }
    /// <summary>Whether a new user was provisioned.</summary>
    public bool WasCreated { get; }
    /// <summary>Whether a new link was created.</summary>
    public bool WasLinked { get; }
    /// <summary>Whether the attempt succeeded.</summary>
    public bool Succeeded => Outcome == ExternalLoginOutcome.Success;

    /// <summary>Creates a success result.</summary>
    public static ExternalLoginResultType Success(AuthTokens tokens, bool created, bool linked) =>
        new(ExternalLoginOutcome.Success, tokens, created, linked);
    /// <summary>Creates a provider-not-found result.</summary>
    public static ExternalLoginResultType ProviderNotFound() => new(ExternalLoginOutcome.ProviderNotFound, null, false, false);
    /// <summary>Creates a provider-rejected result.</summary>
    public static ExternalLoginResultType ProviderRejected() => new(ExternalLoginOutcome.ProviderRejected, null, false, false);
    /// <summary>Creates a denied result.</summary>
    public static ExternalLoginResultType Denied() => new(ExternalLoginOutcome.Denied, null, false, false);
}

/// <summary>Orchestrates the external-login sequence (provider exchange → link/provision → issue
/// tokens). Default impl lives in Themia.Modules.Identity.AspNetCore; replaceable via DI.</summary>
public interface IExternalAuthenticationFlow
{
    /// <summary>Authenticates via the named provider and issues an access + refresh pair.</summary>
    Task<ExternalLoginResultType> AuthenticateAsync(string provider, ExternalAuthRequest request, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 5: Hook contracts** — `Authentication/ExternalAuthHooks.cs`. Mirror the 0.5.1 `AuthenticationHookContext` (`Deny(reason?)` + `IsDenied`/`DenialReason`). Define:
  - `BeforeExternalLoginContext(string Provider) : AuthenticationHookContext` — early gate.
  - `ExternalLoginSucceededContext(User User, bool WasCreated, bool WasLinked) : AuthenticationHookContext` — after user resolved, before tokens; `Deny()` for post-auth gating.
  - `ExternalLoginFailedContext(string Provider, ExternalLoginOutcome Reason)` — audit only (no `Deny`).
  - `IExternalAuthenticationHooks` with `OnBeforeExternalLoginAsync`, `OnExternalLoginSucceededAsync`, `OnExternalLoginFailedAsync` (all `Task`-returning, `CancellationToken` last). Reuse the existing `AuthenticationHookContext` base from 0.5.1 (same namespace) — do not duplicate it.

- [ ] **Step 6: Extend `IUserService`** — add to `IUserService.cs`:

```csharp
    /// <summary>Creates an active, password-less user from an external identity. The username is
    /// caller-supplied (already derived + unique); <paramref name="emailVerified"/> sets EmailConfirmed.</summary>
    Task<UserCreationResult> CreateExternalUserAsync(
        string userName, string? email, bool emailVerified, string? displayName, CancellationToken cancellationToken = default);
```

> Open `IUserService.cs` + `UserCreationResult` first; match the existing `CreateAsync` return-type/shape exactly.

- [ ] **Step 7: Build + capture PublicAPI.** `dotnet build src/modules/Themia.Modules.Identity.Abstractions/Themia.Modules.Identity.Abstractions.csproj --no-incremental` → expect `RS0016` listing every new public member; paste the exact suggested lines (sorted) into `PublicAPI.Unshipped.txt`; rebuild until clean.

- [ ] **Step 8: Commit** — `feat(identity): add external-login entity + provider/flow/hook contracts`

---

## Task 2: Core — external_logins schema, EF/Dapper mapping, fixtures

**Files:** `Migrations/ExternalLoginsMigration.cs`, `EntityConfiguration/IdentityModelConfiguration.cs`, `Mapping/IdentityDapperMappings.cs`, both `Identity.IntegrationTests` fixtures.

- [ ] **Step 1: Migration** (new file; version `202606160003` or later — see Decision #6). Use `IfDatabase("postgresql", "sqlserver")` (note the **`postgresql`** id — repo switched off `"postgres"`), with the same unsupported-provider guard the other Identity migrations use (copy the exact `IfDatabase(p => !p.StartsWith…)` block from `RefreshTokensMigration.cs`).

```csharp
[Migration(202606160003, "Themia.Identity: create identity.external_logins")]
public sealed class ExternalLoginsMigration : Migration
{
    private const string SchemaName = "identity";

    public override void Up()
    {
        IfDatabase("postgresql", "sqlserver").Delegate(CreateExternalLogins);
        IfDatabase(p => !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase)
                     && !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Identity supports only PostgreSQL and SQL Server."));
    }

    private void CreateExternalLogins()
    {
        Create.Table("external_logins").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(/* match users.tenant_id width/type */).Nullable()
            .WithColumn("user_id").AsGuid().NotNullable()
            .WithColumn("provider").AsString(64).NotNullable()
            .WithColumn("external_id").AsString(256).NotNullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable();

        Create.Index("ix_external_logins_user").OnTable("external_logins").InSchema(SchemaName)
            .OnColumn("user_id").Ascending();

        Create.ForeignKey("fk_external_logins_user_id").FromTable("external_logins").InSchema(SchemaName).ForeignColumn("user_id")
            .ToTable("users").InSchema(SchemaName).PrimaryColumn("id");

        // Filtered-unique per tenant + platform — COPY the exact raw-SQL filtered-index idiom that
        // IdentitySchemaMigration uses for users' per-tenant + platform unique indexes (Postgres
        // partial index vs SQL Server filtered index differ; reuse that migration's helper/Execute.Sql).
        // tenant rows:   UNIQUE (tenant_id, provider, external_id) WHERE tenant_id IS NOT NULL
        // platform rows: UNIQUE (provider, external_id)            WHERE tenant_id IS NULL
    }

    public override void Down() => Delete.Table("external_logins").InSchema(SchemaName);
}
```

> **Critical:** open `IdentitySchemaMigration.cs` and replicate **exactly** how it emits per-tenant + platform filtered-unique indexes for `users` (likely `IfDatabase("postgresql").Execute.Sql(...)` + `IfDatabase("sqlserver").Execute.Sql(...)`). Match the `tenant_id` column type to `users.tenant_id`.

- [ ] **Step 2: EF config** — add `ExternalLoginLinkConfiguration : IEntityTypeConfiguration<ExternalLoginLink>` (copy `RefreshTokenConfiguration` style: `ToTable("external_logins", Schema)`, `HasKey`, snake_case `HasColumnName` for each column, `tenant_id` mapped like `users`), and register it in `ApplyThemiaIdentity`.

- [ ] **Step 3: Dapper mapping** — in `IdentityDapperMappings.Apply`: `registry.Register<ExternalLoginLink>(EntityMapping.ForConvention<ExternalLoginLink>("identity.external_logins", null));`

- [ ] **Step 4: Fixtures** — add `identity.external_logins` to the truncation/delete order in **both** `PostgresIdentityFixture.cs` and `SqlServerIdentityFixture.cs` (it's a child of `users`, so truncate it before `users`; for Postgres add to the `TRUNCATE … CASCADE` list, for SQL Server add `DELETE FROM identity.external_logins;` first).

- [ ] **Step 5: Build core** — `dotnet build src/modules/Themia.Modules.Identity/Themia.Modules.Identity.csproj` → PASS.

- [ ] **Step 6: Commit** — `feat(identity): add external_logins schema, EF/Dapper mappings, fixtures`

---

## Task 3: Core — CreateExternalUserAsync + external-login specs

**Files:** `Services/UserService.cs`, `Specifications/IdentitySpecs.cs`.

- [ ] **Step 1: Specs** — append to `IdentitySpecs.cs`:

```csharp
/// <summary>The link matching a provider + external id. The framework tenant filter applies (tenant
/// entity), so this returns the link only within the ambient tenant scope.</summary>
internal sealed class ExternalLoginByProviderKeySpec : Specification<ExternalLoginLink>
{
    public ExternalLoginByProviderKeySpec(string provider, string externalId) =>
        Where(l => l.Provider == provider && l.ExternalId == externalId);
}
```

- [ ] **Step 2: Implement `CreateExternalUserAsync`** in `UserService.cs`. Mirror `CreateAsync` but: no password hash (skip `IPasswordHasher`/`SetPasswordAsync`); set `IsActive = true`; set the email + `EmailConfirmed = emailVerified`; normalize via `IdentityScope.Normalize`; honor the platform/tenant write path (`IdentityScope.SaveScopedAsync`) exactly as `CreateAsync` does. Return the same `UserCreationResult`.

> Read `CreateAsync` fully first and reuse its normalization, uniqueness check, security-stamp init, and save path verbatim — only the password step is omitted.

- [ ] **Step 3: Build core** → PASS.

- [ ] **Step 4: Commit** — `feat(identity): add password-less CreateExternalUserAsync + external-login spec`

---

## Task 4: Core — ExternalLoginService + DI (TDD via conformance harness)

**Files:** `Services/ExternalLoginService.cs`, `DependencyInjection/IdentityServiceCollectionExtensions.cs`, `tests/Themia.Modules.Identity.IntegrationTests/IdentityStoreConformanceTests.cs`.

- [ ] **Step 1: Failing conformance tests.** Add an `ExternalLogins` accessor to the test `Scope` (`GetRequiredService<IExternalLoginService>()`), `using Themia.Modules.Identity.Abstractions.Authentication;`, then add tests:

```csharp
[Fact] // existing link → same user, no create/link
public async Task External_existing_link_returns_same_user() { /*
    create user; manually create a link (via service first-login); call ResolveOrProvisionAsync again
    with the same (provider, subject); assert same User.Id, WasCreated=false, WasLinked=false */ }

[Fact] // verified-email match → link to existing user
public async Task External_verified_email_links_existing_user() { /*
    seed a password user with email "x@acme.test"; ResolveOrProvisionAsync(identity with that email,
    EmailVerified:true, new subject) → same User.Id, WasCreated=false, WasLinked=true */ }

[Fact] // no match → create
public async Task External_no_match_creates_user() { /*
    ResolveOrProvisionAsync(new subject, email "new@acme.test", verified) → WasCreated=true, WasLinked=true */ }

[Fact] // unverified email → create, never link
public async Task External_unverified_email_never_links() { /*
    seed password user "v@acme.test"; ResolveOrProvisionAsync(same email, EmailVerified:false, new subject)
    → WasCreated=true (a DIFFERENT user id), WasLinked=true */ }

[Fact] // tenant isolation: a link created in tenant A is not seen in tenant B
public async Task External_link_is_tenant_isolated() { /* first-login in tenant a; in tenant b the same
    (provider, subject) does NOT resolve to a's user → it provisions a NEW user in b */ }
```

- [ ] **Step 2: Run → expect compile failure** (`IExternalLoginService` unregistered). Good.

- [ ] **Step 3: Implement `ExternalLoginService`.** Constructor injects `IRepository<User,Guid> users`, `IRepository<ExternalLoginLink,Guid> links`, `IUserService userService`, `IUnitOfWork unitOfWork`, `TimeProvider`. Logic:

```
ResolveOrProvisionAsync(identity):
  provider = identity.Provider.ToLowerInvariant()
  link = links.FirstOrDefault(new ExternalLoginByProviderKeySpec(provider, identity.Subject))
  if link != null:
     user = IdentityScope.ResolveUserAsync(users, link.UserId) ?? throw  // dangling link is a fault
     return (user, created:false, linked:false)
  if identity.Email is not null and identity.EmailVerified:
     existing = userService.FindByEmailAsync(identity.Email)
     if existing != null:
        await CreateLinkAsync(existing, provider, identity.Subject)   // honors platform/tenant write path
        return (existing, created:false, linked:true)
  userName = await DeriveUniqueUserNameAsync(identity)
  created = await userService.CreateExternalUserAsync(userName, identity.Email, identity.EmailVerified, identity.DisplayName)
  user = IdentityScope.ResolveUserAsync(users, created.UserId!.Value) ?? throw
  await CreateLinkAsync(user, provider, identity.Subject)
  return (user, created:true, linked:true)
```

- `CreateLinkAsync` builds `ExternalLoginLink { UserId, Provider, ExternalId=subject, TenantId=user.TenantId, CreatedAt=now }`, `SetId(Guid.CreateVersion7())`, `links.AddAsync`, then save via the **same platform/tenant-aware path** Identity uses (`IdentityScope.SaveScopedAsync(unitOfWork, filterScope, isPlatform: user.TenantId is null, ...)` — read `RefreshTokenService`/`UserService` for the exact call).
- `DeriveUniqueUserNameAsync`: prefer email local-part; if `FindByUserNameAsync` shows a collision, append a short disambiguator (e.g. 4 hex of the subject); fall back to `$"{provider}_{subject[..8]}"` when no email. Loop until unique.

> The link lookup + create must run under the framework tenant filter; do NOT add a manual `tenant_id ==` predicate (the filter owns it). For platform users (`TenantId is null`) use the bypass/save path exactly as core services do.

- [ ] **Step 4: Register** — in `AddThemiaIdentityServicesCore`: `services.TryAddScoped<IExternalLoginService, ExternalLoginService>();`

- [ ] **Step 5: Run conformance (both peers + both engines available):**
  `dotnet test tests/Themia.Modules.Identity.EFCore.IntegrationTests --filter "FullyQualifiedName~External"`
  `dotnet test tests/Themia.Modules.Identity.Dapper.SqlServer.IntegrationTests --filter "FullyQualifiedName~External"`
  Expect PASS (needs Docker).

- [ ] **Step 6: Clean build core (fix RS0016) + commit** — `feat(identity): add ExternalLoginService (link/auto-link/provision)`

---

## Task 5: AspNetCore — OIDC provider, registry, options, DI builder (TDD, unit)

**Files:** `External/OidcProviderConfig.cs`, `External/OidcExternalAuthProvider.cs`, `External/ExternalAuthProviderRegistry.cs`, `Options/ExternalAuthOptions.cs`, `DependencyInjection/ExternalAuthBuilder.cs`, `Directory.Packages.props`, `tests/Themia.Modules.Identity.AspNetCore.Tests/`.

- [ ] **Step 1: Pin packages** in `Directory.Packages.props` (match the IdentityModel version already pulled by JwtBearer; `Microsoft.Extensions.Http` matches the runtime 10.0.x):

```xml
    <PackageVersion Include="Microsoft.IdentityModel.Protocols.OpenIdConnect" Version="<match-existing-IdentityModel>" />
    <PackageVersion Include="Microsoft.Extensions.Http" Version="10.0.9" />
```
Add the two `<PackageReference>`s (no Version) to `Themia.Modules.Identity.AspNetCore.csproj`.

- [ ] **Step 2: `OidcProviderConfig`** — a record holding: `Name`, `TokenEndpoint` (Uri), `ClientId`, `ClientSecret`, `Scopes`, expected `Issuer`, expected `Audience`, and a **signing-key strategy**: either `JwksUri` (RS256/asymmetric) **or** `SymmetricSecret` (HS256 — LINE's channel secret). Plus optional claim-name overrides (sub/email/email_verified/name) and an `EmailAlwaysVerified` flag (LINE).

- [ ] **Step 3: Failing unit tests** in `Identity.AspNetCore.Tests` (`OidcExternalAuthProviderTests`) using a stub `HttpMessageHandler`:
  - success: token endpoint returns `{ id_token }` signed with the configured key; assert normalized `ExternalIdentity` (provider/subject/email/emailVerified/displayName).
  - bad signature / wrong issuer / wrong audience / expired → `Failed`.
  - token endpoint returns non-2xx (e.g. `invalid_grant`) → `Failed`.
  - Build helper to mint a test id_token (HS256 for the symmetric path; a self-signed RSA + a stub JWKS doc for the asymmetric path).

- [ ] **Step 4: Implement `OidcExternalAuthProvider`** — ctor injects `IHttpClientFactory`, the `OidcProviderConfig`, `TimeProvider`, and (for JWKS) a cached `ConfigurationManager<OpenIdConnectConfiguration>` or a small JWKS cache. `ExchangeAsync`:
  1. POST `application/x-www-form-urlencoded` to `TokenEndpoint`: `grant_type=authorization_code`, `code`, `redirect_uri`, `client_id`, `client_secret`, and `code_verifier` if present. Non-2xx → `Failed`.
  2. Read `id_token`. Validate with `JsonWebTokenHandler` (reuse the 0.5.1 setup; `MapInboundClaims=false`): `ValidIssuer`, `ValidAudience=ClientId`, `IssuerSigningKeys` from JWKS **or** the symmetric key, lifetime, `ClockSkew`. Invalid → `Failed`.
  3. Map claims → `ExternalIdentity` (subject=`sub`; email=`email`; emailVerified = `EmailAlwaysVerified ? true : bool(email_verified)`; displayName=`name`). Return `Success`.
  Network/5xx-after-retries may throw (genuine fault).

- [ ] **Step 5: `IExternalAuthProviderRegistry`** + impl — resolves `IExternalAuthProvider` by case-insensitive name from the registered set; `TryGet(name, out provider)`.

- [ ] **Step 6: Options + builder** — `ExternalAuthOptions` (+ `GoogleOptions { ClientId, ClientSecret, Scopes }`, `LineOptions { ChannelId, ChannelSecret, Scopes }`, each with `Validate()`). `AddThemiaExternalAuth()` returns a builder:
  - `.AddGoogle(o => …)` → registers an `OidcExternalAuthProvider` named `"google"` with Google's token endpoint + JWKS (`https://www.googleapis.com/oauth2/v3/certs`), `Issuer=https://accounts.google.com`, `Audience=ClientId`, default scopes `openid email profile`, reads `email_verified`.
  - `.AddLine(o => …)` → named `"line"`, LINE token endpoint (`https://api.line.me/oauth2/v2.1/token`), `SymmetricSecret=ChannelSecret` (HS256), `Issuer=https://access.line.me`, `Audience=ChannelId`, `EmailAlwaysVerified=true`.
  - `.AddProvider(IExternalAuthProvider)` / `.AddProvider<T>()` for custom providers.
  - Each provider registers a **named `HttpClient`**; options validated at startup (`ValidateOnStart`).

- [ ] **Step 7: Unit tests green** — `dotnet test tests/Themia.Modules.Identity.AspNetCore.Tests --filter "FullyQualifiedName~Oidc|FullyQualifiedName~ExternalAuthBuilder"`.

- [ ] **Step 8: Clean build (RS0016) + commit** — `feat(identity): add OIDC external-auth provider, registry, Google/LINE builder`

---

## Task 6: AspNetCore — flow orchestrator, hooks base, endpoint, DI wiring

**Files:** `External/ExternalAuthenticationFlow.cs`, `External/ExternalAuthenticationHooksBase.cs`, `Endpoints/IdentityExternalAuthEndpoints.cs`, `DependencyInjection/IdentityAspNetCoreServiceCollectionExtensions.cs`.

- [ ] **Step 1: `ExternalAuthenticationHooksBase`** — implements `IExternalAuthenticationHooks` as no-ops (`Task.CompletedTask`); registered via `TryAdd`.

- [ ] **Step 2: `ExternalAuthenticationFlow`** (implements `IExternalAuthenticationFlow`) — ctor injects `IExternalAuthProviderRegistry`, `IExternalLoginService`, `IClaimsPrincipalFactory`, `IAccessTokenService`, `IRefreshTokenService`, `IExternalAuthenticationHooks`, `TimeProvider`. `AuthenticateAsync(provider, request)`:
  1. `registry.TryGet(provider)` → else `ProviderNotFound` (fire `OnExternalLoginFailed`).
  2. `OnBeforeExternalLoginAsync` → if denied, `OnExternalLoginFailed(Denied)` + return `Denied`.
  3. `provider.ExchangeAsync` → if `!Succeeded`, `OnExternalLoginFailed(ProviderRejected)` + return `ProviderRejected`.
  4. `IExternalLoginService.ResolveOrProvisionAsync(identity)` → user.
  5. `OnExternalLoginSucceededAsync(user, created, linked)` → if denied, `OnExternalLoginFailed(Denied)` + return `Denied`.
  6. principal = `IClaimsPrincipalFactory` for the user (read how `AuthenticationFlow` builds it in 0.5.1); `access = IAccessTokenService.Issue(principal)`; `refresh = IRefreshTokenService.IssueAsync(user.Id)`; build `AuthTokens(access.Token, expiresInSeconds, refresh.RawToken)` (compute `expiresInSeconds` exactly as 0.5.1 login does).
  7. Return `Success(tokens, created, linked)`.

> Mirror `AuthenticationFlow.cs` (0.5.1) for principal building + `AuthTokens` assembly so the external terminus is byte-identical to password login.

- [ ] **Step 3: Endpoint** — `MapIdentityExternalAuthEndpoints(this IEndpointRouteBuilder)`:
  - `POST external/{provider}` binds `ExternalLoginRequest(string Code, string RedirectUri, string? CodeVerifier)`; `{provider}` from route. Validate non-blank `Code`/`RedirectUri` → else `ValidationException` (400). Call `IExternalAuthenticationFlow.AuthenticateAsync`. Map: `Success` → `200 AuthResponse` (reuse the 0.5.1 `AuthResponse`); `ProviderNotFound` → `404`; `ProviderRejected`/`Denied` → uniform `401` (reuse the 0.5.1 `UnauthorizedException`/uniform message). Errors flow through `Themia.AspNetCore` ProblemDetails.

> Reuse the 0.5.1 `AuthResponse` DTO and the same exception types `IdentityAuthEndpoints` throws — do not invent new response shapes.

- [ ] **Step 4: DI wiring** — `AddThemiaIdentityAspNetCore` (or a dedicated `AddThemiaExternalAuth` builder from Task 5) must `TryAdd`: `IExternalAuthenticationFlow → ExternalAuthenticationFlow`, `IExternalAuthenticationHooks → ExternalAuthenticationHooksBase`, `IExternalAuthProviderRegistry`. Confirm precondition guard (like 0.5.1) that `IExternalLoginService`, `IClaimsPrincipalFactory`, `IAccessTokenService`, `IRefreshTokenService` are registered; throw a clear message if missing.

- [ ] **Step 5: Flow unit tests** (`Identity.AspNetCore.Tests`, `ExternalAuthenticationFlowTests`) with a fake `IExternalAuthProvider` + fakes/mocks for the services: unknown provider → `ProviderNotFound`; provider `Failed` → `ProviderRejected`; `OnBeforeExternalLogin`/`OnExternalLoginSucceeded` `Deny()` → `Denied` (+ `OnExternalLoginFailed` fired); success issues access+refresh and fires hooks in order. Run them green.

- [ ] **Step 6: Clean build (RS0016) + commit** — `feat(identity): add external-auth flow, hooks, and POST /auth/external/{provider}`

---

## Task 7: Integration — ExternalAuthConformanceTests (PG + SQL Server, both peers)

**Files:** `tests/Themia.Modules.Identity.AspNetCore.IntegrationTests/` (new `ExternalAuthConformanceTests.cs` + a fake provider; reuse the existing `AuthFlowConformanceTests` harness shape, fixtures, and peer-wiring).

- [ ] **Step 1: Fake provider** — an in-test `IExternalAuthProvider` named `"fake"` whose `ExchangeAsync` returns a configurable `ExternalIdentity` keyed off the `Code` (e.g. `Code` encodes subject+email+verified), so tests drive identity without real network. Register it via `.AddProvider(...)` in the test host.

- [ ] **Step 2: Host wiring** — extend the existing conformance host: add `AddThemiaExternalAuth().AddProvider(fake)` + `app.MapGroup("/auth").MapIdentityExternalAuthEndpoints()` alongside the 0.5.1 endpoints + `GET /me` probe.

- [ ] **Step 3: Tests** (run on PG+EF, PG+Dapper, SqlServer+EF, SqlServer+Dapper subclasses as the harness already parameterizes):
  - `POST /auth/external/fake` (new subject) → `200` with access+refresh; `GET /me` with the access token → `200`.
  - Second `POST` same subject → `200`, and assert it's the **same** user (e.g. `/me` subject equals the first) and no duplicate user/link (query count or `/me` identity).
  - Verified-email auto-link: seed a password user via `POST /auth/login` path or store; `POST /auth/external/fake` with that **verified** email + new subject → tokens for the **same** user.
  - The issued refresh token rotates: `POST /auth/refresh` with it → `200` new pair (proves external tokens are first-class 0.5.1 tokens).
  - Unknown provider `POST /auth/external/nope` → `404`.

- [ ] **Step 4: Run** — `dotnet test tests/Themia.Modules.Identity.AspNetCore.IntegrationTests --filter "FullyQualifiedName~External"` (needs Docker). Expect PASS across peers/engines.

- [ ] **Step 5: Commit** — `test(identity): external-login HTTP conformance across peers + engines`

---

## Task 8: Docs, version bump, full build/test, PR

- [ ] **Step 1: Version** — `Directory.Build.props` `<Version>` `0.5.1` → `0.5.2`.
- [ ] **Step 2: CHANGELOG** — add a `## [0.5.2] - <date>` section: **Added** (pluggable external/OAuth login: `IExternalAuthProvider` + generic OIDC + Google & LINE, `identity.external_logins`, `POST /auth/external/{provider}`, auto-link-by-verified-email/provision, `IExternalAuthenticationHooks`). Move `[Unreleased]` notes if any.
- [ ] **Step 3: README** — document `AddThemiaExternalAuth().AddGoogle(…).AddLine(…)`, `MapIdentityExternalAuthEndpoints()`, the headless code-exchange contract, the verified-email link policy, and the client's `state`/`nonce` responsibility (§Security).
- [ ] **Step 4: Architecture overview** — update the Identity row to "✅ built (0.5.2 — external/OAuth login: pluggable providers + Google/LINE)"; note Facebook/Microsoft/Telegram as deferred additive providers.
- [ ] **Step 5: Full solution build + test** — `dotnet build Themia.sln` (0/0) and `dotnet test Themia.sln` (or at least the Identity family + a representative cross-engine pass). Fix any `RS0016`/warnings.
- [ ] **Step 6: Branch + PR** — branch `feat/identity-external-login-0.5.2`, commit remaining docs, push, open PR titled `Themia 0.5.2 — Identity external/OAuth login (pluggable providers + Google/LINE)`, summary + test plan + link to the design spec. Watch CI green.

---

## Review gates (run after implementation, before merge)

- **Recall-biased code review** (`/code-review`) over the branch diff — focus: tenant isolation on the `(provider, external_id)` lookup (filter, not manual predicate); the **unverified-email-never-links** invariant; id-token validation (issuer/audience/signature/expiry) for both the JWKS and symmetric paths; no provider secrets logged; uniform-401 for provider failures; no double-logging.
- **Adversarial verify** the auto-link path specifically: can an attacker provision/verify an email they don't own and hijack a local account? (Expected: no — auto-link requires `EmailVerified` from the provider; unverified always creates a separate user.)
- Address all CONFIRMED/PLAUSIBLE findings before merge.
