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
/// The access failure counter is a compare-and-set: a guess is charged, or accepted, only against the failure state
/// it read. Each test lets another change land WHILE the guess is being judged (inside the hasher), which is the
/// interleaving parallel requests produce, but deterministically.
/// </summary>
public class FailureCounterGateTests
{
    private readonly List<User> store = [];
    private readonly FakeRepository<User> repo;
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-03T04:00:00Z"));
    private readonly IdentityModuleOptions options = new() { MaxFailedAccessAttempts = 2 };
    private readonly InterleavingHasher hasher = new();
    private readonly UserService sut;

    public FailureCounterGateTests()
    {
        repo = new FakeRepository<User>(store, u => u.Id) { AmbientTenant = new TenantId("acme") };
        sut = Build();
    }

    private UserService Build() =>
        new(repo, new FakeUnitOfWork(), hasher, clock, options, new DataFilterScope(), new FormattingOnlyPhoneNumberNormalizer(), new RecordingUserLifecycleHooks());

    private User AddUser()
    {
        var user = new User { UserName = "victim", NormalizedUserName = "VICTIM", TenantId = new TenantId("acme"), PasswordHash = hasher.Hash("right") };
        user.SetId(Guid.NewGuid());
        store.Add(user);
        return user;
    }

    [Fact]
    public async Task ARightGuess_IsLockedOut_WhenTheAccountLocksWhileItIsBeingJudged()
    {
        var user = AddUser();
        hasher.WhileJudging = () =>
        {
            Assert.Equal(PasswordVerificationResult.Failed, Build().VerifyPasswordAsync("victim", "wrong").GetAwaiter().GetResult());
            Assert.Equal(PasswordVerificationResult.Failed, Build().VerifyPasswordAsync("victim", "wrong").GetAwaiter().GetResult());
        };

        var result = await sut.VerifyPasswordAsync("victim", "right");

        Assert.Equal(PasswordVerificationResult.LockedOut, result);
        Assert.NotNull(user.LockoutEnd);
    }

    [Fact]
    public async Task AWrongGuess_IsChargedOnTopOfAChargeThatLandedWhileItWasBeingJudged()
    {
        var user = AddUser();
        options.MaxFailedAccessAttempts = 10;
        hasher.WhileJudging = () => Build().VerifyPasswordAsync("victim", "wrong").GetAwaiter().GetResult();

        var result = await sut.VerifyPasswordAsync("victim", "wrong");

        Assert.Equal(PasswordVerificationResult.Failed, result);
        Assert.Equal(2, user.AccessFailedCount);
    }

    [Fact]
    public async Task ALockedAccount_AnswersWithoutAnyHashWork()
    {
        var user = AddUser();
        user.LockoutEnd = clock.GetUtcNow().AddMinutes(5);

        Assert.Equal(PasswordVerificationResult.LockedOut, await sut.VerifyPasswordAsync("victim", "right"));
        Assert.Equal(0, hasher.VerifyCalls);
    }

    [Fact]
    public async Task AnInactiveAccount_AnswersWithoutAnyHashWork()
    {
        var user = AddUser();
        user.IsActive = false;

        Assert.Equal(PasswordVerificationResult.Inactive, await sut.VerifyPasswordAsync("victim", "right"));
        Assert.Equal(0, hasher.VerifyCalls);
    }

    [Fact]
    public async Task TheLockingGuess_AnswersFailed_AndRaisesTheLockOnce()
    {
        var user = AddUser();
        user.AccessFailedCount = 1;

        var result = await sut.VerifyPasswordAsync("victim", "wrong");

        Assert.Equal(PasswordVerificationResult.Failed, result);
        Assert.NotNull(user.LockoutEnd);
        Assert.Equal(0, user.AccessFailedCount);   // ResetCountOnLock
    }

    [Fact]
    public async Task ARehash_DoesNotRestoreAnOldPassword_OverAResetThatLandedWhileItWasBeingJudged()
    {
        var user = AddUser();
        var oldHash = user.PasswordHash;
        hasher.RehashEverything = true;
        hasher.WhileJudging = () =>
        {
            user.PasswordHash = hasher.Hash("reset-password");   // a password reset: new hash, new stamp, state cleared
            user.SecurityStamp = Guid.NewGuid().ToString("N");
        };

        var result = await sut.VerifyPasswordAsync("victim", "right");

        Assert.Equal(PasswordVerificationResult.Failed, result);
        Assert.True(hasher.Verify(user.PasswordHash!, "reset-password"));
        Assert.NotEqual(oldHash, user.PasswordHash);
    }

    [Fact]
    public async Task ARehash_IsWritten_AndTheFailureStateCleared_OnARightGuess()
    {
        var user = AddUser();
        user.AccessFailedCount = 1;
        hasher.RehashEverything = true;

        var result = await sut.VerifyPasswordAsync("victim", "right");

        Assert.Equal(PasswordVerificationResult.Success, result);
        Assert.Equal(0, user.AccessFailedCount);
        Assert.StartsWith("rehash:", user.PasswordHash, StringComparison.Ordinal);
    }

    private sealed class InterleavingHasher : IPasswordHasher
    {
        private bool inWhileJudging;

        public Action? WhileJudging { get; set; }

        public bool RehashEverything { get; set; }

        public string Hash(string password) => (RehashEverything ? "rehash:" : "hash:") + password;

        public int VerifyCalls { get; private set; }

        public bool Verify(string encodedHash, string password)
        {
            VerifyCalls++;
            if (WhileJudging is { } hook && !inWhileJudging)
            {
                inWhileJudging = true;
                try
                {
                    hook();
                }
                finally
                {
                    inWhileJudging = false;
                    WhileJudging = null;
                }
            }

            return encodedHash.EndsWith(":" + password, StringComparison.Ordinal);
        }

        public bool NeedsRehash(string encodedHash) => RehashEverything && !encodedHash.StartsWith("rehash:", StringComparison.Ordinal);
    }
}
