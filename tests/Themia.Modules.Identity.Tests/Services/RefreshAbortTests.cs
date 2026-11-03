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
}
