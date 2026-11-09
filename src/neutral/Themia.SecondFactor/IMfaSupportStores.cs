namespace Themia.SecondFactor;

/// <summary>Recovery-code storage. Codes are keyed HMACs bound to the user (see <see cref="RecoveryCodeHasher"/>).</summary>
public interface IMfaRecoveryCodeStore
{
    /// <summary>Replaces the user's unused codes with <paramref name="codeHashes"/> in one transaction.</summary>
    Task ReplaceAsync(string? tenantId, string userId, IReadOnlyCollection<string> codeHashes, string hashScheme, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Marks one code used. <see langword="false"/> when it does not exist or was already used.</summary>
    Task<bool> TryConsumeAsync(string? tenantId, string userId, string codeHash, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>How many unused codes the user has left.</summary>
    Task<int> CountUnusedAsync(string? tenantId, string userId, CancellationToken cancellationToken = default);
}

/// <summary>The failed-attempt counter result.</summary>
/// <param name="FailedAttempts">Consecutive failures including this one.</param>
/// <param name="LockedUntil">When the lock ends, or <see langword="null"/> when not locked.</param>
public sealed record MfaLockState(int FailedAttempts, DateTimeOffset? LockedUntil)
{
    /// <summary>Whether the user is locked at <paramref name="now"/>.</summary>
    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;
}

/// <summary>Per-user state: a brute-force counter shared across TOTP and recovery codes, and the spare-method nag start.</summary>
public interface IMfaUserStateStore
{
    /// <summary>Reads the lock state without changing it.</summary>
    Task<MfaLockState> GetLockStateAsync(string? tenantId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Atomically counts one failure (across all kinds) and locks at <paramref name="maxFailures"/>.</summary>
    Task<MfaLockState> RecordFailureAsync(string? tenantId, string userId, int maxFailures, TimeSpan lockFor, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Clears the counter after a successful verification.</summary>
    Task ResetFailuresAsync(string? tenantId, string userId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>When the spare-method prompt first appeared, or <see langword="null"/>.</summary>
    Task<DateTimeOffset?> GetSpareNagStartedAtAsync(string? tenantId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Records the first prompt time; later calls keep the original value.</summary>
    Task StartSpareNagAsync(string? tenantId, string userId, DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>Why a WebAuthn ceremony was opened.</summary>
public enum MfaCeremonyPurpose
{
    /// <summary>Registering a passkey. Always bound to a user.</summary>
    Register,

    /// <summary>Confirming a pending TOTP enrolment. Always bound to a user.</summary>
    EnrolTotp,

    /// <summary>Signing in with a passkey.</summary>
    Authenticate,
}

/// <summary>A consumed ceremony.</summary>
/// <param name="OptionsJson">The options stored at begin.</param>
/// <param name="UserId">The user the ceremony was opened for, or <see langword="null"/> for a discoverable sign-in.</param>
public sealed record MfaCeremony(string OptionsJson, string? UserId);

/// <summary>Single-use WebAuthn ceremony state, bound to user and purpose.</summary>
public interface IMfaCeremonyStore
{
    /// <summary>Stores ceremony options for <paramref name="ttl"/>. Register and EnrolTotp require a user.</summary>
    Task StoreAsync(string challengeId, string optionsJson, string? tenantId, string? userId, MfaCeremonyPurpose purpose, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically consumes the ceremony. <see langword="null"/> when unknown, expired, already consumed, opened for a
    /// different tenant or user, or opened for a different purpose.
    /// </summary>
    Task<MfaCeremony?> TryConsumeAsync(string challengeId, string? tenantId, string? userId, MfaCeremonyPurpose purpose, CancellationToken cancellationToken = default);

    /// <summary>Deletes expired ceremonies (unauthenticated begin calls would otherwise grow the table). Returns the count.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default);
}

/// <summary>One audit entry. Never carries secrets or contact details.</summary>
/// <param name="TenantId">The target's tenant.</param>
/// <param name="TargetUserId">The user the action applied to.</param>
/// <param name="Actor">The acting user id, or <c>db:&lt;session_user&gt;</c> for break-glass.</param>
/// <param name="Action">A stable action name ("method.added", "admin.reset").</param>
/// <param name="MethodKind">The method kind involved, when any.</param>
/// <param name="Reason">A free-text reason, required for reset and break-glass.</param>
public sealed record MfaAuditEntry(string? TenantId, string TargetUserId, string Actor, string Action, MfaMethodKind? MethodKind, string? Reason);

/// <summary>Append-only audit log.</summary>
public interface IMfaAuditLog
{
    /// <summary>Appends one entry.</summary>
    Task AppendAsync(MfaAuditEntry entry, DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>Wipes every second-factor credential and pending enrolment of one user, and audits it, atomically.</summary>
public interface IMfaResetStore
{
    /// <summary>
    /// In one transaction, under the same per-user locks the method and recovery-code stores take: deletes the user's methods, unused
    /// recovery codes, WebAuthn and pending-TOTP ceremonies and failure counters, and appends one audit row. The audit row is part of
    /// the transaction, so a reset that cannot be audited is not applied.
    /// </summary>
    /// <param name="tenantId">The target's tenant, or <see langword="null"/> for a platform user.</param>
    /// <param name="userId">The target user.</param>
    /// <param name="actor">The acting user id.</param>
    /// <param name="action">The audit action name.</param>
    /// <param name="reason">A free-text reason. Required, and never logged: do not put personal data in it.</param>
    /// <param name="now">The audit timestamp.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>How many methods were removed.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/>, <paramref name="userId"/> or <paramref name="actor"/> is blank.</exception>
    Task<int> ResetAsync(string? tenantId, string userId, string actor, string action, string reason, DateTimeOffset now, CancellationToken cancellationToken = default);
}
