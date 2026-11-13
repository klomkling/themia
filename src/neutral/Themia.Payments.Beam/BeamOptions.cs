namespace Themia.Payments.Beam;

/// <summary>Credentials and environment for the Beam adapter.</summary>
public sealed class BeamOptions
{
    /// <summary>The configuration section these bind from.</summary>
    public const string SectionName = "Payments:Beam";

    /// <summary>The merchant id, used as the Basic-auth user.</summary>
    public string MerchantId { get; set; } = "";

    /// <summary>The merchant API key, used as the Basic-auth password. Never logged.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Which environment to call.</summary>
    public BeamEnvironment Environment { get; set; } = BeamEnvironment.Playground;

    /// <summary>
    /// Overrides the address derived from <see cref="Environment"/>, so a test stack can point the client at a fake
    /// Beam server. Optional; leave unset for real traffic. Refused at startup unless <see cref="Environment"/> is
    /// <see cref="BeamEnvironment.Playground"/>, so a production configuration can never be repointed. It must be an
    /// absolute origin (no user info, query, fragment or path); <c>http</c> is allowed only for a loopback host
    /// (<c>localhost</c>, <c>127.0.0.0/8</c>, <c>::1</c>), any other host needs <c>https</c>. The merchant id and API
    /// key are sent to this address, so only set it for an environment you control.
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// The base64 HMAC key from Lighthouse, used to verify webhooks. Never logged. Optional, but when set it
    /// must decode to at least 16 bytes — validated at startup.
    /// </summary>
    public string? WebhookHmacKey { get; set; }

    /// <summary>The partner id, for a partner acting for a merchant. Sent as <c>X-Beam-Partner-ID</c>.</summary>
    public string? PartnerId { get; set; }

    /// <summary>Per-request timeout. Default 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The base address for an environment.</summary>
    /// <param name="environment">The environment.</param>
    /// <returns>Its base address.</returns>
    public static Uri BaseAddressFor(BeamEnvironment environment) => environment switch
    {
        BeamEnvironment.Playground => new Uri("https://playground.api.beamcheckout.com"),
        BeamEnvironment.Production => new Uri("https://api.beamcheckout.com"),
        _ => throw new ArgumentOutOfRangeException(nameof(environment)),
    };
}
