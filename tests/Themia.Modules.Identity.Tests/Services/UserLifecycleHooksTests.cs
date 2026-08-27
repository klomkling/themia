using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Hashing;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

/// <summary>
/// Every mutation on <see cref="IUserService"/> must ask <see cref="IUserLifecycleHooks"/> first and
/// announce itself after. A seam wired on some paths reads as wired on all of them, so each path is
/// covered here rather than a representative few.
/// </summary>
public class UserLifecycleHooksTests
{
    private readonly List<User> store = [];
    private readonly FakeRepository<User> repo;
    private readonly FakeUnitOfWork uow = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-08-26T00:00:00Z"));
    private readonly IdentityModuleOptions options = new();
    private readonly RecordingUserLifecycleHooks hooks = new();
    private readonly UserService sut;

    public UserLifecycleHooksTests()
    {
        repo = new FakeRepository<User>(store, u => u.Id) { AmbientTenant = new TenantId("acme") };
        sut = new UserService(
            repo, uow, new Argon2idPasswordHasher(), clock, options,
            new DataFilterScope(), new FormattingOnlyPhoneNumberNormalizer(), hooks);
    }

    private async Task<Guid> SeedAsync(string userName = "alice", string? email = "alice@example.com")
    {
        var create = await sut.CreateAsync(userName, "pw", email);
        Assert.True(create.Succeeded);
        hooks.Observed.Clear();
        hooks.Asked.Clear();
        return create.UserId!.Value;
    }

    // ---- refusals ----------------------------------------------------------------------------

    [Fact]
    public async Task SetEmailAsync_refused_leaves_the_address_untouched()
    {
        var userId = await SeedAsync();
        hooks.RefuseSetEmail = "this is the only way you can sign in";

        var result = await sut.SetEmailAsync(userId, "new@example.com");

        Assert.Equal(UserMutationOutcome.Refused, result.Outcome);
        Assert.Equal("this is the only way you can sign in", result.Reason);
        Assert.Equal("alice@example.com", Assert.Single(store).Email);
        Assert.Empty(hooks.Observed);
    }

    [Fact]
    public async Task ConfirmEmailAsync_refused_leaves_the_flag_untouched()
    {
        var userId = await SeedAsync();
        hooks.RefuseConfirmEmail = "verify through the tenant's own flow";

        var result = await sut.ConfirmEmailAsync(userId);

        Assert.Equal(UserMutationOutcome.Refused, result.Outcome);
        Assert.False(Assert.Single(store).EmailConfirmed);
        Assert.Empty(hooks.Observed);
    }

    [Fact]
    public async Task SetPhoneNumberAsync_refused_leaves_the_number_untouched()
    {
        var userId = await SeedAsync();
        Assert.True((await sut.SetPhoneNumberAsync(userId, "+66811112222")).Succeeded);
        hooks.RefuseSetPhoneNumber = "the account would lose its second factor";

        var result = await sut.SetPhoneNumberAsync(userId, "+66833334444");

        Assert.Equal(UserMutationOutcome.Refused, result.Outcome);
        Assert.Equal("+66811112222", Assert.Single(store).PhoneNumber);
    }

    [Fact]
    public async Task ConfirmPhoneNumberAsync_refused_leaves_the_flag_untouched()
    {
        var userId = await SeedAsync();
        Assert.True((await sut.SetPhoneNumberAsync(userId, "+66811112222")).Succeeded);
        hooks.RefuseConfirmPhoneNumber = "confirm through the tenant's own flow";

        var result = await sut.ConfirmPhoneNumberAsync(userId);

        Assert.Equal(UserMutationOutcome.Refused, result.Outcome);
        Assert.False(Assert.Single(store).PhoneNumberConfirmed);
    }

    [Fact]
    public async Task SetPasswordAsync_refused_leaves_the_hash_and_stamp_untouched()
    {
        var userId = await SeedAsync();
        var before = Assert.Single(store);
        var hashBefore = before.PasswordHash;
        var stampBefore = before.SecurityStamp;
        hooks.RefuseSetPassword = "passwords are managed by the directory";

        var result = await sut.SetPasswordAsync(userId, "newpw");

        Assert.Equal(UserMutationOutcome.Refused, result.Outcome);
        var after = Assert.Single(store);
        Assert.Equal(hashBefore, after.PasswordHash);
        Assert.Equal(stampBefore, after.SecurityStamp);
    }

    [Fact]
    public async Task SetActiveAsync_refused_leaves_the_state_untouched()
    {
        var userId = await SeedAsync();
        hooks.RefuseSetActive = "the last administrator cannot be deactivated";

        var result = await sut.SetActiveAsync(userId, false);

        Assert.Equal(UserMutationOutcome.Refused, result.Outcome);
        Assert.True(Assert.Single(store).IsActive);
    }

    [Fact]
    public async Task DeleteAsync_refused_leaves_the_user_findable()
    {
        var userId = await SeedAsync();
        hooks.RefuseDelete = "this user still owns open invoices";

        var result = await sut.DeleteAsync(userId);

        Assert.Equal(UserMutationOutcome.Refused, result.Outcome);
        Assert.Equal("this user still owns open invoices", result.Reason);
        Assert.NotNull(await sut.FindByUserNameAsync("alice"));
    }

    // ---- observation -------------------------------------------------------------------------

    [Fact]
    public async Task Each_applied_mutation_announces_itself_once()
    {
        var userId = await SeedAsync();

        Assert.True((await sut.SetEmailAsync(userId, "new@example.com")).Succeeded);
        Assert.True((await sut.ConfirmEmailAsync(userId)).Succeeded);
        Assert.True((await sut.SetPhoneNumberAsync(userId, "+66811112222")).Succeeded);
        Assert.True((await sut.ConfirmPhoneNumberAsync(userId)).Succeeded);
        Assert.True((await sut.SetPasswordAsync(userId, "newpw")).Succeeded);
        Assert.True((await sut.SetActiveAsync(userId, false)).Succeeded);
        Assert.True((await sut.DeleteAsync(userId)).Succeeded);

        Assert.Equal(
            [
                UserMutation.Email,
                UserMutation.EmailConfirmation,
                UserMutation.Phone,
                UserMutation.PhoneConfirmation,
                UserMutation.Password,
                UserMutation.Active,
                UserMutation.Deleted,
            ],
            hooks.Observed);
    }

    [Fact]
    public async Task Every_mutation_asks_before_it_writes()
    {
        var userId = await SeedAsync();

        await sut.SetEmailAsync(userId, "new@example.com");
        await sut.ConfirmEmailAsync(userId);
        await sut.SetPhoneNumberAsync(userId, "+66811112222");
        await sut.ConfirmPhoneNumberAsync(userId);
        await sut.SetPasswordAsync(userId, "newpw");
        await sut.SetActiveAsync(userId, false);
        await sut.DeleteAsync(userId);

        Assert.Equal(
            [
                nameof(IUserLifecycleHooks.OnBeforeSetEmailAsync),
                nameof(IUserLifecycleHooks.OnBeforeConfirmEmailAsync),
                nameof(IUserLifecycleHooks.OnBeforeSetPhoneNumberAsync),
                nameof(IUserLifecycleHooks.OnBeforeConfirmPhoneNumberAsync),
                nameof(IUserLifecycleHooks.OnBeforeSetPasswordAsync),
                nameof(IUserLifecycleHooks.OnBeforeSetActiveAsync),
                nameof(IUserLifecycleHooks.OnBeforeDeleteAsync),
            ],
            hooks.Asked);
    }

    [Fact]
    public async Task A_before_hook_sees_the_value_being_proposed()
    {
        var userId = await SeedAsync();

        await sut.SetEmailAsync(userId, "new@example.com");
        await sut.SetPhoneNumberAsync(userId, "+66811112222");
        await sut.SetActiveAsync(userId, false);

        Assert.Equal("new@example.com", hooks.LastProposedEmail);
        Assert.Equal("+66811112222", hooks.LastProposedPhoneNumber);
        Assert.False(hooks.LastProposedActive);
    }

    // ---- ordering ----------------------------------------------------------------------------

    [Fact]
    public async Task A_refusal_wins_over_a_duplicate_the_module_would_have_reported()
    {
        await SeedAsync("taken", "taken@example.com");
        var userId = await SeedAsync("bob", "bob@example.com");
        hooks.RefuseSetEmail = "changing the address would break SSO mapping";

        var result = await sut.SetEmailAsync(userId, "taken@example.com");

        // Refused, not Duplicate: the consumer's rule is not conditional on the module happening to
        // reject the value first for a reason of its own.
        Assert.Equal(UserMutationOutcome.Refused, result.Outcome);
    }

    [Fact]
    public async Task An_unknown_user_is_reported_without_asking_the_hook()
    {
        hooks.RefuseDelete = "never reached";

        var result = await sut.DeleteAsync(Guid.CreateVersion7());

        Assert.Equal(UserMutationOutcome.UserNotFound, result.Outcome);
        Assert.Empty(hooks.Asked);
    }
}
