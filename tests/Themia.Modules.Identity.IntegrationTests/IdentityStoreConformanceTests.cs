using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Modules.Identity.Abstractions;
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
