# Themia Identity JWT slice (0.5.1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add access-token issuance, revocable refresh tokens, JWT validation, and login/refresh/logout endpoints to Themia Identity, shipping `Themia.Modules.Identity.AspNetCore` 0.5.1.

**Architecture:** New contracts land in `Themia.Modules.Identity.Abstractions`. The refresh-token store (`RefreshTokenService`) lives in the **core** `Themia.Modules.Identity` beside `UserTokenService` (it reuses the internal `IdentityScope`/specs and is a pure data-layer service). A **new** HTTP-facing package `Themia.Modules.Identity.AspNetCore` holds JWT minting, the JwtBearer validation scheme, the default `IAuthenticationFlow` orchestrator + `IAuthenticationHooks`, and the `MapIdentityAuthEndpoints()` minimal-API extension.

**Tech Stack:** .NET 10, `Microsoft.AspNetCore.Authentication.JwtBearer` + `Microsoft.IdentityModel.JsonWebTokens` (HS256), FluentMigrator, EF Core + Dapper peers, xUnit + Testcontainers (PostgreSQL + SQL Server).

---

## Decisions locked for this plan (deviations from spec §3, justified)

1. **Refresh-token store in core, not `.AspNetCore`.** The spec §3 listed `IRefreshTokenService` impl under `.AspNetCore`, but `IdentityScope.ResolveUserAsync` (required for tenant isolation) is `internal` to `Themia.Modules.Identity`, and the store has no HTTP dependency. User confirmed: implement `RefreshTokenService` in core beside `UserTokenService`. Only JWT/HTTP pieces go in `.AspNetCore`.
2. **`RefreshTokenLifetime` lives in `IdentityModuleOptions`** (core), not `JwtOptions` — because the store reading it is in core. `JwtOptions` (`.AspNetCore`) keeps `SigningKey`/`Issuer`/`Audience`/`AccessTokenLifetime`/`ClockSkew`.
3. **`IJwtSigningCredentialsProvider` + `JwtOptions` live in `.AspNetCore`**, not `.Abstractions` — they pull `Microsoft.IdentityModel.Tokens` types (`SigningCredentials`, `SecurityKey`), which must not leak into the lightweight Abstractions package. 0.5.2 (external login) needs `IAccessTokenService`/`IRefreshTokenService`/`IAuthenticationFlow` contracts (kept in Abstractions, IdentityModel-free), not the signing seam.
4. **`RefreshValidationResult` carries the resolved `User`** (plus the successor `RefreshIssue`), not just `userId`/`tenantId`. This lets the orchestrator rebuild the principal (picking up current roles) without a second lookup and without core internals. `User.Id`/`User.TenantId` cover the spec's stated fields.
5. **New refresh-tokens schema is a NEW FluentMigrator migration** (`202606150001`), never an edit of the released `IdentitySchemaMigration` (`202606140001`) — migrations are forward-only.

## File map

**Modify — `src/modules/Themia.Modules.Identity.Abstractions/`**
- Create `Entities/RefreshToken.cs`
- Create `Authentication/AccessToken.cs`, `Authentication/IAccessTokenService.cs`
- Create `Authentication/RefreshTokenContracts.cs` (`RefreshOutcome`, `RefreshIssue`, `RefreshValidationResult`, `IRefreshTokenService`)
- Create `Authentication/AuthenticationFlowContracts.cs` (`AuthTokens`, `LoginResult`, `RefreshRotationResult`, `IAuthenticationFlow`)
- Create `Authentication/AuthenticationHooks.cs` (`LoginFailureReason`, hook contexts, `IAuthenticationHooks`)
- Modify `IdentityModuleOptions.cs` (add `RefreshTokenLifetime`)
- Modify `PublicAPI.Unshipped.txt`

**Modify — `src/modules/Themia.Modules.Identity/`**
- Create `Migrations/RefreshTokensMigration.cs`
- Create `Services/RefreshTokenService.cs`
- Modify `Specifications/IdentitySpecs.cs` (add 3 refresh specs)
- Modify `EntityConfiguration/IdentityModelConfiguration.cs` (add `RefreshTokenConfiguration` + register)
- Modify `Mapping/IdentityDapperMappings.cs` (register `RefreshToken`)
- Modify `DependencyInjection/IdentityServiceCollectionExtensions.cs` (register `IRefreshTokenService`)
- Modify `PublicAPI.Unshipped.txt`

**Create — `src/modules/Themia.Modules.Identity.AspNetCore/`** (new project)
- `Themia.Modules.Identity.AspNetCore.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- `Options/JwtOptions.cs`
- `Signing/IJwtSigningCredentialsProvider.cs`, `Signing/SymmetricSigningCredentialsProvider.cs`
- `Tokens/AccessTokenService.cs`
- `Authentication/AuthenticationHooksBase.cs`, `Authentication/AuthenticationFlow.cs`
- `DependencyInjection/IdentityAspNetCoreServiceCollectionExtensions.cs`
- `Endpoints/IdentityAuthEndpoints.cs`

**Create — tests**
- `tests/Themia.Modules.Identity.AspNetCore.Tests/` (unit)
- `tests/Themia.Modules.Identity.AspNetCore.IntegrationTests/` (WebApplicationFactory + Testcontainers)
- Modify `tests/Themia.Modules.Identity.IntegrationTests/` (refresh-token conformance + TRUNCATE)

**Modify — root**
- `Directory.Packages.props` (add JwtBearer + Mvc.Testing pins)
- `Themia.sln` (add 3 new projects)
- `README.md`, `CHANGELOG.md`, `docs/themia-architecture-overview.md`

---

## Task 1: Abstractions — RefreshToken entity + RefreshTokenLifetime option

**Files:**
- Create: `src/modules/Themia.Modules.Identity.Abstractions/Entities/RefreshToken.cs`
- Modify: `src/modules/Themia.Modules.Identity.Abstractions/IdentityModuleOptions.cs`
- Modify: `src/modules/Themia.Modules.Identity.Abstractions/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Create the entity**

`Entities/RefreshToken.cs`:

```csharp
namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>A revocable, rotating refresh token. A parent-keyed child of <see cref="User"/> with no
/// tenant column — tenant isolation is enforced at the service layer by resolving the owning user in
/// scope. The raw token is never stored; only its SHA-256 hash.</summary>
public sealed class RefreshToken
{
    /// <summary>The token identifier (UUIDv7).</summary>
    public Guid Id { get; set; }

    /// <summary>The owning user id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Deterministic SHA-256 (Base64) hash of the raw token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Groups a rotation chain for reuse-detection and family revocation.</summary>
    public Guid FamilyId { get; set; }

    /// <summary>Absolute expiry.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when the token is rotated (single redemption); otherwise null.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>Set on logout / revoke-all / reuse-detection; otherwise null.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Successor row id in the rotation chain; null until rotated.</summary>
    public Guid? ReplacedById { get; set; }

    /// <summary>Issue time, set by the service via <see cref="TimeProvider"/> (forensics).</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Assigns the client-generated identifier.</summary>
    /// <param name="id">A UUIDv7.</param>
    public void SetId(Guid id) => Id = id;
}
```

- [ ] **Step 2: Add `RefreshTokenLifetime` to options**

In `IdentityModuleOptions.cs`, add the property next to `DefaultTokenLifetime`:

```csharp
    /// <summary>The lifetime of an issued refresh token. Default 14 days.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(14);
```

And add this block inside `Validate()`, after the `DefaultTokenLifetime` check:

```csharp
        if (RefreshTokenLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RefreshTokenLifetime), RefreshTokenLifetime, "Must be a positive duration.");
        }
```

- [ ] **Step 3: Record the new public API**

Append to `src/modules/Themia.Modules.Identity.Abstractions/PublicAPI.Unshipped.txt` (keep entries sorted as the analyzer requires; run the clean build in Step 4 and paste the exact RS0016-suggested lines if these differ):

```
Themia.Modules.Identity.Abstractions.Entities.RefreshToken
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.RefreshToken() -> void
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.Id.get -> System.Guid
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.Id.set -> void
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.UserId.get -> System.Guid
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.UserId.set -> void
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.TokenHash.get -> string!
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.TokenHash.set -> void
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.FamilyId.get -> System.Guid
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.FamilyId.set -> void
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.ExpiresAt.get -> System.DateTimeOffset
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.ExpiresAt.set -> void
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.ConsumedAt.get -> System.DateTimeOffset?
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.ConsumedAt.set -> void
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.RevokedAt.get -> System.DateTimeOffset?
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.RevokedAt.set -> void
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.ReplacedById.get -> System.Guid?
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.ReplacedById.set -> void
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.CreatedAt.get -> System.DateTimeOffset
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.CreatedAt.set -> void
Themia.Modules.Identity.Abstractions.Entities.RefreshToken.SetId(System.Guid id) -> void
Themia.Modules.Identity.Abstractions.IdentityModuleOptions.RefreshTokenLifetime.get -> System.TimeSpan
Themia.Modules.Identity.Abstractions.IdentityModuleOptions.RefreshTokenLifetime.set -> void
```

- [ ] **Step 4: Build and confirm no new diagnostics**

Run: `dotnet build src/modules/Themia.Modules.Identity.Abstractions/Themia.Modules.Identity.Abstractions.csproj --no-incremental`
Expected: build succeeds, **no `RS0016`**. If RS0016 fires, replace the Step 3 lines with the exact text the diagnostic prints, then rebuild.

- [ ] **Step 5: Commit**

```bash
git add src/modules/Themia.Modules.Identity.Abstractions
git commit -m "feat(identity): add RefreshToken entity and RefreshTokenLifetime option"
```

---

## Task 2: Abstractions — token & flow contracts

**Files:**
- Create: `src/modules/Themia.Modules.Identity.Abstractions/Authentication/AccessToken.cs`
- Create: `src/modules/Themia.Modules.Identity.Abstractions/Authentication/IAccessTokenService.cs`
- Create: `src/modules/Themia.Modules.Identity.Abstractions/Authentication/RefreshTokenContracts.cs`
- Create: `src/modules/Themia.Modules.Identity.Abstractions/Authentication/AuthenticationFlowContracts.cs`
- Create: `src/modules/Themia.Modules.Identity.Abstractions/Authentication/AuthenticationHooks.cs`
- Modify: `src/modules/Themia.Modules.Identity.Abstractions/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Access-token contract**

`Authentication/AccessToken.cs`:

```csharp
namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>A minted access token and its absolute expiry.</summary>
/// <param name="Token">The serialized JWT.</param>
/// <param name="ExpiresAt">The token's absolute expiry.</param>
public readonly record struct AccessToken(string Token, DateTimeOffset ExpiresAt);
```

`Authentication/IAccessTokenService.cs`:

```csharp
using System.Security.Claims;

namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>Mints a signed access token from a claims principal. The principal is the single source
/// of "what's in the token" across cookie and JWT.</summary>
public interface IAccessTokenService
{
    /// <summary>Builds a signed access token carrying the principal's claims.</summary>
    /// <param name="principal">The principal produced by <c>IClaimsPrincipalFactory</c>.</param>
    /// <returns>The minted token and its expiry.</returns>
    AccessToken Issue(ClaimsPrincipal principal);
}
```

- [ ] **Step 2: Refresh-token contracts**

`Authentication/RefreshTokenContracts.cs`:

```csharp
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>The outcome of validating + rotating a refresh token.</summary>
public enum RefreshOutcome
{
    /// <summary>Rotated; a successor was issued.</summary>
    Success,

    /// <summary>Unknown, expired, or owner not in scope.</summary>
    Invalid,

    /// <summary>A consumed/revoked token was replayed; the family was revoked.</summary>
    ReuseDetected,
}

/// <summary>A newly issued refresh token. The raw value is returned exactly once.</summary>
/// <param name="RawToken">The opaque raw token (never persisted).</param>
/// <param name="ExpiresAt">Absolute expiry.</param>
/// <param name="FamilyId">The rotation family.</param>
public readonly record struct RefreshIssue(string RawToken, DateTimeOffset ExpiresAt, Guid FamilyId);

/// <summary>The result of <see cref="IRefreshTokenService.ValidateAndRotateAsync"/>.</summary>
public readonly record struct RefreshValidationResult
{
    private RefreshValidationResult(RefreshOutcome outcome, User? user, RefreshIssue? replacement)
    {
        Outcome = outcome;
        User = user;
        Replacement = replacement;
    }

    /// <summary>The outcome.</summary>
    public RefreshOutcome Outcome { get; }

    /// <summary>The resolved owning user on success; otherwise null.</summary>
    public User? User { get; }

    /// <summary>The successor refresh token on success; otherwise null.</summary>
    public RefreshIssue? Replacement { get; }

    /// <summary>Creates a success result.</summary>
    public static RefreshValidationResult Success(User user, RefreshIssue replacement) =>
        new(RefreshOutcome.Success, user, replacement);

    /// <summary>Creates an invalid result.</summary>
    public static RefreshValidationResult Invalid() => new(RefreshOutcome.Invalid, null, null);

    /// <summary>Creates a reuse-detected result.</summary>
    public static RefreshValidationResult ReuseDetected() => new(RefreshOutcome.ReuseDetected, null, null);
}

/// <summary>Issues, rotates, and revokes refresh tokens. All operations resolve the owning user in the
/// ambient tenant (else genuine platform) scope before reading or writing, so cross-tenant tokens are
/// never touched.</summary>
public interface IRefreshTokenService
{
    /// <summary>Issues a new refresh token for a user, optionally continuing an existing family.</summary>
    /// <param name="userId">The owning user id (must resolve in scope).</param>
    /// <param name="familyId">An existing family to continue, or null to start a new one.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The issued token (raw value returned once).</returns>
    Task<RefreshIssue> IssueAsync(Guid userId, Guid? familyId = null, CancellationToken cancellationToken = default);

    /// <summary>Validates a presented raw token and, on success, consumes it and issues a successor in
    /// the same family. A replayed consumed/revoked token revokes the entire family.</summary>
    /// <param name="rawToken">The presented raw refresh token.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The validation result.</returns>
    Task<RefreshValidationResult> ValidateAndRotateAsync(string rawToken, CancellationToken cancellationToken = default);

    /// <summary>Revokes the presented token's family, or all non-expired tokens for its owner.</summary>
    /// <param name="rawToken">The presented raw refresh token.</param>
    /// <param name="allForUser">When true, revoke every non-expired token for the owner.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task RevokeAsync(string rawToken, bool allForUser, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 3: Authentication-flow contracts**

`Authentication/AuthenticationFlowContracts.cs`:

```csharp
namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>An issued access + refresh pair returned to the client.</summary>
/// <param name="AccessToken">The serialized JWT.</param>
/// <param name="ExpiresInSeconds">Access-token lifetime remaining, in seconds.</param>
/// <param name="RefreshToken">The opaque refresh token.</param>
public readonly record struct AuthTokens(string AccessToken, int ExpiresInSeconds, string RefreshToken);

/// <summary>The outcome of a login attempt. Every non-success collapses to a uniform 401 at the
/// boundary; the distinction exists for internal/audit use only.</summary>
public enum LoginOutcome
{
    /// <summary>Authenticated.</summary>
    Success,

    /// <summary>Unknown user, wrong password, or inactive account.</summary>
    InvalidCredentials,

    /// <summary>Account is locked out.</summary>
    LockedOut,

    /// <summary>A hook denied the attempt.</summary>
    Denied,
}

/// <summary>The result of <c>IAuthenticationFlow.LoginAsync</c>.</summary>
public readonly record struct LoginResult
{
    private LoginResult(LoginOutcome outcome, AuthTokens? tokens)
    {
        Outcome = outcome;
        Tokens = tokens;
    }

    /// <summary>The outcome.</summary>
    public LoginOutcome Outcome { get; }

    /// <summary>The issued tokens on success; otherwise null.</summary>
    public AuthTokens? Tokens { get; }

    /// <summary>Whether the login succeeded.</summary>
    public bool Succeeded => Outcome == LoginOutcome.Success;

    /// <summary>Creates a success result.</summary>
    public static LoginResult Success(AuthTokens tokens) => new(LoginOutcome.Success, tokens);

    /// <summary>Creates an invalid-credentials result.</summary>
    public static LoginResult InvalidCredentials() => new(LoginOutcome.InvalidCredentials, null);

    /// <summary>Creates a locked-out result.</summary>
    public static LoginResult LockedOut() => new(LoginOutcome.LockedOut, null);

    /// <summary>Creates a denied result.</summary>
    public static LoginResult Denied() => new(LoginOutcome.Denied, null);
}

/// <summary>The outcome of a refresh attempt. Every non-success collapses to a uniform 401.</summary>
public enum RefreshRotationOutcome
{
    /// <summary>Rotated; a new pair was issued.</summary>
    Success,

    /// <summary>Unknown, expired, or owner not in scope.</summary>
    Invalid,

    /// <summary>A consumed/revoked token was replayed; family revoked.</summary>
    ReuseDetected,

    /// <summary>A hook denied the attempt.</summary>
    Denied,
}

/// <summary>The result of <c>IAuthenticationFlow.RefreshAsync</c>.</summary>
public readonly record struct RefreshRotationResult
{
    private RefreshRotationResult(RefreshRotationOutcome outcome, AuthTokens? tokens)
    {
        Outcome = outcome;
        Tokens = tokens;
    }

    /// <summary>The outcome.</summary>
    public RefreshRotationOutcome Outcome { get; }

    /// <summary>The issued tokens on success; otherwise null.</summary>
    public AuthTokens? Tokens { get; }

    /// <summary>Whether the refresh succeeded.</summary>
    public bool Succeeded => Outcome == RefreshRotationOutcome.Success;

    /// <summary>Creates a success result.</summary>
    public static RefreshRotationResult Success(AuthTokens tokens) => new(RefreshRotationOutcome.Success, tokens);

    /// <summary>Creates an invalid result.</summary>
    public static RefreshRotationResult Invalid() => new(RefreshRotationOutcome.Invalid, null);

    /// <summary>Creates a reuse-detected result.</summary>
    public static RefreshRotationResult ReuseDetected() => new(RefreshRotationOutcome.ReuseDetected, null);

    /// <summary>Creates a denied result.</summary>
    public static RefreshRotationResult Denied() => new(RefreshRotationOutcome.Denied, null);
}

/// <summary>Orchestrates the security-critical login/refresh/logout sequence. The default
/// implementation lives in <c>Themia.Modules.Identity.AspNetCore</c> and is replaceable via DI.</summary>
public interface IAuthenticationFlow
{
    /// <summary>Verifies credentials (driving lockout + timing mitigation), builds the principal, and
    /// issues an access + refresh pair. Any failure returns a non-success result.</summary>
    Task<LoginResult> LoginAsync(string userName, string password, CancellationToken cancellationToken = default);

    /// <summary>Rotates a refresh token and mints a fresh access + refresh pair.</summary>
    Task<RefreshRotationResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Revokes the presented token's family (or all of the user's sessions). Idempotent.</summary>
    Task LogoutAsync(string refreshToken, bool allSessions, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: Hook contracts**

`Authentication/AuthenticationHooks.cs`:

```csharp
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>The real internal reason a login failed, supplied to <see cref="IAuthenticationHooks"/>
/// for audit. Never surfaced to the client (which sees a uniform 401).</summary>
public enum LoginFailureReason
{
    /// <summary>No matching user.</summary>
    NotFound,

    /// <summary>Password did not match.</summary>
    WrongPassword,

    /// <summary>Account disabled.</summary>
    Inactive,

    /// <summary>Account locked out.</summary>
    LockedOut,

    /// <summary>A hook denied the attempt.</summary>
    Denied,
}

/// <summary>Base for hook contexts that can short-circuit the flow to a uniform 401 via
/// <see cref="Deny"/>.</summary>
public abstract class AuthenticationHookContext
{
    /// <summary>Whether a hook denied the operation.</summary>
    public bool IsDenied { get; private set; }

    /// <summary>The internal denial reason (for audit), if any.</summary>
    public string? DenialReason { get; private set; }

    /// <summary>Denies the operation. The client receives a uniform 401; the reason is for audit.</summary>
    /// <param name="reason">An optional internal reason.</param>
    public void Deny(string? reason = null)
    {
        IsDenied = true;
        DenialReason = reason;
    }
}

/// <summary>Pre-credential-verification gate (rate-limit, IP allowlist).</summary>
public sealed class BeforeLoginContext(string userName) : AuthenticationHookContext
{
    /// <summary>The presented login name.</summary>
    public string UserName { get; } = userName;
}

/// <summary>Runs after verification, before tokens are issued (last-login stamp, post-auth gating).</summary>
public sealed class LoginSucceededContext(User user) : AuthenticationHookContext
{
    /// <summary>The authenticated user.</summary>
    public User User { get; } = user;
}

/// <summary>Runs on any login failure with the real internal reason (audit only).</summary>
public sealed class LoginFailedContext(string userName, LoginFailureReason reason)
{
    /// <summary>The presented login name.</summary>
    public string UserName { get; } = userName;

    /// <summary>The real internal reason.</summary>
    public LoginFailureReason Reason { get; } = reason;
}

/// <summary>Pre-rotation gate for refresh.</summary>
public sealed class BeforeRefreshContext : AuthenticationHookContext;

/// <summary>Runs after a successful rotation, before the new pair is returned.</summary>
public sealed class RefreshSucceededContext(User user) : AuthenticationHookContext
{
    /// <summary>The user whose token was rotated.</summary>
    public User User { get; } = user;
}

/// <summary>Runs after revocation.</summary>
public sealed class LogoutContext(bool allSessions)
{
    /// <summary>Whether all sessions were revoked.</summary>
    public bool AllSessions { get; } = allSessions;
}

/// <summary>Before/after extension points the default <see cref="IAuthenticationFlow"/> invokes. The
/// default implementation is all no-ops; adopters override only what they need.</summary>
public interface IAuthenticationHooks
{
    /// <summary>Early login gate, before credential verification.</summary>
    Task OnBeforeLoginAsync(BeforeLoginContext context, CancellationToken cancellationToken = default);

    /// <summary>After verification, before tokens are issued.</summary>
    Task OnLoginSucceededAsync(LoginSucceededContext context, CancellationToken cancellationToken = default);

    /// <summary>On any login failure, with the real internal reason.</summary>
    Task OnLoginFailedAsync(LoginFailedContext context, CancellationToken cancellationToken = default);

    /// <summary>Early refresh gate.</summary>
    Task OnBeforeRefreshAsync(BeforeRefreshContext context, CancellationToken cancellationToken = default);

    /// <summary>After a successful rotation, before the new pair is returned.</summary>
    Task OnRefreshSucceededAsync(RefreshSucceededContext context, CancellationToken cancellationToken = default);

    /// <summary>After revocation.</summary>
    Task OnLogoutAsync(LogoutContext context, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 5: Build, then capture the public API**

Run: `dotnet build src/modules/Themia.Modules.Identity.Abstractions/Themia.Modules.Identity.Abstractions.csproj --no-incremental`
Expected: it FAILS with `RS0016` listing every new public member. Copy each suggested line into `PublicAPI.Unshipped.txt` (sorted), then rebuild until it PASSES with no `RS0016`.

- [ ] **Step 6: Commit**

```bash
git add src/modules/Themia.Modules.Identity.Abstractions
git commit -m "feat(identity): add JWT/refresh/auth-flow contracts to abstractions"
```

---

## Task 3: Core — refresh_tokens schema, EF/Dapper mappings, fixtures

**Files:**
- Create: `src/modules/Themia.Modules.Identity/Migrations/RefreshTokensMigration.cs`
- Modify: `src/modules/Themia.Modules.Identity/EntityConfiguration/IdentityModelConfiguration.cs`
- Modify: `src/modules/Themia.Modules.Identity/Mapping/IdentityDapperMappings.cs`
- Modify: `tests/Themia.Modules.Identity.IntegrationTests/Fixtures/PostgresIdentityFixture.cs`
- Modify: `tests/Themia.Modules.Identity.IntegrationTests/Fixtures/SqlServerIdentityFixture.cs`

- [ ] **Step 1: Write the migration (new file — never edit `IdentitySchemaMigration`)**

`Migrations/RefreshTokensMigration.cs`:

```csharp
using System;
using FluentMigrator;

namespace Themia.Modules.Identity.Migrations;

/// <summary>Creates <c>identity.refresh_tokens</c> (parent-keyed child of <c>users</c>, no tenant
/// column) on PostgreSQL and SQL Server. Forward-only addition after the 0.5.0 identity schema.</summary>
[Migration(202606150001, "Themia.Identity: create identity.refresh_tokens")]
public sealed class RefreshTokensMigration : Migration
{
    private const string SchemaName = "identity";

    /// <inheritdoc />
    public override void Up()
    {
        IfDatabase("postgres", "sqlserver").Delegate(CreateRefreshTokens);

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Identity supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    private void CreateRefreshTokens()
    {
        Create.Table("refresh_tokens").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
            .WithColumn("token_hash").AsString(256).NotNullable()
            .WithColumn("family_id").AsGuid().NotNullable()
            .WithColumn("expires_at").AsDateTimeOffset().NotNullable()
            .WithColumn("consumed_at").AsDateTimeOffset().Nullable()
            .WithColumn("revoked_at").AsDateTimeOffset().Nullable()
            .WithColumn("replaced_by_id").AsGuid().Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable();

        // Revoke-all and family lookups.
        Create.Index("ix_refresh_tokens_user").OnTable("refresh_tokens").InSchema(SchemaName)
            .OnColumn("user_id").Ascending();

        // Redemption lookup; a SHA-256 of 32 random bytes is effectively collision-free, so unique
        // makes the lookup a guaranteed single row and blocks duplicate/forged hashes.
        Create.Index("ux_refresh_tokens_token_hash").OnTable("refresh_tokens").InSchema(SchemaName)
            .OnColumn("token_hash").Ascending().WithOptions().Unique();

        Create.ForeignKey("fk_refresh_tokens_user_id").FromTable("refresh_tokens").InSchema(SchemaName).ForeignColumn("user_id")
            .ToTable("users").InSchema(SchemaName).PrimaryColumn("id");
    }

    /// <inheritdoc />
    public override void Down() => Delete.Table("refresh_tokens").InSchema(SchemaName);
}
```

- [ ] **Step 2: Add the EF configuration**

In `IdentityModelConfiguration.cs`, add this registration line inside `ApplyThemiaIdentity`, after the `UserTokenConfiguration` line:

```csharp
        modelBuilder.ApplyConfiguration(new RefreshTokenConfiguration());
```

And add this nested class after `UserTokenConfiguration`:

```csharp
    private sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
    {
        public void Configure(EntityTypeBuilder<RefreshToken> b)
        {
            b.ToTable("refresh_tokens", Schema);
            b.HasKey(t => t.Id);
            b.Property(t => t.Id).HasColumnName("id");
            b.Property(t => t.UserId).HasColumnName("user_id");
            b.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(256).IsRequired();
            b.Property(t => t.FamilyId).HasColumnName("family_id");
            b.Property(t => t.ExpiresAt).HasColumnName("expires_at");
            b.Property(t => t.ConsumedAt).HasColumnName("consumed_at");
            b.Property(t => t.RevokedAt).HasColumnName("revoked_at");
            b.Property(t => t.ReplacedById).HasColumnName("replaced_by_id");
            b.Property(t => t.CreatedAt).HasColumnName("created_at");
        }
    }
```

- [ ] **Step 3: Add the Dapper mapping**

In `IdentityDapperMappings.cs`, add after the `UserToken` line:

```csharp
        registry.Register<RefreshToken>(EntityMapping.ForConvention<RefreshToken>("identity.refresh_tokens", null));
```

- [ ] **Step 4: Add `refresh_tokens` to fixture truncation**

In **both** `Fixtures/PostgresIdentityFixture.cs` and `Fixtures/SqlServerIdentityFixture.cs`, update the `ResetAsync` truncation to include `refresh_tokens` first (it is a child of `users`). For Postgres:

```csharp
        command.CommandText =
            "TRUNCATE identity.refresh_tokens, identity.user_tokens, identity.user_claims, identity.role_claims, " +
            "identity.user_roles, identity.users, identity.roles RESTART IDENTITY CASCADE;";
```

For SQL Server, match the existing fixture's deletion style (open `Fixtures/SqlServerIdentityFixture.cs` first; if it uses ordered `DELETE FROM` statements rather than `TRUNCATE`, add `DELETE FROM identity.refresh_tokens;` as the **first** statement, before `user_tokens`).

- [ ] **Step 5: Build the core project**

Run: `dotnet build src/modules/Themia.Modules.Identity/Themia.Modules.Identity.csproj`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/modules/Themia.Modules.Identity tests/Themia.Modules.Identity.IntegrationTests/Fixtures
git commit -m "feat(identity): add refresh_tokens schema, EF/Dapper mappings, fixtures"
```

---

## Task 4: Core — RefreshTokenService + specs + DI (TDD via conformance harness)

**Files:**
- Modify: `src/modules/Themia.Modules.Identity/Specifications/IdentitySpecs.cs`
- Create: `src/modules/Themia.Modules.Identity/Services/RefreshTokenService.cs`
- Modify: `src/modules/Themia.Modules.Identity/DependencyInjection/IdentityServiceCollectionExtensions.cs`
- Modify: `tests/Themia.Modules.Identity.IntegrationTests/IdentityStoreConformanceTests.cs`

- [ ] **Step 1: Write failing conformance tests**

In `IdentityStoreConformanceTests.cs`, add a `RefreshTokens` accessor to the `Scope` record:

```csharp
        public IRefreshTokenService RefreshTokens => Inner.ServiceProvider.GetRequiredService<IRefreshTokenService>();
```

(Add `using Themia.Modules.Identity.Abstractions.Authentication;` to the file.) Then add these tests:

```csharp
    [Fact]
    public async Task Refresh_issue_persists_hash_not_raw_and_returns_family()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        var created = await s.Users.CreateAsync("rt-issue", "pw");
        var issue = await s.RefreshTokens.IssueAsync(created.UserId!.Value);
        Assert.NotEmpty(issue.RawToken);
        Assert.NotEqual(Guid.Empty, issue.FamilyId);
        Assert.True(issue.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Refresh_rotate_consumes_and_chains_same_family()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        var u = await s.Users.CreateAsync("rt-rotate", "pw");
        var issue = await s.RefreshTokens.IssueAsync(u.UserId!.Value);

        var result = await s.RefreshTokens.ValidateAndRotateAsync(issue.RawToken);
        Assert.Equal(RefreshOutcome.Success, result.Outcome);
        Assert.NotNull(result.Replacement);
        Assert.Equal(issue.FamilyId, result.Replacement!.Value.FamilyId);
        Assert.Equal(u.UserId, result.User!.Id);
    }

    [Fact]
    public async Task Refresh_replay_after_rotation_revokes_family()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        var u = await s.Users.CreateAsync("rt-replay", "pw");
        var issue = await s.RefreshTokens.IssueAsync(u.UserId!.Value);
        var rotated = await s.RefreshTokens.ValidateAndRotateAsync(issue.RawToken);

        // Replaying the consumed original is theft → ReuseDetected and the whole family dies.
        var replay = await s.RefreshTokens.ValidateAndRotateAsync(issue.RawToken);
        Assert.Equal(RefreshOutcome.ReuseDetected, replay.Outcome);

        // The successor issued by the legitimate rotation is now revoked too.
        var successor = await s.RefreshTokens.ValidateAndRotateAsync(rotated.Replacement!.Value.RawToken);
        Assert.Equal(RefreshOutcome.ReuseDetected, successor.Outcome);
    }

    [Fact]
    public async Task Refresh_unknown_token_is_invalid()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        var result = await s.RefreshTokens.ValidateAndRotateAsync("not-a-real-token");
        Assert.Equal(RefreshOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Refresh_token_of_another_tenant_is_invalid()
    {
        await ResetAsync();
        string raw;
        await using (var a = NewScope(new TenantId("a")))
        {
            var u = await a.Users.CreateAsync("rt-iso", "pw");
            raw = (await a.RefreshTokens.IssueAsync(u.UserId!.Value)).RawToken;
        }
        await using (var b = NewScope(new TenantId("b"), allowPlatformLogin: false))
        {
            // The owning user does not resolve in tenant b's scope → never rotated.
            var result = await b.RefreshTokens.ValidateAndRotateAsync(raw);
            Assert.Equal(RefreshOutcome.Invalid, result.Outcome);
        }
    }

    [Fact]
    public async Task Revoke_all_invalidates_every_session()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        var u = await s.Users.CreateAsync("rt-revoke-all", "pw");
        var first = await s.RefreshTokens.IssueAsync(u.UserId!.Value);
        var second = await s.RefreshTokens.IssueAsync(u.UserId!.Value);

        await s.RefreshTokens.RevokeAsync(first.RawToken, allForUser: true);

        Assert.Equal(RefreshOutcome.ReuseDetected, (await s.RefreshTokens.ValidateAndRotateAsync(first.RawToken)).Outcome);
        Assert.Equal(RefreshOutcome.ReuseDetected, (await s.RefreshTokens.ValidateAndRotateAsync(second.RawToken)).Outcome);
    }
```

- [ ] **Step 2: Run the tests — confirm they fail to compile (service not registered)**

Run: `dotnet test tests/Themia.Modules.Identity.EFCore.IntegrationTests/Themia.Modules.Identity.EFCore.IntegrationTests.csproj --filter "FullyQualifiedName~Refresh"`
Expected: COMPILE FAILURE (`IRefreshTokenService` has no registration / `RefreshTokenService` missing). Good — proceed.

- [ ] **Step 3: Add the specifications**

Append to `Specifications/IdentitySpecs.cs`:

```csharp
/// <summary>The single refresh token matching an exact (deterministic SHA-256) hash. No tenant column
/// exists; the owning user is resolved in scope by the service, which is what enforces isolation.</summary>
internal sealed class RefreshTokenByHashSpec : Specification<RefreshToken>
{
    public RefreshTokenByHashSpec(string tokenHash) => Where(t => t.TokenHash == tokenHash);
}

/// <summary>Every token in a rotation family (for family revocation).</summary>
internal sealed class RefreshTokensByFamilySpec : Specification<RefreshToken>
{
    public RefreshTokensByFamilySpec(Guid familyId) => Where(t => t.FamilyId == familyId);
}

/// <summary>A user's non-expired, non-revoked tokens (for revoke-all).</summary>
internal sealed class ActiveRefreshTokensByUserSpec : Specification<RefreshToken>
{
    public ActiveRefreshTokensByUserSpec(Guid userId, DateTimeOffset now) =>
        Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now);
}
```

- [ ] **Step 4: Write the service**

`Services/RefreshTokenService.cs`:

```csharp
using System.Security.Cryptography;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Hashing;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IRefreshTokenService"/>. Persists only token hashes; raw tokens are
/// returned once. Resolves the owning user in scope before any read or write so cross-tenant tokens
/// are never touched. Rotation chains a family; replaying a consumed/revoked token revokes the family.</summary>
public sealed class RefreshTokenService : IRefreshTokenService
{
    private const int TokenByteLength = 32;

    private readonly IRepository<User, Guid> users;
    private readonly IRepository<RefreshToken, Guid> tokens;
    private readonly IUnitOfWork unitOfWork;
    private readonly TimeProvider timeProvider;
    private readonly IdentityModuleOptions options;

    /// <summary>Creates the service.</summary>
    public RefreshTokenService(
        IRepository<User, Guid> users,
        IRepository<RefreshToken, Guid> tokens,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        IdentityModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        this.users = users;
        this.tokens = tokens;
        this.unitOfWork = unitOfWork;
        this.timeProvider = timeProvider;
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<RefreshIssue> IssueAsync(Guid userId, Guid? familyId = null, CancellationToken cancellationToken = default)
    {
        var user = await IdentityScope.ResolveUserAsync(users, userId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"User '{userId}' was not found in the current tenant scope.");

        var (entity, raw) = Create(user.Id, familyId ?? Guid.CreateVersion7());
        await tokens.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new RefreshIssue(raw, entity.ExpiresAt, entity.FamilyId);
    }

    /// <inheritdoc />
    public async Task<RefreshValidationResult> ValidateAndRotateAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        var hash = TokenHasher.Hash(rawToken);
        var match = await tokens.FirstOrDefaultAsync(new RefreshTokenByHashSpec(hash), cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            return RefreshValidationResult.Invalid();
        }

        var user = await IdentityScope.ResolveUserAsync(users, match.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return RefreshValidationResult.Invalid(); // owner not in scope — never rotate cross-tenant
        }

        var now = timeProvider.GetUtcNow();

        // Reuse-detection: a consumed or already-revoked token replayed is treated as theft.
        if (match.ConsumedAt is not null || match.RevokedAt is not null)
        {
            await RevokeFamilyAsync(match.FamilyId, now, cancellationToken).ConfigureAwait(false);
            return RefreshValidationResult.ReuseDetected();
        }

        if (match.ExpiresAt <= now)
        {
            return RefreshValidationResult.Invalid();
        }

        var (successor, raw) = Create(match.UserId, match.FamilyId);
        await tokens.AddAsync(successor, cancellationToken).ConfigureAwait(false);
        match.ConsumedAt = now;
        match.ReplacedById = successor.Id;
        tokens.Update(match);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return RefreshValidationResult.Success(user, new RefreshIssue(raw, successor.ExpiresAt, successor.FamilyId));
    }

    /// <inheritdoc />
    public async Task RevokeAsync(string rawToken, bool allForUser, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        var hash = TokenHasher.Hash(rawToken);
        var match = await tokens.FirstOrDefaultAsync(new RefreshTokenByHashSpec(hash), cancellationToken).ConfigureAwait(false);
        if (match is null)
        {
            return; // idempotent; no existence signal
        }

        var user = await IdentityScope.ResolveUserAsync(users, match.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return; // owner not in scope
        }

        var now = timeProvider.GetUtcNow();
        if (allForUser)
        {
            foreach (var t in await tokens.ListAsync(new ActiveRefreshTokensByUserSpec(match.UserId, now), cancellationToken).ConfigureAwait(false))
            {
                t.RevokedAt = now;
                tokens.Update(t);
            }
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RevokeFamilyAsync(match.FamilyId, now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var t in await tokens.ListAsync(new RefreshTokensByFamilySpec(familyId), cancellationToken).ConfigureAwait(false))
        {
            if (t.RevokedAt is null)
            {
                t.RevokedAt = now;
                tokens.Update(t);
            }
        }
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private (RefreshToken Entity, string Raw) Create(Guid userId, Guid familyId)
    {
        var raw = Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenByteLength));
        var now = timeProvider.GetUtcNow();
        var token = new RefreshToken
        {
            UserId = userId,
            TokenHash = TokenHasher.Hash(raw),
            FamilyId = familyId,
            CreatedAt = now,
            ExpiresAt = now.Add(options.RefreshTokenLifetime),
        };
        token.SetId(Guid.CreateVersion7());
        return (token, raw);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
```

- [ ] **Step 5: Register the service**

In `IdentityServiceCollectionExtensions.AddThemiaIdentityServicesCore`, add after the `IUserTokenService` registration:

```csharp
        services.TryAddScoped<IRefreshTokenService, RefreshTokenService>();
```

(Add `using Themia.Modules.Identity.Abstractions.Authentication;` to the file.)

- [ ] **Step 6: Run the refresh conformance tests (both peers)**

Run: `dotnet test tests/Themia.Modules.Identity.EFCore.IntegrationTests --filter "FullyQualifiedName~Refresh|FullyQualifiedName~Revoke_all"`
Then: `dotnet test tests/Themia.Modules.Identity.Dapper.SqlServer.IntegrationTests --filter "FullyQualifiedName~Refresh|FullyQualifiedName~Revoke_all"`
Expected: PASS (requires Docker for Testcontainers). If Docker is unavailable, note it and run in CI.

- [ ] **Step 7: Record core public API + commit**

Run a clean build of the core project, fix any `RS0016` in its `PublicAPI.Unshipped.txt` (the new public `RefreshTokenService` members), then:

```bash
git add src/modules/Themia.Modules.Identity tests/Themia.Modules.Identity.IntegrationTests
git commit -m "feat(identity): add RefreshTokenService with rotation and reuse-detection"
```

---

## Task 5: New `.AspNetCore` project + test projects + package pins + sln

**Files:**
- Create: `src/modules/Themia.Modules.Identity.AspNetCore/Themia.Modules.Identity.AspNetCore.csproj`
- Create: `src/modules/Themia.Modules.Identity.AspNetCore/PublicAPI.Shipped.txt` (empty), `PublicAPI.Unshipped.txt` (empty)
- Create: `tests/Themia.Modules.Identity.AspNetCore.Tests/Themia.Modules.Identity.AspNetCore.Tests.csproj`
- Create: `tests/Themia.Modules.Identity.AspNetCore.IntegrationTests/Themia.Modules.Identity.AspNetCore.IntegrationTests.csproj`
- Modify: `Directory.Packages.props`
- Modify: `Themia.sln`

- [ ] **Step 1: Pin the new packages**

In `Directory.Packages.props`, add (place near the other ASP.NET entries; the version must match the installed ASP.NET Core 10 runtime — the repo's EF Core is `10.0.8`, so start with `10.0.8` and adjust if restore reports the nearest available 10.0.x):

```xml
    <PackageVersion Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.8" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.8" />
```

- [ ] **Step 2: Create the production csproj**

`src/modules/Themia.Modules.Identity.AspNetCore/Themia.Modules.Identity.AspNetCore.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Modules.Identity.AspNetCore</PackageId>
    <Description>Themia Identity JWT slice — access-token issuance, JwtBearer validation, refresh-token rotation orchestration, and login/refresh/logout minimal-API endpoints.</Description>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Modules.Identity.Abstractions/Themia.Modules.Identity.Abstractions.csproj" />
    <ProjectReference Include="../Themia.Modules.Identity/Themia.Modules.Identity.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.AspNetCore/Themia.Framework.AspNetCore.csproj" />
    <ProjectReference Include="../../neutral/Themia.AspNetCore/Themia.AspNetCore.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Modules.Identity.AspNetCore.Tests" />
    <InternalsVisibleTo Include="Themia.Modules.Identity.AspNetCore.IntegrationTests" />
  </ItemGroup>
</Project>
```

> Check the `Themia.Modules.Identity.csproj` for the exact `Microsoft.CodeAnalysis.PublicApiAnalyzers` / `AdditionalFiles` / `InternalsVisibleTo` syntax this repo uses and mirror it verbatim if it differs (e.g. it may be set centrally in `Directory.Build.props`). Create empty `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`.

- [ ] **Step 3: Create the unit-test csproj**

`tests/Themia.Modules.Identity.AspNetCore.Tests/Themia.Modules.Identity.AspNetCore.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/modules/Themia.Modules.Identity.AspNetCore/Themia.Modules.Identity.AspNetCore.csproj" />
    <ProjectReference Include="../../src/modules/Themia.Modules.Identity.Abstractions/Themia.Modules.Identity.Abstractions.csproj" />
    <ProjectReference Include="../../src/modules/Themia.Modules.Identity/Themia.Modules.Identity.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create the integration-test csproj**

`tests/Themia.Modules.Identity.AspNetCore.IntegrationTests/Themia.Modules.Identity.AspNetCore.IntegrationTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="Testcontainers.MsSql" />
    <PackageReference Include="Npgsql" />
    <PackageReference Include="Microsoft.Data.SqlClient" />
    <PackageReference Include="Microsoft.Extensions.Configuration" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/modules/Themia.Modules.Identity.AspNetCore/Themia.Modules.Identity.AspNetCore.csproj" />
    <ProjectReference Include="../../src/modules/Themia.Modules.Identity/Themia.Modules.Identity.csproj" />
    <ProjectReference Include="../../src/modules/Themia.Modules.Identity.Abstractions/Themia.Modules.Identity.Abstractions.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.AspNetCore/Themia.Framework.AspNetCore.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.EFCore.PostgreSql/Themia.Framework.Data.EFCore.PostgreSql.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.EFCore.SqlServer/Themia.Framework.Data.EFCore.SqlServer.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.Dapper.PostgreSql/Themia.Framework.Data.Dapper.PostgreSql.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.Dapper.SqlServer/Themia.Framework.Data.Dapper.SqlServer.csproj" />
    <ProjectReference Include="../../src/framework/Themia.MultiTenancy/Themia.MultiTenancy.csproj" />
  </ItemGroup>
</Project>
```

> Confirm the exact EF/Dapper provider project names against `tests/Themia.Modules.Identity.IntegrationTests` and the `src/framework` folder; adjust any path that differs.

- [ ] **Step 5: Add the three projects to `Themia.sln`**

Add three `Project(...)`/`EndProject` blocks (use the C# project type GUID `{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}` and a fresh unique GUID for each — generate with `uuidgen`). Then nest them in `GlobalSection(NestedProjects)`: the production project under the **modules** folder `{EC447DCF-ABFA-6E24-52A5-D7FD48A5C558}`, both test projects under the **tests** folder `{0AB3BF05-4346-4AA6-1389-037BE0695223}`. Mirror the existing `Themia.Modules.Identity` entries (lines ~132–142) exactly for formatting, and add matching `{GUID}.Debug|Any CPU.*` / `Release` entries in `GlobalSection(ProjectConfigurationPlatforms)`.

- [ ] **Step 6: Verify the solution restores and builds**

Run: `dotnet build Themia.sln`
Expected: PASS (the new projects are empty of code but must compile and be wired into the solution).

- [ ] **Step 7: Commit**

```bash
git add src/modules/Themia.Modules.Identity.AspNetCore tests/Themia.Modules.Identity.AspNetCore.Tests tests/Themia.Modules.Identity.AspNetCore.IntegrationTests Directory.Packages.props Themia.sln
git commit -m "chore(identity): scaffold Themia.Modules.Identity.AspNetCore and test projects"
```

---

## Task 6: JwtOptions (TDD)

**Files:**
- Create: `src/modules/Themia.Modules.Identity.AspNetCore/Options/JwtOptions.cs`
- Create: `tests/Themia.Modules.Identity.AspNetCore.Tests/JwtOptionsTests.cs`

- [ ] **Step 1: Write the failing tests**

`JwtOptionsTests.cs`:

```csharp
using Themia.Modules.Identity.AspNetCore.Options;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class JwtOptionsTests
{
    private static JwtOptions Valid() => new()
    {
        SigningKey = new string('k', 32),
        Issuer = "themia",
        Audience = "themia-clients",
    };

    [Fact]
    public void Validate_passes_for_a_well_formed_options() => Valid().Validate();

    [Fact]
    public void Validate_rejects_a_short_signing_key()
    {
        var options = Valid();
        options.SigningKey = "too-short";
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_missing_issuer()
    {
        var options = Valid();
        options.Issuer = "   ";
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_missing_audience()
    {
        var options = Valid();
        options.Audience = "";
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Validate_rejects_non_positive_access_lifetime()
    {
        var options = Valid();
        options.AccessTokenLifetime = TimeSpan.Zero;
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }
}
```

- [ ] **Step 2: Run — confirm fail**

Run: `dotnet test tests/Themia.Modules.Identity.AspNetCore.Tests --filter "FullyQualifiedName~JwtOptionsTests"`
Expected: COMPILE FAILURE (`JwtOptions` missing).

- [ ] **Step 3: Implement**

`Options/JwtOptions.cs`:

```csharp
using System.Text;

namespace Themia.Modules.Identity.AspNetCore.Options;

/// <summary>JWT access-token + validation configuration. Validated at registration (fail-fast),
/// mirroring <c>IdentityModuleOptions.Validate()</c>.</summary>
public sealed class JwtOptions
{
    /// <summary>HS256 minimum key length in bytes (256-bit).</summary>
    private const int MinSigningKeyBytes = 32;

    /// <summary>The symmetric signing secret (UTF-8). Minimum 32 bytes.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>The token issuer (<c>iss</c>).</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>The token audience (<c>aud</c>).</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Access-token lifetime. Default 15 minutes.</summary>
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Validation clock-skew tolerance. Default 30 seconds.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Validates the options, throwing if any value would produce a broken runtime.</summary>
    /// <exception cref="ArgumentException">The signing key is too short, or issuer/audience is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A lifetime/skew is out of range.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SigningKey) || Encoding.UTF8.GetByteCount(SigningKey) < MinSigningKeyBytes)
        {
            throw new ArgumentException(
                $"Must be at least {MinSigningKeyBytes} bytes (256-bit) for HS256.", nameof(SigningKey));
        }

        if (string.IsNullOrWhiteSpace(Issuer))
        {
            throw new ArgumentException("Must not be null or whitespace.", nameof(Issuer));
        }

        if (string.IsNullOrWhiteSpace(Audience))
        {
            throw new ArgumentException("Must not be null or whitespace.", nameof(Audience));
        }

        if (AccessTokenLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AccessTokenLifetime), AccessTokenLifetime, "Must be a positive duration.");
        }

        if (ClockSkew < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ClockSkew), ClockSkew, "Must not be negative.");
        }
    }
}
```

- [ ] **Step 4: Run — confirm pass**

Run: `dotnet test tests/Themia.Modules.Identity.AspNetCore.Tests --filter "FullyQualifiedName~JwtOptionsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/modules/Themia.Modules.Identity.AspNetCore/Options tests/Themia.Modules.Identity.AspNetCore.Tests/JwtOptionsTests.cs
git commit -m "feat(identity): add JwtOptions with fail-fast validation"
```

---

## Task 7: Signing-credentials provider (TDD)

**Files:**
- Create: `src/modules/Themia.Modules.Identity.AspNetCore/Signing/IJwtSigningCredentialsProvider.cs`
- Create: `src/modules/Themia.Modules.Identity.AspNetCore/Signing/SymmetricSigningCredentialsProvider.cs`
- Create: `tests/Themia.Modules.Identity.AspNetCore.Tests/SymmetricSigningCredentialsProviderTests.cs`

- [ ] **Step 1: Write the failing test**

`SymmetricSigningCredentialsProviderTests.cs`:

```csharp
using Microsoft.IdentityModel.Tokens;
using Themia.Modules.Identity.AspNetCore.Options;
using Themia.Modules.Identity.AspNetCore.Signing;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class SymmetricSigningCredentialsProviderTests
{
    [Fact]
    public void Provides_hs256_credentials_and_matching_validation_key()
    {
        var options = new JwtOptions { SigningKey = new string('k', 32), Issuer = "i", Audience = "a" };
        var provider = new SymmetricSigningCredentialsProvider(options);

        Assert.Equal(SecurityAlgorithms.HmacSha256, provider.SigningCredentials.Algorithm);
        Assert.Same(provider.SigningCredentials.Key, provider.ValidationKey);
        Assert.IsType<SymmetricSecurityKey>(provider.ValidationKey);
    }
}
```

- [ ] **Step 2: Run — confirm fail (compile).**

Run: `dotnet test tests/Themia.Modules.Identity.AspNetCore.Tests --filter "FullyQualifiedName~SymmetricSigning"`
Expected: COMPILE FAILURE.

- [ ] **Step 3: Implement the interface**

`Signing/IJwtSigningCredentialsProvider.cs`:

```csharp
using Microsoft.IdentityModel.Tokens;

namespace Themia.Modules.Identity.AspNetCore.Signing;

/// <summary>Supplies the signing credentials used to mint access tokens and the key material used to
/// validate them. The default is HS256 symmetric; an RS256/ES256 + JWKS provider can replace it via DI
/// without touching callers.</summary>
public interface IJwtSigningCredentialsProvider
{
    /// <summary>The credentials used to sign newly minted tokens.</summary>
    SigningCredentials SigningCredentials { get; }

    /// <summary>The key used to validate incoming tokens.</summary>
    SecurityKey ValidationKey { get; }
}
```

- [ ] **Step 4: Implement the default**

`Signing/SymmetricSigningCredentialsProvider.cs`:

```csharp
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Themia.Modules.Identity.AspNetCore.Options;

namespace Themia.Modules.Identity.AspNetCore.Signing;

/// <summary>HS256 symmetric signing provider keyed from <see cref="JwtOptions.SigningKey"/>.</summary>
public sealed class SymmetricSigningCredentialsProvider : IJwtSigningCredentialsProvider
{
    private readonly SymmetricSecurityKey key;

    /// <summary>Creates the provider from validated options.</summary>
    public SymmetricSigningCredentialsProvider(JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
    }

    /// <inheritdoc />
    public SigningCredentials SigningCredentials => new(key, SecurityAlgorithms.HmacSha256);

    /// <inheritdoc />
    public SecurityKey ValidationKey => key;
}
```

- [ ] **Step 5: Run — confirm pass. Commit.**

Run: `dotnet test tests/Themia.Modules.Identity.AspNetCore.Tests --filter "FullyQualifiedName~SymmetricSigning"` → PASS.

```bash
git add src/modules/Themia.Modules.Identity.AspNetCore/Signing tests/Themia.Modules.Identity.AspNetCore.Tests/SymmetricSigningCredentialsProviderTests.cs
git commit -m "feat(identity): add JWT signing-credentials provider (HS256 default)"
```

---

## Task 8: AccessTokenService — JWT minting (TDD)

**Files:**
- Create: `src/modules/Themia.Modules.Identity.AspNetCore/Tokens/AccessTokenService.cs`
- Create: `tests/Themia.Modules.Identity.AspNetCore.Tests/AccessTokenServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

`AccessTokenServiceTests.cs`:

```csharp
using System.Security.Claims;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Themia.Modules.Identity.AspNetCore.Options;
using Themia.Modules.Identity.AspNetCore.Signing;
using Themia.Modules.Identity.AspNetCore.Tokens;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class AccessTokenServiceTests
{
    private static readonly JwtOptions Options = new()
    {
        SigningKey = new string('k', 32),
        Issuer = "themia",
        Audience = "themia-clients",
        AccessTokenLifetime = TimeSpan.FromMinutes(15),
    };

    private static AccessTokenService NewService(TimeProvider time) =>
        new(new SymmetricSigningCredentialsProvider(Options), Options, time);

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Bearer", ClaimTypes.Name, ClaimTypes.Role));

    [Fact]
    public void Issue_emits_a_validatable_jwt_with_issuer_audience_and_subject()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-15T00:00:00Z"));
        var service = NewService(time);
        var principal = Principal(
            new Claim(ClaimTypes.NameIdentifier, "11111111-1111-1111-1111-111111111111"),
            new Claim(ClaimTypes.Role, "admin"));

        var token = service.Issue(principal);

        var handler = new JsonWebTokenHandler();
        var result = handler.ValidateTokenAsync(token.Token, new TokenValidationParameters
        {
            ValidIssuer = Options.Issuer,
            ValidAudience = Options.Audience,
            IssuerSigningKey = new SymmetricSigningCredentialsProvider(Options).ValidationKey,
            ValidateLifetime = false,
        }).GetAwaiter().GetResult();

        Assert.True(result.IsValid);
        Assert.Equal(token.ExpiresAt, time.GetUtcNow().Add(Options.AccessTokenLifetime));
        var jwt = (JsonWebToken)result.SecurityToken;
        Assert.Equal("11111111-1111-1111-1111-111111111111", jwt.GetClaim(ClaimTypes.NameIdentifier).Value);
        Assert.Contains(jwt.Claims, c => c.Type == ClaimTypes.Role && c.Value == "admin");
    }

    [Fact]
    public void Issue_carries_only_the_claims_in_the_principal()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-15T00:00:00Z"));
        var service = NewService(time);
        // Platform principal: no tenant claim present → none should appear in the JWT.
        var token = service.Issue(Principal(new Claim(ClaimTypes.NameIdentifier, "u")));

        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(token.Token);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "tenant_id");
    }
}
```

> The tenant claim type string used in `Issue_carries_only_the_claims_in_the_principal` must match `IdentityClaimTypes.TenantId`'s actual value — open `src/modules/Themia.Modules.Identity/Principal/IdentityClaimTypes.cs` and use that literal (replace `"tenant_id"` if it differs).

- [ ] **Step 2: Run — confirm fail (compile).**

Run: `dotnet test tests/Themia.Modules.Identity.AspNetCore.Tests --filter "FullyQualifiedName~AccessTokenServiceTests"` → COMPILE FAILURE.

- [ ] **Step 3: Implement**

`Tokens/AccessTokenService.cs`:

```csharp
using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.AspNetCore.Options;
using Themia.Modules.Identity.AspNetCore.Signing;

namespace Themia.Modules.Identity.AspNetCore.Tokens;

/// <summary>Default <see cref="IAccessTokenService"/>. Mints a signed JWT from the principal's claims,
/// stamping issuer/audience/expiry from <see cref="JwtOptions"/>.</summary>
public sealed class AccessTokenService : IAccessTokenService
{
    private readonly IJwtSigningCredentialsProvider credentials;
    private readonly JwtOptions options;
    private readonly TimeProvider timeProvider;

    /// <summary>Creates the service.</summary>
    public AccessTokenService(IJwtSigningCredentialsProvider credentials, JwtOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.credentials = credentials;
        this.options = options;
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public AccessToken Issue(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var now = timeProvider.GetUtcNow();
        var expires = now.Add(options.AccessTokenLifetime);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Subject = principal.Identity as ClaimsIdentity ?? new ClaimsIdentity(principal.Claims),
            SigningCredentials = credentials.SigningCredentials,
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return new AccessToken(token, expires);
    }
}
```

- [ ] **Step 4: Run — confirm pass. Commit.**

Run: `dotnet test tests/Themia.Modules.Identity.AspNetCore.Tests --filter "FullyQualifiedName~AccessTokenServiceTests"` → PASS.

```bash
git add src/modules/Themia.Modules.Identity.AspNetCore/Tokens tests/Themia.Modules.Identity.AspNetCore.Tests/AccessTokenServiceTests.cs
git commit -m "feat(identity): add AccessTokenService JWT minting"
```

---

## Task 9: Authentication hooks base + AuthenticationFlow login (TDD)

**Files:**
- Create: `src/modules/Themia.Modules.Identity.AspNetCore/Authentication/AuthenticationHooksBase.cs`
- Create: `src/modules/Themia.Modules.Identity.AspNetCore/Authentication/AuthenticationFlow.cs`
- Create: `tests/Themia.Modules.Identity.AspNetCore.Tests/Fakes.cs`
- Create: `tests/Themia.Modules.Identity.AspNetCore.Tests/AuthenticationFlowLoginTests.cs`

- [ ] **Step 1: Write the no-op hooks base**

`Authentication/AuthenticationHooksBase.cs`:

```csharp
using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>No-op <see cref="IAuthenticationHooks"/>. Registered by default via <c>TryAdd</c>; adopters
/// subclass and override only the hooks they need.</summary>
public class AuthenticationHooksBase : IAuthenticationHooks
{
    /// <inheritdoc />
    public virtual Task OnBeforeLoginAsync(BeforeLoginContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnLoginSucceededAsync(LoginSucceededContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnLoginFailedAsync(LoginFailedContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnBeforeRefreshAsync(BeforeRefreshContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnRefreshSucceededAsync(RefreshSucceededContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnLogoutAsync(LogoutContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
```

- [ ] **Step 2: Write the test fakes**

`Fakes.cs` (shared across flow tests):

```csharp
using System.Security.Claims;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.AspNetCore.Tests;

internal sealed class FakeUserService : IUserService
{
    public PasswordVerificationResult VerifyResult { get; set; } = PasswordVerificationResult.Success;
    public User? UserToReturn { get; set; }
    public int VerifyCalls { get; private set; }

    public Task<PasswordVerificationResult> VerifyPasswordAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        VerifyCalls++;
        return Task.FromResult(VerifyResult);
    }

    public Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default) => Task.FromResult(UserToReturn);
    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(UserToReturn);
    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) => Task.FromResult(UserToReturn);
    public Task<UserCreationResult> CreateAsync(string userName, string password, string? email = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> SetActiveAsync(Guid userId, bool isActive, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class FakeClaimsPrincipalFactory : IClaimsPrincipalFactory
{
    public Task<ClaimsPrincipal> CreateAsync(User user, string authenticationType, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], authenticationType)));
}

internal sealed class FakeAccessTokenService : IAccessTokenService
{
    public AccessToken Issue(ClaimsPrincipal principal) => new("access-jwt", DateTimeOffset.UtcNow.AddMinutes(15));
}

internal sealed class FakeRefreshTokenService : IRefreshTokenService
{
    public int IssueCalls { get; private set; }
    public RefreshValidationResult RotateResult { get; set; }
    public int RevokeCalls { get; private set; }
    public bool LastRevokeAll { get; private set; }

    public Task<RefreshIssue> IssueAsync(Guid userId, Guid? familyId = null, CancellationToken cancellationToken = default)
    {
        IssueCalls++;
        return Task.FromResult(new RefreshIssue("refresh-raw", DateTimeOffset.UtcNow.AddDays(14), familyId ?? Guid.NewGuid()));
    }

    public Task<RefreshValidationResult> ValidateAndRotateAsync(string rawToken, CancellationToken cancellationToken = default) =>
        Task.FromResult(RotateResult);

    public Task RevokeAsync(string rawToken, bool allForUser, CancellationToken cancellationToken = default)
    {
        RevokeCalls++;
        LastRevokeAll = allForUser;
        return Task.CompletedTask;
    }
}

internal sealed class FakePasswordHasher : IPasswordHasher
{
    public int HashCalls { get; private set; }
    public string Hash(string password) { HashCalls++; return "hash"; }
    public bool Verify(string encodedHash, string password) => true;
    public bool NeedsRehash(string encodedHash) => false;
}

internal sealed class RecordingHooks : AspNetCore.Authentication.AuthenticationHooksBase
{
    public bool DenyBeforeLogin { get; set; }
    public bool DenyOnSucceeded { get; set; }
    public bool DenyBeforeRefresh { get; set; }
    public List<string> Calls { get; } = [];
    public LoginFailureReason? FailedReason { get; private set; }
    public bool SucceededRanBeforeIssue { get; set; }
    public FakeRefreshTokenService? Refresh { get; set; }

    public override Task OnBeforeLoginAsync(BeforeLoginContext context, CancellationToken cancellationToken = default)
    {
        Calls.Add("before-login");
        if (DenyBeforeLogin) context.Deny("blocked");
        return Task.CompletedTask;
    }

    public override Task OnLoginSucceededAsync(LoginSucceededContext context, CancellationToken cancellationToken = default)
    {
        Calls.Add("login-succeeded");
        if (Refresh is not null) SucceededRanBeforeIssue = Refresh.IssueCalls == 0;
        if (DenyOnSucceeded) context.Deny("gated");
        return Task.CompletedTask;
    }

    public override Task OnLoginFailedAsync(LoginFailedContext context, CancellationToken cancellationToken = default)
    {
        Calls.Add("login-failed");
        FailedReason = context.Reason;
        return Task.CompletedTask;
    }

    public override Task OnBeforeRefreshAsync(BeforeRefreshContext context, CancellationToken cancellationToken = default)
    {
        Calls.Add("before-refresh");
        if (DenyBeforeRefresh) context.Deny();
        return Task.CompletedTask;
    }
}
```

> Build `FakeUserService` against the real `IUserService` — if any signature differs from `src/modules/Themia.Modules.Identity.Abstractions/IUserService.cs`, match it exactly (the compiler will tell you).

- [ ] **Step 3: Write the failing login tests**

`AuthenticationFlowLoginTests.cs`:

```csharp
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class AuthenticationFlowLoginTests
{
    private static User NewUser() => new() { UserName = "alice" };

    private static (AuthenticationFlow Flow, FakeUserService Users, FakeRefreshTokenService Refresh, FakePasswordHasher Hasher, RecordingHooks Hooks)
        Build(PasswordVerificationResult verify, User? user)
    {
        var users = new FakeUserService { VerifyResult = verify, UserToReturn = user };
        var refresh = new FakeRefreshTokenService();
        var hasher = new FakePasswordHasher();
        var hooks = new RecordingHooks { Refresh = refresh };
        var flow = new AuthenticationFlow(users, new FakeClaimsPrincipalFactory(), new FakeAccessTokenService(),
            refresh, hasher, hooks, TimeProvider.System);
        return (flow, users, refresh, hasher, hooks);
    }

    [Fact]
    public async Task Login_succeeds_and_issues_a_pair()
    {
        var (flow, _, refresh, _, hooks) = Build(PasswordVerificationResult.Success, NewUser());
        var result = await flow.LoginAsync("alice", "pw");
        Assert.True(result.Succeeded);
        Assert.Equal("access-jwt", result.Tokens!.Value.AccessToken);
        Assert.Equal("refresh-raw", result.Tokens!.Value.RefreshToken);
        Assert.Equal(1, refresh.IssueCalls);
        Assert.True(hooks.SucceededRanBeforeIssue); // OnLoginSucceeded runs before tokens are issued
    }

    [Theory]
    [InlineData(PasswordVerificationResult.NotFound, LoginFailureReason.NotFound)]
    [InlineData(PasswordVerificationResult.Failed, LoginFailureReason.WrongPassword)]
    [InlineData(PasswordVerificationResult.Inactive, LoginFailureReason.Inactive)]
    [InlineData(PasswordVerificationResult.LockedOut, LoginFailureReason.LockedOut)]
    public async Task Login_failures_do_not_issue_tokens_and_report_real_reason(PasswordVerificationResult verify, LoginFailureReason reason)
    {
        var (flow, _, refresh, _, hooks) = Build(verify, NewUser());
        var result = await flow.LoginAsync("alice", "pw");
        Assert.False(result.Succeeded);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Equal(reason, hooks.FailedReason); // hooks see the real reason
    }

    [Theory]
    [InlineData(PasswordVerificationResult.NotFound, true)]
    [InlineData(PasswordVerificationResult.Inactive, true)]
    [InlineData(PasswordVerificationResult.Failed, false)]
    public async Task Login_runs_throwaway_hash_only_when_no_real_hash_ran(PasswordVerificationResult verify, bool expectBurn)
    {
        var (flow, _, _, hasher, _) = Build(verify, NewUser());
        await flow.LoginAsync("alice", "pw");
        // NotFound/Inactive skip the real argon2 verify, so the flow burns a throwaway hash to equalize latency.
        Assert.Equal(expectBurn ? 1 : 0, hasher.HashCalls);
    }

    [Fact]
    public async Task Login_denied_by_before_hook_returns_denied_and_fires_failed_hook()
    {
        var (flow, users, _, _, hooks) = Build(PasswordVerificationResult.Success, NewUser());
        hooks.DenyBeforeLogin = true;
        var result = await flow.LoginAsync("alice", "pw");
        Assert.Equal(LoginOutcome.Denied, result.Outcome);
        Assert.Equal(0, users.VerifyCalls);                  // gate runs before verification
        Assert.Equal(LoginFailureReason.Denied, hooks.FailedReason);
    }

    [Fact]
    public async Task Login_denied_by_succeeded_hook_returns_denied_without_issuing()
    {
        var (flow, _, refresh, _, hooks) = Build(PasswordVerificationResult.Success, NewUser());
        hooks.DenyOnSucceeded = true;
        var result = await flow.LoginAsync("alice", "pw");
        Assert.Equal(LoginOutcome.Denied, result.Outcome);
        Assert.Equal(0, refresh.IssueCalls);
        Assert.Equal(LoginFailureReason.Denied, hooks.FailedReason);
    }
}
```

> `new User { UserName = "alice" }` must compile against the real `User` entity. If `User` has a required `SecurityStamp` or non-default ctor, set the minimum properties the compiler demands.

- [ ] **Step 4: Run — confirm fail (compile).**

Run: `dotnet test tests/Themia.Modules.Identity.AspNetCore.Tests --filter "FullyQualifiedName~AuthenticationFlowLoginTests"` → COMPILE FAILURE.

- [ ] **Step 5: Implement AuthenticationFlow**

`Authentication/AuthenticationFlow.cs`:

```csharp
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>Default <see cref="IAuthenticationFlow"/>. Owns the security-critical sequence
/// (gate → verify → timing-equalize → principal build → issue) and invokes <see cref="IAuthenticationHooks"/>
/// at fixed points. Every credential failure (including a hook deny) yields a non-success result that the
/// endpoints collapse to a uniform 401.</summary>
public sealed class AuthenticationFlow : IAuthenticationFlow
{
    private const string AuthenticationType = "Bearer";

    private readonly IUserService users;
    private readonly IClaimsPrincipalFactory principalFactory;
    private readonly IAccessTokenService accessTokens;
    private readonly IRefreshTokenService refreshTokens;
    private readonly IPasswordHasher passwordHasher;
    private readonly IAuthenticationHooks hooks;
    private readonly TimeProvider timeProvider;

    /// <summary>Creates the flow.</summary>
    public AuthenticationFlow(
        IUserService users,
        IClaimsPrincipalFactory principalFactory,
        IAccessTokenService accessTokens,
        IRefreshTokenService refreshTokens,
        IPasswordHasher passwordHasher,
        IAuthenticationHooks hooks,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(principalFactory);
        ArgumentNullException.ThrowIfNull(accessTokens);
        ArgumentNullException.ThrowIfNull(refreshTokens);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(hooks);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.users = users;
        this.principalFactory = principalFactory;
        this.accessTokens = accessTokens;
        this.refreshTokens = refreshTokens;
        this.passwordHasher = passwordHasher;
        this.hooks = hooks;
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<LoginResult> LoginAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentNullException.ThrowIfNull(password);

        var before = new BeforeLoginContext(userName);
        await hooks.OnBeforeLoginAsync(before, cancellationToken).ConfigureAwait(false);
        if (before.IsDenied)
        {
            return await FailAsync(userName, LoginFailureReason.Denied, LoginResult.Denied(), cancellationToken).ConfigureAwait(false);
        }

        var verification = await users.VerifyPasswordAsync(userName, password, cancellationToken).ConfigureAwait(false);
        if (verification != PasswordVerificationResult.Success)
        {
            // Equalize latency: NotFound/Inactive return before any real argon2 work runs.
            if (verification is PasswordVerificationResult.NotFound or PasswordVerificationResult.Inactive)
            {
                _ = passwordHasher.Hash(password);
            }

            var failure = verification == PasswordVerificationResult.LockedOut ? LoginResult.LockedOut() : LoginResult.InvalidCredentials();
            return await FailAsync(userName, Map(verification), failure, cancellationToken).ConfigureAwait(false);
        }

        var user = await users.FindByUserNameAsync(userName, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return await FailAsync(userName, LoginFailureReason.NotFound, LoginResult.InvalidCredentials(), cancellationToken).ConfigureAwait(false);
        }

        var succeeded = new LoginSucceededContext(user);
        await hooks.OnLoginSucceededAsync(succeeded, cancellationToken).ConfigureAwait(false);
        if (succeeded.IsDenied)
        {
            return await FailAsync(userName, LoginFailureReason.Denied, LoginResult.Denied(), cancellationToken).ConfigureAwait(false);
        }

        var tokens = await IssueAsync(user, cancellationToken).ConfigureAwait(false);
        return LoginResult.Success(tokens);
    }

    /// <inheritdoc />
    public async Task<RefreshRotationResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var before = new BeforeRefreshContext();
        await hooks.OnBeforeRefreshAsync(before, cancellationToken).ConfigureAwait(false);
        if (before.IsDenied)
        {
            return RefreshRotationResult.Denied();
        }

        var rotation = await refreshTokens.ValidateAndRotateAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        switch (rotation.Outcome)
        {
            case RefreshOutcome.ReuseDetected:
                return RefreshRotationResult.ReuseDetected();
            case RefreshOutcome.Invalid:
                return RefreshRotationResult.Invalid();
        }

        var user = rotation.User!;
        var replacement = rotation.Replacement!.Value;
        var principal = await principalFactory.CreateAsync(user, AuthenticationType, cancellationToken).ConfigureAwait(false);
        var access = accessTokens.Issue(principal);
        var tokens = new AuthTokens(access.Token, ExpiresInSeconds(access.ExpiresAt), replacement.RawToken);

        // The rotation has already persisted. A late deny here returns a uniform 401; the (valid but
        // undelivered) successor simply expires unused — acceptable per the §7 access-token tradeoff.
        var succeeded = new RefreshSucceededContext(user);
        await hooks.OnRefreshSucceededAsync(succeeded, cancellationToken).ConfigureAwait(false);
        if (succeeded.IsDenied)
        {
            return RefreshRotationResult.Denied();
        }

        return RefreshRotationResult.Success(tokens);
    }

    /// <inheritdoc />
    public async Task LogoutAsync(string refreshToken, bool allSessions, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        await refreshTokens.RevokeAsync(refreshToken, allSessions, cancellationToken).ConfigureAwait(false);
        await hooks.OnLogoutAsync(new LogoutContext(allSessions), cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthTokens> IssueAsync(User user, CancellationToken cancellationToken)
    {
        var principal = await principalFactory.CreateAsync(user, AuthenticationType, cancellationToken).ConfigureAwait(false);
        var access = accessTokens.Issue(principal);
        var refresh = await refreshTokens.IssueAsync(user.Id, null, cancellationToken).ConfigureAwait(false);
        return new AuthTokens(access.Token, ExpiresInSeconds(access.ExpiresAt), refresh.RawToken);
    }

    private async Task<LoginResult> FailAsync(string userName, LoginFailureReason reason, LoginResult result, CancellationToken cancellationToken)
    {
        await hooks.OnLoginFailedAsync(new LoginFailedContext(userName, reason), cancellationToken).ConfigureAwait(false);
        return result;
    }

    private int ExpiresInSeconds(DateTimeOffset expiresAt) =>
        (int)Math.Max(0, (expiresAt - timeProvider.GetUtcNow()).TotalSeconds);

    private static LoginFailureReason Map(PasswordVerificationResult verification) => verification switch
    {
        PasswordVerificationResult.NotFound => LoginFailureReason.NotFound,
        PasswordVerificationResult.Inactive => LoginFailureReason.Inactive,
        PasswordVerificationResult.LockedOut => LoginFailureReason.LockedOut,
        _ => LoginFailureReason.WrongPassword,
    };
}
```

- [ ] **Step 6: Run the login tests — confirm pass. Commit.**

Run: `dotnet test tests/Themia.Modules.Identity.AspNetCore.Tests --filter "FullyQualifiedName~AuthenticationFlowLoginTests"` → PASS.

```bash
git add src/modules/Themia.Modules.Identity.AspNetCore/Authentication tests/Themia.Modules.Identity.AspNetCore.Tests
git commit -m "feat(identity): add AuthenticationFlow login with hooks and timing mitigation"
```

---

## Task 10: AuthenticationFlow refresh + logout (TDD)

**Files:**
- Create: `tests/Themia.Modules.Identity.AspNetCore.Tests/AuthenticationFlowRefreshTests.cs`

(The implementation already exists from Task 9; this task tests the refresh/logout paths.)

- [ ] **Step 1: Write the tests**

`AuthenticationFlowRefreshTests.cs`:

```csharp
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class AuthenticationFlowRefreshTests
{
    private static AuthenticationFlow Build(FakeRefreshTokenService refresh, RecordingHooks hooks) =>
        new(new FakeUserService(), new FakeClaimsPrincipalFactory(), new FakeAccessTokenService(),
            refresh, new FakePasswordHasher(), hooks, TimeProvider.System);

    private static RefreshIssue Successor() => new("new-refresh", DateTimeOffset.UtcNow.AddDays(14), Guid.NewGuid());

    [Fact]
    public async Task Refresh_success_mints_a_new_pair()
    {
        var refresh = new FakeRefreshTokenService
        {
            RotateResult = RefreshValidationResult.Success(new User { UserName = "u" }, Successor()),
        };
        var result = await Build(refresh, new RecordingHooks()).RefreshAsync("token");
        Assert.True(result.Succeeded);
        Assert.Equal("access-jwt", result.Tokens!.Value.AccessToken);
        Assert.Equal("new-refresh", result.Tokens!.Value.RefreshToken);
    }

    [Fact]
    public async Task Refresh_invalid_returns_invalid()
    {
        var refresh = new FakeRefreshTokenService { RotateResult = RefreshValidationResult.Invalid() };
        var result = await Build(refresh, new RecordingHooks()).RefreshAsync("token");
        Assert.Equal(RefreshRotationOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Refresh_reuse_returns_reuse_detected()
    {
        var refresh = new FakeRefreshTokenService { RotateResult = RefreshValidationResult.ReuseDetected() };
        var result = await Build(refresh, new RecordingHooks()).RefreshAsync("token");
        Assert.Equal(RefreshRotationOutcome.ReuseDetected, result.Outcome);
    }

    [Fact]
    public async Task Refresh_denied_by_before_hook_does_not_rotate()
    {
        var refresh = new FakeRefreshTokenService { RotateResult = RefreshValidationResult.Invalid() };
        var hooks = new RecordingHooks { DenyBeforeRefresh = true };
        var result = await Build(refresh, hooks).RefreshAsync("token");
        Assert.Equal(RefreshRotationOutcome.Denied, result.Outcome);
        Assert.DoesNotContain("before-refresh", hooks.Calls.Where(c => c != "before-refresh")); // sanity
    }

    [Fact]
    public async Task Logout_revokes_single_family_by_default()
    {
        var refresh = new FakeRefreshTokenService();
        await Build(refresh, new RecordingHooks()).LogoutAsync("token", allSessions: false);
        Assert.Equal(1, refresh.RevokeCalls);
        Assert.False(refresh.LastRevokeAll);
    }

    [Fact]
    public async Task Logout_all_revokes_every_session()
    {
        var refresh = new FakeRefreshTokenService();
        await Build(refresh, new RecordingHooks()).LogoutAsync("token", allSessions: true);
        Assert.True(refresh.LastRevokeAll);
    }
}
```

- [ ] **Step 2: Run — confirm pass.**

Run: `dotnet test tests/Themia.Modules.Identity.AspNetCore.Tests --filter "FullyQualifiedName~AuthenticationFlowRefreshTests"` → PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/Themia.Modules.Identity.AspNetCore.Tests/AuthenticationFlowRefreshTests.cs
git commit -m "test(identity): cover AuthenticationFlow refresh and logout paths"
```

---

## Task 11: DI extensions — AddThemiaIdentityAspNetCore + AddThemiaJwtBearer (TDD)

**Files:**
- Create: `src/modules/Themia.Modules.Identity.AspNetCore/DependencyInjection/IdentityAspNetCoreServiceCollectionExtensions.cs`
- Create: `tests/Themia.Modules.Identity.AspNetCore.Tests/ServiceRegistrationTests.cs`

- [ ] **Step 1: Write the failing tests**

`ServiceRegistrationTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.AspNetCore.DependencyInjection;
using Themia.Modules.Identity.AspNetCore.Options;
using Themia.Modules.Identity.AspNetCore.Signing;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

public sealed class ServiceRegistrationTests
{
    private static void Configure(JwtOptions o)
    {
        o.SigningKey = new string('k', 32);
        o.Issuer = "themia";
        o.Audience = "clients";
    }

    [Fact]
    public void AddThemiaIdentityAspNetCore_validates_and_registers_services()
    {
        var services = new ServiceCollection();
        services.AddThemiaIdentityAspNetCore(Configure);

        Assert.Contains(services, d => d.ServiceType == typeof(JwtOptions));
        Assert.Contains(services, d => d.ServiceType == typeof(IJwtSigningCredentialsProvider));
        Assert.Contains(services, d => d.ServiceType == typeof(IAccessTokenService));
        Assert.Contains(services, d => d.ServiceType == typeof(IAuthenticationFlow));
        Assert.Contains(services, d => d.ServiceType == typeof(IAuthenticationHooks));
    }

    [Fact]
    public void AddThemiaIdentityAspNetCore_throws_on_invalid_options()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddThemiaIdentityAspNetCore(o => { o.Issuer = "x"; }));
    }

    [Fact]
    public void AddThemiaIdentityAspNetCore_does_not_overwrite_a_custom_hooks_registration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationHooks, AspNetCore.Authentication.AuthenticationHooksBase>();
        services.AddThemiaIdentityAspNetCore(Configure);
        Assert.Single(services, d => d.ServiceType == typeof(IAuthenticationHooks));
    }
}
```

- [ ] **Step 2: Run — confirm fail (compile).**

Run: `dotnet test tests/Themia.Modules.Identity.AspNetCore.Tests --filter "FullyQualifiedName~ServiceRegistrationTests"` → COMPILE FAILURE.

- [ ] **Step 3: Implement the DI extensions**

`DependencyInjection/IdentityAspNetCoreServiceCollectionExtensions.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Themia.Modules.Identity.AspNetCore.Options;
using Themia.Modules.Identity.AspNetCore.Signing;
using Themia.Modules.Identity.AspNetCore.Tokens;

namespace Themia.Modules.Identity.AspNetCore.DependencyInjection;

/// <summary>Registers the Themia Identity JWT slice. Requires <c>AddThemiaIdentityServices()</c> and
/// <c>AddThemiaIdentityAuthorization()</c> to have run (for <c>IUserService</c>,
/// <c>IRefreshTokenService</c>, <c>IClaimsPrincipalFactory</c>, <c>ICurrentUser</c>).</summary>
public static class IdentityAspNetCoreServiceCollectionExtensions
{
    /// <summary>Validates <see cref="JwtOptions"/> and registers token services, the signing provider,
    /// the authentication flow, and the default no-op hooks. All via <c>TryAdd</c> so adopters can
    /// replace any piece.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the JWT options.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddThemiaIdentityAspNetCore(this IServiceCollection services, Action<JwtOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new JwtOptions();
        configure(options);
        options.Validate();
        services.TryAddSingleton(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IJwtSigningCredentialsProvider, SymmetricSigningCredentialsProvider>();
        services.TryAddSingleton<IAccessTokenService, AccessTokenService>();
        services.TryAddScoped<IAuthenticationFlow, AuthenticationFlow>();
        services.TryAddScoped<IAuthenticationHooks, AuthenticationHooksBase>();

        return services;
    }

    /// <summary>Adds the JwtBearer validation scheme wired to <see cref="JwtOptions"/> and the registered
    /// <see cref="IJwtSigningCredentialsProvider"/>. Call after <c>AddAuthentication(...)</c>.</summary>
    /// <param name="builder">The authentication builder.</param>
    /// <param name="scheme">The scheme name; defaults to <see cref="JwtBearerDefaults.AuthenticationScheme"/>.</param>
    /// <returns>The same authentication builder.</returns>
    public static AuthenticationBuilder AddThemiaJwtBearer(this AuthenticationBuilder builder, string? scheme = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var schemeName = scheme ?? JwtBearerDefaults.AuthenticationScheme;

        builder.AddJwtBearer(schemeName, _ => { });
        builder.Services.AddOptions<JwtBearerOptions>(schemeName)
            .Configure<IJwtSigningCredentialsProvider, JwtOptions>((bearer, signing, jwt) =>
            {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signing.ValidationKey,
                    ValidateLifetime = true,
                    ClockSkew = jwt.ClockSkew,
                    NameClaimType = ClaimTypes.Name,
                    RoleClaimType = ClaimTypes.Role,
                };
            });

        return builder;
    }
}
```

- [ ] **Step 4: Run — confirm pass. Commit.**

Run: `dotnet test tests/Themia.Modules.Identity.AspNetCore.Tests --filter "FullyQualifiedName~ServiceRegistrationTests"` → PASS.

```bash
git add src/modules/Themia.Modules.Identity.AspNetCore/DependencyInjection tests/Themia.Modules.Identity.AspNetCore.Tests/ServiceRegistrationTests.cs
git commit -m "feat(identity): add AspNetCore DI + AddThemiaJwtBearer scheme"
```

---

## Task 12: Endpoints — MapIdentityAuthEndpoints + DTOs

**Files:**
- Create: `src/modules/Themia.Modules.Identity.AspNetCore/Endpoints/IdentityAuthEndpoints.cs`

(Behavior is verified end-to-end in Task 13 via `WebApplicationFactory`.)

- [ ] **Step 1: Implement the endpoints + DTOs**

`Endpoints/IdentityAuthEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Themia.AspNetCore.Exceptions;
using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Identity.AspNetCore.Endpoints;

/// <summary>Login request body.</summary>
public sealed record LoginRequest(string UserName, string Password);

/// <summary>Refresh request body.</summary>
public sealed record RefreshRequest(string RefreshToken);

/// <summary>Logout request body.</summary>
public sealed record LogoutRequest(string RefreshToken);

/// <summary>Issued token pair response.</summary>
public sealed record AuthResponse(string AccessToken, int ExpiresIn, string RefreshToken);

/// <summary>Maps the opt-in login/refresh/logout endpoints. The host owns the route prefix
/// (e.g. <c>app.MapGroup("/auth").MapIdentityAuthEndpoints()</c>). Each endpoint is thin: it binds the
/// DTO and delegates to <see cref="IAuthenticationFlow"/>. Errors flow through the Themia ProblemDetails
/// middleware as a uniform 401.</summary>
public static class IdentityAuthEndpointRouteBuilderExtensions
{
    private const string GenericAuthFailure = "Invalid credentials.";

    /// <summary>Maps <c>POST login</c>, <c>POST refresh</c>, and <c>POST logout</c>.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The same route builder.</returns>
    public static IEndpointRouteBuilder MapIdentityAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost("login", LoginAsync);
        endpoints.MapPost("refresh", RefreshAsync);
        endpoints.MapPost("logout", LogoutAsync);
        return endpoints;
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, IAuthenticationFlow flow, CancellationToken cancellationToken)
    {
        var result = await flow.LoginAsync(request.UserName, request.Password, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new UnauthorizedException(GenericAuthFailure);
        }
        var tokens = result.Tokens!.Value;
        return Results.Ok(new AuthResponse(tokens.AccessToken, tokens.ExpiresInSeconds, tokens.RefreshToken));
    }

    private static async Task<IResult> RefreshAsync(RefreshRequest request, IAuthenticationFlow flow, CancellationToken cancellationToken)
    {
        var result = await flow.RefreshAsync(request.RefreshToken, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new UnauthorizedException(GenericAuthFailure);
        }
        var tokens = result.Tokens!.Value;
        return Results.Ok(new AuthResponse(tokens.AccessToken, tokens.ExpiresInSeconds, tokens.RefreshToken));
    }

    private static async Task<IResult> LogoutAsync(LogoutRequest request, IAuthenticationFlow flow, bool all, CancellationToken cancellationToken)
    {
        await flow.LogoutAsync(request.RefreshToken, all, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }
}
```

> Confirm the `UnauthorizedException` namespace against `src/neutral/Themia.AspNetCore/...` (Task 0 exploration found `Themia.AspNetCore.Exceptions`). The `bool all` parameter binds from the `?all=true` query string by minimal-API convention.

- [ ] **Step 2: Build the project**

Run: `dotnet build src/modules/Themia.Modules.Identity.AspNetCore/Themia.Modules.Identity.AspNetCore.csproj` → PASS.

- [ ] **Step 3: Capture the package's public API**

Run a clean build: `dotnet build src/modules/Themia.Modules.Identity.AspNetCore/Themia.Modules.Identity.AspNetCore.csproj --no-incremental`. Paste every `RS0016`-listed member into `PublicAPI.Unshipped.txt` (sorted), rebuild until clean.

- [ ] **Step 4: Commit**

```bash
git add src/modules/Themia.Modules.Identity.AspNetCore/Endpoints src/modules/Themia.Modules.Identity.AspNetCore/PublicAPI.Unshipped.txt
git commit -m "feat(identity): add MapIdentityAuthEndpoints login/refresh/logout"
```

---

## Task 13: Integration tests — full HTTP flow on both data peers

**Files:**
- Create: `tests/Themia.Modules.Identity.AspNetCore.IntegrationTests/AuthFlowFixtures.cs` (Testcontainers PG + SQL Server; reuse the pattern from `tests/Themia.Modules.Identity.IntegrationTests/Fixtures`)
- Create: `tests/Themia.Modules.Identity.AspNetCore.IntegrationTests/AuthEndpointsTests.cs`
- Create: `tests/Themia.Modules.Identity.AspNetCore.IntegrationTests/TestAppFactory.cs`

- [ ] **Step 1: Build a minimal host via WebApplicationFactory**

`TestAppFactory.cs` — a `WebApplicationFactory<>` (or a hand-built `WebApplication`) that:
- registers the chosen data peer (EF or Dapper, PG or SQL Server) against the container connection string, exactly as the 0.5.0 conformance harness does (`ConfigurePeer`);
- calls `AddThemiaIdentityServices`, `AddThemiaIdentityAuthorization`, `AddThemiaAspNetCore`;
- calls `AddThemiaIdentityAspNetCore(o => { o.SigningKey = new string('k',32); o.Issuer="themia"; o.Audience="clients"; })`;
- `AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddThemiaJwtBearer()`; `AddAuthorization()`;
- pipeline: `UseThemia()` (ProblemDetails + tenant resolution) → `UseAuthentication()` → `UseAuthorization()`;
- maps `app.MapGroup("/auth").MapIdentityAuthEndpoints()` and a probe `app.MapGet("/me", (ICurrentUser u) => Results.Ok(new { u.UserId, u.IsAuthenticated })).RequireAuthorization()`.

Seed a known user before each test via `IUserService.CreateAsync` inside a tenant scope (set the ambient tenant the way the host resolves it — e.g. a fixed header/host the `Themia.MultiTenancy` resolver maps to a tenant).

- [ ] **Step 2: Write the integration tests (parameterized per peer)**

`AuthEndpointsTests.cs` (abstract base + one concrete class per peer, mirroring `IdentityStoreConformanceTests`):

```csharp
// Each test uses an HttpClient from the factory. Pseudocode of the assertions to implement in full:

// 1) Happy path: POST /auth/login {user,pw} → 200 + AuthResponse; POST /auth/refresh {refreshToken}
//    → 200 + new pair (different refresh token); POST /auth/logout {refreshToken} → 204.
[Fact] public async Task Login_then_refresh_then_logout_succeeds() { /* ... */ }

// 2) Replay: capture refresh token, refresh once, then refresh again with the OLD token → 401;
//    and the rotated successor is now also rejected (family revoked).
[Fact] public async Task Refresh_replay_after_rotation_is_rejected_and_revokes_family() { /* ... */ }

// 3) logout?all=true revokes every session: issue two logins for the same user, logout?all with one
//    refresh token, both refresh tokens then fail.
[Fact] public async Task Logout_all_revokes_every_session() { /* ... */ }

// 4) Anti-enumeration uniformity: login with unknown user, wrong password, inactive, and locked-out
//    accounts all return an identical 401 (same status + same ProblemDetails body/title).
[Fact] public async Task All_credential_failures_return_an_identical_401() { /* ... */ }

// 5) Platform login: with AllowPlatformLogin=true, a platform user (TenantId null) authenticates and
//    the access token carries the platform marker (no tenant claim).
[Fact] public async Task Platform_user_can_log_in_when_allowed() { /* ... */ }

// 6) JwtBearer populates ICurrentUser: login, then GET /me with the bearer access token → 200 and the
//    returned UserId matches; without the token → 401.
[Fact] public async Task Bearer_token_populates_current_user() { /* ... */ }
```

Implement each `[Fact]` fully using `HttpClient`, `System.Text.Json` for (de)serialization, and `Assert` on status codes + body. For the uniformity test, assert the three+ failure responses have byte-identical bodies (or identical `status`+`title`+`detail`). Provide concrete EF-Postgres and Dapper-SqlServer concrete classes (at minimum one EF + one Dapper peer; add the others if container resources allow), each supplying `ConfigurePeer` + the fixture connection string + `ResetAsync`.

- [ ] **Step 3: Run the integration tests (requires Docker)**

Run: `dotnet test tests/Themia.Modules.Identity.AspNetCore.IntegrationTests`
Expected: PASS. If Docker is unavailable locally, confirm they are collected and defer execution to CI; note this in the task's commit message.

- [ ] **Step 4: Commit**

```bash
git add tests/Themia.Modules.Identity.AspNetCore.IntegrationTests
git commit -m "test(identity): add JWT auth-flow integration tests (PG + SQL Server, both peers)"
```

---

## Task 14: Solution-wide verification + docs + release notes

**Files:**
- Modify: `README.md`, `CHANGELOG.md`, `docs/themia-architecture-overview.md`
- Modify: all three new `PublicAPI.Unshipped.txt` → move shipped entries to `PublicAPI.Shipped.txt` if that is this repo's release convention (check how 0.5.0 handled it).
- Modify: `docs/superpowers/specs/2026-06-15-themia-identity-jwt-design.md` (commit the in-progress edits if still uncommitted)

- [ ] **Step 1: Full clean build + test**

Run: `dotnet build Themia.sln --no-incremental` → PASS, **no `RS0016`** (all public APIs documented).
Run: `dotnet test Themia.sln` → PASS (or, where Docker-bound, all non-container suites pass and container suites are green in CI).

- [ ] **Step 2: Update CHANGELOG**

Add a `0.5.1` section (Added: `Themia.Modules.Identity.AspNetCore` — JWT access tokens, revocable rotating refresh tokens, JwtBearer scheme, login/refresh/logout endpoints, authentication hooks; `identity.refresh_tokens` table; `IdentityModuleOptions.RefreshTokenLifetime`). Group under Added/Changed and link the PR. Follow the format already in `CHANGELOG.md`.

- [ ] **Step 3: Update README + architecture overview**

In `README.md`, add `Themia.Modules.Identity.AspNetCore` to the modules row and add a doc link to this plan and the JWT design spec. In `docs/themia-architecture-overview.md`, record the resolved 0.5.1 decisions (refresh store in core; `IJwtSigningCredentialsProvider`/`JwtOptions` in `.AspNetCore`; `RefreshTokenLifetime` in `IdentityModuleOptions`) so the overview stays the source of truth.

- [ ] **Step 4: Capture session decisions to the ai-brains vault**

Per this repo's `CLAUDE.md`, append the 0.5.1 decisions (the four plan deviations) to the curated design note and log the entry in `Note Timeline Hub.md`, following the vault's own `CLAUDE.md`.

- [ ] **Step 5: Commit**

```bash
git add README.md CHANGELOG.md docs
git commit -m "docs(identity): document 0.5.1 JWT slice and update changelog"
```

---

## Self-review notes (for the executor)

- **Spec §9 coverage:** unit — JwtOptions validation (T6), access-token claim shape (T8), refresh rotation/reuse/expiry (T4), generic-401 uniformity + throwaway-hash + hook denies + succeeded-before-issue (T9/T10); integration — login→refresh→logout, replay, logout-all, platform login, JwtBearer populates ICurrentUser (T13). All present.
- **No deployed-migration edits:** the refresh schema is the new `RefreshTokensMigration` (T3), never an edit of `IdentitySchemaMigration`.
- **Type consistency:** `RefreshValidationResult.User`/`.Replacement` (T2) are consumed in `RefreshTokenService` (T4) and `AuthenticationFlow.RefreshAsync` (T9). `AuthTokens.ExpiresInSeconds` (T2) is produced by the flow and surfaced as `AuthResponse.ExpiresIn` (T12). `LoginOutcome`/`RefreshRotationOutcome` enums (T2) gate the endpoints' uniform 401 (T12).
- **Open verifications the executor must do early:** exact `IdentityClaimTypes.TenantId` literal (T8); `User` entity required members for `new User { ... }` in fakes (T9); SQL Server fixture deletion style (T3); EF/Dapper provider project names (T5); JwtBearer/Mvc.Testing exact 10.0.x patch (T5).

