using Fido2NetLib;

using Microsoft.Extensions.Logging;

using Themia.Challenges;
using Themia.Totp;
using Themia.WebAuthn;

namespace Themia.SecondFactor;

internal sealed partial class SecondFactorService
{
    public async Task<SecondFactorResult> VerifyTotpAsync(
        MfaSubject subject, MfaPrimary primary, Guid methodId, string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var requirement = await EvaluateAsync(subject, primary, cancellationToken).ConfigureAwait(false);
        if (requirement.Level != MfaRequirementLevel.Verify
            || !requirement.UsableMethods.Any(method => method.Id == methodId && method.Kind == MfaMethodKind.Totp))
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.NotAvailable);
        }

        var attempt = await CountAttemptAsync(subject, cancellationToken).ConfigureAwait(false);
        if (attempt.IsLocked(time.GetUtcNow()))
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.Locked, attempt.LockedUntil);
        }

        var stored = await methods.FindTotpAsync(subject.TenantId, subject.UserId, methodId, cancellationToken).ConfigureAwait(false);
        var secret = stored is null ? null : protector.Unprotect(stored.ProtectionScheme, stored.SecretProtected);
        if (stored is null || secret is null)
        {
            // An unknown protection scheme fails closed: never accept, never guess.
            logger.LogWarning("TOTP method {MethodId} cannot be read (missing or unknown protection scheme).", methodId);
            return SecondFactorResult.Failed(SecondFactorOutcome.NotAvailable);
        }

        var verification = await totp.VerifyAsync(methodId.ToString("D"), secret, code, cancellationToken).ConfigureAwait(false);
        switch (verification.Outcome)
        {
            case TotpOutcome.Valid:
                await ResetAttemptsAsync(subject, cancellationToken).ConfigureAwait(false);
                return Success(MfaMethodKind.Totp);
            case TotpOutcome.Replayed:
                return SecondFactorResult.Failed(SecondFactorOutcome.Replayed);
            default:
                return SecondFactorResult.Failed(SecondFactorOutcome.Incorrect);
        }
    }

    public async Task<SecondFactorResult> VerifyRecoveryCodeAsync(
        MfaSubject subject, MfaPrimary primary, string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var requirement = await EvaluateAsync(subject, primary, cancellationToken).ConfigureAwait(false);
        if (requirement.Level != MfaRequirementLevel.Verify)
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.NotAvailable);
        }

        var attempt = await CountAttemptAsync(subject, cancellationToken).ConfigureAwait(false);
        if (attempt.IsLocked(time.GetUtcNow()))
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.Locked, attempt.LockedUntil);
        }

        var consumed = await recoveryCodes.TryConsumeAsync(
            subject.TenantId, subject.UserId, hasher.Hash(subject.UserId, code), time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (!consumed)
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.Incorrect);
        }

        await ResetAttemptsAsync(subject, cancellationToken).ConfigureAwait(false);
        await AuditAsync(subject, "recovery.used", null, cancellationToken).ConfigureAwait(false);
        return Success(null, usedRecoveryCode: true);
    }

    public async Task<OtpChallengeResult> BeginOtpAsync(
        MfaSubject subject, MfaPrimary primary, MfaMethodKind kind, CancellationToken cancellationToken = default)
    {
        var scope = await ResolveOtpScopeAsync(subject, primary, kind, cancellationToken).ConfigureAwait(false);
        var contact = scope is null ? null : await contacts.ResolveAsync(subject, cancellationToken).ConfigureAwait(false);
        var destination = kind == MfaMethodKind.EmailOtp ? contact?.Email : contact?.Phone;
        if (scope is null || string.IsNullOrWhiteSpace(destination))
        {
            return new OtpChallengeResult(SecondFactorOutcome.NotAvailable);
        }

        var issued = await challenges.IssueAsync(scope, cancellationToken).ConfigureAwait(false);
        if (!issued.Succeeded)
        {
            return new OtpChallengeResult(SecondFactorOutcome.CannotIssue);
        }

        try
        {
            await deliveries[kind].SendAsync(destination, issued.Secret!, cancellationToken).ConfigureAwait(false);
            return new OtpChallengeResult(SecondFactorOutcome.Succeeded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The destination is PII: log the kind only. A code that never reached the user must not burn their rate limit.
            logger.LogWarning(ex, "Delivering a {Kind} second-factor code failed.", kind);
            await challenges.RefundAsync(issued.ChallengeId!.Value, CancellationToken.None).ConfigureAwait(false);
            return new OtpChallengeResult(SecondFactorOutcome.CannotIssue);
        }
    }

    public async Task<SecondFactorResult> VerifyOtpAsync(
        MfaSubject subject, MfaPrimary primary, MfaMethodKind kind, string code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var scope = await ResolveOtpScopeAsync(subject, primary, kind, cancellationToken).ConfigureAwait(false);
        if (scope is null)
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.NotAvailable);
        }

        var verified = await challenges.VerifyAsync(scope, code, cancellationToken).ConfigureAwait(false);
        return verified.Outcome switch
        {
            ChallengeVerifyOutcome.Verified => Success(kind),
            ChallengeVerifyOutcome.RateLimited or ChallengeVerifyOutcome.AttemptsExhausted => SecondFactorResult.Failed(SecondFactorOutcome.Locked),
            _ => SecondFactorResult.Failed(SecondFactorOutcome.Incorrect),
        };
    }

    public async Task<SecondFactorValue<PasskeyCeremony>> BeginPasskeyAssertionAsync(
        MfaSubject subject, MfaPrimary primary, CancellationToken cancellationToken = default)
    {
        var requirement = await EvaluateAsync(subject, primary, cancellationToken).ConfigureAwait(false);
        var hasPasskey = requirement.Level == MfaRequirementLevel.Verify
            && requirement.UsableMethods.Any(method => method.Kind == MfaMethodKind.Passkey);
        if (!hasPasskey)
        {
            return new SecondFactorValue<PasskeyCeremony>(SecondFactorOutcome.NotAvailable);
        }

        var credentialIds = await methods.ListPasskeyCredentialIdsAsync(subject.TenantId, subject.UserId, cancellationToken).ConfigureAwait(false);
        using var ceremony = PasskeyCeremonyContext.Begin(subject.TenantId, subject.UserId, MfaCeremonyPurpose.Authenticate);
        var assertion = await webAuthn.BeginAuthenticationAsync(credentialIds, cancellationToken).ConfigureAwait(false);
        return new SecondFactorValue<PasskeyCeremony>(SecondFactorOutcome.Succeeded, new PasskeyCeremony(assertion.ToJson()));
    }

    public async Task<SecondFactorResult> VerifyPasskeyAsync(
        MfaSubject subject, MfaPrimary primary, AuthenticatorAssertionRawResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var requirement = await EvaluateAsync(subject, primary, cancellationToken).ConfigureAwait(false);
        var passkey = response.RawId is { Length: > 0 }
            ? await methods.FindPasskeyAsync(response.RawId, cancellationToken).ConfigureAwait(false)
            : null;

        // The credential must belong to THIS user and tenant and be usable for this sign-in; the credential id alone
        // identifies an authenticator, not an account.
        if (requirement.Level != MfaRequirementLevel.Verify
            || passkey is null
            || passkey.UserId != subject.UserId
            || passkey.TenantId != subject.TenantId
            || !requirement.UsableMethods.Any(method => method.Id == passkey.MethodId))
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.NotAvailable);
        }

        using var ceremony = PasskeyCeremonyContext.Begin(subject.TenantId, subject.UserId, MfaCeremonyPurpose.Authenticate);
        var stored = new StoredWebAuthnCredential(passkey.CredentialId, passkey.PublicKey, checked((uint)passkey.SignCount), passkey.UserHandle);
        var authentication = await webAuthn.CompleteAuthenticationAsync(response, stored, cancellationToken).ConfigureAwait(false);
        switch (authentication.Outcome)
        {
            case WebAuthnOutcome.Valid:
                break;
            case WebAuthnOutcome.ChallengeNotFound:
                return SecondFactorResult.Failed(SecondFactorOutcome.CeremonyExpired);
            case WebAuthnOutcome.SignCounterRegressed:
                return SecondFactorResult.Failed(SecondFactorOutcome.CounterRegressed);
            default:
                return SecondFactorResult.Failed(SecondFactorOutcome.Incorrect);
        }

        var recorded = await methods.TryUpdateSignCountAsync(
            subject.TenantId, subject.UserId, passkey.MethodId, authentication.SignCount, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return recorded ? Success(MfaMethodKind.Passkey) : SecondFactorResult.Failed(SecondFactorOutcome.CounterRegressed);
    }

    // The scope is returned only when this kind is usable for this user and sign-in.
    private async Task<ChallengeScope?> ResolveOtpScopeAsync(
        MfaSubject subject, MfaPrimary primary, MfaMethodKind kind, CancellationToken cancellationToken)
    {
        if (kind is not (MfaMethodKind.EmailOtp or MfaMethodKind.SmsOtp) || !deliveries.ContainsKey(kind))
        {
            return null;
        }

        var requirement = await EvaluateAsync(subject, primary, cancellationToken).ConfigureAwait(false);
        if (requirement.Level != MfaRequirementLevel.Verify || !requirement.UsableMethods.Any(method => method.Kind == kind))
        {
            return null;
        }

        var purpose = kind == MfaMethodKind.EmailOtp ? SecondFactorOptions.EmailOtpPurpose : SecondFactorOptions.SmsOtpPurpose;
        return new ChallengeScope(subject.UserId, purpose, subject.TenantId);
    }
}
