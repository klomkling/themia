using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

/// <summary>coord #0193: a client abort must not undo a decision the store already made — reuse revokes the
/// family, and hygiene after a committed rotation never fails the refresh.</summary>
public class RefreshAbortTests
{
    private readonly List<User> users = [];
    private readonly List<RefreshToken> tokens = [];
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-03T03:00:00Z"));
    private readonly IdentityModuleOptions options = new();
    private readonly FakeRepository<User> userRepo;
    private readonly FakeRepository<RefreshToken> tokenRepo;

    public RefreshAbortTests()
    {
        userRepo = new FakeRepository<User>(users, u => u.Id);
        tokenRepo = new FakeRepository<RefreshToken>(tokens, t => t.Id);
    }

    private RefreshTokenService Tokens() =>
        new(userRepo, tokenRepo, new FakeUnitOfWork(), clock, options, NullLogger<RefreshTokenService>.Instance);

    private User AddUser()
    {
        var u = new User { UserName = "alice", NormalizedUserName = "ALICE" };
        u.SetId(Guid.NewGuid());
        users.Add(u);
        return u;
    }

    [Fact]
    public async Task ReuseRevokesTheWholeFamily_EvenWhenTheClientAbortsTheRequest()
    {
        options.RefreshReuseGrace = TimeSpan.Zero;
        var issued = await Tokens().IssueAsync(AddUser().Id);
        Assert.True((await Tokens().ValidateAndRotateAsync(issued.RawToken)).TryGetSuccess(out _, out _));
        tokenRepo.HonourCancellationFor = spec => spec.GetType().Name.Contains("Family", StringComparison.Ordinal);
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        var replay = await Tokens().ValidateAndRotateAsync(issued.RawToken, abort.Token);

        Assert.Equal(RefreshOutcome.ReuseDetected, replay.Outcome);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task ASuccessfulRotation_IsStillSuccess_WhenTheClientAbortsDuringTheAgedCiphertextCleanup()
    {
        options.RefreshReuseGrace = TimeSpan.FromSeconds(30);
        var issued = await Tokens().IssueAsync(AddUser().Id);
        using var abort = new CancellationTokenSource();
        tokenRepo.HonourCancellationFor = spec =>
        {
            var cleanup = spec.GetType().Name.Contains("AgedSuccessor", StringComparison.Ordinal);
            if (cleanup) abort.Cancel();
            return cleanup;
        };

        var rotation = await Tokens().ValidateAndRotateAsync(issued.RawToken, abort.Token);

        Assert.True(rotation.TryGetSuccess(out _, out _));
    }

    /// <summary>The user's own "sign out everywhere" (or an admin's revoke) is decided by the time the UPDATE
    /// runs: a client that disconnects at that moment must not leave the sessions valid.</summary>
    private void HonourCancellationOnTheRevokeUpdates() =>
        tokenRepo.HonourCancellationFor = spec => spec.GetType().Name.Contains("ActiveRefreshTokensByUser", StringComparison.Ordinal);

    [Fact]
    public async Task LogoutOfAllSessions_RevokesEverySession_EvenWhenTheClientAbortsTheRequest()
    {
        var user = AddUser();
        var first = await Tokens().IssueAsync(user.Id);
        await Tokens().IssueAsync(user.Id);
        HonourCancellationOnTheRevokeUpdates();
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        var revoked = await Tokens().RevokeAsync(first.RawToken, allForUser: true, abort.Token);

        Assert.Equal(2, revoked);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task RevokeAllForUser_RevokesEverySession_EvenWhenTheClientAbortsTheRequest()
    {
        var user = AddUser();
        await Tokens().IssueAsync(user.Id);
        await Tokens().IssueAsync(user.Id);
        HonourCancellationOnTheRevokeUpdates();
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        var revoked = await Tokens().RevokeAllForUserAsync(user.Id, abort.Token);

        Assert.Equal(2, revoked);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task RevokeAllForUserExcept_RevokesTheOthersAndRebindsTheKeptSession_EvenWhenTheClientAbortsTheRequest()
    {
        var user = AddUser();
        user.SecurityStamp = "stamp-after-the-change";
        await Tokens().IssueAsync(user.Id);
        await Tokens().IssueAsync(user.Id);
        var keptFamily = tokens[0].FamilyId;
        HonourCancellationOnTheRevokeUpdates();
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        var revoked = await Tokens().RevokeAllForUserExceptAsync(user.Id, keptFamily.ToString(), abort.Token);

        Assert.Equal(1, revoked);
        var keptRow = tokens.Single(t => t.FamilyId == keptFamily);
        Assert.Null(keptRow.RevokedAt);
        Assert.Equal("stamp-after-the-change", keptRow.SecurityStamp);
        Assert.All(tokens.Where(t => t.FamilyId != keptFamily), t => Assert.NotNull(t.RevokedAt));
    }
}
