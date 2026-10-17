using Microsoft.Extensions.Logging.Abstractions;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.AspNetCore.Authentication;
using Xunit;

namespace Themia.Modules.Identity.AspNetCore.Tests;

// coord #0166: refresh and logout carry no JWT, so under a claims-only tenant strategy the ambient scope is
// null. After the refresh token has proved who the owner is, the rest of the flow runs as the owner's tenant.
public sealed class AuthenticationFlowTenantScopeTests : IDisposable
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Beta = new("beta");

    private readonly TenantId? original = TenantContextAccessor.CurrentTenantId;
    private readonly FakeRefreshTokenService refresh = new();
    private readonly FakeClaimsPrincipalFactory principals = new();
    private readonly ScopeHooks hooks = new();
    private readonly TenantProbeObserver observer = new();

    public void Dispose() => TenantContextAccessor.CurrentTenantId = original;

    private AuthenticationFlow Flow() =>
        new(new FakeUserService(), principals, new FakeAccessTokenService(), refresh, new FakePasswordHasher(),
            hooks, [observer], TimeProvider.System, NullLogger<AuthenticationFlow>.Instance);

    private static RefreshIssue Successor() => new("new-refresh", DateTimeOffset.UtcNow.AddDays(14), Guid.NewGuid());

    [Fact]
    public async Task Refresh_RunsThePrincipalFactoryAndHooksAsTheTokenOwnersTenant_ThenRestoresTheScope()
    {
        refresh.RotateResult = RefreshValidationResult.Success(new User { UserName = "u", TenantId = Acme }, Successor());
        TenantContextAccessor.CurrentTenantId = null;

        var result = await Flow().RefreshAsync("token");

        Assert.True(result.Succeeded);
        Assert.Equal(Acme, principals.TenantSeen);
        Assert.Equal(Acme, hooks.RefreshSucceededTenant);
        Assert.Null(TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public async Task Refresh_ForAPlatformUser_RunsWithNoTenant()
    {
        refresh.RotateResult = RefreshValidationResult.Success(new User { UserName = "root", TenantId = null }, Successor());
        TenantContextAccessor.CurrentTenantId = Beta;

        await Flow().RefreshAsync("token");

        Assert.True(principals.Called);
        Assert.Null(principals.TenantSeen);
        Assert.Equal(Beta, TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public async Task Refresh_BeforeRefreshHook_RunsBeforeAnyOwnerIsKnown_SoItSeesTheCallersScope()
    {
        refresh.RotateResult = RefreshValidationResult.Success(new User { UserName = "u", TenantId = Acme }, Successor());
        TenantContextAccessor.CurrentTenantId = null;

        await Flow().RefreshAsync("token");

        Assert.True(hooks.BeforeRefreshCalled);
        Assert.Null(hooks.BeforeRefreshTenant);
    }

    [Fact]
    public async Task Refresh_RestoresTheScopeWhenAHookThrows()
    {
        refresh.RotateResult = RefreshValidationResult.Success(new User { UserName = "u", TenantId = Acme }, Successor());
        hooks.ThrowOnRefreshSucceeded = true;
        TenantContextAccessor.CurrentTenantId = Beta;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Flow().RefreshAsync("token"));

        Assert.Equal(Beta, TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public async Task Logout_ReportsRevokedOnlyWhenSomethingWasRevoked()
    {
        refresh.RevokeResult = 2;

        Assert.Equal(LogoutOutcome.Revoked, await Flow().LogoutAsync("token", allSessions: true));
    }

    [Fact]
    public async Task Logout_ReportsNotFoundAndRaisesNoLogoutEventWhenNothingWasRevoked()
    {
        refresh.RevokeResult = 0;

        var outcome = await Flow().LogoutAsync("token", allSessions: false);

        Assert.Equal(LogoutOutcome.NotFound, outcome);
        Assert.DoesNotContain(nameof(IIdentityEventObserver.OnLogoutAsync), observer.Calls);
        Assert.False(hooks.LogoutCalled);
    }

    [Fact]
    public async Task Logout_RaisesTheLogoutEventsWhenASessionWasRevoked()
    {
        refresh.RevokeResult = 1;

        await Flow().LogoutAsync("token", allSessions: false);

        Assert.Contains(nameof(IIdentityEventObserver.OnLogoutAsync), observer.Calls);
        Assert.True(hooks.LogoutCalled);
    }

    [Fact]
    public async Task Logout_RunsTheLogoutHooksAndObserversAsTheTokenOwnersTenant_ThenRestoresTheScope()
    {
        refresh.RevokeResult = 1;
        refresh.OwnerId = Guid.NewGuid();
        refresh.OwnerTenant = Acme;
        TenantContextAccessor.CurrentTenantId = Beta;

        await Flow().LogoutAsync("token", allSessions: false);

        Assert.Equal(Acme, hooks.LogoutTenant);
        Assert.Equal(Acme, observer.LogoutTenant);
        Assert.Equal(Beta, TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public async Task Logout_RestoresTheScopeWhenALogoutHookThrows()
    {
        refresh.RevokeResult = 1;
        refresh.OwnerId = Guid.NewGuid();
        refresh.OwnerTenant = Acme;
        hooks.ThrowOnLogout = true;
        TenantContextAccessor.CurrentTenantId = Beta;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Flow().LogoutAsync("token", allSessions: false));

        Assert.Equal(Beta, TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public async Task Logout_FallsBackToTheCallersScopeWhenTheOwnerIsUnknown()
    {
        refresh.RevokeResult = 1;
        refresh.OwnerId = null;
        TenantContextAccessor.CurrentTenantId = Beta;

        await Flow().LogoutAsync("token", allSessions: false);

        Assert.Equal(Beta, hooks.LogoutTenant);
    }

    private sealed class ScopeHooks : AuthenticationHooksBase
    {
        public TenantId? RefreshSucceededTenant { get; private set; }
        public TenantId? BeforeRefreshTenant { get; private set; }
        public bool BeforeRefreshCalled { get; private set; }
        public bool LogoutCalled { get; private set; }
        public TenantId? LogoutTenant { get; private set; }
        public bool ThrowOnLogout { get; set; }
        public bool ThrowOnRefreshSucceeded { get; set; }

        public override Task OnBeforeRefreshAsync(BeforeRefreshContext context, CancellationToken cancellationToken = default)
        {
            BeforeRefreshCalled = true;
            BeforeRefreshTenant = TenantContextAccessor.CurrentTenantId;
            return Task.CompletedTask;
        }

        public override Task OnRefreshSucceededAsync(RefreshSucceededContext context, CancellationToken cancellationToken = default)
        {
            RefreshSucceededTenant = TenantContextAccessor.CurrentTenantId;
            return ThrowOnRefreshSucceeded ? throw new InvalidOperationException("hook failed") : Task.CompletedTask;
        }

        public override Task OnLogoutAsync(LogoutContext context, CancellationToken cancellationToken = default)
        {
            LogoutCalled = true;
            LogoutTenant = TenantContextAccessor.CurrentTenantId;
            return ThrowOnLogout ? throw new InvalidOperationException("hook failed") : Task.CompletedTask;
        }
    }
}

internal sealed class TenantProbeObserver : IIdentityEventObserver
{
    public List<string> Calls { get; } = [];
    public TenantId? LogoutTenant { get; private set; }

    public Task OnLogoutAsync(Guid? userId, bool allSessions, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(OnLogoutAsync));
        LogoutTenant = TenantContextAccessor.CurrentTenantId;
        return Task.CompletedTask;
    }
}
