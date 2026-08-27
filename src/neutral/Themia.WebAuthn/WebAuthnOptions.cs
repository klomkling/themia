namespace Themia.WebAuthn;

/// <summary>Relying-party identity and ceremony settings.</summary>
public sealed class WebAuthnOptions
{
    /// <summary>The relying party id — your registrable domain, e.g. <c>example.com</c>. No scheme, no port.</summary>
    /// <remarks>
    /// A credential is bound to this value and cannot be used from any other. Changing it after users
    /// have registered invalidates every credential, so it is a decision, not a setting.
    /// </remarks>
    public string ServerDomain { get; set; } = string.Empty;

    /// <summary>The name shown to the user during a ceremony.</summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>Every origin allowed to run a ceremony, e.g. <c>https://example.com</c>.</summary>
    public HashSet<string> Origins { get; set; } = new(StringComparer.Ordinal);

    /// <summary>How long a ceremony may stay open. Defaults to five minutes.</summary>
    public TimeSpan ChallengeTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether to require a discoverable credential — what makes a credential a <b>passkey</b> and what
    /// allows sign-in without the user typing an identifier first. Defaults to <see langword="true"/>.
    /// </summary>
    public bool RequireResidentKey { get; set; } = true;
}
