using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Themia.Challenges;
using Themia.Totp;
using Themia.WebAuthn;

namespace Themia.SecondFactor;

internal sealed partial class SecondFactorService : ISecondFactorService
{
    private readonly IMfaMethodStore methods;
    private readonly IMfaRecoveryCodeStore recoveryCodes;
    private readonly IMfaUserStateStore userState;
    private readonly IMfaCeremonyStore ceremonies;
    private readonly IMfaAuditLog audit;
    private readonly IMfaPolicy policy;
    private readonly ITotpService totp;
    private readonly IWebAuthnService webAuthn;
    private readonly IChallengeService challenges;
    private readonly IReadOnlyDictionary<MfaMethodKind, IOtpDelivery> deliveries;
    private readonly ISecondFactorSecretProtector protector;
    private readonly ISecondFactorContactResolver contacts;
    private readonly RecoveryCodeHasher hasher;
    private readonly SecondFactorOptions options;
    private readonly TimeProvider time;
    private readonly ILogger<SecondFactorService> logger;

    public SecondFactorService(
        IMfaMethodStore methods,
        IMfaRecoveryCodeStore recoveryCodes,
        IMfaUserStateStore userState,
        IMfaCeremonyStore ceremonies,
        IMfaAuditLog audit,
        IMfaPolicy policy,
        ITotpService totp,
        IWebAuthnService webAuthn,
        IChallengeService challenges,
        IEnumerable<IOtpDelivery> deliveries,
        ISecondFactorSecretProtector protector,
        ISecondFactorContactResolver contacts,
        RecoveryCodeHasher hasher,
        IOptions<SecondFactorOptions> options,
        TimeProvider time,
        ILogger<SecondFactorService> logger)
    {
        this.methods = methods;
        this.recoveryCodes = recoveryCodes;
        this.userState = userState;
        this.ceremonies = ceremonies;
        this.audit = audit;
        this.policy = policy;
        this.totp = totp;
        this.webAuthn = webAuthn;
        this.challenges = challenges;
        this.deliveries = deliveries.ToDictionary(delivery => delivery.Kind);
        this.protector = protector;
        this.contacts = contacts;
        this.hasher = hasher;
        this.options = options.Value;
        this.time = time;
        this.logger = logger;
    }

    public Task<IReadOnlyList<MfaMethod>> ListMethodsAsync(MfaSubject subject, CancellationToken cancellationToken = default) =>
        methods.ListAsync(subject.TenantId, subject.UserId, cancellationToken);

    public async Task<MfaRequirement> EvaluateAsync(MfaSubject subject, MfaPrimary primary, CancellationToken cancellationToken = default)
    {
        var held = await methods.ListAsync(subject.TenantId, subject.UserId, cancellationToken).ConfigureAwait(false);
        var nagStartedAt = await userState.GetSpareNagStartedAtAsync(subject.TenantId, subject.UserId, cancellationToken).ConfigureAwait(false);
        var requirement = policy.Evaluate(subject, primary, held, nagStartedAt, time.GetUtcNow());
        if (requirement.SpareNagShouldStart)
        {
            await userState.StartSpareNagAsync(subject.TenantId, subject.UserId, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }

        return requirement;
    }

    // The proof must be strong and recent; the session it came from is the caller's to vouch for. A session opened with a
    // recovery code is not enough: whoever holds only a leaked code must not be able to remove methods or mint new codes.
    private bool IsFreshStrong(MfaProof? proof) => ProofFreshness.IsFreshStrong(proof, time.GetUtcNow(), options.StrongAuthMaxAge);

    private bool IsFresh(MfaProof? proof)
    {
        if (proof is null)
        {
            return false;
        }

        var age = time.GetUtcNow() - proof.AuthenticatedAt;
        return age <= options.StrongAuthMaxAge && age >= -ProofFreshness.ClockSkew;
    }

    // Who may add a method. The first method needs no proof (the caller is in the Enrol stage, with only the primary);
    // later ones need a fresh strong verification; a user who only holds weak methods may add a strong one after a fresh
    // weak verification (upgrade path only).
    private bool MayAdd(IReadOnlyCollection<MfaMethod> held, MfaMethodKind kindToAdd, MfaProof? proof)
    {
        var usable = held.Where(method => method.IsUsable).ToArray();
        if (usable.Length == 0 || IsFreshStrong(proof))
        {
            return true;
        }

        // A recovery-code session is for getting back in: it may enrol a strong method (a lost device is replaced) and nothing else.
        if (proof is { RecoveryCodeOnly: true })
        {
            return IsFresh(proof) && kindToAdd.Strength() == MfaStrength.Strong;
        }

        var holdsStrong = usable.Any(method => method.Kind.Strength() == MfaStrength.Strong);
        return !holdsStrong && IsFresh(proof) && kindToAdd.Strength() == MfaStrength.Strong;
    }

    private SecondFactorResult Success(MfaMethodKind? kind, bool usedRecoveryCode = false)
    {
        var now = time.GetUtcNow();
        var strength = usedRecoveryCode ? MfaStrength.Strong : kind!.Value.Strength();
        return new SecondFactorResult
        {
            Outcome = SecondFactorOutcome.Succeeded,
            Kind = kind,
            UsedRecoveryCode = usedRecoveryCode,
            Strength = strength,
            AuthenticatedAt = now,
            Amr = usedRecoveryCode ? AmrClaims.ForRecoveryCode() : AmrClaims.ForSecondFactor(kind!.Value),
        };
    }

    // The attempt is counted BEFORE the secret is checked, atomically, so a burst of parallel guesses cannot all read
    // "not locked". Passing MaxAttempts + 1 as the limit locks on the attempt after the last allowed one.
    private async Task<MfaLockState> CountAttemptAsync(MfaSubject subject, CancellationToken cancellationToken) =>
        await userState.RecordFailureAsync(
            subject.TenantId, subject.UserId, options.MaxAttempts + 1, options.LockDuration, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);

    private Task ResetAttemptsAsync(MfaSubject subject, CancellationToken cancellationToken) =>
        userState.ResetFailuresAsync(subject.TenantId, subject.UserId, time.GetUtcNow(), cancellationToken);

    private Task AuditAsync(MfaSubject subject, string action, MfaMethodKind? kind, CancellationToken cancellationToken) =>
        audit.AppendAsync(new MfaAuditEntry(subject.TenantId, subject.UserId, subject.UserId, action, kind, null), time.GetUtcNow(), cancellationToken);
}
