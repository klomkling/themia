using Fido2NetLib.Objects;

namespace Themia.WebAuthn;

/// <summary>The result of completing a registration ceremony.</summary>
/// <param name="Outcome">Why it succeeded or failed.</param>
/// <param name="CredentialId">The new credential's id, to be stored against the user.</param>
/// <param name="PublicKey">The COSE public key, to be stored against the credential.</param>
/// <param name="SignCount">The counter to record as the starting value.</param>
/// <param name="AaGuid">The authenticator model identifier, or <see cref="Guid.Empty"/>.</param>
/// <param name="IsBackedUp">
/// Whether the authenticator reports this credential as synced to a provider's backup. A credential
/// that is not backed up dies with the device, which is worth knowing before it is a user's only one.
/// </param>
public sealed record WebAuthnRegistration(
    WebAuthnOutcome Outcome,
    byte[]? CredentialId,
    byte[]? PublicKey,
    uint SignCount,
    Guid AaGuid,
    bool IsBackedUp)
{
    /// <summary>Whether the ceremony completed.</summary>
    public bool Succeeded => Outcome == WebAuthnOutcome.Valid;
}

/// <summary>The result of completing an authentication ceremony.</summary>
/// <param name="Outcome">Why it succeeded or failed.</param>
/// <param name="SignCount">
/// The counter from this assertion. <b>Persist it</b> — the clone check compares the next assertion
/// against it, and a caller that never updates it disables the check without any signal.
/// </param>
/// <param name="UserHandle">The user handle the authenticator returned, when it supplied one.</param>
public sealed record WebAuthnAuthentication(WebAuthnOutcome Outcome, uint SignCount, byte[]? UserHandle)
{
    /// <summary>Whether the assertion verified and its counter was consistent.</summary>
    public bool Succeeded => Outcome == WebAuthnOutcome.Valid;
}

/// <summary>The credential a relying party stored at registration, needed to verify an assertion.</summary>
/// <param name="CredentialId">The credential id.</param>
/// <param name="PublicKey">The COSE public key.</param>
/// <param name="SignCount">The counter recorded at the last successful assertion.</param>
/// <param name="UserHandle">The user handle this credential belongs to.</param>
public sealed record StoredWebAuthnCredential(
    byte[] CredentialId,
    byte[] PublicKey,
    uint SignCount,
    byte[] UserHandle);
