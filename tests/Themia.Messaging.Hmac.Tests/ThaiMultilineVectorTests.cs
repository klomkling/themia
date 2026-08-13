using System.Text.Json;

using Xunit;

namespace Themia.Messaging.Hmac.Tests;

// The properties `lead-post-thai-multiline-body` exists to pin. GoldenVectorTests proves the VALUE — that
// this canonical string signs to that signature — which is not the same as proving the fixture still
// contains the shape the value was chosen for. Delete the vector or straighten its newline and the theory
// simply has one less case; nothing turns red. These tests are what turns red.
//
// This class was CandidateVectorTests until 2026-08-14, when the vector was promoted (coord #0068, #0069):
// ezy-assets reproduced the signature three ways including a Python implementation written from the
// documented rule, and propertiezy recomputed all five independently of its own signer. Its job then
// changed from "keep an unconfirmed pin out of the interop theory" to "keep the shape the pin depends on".
public class ThaiMultilineVectorTests
{
    private const string VectorName = "lead-post-thai-multiline-body";
    private const string ExpectedSignature = "a1f0e020f882b90640cc5f88816186aaaf34156ca894fed53c5bb2cbc06561e8";

    private static GoldenVectorTests.Vector Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Vectors", "golden-vectors.json");
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var element = doc.RootElement.GetProperty("vectors").EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == VectorName);

        return new GoldenVectorTests.Vector(
            element.GetProperty("name").GetString()!,
            element.GetProperty("status").GetString()!,
            element.GetProperty("secret").GetString()!,
            element.GetProperty("timestamp").GetString()!,
            element.GetProperty("method").GetString()!,
            element.GetProperty("pathAndQuery").GetString()!,
            element.GetProperty("body").GetString()!,
            element.GetProperty("signature").GetString()!);
    }

    // The file guard. Dropping the vector would un-pin UTF-8 and the newline rule with nothing else failing.
    [Fact]
    public void TheVector_ShouldStillBePresent_AndConfirmed()
    {
        var vector = Load();

        Assert.Equal(VectorName, vector.Name);
        Assert.Equal("confirmed", vector.Status);
        Assert.Equal(ExpectedSignature, vector.Signature);
    }

    // A newline inside the body is DATA, not a fifth canonical field. An implementation that split the
    // canonical string on '\n' and took four parts would build a shorter string, produce a valid-looking
    // signature, and 401 only on messages whose text happens to wrap — which Thai lead messages routinely do.
    [Fact]
    public void TheBody_ShouldCarryExactlyOneLiteralNewline_NotAnEscapeSequence()
    {
        var body = Load().Body;

        Assert.Equal(1, body.Count(c => c == '\n'));
        Assert.DoesNotContain("\\n", body, StringComparison.Ordinal);
        Assert.Contains("บรรทัดแรก\nบรรทัดที่สอง", body, StringComparison.Ordinal);
    }

    // The canonical string is hashed as UTF-8 BYTES. An implementation reaching for a single-byte encoding
    // agrees on every ASCII vector in the file and diverges only here.
    [Fact]
    public void TheBody_ShouldCarryRawThai_AndSignOverItsUtf8Bytes()
    {
        var vector = Load();

        Assert.Contains("สมชาย ใจดี", vector.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0E", vector.Body, StringComparison.OrdinalIgnoreCase);

        var canonical = ThemiaHmacV1.Canonicalize(
            vector.Timestamp, vector.Method, vector.PathAndQuery, vector.Body);

        // 164 UTF-8 bytes from 104 characters — the number both peers reproduced. Equal counts would mean
        // the Thai had been flattened to ASCII somewhere between the file and the hash.
        Assert.Equal(104, canonical.Length);
        Assert.Equal(164, System.Text.Encoding.UTF8.GetByteCount(canonical));
        Assert.Equal(ExpectedSignature, ThemiaHmacV1.Sign(canonical, vector.Secret));
    }
}
