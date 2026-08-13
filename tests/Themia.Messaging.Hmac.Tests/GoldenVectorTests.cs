using System.Text.Json;

using Xunit;

namespace Themia.Messaging.Hmac.Tests;

// These vectors are the interop contract with the live ezy-assets <-> propertiezy link. A failure here
// means Themia would 401 in production, not that the test needs adjusting.
public class GoldenVectorTests
{
    public sealed record Vector(
        string Name, string Status, string Secret, string Timestamp,
        string Method, string PathAndQuery, string Body, string Signature);

    public static TheoryData<Vector> Confirmed()
    {
        var data = new TheoryData<Vector>();
        foreach (var v in Load().Where(v => v.Status == "confirmed"))
        {
            data.Add(v);
        }

        return data;
    }

    private static IReadOnlyList<Vector> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Vectors", "golden-vectors.json");
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("vectors").EnumerateArray()
            .Select(e => new Vector(
                e.GetProperty("name").GetString()!,
                e.GetProperty("status").GetString()!,
                e.GetProperty("secret").GetString()!,
                e.GetProperty("timestamp").GetString()!,
                e.GetProperty("method").GetString()!,
                e.GetProperty("pathAndQuery").GetString()!,
                e.GetProperty("body").GetString()!,
                e.GetProperty("signature").GetString()!))
            .ToList();
    }

    [Theory]
    [MemberData(nameof(Confirmed))]
    public void Sign_ShouldProduceTheExpectedSignature(Vector v)
    {
        var canonical = ThemiaHmacV1.Canonicalize(v.Timestamp, v.Method, v.PathAndQuery, v.Body);

        Assert.Equal(v.Signature, ThemiaHmacV1.Sign(canonical, v.Secret));
    }

    // The fixture must be reachable at runtime; an empty load would make every Theory above vacuous.
    // Five since 2026-08-14, when lead-post-thai-multiline-body was promoted from candidate after both
    // peers reproduced its signature independently (coord #0068, #0069).
    [Fact]
    public void Vectors_ShouldLoad_AndContainTheFiveConfirmedCases()
        => Assert.Equal(5, Load().Count(v => v.Status == "confirmed"));

    [Fact]
    public void Canonicalize_ShouldRetainTheSeparatorNewline_ForAnEmptyBody()
    {
        var canonical = ThemiaHmacV1.Canonicalize("2026-07-14T09:30:00.0000000Z", "DELETE", "/x", string.Empty);

        Assert.EndsWith("\n", canonical, StringComparison.Ordinal);
        Assert.Equal(3, canonical.Count(c => c == '\n'));
    }

    [Fact]
    public void Canonicalize_ShouldUpperCaseTheMethod()
        => Assert.Contains("\nPUT\n", ThemiaHmacV1.Canonicalize("t", "put", "/x", "b"), StringComparison.Ordinal);

    [Fact]
    public void Canonicalize_ShouldNeverUseCarriageReturns()
        => Assert.DoesNotContain('\r', ThemiaHmacV1.Canonicalize("t", "PUT", "/x", "b"));

    [Fact]
    public void Sign_ShouldReturnLowercaseHex()
    {
        var signature = ThemiaHmacV1.Sign("canonical", "secret");

        Assert.Equal(64, signature.Length);
        Assert.Equal(signature.ToLowerInvariant(), signature);
    }

    // Round-trip format, not a fixed instant: the vectors already pin the exact shape.
    [Fact]
    public void FormatTimestamp_ShouldRoundTripToTheVectorShape()
    {
        var formatted = ThemiaHmacV1.FormatTimestamp(
            new DateTimeOffset(2026, 7, 14, 9, 30, 0, TimeSpan.Zero));

        Assert.Equal("2026-07-14T09:30:00.0000000Z", formatted);
    }

    // Guards the fix that formats via DateTimeOffset.UtcDateTime: a non-zero source offset must still
    // convert to UTC and render a trailing Z, not the offset itself. Prevents a future "simplification"
    // back to formatting the DateTimeOffset directly (which renders +00:00, never Z).
    [Fact]
    public void FormatTimestamp_ShouldConvertANonZeroOffsetToUtc_AndStillRenderZ()
    {
        var formatted = ThemiaHmacV1.FormatTimestamp(
            new DateTimeOffset(2026, 7, 14, 16, 30, 0, TimeSpan.FromHours(7)));

        Assert.Equal("2026-07-14T09:30:00.0000000Z", formatted);
    }

    [Fact]
    public void TryParseTimestamp_ShouldRoundTrip_AValidTimestamp()
    {
        var succeeded = ThemiaHmacV1.TryParseTimestamp("2026-07-14T09:30:00.0000000Z", out var result);

        Assert.True(succeeded);
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 9, 30, 0, TimeSpan.Zero), result);
    }

    [Fact]
    public void TryParseTimestamp_ShouldReturnFalse_ForAMalformedValue()
    {
        var succeeded = ThemiaHmacV1.TryParseTimestamp("not-a-timestamp", out var result);

        Assert.False(succeeded);
        Assert.Equal(default, result);
    }

    // The verifier (next task) depends on this: a peer-supplied timestamp with a non-UTC offset must
    // still resolve to the correct instant, not be rejected or silently truncated.
    [Fact]
    public void TryParseTimestamp_ShouldResolveTheCorrectInstant_ForANonUtcOffset()
    {
        var succeeded = ThemiaHmacV1.TryParseTimestamp("2026-07-14T16:30:00.0000000+07:00", out var result);

        Assert.True(succeeded);
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 9, 30, 0, TimeSpan.Zero), result);
    }

    // F8 (final whole-branch review): RoundtripKind alone reads a timestamp with NO offset designator in
    // server-LOCAL time. These services run on UTC+7 hosts, so a naive timestamp from a peer would be off
    // by ~7 hours, fail the clock-skew window, and dead-letter looking exactly like clock drift. This
    // assertion is offset-based (TimeSpan.Zero), not host-timezone-dependent, so it is meaningful in CI
    // regardless of the runner's local timezone: if the naive value were still read as local time, this
    // would resolve to a DateTimeOffset carrying the runner's local offset instead of zero.
    [Fact]
    public void TryParseTimestamp_ShouldTreatANaiveTimestamp_AsUtc_NotServerLocal()
    {
        var succeeded = ThemiaHmacV1.TryParseTimestamp("2026-07-14T09:30:00.0000000", out var result);

        Assert.True(succeeded);
        Assert.Equal(TimeSpan.Zero, result.Offset);
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 9, 30, 0, TimeSpan.Zero), result);
    }
}
