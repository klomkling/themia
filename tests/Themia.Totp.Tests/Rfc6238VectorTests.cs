using Microsoft.Extensions.Options;
using Themia.Totp;
using Xunit;

namespace Themia.Totp.Tests;

/// <summary>
/// RFC 6238 Appendix B, verbatim. These pin the package against the specification rather than against
/// itself: a vector this package computed would prove only that it agrees with its own arithmetic.
/// <para>
/// Every case supplies the time as an <b>instant</b> and lets the implementation derive the step. A
/// vector handed a step counter has already done half the work under test — the same defect coord #0068
/// found in our HMAC vector, where the timestamp was supplied as a literal string and so could never
/// catch a formatter emitting <c>+00:00</c> instead of <c>Z</c>.
/// </para>
/// </summary>
public sealed class Rfc6238VectorTests
{
    // The RFC's shared secrets, base32-encoded independently of this package's own encoder.
    private const string Sha1Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private const string Sha256Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA====";
    private const string Sha512Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNA=";

    [Theory]
    // Unix time, expected 8-digit code — RFC 6238 Appendix B, SHA-1 column.
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Sha1(long unixSeconds, string expected)
        => AssertVector(Sha1Secret, TotpAlgorithm.Sha1, unixSeconds, expected);

    [Theory]
    [InlineData(59L, "46119246")]
    [InlineData(1111111109L, "68084774")]
    [InlineData(1111111111L, "67062674")]
    [InlineData(1234567890L, "91819424")]
    [InlineData(2000000000L, "90698825")]
    [InlineData(20000000000L, "77737706")]
    public void Sha256(long unixSeconds, string expected)
        => AssertVector(Sha256Secret, TotpAlgorithm.Sha256, unixSeconds, expected);

    [Theory]
    [InlineData(59L, "90693936")]
    [InlineData(1111111109L, "25091201")]
    [InlineData(1111111111L, "99943326")]
    [InlineData(1234567890L, "93441116")]
    [InlineData(2000000000L, "38618901")]
    [InlineData(20000000000L, "47863826")]
    public void Sha512(long unixSeconds, string expected)
        => AssertVector(Sha512Secret, TotpAlgorithm.Sha512, unixSeconds, expected);

    private static void AssertVector(string secret, TotpAlgorithm algorithm, long unixSeconds, string expected)
    {
        // The instant, not the step: deriving the step is part of what is under test.
        var clock = new TestClock(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        var service = new TotpService(
            new AlwaysFreeReplayStore(),
            clock,
            Options.Create(new TotpOptions { Digits = 8, Algorithm = algorithm }));

        Assert.Equal(expected, service.GenerateCode(secret));
    }

    private sealed class AlwaysFreeReplayStore : ITotpReplayStore
    {
        public ValueTask<bool> TryConsumeAsync(string secretId, long matchedStep, CancellationToken ct = default)
            => ValueTask.FromResult(true);
    }
}
