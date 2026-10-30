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

    /// <summary>
    /// The merchant's RSA private key (PEM, PKCS#8 or PKCS#1), which signs refund requests and decrypts refund responses. Its public half is the
    /// merchant public key uploaded to the 2C2P Merchant Portal. Refunds need this AND <see cref="ProviderPublicCertificatePem"/>; with either unset,
    /// <see cref="TwoCTwoPPaymentGateway.RefundAsync"/> refuses with <c>refund_not_supported</c>. Never logged.
    /// </summary>
    public string? MerchantPrivateKeyPem { get; set; }

    /// <summary>
    /// 2C2P's public certificate (PEM, x509), downloaded from the 2C2P Merchant Portal (Account, Options, 2C2P Public Keys). It encrypts refund
    /// requests and verifies refund responses. Must be an RSA certificate that has not expired.
    /// </summary>
    public string? ProviderPublicCertificatePem { get; set; }

    /// <summary>Which environment to call.</summary>
    public TwoCTwoPEnvironment Environment { get; set; } = TwoCTwoPEnvironment.Sandbox;

    /// <summary>The payment methods this merchant is enabled for with 2C2P, sent as <c>paymentChannel[]</c>.</summary>
    public IReadOnlyList<PaymentMethod> PaymentChannels { get; set; } = [];

    /// <summary>
    /// The https URL 2C2P calls with the final status of an asynchronous refund (some 123 payments and wallets), sent as <c>notifyURL</c> in the refund request; point it at an
    /// endpoint that hands the body to <see cref="TwoCTwoPRefundNotificationVerifier"/>. Needs the refund keys. Default <see langword="null"/>: nothing is sent and refunds are followed by polling.
    /// <b>Do not set it in production until a sandbox refund confirms 2C2P accepts the element:</b> the guide only names <c>notifyURL</c>, and a request 2C2P refuses would turn every refund into <c>refund_outcome_unknown</c>.
    /// </summary>
    public string? RefundNotifyUrl { get; set; }

    /// <summary>Per-request timeout. Default 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The UTC offset of 2C2P's transactionDateTime, which carries none and whose zone 2C2P does not
    /// document. Defaults to +07:00 (Thailand). Confirm against a sandbox transaction.</summary>
    public TimeSpan TransactionTimeOffset { get; set; } = TimeSpan.FromHours(7);

    /// <summary>The Payment Maintenance (refund) endpoint of an environment. It lives on a different host than <see cref="BaseAddressFor"/>.</summary>
    /// <param name="environment">The environment.</param>
    /// <returns>Its action endpoint.</returns>
    public static Uri ActionUriFor(TwoCTwoPEnvironment environment) => environment switch
    {
        TwoCTwoPEnvironment.Sandbox => new Uri("https://demo2.2c2p.com/PaymentAction/2.0/action"),
        TwoCTwoPEnvironment.Production => new Uri("https://t.2c2p.com/PaymentAction/2.0/action"),
        _ => throw new ArgumentOutOfRangeException(nameof(environment)),
    };

    /// <summary>The base address for an environment.</summary>
    /// <param name="environment">The environment.</param>
    /// <returns>Its base address.</returns>
    public static Uri BaseAddressFor(TwoCTwoPEnvironment environment) => environment switch
    {
        TwoCTwoPEnvironment.Sandbox => new Uri("https://sandbox-pgw.2c2p.com"),
        TwoCTwoPEnvironment.Production => new Uri("https://pgw.2c2p.com"),
        _ => throw new ArgumentOutOfRangeException(nameof(environment)),
    };

    internal static bool IsValidNotifyUrl(string? value) =>
        value is null || (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0);
}
