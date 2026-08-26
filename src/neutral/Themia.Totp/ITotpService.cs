namespace Themia.Totp;

/// <summary>
/// Generates TOTP secrets and codes (RFC 6238) and verifies submitted codes with a single-use guard.
/// </summary>
/// <remarks>
/// <b>This package never stores a secret.</b> Generation and the provisioning URI belong here;
/// persisting the secret and encrypting it at rest stay with the caller, as the key material does for
/// <c>Themia.AspNetCore.DataProtection</c>. A secret is credential material and its storage is a
/// decision only the consuming application can make.
/// </remarks>
public interface ITotpService
{
    /// <summary>Generates a new cryptographically random shared secret, base32-encoded.</summary>
    /// <param name="byteLength">Secret length in bytes. Defaults to 20, the RFC 4226 recommendation.</param>
    /// <returns>The secret, base32-encoded for an authenticator application.</returns>
    string GenerateSecret(int byteLength = 20);

    /// <summary>Builds the <c>otpauth://totp/</c> URI an authenticator application scans.</summary>
    /// <param name="secret">The base32 shared secret.</param>
    /// <param name="issuer">The service name shown in the app, e.g. your product's name.</param>
    /// <param name="accountName">The account shown under the issuer, e.g. the user's email.</param>
    /// <returns>The provisioning URI.</returns>
    Uri CreateProvisioningUri(string secret, string issuer, string accountName);

    /// <summary>Generates the code for the current instant. Exposed for testing and for a caller that sends codes itself.</summary>
    /// <param name="secret">The base32 shared secret.</param>
    /// <returns>The code, zero-padded to the configured digit count.</returns>
    string GenerateCode(string secret);

    /// <summary>
    /// Verifies a submitted code against the window, and consumes its step so it cannot be used twice.
    /// </summary>
    /// <param name="secretId">
    /// Opaque identifier for the credential — see <see cref="ITotpReplayStore.TryConsumeAsync"/>. Not
    /// the secret itself.
    /// </param>
    /// <param name="secret">The base32 shared secret.</param>
    /// <param name="code">The code the user submitted.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The outcome and the step that matched.</returns>
    ValueTask<TotpVerification> VerifyAsync(string secretId, string secret, string code, CancellationToken ct = default);
}
