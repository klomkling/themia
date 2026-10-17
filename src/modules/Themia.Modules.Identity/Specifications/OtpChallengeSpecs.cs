using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Specifications;

// The three phone-keyed specs below ignore the tenant filter on purpose. A platform user can be reached from
// any tenant context, so a throttle scoped to the ambient tenant would hand an attacker a fresh budget per
// tenant. Keying on the phone alone is the stricter choice: tenants that share a number share its budget.

/// <summary>Challenges issued for a phone since <c>since</c>, across tenants.</summary>
internal sealed class OtpChallengesByPhoneSinceSpec : Specification<OtpLoginChallenge>
{
    public OtpChallengesByPhoneSinceSpec(string normalizedPhone, DateTimeOffset since)
    {
        Where(c => c.NormalizedPhoneNumber == normalizedPhone && c.CreatedAt >= since);
        WithoutTenantFilter();
    }
}

/// <summary>A challenge by id in any tenant. The id is a secret (122 random bits) and a verify request carries
/// no tenant scope, so the row is found by it alone; the login then runs as the row's own tenant.</summary>
internal sealed class OtpChallengeByIdSpec : Specification<OtpLoginChallenge>
{
    public OtpChallengeByIdSpec(Guid id)
    {
        Where(c => c.Id == id);
        WithoutTenantFilter();
    }
}

/// <summary>The compare-and-set gate of one guess: matches only while the challenge is still open, unexpired
/// and has exactly <c>expectedAttempts</c> guesses recorded. Two parallel guesses read the same count and
/// only one of them matches, so the counter can never be overrun and a code can never be used twice.</summary>
internal sealed class OtpChallengeAttemptGateSpec : Specification<OtpLoginChallenge>
{
    public OtpChallengeAttemptGateSpec(Guid id, int expectedAttempts, DateTimeOffset now)
    {
        Where(c => c.Id == id && c.ClosedAt == null && c.ExpiresAt > now && c.AttemptCount == expectedAttempts);
        WithoutTenantFilter();
    }
}

/// <summary>Every still-open challenge for a phone except one.</summary>
internal sealed class OtpOpenChallengesByPhoneExceptSpec : Specification<OtpLoginChallenge>
{
    public OtpOpenChallengesByPhoneExceptSpec(string normalizedPhone, Guid exceptId)
    {
        Where(c => c.NormalizedPhoneNumber == normalizedPhone && c.ClosedAt == null && c.Id != exceptId);
        WithoutTenantFilter();
    }
}

/// <summary>Challenges for a phone that carry recorded failures, from the window start.</summary>
internal sealed class OtpChallengesWithAttemptsSpec : Specification<OtpLoginChallenge>
{
    public OtpChallengesWithAttemptsSpec(string normalizedPhone, DateTimeOffset since)
    {
        Where(c => c.NormalizedPhoneNumber == normalizedPhone && c.CreatedAt >= since && c.AttemptCount > 0);
        WithoutTenantFilter();
    }
}

/// <summary>Rows that expired long enough ago to be dropped.</summary>
internal sealed class OtpChallengesExpiredBeforeSpec : Specification<OtpLoginChallenge>
{
    public OtpChallengesExpiredBeforeSpec(DateTimeOffset cutoff)
    {
        // Housekeeping is not tenant work: a tenant-filtered purge would never reach the rows of tenants
        // that have gone quiet, so the table would only ever shrink for whoever is active now.
        Where(c => c.ExpiresAt < cutoff);
        WithoutTenantFilter();
    }
}
