using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class UserTokenServiceTests
{
    private readonly List<UserToken> tokens = [];
    private readonly List<User> users = [];
    private readonly TenantId tenant = new("acme");
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-06-14T00:00:00Z"));
    private readonly IdentityModuleOptions options = new();
    private readonly UserTokenService sut;
    private readonly Guid userId = Guid.NewGuid();

    public UserTokenServiceTests()
    {
        SeedUser(userId, tenant);
        var userRepo = new FakeRepository<User>(users, u => u.Id) { AmbientTenant = tenant };
        var repo = new FakeRepository<UserToken>(tokens, t => t.Id) { AmbientTenant = tenant };
        sut = new UserTokenService(userRepo, repo, new FakeUnitOfWork(), clock, options);
    }

    private void SeedUser(Guid id, TenantId? userTenant)
    {
        var u = new User { UserName = "u", NormalizedUserName = "U", TenantId = userTenant };
        u.SetId(id);
        users.Add(u);
    }

    [Fact]
    public async Task Generate_returns_raw_token_and_stores_only_its_hash()
    {
        var raw = await sut.GenerateAsync(userId, TokenPurpose.PasswordReset);

        Assert.False(string.IsNullOrWhiteSpace(raw));
        var stored = Assert.Single(tokens);
        Assert.NotEqual(raw, stored.TokenHash);     // hash, not raw
        Assert.Null(stored.ConsumedAt);
    }

    [Fact]
    public async Task Consume_succeeds_once_then_reports_already_consumed()
    {
        var raw = await sut.GenerateAsync(userId, TokenPurpose.EmailConfirm);

        Assert.Equal(TokenConsumeResult.Success, await sut.ConsumeAsync(userId, TokenPurpose.EmailConfirm, raw));
        Assert.Equal(TokenConsumeResult.AlreadyConsumed, await sut.ConsumeAsync(userId, TokenPurpose.EmailConfirm, raw));
    }

    [Fact]
    public async Task Consume_reports_not_found_for_wrong_token()
    {
        await sut.GenerateAsync(userId, TokenPurpose.EmailConfirm);
        Assert.Equal(TokenConsumeResult.NotFound, await sut.ConsumeAsync(userId, TokenPurpose.EmailConfirm, "bogus"));
    }

    [Fact]
    public async Task Consume_reports_expired_after_lifetime()
    {
        var raw = await sut.GenerateAsync(userId, TokenPurpose.PasswordReset, TimeSpan.FromMinutes(5));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(TokenConsumeResult.Expired, await sut.ConsumeAsync(userId, TokenPurpose.PasswordReset, raw));
    }

    [Fact]
    public async Task Generate_throws_for_user_in_another_tenant()
    {
        var otherUser = Guid.NewGuid();
        SeedUser(otherUser, new TenantId("other"));   // invisible under ambient "acme"

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.GenerateAsync(otherUser, TokenPurpose.PasswordReset));

        Assert.Empty(tokens);
    }

    [Fact]
    public async Task Consume_returns_not_found_for_user_in_another_tenant()
    {
        var otherUser = Guid.NewGuid();
        SeedUser(otherUser, new TenantId("other"));   // invisible under ambient "acme"
        // A pre-existing token row for that user (the child table carries no tenant_id column).
        tokens.Add(new UserToken
        {
            Id = Guid.NewGuid(),
            UserId = otherUser,
            Purpose = TokenPurpose.PasswordReset,
            TokenHash = "irrelevant",
            ExpiresAt = clock.GetUtcNow().AddHours(1),
        });

        Assert.Equal(TokenConsumeResult.NotFound, await sut.ConsumeAsync(otherUser, TokenPurpose.PasswordReset, "anything"));
    }

    [Fact]
    public async Task Consume_rejects_token_minted_for_a_different_user()
    {
        var otherUser = Guid.NewGuid();
        SeedUser(otherUser, tenant);                  // both users in scope
        var raw = await sut.GenerateAsync(userId, TokenPurpose.PasswordReset);

        // userB presents userA's raw token: the per-user query never sees it.
        Assert.Equal(TokenConsumeResult.NotFound, await sut.ConsumeAsync(otherUser, TokenPurpose.PasswordReset, raw));
    }

    [Fact]
    public async Task Consume_rejects_token_minted_for_a_different_purpose()
    {
        var raw = await sut.GenerateAsync(userId, TokenPurpose.EmailConfirm);

        Assert.Equal(TokenConsumeResult.NotFound, await sut.ConsumeAsync(userId, TokenPurpose.PasswordReset, raw));
    }
}
