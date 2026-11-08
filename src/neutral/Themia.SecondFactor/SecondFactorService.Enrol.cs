using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Fido2NetLib;

using Themia.Totp;
using Themia.WebAuthn;

namespace Themia.SecondFactor;

internal sealed partial class SecondFactorService
{
    private sealed record PendingTotp(string SecretProtected, string Scheme);

    public async Task<SecondFactorValue<TotpEnrolment>> BeginTotpEnrolAsync(
        MfaSubject subject, MfaPrimary primary, MfaProof? proof, CancellationToken cancellationToken = default)
    {
        var contact = await contacts.ResolveAsync(subject, cancellationToken).ConfigureAwait(false);
        var gate = await GateEnrolAsync(subject, primary, MfaMethodKind.Totp, proof, cancellationToken).ConfigureAwait(false);
        if (gate != SecondFactorOutcome.Succeeded)
        {
            return new SecondFactorValue<TotpEnrolment>(gate);
        }

        // The app shows this label; the user id is a GUID nobody can recognise, so a missing label is refused.
        if (contact is null || string.IsNullOrWhiteSpace(contact.AccountLabel) || contact.AccountLabel == subject.UserId)
        {
            return new SecondFactorValue<TotpEnrolment>(SecondFactorOutcome.NotAllowed);
        }

        var secret = totp.GenerateSecret();
        var uri = totp.CreateProvisioningUri(secret, options.Issuer, contact.AccountLabel);
        var enrolmentId = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var pending = JsonSerializer.Serialize(new PendingTotp(protector.Protect(secret), protector.Scheme));
        await ceremonies.StoreAsync(
            enrolmentId, pending, subject.TenantId, subject.UserId, MfaCeremonyPurpose.EnrolTotp, options.CeremonyTtl, cancellationToken).ConfigureAwait(false);

        return new SecondFactorValue<TotpEnrolment>(SecondFactorOutcome.Succeeded, new TotpEnrolment(enrolmentId, secret, uri, QrSvg.Render(uri)));
    }

    public async Task<SecondFactorResult> ConfirmTotpEnrolAsync(
        MfaSubject subject, string enrolmentId, string code, string label, MfaProof? proof, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enrolmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var attempt = await CountAttemptAsync(subject, cancellationToken).ConfigureAwait(false);
        if (attempt.IsLocked(time.GetUtcNow()))
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.Locked, attempt.LockedUntil);
        }

        // Begin may have run with no proof because the user held nothing; if they have enrolled something since, a
        // primary-only caller must not be able to spend that old pending enrolment to add a second factor unproven.
        var heldNow = await methods.ListAsync(subject.TenantId, subject.UserId, cancellationToken).ConfigureAwait(false);
        if (!MayAdd(heldNow, MfaMethodKind.Totp, proof))
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.StepUpRequired);
        }

        var ceremony = await ceremonies.TryConsumeAsync(enrolmentId, subject.TenantId, subject.UserId, MfaCeremonyPurpose.EnrolTotp, cancellationToken).ConfigureAwait(false);
        var pending = ceremony is null ? null : JsonSerializer.Deserialize<PendingTotp>(ceremony.OptionsJson);
        var secret = pending is null ? null : protector.Unprotect(pending.Scheme, pending.SecretProtected);
        if (pending is null || secret is null)
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.CeremonyExpired);
        }

        // The pending secret has no method row yet, so the replay store (which only advances rows) allows this id; the
        // matched step is stored with the new row instead, which is what stops the confirming code signing in.
        var verification = await totp.VerifyAsync(PendingSecretId(enrolmentId), secret, code, cancellationToken).ConfigureAwait(false);
        if (!verification.Succeeded)
        {
            // A typo must not destroy the pending enrolment: put it back for the rest of its life.
            await ceremonies.StoreAsync(
                enrolmentId, ceremony!.OptionsJson, subject.TenantId, subject.UserId, MfaCeremonyPurpose.EnrolTotp, options.CeremonyTtl, cancellationToken).ConfigureAwait(false);
            return SecondFactorResult.Failed(SecondFactorOutcome.Incorrect);
        }

        var added = await methods.AddAsync(
            new NewMfaMethod
            {
                TenantId = subject.TenantId,
                UserId = subject.UserId,
                Kind = MfaMethodKind.Totp,
                Label = label,
                SecretProtected = pending.SecretProtected,
                ProtectionScheme = pending.Scheme,
                LastUsedStep = verification.MatchedStep,
                EnrolledAt = time.GetUtcNow(),
            },
            cancellationToken).ConfigureAwait(false);
        if (added != MfaEnrolOutcome.Added)
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.NotAllowed);
        }

        await ResetAttemptsAsync(subject, cancellationToken).ConfigureAwait(false);
        await AuditAsync(subject, "method.added", MfaMethodKind.Totp, cancellationToken).ConfigureAwait(false);
        return Success(MfaMethodKind.Totp);
    }

    public async Task<SecondFactorValue<PasskeyCeremony>> BeginPasskeyEnrolAsync(
        MfaSubject subject, MfaPrimary primary, MfaProof? proof, CancellationToken cancellationToken = default)
    {
        var gate = await GateEnrolAsync(subject, primary, MfaMethodKind.Passkey, proof, cancellationToken).ConfigureAwait(false);
        var contact = gate == SecondFactorOutcome.Succeeded
            ? await contacts.ResolveAsync(subject, cancellationToken).ConfigureAwait(false)
            : null;
        if (gate != SecondFactorOutcome.Succeeded || contact is null || string.IsNullOrWhiteSpace(contact.AccountLabel))
        {
            return new SecondFactorValue<PasskeyCeremony>(gate == SecondFactorOutcome.Succeeded ? SecondFactorOutcome.NotAllowed : gate);
        }

        var existing = await methods.ListPasskeyCredentialIdsAsync(subject.TenantId, subject.UserId, cancellationToken).ConfigureAwait(false);
        using var ceremony = PasskeyCeremonyContext.Begin(subject.TenantId, subject.UserId, MfaCeremonyPurpose.Register);
        var creation = await webAuthn.BeginRegistrationAsync(
            UserHandle(subject), contact.AccountLabel, contact.AccountLabel, existing, cancellationToken).ConfigureAwait(false);
        return new SecondFactorValue<PasskeyCeremony>(SecondFactorOutcome.Succeeded, new PasskeyCeremony(creation.ToJson()));
    }

    public async Task<SecondFactorResult> CompletePasskeyEnrolAsync(
        MfaSubject subject, AuthenticatorAttestationRawResponse response, string label, MfaProof? proof, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var heldNow = await methods.ListAsync(subject.TenantId, subject.UserId, cancellationToken).ConfigureAwait(false);
        if (!MayAdd(heldNow, MfaMethodKind.Passkey, proof))
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.StepUpRequired);
        }

        using var ceremony = PasskeyCeremonyContext.Begin(subject.TenantId, subject.UserId, MfaCeremonyPurpose.Register);
        var registration = await webAuthn.CompleteRegistrationAsync(
            response,
            async (credentialId, ct) => await methods.FindPasskeyAsync(credentialId, ct).ConfigureAwait(false) is null,
            cancellationToken).ConfigureAwait(false);
        if (!registration.Succeeded)
        {
            return SecondFactorResult.Failed(
                registration.Outcome == WebAuthnOutcome.ChallengeNotFound ? SecondFactorOutcome.CeremonyExpired : SecondFactorOutcome.Incorrect);
        }

        var added = await methods.AddAsync(
            new NewMfaMethod
            {
                TenantId = subject.TenantId,
                UserId = subject.UserId,
                Kind = MfaMethodKind.Passkey,
                Label = label,
                CredentialId = registration.CredentialId,
                PublicKey = registration.PublicKey,
                UserHandle = UserHandle(subject),
                SignCount = registration.SignCount,
                EnrolledAt = time.GetUtcNow(),
            },
            cancellationToken).ConfigureAwait(false);
        if (added == MfaEnrolOutcome.DuplicateCredential)
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.DuplicateCredential);
        }

        await AuditAsync(subject, "method.added", MfaMethodKind.Passkey, cancellationToken).ConfigureAwait(false);
        return Success(MfaMethodKind.Passkey);
    }

    public async Task<SecondFactorResult> EnrolOtpAsync(
        MfaSubject subject, MfaPrimary primary, MfaMethodKind kind, MfaProof? proof, CancellationToken cancellationToken = default)
    {
        if (kind is not (MfaMethodKind.EmailOtp or MfaMethodKind.SmsOtp))
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.NotAvailable);
        }

        var gate = await GateEnrolAsync(subject, primary, kind, proof, cancellationToken).ConfigureAwait(false);
        if (gate != SecondFactorOutcome.Succeeded)
        {
            return SecondFactorResult.Failed(gate);
        }

        var contact = await contacts.ResolveAsync(subject, cancellationToken).ConfigureAwait(false);
        var destination = kind == MfaMethodKind.EmailOtp ? contact?.Email : contact?.Phone;
        var held = await methods.ListAsync(subject.TenantId, subject.UserId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(destination) || held.Any(method => method.IsUsable && method.Kind == kind))
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.NotAllowed);
        }

        await methods.AddAsync(
            new NewMfaMethod
            {
                TenantId = subject.TenantId,
                UserId = subject.UserId,
                Kind = kind,
                Label = kind == MfaMethodKind.EmailOtp ? "Email" : "SMS",
                EnrolledAt = time.GetUtcNow(),
            },
            cancellationToken).ConfigureAwait(false);
        await AuditAsync(subject, "method.added", kind, cancellationToken).ConfigureAwait(false);
        return Success(kind);
    }

    public async Task<SecondFactorResult> RemoveMethodAsync(
        MfaSubject subject, MfaPrimary primary, Guid methodId, MfaProof? proof, CancellationToken cancellationToken = default)
    {
        if (!IsFreshStrong(proof))
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.StepUpRequired);
        }

        var held = await methods.ListAsync(subject.TenantId, subject.UserId, cancellationToken).ConfigureAwait(false);
        var target = held.FirstOrDefault(method => method.Id == methodId);
        if (target is null)
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.NotAvailable);
        }

        // Never leave the user worse off than policy allows: a required user must keep a method, and removing one must
        // not newly push them under the minimum.
        var nagStartedAt = await userState.GetSpareNagStartedAtAsync(subject.TenantId, subject.UserId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var before = policy.Evaluate(subject, primary, held, nagStartedAt, now);
        var after = policy.Evaluate(subject, primary, [.. held.Where(method => method.Id != methodId)], nagStartedAt, now);
        if (after.Level == MfaRequirementLevel.Enrol || (after.SpareNeeded && !before.SpareNeeded))
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.NotAllowed);
        }

        if (!await methods.RemoveAsync(subject.TenantId, subject.UserId, methodId, cancellationToken).ConfigureAwait(false))
        {
            return SecondFactorResult.Failed(SecondFactorOutcome.NotAvailable);
        }

        await AuditAsync(subject, "method.removed", target.Kind, cancellationToken).ConfigureAwait(false);
        return new SecondFactorResult { Outcome = SecondFactorOutcome.Succeeded, Kind = target.Kind };
    }

    public async Task<RecoveryCodesResult> GenerateRecoveryCodesAsync(
        MfaSubject subject, MfaProof? proof, CancellationToken cancellationToken = default)
    {
        if (!IsFreshStrong(proof))
        {
            return new RecoveryCodesResult(SecondFactorOutcome.StepUpRequired, []);
        }

        var held = await methods.ListAsync(subject.TenantId, subject.UserId, cancellationToken).ConfigureAwait(false);
        if (!held.Any(method => method.IsUsable))
        {
            return new RecoveryCodesResult(SecondFactorOutcome.NotAllowed, []);
        }

        var codes = RecoveryCodeGenerator.Generate(options.RecoveryCodeCount);
        var hashes = codes.Select(code => hasher.Hash(subject.UserId, code)).ToArray();
        await recoveryCodes.ReplaceAsync(subject.TenantId, subject.UserId, hashes, RecoveryCodeHasher.Scheme, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await AuditAsync(subject, "recovery.generated", null, cancellationToken).ConfigureAwait(false);
        return new RecoveryCodesResult(SecondFactorOutcome.Succeeded, codes);
    }

    // Offered for this sign-in, and allowed by the add rules.
    private async Task<SecondFactorOutcome> GateEnrolAsync(
        MfaSubject subject, MfaPrimary primary, MfaMethodKind kind, MfaProof? proof, CancellationToken cancellationToken)
    {
        var requirement = await EvaluateAsync(subject, primary, cancellationToken).ConfigureAwait(false);
        if (requirement.Level == MfaRequirementLevel.Denied || !requirement.EnrolKinds.Contains(kind))
        {
            return SecondFactorOutcome.NotAvailable;
        }

        var held = await methods.ListAsync(subject.TenantId, subject.UserId, cancellationToken).ConfigureAwait(false);
        return MayAdd(held, kind, proof) ? SecondFactorOutcome.Succeeded : SecondFactorOutcome.StepUpRequired;
    }

    private static string PendingSecretId(string enrolmentId) => $"pending:{enrolmentId}";

    // Stable per user and opaque (not PII); the same handle for all of a user's passkeys, as WebAuthn recommends.
    private static byte[] UserHandle(MfaSubject subject) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{subject.TenantId}\n{subject.UserId}"));
}
