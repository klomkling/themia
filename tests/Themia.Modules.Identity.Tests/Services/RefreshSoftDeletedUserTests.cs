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

/// <summary>A refresh token that was never revoked must stop working once its user is soft-deleted: the user
/// lookup finds nobody, so the token is refused and left unconsumed, with no successor issued.</summary>
public class RefreshSoftDeletedUserTests
{
    private static readonly TenantId Acme = new("acme");
    private readonly List<User> users = [];
    private readonly List<RefreshToken> tokens = [];
    private readonly FakeRepository<User> userRepo;
    private readonly RefreshTokenService service;

    public RefreshSoftDeletedUserTests()
    {
        userRepo = new FakeRepository<User>(users, u => u.Id) { AmbientTenant = Acme };
        var tokenRepo = new FakeRepository<RefreshToken>(tokens, t => t.Id);
        service = new RefreshTokenService(
            userRepo, tokenRepo, new FakeUnitOfWork(), new FakeTimeProvider(DateTimeOffset.Parse("2026-11-20T10:00:00Z")),
            new IdentityModuleOptions(), NullLogger<RefreshTokenService>.Instance);
    }

    private User AddUser()
    {
        var u = new User { UserName = "alice", NormalizedUserName = "ALICE", TenantId = Acme };
        u.SetId(Guid.NewGuid());
        users.Add(u);
        return u;
    }

    [Fact]
    public async Task ValidateAndRotate_ShouldReturnInvalidAndNotRotate_WhenTheUserIsSoftDeleted()
    {
        var user = AddUser();
        var issued = await service.IssueAsync(user.Id);
        user.IsDeleted = true;
        userRepo.AmbientTenant = null;

        var result = await service.ValidateAndRotateAsync(issued.RawToken);

        Assert.Equal(RefreshOutcome.Invalid, result.Outcome);
        var only = Assert.Single(tokens);
        Assert.Null(only.ConsumedAt);
        Assert.Null(only.RevokedAt);
    }

    [Fact]
    public async Task ValidateAndRotate_ShouldRotate_WhenTheUserIsNotDeleted()
    {
        var user = AddUser();
        var issued = await service.IssueAsync(user.Id);
        userRepo.AmbientTenant = null;

        var result = await service.ValidateAndRotateAsync(issued.RawToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, tokens.Count);
    }
}
