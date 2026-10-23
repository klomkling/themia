using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class SessionRevocationTests
{
    private static readonly TenantId Acme = new("acme");
    private readonly List<User> users = [];
    private readonly List<RefreshToken> tokens = [];
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-23T10:00:00Z"));
    private readonly RefreshTokenService sut;

    public SessionRevocationTests() => sut = ServiceInScope(Acme);

    private RefreshTokenService ServiceInScope(TenantId? ambient) => new(
        new FakeRepository<User>(users, u => u.Id) { AmbientTenant = ambient },
        new FakeRepository<RefreshToken>(tokens, t => t.Id) { AmbientTenant = ambient },
        new FakeUnitOfWork(), clock, new IdentityModuleOptions(), NullLogger<RefreshTokenService>.Instance);

    private User AddUser(string name)
    {
        var u = new User { UserName = name, NormalizedUserName = name.ToUpperInvariant(), TenantId = Acme };
        u.SetId(Guid.NewGuid());
        users.Add(u);
        return u;
    }

    [Fact]
    public async Task RevokeAllForUserExcept_revokes_every_other_session_and_keeps_the_given_one()
    {
        var user = AddUser("alice");
        var keep = await sut.IssueAsync(user.Id);
        var other1 = await sut.IssueAsync(user.Id);
        var other2 = await sut.IssueAsync(user.Id);

        var revoked = await sut.RevokeAllForUserExceptAsync(user.Id, keep.FamilyId.ToString());

        Assert.Equal(2, revoked);
        Assert.All(tokens.Where(t => t.FamilyId == keep.FamilyId), t => Assert.Null(t.RevokedAt));
        Assert.All(tokens.Where(t => t.FamilyId == other1.FamilyId || t.FamilyId == other2.FamilyId), t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task The_kept_session_survives_a_rotation_because_the_family_is_stable()
    {
        var user = AddUser("alice");
        var first = await sut.IssueAsync(user.Id);
        await sut.IssueAsync(user.Id);   // another device
        Assert.True((await sut.ValidateAndRotateAsync(first.RawToken)).TryGetSuccess(out _, out var rotated));
        Assert.Equal(first.FamilyId, rotated.FamilyId);

        var revoked = await sut.RevokeAllForUserExceptAsync(user.Id, rotated.FamilyId.ToString());

        Assert.Equal(1, revoked);
        Assert.True((await sut.ValidateAndRotateAsync(rotated.RawToken)).TryGetSuccess(out _, out _));
    }

    [Fact]
    public async Task RevokeAllForUserExcept_leaves_other_users_sessions_alone()
    {
        var user = AddUser("alice");
        var bystander = AddUser("bob");
        var keep = await sut.IssueAsync(user.Id);
        var bystanderSession = await sut.IssueAsync(bystander.Id);

        await sut.RevokeAllForUserExceptAsync(user.Id, keep.FamilyId.ToString());

        Assert.All(tokens.Where(t => t.FamilyId == bystanderSession.FamilyId), t => Assert.Null(t.RevokedAt));
    }

    [Fact]
    public async Task RevokeAllForUserExcept_does_not_count_sessions_that_are_already_revoked()
    {
        var user = AddUser("alice");
        var keep = await sut.IssueAsync(user.Id);
        await sut.IssueAsync(user.Id);
        Assert.Equal(1, await sut.RevokeAllForUserExceptAsync(user.Id, keep.FamilyId.ToString()));

        Assert.Equal(0, await sut.RevokeAllForUserExceptAsync(user.Id, keep.FamilyId.ToString()));
    }

    [Fact]
    public async Task RevokeAllForUserExcept_revokes_nothing_for_a_user_outside_the_scope()
    {
        var user = AddUser("alice");
        var keep = await sut.IssueAsync(user.Id);
        await sut.IssueAsync(user.Id);

        var revoked = await ServiceInScope(new TenantId("other")).RevokeAllForUserExceptAsync(user.Id, keep.FamilyId.ToString());

        Assert.Equal(0, revoked);
        Assert.All(tokens, t => Assert.Null(t.RevokedAt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not-a-session-id")]
    public async Task RevokeAllForUserExcept_rejects_a_blank_or_malformed_session_id_and_revokes_nothing(string sessionId)
    {
        var user = AddUser("alice");
        await sut.IssueAsync(user.Id);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => sut.RevokeAllForUserExceptAsync(user.Id, sessionId));

        Assert.All(tokens, t => Assert.Null(t.RevokedAt));
    }

    [Fact]
    public async Task RevokeAllForUserExcept_rejects_a_null_session_id()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => sut.RevokeAllForUserExceptAsync(Guid.NewGuid(), null!));
    }

    [Fact]
    public async Task RevokeAllForUserExcept_throws_for_a_well_formed_unknown_session_id_and_revokes_nothing()
    {
        var user = AddUser("alice");
        await sut.IssueAsync(user.Id);
        await sut.IssueAsync(user.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.RevokeAllForUserExceptAsync(user.Id, Guid.NewGuid().ToString()));

        Assert.All(tokens, t => Assert.Null(t.RevokedAt));
    }

    [Fact]
    public async Task RevokeAllForUserExcept_throws_for_another_users_session_and_revokes_nothing()
    {
        var user = AddUser("alice");
        var other = AddUser("bob");
        await sut.IssueAsync(user.Id);
        var othersSession = await sut.IssueAsync(other.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.RevokeAllForUserExceptAsync(user.Id, othersSession.FamilyId.ToString()));

        Assert.All(tokens, t => Assert.Null(t.RevokedAt));
    }

    [Fact]
    public async Task RevokeAllForUserExcept_throws_when_the_kept_session_is_already_revoked()
    {
        var user = AddUser("alice");
        var keep = await sut.IssueAsync(user.Id);
        await sut.IssueAsync(user.Id);
        await sut.RevokeAsync(keep.RawToken, allForUser: false);

        await Assert.ThrowsAsync<ArgumentException>(() => sut.RevokeAllForUserExceptAsync(user.Id, keep.FamilyId.ToString()));
    }

    [Fact]
    public async Task RevokeAllForUserExcept_throws_when_the_kept_session_has_expired()
    {
        var user = AddUser("alice");
        var keep = await sut.IssueAsync(user.Id);
        clock.Advance(TimeSpan.FromDays(400));

        await Assert.ThrowsAsync<ArgumentException>(() => sut.RevokeAllForUserExceptAsync(user.Id, keep.FamilyId.ToString()));
    }
}
