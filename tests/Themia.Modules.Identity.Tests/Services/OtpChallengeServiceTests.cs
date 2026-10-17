using Microsoft.Extensions.Time.Testing;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Services;
using Themia.Modules.Identity.Tests.Fakes;
using Xunit;

namespace Themia.Modules.Identity.Tests.Services;

public class OtpChallengeServiceTests
{
    private const string Phone = "+66812345678";
    private const string WrongCode = "000000";

    private readonly List<OtpLoginChallenge> rows = [];
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-17T10:00:00Z"));
    private readonly Guid userId = Guid.NewGuid();
    private readonly OtpLoginOptions options = new()
    {
        CodeHashKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(),
        ResendCooldown = TimeSpan.Zero,
    };

    private OtpChallengeService NewService() =>
        new(new FakeRepository<OtpLoginChallenge>(rows, c => c.Id), new FakeUnitOfWork(), clock, options);

    [Fact]
    public async Task Issue_StoresAKeyedHashAndNeverTheCode()
    {
        var issued = await NewService().IssueAsync(Phone, userId);

        var row = Assert.Single(rows);
        Assert.Equal(OtpIssueOutcome.Issued, issued.Outcome);
        Assert.Matches("^[0-9]{6}$", issued.Code);
        Assert.DoesNotContain(issued.Code!, row.CodeHash);
        Assert.Equal(64, row.CodeHash.Length);
        Assert.Equal(issued.ChallengeId, row.Id);
        Assert.Equal(clock.GetUtcNow() + options.Lifetime, issued.ExpiresAt);
    }

    [Fact]
    public async Task Issue_HashDependsOnTheServerKey()
    {
        var first = await NewService().IssueAsync(Phone, userId);
        var firstHash = rows.Single().CodeHash;
        rows.Clear();
        options.CodeHashKey = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
        // Same id and code under another key must not produce the same hash.
        var service = NewService();
        rows.Add(new OtpLoginChallenge
        {
            Id = first.ChallengeId, UserId = userId, NormalizedPhoneNumber = Phone, CodeHash = firstHash,
            CreatedAt = clock.GetUtcNow(), ExpiresAt = first.ExpiresAt,
        });

        var result = await service.VerifyAsync(first.ChallengeId, first.Code!);

        Assert.Equal(OtpVerifyOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Verify_AcceptsTheRightCodeOnce()
    {
        var service = NewService();
        var issued = await service.IssueAsync(Phone, userId);

        var first = await service.VerifyAsync(issued.ChallengeId, issued.Code!);
        var second = await service.VerifyAsync(issued.ChallengeId, issued.Code!);

        Assert.True(first.Verified);
        Assert.Equal(userId, first.UserId);
        Assert.Equal(Phone, first.NormalizedPhoneNumber);
        Assert.Equal(OtpVerifyOutcome.Invalid, second.Outcome);
    }

    [Fact]
    public async Task Verify_LocksOutAfterMaxAttempts_EvenForTheRightCode()
    {
        var service = NewService();
        var issued = await service.IssueAsync(Phone, userId);

        for (var i = 0; i < options.MaxAttempts - 1; i++)
        {
            Assert.Equal(OtpVerifyOutcome.Invalid, (await service.VerifyAsync(issued.ChallengeId, WrongCode)).Outcome);
        }

        Assert.Equal(OtpVerifyOutcome.LockedOut, (await service.VerifyAsync(issued.ChallengeId, WrongCode)).Outcome);
        Assert.Equal(OtpVerifyOutcome.LockedOut, (await service.VerifyAsync(issued.ChallengeId, issued.Code!)).Outcome);
        Assert.Equal(options.MaxAttempts, rows.Single().AttemptCount);
    }

    [Fact]
    public async Task Verify_RejectsAnExpiredChallenge()
    {
        var service = NewService();
        var issued = await service.IssueAsync(Phone, userId);
        clock.Advance(options.Lifetime + TimeSpan.FromSeconds(1));

        Assert.Equal(OtpVerifyOutcome.Invalid, (await service.VerifyAsync(issued.ChallengeId, issued.Code!)).Outcome);
    }

    [Fact]
    public async Task Verify_RejectsAnUnknownChallenge()
    {
        Assert.Equal(OtpVerifyOutcome.Invalid, (await NewService().VerifyAsync(Guid.NewGuid(), WrongCode)).Outcome);
    }

    [Fact]
    public async Task Verify_OnADecoyFailsLikeARealChallengeAndLocksOutAtTheSameCap()
    {
        var service = NewService();
        var decoy = await service.IssueAsync(Phone, userId: null);

        Assert.Null(rows.Single().UserId);
        for (var i = 0; i < options.MaxAttempts - 1; i++)
        {
            Assert.Equal(OtpVerifyOutcome.Invalid, (await service.VerifyAsync(decoy.ChallengeId, decoy.Code!)).Outcome);
        }

        Assert.Equal(OtpVerifyOutcome.LockedOut, (await service.VerifyAsync(decoy.ChallengeId, decoy.Code!)).Outcome);
    }

    [Fact]
    public async Task Issue_ClosesThePreviousOpenChallenge()
    {
        var service = NewService();
        var older = await service.IssueAsync(Phone, userId);
        await service.IssueAsync(Phone, userId);

        Assert.Equal(OtpVerifyOutcome.Invalid, (await service.VerifyAsync(older.ChallengeId, older.Code!)).Outcome);
    }

    [Fact]
    public async Task Issue_IsThrottledInsideTheResendCooldown()
    {
        options.ResendCooldown = TimeSpan.FromSeconds(60);
        var service = NewService();

        var first = await service.IssueAsync(Phone, userId);
        var second = await service.IssueAsync(Phone, userId);
        clock.Advance(TimeSpan.FromSeconds(61));
        var third = await service.IssueAsync(Phone, userId);

        Assert.Equal(OtpIssueOutcome.Issued, first.Outcome);
        Assert.Equal(OtpIssueOutcome.Throttled, second.Outcome);
        Assert.Null(second.Code);
        Assert.Equal(OtpIssueOutcome.Issued, third.Outcome);
    }

    [Fact]
    public async Task Issue_IsThrottledAfterMaxStartsInTheWindow_AndRecoversWhenItSlides()
    {
        var service = NewService();
        for (var i = 0; i < options.MaxStartsPerWindow; i++)
        {
            Assert.Equal(OtpIssueOutcome.Issued, (await service.IssueAsync(Phone, userId)).Outcome);
        }

        Assert.Equal(OtpIssueOutcome.Throttled, (await service.IssueAsync(Phone, userId)).Outcome);
        clock.Advance(options.FailureWindow + TimeSpan.FromSeconds(1));
        Assert.Equal(OtpIssueOutcome.Issued, (await service.IssueAsync(Phone, userId)).Outcome);
    }

    [Fact]
    public async Task Throttle_IsIdenticalForADecoyAndARealPhone()
    {
        options.ResendCooldown = TimeSpan.FromSeconds(60);
        var service = NewService();

        await service.IssueAsync("+66800000001", userId);
        await service.IssueAsync("+66800000002", userId: null);

        Assert.Equal(OtpIssueOutcome.Throttled, (await service.IssueAsync("+66800000001", userId)).Outcome);
        Assert.Equal(OtpIssueOutcome.Throttled, (await service.IssueAsync("+66800000002", userId: null)).Outcome);
    }

    [Fact]
    public async Task FailureCap_SpansChallenges_AndANewStartNeverResetsIt()
    {
        options.MaxAttempts = 3;
        options.MaxFailedAttemptsPerWindow = 5;
        var service = NewService();

        var first = await service.IssueAsync(Phone, userId);
        await Fail(service, first.ChallengeId, 3);
        var second = await service.IssueAsync(Phone, userId);
        await Fail(service, second.ChallengeId, 1);

        // 4 failures so far; the second challenge has exactly one guess left in the phone's budget, and
        // spending it hits the cap.
        Assert.Equal(OtpVerifyOutcome.LockedOut, (await service.VerifyAsync(second.ChallengeId, WrongCode)).Outcome);
        Assert.Equal(OtpVerifyOutcome.LockedOut, (await service.VerifyAsync(second.ChallengeId, second.Code!)).Outcome);
        Assert.Equal(OtpIssueOutcome.Throttled, (await service.IssueAsync(Phone, userId)).Outcome);
    }

    [Fact]
    public async Task FailureCap_StopsALoopOfStartThenGuess()
    {
        options.MaxAttempts = 5;
        options.MaxFailedAttemptsPerWindow = 8;
        options.MaxStartsPerWindow = 100;
        var service = NewService();
        var guesses = 0;

        for (var round = 0; round < 20; round++)
        {
            var issued = await service.IssueAsync(Phone, userId);
            if (issued.Outcome == OtpIssueOutcome.Throttled)
            {
                break;
            }

            for (var i = 0; i < options.MaxAttempts; i++)
            {
                var result = await service.VerifyAsync(issued.ChallengeId, WrongCode);
                if (result.Outcome == OtpVerifyOutcome.LockedOut)
                {
                    break;
                }

                guesses++;
            }
        }

        Assert.True(rows.Sum(r => r.AttemptCount) <= options.MaxFailedAttemptsPerWindow);
        Assert.True(guesses < options.MaxFailedAttemptsPerWindow);
    }

    [Fact]
    public async Task FailureCap_IsKeyedPerPhone()
    {
        options.MaxAttempts = 2;
        options.MaxFailedAttemptsPerWindow = 2;
        var service = NewService();
        var victim = await service.IssueAsync("+66800000001", userId);
        await Fail(service, victim.ChallengeId, 2);

        var other = await service.IssueAsync("+66800000002", userId);

        Assert.True((await service.VerifyAsync(other.ChallengeId, other.Code!)).Verified);
    }

    [Fact]
    public async Task Success_ResetsTheFailureCounter()
    {
        options.MaxAttempts = 3;
        options.MaxFailedAttemptsPerWindow = 4;
        var service = NewService();
        var first = await service.IssueAsync(Phone, userId);
        await Fail(service, first.ChallengeId, 2);
        Assert.True((await service.VerifyAsync(first.ChallengeId, first.Code!)).Verified);

        var next = await service.IssueAsync(Phone, userId);
        await Fail(service, next.ChallengeId, 1);

        // Without the reset the earlier two failures would leave this challenge only two guesses.
        Assert.Equal(OtpVerifyOutcome.Invalid, (await service.VerifyAsync(next.ChallengeId, WrongCode)).Outcome);
    }

    [Fact]
    public async Task ParallelVerify_ConsumesTheCodeExactlyOnce()
    {
        var service = NewService();
        var issued = await service.IssueAsync(Phone, userId);

        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => service.VerifyAsync(issued.ChallengeId, issued.Code!))));

        Assert.Equal(1, results.Count(r => r.Verified));
    }

    [Fact]
    public async Task ParallelWrongGuesses_NeverExceedMaxAttempts()
    {
        var service = NewService();
        var issued = await service.IssueAsync(Phone, userId);

        var results = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => service.VerifyAsync(issued.ChallengeId, WrongCode))));

        Assert.Equal(options.MaxAttempts, rows.Single().AttemptCount);
        Assert.DoesNotContain(results, r => r.Verified);
        Assert.True(results.Count(r => r.Outcome == OtpVerifyOutcome.Invalid) < options.MaxAttempts);
    }

    [Fact]
    public async Task Issue_StampsTheAmbientTenant()
    {
        var tenant = new Themia.Framework.Core.Abstractions.Tenancy.TenantId("acme");
        var repo = new FakeRepository<OtpLoginChallenge>(rows, c => c.Id) { AmbientTenant = tenant };
        var service = new OtpChallengeService(repo, new FakeUnitOfWork(), clock, options);

        await service.IssueAsync(Phone, userId);

        Assert.Equal(tenant, rows.Single().TenantId);
    }

    [Fact]
    public async Task Verify_FindsTheChallengeInAnyTenantScope_AndReportsItsTenant()
    {
        // coord #0166: a verify request has no JWT, so the ambient scope is not the challenge's tenant.
        var acme = new Themia.Framework.Core.Abstractions.Tenancy.TenantId("acme");
        var issuing = new OtpChallengeService(
            new FakeRepository<OtpLoginChallenge>(rows, c => c.Id) { AmbientTenant = acme }, new FakeUnitOfWork(), clock, options);
        var issued = await issuing.IssueAsync(Phone, userId);

        foreach (Themia.Framework.Core.Abstractions.Tenancy.TenantId? scope in new Themia.Framework.Core.Abstractions.Tenancy.TenantId?[] { null, new("other") })
        {
            rows.Single().ClosedAt = null;
            rows.Single().AttemptCount = 0;
            var verifying = new OtpChallengeService(
                new FakeRepository<OtpLoginChallenge>(rows, c => c.Id) { AmbientTenant = scope }, new FakeUnitOfWork(), clock, options);

            var result = await verifying.VerifyAsync(issued.ChallengeId, issued.Code!);

            Assert.True(result.Verified);
            Assert.Equal(acme, result.TenantId);
            Assert.Equal(userId, result.UserId);
        }
    }

    [Fact]
    public async Task Verify_CountsGuessesAgainstTheChallengeFromAnyScope()
    {
        var acme = new Themia.Framework.Core.Abstractions.Tenancy.TenantId("acme");
        var issuing = new OtpChallengeService(
            new FakeRepository<OtpLoginChallenge>(rows, c => c.Id) { AmbientTenant = acme }, new FakeUnitOfWork(), clock, options);
        var issued = await issuing.IssueAsync(Phone, userId);
        var nullScope = new OtpChallengeService(
            new FakeRepository<OtpLoginChallenge>(rows, c => c.Id), new FakeUnitOfWork(), clock, options);

        await Fail(nullScope, issued.ChallengeId, options.MaxAttempts);

        Assert.Equal(options.MaxAttempts, rows.Single().AttemptCount);
        Assert.Equal(OtpVerifyOutcome.LockedOut, (await nullScope.VerifyAsync(issued.ChallengeId, issued.Code!)).Outcome);
    }

    [Fact]
    public async Task Issue_ChallengeIdIsRandomNotTimeOrdered()
    {
        // The id now selects a row across tenants, so it carries the most random bits a Guid can: version 4.
        var first = await NewService().IssueAsync(Phone, userId);
        clock.Advance(TimeSpan.FromMinutes(10));
        var second = await NewService().IssueAsync(Phone, userId);

        Assert.Equal(4, first.ChallengeId.Version);
        Assert.Equal(4, second.ChallengeId.Version);
    }

    [Fact]
    public async Task Throttles_AreKeyedOnThePhoneAcrossTenants()
    {
        options.MaxAttempts = 2;
        options.MaxFailedAttemptsPerWindow = 2;
        var acme = new OtpChallengeService(
            new FakeRepository<OtpLoginChallenge>(rows, c => c.Id) { AmbientTenant = new Themia.Framework.Core.Abstractions.Tenancy.TenantId("acme") },
            new FakeUnitOfWork(), clock, options);
        var other = new OtpChallengeService(
            new FakeRepository<OtpLoginChallenge>(rows, c => c.Id) { AmbientTenant = new Themia.Framework.Core.Abstractions.Tenancy.TenantId("other") },
            new FakeUnitOfWork(), clock, options);
        var first = await acme.IssueAsync(Phone, userId);
        await Fail(acme, first.ChallengeId, 2);

        // A platform user reachable from any tenant context must not get a fresh budget per tenant.
        Assert.Equal(OtpIssueOutcome.Throttled, (await other.IssueAsync(Phone, userId)).Outcome);
    }

    [Fact]
    public async Task Issue_PurgesLongExpiredRowsOfEveryTenant()
    {
        var other = new Themia.Framework.Core.Abstractions.Tenancy.TenantId("other");
        var old = clock.GetUtcNow() - options.FailureWindow - TimeSpan.FromDays(2);
        var stale = new OtpLoginChallenge
        {
            Id = Guid.NewGuid(), TenantId = other, UserId = userId, NormalizedPhoneNumber = "+66800000009", CodeHash = "x",
            CreatedAt = old, ExpiresAt = old + options.Lifetime,
        };
        rows.Add(stale);
        var acme = new OtpChallengeService(
            new FakeRepository<OtpLoginChallenge>(rows, c => c.Id) { AmbientTenant = new Themia.Framework.Core.Abstractions.Tenancy.TenantId("acme") },
            new FakeUnitOfWork(), clock, options);

        await acme.IssueAsync(Phone, userId);

        Assert.DoesNotContain(rows, r => r.Id == stale.Id);
    }

    [Fact]
    public async Task Issue_WithdrawsItselfWhenARivalStartLandedInTheSameInstant()
    {
        options.ResendCooldown = TimeSpan.FromSeconds(60);
        var inner = new FakeRepository<OtpLoginChallenge>(rows, c => c.Id);
        var rival = new OtpLoginChallenge
        {
            Id = Guid.NewGuid(), UserId = userId, NormalizedPhoneNumber = Phone, CodeHash = "x",
            CreatedAt = clock.GetUtcNow(), ExpiresAt = clock.GetUtcNow() + options.Lifetime,
        };
        var racing = new RivalInsertingRepository(inner, rows, rival);
        var service = new OtpChallengeService(racing, new FakeUnitOfWork(), clock, options);

        // The cooldown check passed (no row yet); the rival commits between that check and this insert.
        var result = await service.IssueAsync(Phone, userId);

        Assert.Equal(OtpIssueOutcome.Throttled, result.Outcome);
        Assert.Null(result.Code);
        Assert.Equal(rival.Id, Assert.Single(rows).Id);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    public void Options_RejectACodeLengthOutsideSixToNine(int length)
    {
        options.CodeLength = length;
        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Options_RejectAShortOrMissingHashKey()
    {
        options.CodeHashKey = new byte[31];
        Assert.Throws<ArgumentException>(options.Validate);
        options.CodeHashKey = [];
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void Options_ToStringNeverPrintsTheKey()
    {
        Assert.DoesNotContain("CodeHashKey", options.ToString());
        Assert.DoesNotContain("System.Byte", options.ToString());
    }

    private static async Task Fail(OtpChallengeService service, Guid challengeId, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await service.VerifyAsync(challengeId, WrongCode);
        }
    }
}

/// <summary>Adds a competing row at the moment the service inserts its own, to replay a lost check-then-insert race.</summary>
internal sealed class RivalInsertingRepository(
    FakeRepository<OtpLoginChallenge> inner, List<OtpLoginChallenge> store, OtpLoginChallenge rival)
    : Themia.Framework.Data.Abstractions.Repositories.IRepository<OtpLoginChallenge, Guid>
{
    private bool raced;

    public Task AddAsync(OtpLoginChallenge entity, CancellationToken cancellationToken = default)
    {
        if (!raced)
        {
            raced = true;
            store.Add(rival);
        }

        return inner.AddAsync(entity, cancellationToken);
    }

    public void Update(OtpLoginChallenge entity) => inner.Update(entity);
    public void Remove(OtpLoginChallenge entity) => inner.Remove(entity);
    public Task<int> UpdateWhereAsync(
        Themia.Framework.Data.Abstractions.Specifications.ISpecification<OtpLoginChallenge> specification,
        Action<Themia.Framework.Data.Abstractions.Repositories.IBulkUpdateSetters<OtpLoginChallenge>> set,
        CancellationToken cancellationToken = default) => inner.UpdateWhereAsync(specification, set, cancellationToken);
    public Task<OtpLoginChallenge?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => inner.GetByIdAsync(id, cancellationToken);
    public Task<IReadOnlyList<OtpLoginChallenge>> ListAsync(Themia.Framework.Data.Abstractions.Specifications.ISpecification<OtpLoginChallenge> specification, CancellationToken cancellationToken = default) => inner.ListAsync(specification, cancellationToken);
    public Task<OtpLoginChallenge?> FirstOrDefaultAsync(Themia.Framework.Data.Abstractions.Specifications.ISpecification<OtpLoginChallenge> specification, CancellationToken cancellationToken = default) => inner.FirstOrDefaultAsync(specification, cancellationToken);
    public Task<long> CountAsync(Themia.Framework.Data.Abstractions.Specifications.ISpecification<OtpLoginChallenge> specification, CancellationToken cancellationToken = default) => inner.CountAsync(specification, cancellationToken);
    public Task<bool> AnyAsync(Themia.Framework.Data.Abstractions.Specifications.ISpecification<OtpLoginChallenge> specification, CancellationToken cancellationToken = default) => inner.AnyAsync(specification, cancellationToken);
    public Task<Themia.Framework.Data.Abstractions.Paging.PagedResult<OtpLoginChallenge>> PageAsync(Themia.Framework.Data.Abstractions.Specifications.ISpecification<OtpLoginChallenge> specification, CancellationToken cancellationToken = default) => inner.PageAsync(specification, cancellationToken);
}
