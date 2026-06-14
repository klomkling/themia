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
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-06-14T00:00:00Z"));
    private readonly IdentityModuleOptions options = new();
    private readonly UserTokenService sut;
    private readonly Guid userId = Guid.NewGuid();

    public UserTokenServiceTests()
    {
        var repo = new FakeRepository<UserToken>(tokens, t => t.Id) { AmbientTenant = new TenantId("acme") };
        sut = new UserTokenService(repo, new FakeUnitOfWork(), clock, options);
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
}
