# Themia.Modules.Identity

Tenant-aware Identity core for Themia applications. Provides user/role/claim storage, argon2id
password hashing, account lifecycle tokens (email/phone confirmation, password reset, 2FA flag),
lockout, the `ICurrentUser` principal, and ASP.NET Core authorization integration.

Supports both data peers — **EF Core** and **Dapper** — over a single FluentMigrator schema
(PostgreSQL and SQL Server).

> **This package is the engine-agnostic core.** It carries no data peer, no database driver and no
> migration runner. Reference it **plus exactly one engine package**:
>
> | Your data layer | Add this package | Register with |
> |---|---|---|
> | Dapper | `Themia.Modules.Identity.Dapper` | `AddThemiaIdentityDapper` / `IdentityDapperModule` |
> | EF Core | `Themia.Modules.Identity.EFCore` | `AddThemiaIdentityEFCore` / `IdentityEFCoreModule` |
>
> Upgrading from 0.12.x? `AddThemiaIdentityServices` and `IdentityModule` are gone — see
> [MIGRATION.md](../../../MIGRATION.md).

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
using Themia.Modules.Identity.EntityConfiguration;   // from Themia.Modules.Identity.EFCore

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

Use the module from the engine package matching the peer you registered in step 1. The
`MigrationEngine` argument is the **database** and is orthogonal to the peer — both are explicit.

```csharp
using Themia.Data.Migrations;
using Themia.Modules.Identity.Dapper;   // or Themia.Modules.Identity.EFCore

// Inside your IThemiaBuilder / host setup, AFTER the data peer registration:
builder.AddModule(new IdentityDapperModule(MigrationEngine.Postgres));
// or
builder.AddModule(new IdentityEFCoreModule(MigrationEngine.SqlServer));
```

> **Dapper: the module must be configured after `AddThemiaDapper*`.** `IdentityDapperModule` contributes
> the identity entity mappings to the registry that call creates, and throws if it does not exist yet.
> A host whose module loop runs first fails to start, with the ordering named in the message.

The module automatically:
- Runs the FluentMigrator identity schema migration on startup.
- Registers `IUserService`, `IRoleService`, `IClaimService`, `IUserTokenService`, `IPasswordHasher`,
  `IClaimsPrincipalFactory`, and `ICurrentUser` in the DI container.
- Wires the engine-specific store: the Dapper mappings, or (EF Core) a startup check that
  `ApplyThemiaIdentity()` was actually applied to the context Themia resolves.

Prefer plain DI? Call the engine's extension method instead, again **after** the peer:

```csharp
builder.Services.AddThemiaDapperPostgres(builder.Configuration);
builder.Services.AddThemiaIdentityDapper(o => o.AllowPlatformLogin = true);
builder.Services.AddThemiaIdentityAuthorization();
```

The module already calls `AddThemiaIdentityAuthorization()`, so you normally don't need to. It registers
`IHttpContextAccessor`, the `ICurrentUser` principal, and overrides the audit-user accessor
(`ICurrentUserAccessor`) so it reads the authenticated user from the HTTP context. It does **not** register
any authorization policies.

### Supplying your own repositories

`AddThemiaIdentityCore` registers the services with no data peer at all — for an application providing its
own `IRepository<T, TKey>` implementations. It also applies **no schema**: this package carries the
FluentMigrator migration classes but no runner, because running them needs a driver for each engine and the
core stays driver-free. Run them yourself:

```csharp
ThemiaMigrations.Run(MigrationEngine.Postgres, connectionString, IdentityMigrations.Assembly);
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
| `IUserLifecycleHooks` | Refuse or observe changes to a user's credential state (see below) |
| `IRoleService` | Create roles, assign/remove users from roles |
| `IClaimService` | Add/remove user and role claims, resolve effective claims |
| `IUserTokenService` | Generate and consume one-time tokens (email confirm, password reset, etc.) |
| `ICurrentUser` | Read the authenticated principal (UserId, TenantId, Roles, Claims) |

## Refusing and observing user mutations

`IUserLifecycleHooks` lets your app veto a change to a user's credential state, and see the ones that
went through. `IAuthenticationHooks` covers the login lifecycle only; a rule keyed on credential state —
"this account must keep one usable way to sign in", "you cannot remove the last administrator", "this
user still owns open invoices" — could otherwise only be enforced by owning every call site.

**Every mutation has a hook, not a chosen few.** A seam covering three of seven paths reads as covering
all seven. Every method has a default implementation, so override only what you care about:

```csharp
internal sealed class LockoutGuard(AppDbContext db) : IUserLifecycleHooks
{
    public async ValueTask<UserMutationDecision> OnBeforeSetPhoneNumberAsync(
        Guid userId, string? phoneNumber, CancellationToken ct = default)
    {
        // Setting a number clears its confirmation, so this is the path that can lock an
        // SMS-only account out of its own sign-in.
        if (phoneNumber is null && await db.IsPhoneOnlyAsync(userId, ct))
            return UserMutationDecision.Refuse("This is the only way you can sign in.");

        return UserMutationDecision.Allow();
    }

    public ValueTask OnUserMutatedAsync(Guid userId, UserMutation mutation, CancellationToken ct = default)
        => auditTrail.RecordAsync(userId, mutation, ct);
}

// Register BEFORE AddThemiaIdentity* — the module's permissive default is registered with TryAdd.
services.AddScoped<IUserLifecycleHooks, LockoutGuard>();
```

A refusal returns `UserMutationOutcome.Refused` carrying your reason, and nothing is written:

```csharp
var result = await users.SetPhoneNumberAsync(userId, null, ct);
return result.Outcome switch
{
    UserMutationOutcome.Success      => NoContent(),
    UserMutationOutcome.Refused      => Conflict(result.Reason),
    UserMutationOutcome.Duplicate    => Conflict("That number is already in use."),
    UserMutationOutcome.UserNotFound => NotFound(),
    _ => throw new UnreachableException(),
};
```

**Transaction contract.** A before-hook runs inside the caller's scope, before the module touches any
entity and before its unit of work opens. It must not call `SaveChanges` and must not open a
transaction on the same scoped connection — the module saves immediately after the hook returns, so a
hook holding a transaction there turns a refusal into a deadlock. Read freely; write through your own
connection if you must write at all. `OnUserMutatedAsync` runs after the save: the change is already
committed, and throwing does not undo it.

## Notes / gotchas

- **Dapper: register the data peer first.** Call `AddThemiaDapper*(...)` **before**
  `AddThemiaIdentityDapper` or `IdentityDapperModule`. The identity entity mappings go into the
  `EntityMappingRegistry` that the peer registration creates; registering Identity first means there is no
  registry to contribute to. That used to be skipped silently and surface much later as a query against
  unqualified `users`; it now throws at registration. (EF adopters are unaffected — see
  `ApplyThemiaIdentity()` above.)
- **`AddThemiaIdentityAuthorization()` replaces `ICurrentUserAccessor`.** It calls
  `RemoveAll<ICurrentUserAccessor>()` and registers `IdentityCurrentUserAccessor`, so any
  previously-registered custom `ICurrentUserAccessor` is replaced. This is intentional — Identity
  becomes the audit-user source — but an adopter with a custom accessor should be aware it will not
  survive. (Both engine modules call this automatically.)

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
    public Guid UserId { get; set; }   // FK → identity.users.id
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
}
```

Configure it in your `AppDbContext.OnModelCreating`. Themia never touches this table.

## Options

`IdentityModuleOptions` (configurable via the `IdentityDapperModule(engine, options)` /
`IdentityEFCoreModule(engine, options)` overload, or the `AddThemiaIdentity*` lambda):

| Property | Default | Description |
|----------|---------|-------------|
| `MaxFailedAccessAttempts` | 5 | Consecutive failures before lockout |
| `LockoutDuration` | 15 minutes | How long an account stays locked |
| `DefaultTokenLifetime` | 1 hour | Expiry for generated tokens |
| `AllowPlatformLogin` | `true` | Whether platform users (`tenant_id IS NULL`) can log in |
| `ConnectionStringName` | `"Default"` | Connection string key used by Dapper |
