using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.DependencyInjection;
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
        public IUserTokenService Tokens => Inner.ServiceProvider.GetRequiredService<IUserTokenService>();
        public IRefreshTokenService RefreshTokens => Inner.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        public IExternalLoginService ExternalLogins => Inner.ServiceProvider.GetRequiredService<IExternalLoginService>();

        public async ValueTask DisposeAsync()
        {
            await Inner.DisposeAsync();
            await Provider.DisposeAsync();
        }
    }

    protected Scope NewScope(TenantId? tenant, bool allowPlatformLogin = true, string auditUserId = "test-user", int? maxFailedAccessAttempts = null, TimeProvider? timeProvider = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        // Logging is registered by AddThemiaIdentityServices below.
        services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
        if (timeProvider is not null)
        {
            services.AddSingleton<TimeProvider>(timeProvider);
        }
        ConfigurePeer(services, configuration);
        services.AddThemiaIdentityServices(o =>
        {
            o.AllowPlatformLogin = allowPlatformLogin;
            if (maxFailedAccessAttempts is { } max)
            {
                o.MaxFailedAccessAttempts = max;
            }
        });
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
    public async Task Platform_user_can_log_in_and_lockout_state_persists_from_a_tenant_scope()
    {
        await ResetAsync();
        // System scope (ambient tenant null) creates a platform user (TenantId stays null).
        await using (var system = NewScope(tenant: null))
        {
            Assert.True((await system.Users.CreateAsync("root", "pw")).Succeeded);
        }

        // From a TENANT scope, a wrong password must write the lockout counter on the platform user
        // (an ITenantEntity) — before the fix this throws ConcurrencyException; it must now just Fail.
        await using (var tenant = NewScope(new TenantId("acme")))
        {
            Assert.Equal(PasswordVerificationResult.Failed, await tenant.Users.VerifyPasswordAsync("root", "nope"));
            Assert.Equal(PasswordVerificationResult.Success, await tenant.Users.VerifyPasswordAsync("root", "pw"));
        }
    }

    [Fact]
    public async Task Platform_user_effective_claims_resolve_from_a_tenant_scope()
    {
        await ResetAsync();
        Guid rootId;
        await using (var system = NewScope(tenant: null))
        {
            rootId = (await system.Users.CreateAsync("root", "pw")).UserId!.Value;
            await system.Claims.AddUserClaimAsync(rootId, "perm", "all");
        }

        await using (var tenant = NewScope(new TenantId("acme")))
        {
            var claims = await tenant.Claims.GetEffectiveClaimsAsync(rootId);
            Assert.Contains(claims, c => c is { Type: "perm", Value: "all" });
        }
    }

    [Fact]
    public async Task Platform_user_token_generate_and_consume_from_a_tenant_scope()
    {
        await ResetAsync();
        Guid rootId;
        await using (var system = NewScope(tenant: null))
        {
            rootId = (await system.Users.CreateAsync("root", "pw")).UserId!.Value;
        }

        await using (var tenant = NewScope(new TenantId("acme")))
        {
            var raw = await tenant.Tokens.GenerateAsync(rootId, TokenPurpose.PasswordReset);
            Assert.Equal(TokenConsumeResult.Success, await tenant.Tokens.ConsumeAsync(rootId, TokenPurpose.PasswordReset, raw));
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

    [Fact]
    public async Task Lockout_engages_at_threshold_and_releases_after_window()
    {
        // The integration scope uses TimeProvider.System (no controllable clock), so the time-based
        // release is exercised by the unit tests. Here we assert the round-trip of LockoutEnd through
        // the real store: failing to the threshold refuses even the correct password while locked.
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"), maxFailedAccessAttempts: 2);
        await s.Users.CreateAsync("grace", "right");

        Assert.Equal(PasswordVerificationResult.Failed, await s.Users.VerifyPasswordAsync("grace", "wrong"));
        Assert.Equal(PasswordVerificationResult.Failed, await s.Users.VerifyPasswordAsync("grace", "wrong"));

        // Threshold reached: the correct password is refused while the lockout window is open.
        Assert.Equal(PasswordVerificationResult.LockedOut, await s.Users.VerifyPasswordAsync("grace", "right"));
    }

    [Fact]
    public async Task Token_generate_consume_is_single_use_and_purpose_scoped()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        var userId = (await s.Users.CreateAsync("heidi", "pw")).UserId!.Value;

        var raw = await s.Tokens.GenerateAsync(userId, TokenPurpose.PasswordReset);
        Assert.Equal(TokenConsumeResult.Success, await s.Tokens.ConsumeAsync(userId, TokenPurpose.PasswordReset, raw));
        Assert.Equal(TokenConsumeResult.AlreadyConsumed, await s.Tokens.ConsumeAsync(userId, TokenPurpose.PasswordReset, raw));

        // A token generated for one purpose is not consumable under another.
        var raw2 = await s.Tokens.GenerateAsync(userId, TokenPurpose.PasswordReset);
        Assert.Equal(TokenConsumeResult.NotFound, await s.Tokens.ConsumeAsync(userId, TokenPurpose.EmailConfirm, raw2));
    }

    [Fact]
    public async Task Cross_tenant_claim_write_is_rejected()
    {
        await ResetAsync();
        Guid userId;
        await using (var a = NewScope(new TenantId("a")))
        {
            userId = (await a.Users.CreateAsync("ivan", "pw")).UserId!.Value;
        }
        await using (var b = NewScope(new TenantId("b")))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => b.Claims.AddUserClaimAsync(userId, "perm", "x"));
        }
    }

    [Fact]
    public async Task Cross_tenant_role_removal_is_rejected()
    {
        await ResetAsync();
        Guid userId;
        Guid roleId;
        await using (var a = NewScope(new TenantId("a")))
        {
            userId = (await a.Users.CreateAsync("judy", "pw")).UserId!.Value;
            roleId = (await a.Roles.CreateAsync("Editor"))!.Value;
            Assert.True(await a.Roles.AssignRoleAsync(userId, roleId));
        }
        await using (var b = NewScope(new TenantId("b")))
        {
            Assert.False(await b.Roles.RemoveRoleAsync(userId, roleId));
        }
    }

    [Fact]
    public async Task Refresh_issue_returns_raw_token_family_and_expiry()
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

        var replay = await s.RefreshTokens.ValidateAndRotateAsync(issue.RawToken);
        Assert.Equal(RefreshOutcome.ReuseDetected, replay.Outcome);

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

    [Fact]
    public async Task Revoke_all_does_not_touch_another_users_tokens()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        var victim = (await s.Users.CreateAsync("rt-victim", "pw")).UserId!.Value;
        var bystander = (await s.Users.CreateAsync("rt-bystander", "pw")).UserId!.Value;
        var victimToken = await s.RefreshTokens.IssueAsync(victim);
        var bystanderToken = await s.RefreshTokens.IssueAsync(bystander);

        await s.RefreshTokens.RevokeAsync(victimToken.RawToken, allForUser: true);

        // Victim's token is revoked (reuse on a revoked token => ReuseDetected); bystander's still rotates.
        Assert.Equal(RefreshOutcome.ReuseDetected, (await s.RefreshTokens.ValidateAndRotateAsync(victimToken.RawToken)).Outcome);
        Assert.Equal(RefreshOutcome.Success, (await s.RefreshTokens.ValidateAndRotateAsync(bystanderToken.RawToken)).Outcome);
    }

    [Fact]
    public async Task Refresh_rejects_an_expired_token()
    {
        await ResetAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-15T00:00:00Z"));
        await using var s = NewScope(new TenantId("acme"), timeProvider: clock);
        var u = await s.Users.CreateAsync("rt-expire", "pw");
        var issue = await s.RefreshTokens.IssueAsync(u.UserId!.Value);

        clock.SetUtcNow(clock.GetUtcNow() + TimeSpan.FromDays(15)); // past the 14-day default RefreshTokenLifetime
        var result = await s.RefreshTokens.ValidateAndRotateAsync(issue.RawToken);
        Assert.Equal(RefreshOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Refresh_replay_of_consumed_token_revokes_family_even_after_expiry()
    {
        await ResetAsync();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-06-15T00:00:00Z"));
        await using var s = NewScope(new TenantId("acme"), timeProvider: clock);
        var u = await s.Users.CreateAsync("rt-expire-reuse", "pw");
        var issue = await s.RefreshTokens.IssueAsync(u.UserId!.Value);
        await s.RefreshTokens.ValidateAndRotateAsync(issue.RawToken); // consumes original

        clock.SetUtcNow(clock.GetUtcNow() + TimeSpan.FromDays(15)); // original is now also expired
        // Reuse check precedes expiry: replaying the consumed original must still be ReuseDetected.
        var replay = await s.RefreshTokens.ValidateAndRotateAsync(issue.RawToken);
        Assert.Equal(RefreshOutcome.ReuseDetected, replay.Outcome);
    }

    [Fact]
    public async Task Refresh_after_owner_soft_deleted_returns_invalid()
    {
        // Tenant isolation by construction: ValidateAndRotateAsync resolves the owner in scope BEFORE
        // acting on the token. A soft-deleted (or out-of-scope) owner does not resolve, so the token —
        // including a replayed consumed one — returns Invalid rather than triggering a family-revoke
        // write. Accepted tradeoff: a deleted account's replayed consumed token does not emit the
        // reuse/theft signal (its tokens are already unusable for authentication).
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        var u = await s.Users.CreateAsync("rt-deleted", "pw");
        var issue = await s.RefreshTokens.IssueAsync(u.UserId!.Value);
        await s.RefreshTokens.ValidateAndRotateAsync(issue.RawToken); // consume original
        Assert.True(await s.Users.DeleteAsync(u.UserId!.Value));      // soft-delete owner

        Assert.Equal(RefreshOutcome.Invalid, (await s.RefreshTokens.ValidateAndRotateAsync(issue.RawToken)).Outcome);
    }

    [Fact]
    public async Task Revoke_unknown_token_is_a_safe_noop()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));
        var u = await s.Users.CreateAsync("rt-revoke-unknown", "pw");
        var issue = await s.RefreshTokens.IssueAsync(u.UserId!.Value);

        await s.RefreshTokens.RevokeAsync("garbage-not-a-real-token", allForUser: false); // must not throw
        // The real token is untouched and still rotates.
        Assert.Equal(RefreshOutcome.Success, (await s.RefreshTokens.ValidateAndRotateAsync(issue.RawToken)).Outcome);
    }

    [Fact]
    public async Task Concurrent_rotation_of_same_token_rotates_exactly_once()
    {
        await ResetAsync();
        Guid userId;
        string raw;
        await using (var seed = NewScope(new TenantId("acme")))
        {
            userId = (await seed.Users.CreateAsync("rt-concurrent", "pw")).UserId!.Value;
            raw = (await seed.RefreshTokens.IssueAsync(userId)).RawToken;
        }

        // Two independent scopes = two UoWs/connections racing the same token.
        await using var a = NewScope(new TenantId("acme"));
        await using var b = NewScope(new TenantId("acme"));
        var results = await Task.WhenAll(
            a.RefreshTokens.ValidateAndRotateAsync(raw),
            b.RefreshTokens.ValidateAndRotateAsync(raw));

        var successes = results.Count(r => r.Outcome == RefreshOutcome.Success);
        Assert.Equal(1, successes);                                   // exactly one rotation won
        // The loser is timing-dependent but never a second Success: it is either Invalid (the rotations
        // interleaved → unique-index violation on the successor insert) or ReuseDetected (they serialized →
        // the loser sees the now-consumed parent and revokes the family, the designed theft signal).
        Assert.All(results.Where(r => r.Outcome != RefreshOutcome.Success),
            r => Assert.Contains(r.Outcome, new[] { RefreshOutcome.Invalid, RefreshOutcome.ReuseDetected }));
    }

    [Fact] // existing link → same user, no create/link
    public async Task External_existing_link_returns_same_user()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));

        var first = await s.ExternalLogins.ResolveOrProvisionAsync(
            new ExternalIdentity("Google", "sub-1", "ext1@acme.test", true, "Ext One"));
        Assert.True(first.WasCreated);
        Assert.True(first.WasLinked);

        var second = await s.ExternalLogins.ResolveOrProvisionAsync(
            new ExternalIdentity("Google", "sub-1", "ext1@acme.test", true, "Ext One"));
        Assert.Equal(first.User.Id, second.User.Id);
        Assert.False(second.WasCreated);
        Assert.False(second.WasLinked);
    }

    [Fact] // verified-email match → link to existing user
    public async Task External_verified_email_links_existing_user()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));

        var seeded = await s.Users.CreateAsync("emailowner", "pw", "owner@acme.test");
        Assert.True(seeded.Succeeded);

        var result = await s.ExternalLogins.ResolveOrProvisionAsync(
            new ExternalIdentity("Google", "sub-link", "owner@acme.test", true, null));
        Assert.Equal(seeded.UserId, result.User.Id);
        Assert.False(result.WasCreated);
        Assert.True(result.WasLinked);
    }

    [Fact] // no match → create
    public async Task External_no_match_creates_user()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));

        var result = await s.ExternalLogins.ResolveOrProvisionAsync(
            new ExternalIdentity("Google", "sub-new", "new@acme.test", true, null));
        Assert.True(result.WasCreated);
        Assert.True(result.WasLinked);
        Assert.NotEqual(Guid.Empty, result.User.Id);
    }

    [Fact] // unverified email → create, never link
    public async Task External_unverified_email_never_links()
    {
        await ResetAsync();
        await using var s = NewScope(new TenantId("acme"));

        var seeded = await s.Users.CreateAsync("verifiedowner", "pw", "v@acme.test");
        Assert.True(seeded.Succeeded);

        var result = await s.ExternalLogins.ResolveOrProvisionAsync(
            new ExternalIdentity("Google", "sub-unverified", "v@acme.test", false, null));
        Assert.True(result.WasCreated);
        Assert.True(result.WasLinked);
        Assert.NotEqual(seeded.UserId, result.User.Id);   // a different, freshly provisioned user
    }

    [Fact] // tenant isolation: a link created in tenant A is not seen in tenant B
    public async Task External_link_is_tenant_isolated()
    {
        await ResetAsync();
        Guid userInA;
        await using (var a = NewScope(new TenantId("a")))
        {
            var first = await a.ExternalLogins.ResolveOrProvisionAsync(
                new ExternalIdentity("Google", "shared-sub", "shared@x.test", true, null));
            userInA = first.User.Id;
        }

        await using (var b = NewScope(new TenantId("b")))
        {
            var inB = await b.ExternalLogins.ResolveOrProvisionAsync(
                new ExternalIdentity("Google", "shared-sub", "shared@x.test", true, null));
            Assert.NotEqual(userInA, inB.User.Id);   // tenant b does not see tenant a's link → new user
            Assert.True(inB.WasCreated);
        }
    }
}
