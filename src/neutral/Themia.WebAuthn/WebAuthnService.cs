using System.Buffers.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.Options;

namespace Themia.WebAuthn;

/// <inheritdoc cref="IWebAuthnService" />
public sealed class WebAuthnService : IWebAuthnService
{
    private readonly IFido2 _fido2;
    private readonly IWebAuthnChallengeStore _challenges;
    private readonly WebAuthnOptions _options;

    /// <summary>Initializes a new instance of the <see cref="WebAuthnService"/> class.</summary>
    /// <param name="fido2">The underlying ceremony implementation.</param>
    /// <param name="challenges">Holds the in-flight ceremony. Required — see <see cref="IWebAuthnChallengeStore"/>.</param>
    /// <param name="options">Relying-party identity and ceremony settings.</param>
    public WebAuthnService(IFido2 fido2, IWebAuthnChallengeStore challenges, IOptions<WebAuthnOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _fido2 = fido2 ?? throw new ArgumentNullException(nameof(fido2));
        _challenges = challenges ?? throw new ArgumentNullException(nameof(challenges));
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async ValueTask<CredentialCreateOptions> BeginRegistrationAsync(
        byte[] userId,
        string userName,
        string displayName,
        IReadOnlyList<byte[]> existingCredentialIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(existingCredentialIds);

        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = userId, Name = userName, DisplayName = displayName },
            ExcludeCredentials = [.. existingCredentialIds.Select(id => new PublicKeyCredentialDescriptor(id))],
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = _options.RequireResidentKey ? ResidentKeyRequirement.Required : ResidentKeyRequirement.Discouraged,
                UserVerification = UserVerificationRequirement.Required,
            },
            // No attestation: a synced passkey cannot be meaningfully attested anyway, and requesting
            // it would pull the metadata service - a network call - into every registration.
            AttestationPreference = AttestationConveyancePreference.None,
        });

        await StoreCeremonyAsync(options.Challenge, options.ToJson(), ct).ConfigureAwait(false);
        return options;
    }

    /// <inheritdoc />
    public async ValueTask<WebAuthnRegistration> CompleteRegistrationAsync(
        AuthenticatorAttestationRawResponse response,
        Func<byte[], CancellationToken, ValueTask<bool>> isCredentialIdUnique,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(isCredentialIdUnique);

        var stored = await ConsumeCeremonyAsync(response.Response?.ClientDataJson, ct).ConfigureAwait(false);
        if (stored is null)
        {
            return new WebAuthnRegistration(WebAuthnOutcome.ChallengeNotFound, null, null, 0, Guid.Empty, false);
        }

        try
        {
            var result = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = CredentialCreateOptions.FromJson(stored),
                IsCredentialIdUniqueToUserCallback = async (args, innerCt) =>
                    await isCredentialIdUnique(args.CredentialId, innerCt).ConfigureAwait(false),
            }, ct).ConfigureAwait(false);

            return new WebAuthnRegistration(
                WebAuthnOutcome.Valid,
                result.Id,
                result.PublicKey,
                result.SignCount,
                result.AaGuid,
                result.IsBackedUp);
        }
        catch (Fido2VerificationException)
        {
            return new WebAuthnRegistration(WebAuthnOutcome.VerificationFailed, null, null, 0, Guid.Empty, false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<AssertionOptions> BeginAuthenticationAsync(
        IReadOnlyList<byte[]> allowedCredentialIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(allowedCredentialIds);

        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [.. allowedCredentialIds.Select(id => new PublicKeyCredentialDescriptor(id))],
            UserVerification = UserVerificationRequirement.Required,
        });

        await StoreCeremonyAsync(options.Challenge, options.ToJson(), ct).ConfigureAwait(false);
        return options;
    }

    /// <inheritdoc />
    public async ValueTask<WebAuthnAuthentication> CompleteAuthenticationAsync(
        AuthenticatorAssertionRawResponse response,
        StoredWebAuthnCredential storedCredential,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(storedCredential);

        var stored = await ConsumeCeremonyAsync(response.Response?.ClientDataJson, ct).ConfigureAwait(false);
        if (stored is null)
        {
            return new WebAuthnAuthentication(WebAuthnOutcome.ChallengeNotFound, 0, null);
        }

        VerifyAssertionResult result;
        try
        {
            result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = AssertionOptions.FromJson(stored),
                StoredPublicKey = storedCredential.PublicKey,
                StoredSignatureCounter = storedCredential.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(args.UserHandle.SequenceEqual(storedCredential.UserHandle)),
            }, ct).ConfigureAwait(false);
        }
        catch (Fido2VerificationException)
        {
            return new WebAuthnAuthentication(WebAuthnOutcome.VerificationFailed, 0, null);
        }

        // Verified cryptographically, and still not necessarily trustworthy: a counter that did not
        // move forward means another authenticator is answering for this credential.
        if (!SignCounterPolicy.IsAcceptable(storedCredential.SignCount, result.SignCount))
        {
            return new WebAuthnAuthentication(WebAuthnOutcome.SignCounterRegressed, result.SignCount, null);
        }

        return new WebAuthnAuthentication(WebAuthnOutcome.Valid, result.SignCount, response.Response?.UserHandle);
    }

    private ValueTask StoreCeremonyAsync(byte[] challenge, string optionsJson, CancellationToken ct)
        => _challenges.StoreAsync(Base64Url.EncodeToString(challenge), optionsJson, _options.ChallengeTimeout, ct);

    /// <summary>
    /// Reads the challenge back out of the client data and consumes the ceremony it belongs to.
    /// </summary>
    /// <remarks>
    /// The challenge is taken from the RESPONSE, so a response carrying a challenge we never issued —
    /// or one already used — finds nothing and is refused before any verification runs.
    /// </remarks>
    private async ValueTask<string?> ConsumeCeremonyAsync(byte[]? clientDataJson, CancellationToken ct)
    {
        if (clientDataJson is null || clientDataJson.Length == 0)
        {
            return null;
        }

        string? challenge;
        try
        {
            using var document = JsonDocument.Parse(clientDataJson);
            challenge = document.RootElement.TryGetProperty("challenge", out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }

        return string.IsNullOrEmpty(challenge)
            ? null
            : await _challenges.TryConsumeAsync(challenge, ct).ConfigureAwait(false);
    }
}
