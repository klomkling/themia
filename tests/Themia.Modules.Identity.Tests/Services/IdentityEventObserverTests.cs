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

/// <summary>Coverage for <see cref="IIdentityEventObserver"/> fan-out across the two <see cref="UserService"/>
/// paths <see cref="AuthenticationFlow"/> and <c>ExternalAuthenticationFlow</c> do not own: lockout
/// (applied inside <see cref="UserService.VerifyPasswordAsync"/>, with no notification before this seam)
/// and every user-credential mutation.</summary>
public sealed class IdentityEventObserverTests
{
    private readonly List<User> store = [];
    private readonly FakeRepository<User> repo;
    private readonly FakeUnitOfWork uow = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-06-14T00:00:00Z"));
    private readonly IdentityModuleOptions options = new();
    private readonly RecordingUserLifecycleHooks hooks = new();
    private readonly RecordingIdentityEventObserver observer = new();
    private readonly UserService sut;

    public IdentityEventObserverTests()
    {
        repo = new FakeRepository<User>(store, u => u.Id) { AmbientTenant = new TenantId("acme") };
        sut = Build(observer);
    }

    private UserService Build(params IIdentityEventObserver[] observers) =>
        new(repo, uow, new Argon2idPasswordHasher(), clock, options, new DataFilterScope(),
            new FormattingOnlyPhoneNumberNormalizer(), hooks, observers, NullLogger<UserService>.Instance);

    [Fact]
    public async Task Lockout_raises_OnLockedOut_when_the_threshold_is_reached()
    {
        options.MaxFailedAccessAttempts = 2;
        var created = await sut.CreateAsync("erin", "right");

        await sut.VerifyPasswordAsync("erin", "wrong");
        Assert.DoesNotContain(nameof(IIdentityEventObserver.OnLockedOutAsync), observer.Calls);

        await sut.VerifyPasswordAsync("erin", "wrong");

        Assert.Contains(nameof(IIdentityEventObserver.OnLockedOutAsync), observer.Calls);
        Assert.Equal(created.UserId!.Value, observer.LastLockedOut!.Value.UserId);
    }

    [Fact]
    public async Task Lockout_does_not_raise_OnLockedOut_again_on_a_subsequent_failure_while_still_locked()
    {
        options.MaxFailedAccessAttempts = 1;
        await sut.CreateAsync("mia", "right");

        await sut.VerifyPasswordAsync("mia", "wrong"); // locks out, raises once
        var callsAfterFirstLockout = observer.Calls.Count(c => c == nameof(IIdentityEventObserver.OnLockedOutAsync));

        await sut.VerifyPasswordAsync("mia", "right"); // still locked out; LockedOut is returned before the counter logic runs

        Assert.Equal(1, callsAfterFirstLockout);
        Assert.Equal(1, observer.Calls.Count(c => c == nameof(IIdentityEventObserver.OnLockedOutAsync)));
    }

    [Theory]
    [InlineData(nameof(UserService.SetPasswordAsync))]
    [InlineData(nameof(UserService.SetActiveAsync))]
    [InlineData(nameof(UserService.ConfirmEmailAsync))]
    [InlineData(nameof(UserService.ConfirmPhoneNumberAsync))]
    [InlineData(nameof(UserService.DeleteAsync))]
    public async Task Every_user_mutation_raises_OnUserMutated(string method)
    {
        var created = await sut.CreateAsync("mut-" + method.ToLowerInvariant(), "pw");
        var userId = created.UserId!.Value;

        // Give email/phone something to confirm where the method needs it.
        var user = Assert.Single(store, u => u.Id == userId);
        user.NormalizedEmail = "MUT@EXAMPLE.COM";
        user.NormalizedPhoneNumber = "0800000000";

        var expectedMutation = await InvokeAsync(method, userId);

        Assert.Contains(nameof(IIdentityEventObserver.OnUserMutatedAsync), observer.Calls);
        Assert.Contains((userId, expectedMutation), observer.Mutations);
    }

    [Fact]
    public async Task CreateAsync_raises_OnUserMutated_with_Created()
    {
        var created = await sut.CreateAsync("frank", "pw");

        Assert.Contains((created.UserId!.Value, UserMutation.Created), observer.Mutations);
    }

    [Fact]
    public async Task CreateExternalUserAsync_raises_OnUserMutated_with_Created()
    {
        var created = await sut.CreateExternalUserAsync("gina", "gina@example.com", emailVerified: true);

        Assert.Contains((created.UserId!.Value, UserMutation.Created), observer.Mutations);
    }

    [Theory]
    [InlineData(nameof(UserService.SetEmailAsync))]
    [InlineData(nameof(UserService.ConfirmEmailAsync))]
    [InlineData(nameof(UserService.SetPhoneNumberAsync))]
    [InlineData(nameof(UserService.ConfirmPhoneNumberAsync))]
    [InlineData(nameof(UserService.SetPasswordAsync))]
    [InlineData(nameof(UserService.SetActiveAsync))]
    [InlineData(nameof(UserService.DeleteAsync))]
    public async Task A_refused_mutation_raises_OnUserMutationRefused(string method)
    {
        const string reason = "no";
        hooks.RefuseSetEmail = reason;
        hooks.RefuseConfirmEmail = reason;
        hooks.RefuseSetPhoneNumber = reason;
        hooks.RefuseConfirmPhoneNumber = reason;
        hooks.RefuseSetPassword = reason;
        hooks.RefuseSetActive = reason;
        hooks.RefuseDelete = reason;

        var created = await sut.CreateAsync("refuse-" + method.ToLowerInvariant(), "pw");
        var userId = created.UserId!.Value;
        var user = Assert.Single(store, u => u.Id == userId);
        user.NormalizedEmail = "REFUSE@EXAMPLE.COM";
        user.NormalizedPhoneNumber = "0800000000";

        var expectedMutation = await InvokeRefusableAsync(method, userId);

        Assert.Contains(nameof(IIdentityEventObserver.OnUserMutationRefusedAsync), observer.Calls);
        Assert.Contains((userId, expectedMutation, reason), observer.Refusals);
        Assert.DoesNotContain((userId, expectedMutation), observer.Mutations);
    }

    private async Task<UserMutation> InvokeRefusableAsync(string method, Guid userId)
    {
        switch (method)
        {
            case nameof(UserService.SetEmailAsync):
                await sut.SetEmailAsync(userId, "new@example.com");
                return UserMutation.Email;
            case nameof(UserService.ConfirmEmailAsync):
                await sut.ConfirmEmailAsync(userId);
                return UserMutation.EmailConfirmation;
            case nameof(UserService.SetPhoneNumberAsync):
                await sut.SetPhoneNumberAsync(userId, "+66822223333");
                return UserMutation.Phone;
            case nameof(UserService.ConfirmPhoneNumberAsync):
                await sut.ConfirmPhoneNumberAsync(userId);
                return UserMutation.PhoneConfirmation;
            case nameof(UserService.SetPasswordAsync):
                await sut.SetPasswordAsync(userId, "new-pw");
                return UserMutation.Password;
            case nameof(UserService.SetActiveAsync):
                await sut.SetActiveAsync(userId, false);
                return UserMutation.Active;
            case nameof(UserService.DeleteAsync):
                await sut.DeleteAsync(userId);
                return UserMutation.Deleted;
            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, "Unhandled mutation method.");
        }
    }

    [Fact]
    public async Task SetEmailAsync_raises_OnUserMutated_with_Email()
    {
        var created = await sut.CreateAsync("nia", "pw");
        await sut.SetEmailAsync(created.UserId!.Value, "nia@example.com");
        Assert.Contains((created.UserId!.Value, UserMutation.Email), observer.Mutations);
    }

    [Fact]
    public async Task SetPhoneNumberAsync_raises_OnUserMutated_with_Phone()
    {
        var created = await sut.CreateAsync("owen", "pw");
        await sut.SetPhoneNumberAsync(created.UserId!.Value, "+66811112222");
        Assert.Contains((created.UserId!.Value, UserMutation.Phone), observer.Mutations);
    }

    private async Task<UserMutation> InvokeAsync(string method, Guid userId)
    {
        switch (method)
        {
            case nameof(UserService.SetPasswordAsync):
                await sut.SetPasswordAsync(userId, "new-pw");
                return UserMutation.Password;
            case nameof(UserService.SetActiveAsync):
                await sut.SetActiveAsync(userId, false);
                return UserMutation.Active;
            case nameof(UserService.ConfirmEmailAsync):
                await sut.ConfirmEmailAsync(userId);
                return UserMutation.EmailConfirmation;
            case nameof(UserService.ConfirmPhoneNumberAsync):
                await sut.ConfirmPhoneNumberAsync(userId);
                return UserMutation.PhoneConfirmation;
            case nameof(UserService.DeleteAsync):
                await sut.DeleteAsync(userId);
                return UserMutation.Deleted;
            default:
                throw new ArgumentOutOfRangeException(nameof(method), method, "Unhandled mutation method.");
        }
    }

    [Fact]
    public async Task Observers_and_the_adopters_own_lifecycle_hooks_both_run()
    {
        var created = await sut.CreateAsync("paula", "pw");
        await sut.SetPasswordAsync(created.UserId!.Value, "new-pw");

        Assert.Contains(UserMutation.Password, hooks.Observed);
        Assert.Contains((created.UserId!.Value, UserMutation.Password), observer.Mutations);
    }

    [Fact]
    public async Task A_throwing_observer_does_not_change_the_lockout_result()
    {
        var throwingSut = Build(new ThrowingIdentityEventObserver());
        options.MaxFailedAccessAttempts = 1;
        var created = await throwingSut.CreateAsync("quinn", "right");

        var result = await throwingSut.VerifyPasswordAsync("quinn", "wrong");

        Assert.Equal(PasswordVerificationResult.Failed, result);
        Assert.NotNull(created.UserId);
    }

    [Fact]
    public async Task A_throwing_observer_does_not_change_a_mutation_result()
    {
        var throwingSut = Build(new ThrowingIdentityEventObserver());
        var created = await throwingSut.CreateAsync("rae", "pw");

        var result = await throwingSut.SetPasswordAsync(created.UserId!.Value, "new-pw");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task An_observer_throwing_OperationCanceledException_does_not_fail_an_already_applied_mutation()
    {
        // A client disconnect mid-observer-write must not take down a mutation that already saved — the
        // caller is not waiting on the observer.
        var throwingSut = Build(new ThrowingIdentityEventObserver { ThrowOperationCanceled = true });
        var created = await throwingSut.CreateAsync("sana", "pw");

        var result = await throwingSut.SetPasswordAsync(created.UserId!.Value, "new-pw");

        Assert.True(result.Succeeded);
    }
}
