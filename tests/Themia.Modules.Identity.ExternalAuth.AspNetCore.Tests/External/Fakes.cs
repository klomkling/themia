using System.Security.Claims;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests.External;

internal sealed class FakeClaimsPrincipalFactory : IClaimsPrincipalFactory
{
    public Task<ClaimsPrincipal> CreateAsync(User user, string authenticationType, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], authenticationType)));
}

internal sealed class FakeAccessTokenService(TimeProvider? clock = null) : IAccessTokenService
{
    public int IssueCalls { get; private set; }

    public AccessToken Issue(ClaimsPrincipal principal)
    {
        IssueCalls++;
        return new("access-jwt", (clock ?? TimeProvider.System).GetUtcNow().AddMinutes(15));
    }
}

internal sealed class FakeRefreshTokenService : IRefreshTokenService
{
    public int IssueCalls { get; private set; }
    public RefreshValidationResult RotateResult { get; set; }
    public int RevokeCalls { get; private set; }
    public bool LastRevokeAllForUser { get; private set; }

    public Task<RefreshIssue> IssueAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        IssueCalls++;
        return Task.FromResult(new RefreshIssue("refresh-raw", DateTimeOffset.UtcNow.AddDays(14), Guid.NewGuid()));
    }

    public Task<RefreshValidationResult> ValidateAndRotateAsync(string rawToken, CancellationToken cancellationToken = default) =>
        Task.FromResult(RotateResult);

    public Task RevokeAsync(string rawToken, bool allForUser, CancellationToken cancellationToken = default)
    {
        RevokeCalls++;
        LastRevokeAllForUser = allForUser;
        return Task.CompletedTask;
    }
}
