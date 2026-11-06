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

/// <summary>A save that loses to a newer write of the same user is a Conflict outcome, never a raw exception.</summary>
public class UserServiceConflictTests
{
    private readonly List<User> store = [];
    private readonly FakeUnitOfWork uow = new();
    private readonly RecordingUserLifecycleHooks hooks = new();
    private readonly UserService sut;

    public UserServiceConflictTests() =>
        sut = new UserService(
            new FakeRepository<User>(store, u => u.Id) { AmbientTenant = new TenantId("acme") }, uow,
            new Argon2idPasswordHasher(), new FakeTimeProvider(DateTimeOffset.Parse("2026-11-06T00:00:00Z")),
            new IdentityModuleOptions(), new DataFilterScope(), new FormattingOnlyPhoneNumberNormalizer(), hooks);

    public static TheoryData<string, Func<UserService, Guid, Task<UserMutationResult>>> Mutators => new()
    {
        { "SetEmail", (s, id) => s.SetEmailAsync(id, "new@example.com") },
        { "ConfirmEmail", (s, id) => s.ConfirmEmailAsync(id) },
        { "SetPhone", (s, id) => s.SetPhoneNumberAsync(id, "+66812345678") },
        { "ConfirmPhone", (s, id) => s.ConfirmPhoneNumberAsync(id) },
        { "SetPassword", (s, id) => s.SetPasswordAsync(id, "new-password") },
        { "SetActive", (s, id) => s.SetActiveAsync(id, false) },
        { "Delete", (s, id) => s.DeleteAsync(id) },
    };

    [Theory]
    [MemberData(nameof(Mutators))]
    public async Task A_stale_save_returns_Conflict_and_announces_nothing(
        string name, Func<UserService, Guid, Task<UserMutationResult>> mutate)
    {
        var user = new User { TenantId = new TenantId("acme"), UserName = "alice", NormalizedUserName = "ALICE", Email = "a@example.com", NormalizedEmail = "A@EXAMPLE.COM", PhoneNumber = "+66811111111", NormalizedPhoneNumber = "+66811111111", IsActive = true };
        user.SetId(Guid.NewGuid());
        store.Add(user);
        uow.ConcurrencyFailures = 1;

        var result = await mutate(sut, user.Id);

        Assert.Equal(UserMutationOutcome.Conflict, result.Outcome);
        Assert.False(result.Succeeded, name);
        Assert.Empty(hooks.Observed);
    }
}
