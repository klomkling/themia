using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Specifications;

namespace Themia.Modules.Identity.Services;

/// <summary>
/// Default <see cref="IOtpChallengeService"/>. A guess is counted and compared in one conditional
/// UPDATE (<c>... SET attempt_count = n + 1 [, closed_at = now] WHERE id = @id AND attempt_count = n AND
/// closed_at IS NULL AND expires_at &gt; @now</c>), so parallel guesses cannot overrun the cap and a code
/// cannot be used twice. The cap is per phone across challenges: a new challenge starts with whatever
/// budget the phone has left in the rolling window, never a fresh one.
/// </summary>
public sealed class OtpChallengeService : IOtpChallengeService
{
    // Bounded retries for the compare-and-set: each loss means another guess won the slot and the cap
    // moved by one, so a loop longer than the cap itself cannot make progress.
    private const int MaxCasRetries = 16;

    private const int PurgeBatchSize = 50;

    // Expired rows are kept this long beyond the failure window, then purged on the next issue.
    private static readonly TimeSpan RetentionBeyondWindow = TimeSpan.FromDays(1);

    private readonly IRepository<OtpLoginChallenge, Guid> challenges;
    private readonly IUnitOfWork unitOfWork;
    private readonly TimeProvider timeProvider;
    private readonly OtpLoginOptions options;

    /// <summary>Creates the service. <paramref name="options"/> must already be validated.</summary>
    public OtpChallengeService(
        IRepository<OtpLoginChallenge, Guid> challenges,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        OtpLoginOptions options)
    {
        ArgumentNullException.ThrowIfNull(challenges);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        this.challenges = challenges;
        this.unitOfWork = unitOfWork;
        this.timeProvider = timeProvider;
        this.options = options;
    }

    /// <inheritdoc />
    public async Task<OtpIssueResult> IssueAsync(string normalizedPhoneNumber, Guid? userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPhoneNumber);

        var now = timeProvider.GetUtcNow();
        var windowStart = now - options.FailureWindow;
        var recent = await challenges.ListAsync(new OtpChallengesByPhoneSinceSpec(normalizedPhoneNumber, windowStart), cancellationToken).ConfigureAwait(false);

        if (IsThrottled(recent, now))
        {
            return new OtpIssueResult(OtpIssueOutcome.Throttled, Guid.Empty, default, null);
        }

        await PurgeExpiredAsync(now, cancellationToken).ConfigureAwait(false);

        var id = Guid.CreateVersion7();
        var code = GenerateCode(options.CodeLength);
        var challenge = new OtpLoginChallenge
        {
            Id = id,
            UserId = userId,
            NormalizedPhoneNumber = normalizedPhoneNumber,
            CodeHash = Hash(id, code),
            CreatedAt = now,
            ExpiresAt = now + options.Lifetime,
        };
        await challenges.AddAsync(challenge, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The check above and the insert are not one atomic step, so parallel starts can all pass it. Look
        // again now that the row is in, against everything except itself, and withdraw if the phone is over
        // its limits. Two racing starts may both withdraw (the user retries); neither can both stay open and
        // send, which is the SMS-cost bypass this closes.
        var settled = await challenges.ListAsync(new OtpChallengesByPhoneSinceSpec(normalizedPhoneNumber, windowStart), cancellationToken).ConfigureAwait(false);
        if (IsThrottled(settled.Where(c => c.Id != id).ToList(), now))
        {
            challenges.Remove(challenge);
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new OtpIssueResult(OtpIssueOutcome.Throttled, Guid.Empty, default, null);
        }

        // The newest challenge is the only open one: a second start never leaves two codes guessable at
        // once. If two starts race, each closes the other and both fail closed.
        await challenges.UpdateWhereAsync(
            new OtpOpenChallengesByPhoneExceptSpec(normalizedPhoneNumber, id),
            set => set.Set(c => c.ClosedAt, now),
            cancellationToken).ConfigureAwait(false);

        return new OtpIssueResult(OtpIssueOutcome.Issued, id, challenge.ExpiresAt, code);
    }

    /// <inheritdoc />
    public async Task<OtpVerifyResult> VerifyAsync(Guid challengeId, string code, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(code);

        var now = timeProvider.GetUtcNow();
        var challenge = await challenges.GetByIdAsync(challengeId, cancellationToken).ConfigureAwait(false);
        if (challenge is null)
        {
            // Same HMAC work as a real guess, so an unknown id costs the same as a wrong code.
            _ = Hash(challengeId, code);
            return Invalid();
        }

        for (var attempt = 0; attempt < MaxCasRetries; attempt++)
        {
            if (challenge.ClosedAt is not null || challenge.ExpiresAt <= now)
            {
                _ = Hash(challengeId, code);
                return Invalid();
            }

            var allowed = await AllowedAttemptsAsync(challenge, now, cancellationToken).ConfigureAwait(false);
            var used = challenge.AttemptCount;
            if (used >= allowed)
            {
                return LockedOut();
            }

            // A decoy can never match: it was never delivered, and the comparison still runs.
            var matches = Matches(challenge, code) && challenge.UserId is not null;
            var updated = matches
                ? await challenges.UpdateWhereAsync(
                    new OtpChallengeAttemptGateSpec(challengeId, used, now),
                    set => set.Set(c => c.AttemptCount, used + 1).Set(c => c.ClosedAt, now),
                    cancellationToken).ConfigureAwait(false)
                : await challenges.UpdateWhereAsync(
                    new OtpChallengeAttemptGateSpec(challengeId, used, now),
                    set => set.Set(c => c.AttemptCount, used + 1),
                    cancellationToken).ConfigureAwait(false);

            if (updated == 1)
            {
                if (matches)
                {
                    await OnVerifiedAsync(challenge, now, cancellationToken).ConfigureAwait(false);
                    return new OtpVerifyResult(OtpVerifyOutcome.Verified, challenge.UserId, challenge.NormalizedPhoneNumber);
                }

                return used + 1 >= allowed ? LockedOut() : Invalid();
            }

            // Lost the slot to a parallel guess: re-read and decide again on the new count.
            var reloaded = await challenges.GetByIdAsync(challengeId, cancellationToken).ConfigureAwait(false);
            if (reloaded is null)
            {
                return Invalid();
            }

            challenge = reloaded;
        }

        return Invalid();
    }

    // Bounded housekeeping on the write path, so the table needs no background job: one small batch of
    // long-expired rows per issue is enough to keep pace with the issue rate.
    private async Task PurgeExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expired = await challenges.ListAsync(
            new OtpChallengesExpiredBeforeSpec(now - options.FailureWindow - RetentionBeyondWindow).Page(0, PurgeBatchSize),
            cancellationToken).ConfigureAwait(false);
        foreach (var row in expired)
        {
            challenges.Remove(row);
        }
    }

    private bool IsThrottled(IReadOnlyList<OtpLoginChallenge> recent, DateTimeOffset now)
    {
        if (recent.Count >= options.MaxStartsPerWindow)
        {
            return true;
        }

        if (recent.Sum(c => (long)c.AttemptCount) >= options.MaxFailedAttemptsPerWindow)
        {
            return true;
        }

        return options.ResendCooldown > TimeSpan.Zero
            && recent.Any(c => c.CreatedAt > now - options.ResendCooldown);
    }

    // The guesses this challenge may still receive: its own cap, bounded by what the phone has left of the
    // window budget after the OTHER challenges' failures.
    private async Task<int> AllowedAttemptsAsync(OtpLoginChallenge challenge, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var recent = await challenges.ListAsync(
            new OtpChallengesByPhoneSinceSpec(challenge.NormalizedPhoneNumber, now - options.FailureWindow),
            cancellationToken).ConfigureAwait(false);
        var others = recent.Where(c => c.Id != challenge.Id).Sum(c => (long)c.AttemptCount);
        var remaining = (int)Math.Max(0, options.MaxFailedAttemptsPerWindow - others);
        return Math.Min(options.MaxAttempts, remaining);
    }

    private async Task OnVerifiedAsync(OtpLoginChallenge challenge, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A success clears the phone's failure history and closes any other open challenge.
        await challenges.UpdateWhereAsync(
            new OtpOpenChallengesByPhoneExceptSpec(challenge.NormalizedPhoneNumber, challenge.Id),
            set => set.Set(c => c.ClosedAt, now),
            cancellationToken).ConfigureAwait(false);
        await challenges.UpdateWhereAsync(
            new OtpChallengesWithAttemptsSpec(challenge.NormalizedPhoneNumber, now - options.FailureWindow),
            set => set.Set(c => c.AttemptCount, 0),
            cancellationToken).ConfigureAwait(false);
    }

    private bool Matches(OtpLoginChallenge challenge, string code)
    {
        var expected = Encoding.ASCII.GetBytes(challenge.CodeHash);
        var actual = Encoding.ASCII.GetBytes(Hash(challenge.Id, code));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private string Hash(Guid challengeId, string code)
    {
        var payload = Encoding.UTF8.GetBytes($"{challengeId:N}:{code}");
        return Convert.ToHexString(HMACSHA256.HashData(options.CodeHashKey, payload));
    }

    private static string GenerateCode(int length)
    {
        // GetInt32 is unbiased; zero padding keeps the full digit space (000000..999999).
        var upper = (int)Math.Pow(10, length);
        return RandomNumberGenerator.GetInt32(0, upper).ToString($"D{length}", CultureInfo.InvariantCulture);
    }

    private static OtpVerifyResult Invalid() => new(OtpVerifyOutcome.Invalid, null, null);

    private static OtpVerifyResult LockedOut() => new(OtpVerifyOutcome.LockedOut, null, null);
}
