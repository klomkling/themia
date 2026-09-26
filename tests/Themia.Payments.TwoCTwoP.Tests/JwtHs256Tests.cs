using System.Globalization;
using System.Text.Json;
using Themia.Payments.TwoCTwoP.Internal;
using Xunit;

namespace Themia.Payments.TwoCTwoP.Tests;

public class JwtHs256Tests
{
    [Fact]
    public void Encode_then_decode_round_trips_the_claims()
    {
        var token = JwtHs256.Encode(new Dictionary<string, object?>
        {
            ["merchantID"] = "JT01",
            ["invoiceNo"] = "1523953661",
            ["amount"] = 1000.00m,
            ["currencyCode"] = "THB",
        }, "secret");

        Assert.True(JwtHs256.TryDecode(token, "secret", out var payload));
        Assert.Equal("JT01", payload.GetProperty("merchantID").GetString());
        Assert.Equal("1523953661", payload.GetProperty("invoiceNo").GetString());
    }

    [Fact]
    public void A_token_signed_with_another_secret_does_not_decode()
    {
        var token = JwtHs256.Encode(new Dictionary<string, object?> { ["invoiceNo"] = "1" }, "secret");

        Assert.False(JwtHs256.TryDecode(token, "other-secret", out _));
    }

    [Fact]
    public void A_tampered_payload_does_not_decode()
    {
        var token = JwtHs256.Encode(new Dictionary<string, object?> { ["amount"] = 100m }, "secret");
        var parts = token.Split('.');
        var tampered = $"{parts[0]}.{parts[1][..^2]}XY.{parts[2]}";

        Assert.False(JwtHs256.TryDecode(tampered, "secret", out _));
    }

    [Fact]
    public void The_header_is_alg_HS256_and_typ_JWT()
    {
        var token = JwtHs256.Encode(new Dictionary<string, object?> { ["a"] = 1 }, "secret");
        var header = JsonDocument.Parse(JwtHs256.Base64UrlDecode(token.Split('.')[0]));

        Assert.Equal("HS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());
    }

    [Fact]
    public void A_decimal_is_written_as_a_json_number_with_its_scale_intact_under_any_culture()
    {
        // The encoder adds no formatting of its own: System.Text.Json writes a decimal with the scale it
        // carries, as a number, culture-invariantly. Forcing two places is the adapter's job (Task 13).
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");   // comma decimal separator
            var token = JwtHs256.Encode(new Dictionary<string, object?> { ["amount"] = 1000.50m }, "secret");

            Assert.True(JwtHs256.TryDecode(token, "secret", out var payload));
            Assert.Equal(JsonValueKind.Number, payload.GetProperty("amount").ValueKind);
            Assert.Equal("1000.50", payload.GetProperty("amount").GetRawText());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("a.b")]
    [InlineData("not-a-jwt")]
    public void A_malformed_token_does_not_decode(string token)
    {
        Assert.False(JwtHs256.TryDecode(token, "secret", out _));
    }
}
