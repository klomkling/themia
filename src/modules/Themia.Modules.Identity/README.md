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
| `IExternalLoginLinkService` | Link, unlink, list and look up the external identities on an existing user (see below) |
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

With more than one rule on a mutation, refuse with a code — `UserMutationDecision.Refuse(reason, code)` —
and branch on the result's `RefusalCode`, never on the wording of `Reason`. Every result that can be
`Refused` carries it.

**Transaction contract.** A before-hook runs inside the caller's scope, before the module touches any
entity and before its unit of work opens. It must not call `SaveChanges` and must not open a
transaction on the same scoped connection — the module saves immediately after the hook returns, so a
hook holding a transaction there turns a refusal into a deadlock. Read freely; write through your own
connection if you must write at all. `OnUserMutatedAsync` runs after the save: the change is already
committed, and throwing does not undo it.

## Linking external identities to an existing user

`IExternalLoginService.ResolveOrProvisionAsync` is the sign-in path: it resolves an identity, and opens an
account when it cannot. `IExternalLoginLinkService` is everything else — for a user who is already signed
in and wants a second channel, and for code that needs to ask "who owns this identity?" without creating
anyone.

```csharp
// The user is signed in (via LINE) and has just proved control of a Telegram identity.
var result = await linking.LinkAsync(userId, telegramIdentity, ct);
return result.Outcome switch
{
    ExternalLoginLinkOutcome.Linked or
    ExternalLoginLinkOutcome.AlreadyLinkedToUser => await ReissueSessionAsync(userId, ct),
    ExternalLoginLinkOutcome.LinkedToAnotherUser => Conflict("channel_identity_already_linked"),
    ExternalLoginLinkOutcome.UserInactive        => Forbid(),
    ExternalLoginLinkOutcome.Refused             => Conflict(result.Reason),
    ExternalLoginLinkOutcome.UserNotFound        => Unauthorized(),
    _ => throw new UnreachableException(),
};

// "Does anyone own this identity?" — never provisions.
var owner = await linking.FindUserByLoginAsync("telegram", subject, ct);

// A notification job resolving addresses for a batch of recipients: one call, not one per row.
var byUser = await linking.GetLoginsForUsersAsync(recipientIds, ct);   // at most 1,000 distinct ids
```

Four rules worth knowing before you build on it:

- **An identity is never moved between users.** Linking one another user holds returns
  `LinkedToAnotherUser` and writes nothing. Two concurrent links of the same identity to different users
  are settled by the database's unique index: exactly one `Linked`.
- **A deactivated or locked-out user is never linked** (`UserInactive`) — the same rule the sign-in
  path's auto-link follows, so a reactivation cannot inherit a login nobody approved.
- **A user may hold several identities from one provider.** Two Google accounts is legitimate, so the
  module does not refuse a second. If your product allows one per provider, refuse it in the
  `IUserLifecycleHooks.OnBeforeLinkExternalLoginAsync` overload that carries `currentLogins`.
- **Linking and unlinking end no sessions.** Sessions are not tagged by the identity that opened them, so
  after unlinking a compromised identity call `IRefreshTokenService.RevokeAllForUserAsync(userId)` — it is
  the only way to reach the session that identity holds.

**Unlinking is how a user without a password locks themselves out**, and the module does not stop it: a
sign-in method can live entirely outside Identity (a phone-OTP login on `Themia.Challenges`, for one), so
"the last way in" is something only your code can compute. Put that rule in
`IUserLifecycleHooks.OnBeforeUnlinkExternalLoginAsync`.

Both link hooks have an overload that receives the user's current links, because a hook cannot read them
itself — `IExternalLoginLinkService` is the one calling it, so injecting it is a DI cycle. The unlink
overload gets every provider's links, not only the one being removed:

```csharp
internal sealed class ChannelRules : IUserLifecycleHooks
{
    public ValueTask<UserMutationDecision> OnBeforeLinkExternalLoginAsync(
        Guid userId, string provider, string subject, IReadOnlyList<ExternalLoginInfo> currentLogins,
        CancellationToken ct = default) =>
        ValueTask.FromResult(currentLogins.Any(l => l.Provider == provider)
            ? UserMutationDecision.Refuse("You already linked this channel.", "channel_already_linked")
            : UserMutationDecision.Allow());

    public ValueTask<UserMutationDecision> OnBeforeUnlinkExternalLoginAsync(
        Guid userId, string provider, IReadOnlyList<ExternalLoginInfo> currentLogins,
        CancellationToken ct = default) =>
        ValueTask.FromResult(currentLogins.All(l => l.Provider == provider)
            ? UserMutationDecision.Refuse("This is your only channel.", "last_channel")
            : UserMutationDecision.Allow());
}
```

The list is read before the write, not locked: two concurrent links for one user each see the other's
absent, so a rule that must hold under concurrency needs a constraint of its own.

## Passwordless phone-code login

`IOtpLoginFlow` signs a user in with a short numeric code sent to their confirmed phone, and issues the same tokens as
`IAuthenticationFlow.LoginAsync`, so `refresh`, `logout` and your hooks and observers work unchanged. It is opt in.

```csharp
services.AddThemiaIdentityAspNetCore(/* jwt */);
services.AddScoped<IOtpCodeSender, SmsCodeSender>();          // yours: delivery stays in the application
services.AddThemiaIdentityOtpLogin(o =>
{
    o.CodeHashKey = Convert.FromBase64String(config["Otp:HashKey"]!);   // required, at least 32 random bytes
});

app.MapGroup("/auth").MapIdentityAuthEndpoints()
   .MapIdentityOtpEndpoints(b => b.RequireRateLimiting("otp"));         // POST otp/start, POST otp/verify
```

`StartAsync(phone)` returns a `ChallengeId` and `ExpiresAt`, the same shape whether or not the phone has an account (an unknown, unconfirmed,
inactive or locked-out phone gets a decoy challenge and no message). `VerifyAsync(challengeId, code)` returns the usual `LoginResult`:
`InvalidCredentials` for a wrong, expired, used or unknown code, `LockedOut` once the attempt cap is hit (hooks and observers see
`LoginFailureReason.OtpAttemptsExceeded`, not `LockedOut`, which means the account is locked), `Denied` when a hook refuses. The
HTTP endpoints answer every failure with the same 401, and 429 when a start is throttled.

| Option | Default | |
|---|---|---|
| `CodeHashKey` | none, required | HMAC key for the stored code hash, at least 32 random bytes. You supply it (user secrets, key vault, environment); Themia generates and stores nothing. Every instance must get the same key. Never logged. Rotating it ends the codes in flight. |
| `CodeLength` | 6 | 6 to 9 digits. |
| `Lifetime` | 5 min | |
| `MaxAttempts` | 5 | Guesses per challenge. |
| `MaxFailedAttemptsPerWindow` | 10 | Failed guesses per phone across all challenges inside `FailureWindow`. A new start never resets it. |
| `FailureWindow` | 1 h | Rolling window for the failure cap and the start cap. |
| `ResendCooldown` | 60 s | Minimum time between two starts for one phone. |
| `MaxStartsPerWindow` | 5 | Starts per phone inside `FailureWindow`. |

What is guaranteed: the code is random (`RandomNumberGenerator`), stored only as an HMAC and compared in constant time. A guess is counted
and the code consumed in one conditional UPDATE, so parallel guesses cannot exceed the cap and a code cannot log in twice. Only the newest
challenge for a phone is open. After the code is right, `VerifyAsync` re-checks that the user is active, not locked out and still has a
confirmed phone, runs `OnBeforeLoginAsync` (identifier is the normalized phone) and `OnLoginSucceededAsync`, then issues tokens. The OTP
failure counter is separate from `AccessFailedCount`, so guessing codes cannot lock a user out of password login.

Known limits, so you can decide what to add:

- **Delivery latency.** `StartAsync` awaits `IOtpCodeSender` only for a phone that can log in, so a slow gateway makes that response slower than
  the unknown-phone case. Implement the sender to enqueue and return quickly. The start and failure throttles bound how often this can be probed.
- **The host app must add a per-IP rate limiter on `otp/start` and `otp/verify`.** The library cannot see the caller. Pass
  `RequireRateLimiting` to `MapIdentityOtpEndpoints`, or limit in your own endpoints. Without it one client can spread starts over many phone
  numbers and run up SMS cost.
- **The phone budget can be used against its owner.** Anyone who knows a number can spend its start and failure budget (5 starts, 10 failed
  guesses per window) and so block that person's one-time-code login until the window slides. Password login is not affected. This is the
  price of a cap that does not reset per challenge; the per-IP limiter above is what makes it expensive to do at scale.
- **Options are read once, at registration.** `AddThemiaIdentityOtpLogin` takes the values when it runs and validates them there; they are not
  `IOptions`, do not reload, and a bad or missing `CodeHashKey` stops the application at startup.
- **Phone uniqueness.** A phone shared by two users in scope resolves to whichever `IUserService.FindByPhoneAsync` returns; keep phone numbers
  unique per tenant (the module's normalized-phone index does).
- **Throttles are keyed on the phone across tenants.** A platform user is reachable from any tenant context, so tenants that share a number
  share its start and failure budget.
- **A successful login clears the phone's recent failure count**, so a user who logs in regularly gives a guesser fresh budget; the
  per-challenge cap and the start cap still bound it (at most `MaxFailedAttemptsPerWindow` guesses of 10^6 per success).
- **Parallel starts** are re-checked after the insert, so a burst cannot all send; racing starts may all be refused and the user retries.
- **Expired rows** are purged in small batches on later starts; there is no background job.
- **Which login methods are enabled** per tenant or user is not modelled here. Enforce it in `OnBeforeLoginAsync` for now.

## Requiring a second factor (staff MFA)

Themia does not verify a second factor; it gives the app one place to say "this user owes one" and one way to finish the
login afterwards. Register an `ISecondFactorPolicy`; the default asks nobody, so nothing changes until you do.

```csharp
public sealed class StaffMfaPolicy(IMfaStore mfa) : ISecondFactorPolicy
{
    public async ValueTask<SecondFactorRequirement> EvaluateAsync(SecondFactorContext context, CancellationToken ct = default) =>
        !await mfa.IsStaffAsync(context.UserId, ct) ? SecondFactorRequirement.NotRequired
        : await mfa.HasFactorAsync(context.UserId, ct) ? SecondFactorRequirement.Verify
        : SecondFactorRequirement.Enrol;
}
services.AddSingleton<ISecondFactorPolicy, StaffMfaPolicy>();   // after AddThemiaIdentityAspNetCore (TryAdd)
```

- The policy runs **after the primary check succeeded and before anything is issued**, in all three Themia flows: password
  (`LoginAsync`), one-time code (`VerifyAsync`) and external login (`AuthenticateAsync`). `SecondFactorContext` carries the
  method and, for external login, the provider key.
- A login that owes a second factor returns `LoginOutcome.SecondFactorRequired` (external: `ExternalLoginOutcome.SecondFactorRequired`)
  with `result.SecondFactor`: the `UserId`, the `Stage` (`Verify` or `Enrol`), an opaque single-use `Ticket` and its `ExpiresAt`
  (Verify tickets live 5 minutes, Enrol tickets 15). Nothing is issued: no refresh row, no tokens. It is **not** a failed login:
  no failure hook, no lockout count, and `OnLoginSucceeded` fires only when the login completes. The built-in endpoints answer
  it with their uniform 401 because they cannot complete the factor; use your own controller.
- The app verifies (or enrols and confirms) the factor itself, for example with `Themia.Totp`, then calls
  `ISecondFactorCompletion.CompleteAsync(ticket, userId, stage)`. It consumes the ticket, **repeats the original flow's gates** in the ticket's
  tenant scope (the user still exists in that tenant, is active and not locked out, has the same security stamp, the login method
  is still switched on, a one-time-code user still has a confirmed phone), runs the method's own success hook and observer
  (external logins get the external ones, with `WasCreated`/`WasLinked`), and issues the session exactly as a normal login would
  (same claims, same `sid`). A hook that denies at completion returns `Denied`. The single-use claim is one conditional `UPDATE` (the first of two parallel
  completions wins, the other is refused). After too many wrong codes call
  `RejectAsync(ticket)` to burn the ticket; the attempt limits are the app's.
- A policy that throws stops the login with that exception and nothing is issued (it is not turned into a "bad password").
  A policy that asks for a second factor while no `ISecondFactorTicketService` is registered also throws.
- **Pass the user and stage you just verified.** `CompleteAsync` refuses (and burns) a ticket that is not for exactly that user and
  stage. Take `userId` from the account whose factor you checked, never from the request body: otherwise someone holding a
  victim's ticket could pass their own factor and receive the victim's session, or spend a verify ticket at an enrol endpoint.
  Your endpoint should look the pending login up by your own record of it (keyed by the ticket you handed out), not by a
  client-supplied user id.
- **Call `CompleteAsync` only after your own factor check has passed**: completion trusts that the app verified (or enrolled and confirmed)
  the factor; Themia cannot tell. The ticket proves the primary check passed, not that the second factor did.
- **Enrol is only as strong as the primary check.** Return `Enrol` only for accounts that have no factor yet, and notify the user
  (and any existing devices) when a factor is enrolled, otherwise whoever holds a stolen password can enrol their own.
- Tickets are protected with ASP.NET Core data protection plus a single-use user token (`TokenPurpose.SecondFactorTicket`); persist and protect
  the data-protection keys in production (a key store that is not encrypted at rest is a risk for every ticket and cookie). Losing them only invalidates tickets in flight. The ticket and the password are never logged.
- **The seam covers only the three Themia flows.** Code that mints sessions with the public building blocks
  (`IRefreshTokenService.IssueAsync`, `IAccessTokenService.Issue`) bypasses it and must call `ISecondFactorPolicy` itself. A
  federation or bot sign-in in your app is such a path.
- **Enabling a policy does not end existing sessions.** Refresh families minted before the policy was on stay valid and keep
  rotating; revoke the affected users' families (`IRefreshTokenService.RevokeAllForUserAsync`) when you turn it on.
- Breaking for source: `LoginOutcome` and `ExternalLoginOutcome` gain a member, so an exhaustive `switch` without a discard stops
  compiling until it handles `SecondFactorRequired`. `result.Succeeded` is unchanged, so `if (!result.Succeeded)` callers fail closed.

## Refresh, logout and one-time-code login with no tenant scope

With a claims-only tenant strategy the tenant comes from the JWT, but `refresh`, `logout`, `otp/start` and `otp/verify` carry no JWT, so
their ambient tenant is **null**. Identity handles that without trusting the caller: the proof is a secret, and the tenant is read from the
row the secret proves.

- **Refresh / logout.** The refresh-token hash (256 random bits) finds the token; its owner is that row's own user, in whatever tenant it
  lives. Rotation, the lockout check, the claims principal, `OnRefreshSucceededAsync` and the observers run in the owner's tenant. A token only
  ever acts as its own user. `LogoutAsync` returns `LogoutOutcome.Revoked` only when a token was revoked; the `logout` endpoint answers 204 for
  that and 401 otherwise (unknown or already-revoked token).
- **`OnBeforeRefreshAsync` sees a null scope.** It runs before the token is looked up, so no owner is known yet. Do not read "no tenant" as
  "platform user" or as "allowed" in that hook; make the decision after the owner is known (`OnRefreshSucceededAsync`).
- **OTP start.** `IOtpRecipientResolver` finds the holder of the phone across tenants. Exactly one active user with a confirmed phone and no
  lockout is a recipient; none, or several (the same number active in two tenants), is a decoy with the same response and no message. If the
  host does set an ambient tenant, only that tenant and platform users are candidates.
- **OTP verify.** The challenge is found by its id (a random GUID) in any tenant, the guess is counted and the code consumed there, and the
  login runs in the challenge's tenant. The user must belong to that same tenant.
- **Same number in two tenants.** With no tenant hint such a number cannot receive a code. To stop it arising, set
  `IdentityModuleOptions.RejectPhoneNumberHeldInAnotherTenant = true`: `SetPhoneNumberAsync` then refuses a number an active user in another
  tenant (or the platform) holds, with refusal code `IdentityModuleOptions.PhoneNumberInAnotherTenantRefusalCode`. It only blocks new
  assignments; numbers already shared are untouched.
- **The tenant switch needs the accessor-backed `ITenantContext`.** Identity switches the ambient tenant through
  `TenantContextAccessor`. EF Core's runtime tenant filter and Dapper behind the `ITenantContext` that `AddThemiaAspNetCore` registers both read
  it, so they follow the switch. A custom `ITenantContext` that returns its own fixed value is not switched, and a claims-only host using one
  would still see a null scope. The switch is internal and shared across `Themia.Framework.Core`, `Themia.Modules.Identity` and
  `Themia.Modules.Identity.AspNetCore` through `InternalsVisibleTo`, so install those at the same version (all Themia packages share one).
- **The cross-tenant phone option is not atomic.** `RejectPhoneNumberHeldInAnotherTenant` reads, then writes: two tenants setting the same number
  at the same instant can both pass, and no portable constraint can express "unique across tenants among active users". The one-time-code
  login fails closed on any duplicate that gets through (a decoy for every holder).
- **Logout hooks.** `OnLogoutAsync` and the logout observers run in the token owner's tenant scope (via `IRefreshTokenService.FindOwnerAsync`);
  a custom `IRefreshTokenService` that does not implement it keeps the caller's scope.
- **Mutation hooks.** `OnUserMutatedAsync` and the observers run in the mutated user's tenant, so a hook that calls
  `IRefreshTokenService.RevokeAllForUserAsync(userId)` reaches the user's tokens whoever made the change. `RevokeAllForUserAsync` itself stays
  caller-scoped (a tenant's code cannot revoke another tenant's sessions by id) and logs a warning when it revokes nothing because the user is
  not visible.

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
| `LockoutPolicy` | `ResetCountOnLock` | `ResetCountOnLock` zeroes `AccessFailedCount` at lock, so each expired lock buys `MaxFailedAccessAttempts` fresh guesses. `KeepCount` keeps it, so the first wrong password after expiry re-locks. `Progressive` is `KeepCount` plus a lock that doubles per consecutive lock up to `MaxLockoutDuration`; a successful login restarts it. `Progressive` lets someone who knows an account name keep it locked for up to the cap. Under `KeepCount` and `Progressive` only a successful login or a password reset (`SetPasswordAsync`) clears the count and the lock; there is no unlock API yet |
| `MaxLockoutDuration` | 24 hours | Longest single lock under `Progressive`; must be between `LockoutDuration` and 365 days. Ignored otherwise |
| `ValidateSecurityStampOnEachRequest` | `false` | When on, the Themia JwtBearer scheme refuses a token whose stamp claim differs from the user's stored stamp, or whose user is inactive or deleted. Password change, disable and role change rotate the stamp; delete removes the user. Tokens without the claim are refused once on |
| `SecurityStampCacheDuration` | 30 seconds | How long a per-user stamp lookup is cached in process; bounds how long a revoked token keeps working. `TimeSpan.Zero` disables the cache |
| `DefaultTokenLifetime` | 1 hour | Expiry for generated tokens |
| `AllowPlatformLogin` | `true` | Whether platform users (`tenant_id IS NULL`) can log in |
| `ConnectionStringName` | `"Default"` | Connection string key used by Dapper |
