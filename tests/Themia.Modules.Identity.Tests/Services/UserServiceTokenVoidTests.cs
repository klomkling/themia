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

/// <summary>A password, email or phone change must kill the user's live password-reset links (coord #0198).</summary>
public class UserServiceTokenVoidTests
{
    private static readonly TenantId Acme = new("acme");
    private readonly List<User> users = [];
    private readonly List<UserToken> tokens = [];
    private readonly FakeUnitOfWork uow = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-06T00:00:00Z"));
    private readonly RecordingUserLifecycleHooks hooks = new();
    private readonly FakeRepository<UserToken> tokenRepo;
    private readonly UserService sut;

    public UserServiceTokenVoidTests()
    {
        tokenRepo = new FakeRepository<UserToken>(tokens, t => t.Id);
        sut = new UserService(
            new FakeRepository<User>(users, u => u.Id) { AmbientTenant = Acme }, uow,
            new StubHasher(), clock, new IdentityModuleOptions(), new DataFilterScope(),
            new FormattingOnlyPhoneNumberNormalizer(), hooks, [], tokenRepo, NullLogger<UserService>.Instance, null);
    }

    public static TheoryData<string, Func<UserService, Guid, Task<UserMutationResult>>> Mutators => new()
    {
        { "SetPassword", (s, id) => s.SetPasswordAsync(id, "new-password") },
        { "SetEmail", (s, id) => s.SetEmailAsync(id, "new@example.com") },
        { "SetPhone", (s, id) => s.SetPhoneNumberAsync(id, "+66812345678") },
    };

    [Theory]
    [MemberData(nameof(Mutators))]
    public async Task Mutation_voids_only_the_users_live_reset_tokens(
        string name, Func<UserService, Guid, Task<UserMutationResult>> mutate)
    {
        var user = AddUser("alice");
        var other = AddUser("bob");
        var live = AddToken(user, TokenPurpose.PasswordReset);
        var liveToo = AddToken(user, TokenPurpose.PasswordReset);
        var expired = AddToken(user, TokenPurpose.PasswordReset, expiresIn: TimeSpan.FromMinutes(-1));
        var otherPurpose = AddToken(user, TokenPurpose.EmailConfirm);
        var otherUser = AddToken(other, TokenPurpose.PasswordReset);

        Assert.True((await mutate(sut, user.Id)).Succeeded, name);

        Assert.NotNull(live.ConsumedAt);
        Assert.NotNull(liveToo.ConsumedAt);
        Assert.Null(expired.ConsumedAt);
        Assert.Null(otherPurpose.ConsumedAt);
        Assert.Null(otherUser.ConsumedAt);
    }

    [Fact]
    public async Task A_reset_link_issued_while_the_password_change_loses_a_race_is_voided_by_the_pass_that_lands()
    {
        var user = AddUser("alice");
        UserToken? late = null;
        uow.ConcurrencyFailures = 1;
        uow.OnConcurrencyFailure = () => late = AddToken(user, TokenPurpose.PasswordReset);   // issued between the two passes

        Assert.True((await sut.SetPasswordAsync(user.Id, "new-password")).Succeeded);

        Assert.NotNull(late);
        Assert.NotNull(late!.ConsumedAt);
    }

    [Fact]
    public async Task A_refused_password_change_voids_nothing()
    {
        var user = AddUser("alice");
        var live = AddToken(user, TokenPurpose.PasswordReset);
        hooks.RefuseSetPassword = "frozen";

        Assert.Equal(UserMutationOutcome.Refused, (await sut.SetPasswordAsync(user.Id, "new-password")).Outcome);

        Assert.Null(live.ConsumedAt);
    }

    [Fact]
    public async Task A_failed_void_leaves_the_password_unchanged()
    {
        var user = AddUser("alice");
        var stamp = user.SecurityStamp;
        AddToken(user, TokenPurpose.PasswordReset);
        tokenRepo.BeforeUpdateWhere = _ => throw new InvalidOperationException("db down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SetPasswordAsync(user.Id, "new-password"));

        Assert.Equal("old-hash", user.PasswordHash);
        Assert.Equal(stamp, user.SecurityStamp);
        Assert.Equal(0, uow.SaveCount);
    }

    [Fact]
    public async Task A_failed_save_after_the_void_keeps_the_tokens_dead()
    {
        var user = AddUser("alice");
        var live = AddToken(user, TokenPurpose.PasswordReset);
        uow.ThrowOnSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.SetPasswordAsync(user.Id, "new-password"));

        Assert.NotNull(live.ConsumedAt);
    }

    [Fact]
    public async Task The_reset_flow_still_works_and_kills_the_sibling_links()
    {
        var user = AddUser("alice");
        var used = AddToken(user, TokenPurpose.PasswordReset);
        var sibling = AddToken(user, TokenPurpose.PasswordReset);
        used.ConsumedAt = clock.GetUtcNow();   // the reset endpoint consumed the presented link first

        Assert.True((await sut.SetPasswordAsync(user.Id, "new-password")).Succeeded);

        Assert.Equal("new-hash", user.PasswordHash);
        Assert.NotNull(sibling.ConsumedAt);
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

    private UserToken AddToken(User user, TokenPurpose purpose, TimeSpan? expiresIn = null)
    {
        var t = new UserToken
        {
            UserId = user.Id, Purpose = purpose, TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = clock.GetUtcNow().Add(expiresIn ?? TimeSpan.FromHours(1)),
        };
        t.SetId(Guid.NewGuid());
        tokens.Add(t);
        return t;
    }

    private sealed class StubHasher : IPasswordHasher
    {
        public string Hash(string password) => "new-hash";
        public bool Verify(string encodedHash, string password) => true;
        public bool NeedsRehash(string encodedHash) => false;
    }
}
