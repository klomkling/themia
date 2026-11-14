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

/// <summary>A refresh that carries no JWT runs in a scope with no tenant. The post-rotation liveness check must still see the
/// tenant user the rotation just resolved by the token hash, and must still refuse a session that ended.</summary>
public class RefreshRotationLivenessTests
{
    private static readonly TenantId Acme = new("acme");
    private readonly List<User> users = [];
    private readonly List<RefreshToken> tokens = [];
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-14T10:00:00Z"));
    private readonly FakeRepository<User> userRepo;
    private readonly FakeRepository<RefreshToken> tokenRepo;

    public RefreshRotationLivenessTests()
    {
        userRepo = new FakeRepository<User>(users, u => u.Id) { AmbientTenant = Acme };
        tokenRepo = new FakeRepository<RefreshToken>(tokens, t => t.Id);
    }

    private RefreshTokenService Tokens() =>
        new(userRepo, tokenRepo, new FakeUnitOfWork(), clock, new IdentityModuleOptions(), NullLogger<RefreshTokenService>.Instance);

    private User AddTenantUser()
    {
        var u = new User { UserName = "alice", NormalizedUserName = "ALICE", TenantId = Acme };
        u.SetId(Guid.NewGuid());
        users.Add(u);
        return u;
    }

    private void LeaveTenantScope() => userRepo.AmbientTenant = null;

    private async Task<(User User, RefreshIssue Issued, RefreshIssue Rotated)> IssueThenRotateWithNoTenant()
    {
        var user = AddTenantUser();
        var issued = await Tokens().IssueAsync(user.Id);
        LeaveTenantScope();
        Assert.True((await Tokens().ValidateAndRotateAsync(issued.RawToken)).TryGetSuccess(out _, out var next));
        return (user, issued, next);
    }

    [Fact]
    public async Task IsSessionLiveForRotation_sees_a_just_rotated_tenant_user_when_the_scope_has_no_tenant()
    {
        var (user, issued, _) = await IssueThenRotateWithNoTenant();

        Assert.True(await Tokens().IsSessionLiveForRotationAsync(user.Id, issued.FamilyId));
    }

    [Fact]
    public async Task IsSessionLive_stays_ambient_only_and_does_not_see_a_tenant_user_from_a_scope_with_no_tenant()
    {
        var (user, issued, _) = await IssueThenRotateWithNoTenant();

        Assert.False(await Tokens().IsSessionLiveAsync(user.Id, issued.FamilyId));
    }

    [Fact]
    public async Task IsSessionLiveForRotation_is_false_for_a_revoked_family_when_the_scope_has_no_tenant()
    {
        var (user, issued, rotated) = await IssueThenRotateWithNoTenant();
        await Tokens().RevokeAsync(rotated.RawToken, allForUser: false);

        Assert.False(await Tokens().IsSessionLiveForRotationAsync(user.Id, issued.FamilyId));
    }

    [Fact]
    public async Task IsSessionLiveForRotation_is_false_once_the_stamp_rotated_when_the_scope_has_no_tenant()
    {
        var (user, issued, _) = await IssueThenRotateWithNoTenant();
        user.SecurityStamp = SecurityStampCache.NewStamp();

        Assert.False(await Tokens().IsSessionLiveForRotationAsync(user.Id, issued.FamilyId));
    }

    [Fact]
    public async Task IsSessionLiveForRotation_is_false_for_a_family_that_belongs_to_another_user()
    {
        var (_, issued, _) = await IssueThenRotateWithNoTenant();

        Assert.False(await Tokens().IsSessionLiveForRotationAsync(Guid.NewGuid(), issued.FamilyId));
    }

    [Fact]
    public async Task IsSessionLiveForRotation_is_false_for_a_deleted_user()
    {
        var (user, issued, _) = await IssueThenRotateWithNoTenant();
        user.IsDeleted = true;

        Assert.False(await Tokens().IsSessionLiveForRotationAsync(user.Id, issued.FamilyId));
    }
}
