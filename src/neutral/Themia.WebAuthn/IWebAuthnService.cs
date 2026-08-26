using Fido2NetLib;

namespace Themia.WebAuthn;

/// <summary>
/// The four WebAuthn ceremonies, with the challenge held single-use and the signature counter checked.
/// </summary>
/// <remarks>
/// Storing credentials is <b>not</b> included: the public key, its counter and the user it belongs to
/// live in your users table, as the TOTP secret does for <c>Themia.Totp</c>. This package holds only
/// the in-flight ceremony.
/// </remarks>
public interface IWebAuthnService
{
    /// <summary>Begins registration and stores the ceremony for <see cref="CompleteRegistrationAsync"/>.</summary>
    /// <param name="userId">Opaque, stable, random user handle. <b>Not an email or username</b> — it is stored on the authenticator and is exposed.</param>
    /// <param name="userName">The account identifier shown in the authenticator's picker.</param>
    /// <param name="displayName">The human-readable name shown alongside it.</param>
    /// <param name="existingCredentialIds">Credentials this user already has, so an authenticator does not enrol twice.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Options to hand to <c>navigator.credentials.create()</c>.</returns>
    ValueTask<CredentialCreateOptions> BeginRegistrationAsync(
        byte[] userId,
        string userName,
        string displayName,
        IReadOnlyList<byte[]> existingCredentialIds,
        CancellationToken ct = default);

    /// <summary>Verifies a registration response against the ceremony it belongs to, consuming it.</summary>
    /// <param name="response">The browser's response.</param>
    /// <param name="isCredentialIdUnique">Confirms the credential id is not already registered to anyone.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The credential to store, or why it was refused.</returns>
    ValueTask<WebAuthnRegistration> CompleteRegistrationAsync(
        AuthenticatorAttestationRawResponse response,
        Func<byte[], CancellationToken, ValueTask<bool>> isCredentialIdUnique,
        CancellationToken ct = default);

    /// <summary>Begins authentication and stores the ceremony for <see cref="CompleteAuthenticationAsync"/>.</summary>
    /// <param name="allowedCredentialIds">
    /// Credentials to allow. Pass an <b>empty</b> list for passkey sign-in, where the authenticator
    /// offers the account and the user never types an identifier.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>Options to hand to <c>navigator.credentials.get()</c>.</returns>
    ValueTask<AssertionOptions> BeginAuthenticationAsync(
        IReadOnlyList<byte[]> allowedCredentialIds,
        CancellationToken ct = default);

    /// <summary>Verifies an assertion, consuming the ceremony and checking the signature counter.</summary>
    /// <param name="response">The browser's response.</param>
    /// <param name="storedCredential">The credential as your store holds it.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>
    /// The outcome and the counter to persist. A <see cref="WebAuthnOutcome.SignCounterRegressed"/>
    /// result means the assertion was cryptographically valid and still must not be trusted.
    /// </returns>
    ValueTask<WebAuthnAuthentication> CompleteAuthenticationAsync(
        AuthenticatorAssertionRawResponse response,
        StoredWebAuthnCredential storedCredential,
        CancellationToken ct = default);
}
