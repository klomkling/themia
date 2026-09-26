namespace Themia.Payments.TwoCTwoP;

/// <summary>Credentials and environment for the 2C2P adapter.</summary>
public sealed class TwoCTwoPOptions
{
    /// <summary>The configuration section these bind from.</summary>
    public const string SectionName = "Payments:TwoCTwoP";

    /// <summary>The merchant id 2C2P assigned, sent as <c>merchantID</c> in every JWT payload.</summary>
    public string MerchantId { get; set; } = "";

    /// <summary>The merchant secret key. Signs every outgoing JWT and verifies every incoming one. Never logged.</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>Which environment to call.</summary>
    public TwoCTwoPEnvironment Environment { get; set; } = TwoCTwoPEnvironment.Sandbox;

    /// <summary>The payment methods this merchant is enabled for with 2C2P, sent as <c>paymentChannel[]</c>.</summary>
    public IReadOnlyList<PaymentMethod> PaymentChannels { get; set; } = [];

    /// <summary>Per-request timeout. Default 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The base address for an environment.</summary>
    /// <param name="environment">The environment.</param>
    /// <returns>Its base address.</returns>
    public static Uri BaseAddressFor(TwoCTwoPEnvironment environment) => environment switch
    {
        TwoCTwoPEnvironment.Sandbox => new Uri("https://sandbox-pgw.2c2p.com"),
        TwoCTwoPEnvironment.Production => new Uri("https://pgw.2c2p.com"),
        _ => throw new ArgumentOutOfRangeException(nameof(environment)),
    };
}
