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
/// An admin's disable, delete or password reset must not be starved by a busy row (a stream of logins): the three
/// mutators reload and retry up to four times before they answer Conflict.
/// </summary>
public class UserServiceConflictRetryTests
{
    private readonly List<User> store = [];
    private readonly FakeUnitOfWork uow = new();
    private readonly RecordingUserLifecycleHooks hooks = new();
    private readonly CountingHasher hasher = new();
    private readonly UserService sut;

    public UserServiceConflictRetryTests() =>
        sut = new UserService(
            new FakeRepository<User>(store, u => u.Id) { AmbientTenant = new TenantId("acme") }, uow,
            hasher, new FakeTimeProvider(DateTimeOffset.Parse("2026-11-06T00:00:00Z")),
            new IdentityModuleOptions(), new DataFilterScope(), new FormattingOnlyPhoneNumberNormalizer(), hooks);

    public static TheoryData<string, Func<UserService, Guid, Task<UserMutationResult>>> RetryingMutators => new()
    {
        { "SetPassword", (s, id) => s.SetPasswordAsync(id, "new-password") },
        { "SetActive", (s, id) => s.SetActiveAsync(id, false) },
        { "Delete", (s, id) => s.DeleteAsync(id) },
    };

    [Theory]
    [MemberData(nameof(RetryingMutators))]
    public async Task One_to_four_lost_races_still_end_in_Success_and_announce_once(
        string name, Func<UserService, Guid, Task<UserMutationResult>> mutate)
    {
        foreach (var failures in new[] { 1, 2, 3, 4 })
        {
            store.Clear();
            hooks.Observed.Clear();
            var user = Seed();
            uow.ConcurrencyFailures = failures;
            uow.OnConcurrencyFailure = () => user.IsDeleted = false;   // a lost save leaves the row where it was (the fake's Remove soft-deletes it at once)

            var result = await mutate(sut, user.Id);

            Assert.True(result.Succeeded, $"{name} after {failures} lost races: {result.Outcome}");
            Assert.Single(hooks.Observed);
        }
    }

    [Fact]
    public async Task SetPassword_hashes_once_however_many_passes_it_takes()
    {
        var user = Seed();
        uow.ConcurrencyFailures = 4;

        var result = await sut.SetPasswordAsync(user.Id, "new-password");

        Assert.True(result.Succeeded);
        Assert.Equal(1, hasher.Hashes);
    }

    [Fact]
    public async Task Every_pass_asks_the_hook_again_so_a_veto_between_passes_is_honoured()
    {
        var user = Seed();
        uow.ConcurrencyFailures = 2;
        uow.OnConcurrencyFailure = () => hooks.RefuseSetActive = "legal hold";

        var result = await sut.SetActiveAsync(user.Id, false);

        Assert.Equal(UserMutationOutcome.Refused, result.Outcome);
        Assert.Equal(2, hooks.Asked.Count(a => a == nameof(IUserLifecycleHooks.OnBeforeSetActiveAsync)));
    }

    private User Seed()
    {
        var user = new User { TenantId = new TenantId("acme"), UserName = "alice", NormalizedUserName = "ALICE", PasswordHash = "x" };
        user.SetId(Guid.NewGuid());
        store.Add(user);
        return user;
    }

    private sealed class CountingHasher : IPasswordHasher
    {
        private readonly Argon2idPasswordHasher inner = new();

        public int Hashes { get; private set; }

        public string Hash(string password)
        {
            Hashes++;
            return inner.Hash(password);
        }

        public bool Verify(string encodedHash, string password) => inner.Verify(encodedHash, password);

        public bool NeedsRehash(string encodedHash) => inner.NeedsRehash(encodedHash);
    }
}
