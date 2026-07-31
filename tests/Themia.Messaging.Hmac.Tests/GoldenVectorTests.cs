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
    [Fact]
    public void Vectors_ShouldLoad_AndContainTheFourConfirmedCases()
        => Assert.Equal(4, Load().Count(v => v.Status == "confirmed"));

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
}
