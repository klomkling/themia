using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

// coord #0166: with a claims-only tenant strategy the refresh and logout requests carry no JWT, so the
// ambient scope is null. The token hash is the proof; the owner is found without the tenant filter.
public class RefreshTokenServiceTenantScopeTests
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Beta = new("beta");

    private readonly List<User> users = [];
    private readonly List<RefreshToken> tokens = [];
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-17T10:00:00Z"));
    private readonly CapturingLogger logger = new();

    private RefreshTokenService ServiceInScope(TenantId? ambient, IdentityModuleOptions? options = null) => new(
        new FakeRepository<User>(users, u => u.Id) { AmbientTenant = ambient },
        new FakeRepository<RefreshToken>(tokens, t => t.Id) { AmbientTenant = ambient },
        new FakeUnitOfWork(), clock, options ?? new IdentityModuleOptions(), logger);

    private User AddUser(string name, TenantId? tenant, bool active = true)
    {
        var u = new User { UserName = name, NormalizedUserName = name.ToUpperInvariant(), TenantId = tenant, IsActive = active };
        u.SetId(Guid.NewGuid());
        users.Add(u);
        return u;
    }

    private async Task<string> IssueAsync(User user)
    {
        var issued = await ServiceInScope(user.TenantId).IssueAsync(user.Id);
        return issued.RawToken;
    }

    [Fact]
    public async Task ValidateAndRotate_WorksForATenantUserUnderANullScope()
    {
        var user = AddUser("alice", Acme);
        var raw = await IssueAsync(user);

        var result = await ServiceInScope(null).ValidateAndRotateAsync(raw);

        Assert.True(result.TryGetSuccess(out var owner, out var next));
        Assert.Equal(user.Id, owner.Id);
        Assert.NotEqual(raw, next.RawToken);
    }

    [Fact]
    public async Task ValidateAndRotate_RefusesAnInactiveUserWithoutConsumingTheToken()
    {
        var user = AddUser("alice", Acme, active: false);
        var raw = await IssueAsync(user);

        var result = await ServiceInScope(null).ValidateAndRotateAsync(raw);

        Assert.Equal(RefreshOutcome.Invalid, result.Outcome);
        Assert.All(tokens, t => Assert.Null(t.ConsumedAt));
        Assert.Single(tokens);
    }

    [Fact]
    public async Task ValidateAndRotate_RefusesAPlatformUserWhenPlatformLoginIsOff_WithoutConsumingTheToken()
    {
        var user = AddUser("root", null);
        var raw = await IssueAsync(user);

        var result = await ServiceInScope(null, new IdentityModuleOptions { AllowPlatformLogin = false }).ValidateAndRotateAsync(raw);

        Assert.Equal(RefreshOutcome.Invalid, result.Outcome);
        Assert.All(tokens, t => Assert.Null(t.ConsumedAt));
        Assert.Single(tokens);
    }

    [Fact]
    public async Task ValidateAndRotate_RotatesAPlatformUserWhenPlatformLoginIsOn()
    {
        var user = AddUser("root", null);
        var raw = await IssueAsync(user);

        var result = await ServiceInScope(null, new IdentityModuleOptions { AllowPlatformLogin = true }).ValidateAndRotateAsync(raw);

        Assert.True(result.TryGetSuccess(out var owner, out _));
        Assert.Equal(user.Id, owner.Id);
    }

    [Fact]
    public async Task ValidateAndRotate_IgnoresAllowPlatformLoginForATenantUser()
    {
        var user = AddUser("alice", Acme);
        var raw = await IssueAsync(user);

        var result = await ServiceInScope(null, new IdentityModuleOptions { AllowPlatformLogin = false }).ValidateAndRotateAsync(raw);

        Assert.True(result.TryGetSuccess(out _, out _));
    }

    [Fact]
    public async Task ResolveOwner_FindsATenantUsersTokenUnderANullScope()
    {
        var user = AddUser("alice", Acme);
        var raw = await IssueAsync(user);

        Assert.Equal(user.Id, await ServiceInScope(null).ResolveOwnerAsync(raw));
    }

    [Fact]
    public async Task FindOwner_ReportsTheOwnersTenantUnderANullScope_AndNullForAnUnknownToken()
    {
        var alice = AddUser("alice", Acme);
        var root = AddUser("root", tenant: null);
        var aliceToken = await IssueAsync(alice);
        var rootToken = await IssueAsync(root);
        var service = ServiceInScope(null);

        Assert.Equal(new RefreshTokenOwner(alice.Id, Acme), await service.FindOwnerAsync(aliceToken));
        Assert.Equal(new RefreshTokenOwner(root.Id, null), await service.FindOwnerAsync(rootToken));
        Assert.Null(await service.FindOwnerAsync("not-a-token"));
    }

    [Fact]
    public async Task Revoke_OfASingleSession_ReportsHowManyTokensItRevoked()
    {
        var user = AddUser("alice", Acme);
        var raw = await IssueAsync(user);

        var revoked = await ServiceInScope(null).RevokeAsync(raw, allForUser: false);

        Assert.Equal(1, revoked);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
        // A revoked token presented again is the existing replay case (ReuseDetected), not a success.
        Assert.False((await ServiceInScope(null).ValidateAndRotateAsync(raw)).TryGetSuccess(out _, out _));
    }

    [Fact]
    public async Task Revoke_OfAllSessions_RevokesEveryActiveTokenOfTheOwnerAndNobodyElse()
    {
        var alice = AddUser("alice", Acme);
        var bob = AddUser("bob", Beta);
        var first = await IssueAsync(alice);
        await IssueAsync(alice);
        await IssueAsync(bob);

        var revoked = await ServiceInScope(null).RevokeAsync(first, allForUser: true);

        Assert.Equal(2, revoked);
        Assert.Equal(1, tokens.Count(t => t.RevokedAt is null));
        Assert.All(tokens.Where(t => t.RevokedAt is null), t => Assert.Equal(bob.Id, t.UserId));
    }

    [Fact]
    public async Task Revoke_OfAnUnknownOrAlreadyRevokedToken_ReportsZero()
    {
        var user = AddUser("alice", Acme);
        var raw = await IssueAsync(user);
        var service = ServiceInScope(null);

        Assert.Equal(0, await service.RevokeAsync("not-a-token", allForUser: false));
        Assert.Equal(1, await service.RevokeAsync(raw, allForUser: false));
        Assert.Equal(0, await service.RevokeAsync(raw, allForUser: false));
    }

    [Fact]
    public async Task RevokeAllForUser_StaysCallerScoped_AndSaysSoWhenTheUserIsOutOfScope()
    {
        var user = AddUser("alice", Acme);
        await IssueAsync(user);

        var revoked = await ServiceInScope(Beta).RevokeAllForUserAsync(user.Id);

        Assert.Equal(0, revoked);
        Assert.All(tokens, t => Assert.Null(t.RevokedAt));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(user.Id.ToString()));
    }

    [Fact]
    public async Task RevokeAllForUser_RevokesWhenCalledFromTheUsersOwnScope()
    {
        var user = AddUser("alice", Acme);
        await IssueAsync(user);
        await IssueAsync(user);

        Assert.Equal(2, await ServiceInScope(Acme).RevokeAllForUserAsync(user.Id));
    }

    private sealed class CapturingLogger : ILogger<RefreshTokenService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
