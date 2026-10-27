using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Themia.Payments.TwoCTwoP.Internal;

/// <summary>The merchant private key and 2C2P public key for refunds, parsed once.</summary>
internal sealed class TwoCTwoPRefundKeys : IDisposable
{
    private const int MinimumRsaBits = 2048;

    private TwoCTwoPRefundKeys(RSA merchantPrivate, RSA providerPublic)
    {
        MerchantPrivate = merchantPrivate;
        ProviderPublic = providerPublic;
    }

    public RSA MerchantPrivate { get; }

    public RSA ProviderPublic { get; }

    /// <summary>True when refunds are configured at all (both PEMs set).</summary>
    public static bool IsConfigured(TwoCTwoPOptions options) =>
        !string.IsNullOrWhiteSpace(options.MerchantPrivateKeyPem) && !string.IsNullOrWhiteSpace(options.ProviderPublicCertificatePem);

    /// <summary>Parses the keys, or returns why they are unusable. The reason never contains key material.</summary>
    public static bool TryCreate(TwoCTwoPOptions options, TimeProvider time, out TwoCTwoPRefundKeys? keys, out string? error)
    {
        keys = null;
        RSA? merchant = null;
        RSA? provider = null;
        try
        {
            merchant = RSA.Create();
            merchant.ImportFromPem(options.MerchantPrivateKeyPem);
            if (merchant.KeySize < MinimumRsaBits)
            {
                return Fail(merchant, provider, $"MerchantPrivateKeyPem must be an RSA key of at least {MinimumRsaBits} bits.", out error);
            }

            using var certificate = X509Certificate2.CreateFromPem(options.ProviderPublicCertificatePem);
            var now = time.GetUtcNow();
            if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
            {
                return Fail(merchant, provider, "ProviderPublicCertificatePem is outside its validity period.", out error);
            }

            provider = certificate.GetRSAPublicKey();
            if (provider is null || provider.KeySize < MinimumRsaBits)
            {
                return Fail(merchant, provider, $"ProviderPublicCertificatePem must hold an RSA key of at least {MinimumRsaBits} bits.", out error);
            }

            keys = new TwoCTwoPRefundKeys(merchant, provider);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            // The exception text is dropped: a parser may echo part of the input.
            return Fail(merchant, provider, "MerchantPrivateKeyPem and ProviderPublicCertificatePem must be valid PEM (an RSA private key and an x509 certificate).", out error);
        }
    }

    public void Dispose()
    {
        MerchantPrivate.Dispose();
        ProviderPublic.Dispose();
    }

    private static bool Fail(RSA? merchant, RSA? provider, string message, out string? error)
    {
        merchant?.Dispose();
        provider?.Dispose();
        error = message;
        return false;
    }
}
