using System.Security.Claims;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests.External;

internal sealed class FakeClaimsPrincipalFactory : IClaimsPrincipalFactory
{
    /// <summary>The user the last principal was built from: which copy of the account the flow minted the session from.</summary>
    public User? LastUser { get; private set; }

    public Task<ClaimsPrincipal> CreateAsync(User user, string authenticationType, CancellationToken cancellationToken = default)
    {
        LastUser = user;
        return Task.FromResult(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], authenticationType)));
    }
}

/// <summary>A store that answers only the id lookup, which is all the flow's post-hook reload uses.</summary>
internal class FakeUserStore : IUserService
{
    public User? Stored { get; set; }

    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Stored is { } u && u.Id == id ? u : null);

    public Task<UserCreationResult> CreateAsync(string userName, string password, string? email = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserCreationResult> CreateExternalUserAsync(string userName, string? email, bool emailVerified, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<User?> FindByPhoneAsync(string phoneNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserMutationResult> SetEmailAsync(Guid userId, string? email, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserMutationResult> ConfirmEmailAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserMutationResult> SetPhoneNumberAsync(Guid userId, string? phoneNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserMutationResult> ConfirmPhoneNumberAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserMutationResult> SetPasswordAsync(Guid userId, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<PasswordVerificationResult> VerifyPasswordAsync(string userName, string password, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserMutationResult> SetActiveAsync(Guid userId, bool isActive, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<UserMutationResult> DeleteAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

    public Task<int> RevokeAsync(string rawToken, bool allForUser, CancellationToken cancellationToken = default)
    {
        RevokeCalls++;
        LastRevokeAllForUser = allForUser;
        return Task.FromResult(1);
    }

    public Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        RevokeCalls++;
        LastRevokeAllForUser = true;
        return Task.FromResult(0);
    }
}

/// <summary>A store that can also read a platform (tenant-less) user by id, as <c>UserService</c> does.</summary>
internal sealed class FakePlatformAwareUserStore : FakeUserStore, IPlatformUserReader
{
    public Task<User?> FindPlatformUserByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Stored is { TenantId: null } u && u.Id == id ? u : null);
}
