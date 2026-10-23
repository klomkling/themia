using Microsoft.Extensions.Time.Testing;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class UserTokenValidationTests
{
    private readonly List<UserToken> tokens = [];
    private readonly List<User> users = [];
    private readonly TenantId tenant = new("acme");
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-23T00:00:00Z"));
    private readonly UserTokenService sut;
    private readonly Guid userId = Guid.NewGuid();

    public UserTokenValidationTests()
    {
        SeedUser(userId, tenant);
        sut = new UserTokenService(
            new FakeRepository<User>(users, u => u.Id) { AmbientTenant = tenant },
            new FakeRepository<UserToken>(tokens, t => t.Id) { AmbientTenant = tenant },
            new FakeUnitOfWork(), clock, new IdentityModuleOptions());
    }

    private void SeedUser(Guid id, TenantId? userTenant)
    {
        var u = new User { UserName = "u", NormalizedUserName = "U", TenantId = userTenant };
        u.SetId(id);
        users.Add(u);
    }

    [Fact]
    public async Task ValidateAsync_returns_the_user_of_a_valid_token_without_consuming_it()
    {
        var raw = await sut.GenerateAsync(userId, TokenPurpose.PasswordReset);

        var result = await sut.ValidateAsync(TokenPurpose.PasswordReset, raw);

        Assert.True(result.IsValid);
        Assert.Equal(TokenValidationOutcome.Valid, result.Outcome);
        Assert.Equal(userId, result.UserId);
        Assert.Null(Assert.Single(tokens).ConsumedAt);
        Assert.Equal(TokenConsumeResult.Success, await sut.ConsumeAsync(userId, TokenPurpose.PasswordReset, raw));
    }

    [Fact]
    public async Task ValidateAsync_can_be_repeated_and_does_not_extend_the_expiry()
    {
        var raw = await sut.GenerateAsync(userId, TokenPurpose.PasswordReset, TimeSpan.FromHours(1));
        var expiry = Assert.Single(tokens).ExpiresAt;

        Assert.True((await sut.ValidateAsync(TokenPurpose.PasswordReset, raw)).IsValid);
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True((await sut.ValidateAsync(TokenPurpose.PasswordReset, raw)).IsValid);

        Assert.Equal(expiry, Assert.Single(tokens).ExpiresAt);
    }

    [Fact]
    public async Task ValidateAsync_reports_not_found_for_an_unknown_token_and_no_user()
    {
        await sut.GenerateAsync(userId, TokenPurpose.PasswordReset);

        var result = await sut.ValidateAsync(TokenPurpose.PasswordReset, "not-a-real-token");

        Assert.False(result.IsValid);
        Assert.Equal(TokenValidationOutcome.NotFound, result.Outcome);
        Assert.Null(result.UserId);
    }

    [Fact]
    public async Task ValidateAsync_reports_not_found_for_the_wrong_purpose()
    {
        var raw = await sut.GenerateAsync(userId, TokenPurpose.PasswordReset);

        var result = await sut.ValidateAsync(TokenPurpose.EmailConfirm, raw);

        Assert.Equal(TokenValidationOutcome.NotFound, result.Outcome);
        Assert.Null(result.UserId);
    }

    [Fact]
    public async Task ValidateAsync_reports_expired_without_revealing_the_user()
    {
        var raw = await sut.GenerateAsync(userId, TokenPurpose.PasswordReset, TimeSpan.FromMinutes(5));
        clock.Advance(TimeSpan.FromMinutes(6));

        var result = await sut.ValidateAsync(TokenPurpose.PasswordReset, raw);

        Assert.Equal(TokenValidationOutcome.Expired, result.Outcome);
        Assert.Null(result.UserId);
    }

    [Fact]
    public async Task ValidateAsync_reports_already_consumed_without_revealing_the_user()
    {
        var raw = await sut.GenerateAsync(userId, TokenPurpose.PasswordReset);
        await sut.ConsumeAsync(userId, TokenPurpose.PasswordReset, raw);

        var result = await sut.ValidateAsync(TokenPurpose.PasswordReset, raw);

        Assert.Equal(TokenValidationOutcome.AlreadyConsumed, result.Outcome);
        Assert.Null(result.UserId);
    }

    [Fact]
    public async Task ValidateAsync_reports_not_found_when_the_token_owner_is_in_another_tenant()
    {
        var otherUser = Guid.NewGuid();
        SeedUser(otherUser, new TenantId("other"));
        // The owner is outside the ambient tenant, so the token is planted directly (GenerateAsync refuses it).
        var raw = "planted-token-value";
        var planted = new UserToken
        {
            UserId = otherUser,
            Purpose = TokenPurpose.PasswordReset,
            TokenHash = Themia.Modules.Identity.Hashing.TokenHasher.Hash(raw),
            ExpiresAt = clock.GetUtcNow().AddHours(1),
        };
        planted.SetId(Guid.NewGuid());
        tokens.Add(planted);

        var result = await sut.ValidateAsync(TokenPurpose.PasswordReset, raw);

        Assert.Equal(TokenValidationOutcome.NotFound, result.Outcome);
        Assert.Null(result.UserId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ValidateAsync_rejects_a_blank_token(string raw)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => sut.ValidateAsync(TokenPurpose.PasswordReset, raw));
    }
}
