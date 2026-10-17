using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Identity.Abstractions.Entities;

/// <summary>
/// One short-lived numeric code issued for a phone number. Only a keyed hash of the code is stored. A row
/// with <see cref="UserId"/> <see langword="null"/> is a decoy issued for a phone that cannot log in: it
/// behaves exactly like a real challenge (attempts, expiry, lockout) but no code is ever sent for it, so a
/// caller cannot tell whether the phone belongs to an account.
/// </summary>
public sealed class OtpLoginChallenge : ITenantEntity
{
    /// <summary>The challenge id handed to the caller. Random, so unguessable.</summary>
    public Guid Id { get; set; }

    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The user resolved when the challenge was issued; <see langword="null"/> for a decoy.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The normalized phone number the throttles and the failure cap are keyed on.</summary>
    public string NormalizedPhoneNumber { get; set; } = string.Empty;

    /// <summary>Hex HMAC-SHA256 of the challenge id and the code under <c>OtpLoginOptions.CodeHashKey</c>.</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>When the challenge was issued.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the code stops being accepted.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Guesses made against this challenge. Counted before the comparison, never after.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Set when the code was used or the challenge was superseded by a newer one. A closed
    /// challenge accepts nothing.</summary>
    public DateTimeOffset? ClosedAt { get; set; }
}
