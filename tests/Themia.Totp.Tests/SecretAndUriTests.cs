using System.Web;
using Microsoft.Extensions.Options;
using Themia.Totp;
using Xunit;

namespace Themia.Totp.Tests;

public sealed class SecretAndUriTests
{
    private sealed class NoopStore : ITotpReplayStore
    {
        public ValueTask<bool> TryAdvanceAsync(string secretId, long matchedStep, CancellationToken ct = default)
            => ValueTask.FromResult(true);
    }

    private static TotpService Build(TotpOptions? options = null)
        => new(new NoopStore(),
               new TestClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)),
               Options.Create(options ?? new TotpOptions()));

    [Fact]
    public void A_generated_secret_round_trips_and_produces_a_usable_code()
    {
        var service = Build();

        var secret = service.GenerateSecret();

        // Base32 alphabet only, and long enough to matter.
        Assert.Matches("^[A-Z2-7]+=*$", secret);
        // The real property: it can actually be used, which a pure format assertion would not prove.
        Assert.Equal(6, service.GenerateCode(secret).Length);
    }

    [Theory]
    [InlineData("========")]          // padding only — base32 ignores it, so this decodes to nothing
    [InlineData("--  --")]            // separators only, which authenticator apps also strip
    [InlineData("GEZDGNBVGY3TQOJQ==")] // 10 decoded bytes: below the floor GenerateSecret refuses to mint
    public void A_secret_that_decodes_below_the_minimum_is_refused_rather_than_HMACd(string secret)
    {
        var service = Build();

        // Not "returns InvalidCode": an empty key produces a perfectly ordinary code that anyone can
        // reproduce, so a login would succeed against a credential with no secret behind it. A stored
        // secret this shape is broken data, and the caller needs to hear about it.
        Assert.Throws<ArgumentException>(() => service.GenerateCode(secret));
    }

    [Theory]
    [InlineData(500)]     // sub-second: (long)TotalSeconds is 0, and the step arithmetic divides by it
    [InlineData(30_500)]  // fractional: truncates to a different window than the one configured
    public void A_period_that_is_not_a_whole_number_of_seconds_is_refused_at_construction(int milliseconds)
    {
        var options = new TotpOptions { Period = TimeSpan.FromMilliseconds(milliseconds) };

        var error = Assert.Throws<ArgumentException>(() => Build(options));

        Assert.Contains("Period", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ten_digit_codes_are_ten_digits_and_not_truncated_by_an_int_modulus()
    {
        // (int)Math.Pow(10, 10) saturates to int.MaxValue rather than overflowing, so a 10-digit
        // configuration silently applies a modulus of 2147483647.
        var service = Build(new TotpOptions { Digits = 10 });

        var code = service.GenerateCode("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ");

        Assert.Equal(10, code.Length);
        Assert.Matches("^[0-9]{10}$", code);
    }

    [Fact]
    public void Two_generated_secrets_differ()
    {
        var service = Build();

        Assert.NotEqual(service.GenerateSecret(), service.GenerateSecret());
    }

    [Fact]
    public void A_secret_shorter_than_the_RFC_minimum_is_refused()
    {
        var service = Build();

        // RFC 4226 §4 requires at least 128 bits. Silently accepting 8 bytes would weaken every
        // credential minted through this package.
        Assert.Throws<ArgumentOutOfRangeException>(() => service.GenerateSecret(byteLength: 8));
    }

    [Fact]
    public void The_provisioning_uri_carries_everything_an_authenticator_needs()
    {
        var service = Build(new TotpOptions { Digits = 8, Algorithm = TotpAlgorithm.Sha256, Period = TimeSpan.FromSeconds(60) });

        var uri = service.CreateProvisioningUri("GEZDGNBVGY3TQOJQ", "Ezy Assets", "someone@example.com");

        Assert.Equal("otpauth", uri.Scheme);
        Assert.Equal("totp", uri.Host);

        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("GEZDGNBVGY3TQOJQ", query["secret"]);
        Assert.Equal("Ezy Assets", query["issuer"]);
        Assert.Equal("SHA256", query["algorithm"]);
        Assert.Equal("8", query["digits"]);
        Assert.Equal("60", query["period"]);

        // The issuer appears in the label AS WELL as the parameter: apps disagree about which they
        // read, and omitting either shows the wrong name on somebody's phone.
        Assert.Contains("Ezy%20Assets:someone%40example.com", uri.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void The_provisioning_uri_strips_base32_padding_from_the_secret()
    {
        var service = Build();

        // Authenticator apps reject '=' in the secret parameter; a padded secret would produce a QR
        // code that scans and then never matches.
        var uri = service.CreateProvisioningUri("GEZDGNBVGY3TQOJQGEZA====", "Issuer", "account");

        Assert.Equal("GEZDGNBVGY3TQOJQGEZA", HttpUtility.ParseQueryString(uri.Query)["secret"]);
    }

    [Theory]
    [InlineData("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ")]      // no padding
    [InlineData("gezdgnbvgy3tqojqgezdgnbvgy3tqojq")]      // lower case, as a user might paste it
    [InlineData("GEZD GNBV GY3T QOJQ GEZD GNBV GY3T QOJQ")] // spaced, as apps display it
    [InlineData("GEZD-GNBV-GY3T-QOJQ-GEZD-GNBV-GY3T-QOJQ")]
    public void A_secret_is_accepted_however_the_user_pasted_it(string secret)
    {
        var service = Build();

        // All four are the same key, so they must all produce the RFC's code for this instant.
        Assert.Equal(service.GenerateCode("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"), service.GenerateCode(secret));
    }

    [Fact]
    public void A_secret_that_is_not_base32_is_rejected_clearly()
    {
        var service = Build();

        Assert.Throws<FormatException>(() => service.GenerateCode("not-base32!"));
    }
}
