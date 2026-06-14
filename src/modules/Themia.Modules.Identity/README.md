# Themia.Modules.Identity

Tenant-aware Identity core for Themia applications. Provides user/role/claim storage, argon2id
password hashing, account lifecycle tokens (email/phone confirmation, password reset, 2FA flag),
lockout, the `ICurrentUser` principal, and ASP.NET Core authorization integration.

Supports both data peers — **EF Core** and **Dapper** — over a single FluentMigrator schema
(PostgreSQL and SQL Server).

## Quick start

### 1. Register a data peer

Pick **one** of the following depending on your data layer.

**EF Core — PostgreSQL**
```csharp
builder.Services.AddThemiaPostgres<AppDbContext>(builder.Configuration);
```

**EF Core — SQL Server**
```csharp
builder.Services.AddThemiaSqlServer<AppDbContext>(builder.Configuration);
```

**Dapper — PostgreSQL**
```csharp
builder.Services.AddThemiaDapperPostgres(builder.Configuration);
```

**Dapper — SQL Server**
```csharp
builder.Services.AddThemiaDapperSqlServer(builder.Configuration);
```

### 2. Configure your DbContext (EF Core only)

Derive from `ThemiaDbContext` and call `modelBuilder.ApplyThemiaIdentity()` in `OnModelCreating`.

> **Important — EF audit stamping:** `ThemiaDbContext` stamps `created_by`/`modified_by` from its
> `protected virtual string? CurrentUserId` property (defaults to `null`), **not** from
> `ICurrentUserAccessor`. To record the real user you must override `CurrentUserId` in your context:

```csharp
using Themia.Framework.Data.EFCore;
using Themia.Framework.Core.Abstractions.Security;
using Themia.Modules.Identity.EntityConfiguration;

public sealed class AppDbContext(DbContextOptions options, ICurrentUserAccessor currentUser)
    : ThemiaDbContext(options)
{
    protected override string? CurrentUserId => currentUser.UserId;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.ApplyThemiaIdentity();
        base.OnModelCreating(b);
    }
}
```

The **Dapper** peer reads `ICurrentUserAccessor` directly, so no additional override is needed there.

### 3. Register the Identity module

Pass the migration engine that matches your database provider and register the module with Themia's
host builder:

```csharp
using Themia.Data.Migrations;
using Themia.Modules.Identity;

// Inside your IThemiaBuilder / host setup:
builder.AddModule(new IdentityModule(MigrationEngine.Postgres));
// or
builder.AddModule(new IdentityModule(MigrationEngine.SqlServer));
```

`IdentityModule` automatically:
- Runs the FluentMigrator identity schema migration on startup.
- Registers `IUserService`, `IRoleService`, `IClaimService`, `IUserTokenService`, `IPasswordHasher`,
  `IClaimsPrincipalFactory`, and `ICurrentUser` in the DI container.

To also wire up ASP.NET Core authorization (adds claims-based policy support and registers
`ICurrentUserAccessor` from the HTTP context):

```csharp
builder.Services.AddThemiaIdentityAuthorization();
```

### 4. Use the services

```csharp
public class AccountController(IUserService users, ICurrentUser currentUser) : ControllerBase
{
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterDto dto, CancellationToken ct)
    {
        var result = await users.CreateAsync(dto.UserName, dto.Password, dto.Email, ct);
        if (!result.Succeeded)
            return BadRequest(result.Error);
        return Ok(new { result.UserId });
    }
}
```

Inject any of:

| Interface | Purpose |
|-----------|---------|
| `IUserService` | Create, find, delete, set-active, change password, verify password |
| `IRoleService` | Create roles, assign/remove users from roles |
| `IClaimService` | Add/remove user and role claims, resolve effective claims |
| `IUserTokenService` | Generate and consume one-time tokens (email confirm, password reset, etc.) |
| `ICurrentUser` | Read the authenticated principal (UserId, TenantId, Roles, Claims) |

## Platform users

A **platform user** is a user whose `tenant_id IS NULL` in the database. Platform users can
authenticate across all tenants when `IdentityModuleOptions.AllowPlatformLogin = true` (the
default).

```csharp
// Check at runtime:
if (currentUser.IsPlatform) { /* platform-level operation */ }
```

## Extending the user profile (1:1 table pattern)

Themia's `User` entity holds identity data only. Add app-specific profile fields in your own table
with a foreign key to `user_id`:

```csharp
public class UserProfile
{
    public Guid UserId { get; set; }   // FK → themia_users.id
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
}
```

Configure it in your `AppDbContext.OnModelCreating`. Themia never touches this table.

## Options

`IdentityModuleOptions` (configurable via the `IdentityModule(engine, options)` overload):

| Property | Default | Description |
|----------|---------|-------------|
| `MaxFailedAccessAttempts` | 5 | Consecutive failures before lockout |
| `LockoutDuration` | 15 minutes | How long an account stays locked |
| `DefaultTokenLifetime` | 24 hours | Expiry for generated tokens |
| `AllowPlatformLogin` | `true` | Whether platform users (`tenant_id IS NULL`) can log in |
| `ConnectionStringName` | `"Default"` | Connection string key used by Dapper |
