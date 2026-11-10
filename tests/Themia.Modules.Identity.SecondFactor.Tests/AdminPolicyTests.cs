using System.Reflection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.SecondFactor;
using Xunit;

namespace Themia.Modules.Identity.SecondFactor.Tests;

public sealed class AdminPolicyTests
{
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly Guid TargetId = Guid.NewGuid();

    private readonly Dictionary<Guid, User> users = [];
    private readonly Dictionary<Guid, RoleLevel> levels = [];

    private static MfaSubject Actor(string? tenant = "t1") => new(ActorId.ToString("D"), tenant, new HashSet<string>());

    private RoleLevelActOnPolicy Policy() => new(
        Proxy<IUserService>.Create((name, args) => name == nameof(IUserService.FindByIdAsync)
            ? Task.FromResult(users.GetValueOrDefault((Guid)args[0]!))
            : throw new NotSupportedException(name)),
        Proxy<IUserLevelReader>.Create((_, args) => Task.FromResult(levels.GetValueOrDefault((Guid)args[0]!, RoleLevel.Level5))));

    private void Given(string? targetTenant, RoleLevel actor, RoleLevel target)
    {
        users[TargetId] = new User { TenantId = targetTenant is null ? null : new TenantId(targetTenant) };
        levels[ActorId] = actor;
        levels[TargetId] = target;
    }

    [Theory]
    [InlineData(RoleLevel.Level2, RoleLevel.Level3, true)]
    [InlineData(RoleLevel.Level3, RoleLevel.Level2, false)]
    [InlineData(RoleLevel.Level3, RoleLevel.Level3, false)]
    [InlineData(RoleLevel.Level1, RoleLevel.Level1, true)]
    public async Task CanActOnAsync_ShouldFollowRoleLevelPolicy(RoleLevel actor, RoleLevel target, bool expected)
    {
        Given("t1", actor, target);

        Assert.Equal(expected, await Policy().CanActOnAsync(Actor(), TargetId.ToString("D")));
    }

    [Fact]
    public async Task CanActOnAsync_ShouldRefuse_WhenTheTargetIsInAnotherTenant()
    {
        Given("t2", RoleLevel.Level1, RoleLevel.Level4);

        Assert.False(await Policy().CanActOnAsync(Actor(), TargetId.ToString("D")));
    }

    [Fact]
    public async Task CanActOnAsync_ShouldRefuse_WhenAPlatformTargetIsReachedFromATenant()
    {
        Given(null, RoleLevel.Level1, RoleLevel.Level4);

        Assert.False(await Policy().CanActOnAsync(Actor(), TargetId.ToString("D")));
    }

    [Fact]
    public async Task CanActOnAsync_ShouldRefuse_WhenTheTargetDoesNotExist()
    {
        levels[ActorId] = RoleLevel.Level1;

        Assert.False(await Policy().CanActOnAsync(Actor(), Guid.NewGuid().ToString("D")));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public async Task CanActOnAsync_ShouldRefuse_WhenTheTargetIsNotAGuid(string target)
    {
        Given("t1", RoleLevel.Level1, RoleLevel.Level4);

        Assert.False(await Policy().CanActOnAsync(Actor(), target));
    }

    [Fact]
    public async Task CanActOnAsync_ShouldRefuse_WhenTheTargetIsNotInCanonicalForm()
    {
        Given("t1", RoleLevel.Level1, RoleLevel.Level4);

        Assert.False(await Policy().CanActOnAsync(Actor(), TargetId.ToString("N")));
    }

    [Fact]
    public async Task OnResetAsync_ShouldRevokeEveryRefreshTokenOfTheTarget()
    {
        Guid? revoked = null;
        var tokens = Proxy<IRefreshTokenService>.Create((name, args) =>
        {
            Assert.Equal(nameof(IRefreshTokenService.RevokeAllForUserAsync), name);
            revoked = (Guid)args[0]!;
            return Task.FromResult(3);
        });

        await new RevokeSessionsResetObserver(tokens, UsersProxy()).OnResetAsync("t1", TargetId.ToString("D"));

        Assert.Equal(TargetId, revoked);
    }

    private IUserService UsersProxy() => Proxy<IUserService>.Create((name, args) => name == nameof(IUserService.FindByIdAsync)
        ? Task.FromResult(users.GetValueOrDefault((Guid)args[0]!))
        : throw new NotSupportedException(name));

    private static IRefreshTokenService TokensRevoking(int count) => Proxy<IRefreshTokenService>.Create((_, _) => Task.FromResult(count));

    [Fact]
    public async Task OnResetAsync_ShouldNotThrow_WhenTheTargetHasNoActiveSessionsButIsInScope()
    {
        Given("t1", RoleLevel.Level1, RoleLevel.Level4);

        await new RevokeSessionsResetObserver(TokensRevoking(0), UsersProxy()).OnResetAsync("t1", TargetId.ToString("D"));
    }

    [Fact]
    public async Task OnResetAsync_ShouldThrow_WhenNothingWasRevokedAndTheTargetIsNotVisibleInScope()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new RevokeSessionsResetObserver(TokensRevoking(0), UsersProxy()).OnResetAsync("t1", TargetId.ToString("D")));
    }

    [Fact]
    public async Task OnResetAsync_ShouldThrow_WhenTheTargetIsNotAUserId()
    {
        var tokens = Proxy<IRefreshTokenService>.Create((name, _) => throw new NotSupportedException(name));

        await Assert.ThrowsAsync<ArgumentException>(() => new RevokeSessionsResetObserver(tokens, UsersProxy()).OnResetAsync("t1", "nope"));
    }

    // Only the members a test touches are answered; any other call fails the test.
    private class Proxy<T> : DispatchProxy
        where T : class
    {
        private Func<string, object?[], object?> handler = (name, _) => throw new NotSupportedException(name);

        public static T Create(Func<string, object?[], object?> handler)
        {
            var proxy = Create<T, Proxy<T>>();
            ((Proxy<T>)(object)proxy).handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => handler(targetMethod!.Name, args ?? []);
    }
}
