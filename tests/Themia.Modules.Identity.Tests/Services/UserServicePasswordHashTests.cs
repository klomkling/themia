using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

/// <summary>
/// <see cref="IUserPasswordHashService"/> writes an already-computed hash through the same pass as
/// <c>SetPasswordAsync</c>, so the reset-link void, stamp rotation and lock lift all apply (coord #0202).
/// </summary>
public class UserServicePasswordHashTests
{
    private const string AppBcryptHash = "$2a$11$abcdefghijklmnopqrstuuABCDEFGHIJKLMNOPQRSTUVWXYZ01234";

    private static readonly TenantId Acme = new("acme");
    private readonly List<User> users = [];
    private readonly List<UserToken> tokens = [];
    private readonly FakeUnitOfWork uow = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-06T00:00:00Z"));
    private readonly RecordingUserLifecycleHooks hooks = new();
    private readonly CountingHasher hasher = new();
    private readonly IUserPasswordHashService sut;

    public UserServicePasswordHashTests()
    {
        sut = new UserService(
            new FakeRepository<User>(users, u => u.Id) { AmbientTenant = Acme }, uow,
            hasher, clock, new IdentityModuleOptions(), new DataFilterScope(),
            new FormattingOnlyPhoneNumberNormalizer(), hooks, [],
            new FakeRepository<UserToken>(tokens, t => t.Id), NullLogger<UserService>.Instance, null);
    }

    [Fact]
    public async Task The_hash_is_stored_verbatim_and_never_hashed_again()
    {
        var user = AddUser("alice");

        Assert.True((await sut.SetPasswordHashAsync(user.Id, AppBcryptHash)).Succeeded);

        Assert.Equal(AppBcryptHash, user.PasswordHash);
        Assert.Equal(0, hasher.HashCalls);
    }

    [Fact]
    public async Task A_null_hash_clears_the_password()
    {
        var user = AddUser("alice");

        Assert.True((await sut.SetPasswordHashAsync(user.Id, null)).Succeeded);

        Assert.Null(user.PasswordHash);
        Assert.Equal(0, hasher.HashCalls);
    }

    [Theory]
    [InlineData(AppBcryptHash)]
    [InlineData(null)]
    public async Task The_users_live_reset_links_die_and_the_stamp_rotates_and_a_lock_lifts(string? hash)
    {
        var user = AddUser("alice");
        user.AccessFailedCount = 4;
        user.LockoutEnd = clock.GetUtcNow().AddHours(1);
        var stamp = user.SecurityStamp;
        var live = AddToken(user, TokenPurpose.PasswordReset);
        var otherPurpose = AddToken(user, TokenPurpose.EmailConfirm);

        Assert.True((await sut.SetPasswordHashAsync(user.Id, hash)).Succeeded);

        Assert.NotNull(live.ConsumedAt);
        Assert.Null(otherPurpose.ConsumedAt);
        Assert.NotEqual(stamp, user.SecurityStamp);
        Assert.Equal(0, user.AccessFailedCount);
        Assert.Null(user.LockoutEnd);
        Assert.Equal([UserMutation.Password], hooks.Observed);
    }

    [Fact]
    public async Task A_refused_change_writes_nothing_and_voids_nothing()
    {
        var user = AddUser("alice");
        var live = AddToken(user, TokenPurpose.PasswordReset);
        hooks.RefuseSetPassword = "frozen";

        Assert.Equal(UserMutationOutcome.Refused, (await sut.SetPasswordHashAsync(user.Id, AppBcryptHash)).Outcome);

        Assert.Equal("old-hash", user.PasswordHash);
        Assert.Null(live.ConsumedAt);
        Assert.Empty(hooks.Observed);
    }

    [Fact]
    public async Task An_unknown_user_is_not_found()
    {
        Assert.Equal(UserMutationOutcome.UserNotFound, (await sut.SetPasswordHashAsync(Guid.NewGuid(), AppBcryptHash)).Outcome);
    }

    [Fact]
    public async Task A_lost_race_is_retried_and_the_pass_that_lands_voids_a_late_link()
    {
        var user = AddUser("alice");
        UserToken? late = null;
        uow.ConcurrencyFailures = 1;
        uow.OnConcurrencyFailure = () => late = AddToken(user, TokenPurpose.PasswordReset);

        Assert.True((await sut.SetPasswordHashAsync(user.Id, AppBcryptHash)).Succeeded);

        Assert.NotNull(late!.ConsumedAt);
        Assert.Equal(AppBcryptHash, user.PasswordHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_hash_is_rejected_because_null_is_the_only_way_to_clear(string hash)
    {
        var user = AddUser("alice");

        await Assert.ThrowsAsync<ArgumentException>(() => sut.SetPasswordHashAsync(user.Id, hash));

        Assert.Equal("old-hash", user.PasswordHash);
    }

    [Fact]
    public async Task A_hash_longer_than_the_column_is_rejected_before_any_write()
    {
        var user = AddUser("alice");

        await Assert.ThrowsAnyAsync<ArgumentException>(() => sut.SetPasswordHashAsync(user.Id, new string('x', 1025)));

        Assert.Equal(0, uow.SaveCount);
    }

    private User AddUser(string name)
    {
        var u = new User
        {
            TenantId = Acme, UserName = name, NormalizedUserName = name.ToUpperInvariant(),
            PasswordHash = "old-hash", IsActive = true,
        };
        u.SetId(Guid.NewGuid());
        users.Add(u);
        return u;
    }

    private UserToken AddToken(User user, TokenPurpose purpose)
    {
        var t = new UserToken
        {
            UserId = user.Id, Purpose = purpose, TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = clock.GetUtcNow().AddHours(1),
        };
        t.SetId(Guid.NewGuid());
        tokens.Add(t);
        return t;
    }

    private sealed class CountingHasher : IPasswordHasher
    {
        public int HashCalls { get; private set; }

        public string Hash(string password)
        {
            HashCalls++;
            return "new-hash";
        }

        public bool Verify(string encodedHash, string password) => true;

        public bool NeedsRehash(string encodedHash) => false;
    }
}
