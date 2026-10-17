using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Hashing;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

// coord #0166 R1: a user-mutation hook that revokes sessions must reach the mutated user's tokens whatever
// scope the caller happens to be in, so OnUserMutatedAsync runs in the mutated user's own tenant scope.
public sealed class UserServiceTenantScopeTests : IDisposable
{
    private static readonly TenantId Acme = new("acme");
    private static readonly TenantId Beta = new("beta");

    private readonly TenantId? original = TenantContextAccessor.CurrentTenantId;
    private readonly List<User> store = [];
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-17T10:00:00Z"));
    private readonly IdentityModuleOptions options = new();
    private readonly ScopeProbeHooks hooks = new();
    private readonly ScopeProbeObserver observer = new();

    public void Dispose() => TenantContextAccessor.CurrentTenantId = original;

    private UserService ServiceIn(TenantId? ambient, IUserLifecycleHooks? lifecycle = null) => new(
        new FakeRepository<User>(store, u => u.Id) { AmbientTenant = ambient },
        new FakeUnitOfWork(), new Argon2idPasswordHasher(), clock, options, new DataFilterScope(),
        new FormattingOnlyPhoneNumberNormalizer(), lifecycle ?? hooks, [observer], NullLogger<UserService>.Instance);

    private User AddUser(string name, TenantId? tenant, bool active = true, string? phone = null)
    {
        var u = new User
        {
            UserName = name, NormalizedUserName = name.ToUpperInvariant(), TenantId = tenant, IsActive = active,
            PhoneNumber = phone, NormalizedPhoneNumber = phone, PhoneNumberConfirmed = phone is not null,
        };
        u.SetId(Guid.NewGuid());
        store.Add(u);
        return u;
    }

    [Fact]
    public async Task OnUserMutated_RunsInTheMutatedUsersTenantScope_AndRestoresTheCallersScope()
    {
        var user = AddUser("alice", Acme);
        TenantContextAccessor.CurrentTenantId = Beta;

        var result = await ServiceIn(Acme).SetActiveAsync(user.Id, false);

        Assert.True(result.Succeeded);
        Assert.Equal(Acme, hooks.SeenTenant);
        Assert.Equal(Acme, observer.SeenTenant);
        Assert.Equal(Beta, TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public async Task OnUserMutated_ForAPlatformUser_RunsWithNoTenant()
    {
        var user = AddUser("root", tenant: null);
        TenantContextAccessor.CurrentTenantId = Beta;

        await ServiceIn(Beta).SetActiveAsync(user.Id, false);

        Assert.True(hooks.Called);
        Assert.Null(hooks.SeenTenant);
        Assert.Equal(Beta, TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public async Task OnUserMutated_RestoresTheCallersScopeWhenTheHookThrows()
    {
        var user = AddUser("alice", Acme);
        TenantContextAccessor.CurrentTenantId = Beta;
        hooks.Throw = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceIn(Acme).SetActiveAsync(user.Id, false));

        Assert.Equal(Beta, TenantContextAccessor.CurrentTenantId);
    }

    [Fact]
    public async Task SetPhone_AllowsANumberHeldInAnotherTenant_ByDefault()
    {
        AddUser("alice", Acme, phone: "0812345678");
        var bob = AddUser("bob", Beta);

        Assert.True((await ServiceIn(Beta).SetPhoneNumberAsync(bob.Id, "081-234-5678")).Succeeded);
    }

    [Fact]
    public async Task SetPhone_WithTheOption_RefusesANumberHeldByAnActiveUserInAnotherTenant()
    {
        options.RejectPhoneNumberHeldInAnotherTenant = true;
        AddUser("alice", Acme, phone: "0812345678");
        var bob = AddUser("bob", Beta);

        var result = await ServiceIn(Beta).SetPhoneNumberAsync(bob.Id, "081-234-5678");

        Assert.Equal(UserMutationOutcome.Refused, result.Outcome);
        Assert.Equal(IdentityModuleOptions.PhoneNumberInAnotherTenantRefusalCode, result.RefusalCode);
        Assert.Contains("another tenant", result.Reason);
        Assert.Null(bob.PhoneNumber);
    }

    [Fact]
    public async Task SetPhone_WithTheOption_RefusesANumberHeldByAPlatformUser()
    {
        options.RejectPhoneNumberHeldInAnotherTenant = true;
        AddUser("root", tenant: null, phone: "0812345678");
        var bob = AddUser("bob", Beta);

        var result = await ServiceIn(Beta).SetPhoneNumberAsync(bob.Id, "0812345678");

        Assert.Equal(UserMutationOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task SetPhone_WithTheOption_AllowsANumberHeldOnlyByAnInactiveUserElsewhere()
    {
        options.RejectPhoneNumberHeldInAnotherTenant = true;
        AddUser("alice", Acme, active: false, phone: "0812345678");
        var bob = AddUser("bob", Beta);

        Assert.True((await ServiceIn(Beta).SetPhoneNumberAsync(bob.Id, "0812345678")).Succeeded);
    }

    [Fact]
    public async Task SetPhone_WithTheOption_StillReportsASameTenantClashAsDuplicate_AndAllowsClearingAndOwnNumber()
    {
        options.RejectPhoneNumberHeldInAnotherTenant = true;
        AddUser("carol", Beta, phone: "0811111111");
        var bob = AddUser("bob", Beta, phone: "0822222222");
        var service = ServiceIn(Beta);

        Assert.Equal(UserMutationOutcome.Duplicate, (await service.SetPhoneNumberAsync(bob.Id, "0811111111")).Outcome);
        Assert.True((await service.SetPhoneNumberAsync(bob.Id, "0822222222")).Succeeded);
        Assert.True((await service.SetPhoneNumberAsync(bob.Id, null)).Succeeded);
    }

    private sealed class ScopeProbeHooks : IUserLifecycleHooks
    {
        public bool Called { get; private set; }
        public bool Throw { get; set; }
        public TenantId? SeenTenant { get; private set; }

        public ValueTask OnUserMutatedAsync(Guid userId, UserMutation mutation, CancellationToken cancellationToken = default)
        {
            Called = true;
            SeenTenant = TenantContextAccessor.CurrentTenantId;
            return Throw ? throw new InvalidOperationException("hook failed") : ValueTask.CompletedTask;
        }
    }

    private sealed class ScopeProbeObserver : IIdentityEventObserver
    {
        public TenantId? SeenTenant { get; private set; }

        public Task OnUserMutatedAsync(Guid userId, UserMutation mutation, CancellationToken cancellationToken = default)
        {
            SeenTenant = TenantContextAccessor.CurrentTenantId;
            return Task.CompletedTask;
        }
    }
}
