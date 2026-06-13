# Themia.Modules.Identity — Core slice (0.5.0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the tenant-aware Identity core — user/role/claim store, argon2id password hashing, account-lifecycle tokens + lockout, the current-user principal, and ASP.NET Core authorization integration — running on either data peer (EF Core or Dapper) over a FluentMigrator schema (PostgreSQL + SQL Server).

**Architecture:** Two net10 packages. `Themia.Modules.Identity.Abstractions` holds entities, service interfaces, `ICurrentUser`, `IPasswordHasher`, options, and result/enum types — no EF/Dapper dependency. `Themia.Modules.Identity` holds the service implementations over `Themia.Framework.Data.Abstractions` (`IRepository<T>`/`ISpecification<T>`/`IUnitOfWork`/`IDataFilterScope`), the argon2id hasher, the FluentMigrator schema, the EF `IEntityTypeConfiguration<>` set + `ApplyThemiaIdentity()` extension, the Dapper `EntityMapping` registration, the `IdentityModule`, and the DI + authorization wiring. Platform users are the framework's global records (`tenant_id IS NULL`); per-tenant + platform uniqueness is enforced with two filtered unique indexes per table.

**Tech Stack:** .NET 10, `Themia.Framework.*` (Core, Data.Abstractions, Data.EFCore, Data.Dapper), `Themia.Data.Migrations` (FluentMigrator 6.2.0), `Konscious.Security.Cryptography.Argon2`, ASP.NET Core authorization, xUnit + Testcontainers (PostgreSQL + SQL Server).

**Spec:** [`docs/superpowers/specs/2026-06-14-themia-identity-core-design.md`](../specs/2026-06-14-themia-identity-core-design.md). Read it before starting.

**Branch:** `feat/themia-identity-core` (already created; the spec is committed there).

---

## Conventions every task follows

- **`System.Text.Json` only** (never Newtonsoft); **`ILogger<T>` only** (no `Console.*`).
- `TreatWarningsAsErrors=true` and `GenerateDocumentationFile=true` are on — XML doc comments are required on every public member or the build fails (CS1591). A clean build reports undocumented public members as `RS0016` against the PublicAPI files.
- After adding/changing public surface, run `dotnet build Themia.sln --no-incremental` and add the reported members to the package's `PublicAPI.Unshipped.txt` (the analyzer prints the exact line to add).
- Tenant column is `tenant_id` (`varchar(100)`/`nvarchar(100)`), nullable; `NULL` ⇒ platform. The framework's `TenantId` is a validated string record struct, **not** a Guid.
- Run a single test class with `dotnet test Themia.sln --filter <ClassName>`; a single test with `--filter "FullyQualifiedName~<Name>"`.
- Commit after each task with a `feat:`/`test:` message scoped to that task.

---

## File structure (what each file owns)

**`src/modules/Themia.Modules.Identity.Abstractions/`**
- `Entities/User.cs`, `Role.cs`, `UserRole.cs`, `UserClaim.cs`, `RoleClaim.cs`, `UserToken.cs` — entity POCOs.
- `Entities/TokenPurpose.cs` — `EmailConfirm | PhoneConfirm | PasswordReset | TwoFactor`.
- `PasswordVerificationResult.cs` — `Success | Failed | LockedOut | Inactive | NotFound`.
- `IPasswordHasher.cs`, `IUserService.cs`, `IRoleService.cs`, `IClaimService.cs`, `IUserTokenService.cs`.
- `IClaimsPrincipalFactory.cs`, `ICurrentUser.cs`.
- `IdentityModuleOptions.cs`.
- `Results.cs` — `UserCreationResult`, `TokenConsumeResult` (small result records).

**`src/modules/Themia.Modules.Identity/`**
- `Hashing/Argon2idPasswordHasher.cs`, `Hashing/TokenHasher.cs`.
- `Services/UserService.cs`, `RoleService.cs`, `ClaimService.cs`, `UserTokenService.cs`.
- `Specifications/IdentitySpecs.cs` — small `Specification<T>` subclasses for lookups.
- `Principal/CurrentUser.cs`, `Principal/ClaimsPrincipalFactory.cs`, `Principal/IdentityClaimTypes.cs`, `Principal/HttpContextCurrentUserAccessor.cs`, `Principal/IdentityCurrentUserAccessor.cs`.
- `Migrations/IdentitySchemaMigration.cs`.
- `EntityConfiguration/IdentityModelConfiguration.cs` — `IEntityTypeConfiguration<>` set + `ModelBuilderExtensions.ApplyThemiaIdentity`.
- `Mapping/IdentityDapperMappings.cs` — registers `EntityMapping`s.
- `IdentityModule.cs`, `DependencyInjection/IdentityServiceCollectionExtensions.cs`.

**`tests/`**
- `Themia.Modules.Identity.Tests` — unit tests (hasher, token hasher, services with in-memory fake repos, principal factory).
- `Themia.Modules.Identity.Conformance` — abstract `IdentityStoreConformanceTests` base + shared fixtures contract.
- `Themia.Modules.Identity.IntegrationTests` — EF×PG, EF×SqlServer, Dapper×PG, Dapper×SqlServer conformance subclasses + migration apply test.

---

## Task 1: Scaffold the two packages, wire the solution, add the Argon2 package

**Files:**
- Create: `src/modules/Themia.Modules.Identity.Abstractions/Themia.Modules.Identity.Abstractions.csproj`
- Create: `src/modules/Themia.Modules.Identity/Themia.Modules.Identity.csproj`
- Create: `src/modules/Themia.Modules.Identity.Abstractions/PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- Create: `src/modules/Themia.Modules.Identity/PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- Modify: `Directory.Packages.props` (add `Konscious.Security.Cryptography.Argon2`)
- Modify: `Themia.sln`

- [ ] **Step 1: Add the Argon2 package version**

In `Directory.Packages.props`, add inside the `<ItemGroup>` of `<PackageVersion>` entries:

```xml
<PackageVersion Include="Konscious.Security.Cryptography.Argon2" Version="1.3.1" />
```

- [ ] **Step 2: Create the Abstractions csproj**

`src/modules/Themia.Modules.Identity.Abstractions/Themia.Modules.Identity.Abstractions.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Modules.Identity.Abstractions</PackageId>
    <Description>Tenant-aware identity contracts — user/role/claim entities, service interfaces, the current-user principal, and the password-hasher abstraction. No EF/Dapper dependency.</Description>
    <PackageTags>themia;identity;abstractions;authentication;authorization;multi-tenancy</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../framework/Themia.Framework.Core/Themia.Framework.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Modules.Identity.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create the implementation csproj**

`src/modules/Themia.Modules.Identity/Themia.Modules.Identity.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Modules.Identity</PackageId>
    <Description>Tenant-aware Identity core — user/role/claim store over the Themia data abstractions (EF or Dapper), argon2id hashing, account-lifecycle tokens + lockout, current-user principal, and ASP.NET Core authorization integration. FluentMigrator schema (PostgreSQL + SQL Server).</Description>
    <PackageTags>themia;identity;authentication;authorization;multi-tenancy;efcore;dapper</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="Themia.Modules.Identity.Abstractions/Themia.Modules.Identity.Abstractions.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Core/Themia.Framework.Core.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Data.Abstractions/Themia.Framework.Data.Abstractions.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj" />
    <ProjectReference Include="../../framework/Themia.Framework.Data.Dapper/Themia.Framework.Data.Dapper.csproj" />
    <ProjectReference Include="../../neutral/Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
  </ItemGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Relational" />
    <PackageReference Include="FluentMigrator" />
    <PackageReference Include="Konscious.Security.Cryptography.Argon2" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Modules.Identity.Tests" />
    <InternalsVisibleTo Include="Themia.Modules.Identity.IntegrationTests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create the four PublicAPI files**

Each of the four files (`PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` in both projects) contains exactly one line:

```
#nullable enable
```

- [ ] **Step 5: Add both projects to the solution**

```bash
cd /Users/sarawut/GitHub/Idevs/single-repo/Packages/themia
dotnet sln Themia.sln add src/modules/Themia.Modules.Identity.Abstractions/Themia.Modules.Identity.Abstractions.csproj
dotnet sln Themia.sln add src/modules/Themia.Modules.Identity/Themia.Modules.Identity.csproj
```

- [ ] **Step 6: Build to verify the scaffold compiles**

Run: `dotnet build Themia.sln`
Expected: build succeeds (both new projects produce empty assemblies; no warnings).

- [ ] **Step 7: Commit**

```bash
git add Directory.Packages.props Themia.sln src/modules/Themia.Modules.Identity.Abstractions src/modules/Themia.Modules.Identity
git commit -m "feat(identity): scaffold Identity.Abstractions + Identity packages"
```

---

## Task 2: Entities (Abstractions)

**Files:**
- Create: `src/modules/Themia.Modules.Identity.Abstractions/Entities/TokenPurpose.cs`
- Create: `src/modules/Themia.Modules.Identity.Abstractions/Entities/User.cs`, `Role.cs`, `UserRole.cs`, `UserClaim.cs`, `RoleClaim.cs`, `UserToken.cs`
- Test: `tests/Themia.Modules.Identity.Tests/Entities/EntityDefaultsTests.cs`

**Design notes (read first):**
- `User` and `Role` derive from `SoftDeletableEntity<Guid>` (gives audit + soft-delete columns) and implement `ITenantEntity` (tenant column + auto-stamp). Add a public `SetId(Guid)` so callers/tests assign client-generated GUIDs (matches the Dapper conformance `Widget.SetId` pattern). **Optimistic concurrency (`IConcurrencyAware`/`RowVersion`) is intentionally omitted for 0.5.0** — it pulls in the `rowversion`(SQL Server)/`xmin`(PostgreSQL) split plus Dapper's separate concurrency path for marginal benefit on identity rows; it is a documented later hardening (spec §4 "+ concurrency" defers here).
- Child tables (`UserRole`, `UserClaim`, `RoleClaim`, `UserToken`) are **parent-keyed POCOs with no `tenant_id`**. Tenant isolation for children is enforced at the service layer: every child mutation first loads the tenant-scoped parent. (Construction-level child isolation via `tenant_id` on children is a documented future hardening, out of scope for 0.5.0 — see spec §4.)

- [ ] **Step 1: Write the failing test**

`tests/Themia.Modules.Identity.Tests/Entities/EntityDefaultsTests.cs`:

```csharp
using Themia.Modules.Identity.Abstractions.Entities;
using Xunit;

namespace Themia.Modules.Identity.Tests.Entities;

public class EntityDefaultsTests
{
    [Fact]
    public void SetId_assigns_identifier()
    {
        var user = new User();
        var id = Guid.NewGuid();

        user.SetId(id);

        Assert.Equal(id, user.Id);
    }

    [Fact]
    public void New_user_defaults_are_safe()
    {
        var user = new User();

        Assert.False(user.IsDeleted);
        Assert.True(user.IsActive);            // created enabled
        Assert.Equal(0, user.AccessFailedCount);
        Assert.Null(user.LockoutEnd);
        Assert.False(user.EmailConfirmed);
        Assert.False(user.TwoFactorEnabled);
        Assert.Null(user.TenantId);            // null == platform until a tenant is stamped
    }

    [Fact]
    public void UserToken_purpose_roundtrips()
    {
        var token = new UserToken { Purpose = TokenPurpose.PasswordReset };
        Assert.Equal(TokenPurpose.PasswordReset, token.Purpose);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Themia.sln --filter EntityDefaultsTests`
Expected: FAIL — `User`/`UserToken`/`TokenPurpose` do not exist (compile error). (The test project is created in Step 4 of this task if it does not yet exist; if the filter reports "no test project", create the test project first per Step 3, then re-run.)

- [ ] **Step 3: Create the test project (if absent) and reference it**

`tests/Themia.Modules.Identity.Tests/Themia.Modules.Identity.Tests.csproj`:

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
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/modules/Themia.Modules.Identity/Themia.Modules.Identity.csproj" />
    <ProjectReference Include="../../src/modules/Themia.Modules.Identity.Abstractions/Themia.Modules.Identity.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

Add to the solution:

```bash
dotnet sln Themia.sln add tests/Themia.Modules.Identity.Tests/Themia.Modules.Identity.Tests.csproj
```

- [ ] **Step 4: Implement the enum and entities**

`Entities/TokenPurpose.cs`:

```csharp
namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>The purpose a <see cref="UserToken"/> serves.</summary>
public enum TokenPurpose
{
    /// <summary>Confirms a user's email address.</summary>
    EmailConfirm,

    /// <summary>Confirms a user's phone number.</summary>
    PhoneConfirm,

    /// <summary>Authorizes a password reset.</summary>
    PasswordReset,

    /// <summary>A two-factor authentication challenge token.</summary>
    TwoFactor,
}
```

`Entities/User.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>
/// A user account. Tenant-scoped when <see cref="ITenantEntity.TenantId"/> is set; a platform
/// (cross-tenant) super-admin when it is <see langword="null"/>.
/// </summary>
public sealed class User : SoftDeletableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The login name, as entered by the user.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>The upper-invariant normalized login name used for lookups and uniqueness.</summary>
    public string NormalizedUserName { get; set; } = string.Empty;

    /// <summary>The email address, as entered.</summary>
    public string? Email { get; set; }

    /// <summary>The upper-invariant normalized email used for lookups and uniqueness.</summary>
    public string? NormalizedEmail { get; set; }

    /// <summary>Whether the email address has been confirmed.</summary>
    public bool EmailConfirmed { get; set; }

    /// <summary>The phone number, as entered.</summary>
    public string? PhoneNumber { get; set; }

    /// <summary>Whether the phone number has been confirmed.</summary>
    public bool PhoneNumberConfirmed { get; set; }

    /// <summary>The argon2id password hash, or <see langword="null"/> when no password is set.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>A random value reissued whenever credentials change; invalidates stale principals.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Whether the account is enabled. Disabled accounts cannot authenticate.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>The number of consecutive failed password verifications since the last success.</summary>
    public int AccessFailedCount { get; set; }

    /// <summary>When the account lockout ends, or <see langword="null"/> when not locked out.</summary>
    public DateTimeOffset? LockoutEnd { get; set; }

    /// <summary>Whether lockout is enforced for this account.</summary>
    public bool LockoutEnabled { get; set; } = true;

    /// <summary>Whether two-factor authentication is enabled (the 0.5.0 hook; TOTP arrives later).</summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>Assigns the identifier for a new (transient) user.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
```

`Entities/Role.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>A named role granting a set of claims. Platform-wide when <see cref="ITenantEntity.TenantId"/> is null.</summary>
public sealed class Role : SoftDeletableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The role name, as entered.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The upper-invariant normalized name used for lookups and uniqueness.</summary>
    public string NormalizedName { get; set; } = string.Empty;

    /// <summary>An optional human-readable description.</summary>
    public string? Description { get; set; }

    /// <summary>Assigns the identifier for a new (transient) role.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
```

`Entities/UserRole.cs`:

```csharp
namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>Join row assigning a <see cref="Role"/> to a <see cref="User"/>. Carries a surrogate id so it keys uniformly through the generic repository; a unique index on (user_id, role_id) prevents duplicates.</summary>
public sealed class UserRole
{
    /// <summary>The surrogate identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The user identifier.</summary>
    public Guid UserId { get; set; }

    /// <summary>The role identifier.</summary>
    public Guid RoleId { get; set; }

    /// <summary>Assigns the identifier for a new (transient) membership.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
```

`Entities/UserClaim.cs`:

```csharp
namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>A claim granted directly to a user.</summary>
public sealed class UserClaim
{
    /// <summary>The claim identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning user identifier.</summary>
    public Guid UserId { get; set; }

    /// <summary>The claim type (e.g. a permission name).</summary>
    public string ClaimType { get; set; } = string.Empty;

    /// <summary>The claim value.</summary>
    public string ClaimValue { get; set; } = string.Empty;

    /// <summary>Assigns the identifier for a new (transient) claim.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
```

`Entities/RoleClaim.cs`:

```csharp
namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>A claim granted to all users in a role.</summary>
public sealed class RoleClaim
{
    /// <summary>The claim identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning role identifier.</summary>
    public Guid RoleId { get; set; }

    /// <summary>The claim type.</summary>
    public string ClaimType { get; set; } = string.Empty;

    /// <summary>The claim value.</summary>
    public string ClaimValue { get; set; } = string.Empty;

    /// <summary>Assigns the identifier for a new (transient) claim.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
```

`Entities/UserToken.cs`:

```csharp
namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>A persisted, single-use, expiring token (email/phone confirmation, password reset, 2FA). The raw token is never stored — only its hash.</summary>
public sealed class UserToken
{
    /// <summary>The token identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The owning user identifier.</summary>
    public Guid UserId { get; set; }

    /// <summary>What the token authorizes.</summary>
    public TokenPurpose Purpose { get; set; }

    /// <summary>The hash of the raw token value.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>When the token expires.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the token was consumed, or <see langword="null"/> while still valid.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>Assigns the identifier for a new (transient) token.</summary>
    /// <param name="id">A client-generated identifier.</param>
    public void SetId(Guid id) => Id = id;
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Themia.sln --filter EntityDefaultsTests`
Expected: PASS (3 tests).

- [ ] **Step 6: Update PublicAPI and build clean**

Run: `dotnet build Themia.sln --no-incremental`
Add every `RS0016`-reported member to `src/modules/Themia.Modules.Identity.Abstractions/PublicAPI.Unshipped.txt` (the diagnostic prints each line verbatim). Re-run until the build is clean.

- [ ] **Step 7: Commit**

```bash
git add src/modules/Themia.Modules.Identity.Abstractions tests/Themia.Modules.Identity.Tests Themia.sln
git commit -m "feat(identity): user/role/claim/token entities"
```

---

## Task 3: Abstraction contracts — enums, options, results, service + principal interfaces

**Files:**
- Create: `src/modules/Themia.Modules.Identity.Abstractions/PasswordVerificationResult.cs`
- Create: `src/modules/Themia.Modules.Identity.Abstractions/Results.cs`
- Create: `src/modules/Themia.Modules.Identity.Abstractions/IdentityModuleOptions.cs`
- Create: `src/modules/Themia.Modules.Identity.Abstractions/IPasswordHasher.cs`
- Create: `src/modules/Themia.Modules.Identity.Abstractions/IUserService.cs`, `IRoleService.cs`, `IClaimService.cs`, `IUserTokenService.cs`
- Create: `src/modules/Themia.Modules.Identity.Abstractions/ICurrentUser.cs`, `IClaimsPrincipalFactory.cs`

These are contract-only types (no tests of their own — they are exercised by every later task). After writing them, build clean and update PublicAPI.

- [ ] **Step 1: PasswordVerificationResult**

`PasswordVerificationResult.cs`:

```csharp
namespace Themia.Modules.Identity.Abstractions;

/// <summary>The outcome of verifying a password against a user account.</summary>
public enum PasswordVerificationResult
{
    /// <summary>The password matched and the account may authenticate.</summary>
    Success,

    /// <summary>The password did not match.</summary>
    Failed,

    /// <summary>The account is locked out and cannot authenticate right now.</summary>
    LockedOut,

    /// <summary>The account is disabled.</summary>
    Inactive,

    /// <summary>No matching user exists.</summary>
    NotFound,
}
```

- [ ] **Step 2: Results**

`Results.cs`:

```csharp
namespace Themia.Modules.Identity.Abstractions;

/// <summary>The outcome of creating a user.</summary>
/// <param name="Succeeded">Whether the user was created.</param>
/// <param name="UserId">The new user's id when <paramref name="Succeeded"/> is true; otherwise null.</param>
/// <param name="Error">A stable error code when creation failed (e.g. <c>"duplicate_user_name"</c>, <c>"duplicate_email"</c>); otherwise null.</param>
public readonly record struct UserCreationResult(bool Succeeded, Guid? UserId, string? Error)
{
    /// <summary>Creates a success result.</summary>
    /// <param name="userId">The new user's identifier.</param>
    public static UserCreationResult Success(Guid userId) => new(true, userId, null);

    /// <summary>Creates a failure result.</summary>
    /// <param name="error">A stable error code.</param>
    public static UserCreationResult Failure(string error) => new(false, null, error);
}

/// <summary>The outcome of consuming a user token.</summary>
public enum TokenConsumeResult
{
    /// <summary>The token was valid and is now consumed.</summary>
    Success,

    /// <summary>No matching unconsumed token exists for the user and purpose.</summary>
    NotFound,

    /// <summary>The token existed but has expired.</summary>
    Expired,

    /// <summary>The token was already consumed.</summary>
    AlreadyConsumed,
}
```

- [ ] **Step 3: IdentityModuleOptions**

`IdentityModuleOptions.cs`:

```csharp
namespace Themia.Modules.Identity.Abstractions;

/// <summary>Tunable policy for the Identity module.</summary>
public sealed class IdentityModuleOptions
{
    /// <summary>The configuration connection-string name the schema migration runs against. Defaults to <c>"Default"</c>.</summary>
    public string ConnectionStringName { get; set; } = "Default";

    /// <summary>Consecutive failed password attempts before lockout engages. Defaults to 5.</summary>
    public int MaxFailedAccessAttempts { get; set; } = 5;

    /// <summary>How long an account stays locked out once the threshold is hit. Defaults to 15 minutes.</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Whether a platform (global) user may authenticate against a tenant entry point. Defaults to true.</summary>
    public bool AllowPlatformLogin { get; set; } = true;

    /// <summary>Default lifetime for generated <see cref="Entities.TokenPurpose"/> tokens. Defaults to 1 hour.</summary>
    public TimeSpan DefaultTokenLifetime { get; set; } = TimeSpan.FromHours(1);
}
```

- [ ] **Step 4: IPasswordHasher**

`IPasswordHasher.cs`:

```csharp
namespace Themia.Modules.Identity.Abstractions;

/// <summary>Hashes and verifies passwords. The default implementation is argon2id; swap via DI to override.</summary>
public interface IPasswordHasher
{
    /// <summary>Hashes a plaintext password into a self-describing encoded string.</summary>
    /// <param name="password">The plaintext password.</param>
    /// <returns>An encoded hash that embeds the algorithm parameters and salt.</returns>
    string Hash(string password);

    /// <summary>Verifies a plaintext password against an encoded hash.</summary>
    /// <param name="encodedHash">A hash previously produced by <see cref="Hash"/>.</param>
    /// <param name="password">The plaintext password to check.</param>
    /// <returns><see langword="true"/> when the password matches.</returns>
    bool Verify(string encodedHash, string password);

    /// <summary>Indicates whether an existing hash should be re-computed because the hashing parameters have changed.</summary>
    /// <param name="encodedHash">A hash previously produced by <see cref="Hash"/>.</param>
    /// <returns><see langword="true"/> when the caller should re-hash on the next successful verify.</returns>
    bool NeedsRehash(string encodedHash);
}
```

- [ ] **Step 5: Service interfaces**

`IUserService.cs`:

```csharp
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>Creates and manages <see cref="User"/> accounts within the ambient tenant (and, for lookups, the platform scope).</summary>
public interface IUserService
{
    /// <summary>Creates a user in the ambient tenant with the given password. Normalizes the user name and email.</summary>
    /// <param name="userName">The login name.</param>
    /// <param name="password">The plaintext password (hashed before storage).</param>
    /// <param name="email">An optional email address.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A <see cref="UserCreationResult"/>.</returns>
    Task<UserCreationResult> CreateAsync(string userName, string password, string? email = null, CancellationToken cancellationToken = default);

    /// <summary>Finds a user by id within the ambient tenant.</summary>
    /// <param name="id">The user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The user, or null.</returns>
    Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Finds a user by login name — first in the ambient tenant, then (when allowed) in the platform scope.</summary>
    /// <param name="userName">The login name (any casing).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The user, or null.</returns>
    Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default);

    /// <summary>Finds a user by email — first in the ambient tenant, then (when allowed) in the platform scope.</summary>
    /// <param name="email">The email address (any casing).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The user, or null.</returns>
    Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Sets (or replaces) a user's password and reissues the security stamp.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="password">The new plaintext password.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when the user was found and updated.</returns>
    Task<bool> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default);

    /// <summary>Verifies a password and applies the lockout state machine (increments/locks on failure, resets on success).</summary>
    /// <param name="userName">The login name.</param>
    /// <param name="password">The plaintext password.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The <see cref="PasswordVerificationResult"/>.</returns>
    Task<PasswordVerificationResult> VerifyPasswordAsync(string userName, string password, CancellationToken cancellationToken = default);

    /// <summary>Enables or disables an account.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="isActive">Whether the account is enabled.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when the user was found and updated.</returns>
    Task<bool> SetActiveAsync(Guid userId, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes a user.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when the user was found and deleted.</returns>
    Task<bool> DeleteAsync(Guid userId, CancellationToken cancellationToken = default);
}
```

`IRoleService.cs`:

```csharp
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>Creates and assigns <see cref="Role"/> records.</summary>
public interface IRoleService
{
    /// <summary>Creates a role in the ambient tenant (or platform scope when no tenant is ambient).</summary>
    /// <param name="name">The role name.</param>
    /// <param name="description">An optional description.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The new role id, or null when a same-named role already exists in scope.</returns>
    Task<Guid?> CreateAsync(string name, string? description = null, CancellationToken cancellationToken = default);

    /// <summary>Finds a role by normalized name within the ambient tenant (then platform scope).</summary>
    /// <param name="name">The role name (any casing).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The role, or null.</returns>
    Task<Role?> FindByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Assigns a role to a user. Both must resolve within the ambient tenant scope.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="roleId">The role id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when assigned (or already assigned); false when either side is not found in scope.</returns>
    Task<bool> AssignRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>Removes a role from a user.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="roleId">The role id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when a membership was removed.</returns>
    Task<bool> RemoveRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default);

    /// <summary>Lists the role ids assigned to a user.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The assigned role ids.</returns>
    Task<IReadOnlyList<Guid>> GetRoleIdsAsync(Guid userId, CancellationToken cancellationToken = default);
}
```

`IClaimService.cs`:

```csharp
using System.Security.Claims;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>Manages user and role claims and computes a user's effective claim set.</summary>
public interface IClaimService
{
    /// <summary>Adds a claim directly to a user.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="claimType">The claim type.</param>
    /// <param name="claimValue">The claim value.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task AddUserClaimAsync(Guid userId, string claimType, string claimValue, CancellationToken cancellationToken = default);

    /// <summary>Removes a matching claim from a user.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="claimType">The claim type.</param>
    /// <param name="claimValue">The claim value.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> when a claim was removed.</returns>
    Task<bool> RemoveUserClaimAsync(Guid userId, string claimType, string claimValue, CancellationToken cancellationToken = default);

    /// <summary>Adds a claim to a role.</summary>
    /// <param name="roleId">The role id.</param>
    /// <param name="claimType">The claim type.</param>
    /// <param name="claimValue">The claim value.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task AddRoleClaimAsync(Guid roleId, string claimType, string claimValue, CancellationToken cancellationToken = default);

    /// <summary>Computes the union of a user's direct claims and the claims of every role assigned to the user.</summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The distinct effective claims.</returns>
    Task<IReadOnlyList<Claim>> GetEffectiveClaimsAsync(Guid userId, CancellationToken cancellationToken = default);
}
```

`IUserTokenService.cs`:

```csharp
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>Issues and consumes single-use, expiring user tokens (email/phone confirmation, password reset, 2FA).</summary>
public interface IUserTokenService
{
    /// <summary>Generates a token for a purpose and persists only its hash. The raw token is returned exactly once.</summary>
    /// <param name="userId">The owning user id.</param>
    /// <param name="purpose">What the token authorizes.</param>
    /// <param name="lifetime">An optional lifetime; defaults to <see cref="IdentityModuleOptions.DefaultTokenLifetime"/>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The raw token value to deliver to the user.</returns>
    Task<string> GenerateAsync(Guid userId, TokenPurpose purpose, TimeSpan? lifetime = null, CancellationToken cancellationToken = default);

    /// <summary>Validates and consumes a token (single-use, expiry-checked, constant-time hash compare).</summary>
    /// <param name="userId">The owning user id.</param>
    /// <param name="purpose">The expected purpose.</param>
    /// <param name="rawToken">The raw token presented by the user.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The <see cref="TokenConsumeResult"/>.</returns>
    Task<TokenConsumeResult> ConsumeAsync(Guid userId, TokenPurpose purpose, string rawToken, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 6: Principal interfaces**

`ICurrentUser.cs`:

```csharp
using System.Security.Claims;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>The ambient authenticated principal for the current request. Inject this into application code.</summary>
public interface ICurrentUser
{
    /// <summary>Whether a user is authenticated.</summary>
    bool IsAuthenticated { get; }

    /// <summary>The authenticated user's id, or null when unauthenticated.</summary>
    Guid? UserId { get; }

    /// <summary>The user's tenant id, or null for a platform (cross-tenant) user or when unauthenticated.</summary>
    string? TenantId { get; }

    /// <summary>Whether the user is a platform (cross-tenant) user — true when authenticated with no tenant.</summary>
    bool IsPlatform { get; }

    /// <summary>The user's login name, or null when unauthenticated.</summary>
    string? UserName { get; }

    /// <summary>The user's role names.</summary>
    IReadOnlyCollection<string> Roles { get; }

    /// <summary>The user's claims.</summary>
    IReadOnlyCollection<Claim> Claims { get; }

    /// <summary>Whether the user is in the named role.</summary>
    /// <param name="role">The role name.</param>
    /// <returns><see langword="true"/> when the user holds the role.</returns>
    bool IsInRole(string role);
}
```

`IClaimsPrincipalFactory.cs`:

```csharp
using System.Security.Claims;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>Builds a <see cref="ClaimsPrincipal"/> from a user — the single source of what goes into the principal (used by cookie auth in 0.5.0 and JWT issuance in 0.5.1).</summary>
public interface IClaimsPrincipalFactory
{
    /// <summary>Creates the claims principal for a user, including role claims and the effective claim set.</summary>
    /// <param name="user">The user.</param>
    /// <param name="authenticationType">The authentication type to stamp on the identity (e.g. <c>"Identity.Application"</c> or <c>"Bearer"</c>).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The constructed principal.</returns>
    Task<ClaimsPrincipal> CreateAsync(User user, string authenticationType, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 7: Build clean and update PublicAPI**

Run: `dotnet build Themia.sln --no-incremental`
Add every `RS0016`-reported member to `src/modules/Themia.Modules.Identity.Abstractions/PublicAPI.Unshipped.txt`. Re-run until clean.

- [ ] **Step 8: Commit**

```bash
git add src/modules/Themia.Modules.Identity.Abstractions
git commit -m "feat(identity): abstraction contracts — services, hasher, principal, options"
```

---

## Task 4: Argon2id password hasher + token hasher

**Files:**
- Create: `src/modules/Themia.Modules.Identity/Hashing/Argon2idPasswordHasher.cs`
- Create: `src/modules/Themia.Modules.Identity/Hashing/TokenHasher.cs`
- Test: `tests/Themia.Modules.Identity.Tests/Hashing/Argon2idPasswordHasherTests.cs`
- Test: `tests/Themia.Modules.Identity.Tests/Hashing/TokenHasherTests.cs`

**Design notes:** The encoded hash format is `argon2id$v=19$m=<mem>,t=<iter>,p=<par>$<saltB64>$<hashB64>` so it self-describes its parameters; `NeedsRehash` parses the `m/t/p` triplet and compares to the current cost. `Verify` and the token compare use `CryptographicOperations.FixedTimeEquals` for constant-time comparison.

- [ ] **Step 1: Write the failing tests**

`tests/Themia.Modules.Identity.Tests/Hashing/Argon2idPasswordHasherTests.cs`:

```csharp
using Themia.Modules.Identity.Hashing;
using Xunit;

namespace Themia.Modules.Identity.Tests.Hashing;

public class Argon2idPasswordHasherTests
{
    private readonly Argon2idPasswordHasher hasher = new();

    [Fact]
    public void Hash_is_not_plaintext_and_is_encoded()
    {
        var encoded = hasher.Hash("correct horse battery staple");

        Assert.DoesNotContain("correct horse", encoded);
        Assert.StartsWith("argon2id$", encoded);
    }

    [Fact]
    public void Hash_is_salted_so_two_hashes_of_same_password_differ()
    {
        Assert.NotEqual(hasher.Hash("pw"), hasher.Hash("pw"));
    }

    [Fact]
    public void Verify_succeeds_for_correct_password()
    {
        var encoded = hasher.Hash("s3cret");
        Assert.True(hasher.Verify(encoded, "s3cret"));
    }

    [Fact]
    public void Verify_fails_for_wrong_password()
    {
        var encoded = hasher.Hash("s3cret");
        Assert.False(hasher.Verify(encoded, "wrong"));
    }

    [Fact]
    public void Verify_returns_false_for_malformed_hash_instead_of_throwing()
    {
        Assert.False(hasher.Verify("not-a-valid-hash", "pw"));
    }

    [Fact]
    public void NeedsRehash_is_false_for_a_freshly_made_hash()
    {
        var encoded = hasher.Hash("pw");
        Assert.False(hasher.NeedsRehash(encoded));
    }

    [Fact]
    public void NeedsRehash_is_true_for_weaker_parameters()
    {
        var weak = "argon2id$v=19$m=1024,t=1,p=1$c2FsdHNhbHQ=$aGFzaGhhc2g=";
        Assert.True(hasher.NeedsRehash(weak));
    }
}
```

`tests/Themia.Modules.Identity.Tests/Hashing/TokenHasherTests.cs`:

```csharp
using Themia.Modules.Identity.Hashing;
using Xunit;

namespace Themia.Modules.Identity.Tests.Hashing;

public class TokenHasherTests
{
    [Fact]
    public void Hash_is_deterministic_for_the_same_token()
    {
        Assert.Equal(TokenHasher.Hash("abc"), TokenHasher.Hash("abc"));
    }

    [Fact]
    public void Hash_differs_for_different_tokens()
    {
        Assert.NotEqual(TokenHasher.Hash("abc"), TokenHasher.Hash("abd"));
    }

    [Fact]
    public void Matches_is_true_only_for_the_right_token()
    {
        var hash = TokenHasher.Hash("token-value");
        Assert.True(TokenHasher.Matches(hash, "token-value"));
        Assert.False(TokenHasher.Matches(hash, "other"));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~Themia.Modules.Identity.Tests.Hashing"`
Expected: FAIL — `Argon2idPasswordHasher` / `TokenHasher` do not exist.

- [ ] **Step 3: Implement the token hasher**

`src/modules/Themia.Modules.Identity/Hashing/TokenHasher.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Themia.Modules.Identity.Hashing;

/// <summary>Hashes opaque tokens (SHA-256) for at-rest storage, with a constant-time compare. Tokens carry their own entropy, so no salt is needed.</summary>
internal static class TokenHasher
{
    /// <summary>Returns the Base64 SHA-256 hash of a raw token.</summary>
    public static string Hash(string rawToken)
    {
        ArgumentNullException.ThrowIfNull(rawToken);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Constant-time check that a stored hash matches a presented raw token.</summary>
    public static bool Matches(string storedHash, string rawToken)
    {
        ArgumentNullException.ThrowIfNull(storedHash);
        ArgumentNullException.ThrowIfNull(rawToken);

        var presented = Encoding.UTF8.GetBytes(Hash(rawToken));
        var stored = Encoding.UTF8.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(presented, stored);
    }
}
```

- [ ] **Step 4: Implement the argon2id hasher**

`src/modules/Themia.Modules.Identity/Hashing/Argon2idPasswordHasher.cs`:

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Themia.Modules.Identity.Abstractions;

namespace Themia.Modules.Identity.Hashing;

/// <summary>
/// Default <see cref="IPasswordHasher"/> using argon2id. The encoded form is
/// <c>argon2id$v=19$m=&lt;memKiB&gt;,t=&lt;iterations&gt;,p=&lt;parallelism&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;</c>,
/// which self-describes its parameters so <see cref="NeedsRehash"/> can detect outdated costs.
/// </summary>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    private const int Version = 19;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    // Current cost parameters. Raising any of these makes existing hashes "need rehash".
    private const int MemoryKiB = 19 * 1024;   // 19 MiB
    private const int Iterations = 2;
    private const int Parallelism = 1;

    /// <inheritdoc />
    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Compute(password, salt, MemoryKiB, Iterations, Parallelism);

        return string.Create(CultureInfo.InvariantCulture,
            $"argon2id$v={Version}$m={MemoryKiB},t={Iterations},p={Parallelism}$" +
            $"{Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <inheritdoc />
    public bool Verify(string encodedHash, string password)
    {
        ArgumentNullException.ThrowIfNull(encodedHash);
        ArgumentNullException.ThrowIfNull(password);

        if (!TryParse(encodedHash, out var p))
        {
            return false;
        }

        var computed = Compute(password, p.Salt, p.MemoryKiB, p.Iterations, p.Parallelism);
        return CryptographicOperations.FixedTimeEquals(computed, p.Hash);
    }

    /// <inheritdoc />
    public bool NeedsRehash(string encodedHash)
    {
        ArgumentNullException.ThrowIfNull(encodedHash);

        if (!TryParse(encodedHash, out var p))
        {
            return true;
        }

        return p.MemoryKiB < MemoryKiB || p.Iterations < Iterations || p.Parallelism < Parallelism;
    }

    private static byte[] Compute(string password, byte[] salt, int memoryKiB, int iterations, int parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKiB,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(HashSize);
    }

    private readonly record struct Parameters(byte[] Salt, byte[] Hash, int MemoryKiB, int Iterations, int Parallelism);

    private static bool TryParse(string encoded, out Parameters parameters)
    {
        parameters = default;

        // argon2id $ v=19 $ m=..,t=..,p=.. $ saltB64 $ hashB64
        var parts = encoded.Split('$');
        if (parts.Length != 5 || parts[0] != "argon2id")
        {
            return false;
        }

        var cost = parts[2].Split(',');
        if (cost.Length != 3)
        {
            return false;
        }

        if (!TryReadInt(cost[0], "m=", out var mem) ||
            !TryReadInt(cost[1], "t=", out var iter) ||
            !TryReadInt(cost[2], "p=", out var par))
        {
            return false;
        }

        try
        {
            parameters = new Parameters(
                Convert.FromBase64String(parts[3]),
                Convert.FromBase64String(parts[4]),
                mem, iter, par);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryReadInt(string token, string prefix, out int value)
    {
        value = 0;
        return token.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(token.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~Themia.Modules.Identity.Tests.Hashing"`
Expected: PASS (10 tests).

- [ ] **Step 6: Build clean and update PublicAPI**

Run: `dotnet build Themia.sln --no-incremental`
Add `RS0016`-reported members (the `Argon2idPasswordHasher` public surface) to `src/modules/Themia.Modules.Identity/PublicAPI.Unshipped.txt`. `TokenHasher` is `internal` — it should not appear.

- [ ] **Step 7: Commit**

```bash
git add src/modules/Themia.Modules.Identity/Hashing tests/Themia.Modules.Identity.Tests/Hashing src/modules/Themia.Modules.Identity/PublicAPI.Unshipped.txt
git commit -m "feat(identity): argon2id password hasher + token hasher"
```

---

## Task 5: UserService (with in-memory test fakes)

**Files:**
- Create: `tests/Themia.Modules.Identity.Tests/Fakes/FakeRepository.cs`, `Fakes/FakeUnitOfWork.cs`
- Create: `src/modules/Themia.Modules.Identity/Specifications/IdentitySpecs.cs`
- Create: `src/modules/Themia.Modules.Identity/Services/UserService.cs`
- Test: `tests/Themia.Modules.Identity.Tests/Services/UserServiceTests.cs`

**Design notes:** The fakes evaluate a `Specification<T>`'s compiled `Criteria` against an in-memory list, excluding soft-deleted rows and (unless `IgnoreTenantFilter`) rows whose `TenantId` ≠ the fake's `AmbientTenant`. This mirrors the real repositories closely enough to test service *logic*; real tenancy/SQL is proven by the integration conformance suite (Task 13). The real repos auto-stamp `TenantId` from the ambient tenant on `AddAsync`; the fake does the same.

- [ ] **Step 1: Write the fakes**

`tests/Themia.Modules.Identity.Tests/Fakes/FakeRepository.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Paging;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;

namespace Themia.Modules.Identity.Tests.Fakes;

/// <summary>In-memory IRepository for service unit tests. Honors soft-delete, tenant filtering, and IgnoreTenantFilter.</summary>
internal sealed class FakeRepository<T>(List<T> store, Func<T, Guid> idSelector) : IRepository<T, Guid>
    where T : class
{
    public TenantId? AmbientTenant { get; set; }

    private bool TenantMatches(T e) =>
        e is not ITenantEntity te || Nullable.Equals(te.TenantId, AmbientTenant);

    private static bool NotDeleted(T e) => e is not ISoftDeletable sd || !sd.IsDeleted;

    private IEnumerable<T> Query(ISpecification<T> spec)
    {
        IEnumerable<T> q = store.Where(NotDeleted);
        if (!spec.IgnoreTenantFilter)
        {
            q = q.Where(TenantMatches);
        }
        if (spec.Criteria is not null)
        {
            q = q.Where(spec.Criteria.Compile());
        }
        return q;
    }

    public Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(store.FirstOrDefault(e => NotDeleted(e) && TenantMatches(e) && idSelector(e) == id));

    public Task<T?> FirstOrDefaultAsync(ISpecification<T> specification, CancellationToken cancellationToken = default) =>
        Task.FromResult(Query(specification).FirstOrDefault());

    public Task<IReadOnlyList<T>> ListAsync(ISpecification<T> specification, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<T>>(Query(specification).ToList());

    public Task<long> CountAsync(ISpecification<T> specification, CancellationToken cancellationToken = default) =>
        Task.FromResult<long>(Query(specification).LongCount());

    public Task<bool> AnyAsync(ISpecification<T> specification, CancellationToken cancellationToken = default) =>
        Task.FromResult(Query(specification).Any());

    public Task<PagedResult<T>> PageAsync(ISpecification<T> specification, CancellationToken cancellationToken = default)
    {
        var all = Query(specification).ToList();
        return Task.FromResult(new PagedResult<T>(all, all.Count, 0, all.Count));
    }

    public Task AddAsync(T entity, CancellationToken cancellationToken = default)
    {
        if (entity is ITenantEntity { TenantId: null } te && AmbientTenant is { } t)
        {
            te.TenantId = t;   // mirror the real repos' ambient-tenant stamp on add
        }
        store.Add(entity);
        return Task.CompletedTask;
    }

    public void Update(T entity) { /* in-memory: the instance is already mutated */ }

    public void Remove(T entity)
    {
        if (entity is ISoftDeletable)
        {
            // Mirror ThemiaDbContext: Remove on a soft-deletable converts to soft-delete.
            var prop = typeof(T).GetProperty(nameof(ISoftDeletable.IsDeleted))!;
            prop.SetValue(entity, true);
        }
        else
        {
            store.Remove(entity);
        }
    }
}
```

> Note `PagedResult<T>`'s constructor shape: confirm against `src/framework/Themia.Framework.Data.Abstractions/Paging/PagedResult.cs` and adjust the argument order if it differs. `PageAsync` is not exercised by these unit tests; it exists only to satisfy the interface.

`tests/Themia.Modules.Identity.Tests/Fakes/FakeUnitOfWork.cs`:

```csharp
using Themia.Framework.Data.Abstractions.UnitOfWork;

namespace Themia.Modules.Identity.Tests.Fakes;

/// <summary>No-op unit of work for unit tests (the fake repository mutates its list eagerly).</summary>
internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        return Task.FromResult(0);
    }

    public Task<ITransactionScope> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default) =>
        work(cancellationToken);
}
```

- [ ] **Step 2: Write the lookup specifications**

`src/modules/Themia.Modules.Identity/Specifications/IdentitySpecs.cs`:

```csharp
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Specifications;

/// <summary>Finds a user by normalized user name within the ambient tenant.</summary>
internal sealed class UserByNormalizedNameSpec : Specification<User>
{
    public UserByNormalizedNameSpec(string normalizedUserName) =>
        Where(u => u.NormalizedUserName == normalizedUserName);
}

/// <summary>Finds a platform (global) user by normalized user name, bypassing the tenant filter.</summary>
internal sealed class PlatformUserByNormalizedNameSpec : Specification<User>
{
    public PlatformUserByNormalizedNameSpec(string normalizedUserName)
    {
        Where(u => u.NormalizedUserName == normalizedUserName && u.TenantId == null);
        WithoutTenantFilter();
    }
}

/// <summary>Finds a user by normalized email within the ambient tenant.</summary>
internal sealed class UserByNormalizedEmailSpec : Specification<User>
{
    public UserByNormalizedEmailSpec(string normalizedEmail) =>
        Where(u => u.NormalizedEmail == normalizedEmail);
}

/// <summary>Finds a platform (global) user by normalized email, bypassing the tenant filter.</summary>
internal sealed class PlatformUserByNormalizedEmailSpec : Specification<User>
{
    public PlatformUserByNormalizedEmailSpec(string normalizedEmail)
    {
        Where(u => u.NormalizedEmail == normalizedEmail && u.TenantId == null);
        WithoutTenantFilter();
    }
}
```

> EF translation risk: `u.TenantId == null` goes through the `TenantId` value converter. If an integration test later reports the comparison cannot be translated, change those predicates to `!u.TenantId.HasValue`. Verified at Task 13.

- [ ] **Step 3: Write the failing UserService tests**

`tests/Themia.Modules.Identity.Tests/Services/UserServiceTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Hashing;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class UserServiceTests
{
    private readonly List<User> store = [];
    private readonly FakeRepository<User> repo;
    private readonly FakeUnitOfWork uow = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-06-14T00:00:00Z"));
    private readonly IdentityModuleOptions options = new();
    private readonly UserService sut;

    public UserServiceTests()
    {
        repo = new FakeRepository<User>(store, u => u.Id) { AmbientTenant = new TenantId("acme") };
        sut = new UserService(repo, uow, new Argon2idPasswordHasher(), clock, options);
    }

    [Fact]
    public async Task CreateAsync_persists_normalized_user_with_hashed_password()
    {
        var result = await sut.CreateAsync("Alice", "pw1", "Alice@Example.com");

        Assert.True(result.Succeeded);
        var user = Assert.Single(store);
        Assert.Equal("ALICE", user.NormalizedUserName);
        Assert.Equal("ALICE@EXAMPLE.COM", user.NormalizedEmail);
        Assert.NotEqual("pw1", user.PasswordHash);
        Assert.Equal(new TenantId("acme"), user.TenantId);   // stamped by the repo
    }

    [Fact]
    public async Task CreateAsync_rejects_duplicate_user_name_in_same_tenant()
    {
        await sut.CreateAsync("bob", "pw");
        var second = await sut.CreateAsync("BOB", "pw");

        Assert.False(second.Succeeded);
        Assert.Equal("duplicate_user_name", second.Error);
    }

    [Fact]
    public async Task FindByUserNameAsync_finds_tenant_user_case_insensitively()
    {
        await sut.CreateAsync("carol", "pw");
        var found = await sut.FindByUserNameAsync("CAROL");
        Assert.NotNull(found);
    }

    [Fact]
    public async Task FindByUserNameAsync_falls_back_to_platform_user()
    {
        // A platform user (TenantId null) created directly in the store.
        var platform = new User { UserName = "root", NormalizedUserName = "ROOT", PasswordHash = "x", TenantId = null };
        platform.SetId(Guid.NewGuid());
        store.Add(platform);

        var found = await sut.FindByUserNameAsync("root");
        Assert.NotNull(found);
        Assert.Null(found!.TenantId);
    }

    [Fact]
    public async Task FindByUserNameAsync_skips_platform_when_disabled()
    {
        options.AllowPlatformLogin = false;
        var platform = new User { UserName = "root", NormalizedUserName = "ROOT", PasswordHash = "x", TenantId = null };
        platform.SetId(Guid.NewGuid());
        store.Add(platform);

        Assert.Null(await sut.FindByUserNameAsync("root"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_succeeds_for_correct_password()
    {
        await sut.CreateAsync("dave", "secret");
        Assert.Equal(PasswordVerificationResult.Success, await sut.VerifyPasswordAsync("dave", "secret"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_reports_not_found_for_unknown_user()
    {
        Assert.Equal(PasswordVerificationResult.NotFound, await sut.VerifyPasswordAsync("ghost", "x"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_locks_out_after_threshold_then_reports_locked()
    {
        options.MaxFailedAccessAttempts = 3;
        await sut.CreateAsync("erin", "right");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(PasswordVerificationResult.Failed, await sut.VerifyPasswordAsync("erin", "wrong"));
        }

        // Threshold reached: even the correct password is refused while locked out.
        Assert.Equal(PasswordVerificationResult.LockedOut, await sut.VerifyPasswordAsync("erin", "right"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_unlocks_after_lockout_window()
    {
        options.MaxFailedAccessAttempts = 1;
        options.LockoutDuration = TimeSpan.FromMinutes(10);
        await sut.CreateAsync("frank", "right");

        Assert.Equal(PasswordVerificationResult.Failed, await sut.VerifyPasswordAsync("frank", "wrong"));
        Assert.Equal(PasswordVerificationResult.LockedOut, await sut.VerifyPasswordAsync("frank", "right"));

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(PasswordVerificationResult.Success, await sut.VerifyPasswordAsync("frank", "right"));
    }

    [Fact]
    public async Task VerifyPasswordAsync_reports_inactive_for_disabled_account()
    {
        var create = await sut.CreateAsync("gina", "pw");
        await sut.SetActiveAsync(create.UserId!.Value, false);
        Assert.Equal(PasswordVerificationResult.Inactive, await sut.VerifyPasswordAsync("gina", "pw"));
    }

    [Fact]
    public async Task DeleteAsync_soft_deletes_so_lookup_no_longer_finds_the_user()
    {
        var create = await sut.CreateAsync("hank", "pw");
        Assert.True(await sut.DeleteAsync(create.UserId!.Value));
        Assert.Null(await sut.FindByUserNameAsync("hank"));
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test Themia.sln --filter UserServiceTests`
Expected: FAIL — `UserService` does not exist. (Add `Microsoft.Extensions.TimeProvider.Testing` to the test csproj `<ItemGroup>` if `FakeTimeProvider` is unresolved: `<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />`, and add a matching `<PackageVersion>` to `Directory.Packages.props` if absent — use the version already used by other Themia tests; grep `FakeTimeProvider` to confirm the package is registered.)

- [ ] **Step 5: Implement UserService**

`src/modules/Themia.Modules.Identity/Services/UserService.cs`:

```csharp
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IUserService"/> over the Themia data abstractions.</summary>
public sealed class UserService : IUserService
{
    private readonly IRepository<User, Guid> users;
    private readonly IUnitOfWork unitOfWork;
    private readonly IPasswordHasher passwordHasher;
    private readonly TimeProvider timeProvider;
    private readonly IdentityModuleOptions options;

    /// <summary>Creates the service.</summary>
    public UserService(
        IRepository<User, Guid> users,
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        TimeProvider timeProvider,
        IdentityModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(passwordHasher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        this.users = users;
        this.unitOfWork = unitOfWork;
        this.passwordHasher = passwordHasher;
        this.timeProvider = timeProvider;
        this.options = options;
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    /// <inheritdoc />
    public async Task<UserCreationResult> CreateAsync(string userName, string password, string? email = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var normalizedName = Normalize(userName);
        if (await users.AnyAsync(new UserByNormalizedNameSpec(normalizedName), cancellationToken).ConfigureAwait(false))
        {
            return UserCreationResult.Failure("duplicate_user_name");
        }

        string? normalizedEmail = null;
        if (!string.IsNullOrWhiteSpace(email))
        {
            normalizedEmail = Normalize(email);
            if (await users.AnyAsync(new UserByNormalizedEmailSpec(normalizedEmail), cancellationToken).ConfigureAwait(false))
            {
                return UserCreationResult.Failure("duplicate_email");
            }
        }

        var user = new User
        {
            UserName = userName,
            NormalizedUserName = normalizedName,
            Email = email,
            NormalizedEmail = normalizedEmail,
            PasswordHash = passwordHasher.Hash(password),
        };
        user.SetId(Guid.CreateVersion7());

        await users.AddAsync(user, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return UserCreationResult.Success(user.Id);
    }

    /// <inheritdoc />
    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        users.GetByIdAsync(id, cancellationToken);

    /// <inheritdoc />
    public async Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        var normalized = Normalize(userName);

        var inTenant = await users.FirstOrDefaultAsync(new UserByNormalizedNameSpec(normalized), cancellationToken).ConfigureAwait(false);
        if (inTenant is not null)
        {
            return inTenant;
        }

        if (!options.AllowPlatformLogin)
        {
            return null;
        }

        return await users.FirstOrDefaultAsync(new PlatformUserByNormalizedNameSpec(normalized), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var normalized = Normalize(email);

        var inTenant = await users.FirstOrDefaultAsync(new UserByNormalizedEmailSpec(normalized), cancellationToken).ConfigureAwait(false);
        if (inTenant is not null)
        {
            return inTenant;
        }

        if (!options.AllowPlatformLogin)
        {
            return null;
        }

        return await users.FirstOrDefaultAsync(new PlatformUserByNormalizedEmailSpec(normalized), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var user = await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }

        user.PasswordHash = passwordHasher.Hash(password);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        users.Update(user);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<PasswordVerificationResult> VerifyPasswordAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        var user = await FindByUserNameAsync(userName, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return PasswordVerificationResult.NotFound;
        }

        if (!user.IsActive)
        {
            return PasswordVerificationResult.Inactive;
        }

        var now = timeProvider.GetUtcNow();
        if (user.LockoutEnabled && user.LockoutEnd is { } end && end > now)
        {
            return PasswordVerificationResult.LockedOut;
        }

        if (user.PasswordHash is null || !passwordHasher.Verify(user.PasswordHash, password))
        {
            if (user.LockoutEnabled)
            {
                user.AccessFailedCount++;
                if (user.AccessFailedCount >= options.MaxFailedAccessAttempts)
                {
                    user.LockoutEnd = now.Add(options.LockoutDuration);
                    user.AccessFailedCount = 0;
                }
                users.Update(user);
                await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            return PasswordVerificationResult.Failed;
        }

        // Success: clear failure state and re-hash if the cost parameters changed.
        var changed = false;
        if (user.AccessFailedCount != 0 || user.LockoutEnd is not null)
        {
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
            changed = true;
        }
        if (passwordHasher.NeedsRehash(user.PasswordHash))
        {
            user.PasswordHash = passwordHasher.Hash(password);
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            changed = true;
        }
        if (changed)
        {
            users.Update(user);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return PasswordVerificationResult.Success;
    }

    /// <inheritdoc />
    public async Task<bool> SetActiveAsync(Guid userId, bool isActive, CancellationToken cancellationToken = default)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }

        user.IsActive = isActive;
        users.Update(user);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }

        users.Remove(user);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Themia.sln --filter UserServiceTests`
Expected: PASS (12 tests).

- [ ] **Step 7: Build clean, update PublicAPI, commit**

Run: `dotnet build Themia.sln --no-incremental`, add `RS0016` members for `UserService` to the impl `PublicAPI.Unshipped.txt` (the internal spec classes do not appear).

```bash
git add src/modules/Themia.Modules.Identity tests/Themia.Modules.Identity.Tests
git commit -m "feat(identity): UserService — create/find/verify with lockout"
```

---

## Task 6: RoleService

**Files:**
- Modify: `src/modules/Themia.Modules.Identity/Specifications/IdentitySpecs.cs` (add role + membership specs)
- Create: `src/modules/Themia.Modules.Identity/Services/RoleService.cs`
- Test: `tests/Themia.Modules.Identity.Tests/Services/RoleServiceTests.cs`

- [ ] **Step 1: Add role specifications**

Append to `src/modules/Themia.Modules.Identity/Specifications/IdentitySpecs.cs`:

```csharp
/// <summary>Finds a role by normalized name within the ambient tenant.</summary>
internal sealed class RoleByNormalizedNameSpec : Specification<Role>
{
    public RoleByNormalizedNameSpec(string normalizedName) =>
        Where(r => r.NormalizedName == normalizedName);
}

/// <summary>Finds a platform (global) role by normalized name, bypassing the tenant filter.</summary>
internal sealed class PlatformRoleByNormalizedNameSpec : Specification<Role>
{
    public PlatformRoleByNormalizedNameSpec(string normalizedName)
    {
        Where(r => r.NormalizedName == normalizedName && r.TenantId == null);
        WithoutTenantFilter();
    }
}

/// <summary>All membership rows for a user.</summary>
internal sealed class UserRolesByUserSpec : Specification<UserRole>
{
    public UserRolesByUserSpec(Guid userId) => Where(ur => ur.UserId == userId);
}

/// <summary>A specific user–role membership row.</summary>
internal sealed class UserRoleSpec : Specification<UserRole>
{
    public UserRoleSpec(Guid userId, Guid roleId) => Where(ur => ur.UserId == userId && ur.RoleId == roleId);
}
```

> Note: `UserRole` has no `TenantId` (it is parent-keyed), so the tenant filter does not apply to it; these specs do not need `WithoutTenantFilter()`. Isolation is enforced because `RoleService` resolves the user and role through the tenant-scoped `IRepository<User>`/`IRepository<Role>` first.

- [ ] **Step 2: Write the failing tests**

`tests/Themia.Modules.Identity.Tests/Services/RoleServiceTests.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class RoleServiceTests
{
    private readonly List<User> users = [];
    private readonly List<Role> roles = [];
    private readonly List<UserRole> memberships = [];
    private readonly FakeRepository<User> userRepo;
    private readonly FakeRepository<Role> roleRepo;
    private readonly FakeRepository<UserRole> membershipRepo;
    private readonly FakeUnitOfWork uow = new();
    private readonly RoleService sut;

    public RoleServiceTests()
    {
        var tenant = new TenantId("acme");
        userRepo = new FakeRepository<User>(users, u => u.Id) { AmbientTenant = tenant };
        roleRepo = new FakeRepository<Role>(roles, r => r.Id) { AmbientTenant = tenant };
        membershipRepo = new FakeRepository<UserRole>(memberships, ur => ur.Id) { AmbientTenant = tenant };
        sut = new RoleService(userRepo, roleRepo, membershipRepo, uow);
    }

    private Guid SeedUser()
    {
        var u = new User { UserName = "u", NormalizedUserName = "U", TenantId = new TenantId("acme") };
        u.SetId(Guid.NewGuid());
        users.Add(u);
        return u.Id;
    }

    [Fact]
    public async Task CreateAsync_creates_role_and_rejects_duplicate()
    {
        var id = await sut.CreateAsync("Admin", "Administrators");
        Assert.NotNull(id);
        Assert.Equal("ADMIN", Assert.Single(roles).NormalizedName);

        Assert.Null(await sut.CreateAsync("admin"));   // duplicate in scope
    }

    [Fact]
    public async Task AssignRoleAsync_then_GetRoleIds_reflects_membership()
    {
        var userId = SeedUser();
        var roleId = (await sut.CreateAsync("Editor"))!.Value;

        Assert.True(await sut.AssignRoleAsync(userId, roleId));
        Assert.Contains(roleId, await sut.GetRoleIdsAsync(userId));
    }

    [Fact]
    public async Task AssignRoleAsync_is_idempotent()
    {
        var userId = SeedUser();
        var roleId = (await sut.CreateAsync("Editor"))!.Value;

        Assert.True(await sut.AssignRoleAsync(userId, roleId));
        Assert.True(await sut.AssignRoleAsync(userId, roleId));
        Assert.Single(await sut.GetRoleIdsAsync(userId));
    }

    [Fact]
    public async Task AssignRoleAsync_fails_when_user_not_in_scope()
    {
        var roleId = (await sut.CreateAsync("Editor"))!.Value;
        Assert.False(await sut.AssignRoleAsync(Guid.NewGuid(), roleId));
    }

    [Fact]
    public async Task RemoveRoleAsync_removes_membership()
    {
        var userId = SeedUser();
        var roleId = (await sut.CreateAsync("Editor"))!.Value;
        await sut.AssignRoleAsync(userId, roleId);

        Assert.True(await sut.RemoveRoleAsync(userId, roleId));
        Assert.Empty(await sut.GetRoleIdsAsync(userId));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Themia.sln --filter RoleServiceTests`
Expected: FAIL — `RoleService` does not exist.

- [ ] **Step 4: Implement RoleService**

`src/modules/Themia.Modules.Identity/Services/RoleService.cs`:

```csharp
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IRoleService"/> over the Themia data abstractions.</summary>
public sealed class RoleService : IRoleService
{
    private readonly IRepository<User, Guid> users;
    private readonly IRepository<Role, Guid> roles;
    private readonly IRepository<UserRole, Guid> memberships;
    private readonly IUnitOfWork unitOfWork;

    /// <summary>Creates the service.</summary>
    public RoleService(
        IRepository<User, Guid> users,
        IRepository<Role, Guid> roles,
        IRepository<UserRole, Guid> memberships,
        IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        this.users = users;
        this.roles = roles;
        this.memberships = memberships;
        this.unitOfWork = unitOfWork;
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    /// <inheritdoc />
    public async Task<Guid?> CreateAsync(string name, string? description = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = Normalize(name);
        if (await roles.AnyAsync(new RoleByNormalizedNameSpec(normalized), cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var role = new Role { Name = name, NormalizedName = normalized, Description = description };
        role.SetId(Guid.CreateVersion7());
        await roles.AddAsync(role, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return role.Id;
    }

    /// <inheritdoc />
    public async Task<Role?> FindByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = Normalize(name);
        var inTenant = await roles.FirstOrDefaultAsync(new RoleByNormalizedNameSpec(normalized), cancellationToken).ConfigureAwait(false);
        return inTenant ?? await roles.FirstOrDefaultAsync(new PlatformRoleByNormalizedNameSpec(normalized), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> AssignRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default)
    {
        // Both sides must resolve within the ambient tenant scope (tenant isolation for the parent-keyed join).
        if (await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }
        if (await roles.GetByIdAsync(roleId, cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        var existing = await memberships.FirstOrDefaultAsync(new UserRoleSpec(userId, roleId), cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return true;   // idempotent
        }

        var membership = new UserRole { UserId = userId, RoleId = roleId };
        membership.SetId(Guid.CreateVersion7());
        await memberships.AddAsync(membership, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default)
    {
        var existing = await memberships.FirstOrDefaultAsync(new UserRoleSpec(userId, roleId), cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        memberships.Remove(existing);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetRoleIdsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var rows = await memberships.ListAsync(new UserRolesByUserSpec(userId), cancellationToken).ConfigureAwait(false);
        return rows.Select(r => r.RoleId).ToList();
    }
}
```

> `IRepository<UserRole, Guid>` keys on `UserRole.Id` (the surrogate added in Task 2), so it works uniformly through the generic EF/Dapper repositories. The unique index on `(user_id, role_id)` (Task 11 migration) enforces no-duplicate-membership at the DB level; `AssignRoleAsync`'s spec check is the friendly pre-check.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Themia.sln --filter RoleServiceTests`
Expected: PASS (5 tests).

- [ ] **Step 6: Build clean, update PublicAPI, commit**

```bash
git add src/modules/Themia.Modules.Identity tests/Themia.Modules.Identity.Tests
git commit -m "feat(identity): RoleService — create/assign/remove roles"
```

---

## Task 7: ClaimService (incl. effective-claims union)

**Files:**
- Modify: `src/modules/Themia.Modules.Identity/Specifications/IdentitySpecs.cs` (claim specs)
- Create: `src/modules/Themia.Modules.Identity/Services/ClaimService.cs`
- Test: `tests/Themia.Modules.Identity.Tests/Services/ClaimServiceTests.cs`

- [ ] **Step 1: Add claim specifications**

Append to `IdentitySpecs.cs`:

```csharp
/// <summary>All direct claims of a user.</summary>
internal sealed class UserClaimsByUserSpec : Specification<UserClaim>
{
    public UserClaimsByUserSpec(Guid userId) => Where(c => c.UserId == userId);
}

/// <summary>A specific user-claim row (for removal).</summary>
internal sealed class UserClaimMatchSpec : Specification<UserClaim>
{
    public UserClaimMatchSpec(Guid userId, string claimType, string claimValue) =>
        Where(c => c.UserId == userId && c.ClaimType == claimType && c.ClaimValue == claimValue);
}

/// <summary>All claims belonging to any of the given roles.</summary>
internal sealed class RoleClaimsByRoleIdsSpec : Specification<RoleClaim>
{
    public RoleClaimsByRoleIdsSpec(IReadOnlyCollection<Guid> roleIds) =>
        Where(c => roleIds.Contains(c.RoleId));
}
```

- [ ] **Step 2: Write the failing tests**

`tests/Themia.Modules.Identity.Tests/Services/ClaimServiceTests.cs`:

```csharp
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class ClaimServiceTests
{
    private readonly List<UserClaim> userClaims = [];
    private readonly List<RoleClaim> roleClaims = [];
    private readonly List<UserRole> memberships = [];
    private readonly ClaimService sut;
    private readonly Guid userId = Guid.NewGuid();
    private readonly Guid roleId = Guid.NewGuid();

    public ClaimServiceTests()
    {
        var tenant = new TenantId("acme");
        sut = new ClaimService(
            new FakeRepository<UserClaim>(userClaims, c => c.Id) { AmbientTenant = tenant },
            new FakeRepository<RoleClaim>(roleClaims, c => c.Id) { AmbientTenant = tenant },
            new FakeRepository<UserRole>(memberships, ur => ur.Id) { AmbientTenant = tenant },
            new FakeUnitOfWork());
    }

    [Fact]
    public async Task AddUserClaim_then_GetEffectiveClaims_includes_it()
    {
        await sut.AddUserClaimAsync(userId, "perm", "read");
        var claims = await sut.GetEffectiveClaimsAsync(userId);
        Assert.Contains(claims, c => c is { Type: "perm", Value: "read" });
    }

    [Fact]
    public async Task GetEffectiveClaims_unions_user_and_role_claims_distinctly()
    {
        // user directly has perm:read; the user's role has perm:read (dup) and perm:write.
        await sut.AddUserClaimAsync(userId, "perm", "read");
        memberships.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = roleId });
        await sut.AddRoleClaimAsync(roleId, "perm", "read");
        await sut.AddRoleClaimAsync(roleId, "perm", "write");

        var claims = await sut.GetEffectiveClaimsAsync(userId);

        Assert.Equal(2, claims.Count);   // read (deduped) + write
        Assert.Contains(claims, c => c.Value == "read");
        Assert.Contains(claims, c => c.Value == "write");
    }

    [Fact]
    public async Task RemoveUserClaim_removes_only_the_matching_claim()
    {
        await sut.AddUserClaimAsync(userId, "perm", "read");
        await sut.AddUserClaimAsync(userId, "perm", "write");

        Assert.True(await sut.RemoveUserClaimAsync(userId, "perm", "read"));

        var claims = await sut.GetEffectiveClaimsAsync(userId);
        Assert.DoesNotContain(claims, c => c.Value == "read");
        Assert.Contains(claims, c => c.Value == "write");
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Themia.sln --filter ClaimServiceTests`
Expected: FAIL — `ClaimService` does not exist.

- [ ] **Step 4: Implement ClaimService**

`src/modules/Themia.Modules.Identity/Services/ClaimService.cs`:

```csharp
using System.Security.Claims;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IClaimService"/> over the Themia data abstractions.</summary>
public sealed class ClaimService : IClaimService
{
    private readonly IRepository<UserClaim, Guid> userClaims;
    private readonly IRepository<RoleClaim, Guid> roleClaims;
    private readonly IRepository<UserRole, Guid> memberships;
    private readonly IUnitOfWork unitOfWork;

    /// <summary>Creates the service.</summary>
    public ClaimService(
        IRepository<UserClaim, Guid> userClaims,
        IRepository<RoleClaim, Guid> roleClaims,
        IRepository<UserRole, Guid> memberships,
        IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(userClaims);
        ArgumentNullException.ThrowIfNull(roleClaims);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        this.userClaims = userClaims;
        this.roleClaims = roleClaims;
        this.memberships = memberships;
        this.unitOfWork = unitOfWork;
    }

    /// <inheritdoc />
    public async Task AddUserClaimAsync(Guid userId, string claimType, string claimValue, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);
        ArgumentNullException.ThrowIfNull(claimValue);

        var claim = new UserClaim { UserId = userId, ClaimType = claimType, ClaimValue = claimValue };
        claim.SetId(Guid.CreateVersion7());
        await userClaims.AddAsync(claim, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveUserClaimAsync(Guid userId, string claimType, string claimValue, CancellationToken cancellationToken = default)
    {
        var existing = await userClaims.FirstOrDefaultAsync(new UserClaimMatchSpec(userId, claimType, claimValue), cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        userClaims.Remove(existing);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task AddRoleClaimAsync(Guid roleId, string claimType, string claimValue, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimType);
        ArgumentNullException.ThrowIfNull(claimValue);

        var claim = new RoleClaim { RoleId = roleId, ClaimType = claimType, ClaimValue = claimValue };
        claim.SetId(Guid.CreateVersion7());
        await roleClaims.AddAsync(claim, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Claim>> GetEffectiveClaimsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var direct = await userClaims.ListAsync(new UserClaimsByUserSpec(userId), cancellationToken).ConfigureAwait(false);

        var roleIds = (await memberships.ListAsync(new Specifications.UserRolesByUserSpec(userId), cancellationToken).ConfigureAwait(false))
            .Select(m => m.RoleId)
            .ToList();

        var fromRoles = roleIds.Count == 0
            ? []
            : await roleClaims.ListAsync(new RoleClaimsByRoleIdsSpec(roleIds), cancellationToken).ConfigureAwait(false);

        // Union by (type, value); deduplicate so a claim granted both directly and via a role appears once.
        var seen = new HashSet<(string, string)>();
        var result = new List<Claim>();
        foreach (var (type, value) in direct.Select(c => (c.ClaimType, c.ClaimValue))
                     .Concat(fromRoles.Select(c => (c.ClaimType, c.ClaimValue))))
        {
            if (seen.Add((type, value)))
            {
                result.Add(new Claim(type, value));
            }
        }
        return result;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Themia.sln --filter ClaimServiceTests`
Expected: PASS (3 tests).

- [ ] **Step 6: Build clean, update PublicAPI, commit**

```bash
git add src/modules/Themia.Modules.Identity tests/Themia.Modules.Identity.Tests
git commit -m "feat(identity): ClaimService with effective-claims union"
```

---

## Task 8: UserTokenService

**Files:**
- Modify: `src/modules/Themia.Modules.Identity/Specifications/IdentitySpecs.cs` (token spec)
- Create: `src/modules/Themia.Modules.Identity/Services/UserTokenService.cs`
- Test: `tests/Themia.Modules.Identity.Tests/Services/UserTokenServiceTests.cs`

- [ ] **Step 1: Add the token spec**

Append to `IdentitySpecs.cs`:

```csharp
/// <summary>All tokens for a user and purpose (consumed and unconsumed).</summary>
internal sealed class TokensByUserAndPurposeSpec : Specification<UserToken>
{
    public TokensByUserAndPurposeSpec(Guid userId, TokenPurpose purpose) =>
        Where(t => t.UserId == userId && t.Purpose == purpose);
}
```

- [ ] **Step 2: Write the failing tests**

`tests/Themia.Modules.Identity.Tests/Services/UserTokenServiceTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class UserTokenServiceTests
{
    private readonly List<UserToken> tokens = [];
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-06-14T00:00:00Z"));
    private readonly IdentityModuleOptions options = new();
    private readonly UserTokenService sut;
    private readonly Guid userId = Guid.NewGuid();

    public UserTokenServiceTests()
    {
        var repo = new FakeRepository<UserToken>(tokens, t => t.Id) { AmbientTenant = new TenantId("acme") };
        sut = new UserTokenService(repo, new FakeUnitOfWork(), clock, options);
    }

    [Fact]
    public async Task Generate_returns_raw_token_and_stores_only_its_hash()
    {
        var raw = await sut.GenerateAsync(userId, TokenPurpose.PasswordReset);

        Assert.False(string.IsNullOrWhiteSpace(raw));
        var stored = Assert.Single(tokens);
        Assert.NotEqual(raw, stored.TokenHash);     // hash, not raw
        Assert.Null(stored.ConsumedAt);
    }

    [Fact]
    public async Task Consume_succeeds_once_then_reports_already_consumed()
    {
        var raw = await sut.GenerateAsync(userId, TokenPurpose.EmailConfirm);

        Assert.Equal(TokenConsumeResult.Success, await sut.ConsumeAsync(userId, TokenPurpose.EmailConfirm, raw));
        Assert.Equal(TokenConsumeResult.AlreadyConsumed, await sut.ConsumeAsync(userId, TokenPurpose.EmailConfirm, raw));
    }

    [Fact]
    public async Task Consume_reports_not_found_for_wrong_token()
    {
        await sut.GenerateAsync(userId, TokenPurpose.EmailConfirm);
        Assert.Equal(TokenConsumeResult.NotFound, await sut.ConsumeAsync(userId, TokenPurpose.EmailConfirm, "bogus"));
    }

    [Fact]
    public async Task Consume_reports_expired_after_lifetime()
    {
        var raw = await sut.GenerateAsync(userId, TokenPurpose.PasswordReset, TimeSpan.FromMinutes(5));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(TokenConsumeResult.Expired, await sut.ConsumeAsync(userId, TokenPurpose.PasswordReset, raw));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Themia.sln --filter UserTokenServiceTests`
Expected: FAIL — `UserTokenService` does not exist.

- [ ] **Step 4: Implement UserTokenService**

`src/modules/Themia.Modules.Identity/Services/UserTokenService.cs`:

```csharp
using System.Security.Cryptography;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Hashing;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>Default <see cref="IUserTokenService"/>. Persists only token hashes; raw tokens are returned once.</summary>
public sealed class UserTokenService : IUserTokenService
{
    private const int TokenByteLength = 32;

    private readonly IRepository<UserToken, Guid> tokens;
    private readonly IUnitOfWork unitOfWork;
    private readonly TimeProvider timeProvider;
    private readonly IdentityModuleOptions options;

    /// <summary>Creates the service.</summary>
    public UserTokenService(
        IRepository<UserToken, Guid> tokens,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        IdentityModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        this.tokens = tokens;
        this.unitOfWork = unitOfWork;
        this.timeProvider = timeProvider;
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<string> GenerateAsync(Guid userId, TokenPurpose purpose, TimeSpan? lifetime = null, CancellationToken cancellationToken = default)
    {
        var raw = Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenByteLength));
        var token = new UserToken
        {
            UserId = userId,
            Purpose = purpose,
            TokenHash = TokenHasher.Hash(raw),
            ExpiresAt = timeProvider.GetUtcNow().Add(lifetime ?? options.DefaultTokenLifetime),
        };
        token.SetId(Guid.CreateVersion7());

        await tokens.AddAsync(token, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return raw;
    }

    /// <inheritdoc />
    public async Task<TokenConsumeResult> ConsumeAsync(Guid userId, TokenPurpose purpose, string rawToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);

        var candidates = await tokens.ListAsync(new TokensByUserAndPurposeSpec(userId, purpose), cancellationToken).ConfigureAwait(false);
        var match = candidates.FirstOrDefault(t => TokenHasher.Matches(t.TokenHash, rawToken));
        if (match is null)
        {
            return TokenConsumeResult.NotFound;
        }

        if (match.ConsumedAt is not null)
        {
            return TokenConsumeResult.AlreadyConsumed;
        }

        if (match.ExpiresAt <= timeProvider.GetUtcNow())
        {
            return TokenConsumeResult.Expired;
        }

        match.ConsumedAt = timeProvider.GetUtcNow();
        tokens.Update(match);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return TokenConsumeResult.Success;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Themia.sln --filter UserTokenServiceTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Build clean, update PublicAPI, commit**

```bash
git add src/modules/Themia.Modules.Identity tests/Themia.Modules.Identity.Tests
git commit -m "feat(identity): UserTokenService — single-use expiring tokens"
```

---

## Task 9: ClaimsPrincipalFactory + IdentityClaimTypes

**Files:**
- Modify: `src/modules/Themia.Modules.Identity/Specifications/IdentitySpecs.cs` (roles-by-ids spec)
- Create: `src/modules/Themia.Modules.Identity/Principal/IdentityClaimTypes.cs`
- Create: `src/modules/Themia.Modules.Identity/Principal/ClaimsPrincipalFactory.cs`
- Test: `tests/Themia.Modules.Identity.Tests/Principal/ClaimsPrincipalFactoryTests.cs`

- [ ] **Step 1: Add the roles-by-ids spec**

Append to `IdentitySpecs.cs`:

```csharp
/// <summary>All roles whose id is in the given set.</summary>
internal sealed class RolesByIdsSpec : Specification<Role>
{
    public RolesByIdsSpec(IReadOnlyCollection<Guid> roleIds) => Where(r => roleIds.Contains(r.Id));
}
```

- [ ] **Step 2: Write the failing tests**

`tests/Themia.Modules.Identity.Tests/Principal/ClaimsPrincipalFactoryTests.cs`:

```csharp
using System.Security.Claims;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Principal;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Principal;

public class ClaimsPrincipalFactoryTests
{
    private readonly List<Role> roles = [];
    private readonly List<UserRole> memberships = [];
    private readonly List<UserClaim> userClaims = [];
    private readonly List<RoleClaim> roleClaims = [];
    private readonly ClaimsPrincipalFactory sut;

    public ClaimsPrincipalFactoryTests()
    {
        var tenant = new TenantId("acme");
        var claimService = new ClaimService(
            new FakeRepository<UserClaim>(userClaims, c => c.Id) { AmbientTenant = tenant },
            new FakeRepository<RoleClaim>(roleClaims, c => c.Id) { AmbientTenant = tenant },
            new FakeRepository<UserRole>(memberships, ur => ur.Id) { AmbientTenant = tenant },
            new FakeUnitOfWork());

        sut = new ClaimsPrincipalFactory(
            new FakeRepository<UserRole>(memberships, ur => ur.Id) { AmbientTenant = tenant },
            new FakeRepository<Role>(roles, r => r.Id) { AmbientTenant = tenant },
            claimService);
    }

    private User MakeUser(TenantId? tenant)
    {
        var user = new User { UserName = "alice", NormalizedUserName = "ALICE", TenantId = tenant };
        user.SetId(Guid.NewGuid());
        return user;
    }

    [Fact]
    public async Task Creates_principal_with_subject_name_role_and_effective_claims()
    {
        var user = MakeUser(new TenantId("acme"));
        var roleId = Guid.NewGuid();
        roles.Add(new Role { Id = roleId, Name = "Admin", NormalizedName = "ADMIN", TenantId = new TenantId("acme") });
        memberships.Add(new UserRole { Id = Guid.NewGuid(), UserId = user.Id, RoleId = roleId });
        roleClaims.Add(new RoleClaim { Id = Guid.NewGuid(), RoleId = roleId, ClaimType = "perm", ClaimValue = "write" });

        var principal = await sut.CreateAsync(user, "Identity.Application");

        Assert.Equal(user.Id.ToString(), principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
        Assert.Equal("alice", principal.FindFirst(ClaimTypes.Name)!.Value);
        Assert.True(principal.IsInRole("Admin"));
        Assert.Equal("acme", principal.FindFirst(IdentityClaimTypes.TenantId)!.Value);
        Assert.Contains(principal.Claims, c => c is { Type: "perm", Value: "write" });
        Assert.True(principal.Identity!.IsAuthenticated);
    }

    [Fact]
    public async Task Platform_user_has_no_tenant_claim()
    {
        var principal = await sut.CreateAsync(MakeUser(null), "Identity.Application");
        Assert.Null(principal.FindFirst(IdentityClaimTypes.TenantId));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test Themia.sln --filter ClaimsPrincipalFactoryTests`
Expected: FAIL — `ClaimsPrincipalFactory` / `IdentityClaimTypes` do not exist.

- [ ] **Step 4: Implement IdentityClaimTypes and ClaimsPrincipalFactory**

`src/modules/Themia.Modules.Identity/Principal/IdentityClaimTypes.cs`:

```csharp
namespace Themia.Modules.Identity.Principal;

/// <summary>Themia-specific claim type URIs added to the principal alongside the standard <see cref="System.Security.Claims.ClaimTypes"/>.</summary>
public static class IdentityClaimTypes
{
    /// <summary>The user's tenant id. Absent for platform users.</summary>
    public const string TenantId = "themia:tenant_id";

    /// <summary>The user's security stamp, used to invalidate stale principals when credentials change.</summary>
    public const string SecurityStamp = "themia:security_stamp";
}
```

`src/modules/Themia.Modules.Identity/Principal/ClaimsPrincipalFactory.cs`:

```csharp
using System.Security.Claims;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Principal;

/// <summary>Default <see cref="IClaimsPrincipalFactory"/>. The single source of truth for principal contents.</summary>
public sealed class ClaimsPrincipalFactory : IClaimsPrincipalFactory
{
    private readonly IRepository<UserRole, Guid> memberships;
    private readonly IRepository<Role, Guid> roles;
    private readonly IClaimService claims;

    /// <summary>Creates the factory.</summary>
    public ClaimsPrincipalFactory(
        IRepository<UserRole, Guid> memberships,
        IRepository<Role, Guid> roles,
        IClaimService claims)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(claims);
        this.memberships = memberships;
        this.roles = roles;
        this.claims = claims;
    }

    /// <inheritdoc />
    public async Task<ClaimsPrincipal> CreateAsync(User user, string authenticationType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationType);

        // ClaimTypes.Role + ClaimTypes.NameIdentifier so [Authorize(Roles=...)] and User identity work out of the box.
        var identity = new ClaimsIdentity(authenticationType, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.UserName));
        identity.AddClaim(new Claim(IdentityClaimTypes.SecurityStamp, user.SecurityStamp));

        if (user.TenantId is { } tenant)
        {
            identity.AddClaim(new Claim(IdentityClaimTypes.TenantId, tenant.Value));
        }

        var roleIds = (await memberships.ListAsync(new UserRolesByUserSpec(user.Id), cancellationToken).ConfigureAwait(false))
            .Select(m => m.RoleId)
            .ToList();
        if (roleIds.Count > 0)
        {
            var roleRows = await roles.ListAsync(new RolesByIdsSpec(roleIds), cancellationToken).ConfigureAwait(false);
            foreach (var role in roleRows)
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role.Name));
            }
        }

        foreach (var claim in await claims.GetEffectiveClaimsAsync(user.Id, cancellationToken).ConfigureAwait(false))
        {
            identity.AddClaim(claim);
        }

        return new ClaimsPrincipal(identity);
    }
}
```

> `TenantId.Value` is the underlying string (confirmed in `TenantId.cs`). If the property name differs, adjust.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Themia.sln --filter ClaimsPrincipalFactoryTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Build clean, update PublicAPI, commit**

```bash
git add src/modules/Themia.Modules.Identity tests/Themia.Modules.Identity.Tests
git commit -m "feat(identity): ClaimsPrincipalFactory + IdentityClaimTypes"
```

---

## Task 10: Current-user principal + accessors

**Files:**
- Create: `src/modules/Themia.Modules.Identity/Principal/CurrentUser.cs`
- Create: `src/modules/Themia.Modules.Identity/Principal/IdentityCurrentUserAccessor.cs`
- Test: `tests/Themia.Modules.Identity.Tests/Principal/CurrentUserTests.cs`

**Design notes:** `CurrentUser` reads the ambient `ClaimsPrincipal` from `IHttpContextAccessor`. `IdentityCurrentUserAccessor` implements the framework's `ICurrentUserAccessor` (audit stamping) by returning that principal's subject id — replacing the `NullCurrentUserAccessor` so `created_by`/`modified_by` reflect the real user.

- [ ] **Step 1: Write the failing tests**

`tests/Themia.Modules.Identity.Tests/Principal/CurrentUserTests.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Themia.Modules.Identity.Principal;
using Xunit;

namespace Themia.Modules.Identity.Tests.Principal;

public class CurrentUserTests
{
    private static IHttpContextAccessor Accessor(ClaimsPrincipal? user)
    {
        var ctx = new DefaultHttpContext();
        if (user is not null)
        {
            ctx.User = user;
        }
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static ClaimsPrincipal Authenticated(Guid id, string? tenant, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, id.ToString()),
            new(ClaimTypes.Name, "alice"),
        };
        if (tenant is not null)
        {
            claims.Add(new Claim(IdentityClaimTypes.TenantId, tenant));
        }
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.Name, ClaimTypes.Role));
    }

    [Fact]
    public void Unauthenticated_when_no_user()
    {
        var sut = new CurrentUser(Accessor(null));
        Assert.False(sut.IsAuthenticated);
        Assert.Null(sut.UserId);
    }

    [Fact]
    public void Reads_tenant_user_identity_and_roles()
    {
        var id = Guid.NewGuid();
        var sut = new CurrentUser(Accessor(Authenticated(id, "acme", "Admin", "Editor")));

        Assert.True(sut.IsAuthenticated);
        Assert.Equal(id, sut.UserId);
        Assert.Equal("acme", sut.TenantId);
        Assert.False(sut.IsPlatform);
        Assert.True(sut.IsInRole("Admin"));
        Assert.Contains("Editor", sut.Roles);
    }

    [Fact]
    public void Platform_user_has_null_tenant_and_is_platform()
    {
        var sut = new CurrentUser(Accessor(Authenticated(Guid.NewGuid(), tenant: null)));
        Assert.True(sut.IsAuthenticated);
        Assert.Null(sut.TenantId);
        Assert.True(sut.IsPlatform);
    }

    [Fact]
    public void Audit_accessor_returns_subject_id_string()
    {
        var id = Guid.NewGuid();
        var accessor = new IdentityCurrentUserAccessor(Accessor(Authenticated(id, "acme")));
        Assert.Equal(id.ToString(), accessor.UserId);
    }

    [Fact]
    public void Audit_accessor_returns_null_when_unauthenticated()
    {
        var accessor = new IdentityCurrentUserAccessor(Accessor(null));
        Assert.Null(accessor.UserId);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Themia.sln --filter CurrentUserTests`
Expected: FAIL — `CurrentUser` / `IdentityCurrentUserAccessor` do not exist.

- [ ] **Step 3: Implement CurrentUser and IdentityCurrentUserAccessor**

`src/modules/Themia.Modules.Identity/Principal/CurrentUser.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Themia.Modules.Identity.Abstractions;

namespace Themia.Modules.Identity.Principal;

/// <summary>Default <see cref="ICurrentUser"/> reading the ambient principal from the current <see cref="HttpContext"/>.</summary>
public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>Creates the accessor.</summary>
    public CurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        this.httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    /// <inheritdoc />
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    /// <inheritdoc />
    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <inheritdoc />
    public string? TenantId => Principal?.FindFirst(IdentityClaimTypes.TenantId)?.Value;

    /// <inheritdoc />
    public bool IsPlatform => IsAuthenticated && TenantId is null;

    /// <inheritdoc />
    public string? UserName => Principal?.FindFirst(ClaimTypes.Name)?.Value;

    /// <inheritdoc />
    public IReadOnlyCollection<string> Roles =>
        Principal?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray() ?? [];

    /// <inheritdoc />
    public IReadOnlyCollection<Claim> Claims => Principal?.Claims.ToArray() ?? [];

    /// <inheritdoc />
    public bool IsInRole(string role) => Principal?.IsInRole(role) == true;
}
```

`src/modules/Themia.Modules.Identity/Principal/IdentityCurrentUserAccessor.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Themia.Framework.Data.Abstractions.Auditing;

namespace Themia.Modules.Identity.Principal;

/// <summary>Supplies the audit user id (<see cref="ICurrentUserAccessor"/>) from the authenticated principal, replacing the framework's null default.</summary>
public sealed class IdentityCurrentUserAccessor : ICurrentUserAccessor
{
    private readonly IHttpContextAccessor httpContextAccessor;

    /// <summary>Creates the accessor.</summary>
    public IdentityCurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        this.httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public string? UserId =>
        httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Themia.sln --filter CurrentUserTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Build clean, update PublicAPI, commit**

```bash
git add src/modules/Themia.Modules.Identity tests/Themia.Modules.Identity.Tests
git commit -m "feat(identity): current-user principal + audit accessor"
```

---

## Task 11: FluentMigrator schema (`identity` tables + filtered unique indexes)

**Files:**
- Create: `src/modules/Themia.Modules.Identity/Migrations/IdentitySchemaMigration.cs`

**Design notes:** Mirrors `SchedulingSchemaMigration` — `IfDatabase("postgres","sqlserver").Delegate(...)` for the table DDL, with a not-supported guard for other engines. The two-filtered-unique-indexes-per-table requirement is emitted with `Execute.Sql` (FluentMigrator's fluent index builder has no portable filtered-index API); the `CREATE UNIQUE INDEX ... WHERE ...` syntax is identical on PostgreSQL and SQL Server. Child tables (`user_roles`, `user_claims`, `role_claims`, `user_tokens`) are plain POCOs — no audit/soft-delete columns. **This task's DB verification is the integration apply test in Task 15** (a migration cannot be unit-tested without a database); locally, only `dotnet build` (it compiles) is checked here.

- [ ] **Step 1: Implement the migration**

`src/modules/Themia.Modules.Identity/Migrations/IdentitySchemaMigration.cs`:

```csharp
using System;
using FluentMigrator;

namespace Themia.Modules.Identity.Migrations;

/// <summary>Creates the <c>identity</c> schema and tables (users, roles, memberships, claims, tokens) with per-tenant + platform filtered unique indexes, on PostgreSQL and SQL Server.</summary>
[Migration(202606140001, "Themia.Identity: create identity schema and tables")]
public sealed class IdentitySchemaMigration : Migration
{
    private const string SchemaName = "identity";

    /// <inheritdoc />
    public override void Up()
    {
        IfDatabase("postgres", "sqlserver").Delegate(CreateSchemaAndTables);
        IfDatabase("postgres", "sqlserver").Delegate(CreateFilteredIndexes);

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Identity supports only PostgreSQL and SQL Server. The active database provider " +
                "is not supported; add a migration branch for it."));
    }

    private void CreateSchemaAndTables()
    {
        if (!Schema.Schema(SchemaName).Exists())
        {
            Create.Schema(SchemaName);
        }

        Create.Table("users").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("user_name").AsString(256).NotNullable()
            .WithColumn("normalized_user_name").AsString(256).NotNullable()
            .WithColumn("email").AsString(256).Nullable()
            .WithColumn("normalized_email").AsString(256).Nullable()
            .WithColumn("email_confirmed").AsBoolean().NotNullable()
            .WithColumn("phone_number").AsString(64).Nullable()
            .WithColumn("phone_number_confirmed").AsBoolean().NotNullable()
            .WithColumn("password_hash").AsString(1024).Nullable()
            .WithColumn("security_stamp").AsString(128).NotNullable()
            .WithColumn("is_active").AsBoolean().NotNullable()
            .WithColumn("access_failed_count").AsInt32().NotNullable()
            .WithColumn("lockout_end").AsDateTimeOffset().Nullable()
            .WithColumn("lockout_enabled").AsBoolean().NotNullable()
            .WithColumn("two_factor_enabled").AsBoolean().NotNullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_by").AsString(100).Nullable()
            .WithColumn("last_modified_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_modified_by").AsString(100).Nullable()
            .WithColumn("is_deleted").AsBoolean().NotNullable()
            .WithColumn("deleted_at").AsDateTimeOffset().Nullable()
            .WithColumn("deleted_by").AsString(100).Nullable()
            .WithColumn("restored_at").AsDateTimeOffset().Nullable()
            .WithColumn("restored_by").AsString(100).Nullable();

        Create.Table("roles").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("tenant_id").AsString(100).Nullable()
            .WithColumn("name").AsString(256).NotNullable()
            .WithColumn("normalized_name").AsString(256).NotNullable()
            .WithColumn("description").AsString(512).Nullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable()
            .WithColumn("created_by").AsString(100).Nullable()
            .WithColumn("last_modified_at").AsDateTimeOffset().Nullable()
            .WithColumn("last_modified_by").AsString(100).Nullable()
            .WithColumn("is_deleted").AsBoolean().NotNullable()
            .WithColumn("deleted_at").AsDateTimeOffset().Nullable()
            .WithColumn("deleted_by").AsString(100).Nullable()
            .WithColumn("restored_at").AsDateTimeOffset().Nullable()
            .WithColumn("restored_by").AsString(100).Nullable();

        Create.Table("user_roles").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
            .WithColumn("role_id").AsGuid().NotNullable();

        Create.Table("user_claims").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
            .WithColumn("claim_type").AsString(256).NotNullable()
            .WithColumn("claim_value").AsString(1024).NotNullable();

        Create.Table("role_claims").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("role_id").AsGuid().NotNullable()
            .WithColumn("claim_type").AsString(256).NotNullable()
            .WithColumn("claim_value").AsString(1024).NotNullable();

        Create.Table("user_tokens").InSchema(SchemaName)
            .WithColumn("id").AsGuid().NotNullable().PrimaryKey()
            .WithColumn("user_id").AsGuid().NotNullable()
            .WithColumn("purpose").AsInt32().NotNullable()
            .WithColumn("token_hash").AsString(256).NotNullable()
            .WithColumn("expires_at").AsDateTimeOffset().NotNullable()
            .WithColumn("consumed_at").AsDateTimeOffset().Nullable();

        // Non-filtered lookup indexes (FK access paths) — fluent API is portable for these.
        Create.Index("ix_user_roles_user").OnTable("user_roles").InSchema(SchemaName)
            .OnColumn("user_id").Ascending();
        Create.Index("ix_user_claims_user").OnTable("user_claims").InSchema(SchemaName)
            .OnColumn("user_id").Ascending();
        Create.Index("ix_role_claims_role").OnTable("role_claims").InSchema(SchemaName)
            .OnColumn("role_id").Ascending();
        Create.Index("ix_user_tokens_user_purpose").OnTable("user_tokens").InSchema(SchemaName)
            .OnColumn("user_id").Ascending().OnColumn("purpose").Ascending();

        // No-duplicate-membership: a plain unique index (no NULLs involved).
        Create.Index("ux_user_roles_user_role").OnTable("user_roles").InSchema(SchemaName)
            .OnColumn("user_id").Ascending().OnColumn("role_id").Ascending()
            .WithOptions().Unique();
    }

    private void CreateFilteredIndexes()
    {
        // Two filtered unique indexes per "named" table: one scoping uniqueness within a tenant,
        // one enforcing global uniqueness among platform (tenant_id IS NULL) rows. Identical syntax
        // on PostgreSQL and SQL Server.
        Execute.Sql($"CREATE UNIQUE INDEX ux_users_tenant_user_name ON {SchemaName}.users (tenant_id, normalized_user_name) WHERE tenant_id IS NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_users_platform_user_name ON {SchemaName}.users (normalized_user_name) WHERE tenant_id IS NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_users_tenant_email ON {SchemaName}.users (tenant_id, normalized_email) WHERE tenant_id IS NOT NULL AND normalized_email IS NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_users_platform_email ON {SchemaName}.users (normalized_email) WHERE tenant_id IS NULL AND normalized_email IS NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_roles_tenant_name ON {SchemaName}.roles (tenant_id, normalized_name) WHERE tenant_id IS NOT NULL;");
        Execute.Sql($"CREATE UNIQUE INDEX ux_roles_platform_name ON {SchemaName}.roles (normalized_name) WHERE tenant_id IS NULL;");
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table("user_tokens").InSchema(SchemaName);
        Delete.Table("role_claims").InSchema(SchemaName);
        Delete.Table("user_claims").InSchema(SchemaName);
        Delete.Table("user_roles").InSchema(SchemaName);
        Delete.Table("roles").InSchema(SchemaName);
        Delete.Table("users").InSchema(SchemaName);
        Delete.Schema(SchemaName);
    }
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build Themia.sln`
Expected: build succeeds.

- [ ] **Step 3: Update PublicAPI and commit**

Run: `dotnet build Themia.sln --no-incremental`; add the `IdentitySchemaMigration` members to the impl `PublicAPI.Unshipped.txt`.

```bash
git add src/modules/Themia.Modules.Identity/Migrations src/modules/Themia.Modules.Identity/PublicAPI.Unshipped.txt
git commit -m "feat(identity): FluentMigrator identity schema + filtered unique indexes"
```

---

## Task 12: EF entity configurations + `ApplyThemiaIdentity`

**Files:**
- Create: `src/modules/Themia.Modules.Identity/EntityConfiguration/IdentityModelConfiguration.cs`
- Test: `tests/Themia.Modules.Identity.Tests/EntityConfiguration/IdentityModelConfigurationTests.cs`
- Modify: `tests/Themia.Modules.Identity.Tests/Themia.Modules.Identity.Tests.csproj` (add EF refs)

**Design notes:** `ThemiaDbContext` already maps `id`, `tenant_id`, and the audit + soft-delete columns to snake_case for entities deriving from `Entity<>` with the marker interfaces, and registers the `TenantId?` conversion (verified in `ThemiaDbContext.ApplyFrameworkColumnNames`/`ApplyTenantIdConversions`). So the EF config only maps **Identity-specific** columns on `User`/`Role`, and **all** columns on the plain child entities (`UserRole`/`UserClaim`/`RoleClaim`/`UserToken`, which the framework leaves untouched). No indexes are declared in EF — FluentMigrator owns the schema. The adopter calls `modelBuilder.ApplyThemiaIdentity()` **before** `base.OnModelCreating(modelBuilder)`.

- [ ] **Step 1: Add EF refs to the unit-test project**

In `tests/Themia.Modules.Identity.Tests/Themia.Modules.Identity.Tests.csproj`, add to the package `<ItemGroup>`:

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore" />
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
```

and to the project-reference `<ItemGroup>`:

```xml
<ProjectReference Include="../../src/framework/Themia.Framework.Data.EFCore/Themia.Framework.Data.EFCore.csproj" />
```

- [ ] **Step 2: Write the failing test**

`tests/Themia.Modules.Identity.Tests/EntityConfiguration/IdentityModelConfigurationTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Themia.Framework.Data.EFCore;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.EntityConfiguration;
using Xunit;

namespace Themia.Modules.Identity.Tests.EntityConfiguration;

public class IdentityModelConfigurationTests
{
    // A minimal ThemiaDbContext-derived context that registers the Identity model.
    private sealed class TestIdentityDbContext(DbContextOptions options) : ThemiaDbContext(options, null, null)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyThemiaIdentity();
            base.OnModelCreating(modelBuilder);
        }
    }

    private static TestIdentityDbContext BuildContext()
    {
        // UseNpgsql builds the model without opening a connection.
        var options = new DbContextOptionsBuilder<TestIdentityDbContext>()
            .UseNpgsql("Host=localhost;Database=identity_model_test")
            .Options;
        return new TestIdentityDbContext(options);
    }

    [Fact]
    public void User_maps_to_identity_users_with_snake_case_columns()
    {
        using var ctx = BuildContext();
        var entity = ctx.Model.FindEntityType(typeof(User))!;

        Assert.Equal("identity", entity.GetSchema());
        Assert.Equal("users", entity.GetTableName());
        Assert.Equal("normalized_user_name", entity.FindProperty(nameof(User.NormalizedUserName))!.GetColumnName());
        Assert.Equal("tenant_id", entity.FindProperty(nameof(User.TenantId))!.GetColumnName());     // framework-mapped
        Assert.Equal("is_deleted", entity.FindProperty(nameof(User.IsDeleted))!.GetColumnName());   // framework-mapped
    }

    [Fact]
    public void UserRole_maps_all_columns_to_identity_user_roles()
    {
        using var ctx = BuildContext();
        var entity = ctx.Model.FindEntityType(typeof(UserRole))!;

        Assert.Equal("identity", entity.GetSchema());
        Assert.Equal("user_roles", entity.GetTableName());
        Assert.Equal("user_id", entity.FindProperty(nameof(UserRole.UserId))!.GetColumnName());
        Assert.Equal("role_id", entity.FindProperty(nameof(UserRole.RoleId))!.GetColumnName());
    }

    [Fact]
    public void UserToken_purpose_maps_to_int_column()
    {
        using var ctx = BuildContext();
        var entity = ctx.Model.FindEntityType(typeof(UserToken))!;
        Assert.Equal("user_tokens", entity.GetTableName());
        Assert.Equal("purpose", entity.FindProperty(nameof(UserToken.Purpose))!.GetColumnName());
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test Themia.sln --filter IdentityModelConfigurationTests`
Expected: FAIL — `ApplyThemiaIdentity` does not exist.

- [ ] **Step 4: Implement the configurations + extension**

`src/modules/Themia.Modules.Identity/EntityConfiguration/IdentityModelConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.EntityConfiguration;

/// <summary>Applies the Themia Identity entity configurations to an EF model. Call inside your <c>ThemiaDbContext</c>-derived <c>OnModelCreating</c>, before <c>base.OnModelCreating</c>.</summary>
public static class ModelBuilderExtensions
{
    private const string Schema = "identity";

    /// <summary>Registers the Identity entities (users, roles, memberships, claims, tokens) into the model.</summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <returns>The same model builder, for chaining.</returns>
    public static ModelBuilder ApplyThemiaIdentity(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new RoleConfiguration());
        modelBuilder.ApplyConfiguration(new UserRoleConfiguration());
        modelBuilder.ApplyConfiguration(new UserClaimConfiguration());
        modelBuilder.ApplyConfiguration(new RoleClaimConfiguration());
        modelBuilder.ApplyConfiguration(new UserTokenConfiguration());
        return modelBuilder;
    }

    private sealed class UserConfiguration : IEntityTypeConfiguration<User>
    {
        public void Configure(EntityTypeBuilder<User> b)
        {
            b.ToTable("users", Schema);
            b.HasKey(u => u.Id);
            // Framework maps id/tenant_id/audit/soft-delete columns; map the identity-specific columns here.
            b.Property(u => u.UserName).HasColumnName("user_name").HasMaxLength(256).IsRequired();
            b.Property(u => u.NormalizedUserName).HasColumnName("normalized_user_name").HasMaxLength(256).IsRequired();
            b.Property(u => u.Email).HasColumnName("email").HasMaxLength(256);
            b.Property(u => u.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(256);
            b.Property(u => u.EmailConfirmed).HasColumnName("email_confirmed");
            b.Property(u => u.PhoneNumber).HasColumnName("phone_number").HasMaxLength(64);
            b.Property(u => u.PhoneNumberConfirmed).HasColumnName("phone_number_confirmed");
            b.Property(u => u.PasswordHash).HasColumnName("password_hash").HasMaxLength(1024);
            b.Property(u => u.SecurityStamp).HasColumnName("security_stamp").HasMaxLength(128).IsRequired();
            b.Property(u => u.IsActive).HasColumnName("is_active");
            b.Property(u => u.AccessFailedCount).HasColumnName("access_failed_count");
            b.Property(u => u.LockoutEnd).HasColumnName("lockout_end");
            b.Property(u => u.LockoutEnabled).HasColumnName("lockout_enabled");
            b.Property(u => u.TwoFactorEnabled).HasColumnName("two_factor_enabled");
        }
    }

    private sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
    {
        public void Configure(EntityTypeBuilder<Role> b)
        {
            b.ToTable("roles", Schema);
            b.HasKey(r => r.Id);
            b.Property(r => r.Name).HasColumnName("name").HasMaxLength(256).IsRequired();
            b.Property(r => r.NormalizedName).HasColumnName("normalized_name").HasMaxLength(256).IsRequired();
            b.Property(r => r.Description).HasColumnName("description").HasMaxLength(512);
        }
    }

    private sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
    {
        public void Configure(EntityTypeBuilder<UserRole> b)
        {
            b.ToTable("user_roles", Schema);
            b.HasKey(ur => ur.Id);
            b.Property(ur => ur.Id).HasColumnName("id");
            b.Property(ur => ur.UserId).HasColumnName("user_id");
            b.Property(ur => ur.RoleId).HasColumnName("role_id");
        }
    }

    private sealed class UserClaimConfiguration : IEntityTypeConfiguration<UserClaim>
    {
        public void Configure(EntityTypeBuilder<UserClaim> b)
        {
            b.ToTable("user_claims", Schema);
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).HasColumnName("id");
            b.Property(c => c.UserId).HasColumnName("user_id");
            b.Property(c => c.ClaimType).HasColumnName("claim_type").HasMaxLength(256).IsRequired();
            b.Property(c => c.ClaimValue).HasColumnName("claim_value").HasMaxLength(1024).IsRequired();
        }
    }

    private sealed class RoleClaimConfiguration : IEntityTypeConfiguration<RoleClaim>
    {
        public void Configure(EntityTypeBuilder<RoleClaim> b)
        {
            b.ToTable("role_claims", Schema);
            b.HasKey(c => c.Id);
            b.Property(c => c.Id).HasColumnName("id");
            b.Property(c => c.RoleId).HasColumnName("role_id");
            b.Property(c => c.ClaimType).HasColumnName("claim_type").HasMaxLength(256).IsRequired();
            b.Property(c => c.ClaimValue).HasColumnName("claim_value").HasMaxLength(1024).IsRequired();
        }
    }

    private sealed class UserTokenConfiguration : IEntityTypeConfiguration<UserToken>
    {
        public void Configure(EntityTypeBuilder<UserToken> b)
        {
            b.ToTable("user_tokens", Schema);
            b.HasKey(t => t.Id);
            b.Property(t => t.Id).HasColumnName("id");
            b.Property(t => t.UserId).HasColumnName("user_id");
            b.Property(t => t.Purpose).HasColumnName("purpose");                 // enum → int by convention
            b.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(256).IsRequired();
            b.Property(t => t.ExpiresAt).HasColumnName("expires_at");
            b.Property(t => t.ConsumedAt).HasColumnName("consumed_at");
        }
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Themia.sln --filter IdentityModelConfigurationTests`
Expected: PASS (3 tests).

- [ ] **Step 6: Build clean, update PublicAPI, commit**

```bash
git add src/modules/Themia.Modules.Identity tests/Themia.Modules.Identity.Tests
git commit -m "feat(identity): EF entity configurations + ApplyThemiaIdentity"
```

---

## Task 13: Dapper entity mappings

**Files:**
- Create: `src/modules/Themia.Modules.Identity/Mapping/IdentityDapperMappings.cs`
- Test: `tests/Themia.Modules.Identity.Tests/Mapping/IdentityDapperMappingsTests.cs`
- Modify: `tests/Themia.Modules.Identity.Tests/Themia.Modules.Identity.Tests.csproj` (add Dapper ref)

**Design notes:** The Dapper convention already produces snake_case columns (`NormalizedUserName` → `normalized_user_name`, `TenantId` → `tenant_id`, audit columns too), so each mapping only overrides the table name to schema-qualify it (`identity.users`, …). Confirmed against `EntityMapping.ForConvention` and `ToSnakeCase`.

- [ ] **Step 1: Add the Dapper ref to the unit-test project**

In `tests/Themia.Modules.Identity.Tests/Themia.Modules.Identity.Tests.csproj`, add to the project-reference `<ItemGroup>`:

```xml
<ProjectReference Include="../../src/framework/Themia.Framework.Data.Dapper/Themia.Framework.Data.Dapper.csproj" />
```

- [ ] **Step 2: Write the failing test**

`tests/Themia.Modules.Identity.Tests/Mapping/IdentityDapperMappingsTests.cs`:

```csharp
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Mapping;
using Xunit;

namespace Themia.Modules.Identity.Tests.Mapping;

public class IdentityDapperMappingsTests
{
    [Fact]
    public void Maps_user_to_schema_qualified_table_with_snake_case_columns()
    {
        var registry = new EntityMappingRegistry();
        IdentityDapperMappings.Apply(registry);

        var mapping = registry.For<User>();
        Assert.Equal("identity.users", mapping.Table);
        Assert.Equal("normalized_user_name", mapping.Column(nameof(User.NormalizedUserName)));
        Assert.Equal("tenant_id", mapping.Column(nameof(User.TenantId)));
    }

    [Fact]
    public void Maps_all_identity_entities()
    {
        var registry = new EntityMappingRegistry();
        IdentityDapperMappings.Apply(registry);

        Assert.Equal("identity.roles", registry.For<Role>().Table);
        Assert.Equal("identity.user_roles", registry.For<UserRole>().Table);
        Assert.Equal("identity.user_claims", registry.For<UserClaim>().Table);
        Assert.Equal("identity.role_claims", registry.For<RoleClaim>().Table);
        Assert.Equal("identity.user_tokens", registry.For<UserToken>().Table);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test Themia.sln --filter IdentityDapperMappingsTests`
Expected: FAIL — `IdentityDapperMappings` does not exist.

- [ ] **Step 4: Implement the mappings**

`src/modules/Themia.Modules.Identity/Mapping/IdentityDapperMappings.cs`:

```csharp
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Mapping;

/// <summary>Registers Themia Identity entity mappings (schema-qualified table names) into a Dapper <see cref="EntityMappingRegistry"/>.</summary>
public static class IdentityDapperMappings
{
    /// <summary>Registers the Identity entity mappings. The snake_case column convention is kept; only the table names are schema-qualified.</summary>
    /// <param name="registry">The registry to populate.</param>
    public static void Apply(EntityMappingRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register<User>(EntityMapping.ForConvention<User>("identity.users", null));
        registry.Register<Role>(EntityMapping.ForConvention<Role>("identity.roles", null));
        registry.Register<UserRole>(EntityMapping.ForConvention<UserRole>("identity.user_roles", null));
        registry.Register<UserClaim>(EntityMapping.ForConvention<UserClaim>("identity.user_claims", null));
        registry.Register<RoleClaim>(EntityMapping.ForConvention<RoleClaim>("identity.role_claims", null));
        registry.Register<UserToken>(EntityMapping.ForConvention<UserToken>("identity.user_tokens", null));
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Themia.sln --filter IdentityDapperMappingsTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Build clean, update PublicAPI, commit**

```bash
git add src/modules/Themia.Modules.Identity tests/Themia.Modules.Identity.Tests
git commit -m "feat(identity): Dapper entity mappings"
```

---

## Task 14: IdentityModule + DI extensions

**Files:**
- Create: `src/modules/Themia.Modules.Identity/DependencyInjection/IdentityServiceCollectionExtensions.cs`
- Create: `src/modules/Themia.Modules.Identity/IdentityModule.cs`
- Test: `tests/Themia.Modules.Identity.Tests/DependencyInjection/IdentityServiceCollectionExtensionsTests.cs`

**Design notes:**
- The engine is passed explicitly (`MigrationEngine`) because the Dapper layer exposes no `IDatabaseProvider`/engine signal (only `ISqlCompiler`), so neither peer offers a uniform auto-detect. EF adopters could auto-detect, but an explicit argument keeps one code path for both peers.
- `AddThemiaIdentityServices` registers the services (scoped), hasher + options (singleton), and — if a Dapper `EntityMappingRegistry` singleton is already registered — contributes the Identity mappings to it (so Dapper adopters need no extra wiring; they must call `AddThemiaDapper*` **before** `AddThemiaIdentity*`).
- `AddThemiaIdentityAuthorization` registers `IHttpContextAccessor`, `ICurrentUser`, and replaces the framework's `ICurrentUserAccessor` so audit columns reflect the real user.
- The migration runs in `InitializeAsync` via `ThemiaMigrations.Run`, mirroring `SchedulingModule`.

- [ ] **Step 1: Write the failing test**

`tests/Themia.Modules.Identity.Tests/DependencyInjection/IdentityServiceCollectionExtensionsTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.DependencyInjection;
using Themia.Modules.Identity.Principal;
using Xunit;

namespace Themia.Modules.Identity.Tests.DependencyInjection;

public class IdentityServiceCollectionExtensionsTests
{
    [Fact]
    public void Registers_core_services_and_hasher()
    {
        var services = new ServiceCollection();
        services.AddThemiaIdentityServices();

        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IPasswordHasher>());
        Assert.NotNull(provider.GetService<IdentityModuleOptions>());
    }

    [Fact]
    public void Authorization_replaces_the_null_current_user_accessor()
    {
        var services = new ServiceCollection();
        services.AddThemiaIdentityServices();
        services.AddThemiaIdentityAuthorization();

        var provider = services.BuildServiceProvider();
        Assert.IsType<IdentityCurrentUserAccessor>(provider.GetRequiredService<ICurrentUserAccessor>());
        Assert.NotNull(provider.GetService<ICurrentUser>());
    }

    [Fact]
    public void Contributes_dapper_mappings_when_registry_present()
    {
        var services = new ServiceCollection();
        var registry = new EntityMappingRegistry();
        services.AddSingleton(registry);     // simulate AddThemiaDapper* having run first
        services.AddThemiaIdentityServices();

        Assert.Equal("identity.users", registry.For<User>().Table);
    }

    [Fact]
    public void Options_are_configurable()
    {
        var services = new ServiceCollection();
        services.AddThemiaIdentityServices(o => o.MaxFailedAccessAttempts = 9);

        var options = services.BuildServiceProvider().GetRequiredService<IdentityModuleOptions>();
        Assert.Equal(9, options.MaxFailedAccessAttempts);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Themia.sln --filter IdentityServiceCollectionExtensionsTests`
Expected: FAIL — `AddThemiaIdentityServices` does not exist.

- [ ] **Step 3: Implement the DI extensions**

`src/modules/Themia.Modules.Identity/DependencyInjection/IdentityServiceCollectionExtensions.cs`:

```csharp
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Hashing;
using Themia.Modules.Identity.Mapping;
using Themia.Modules.Identity.Principal;
using Themia.Modules.Identity.Services;

namespace Themia.Modules.Identity.DependencyInjection;

/// <summary>Registers Themia Identity services and authorization integration.</summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>Registers the Identity stores, services, password hasher, and options. If a Dapper <see cref="EntityMappingRegistry"/> is already registered, contributes the Identity mappings to it.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configuration.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddThemiaIdentityServices(this IServiceCollection services, Action<IdentityModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new IdentityModuleOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPasswordHasher, Argon2idPasswordHasher>();

        services.TryAddScoped<IUserService, UserService>();
        services.TryAddScoped<IRoleService, RoleService>();
        services.TryAddScoped<IClaimService, ClaimService>();
        services.TryAddScoped<IUserTokenService, UserTokenService>();
        services.TryAddScoped<IClaimsPrincipalFactory, Principal.ClaimsPrincipalFactory>();

        // Dapper adopters: contribute mappings to the registry they already registered.
        ContributeDapperMappings(services);

        return services;
    }

    /// <summary>Registers the current-user principal and replaces the framework's null audit-user accessor.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddThemiaIdentityAuthorization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.TryAddScoped<ICurrentUser, CurrentUser>();

        // Override the framework's NullCurrentUserAccessor so audit columns capture the real user.
        services.RemoveAll<ICurrentUserAccessor>();
        services.AddScoped<ICurrentUserAccessor, IdentityCurrentUserAccessor>();
        return services;
    }

    private static void ContributeDapperMappings(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(EntityMappingRegistry)
                && services[i].ImplementationInstance is EntityMappingRegistry registry)
            {
                IdentityDapperMappings.Apply(registry);
                return;
            }
        }
    }
}
```

> `RemoveAll<ICurrentUserAccessor>` comes from `Microsoft.Extensions.DependencyInjection.Extensions`. If the EF data layer registered its own `ICurrentUserAccessor`, this replaces it too — intended, since Identity now owns the audit-user source.

- [ ] **Step 4: Implement the module**

`src/modules/Themia.Modules.Identity/IdentityModule.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Framework.Core.Modules;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.DependencyInjection;
using Themia.Modules.Identity.Migrations;

namespace Themia.Modules.Identity;

/// <summary>
/// Themia module that registers the Identity services + authorization integration and creates/upgrades the
/// <c>identity</c> schema on startup via FluentMigrator. Runs on either data peer (EF or Dapper) — the engine
/// is supplied explicitly because the data layers expose no uniform engine signal.
/// </summary>
public sealed class IdentityModule : ThemiaModuleBase
{
    private readonly MigrationEngine engine;
    private readonly IdentityModuleOptions options;

    /// <summary>Creates the module for the given migration engine with default options.</summary>
    /// <param name="engine">The database engine the schema migration targets.</param>
    public IdentityModule(MigrationEngine engine)
        : this(engine, new IdentityModuleOptions())
    {
    }

    /// <summary>Creates the module for the given migration engine and options.</summary>
    /// <param name="engine">The database engine the schema migration targets.</param>
    /// <param name="options">The module options.</param>
    public IdentityModule(MigrationEngine engine, IdentityModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.engine = engine;
        this.options = options;
    }

    /// <inheritdoc />
    public override ModuleDescriptor Descriptor { get; } = new(
        name: "Themia.Identity",
        displayName: "Identity",
        description: "Tenant-aware user/role/claim store with argon2id hashing and ASP.NET Core authorization integration.",
        version: new Version(0, 5, 0, 0));

    /// <inheritdoc />
    public override void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddThemiaIdentityServices(o =>
        {
            o.ConnectionStringName = options.ConnectionStringName;
            o.MaxFailedAccessAttempts = options.MaxFailedAccessAttempts;
            o.LockoutDuration = options.LockoutDuration;
            o.AllowPlatformLogin = options.AllowPlatformLogin;
            o.DefaultTokenLifetime = options.DefaultTokenLifetime;
        });
        services.AddThemiaIdentityAuthorization();
    }

    /// <inheritdoc />
    public override ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = serviceProvider.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{options.ConnectionStringName}' was not found; the identity module requires it.");

        ThemiaMigrations.Run(engine, connectionString, typeof(IdentitySchemaMigration).Assembly);
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Themia.sln --filter IdentityServiceCollectionExtensionsTests`
Expected: PASS (4 tests).

- [ ] **Step 6: Build clean, update PublicAPI, commit**

```bash
git add src/modules/Themia.Modules.Identity tests/Themia.Modules.Identity.Tests
git commit -m "feat(identity): IdentityModule + DI/authorization extensions"
```

---

## Task 15: Integration conformance — EF × Dapper × PostgreSQL × SQL Server

**Files:**
- Create: `tests/Themia.Modules.Identity.IntegrationTests/Themia.Modules.Identity.IntegrationTests.csproj`
- Create: `tests/Themia.Modules.Identity.IntegrationTests/TestIdentityDbContext.cs`
- Create: `tests/Themia.Modules.Identity.IntegrationTests/Fixtures/PostgresIdentityFixture.cs`, `Fixtures/SqlServerIdentityFixture.cs`
- Create: `tests/Themia.Modules.Identity.IntegrationTests/IdentityStoreConformanceTests.cs`
- Create: `tests/Themia.Modules.Identity.IntegrationTests/EfPostgresIdentityTests.cs`, `EfSqlServerIdentityTests.cs`, `DapperPostgresIdentityTests.cs`, `DapperSqlServerIdentityTests.cs`

**Design notes:** One abstract `IdentityStoreConformanceTests` declares each behavior once; four concrete classes run it against each peer×engine — the DECISION #6 parity proof for Identity (mirrors `DataLayerConformanceTests`). Each concrete class supplies (a) its container fixture and (b) a `ConfigurePeer` that wires the data layer. **Mirror the existing wiring** in `tests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests/EfPostgresConformanceTests.cs` (EF) and `.../DapperPostgresConformanceTests.cs` (Dapper) for the exact `AddThemia*`/provider calls — those are the proven patterns; do not invent provider constructor signatures. The schema is created by running `ThemiaMigrations.Run` against the container in the fixture. Audit is tested with a stub `ICurrentUserAccessor` (no HttpContext in integration tests).

- [ ] **Step 1: Create the test project**

`tests/Themia.Modules.Identity.IntegrationTests/Themia.Modules.Identity.IntegrationTests.csproj`:

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
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="Testcontainers.MsSql" />
    <PackageReference Include="Npgsql" />
    <PackageReference Include="Microsoft.Data.SqlClient" />
    <PackageReference Include="Microsoft.Extensions.Configuration" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/modules/Themia.Modules.Identity/Themia.Modules.Identity.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.EFCore.PostgreSql/Themia.Framework.Data.EFCore.PostgreSql.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.EFCore.SqlServer/Themia.Framework.Data.EFCore.SqlServer.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.Dapper.PostgreSql/Themia.Framework.Data.Dapper.PostgreSql.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.Dapper.SqlServer/Themia.Framework.Data.Dapper.SqlServer.csproj" />
    <ProjectReference Include="../../src/framework/Themia.MultiTenancy/Themia.MultiTenancy.csproj" />
  </ItemGroup>
</Project>
```

Add to the solution: `dotnet sln Themia.sln add tests/Themia.Modules.Identity.IntegrationTests/Themia.Modules.Identity.IntegrationTests.csproj`

- [ ] **Step 2: Create the EF test context**

`tests/Themia.Modules.Identity.IntegrationTests/TestIdentityDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;
using Themia.Modules.Identity.EntityConfiguration;

namespace Themia.Modules.Identity.IntegrationTests;

/// <summary>A ThemiaDbContext that registers the Identity model — the EF adopter pattern under test.</summary>
public sealed class TestIdentityDbContext(DbContextOptions options, ITenantContext? tenantContext = null)
    : ThemiaDbContext(options, tenantContext, null)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyThemiaIdentity();
        base.OnModelCreating(modelBuilder);
    }
}
```

- [ ] **Step 3: Create the container fixtures (run the migration)**

`tests/Themia.Modules.Identity.IntegrationTests/Fixtures/PostgresIdentityFixture.cs`:

```csharp
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Data.Migrations;
using Themia.Modules.Identity.Migrations;
using Xunit;

namespace Themia.Modules.Identity.IntegrationTests.Fixtures;

public sealed class PostgresIdentityFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("themia_identity_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .WithCleanUp(true)
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public MigrationEngine Engine => MigrationEngine.Postgres;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
        ThemiaMigrations.Run(Engine, ConnectionString, typeof(IdentitySchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "TRUNCATE identity.user_tokens, identity.user_claims, identity.role_claims, " +
            "identity.user_roles, identity.users, identity.roles RESTART IDENTITY CASCADE;";
        await command.ExecuteNonQueryAsync();
    }
}
```

`tests/Themia.Modules.Identity.IntegrationTests/Fixtures/SqlServerIdentityFixture.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Themia.Data.Migrations;
using Themia.Modules.Identity.Migrations;
using Xunit;

namespace Themia.Modules.Identity.IntegrationTests.Fixtures;

public sealed class SqlServerIdentityFixture : IAsyncLifetime
{
    private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
        .WithCleanUp(true)
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public MigrationEngine Engine => MigrationEngine.SqlServer;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
        ThemiaMigrations.Run(Engine, ConnectionString, typeof(IdentitySchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    public async Task ResetAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM identity.user_tokens; DELETE FROM identity.user_claims; DELETE FROM identity.role_claims; " +
            "DELETE FROM identity.user_roles; DELETE FROM identity.users; DELETE FROM identity.roles;";
        await command.ExecuteNonQueryAsync();
    }
}
```

- [ ] **Step 4: Write the conformance base (the shared tests)**

`tests/Themia.Modules.Identity.IntegrationTests/IdentityStoreConformanceTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.DependencyInjection;
using Themia.Modules.Identity.Abstractions.Entities;
using Xunit;

namespace Themia.Modules.Identity.IntegrationTests;

/// <summary>Stub audit user so created_by/modified_by are observable in integration tests (no HttpContext).</summary>
file sealed class StubCurrentUserAccessor(string? userId) : ICurrentUserAccessor
{
    public string? UserId { get; } = userId;
}

public abstract class IdentityStoreConformanceTests
{
    /// <summary>Wires the peer-specific data layer (EF or Dapper) against the test connection string.</summary>
    protected abstract void ConfigurePeer(IServiceCollection services, IConfiguration configuration);

    /// <summary>Truncates the identity tables between tests.</summary>
    protected abstract Task ResetAsync();

    /// <summary>The test connection string from the concrete class's fixture.</summary>
    protected abstract string ConnectionString { get; }

    protected sealed record Scope(ServiceProvider Provider, AsyncServiceScope Inner) : IAsyncDisposable
    {
        public IUserService Users => Inner.ServiceProvider.GetRequiredService<IUserService>();
        public IRoleService Roles => Inner.ServiceProvider.GetRequiredService<IRoleService>();
        public IClaimService Claims => Inner.ServiceProvider.GetRequiredService<IClaimService>();

        public async ValueTask DisposeAsync()
        {
            await Inner.DisposeAsync();
            await Provider.DisposeAsync();
        }
    }

    protected Scope NewScope(TenantId? tenant, bool allowPlatformLogin = true, string auditUserId = "test-user")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
        ConfigurePeer(services, configuration);
        services.AddThemiaIdentityServices(o => o.AllowPlatformLogin = allowPlatformLogin);
        // Override the framework null accessor with a deterministic audit user (no HttpContext here).
        services.RemoveAll<ICurrentUserAccessor>();
        services.AddSingleton<ICurrentUserAccessor>(new StubCurrentUserAccessor(auditUserId));

        var provider = services.BuildServiceProvider();
        return new Scope(provider, provider.CreateAsyncScope());
    }

    [Fact]
    public async Task Create_then_find_by_username_within_tenant_stamps_audit()
    {
        await ResetAsync();
        await using (var s = NewScope(new TenantId("acme")))
        {
            Assert.True((await s.Users.CreateAsync("alice", "pw", "alice@x.com")).Succeeded);
        }
        await using (var s = NewScope(new TenantId("acme")))
        {
            var user = await s.Users.FindByUserNameAsync("ALICE");
            Assert.NotNull(user);
            Assert.Equal("test-user", user!.CreatedBy);
            Assert.Equal(new TenantId("acme"), user.TenantId);
        }
    }

    [Fact]
    public async Task Same_username_allowed_in_two_tenants_but_not_within_one()
    {
        await ResetAsync();
        await using (var a = NewScope(new TenantId("a")))
        {
            Assert.True((await a.Users.CreateAsync("bob", "pw")).Succeeded);
            Assert.False((await a.Users.CreateAsync("bob", "pw")).Succeeded);   // duplicate within tenant
        }
        await using (var b = NewScope(new TenantId("b")))
        {
            Assert.True((await b.Users.CreateAsync("bob", "pw")).Succeeded);    // same name, other tenant
        }
    }

    [Fact]
    public async Task Tenant_user_is_invisible_to_another_tenant()
    {
        await ResetAsync();
        await using (var a = NewScope(new TenantId("a")))
        {
            await a.Users.CreateAsync("carol", "pw");
        }
        await using (var b = NewScope(new TenantId("b"), allowPlatformLogin: false))
        {
            Assert.Null(await b.Users.FindByUserNameAsync("carol"));
        }
    }

    [Fact]
    public async Task Platform_user_is_found_from_a_tenant_scope()
    {
        await ResetAsync();
        await using (var platform = NewScope(tenant: null))
        {
            Assert.True((await platform.Users.CreateAsync("root", "pw")).Succeeded);   // TenantId stays null
        }
        await using (var tenant = NewScope(new TenantId("acme")))
        {
            var user = await tenant.Users.FindByUserNameAsync("root");
            Assert.NotNull(user);
            Assert.Null(user!.TenantId);
        }
    }

    [Fact]
    public async Task Assigned_role_claim_appears_in_effective_claims()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        var userId = (await s.Users.CreateAsync("dave", "pw")).UserId!.Value;
        var roleId = (await s.Roles.CreateAsync("Editor"))!.Value;
        await s.Claims.AddRoleClaimAsync(roleId, "perm", "write");
        Assert.True(await s.Roles.AssignRoleAsync(userId, roleId));

        var claims = await s.Claims.GetEffectiveClaimsAsync(userId);
        Assert.Contains(claims, c => c is { Type: "perm", Value: "write" });
    }

    [Fact]
    public async Task Soft_deleted_user_is_not_found()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        var userId = (await s.Users.CreateAsync("erin", "pw")).UserId!.Value;
        Assert.True(await s.Users.DeleteAsync(userId));
        Assert.Null(await s.Users.FindByUserNameAsync("erin"));
    }

    [Fact]
    public async Task Password_verifies_against_the_real_store()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        await s.Users.CreateAsync("frank", "s3cret");
        Assert.Equal(PasswordVerificationResult.Success, await s.Users.VerifyPasswordAsync("frank", "s3cret"));
        Assert.Equal(PasswordVerificationResult.Failed, await s.Users.VerifyPasswordAsync("frank", "nope"));
    }
}
```

- [ ] **Step 5: Write the four concrete classes**

For each, mirror the `ConfigurePeer` wiring from the referenced existing conformance tests. EF example:

`tests/Themia.Modules.Identity.IntegrationTests/EfPostgresIdentityTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Identity.IntegrationTests.Fixtures;
using Xunit;

namespace Themia.Modules.Identity.IntegrationTests;

public sealed class EfPostgresIdentityTests(PostgresIdentityFixture fixture)
    : IdentityStoreConformanceTests, IClassFixture<PostgresIdentityFixture>
{
    protected override string ConnectionString => fixture.ConnectionString;
    protected override Task ResetAsync() => fixture.ResetAsync();

    protected override void ConfigurePeer(IServiceCollection services, IConfiguration configuration)
    {
        // Mirror tests/Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests/EfPostgresConformanceTests.cs:
        // register the Postgres EF provider + TestIdentityDbContext via AddThemiaDbContext / AddThemiaPostgres,
        // then the open-generic IRepository<,> over TestIdentityDbContext.
        // The exact provider call is whatever that file uses (e.g. AddThemiaPostgres<TestIdentityDbContext>(configuration)).
    }
}
```

Do the same for `EfSqlServerIdentityTests` (SqlServerIdentityFixture + SQL Server EF provider), `DapperPostgresIdentityTests` (PostgresIdentityFixture + `services.AddThemiaDapperPostgres(configuration)`), and `DapperSqlServerIdentityTests` (SqlServerIdentityFixture + `services.AddThemiaDapperSqlServer(configuration)`). The Dapper `ConfigurePeer` is a one-liner (`AddThemiaDapperPostgres`/`AddThemiaDapperSqlServer`); `AddThemiaIdentityServices` (called in the base) contributes the Dapper mappings to the registry those calls registered.

> Important ordering: the base calls `AddThemiaIdentityServices` **after** `ConfigurePeer`, so the Dapper `EntityMappingRegistry` exists when the Identity mappings are contributed. Keep that order.

- [ ] **Step 6: Run the integration tests**

Run: `dotnet test Themia.sln --filter "FullyQualifiedName~Themia.Modules.Identity.IntegrationTests"`
Expected: PASS — all 7 behaviors green on all four peer×engine combinations (28 test executions). Requires Docker.

If `u.TenantId == null` fails to translate on EF, change the platform specs (Task 5) to `!u.TenantId.HasValue` and re-run.

- [ ] **Step 7: Commit**

```bash
git add tests/Themia.Modules.Identity.IntegrationTests Themia.sln
git commit -m "test(identity): EF+Dapper × PG+SQL Server store conformance"
```

---

## Task 16: Version bump, CHANGELOG, README, finalize PublicAPI

**Files:**
- Modify: `Directory.Build.props` (`<Version>` → `0.5.0`)
- Modify: `CHANGELOG.md`
- Create: `src/modules/Themia.Modules.Identity/README.md`
- Modify: the four `PublicAPI.Unshipped.txt` → move shipped lines into `PublicAPI.Shipped.txt`

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, set `<Version>0.5.0</Version>` (the single shared package version; per the release-strategy spec, 0.5.0 opens the remaining Phase-1 modules).

- [ ] **Step 2: Update CHANGELOG**

Add a dated `0.5.0` section to `CHANGELOG.md` under `Added`:

```markdown
## [0.5.0] - 2026-06-14

### Added
- `Themia.Modules.Identity.Abstractions` and `Themia.Modules.Identity`: tenant-aware Identity core —
  user/role/claim store with full account lifecycle (lockout, email/phone confirmation + password-reset
  tokens, a 2FA flag), argon2id password hashing, the `ICurrentUser` principal + `ClaimsPrincipalFactory`,
  and ASP.NET Core authorization integration. Runs on either data peer (EF Core or Dapper) over a
  FluentMigrator schema (PostgreSQL + SQL Server). Platform (cross-tenant) users are modeled as global
  records (`tenant_id IS NULL`). First slice of the full Identity provider (JWT → 0.5.1, external/LINE
  login → 0.5.2).
```

- [ ] **Step 3: Write the package README**

`src/modules/Themia.Modules.Identity/README.md` — a short usage guide: register a data peer (`AddThemiaPostgres`/`AddThemiaDapperPostgres`), for EF call `modelBuilder.ApplyThemiaIdentity()` in your `ThemiaDbContext`-derived `OnModelCreating`, register the module (`new IdentityModule(MigrationEngine.Postgres)`), and inject `IUserService`/`IRoleService`/`IClaimService`/`ICurrentUser`. Note platform users (`tenant_id IS NULL`) and the adopter-owned 1:1 profile-table extension pattern.

- [ ] **Step 4: Finalize PublicAPI**

For each of the four `PublicAPI.Unshipped.txt` files (the two `Identity.Abstractions`/`Identity` package files), move the accumulated lines (below `#nullable enable`) into the corresponding `PublicAPI.Shipped.txt`, leaving `PublicAPI.Unshipped.txt` with only `#nullable enable`. (This marks the 0.5.0 surface as shipped.)

- [ ] **Step 5: Full clean build + test**

Run: `dotnet build Themia.sln --no-incremental`
Expected: clean (no RS0016, no warnings).

Run: `dotnet test Themia.sln`
Expected: all tests pass (unit + integration; integration requires Docker).

- [ ] **Step 6: Commit**

```bash
git add Directory.Build.props CHANGELOG.md src/modules/Themia.Modules.Identity/README.md src/modules/Themia.Modules.Identity*/PublicAPI.*.txt
git commit -m "chore(identity): release 0.5.0 — version, CHANGELOG, README, PublicAPI shipped"
```

- [ ] **Step 7: Finish the branch**

Use the **superpowers:finishing-a-development-branch** skill to verify tests and open the PR for `feat/themia-identity-core`.

---

## Self-review checklist (run before handing off)

- **Spec coverage:** §3 layout → Tasks 1,3; §4 schema → Tasks 2,11; §5 services → Tasks 4–8; §6 principal/authz → Tasks 9,10,14; §7 EF/Dapper integration → Tasks 12,13,14; §8 security → Tasks 4,8; §9 testing → Tasks 5–10 (unit) + 15 (integration); §10 out-of-scope honored (no JWT/endpoints/LINE/TOTP/generic-TUser).
- **Type consistency:** `MigrationEngine` (Themia.Data.Migrations) used in Tasks 14,15; `TenantId` is a string record struct everywhere; `UserRole.Id` surrogate consistent across Tasks 2,6,11,12,13; `IdentityModuleOptions` properties consistent across Tasks 3,5,8,14.
- **Platform model:** `tenant_id IS NULL` + filtered unique indexes consistent (spec corrected, Tasks 5,11).
- **Deferred (documented):** optimistic concurrency on User/Role; construction-level tenant isolation on child tables; both noted in-task and in the spec.
