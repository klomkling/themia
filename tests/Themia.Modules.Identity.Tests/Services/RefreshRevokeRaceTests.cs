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

/// <summary>coord #0245: a refresh racing a revoke must never leave a live session. The fake repository replays what the database does under
/// concurrency: a writer that commits just before a statement, and a writer that commits after a running statement took its snapshot.</summary>
public class RefreshRevokeRaceTests
{
    private static readonly TenantId Acme = new("acme");
    private readonly List<User> users = [];
    private readonly List<RefreshToken> tokens = [];
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-26T05:00:00Z"));
    private readonly FakeRepository<User> userRepo;
    private readonly FakeRepository<RefreshToken> tokenRepo;

    public RefreshRevokeRaceTests()
    {
        userRepo = new FakeRepository<User>(users, u => u.Id) { AmbientTenant = Acme };
        tokenRepo = new FakeRepository<RefreshToken>(tokens, t => t.Id) { AmbientTenant = Acme };
    }

    private RefreshTokenService Sut(TimeSpan? grace = null) => new(
        userRepo, tokenRepo, new FakeUnitOfWork(), clock,
        new IdentityModuleOptions { RefreshReuseGrace = grace ?? TimeSpan.Zero }, NullLogger<RefreshTokenService>.Instance);

    private User AddUser()
    {
        var u = new User { UserName = "alice", NormalizedUserName = "ALICE", TenantId = Acme, SecurityStamp = "stamp-1" };
        u.SetId(Guid.NewGuid());
        users.Add(u);
        return u;
    }

    private RefreshToken AddLiveRow(User user, Guid familyId)
    {
        var row = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = Guid.NewGuid().ToString("N"),
            FamilyId = familyId,
            CreatedAt = clock.GetUtcNow(),
            ExpiresAt = clock.GetUtcNow().AddDays(1),
            SecurityStamp = user.SecurityStamp,
        };
        row.SetId(Guid.NewGuid());
        tokens.Add(row);
        return row;
    }

    [Fact]
    public async Task ValidateAndRotate_ShouldAnswerInvalidAndCreateNoSuccessor_WhenARevokeCommitsAfterTheReadAndBeforeTheParentIsSet()
    {
        var user = AddUser();
        var issued = await Sut().IssueAsync(user.Id);
        var parent = tokens.Single();
        tokenRepo.BeforeUpdateWhere = _ =>
        {
            tokenRepo.BeforeUpdateWhere = null;
            parent.RevokedAt = clock.GetUtcNow(); // the revoke's commit, between the rotation's read and its write
        };

        var result = await Sut().ValidateAndRotateAsync(issued.RawToken);

        Assert.Equal(RefreshOutcome.Invalid, result.Outcome);
        Assert.DoesNotContain(tokens, t => t.ReplacedTokenId == parent.Id);
        Assert.NotNull(parent.RevokedAt);
        Assert.Null(parent.ConsumedAt);
        Assert.DoesNotContain(tokens, t => t.RevokedAt is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidateAndRotate_ShouldLeaveOneSuccessorAndMatchTheGraceSetting_WhenARivalRotatesTheSameTokenFirst(bool graceOn)
    {
        var user = AddUser();
        var grace = graceOn ? TimeSpan.FromSeconds(30) : (TimeSpan?)null;
        var issued = await Sut(grace).IssueAsync(user.Id);
        tokenRepo.BeforeUpdateWhere = spec =>
        {
            tokenRepo.BeforeUpdateWhere = null;
            Assert.True(Sut(grace).ValidateAndRotateAsync(issued.RawToken).GetAwaiter().GetResult().TryGetSuccess(out _, out _)); // the rival wins
        };

        var loser = await Sut(grace).ValidateAndRotateAsync(issued.RawToken);

        Assert.Equal(2, tokens.Count); // the parent and the winner's successor, nothing from the loser
        Assert.Equal(graceOn ? RefreshOutcome.ReplayedWithinGrace : RefreshOutcome.Invalid, loser.Outcome);
        Assert.Single(tokens, t => t.ReplacedTokenId is not null);
    }

    [Fact]
    public async Task RevokeAllForUser_ShouldRevokeASuccessorThatCommittedAfterTheStatementTookItsSnapshot()
    {
        var user = AddUser();
        var issued = await Sut().IssueAsync(user.Id);
        var added = false;
        tokenRepo.AfterUpdateWhere = (_, _) =>
        {
            if (added)
            {
                return;
            }

            added = true;
            AddLiveRow(user, issued.FamilyId); // the rotation commits its successor after the revoke's snapshot
        };

        var revoked = await Sut().RevokeAllForUserAsync(user.Id);

        Assert.Equal(2, revoked);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task RevokeFamily_ShouldRevokeASuccessorThatCommittedAfterTheStatementTookItsSnapshot()
    {
        var user = AddUser();
        var issued = await Sut().IssueAsync(user.Id);
        var added = false;
        tokenRepo.AfterUpdateWhere = (_, _) =>
        {
            if (!added)
            {
                added = true;
                AddLiveRow(user, issued.FamilyId);
            }
        };

        await Sut().RevokeAsync(issued.RawToken, allForUser: false);

        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task RevokeAsyncForAllUserSessions_ShouldRevokeASuccessorThatCommittedAfterTheStatementTookItsSnapshot()
    {
        var user = AddUser();
        var issued = await Sut().IssueAsync(user.Id);
        var added = false;
        tokenRepo.AfterUpdateWhere = (_, _) =>
        {
            if (!added)
            {
                added = true;
                AddLiveRow(user, issued.FamilyId);
            }
        };

        var revoked = await Sut().RevokeAsync(issued.RawToken, allForUser: true);

        Assert.Equal(2, revoked);
        Assert.All(tokens, t => Assert.NotNull(t.RevokedAt));
    }

    [Fact]
    public async Task RevokeAllForUser_ShouldThrowAfterFivePasses_WhenEveryPassFindsAFreshLiveRow()
    {
        var user = AddUser();
        var issued = await Sut().IssueAsync(user.Id);
        var passes = 0;
        tokenRepo.AfterUpdateWhere = (_, _) =>
        {
            passes++;
            AddLiveRow(user, issued.FamilyId);
        };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Sut().RevokeAllForUserAsync(user.Id));

        Assert.Equal(5, passes);
        Assert.Contains("did not settle after 5 passes", thrown.Message);
    }

    [Fact]
    public async Task RevokeAllForUserExcept_ShouldBindTheKeptFamilyAndStop_WhenTheStampMoved()
    {
        var user = AddUser();
        var kept = await Sut().IssueAsync(user.Id);
        await Sut().IssueAsync(user.Id);
        user.SecurityStamp = "stamp-2";
        var passes = 0;
        tokenRepo.AfterUpdateWhere = (_, _) => passes++;

        var revoked = await Sut().RevokeAllForUserExceptAsync(user.Id, kept.FamilyId.ToString());

        Assert.Equal(1, revoked);
        Assert.All(tokens.Where(t => t.FamilyId == kept.FamilyId), t => Assert.Equal("stamp-2", t.SecurityStamp));
        Assert.InRange(passes, 4, 4); // revoke: one pass that matches and one that settles; rebind: the same
    }
}
