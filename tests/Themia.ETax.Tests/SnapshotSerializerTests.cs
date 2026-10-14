using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Themia.ETax.Tests;

public class SnapshotSerializerTests
{
    [Fact]
    public void Serialize_ShouldBeDeterministic_AndRoundTrip()
    {
        var snapshot = Samples.FullCancellationCreditNote();

        var json = TaxDocumentSnapshotSerializer.Serialize(snapshot);
        var hash = TaxDocumentSnapshotSerializer.ComputeHash(json);

        Assert.Equal(json, TaxDocumentSnapshotSerializer.Serialize(snapshot));
        var back = TaxDocumentSnapshotSerializer.Deserialize(json, hash);
        Assert.Equal(json, TaxDocumentSnapshotSerializer.Serialize(back));
        Assert.Equal(snapshot.Seller.Name, back.Seller.Name);
        Assert.Equal(TaxNoteMode.FullCancellation, back.Note!.Mode);
    }

    [Fact]
    public void Serialize_ShouldUseSnakeCaseNames_StringEnums_AndSchemaVersion()
    {
        var json = TaxDocumentSnapshotSerializer.Serialize(Samples.Receipt());

        Assert.Contains("\"schema_version\":1", json);
        Assert.Contains("\"document_number\":\"TAX-2026-000001\"", json);
        Assert.Contains("\"kind\":\"TaxInvoiceReceipt\"", json);
        Assert.Contains("\"issue_date\":\"2026-10-14\"", json);
        Assert.Contains("\"amount_ex_vat_satang\":100000", json);
        Assert.DoesNotContain("\"note\"", json);
    }

    [Fact]
    public void Serialize_ShouldBeAsciiSafe_SoColumnEncodingCannotChangeTheBytes()
    {
        var json = TaxDocumentSnapshotSerializer.Serialize(Samples.Receipt());

        Assert.All(json, c => Assert.True(c < 128));
        Assert.Equal("คุณสมชาย ใจดี", TaxDocumentSnapshotSerializer.Deserialize(json, TaxDocumentSnapshotSerializer.ComputeHash(json)).Buyer.Name);
    }

    [Fact]
    public void ComputeHash_ShouldBeLowercaseSha256Hex_OfTheUtf8Bytes()
    {
        const string json = "{\"a\":1}";

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

        Assert.Equal(expected, TaxDocumentSnapshotSerializer.ComputeHash(json));
    }

    [Fact]
    public void Serialize_ShouldMatchGoldenHash_SoTheStoredFormatCannotChangeSilently()
    {
        var json = TaxDocumentSnapshotSerializer.Serialize(Samples.Receipt());

        Assert.Equal(GoldenReceiptJson, json);
        Assert.Equal(GoldenReceiptHash, TaxDocumentSnapshotSerializer.ComputeHash(json));
    }

    private const string GoldenReceiptHash = "819b06dc3d2cb9f5e9b7cc63816a2da3b67d4cccce678e1d574ab61ce2372809";

    private const string GoldenReceiptJson =
        "{\"schema_version\":1,\"kind\":\"TaxInvoiceReceipt\",\"document_number\":\"TAX-2026-000001\",\"issue_date\":\"2026-10-14\",\"seller\":{\"name\":\"\\u0E1A\\u0E23\\u0E34\\u0E29\\u0E31\\u0E17 \\u0E15\\u0E31\\u0E27\\u0E2D\\u0E22\\u0E48\\u0E32\\u0E07 \\u0E08\\u0E33\\u0E01\\u0E31\\u0E14\",\"tax_id\":\"0105536078673\",\"branch\":\"00000\",\"address\":\"1 \\u0E16\\u0E19\\u0E19\\u0E2A\\u0E38\\u0E02\\u0E38\\u0E21\\u0E27\\u0E34\\u0E17 \\u0E01\\u0E23\\u0E38\\u0E07\\u0E40\\u0E17\\u0E1E\\u0E21\\u0E2B\\u0E32\\u0E19\\u0E04\\u0E23\"},\"buyer\":{\"name\":\"\\u0E04\\u0E38\\u0E13\\u0E2A\\u0E21\\u0E0A\\u0E32\\u0E22 \\u0E43\\u0E08\\u0E14\\u0E35\",\"tax_id\":\"1101700230708\"},\"lines\":[{\"description\":\"Boost package\",\"amount_ex_vat_satang\":100000}],\"totals\":{\"ex_vat_satang\":100000,\"vat_satang\":7000,\"incl_vat_satang\":107000,\"withheld_satang\":3000,\"net_received_satang\":104000,\"vat_rate_basis_points\":700}}";

    [Fact]
    public void Deserialize_ShouldThrowHashMismatch_WhenTheStoredTextWasRewritten()
    {
        var json = TaxDocumentSnapshotSerializer.Serialize(Samples.Receipt());
        var hash = TaxDocumentSnapshotSerializer.ComputeHash(json);
        var reordered = json.Replace("\"kind\":\"TaxInvoiceReceipt\"", " \"kind\":\"TaxInvoiceReceipt\"");

        var ex = Assert.Throws<TaxDocumentSnapshotException>(() => TaxDocumentSnapshotSerializer.Deserialize(reordered, hash));

        Assert.Equal(TaxDocumentSnapshotFailure.HashMismatch, ex.Failure);
    }

    [Fact]
    public void Deserialize_ShouldAcceptUppercaseHash()
    {
        var json = TaxDocumentSnapshotSerializer.Serialize(Samples.Receipt());

        TaxDocumentSnapshotSerializer.Deserialize(json, TaxDocumentSnapshotSerializer.ComputeHash(json).ToUpperInvariant());
    }

    [Fact]
    public void Deserialize_ShouldThrowUnsupportedSchema_ForAFutureVersion()
    {
        var json = TaxDocumentSnapshotSerializer.Serialize(Samples.Receipt()).Replace("\"schema_version\":1", "\"schema_version\":2");

        var ex = Assert.Throws<TaxDocumentSnapshotException>(
            () => TaxDocumentSnapshotSerializer.Deserialize(json, TaxDocumentSnapshotSerializer.ComputeHash(json)));

        Assert.Equal(TaxDocumentSnapshotFailure.UnsupportedSchemaVersion, ex.Failure);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Deserialize_ShouldThrowMalformed_ForGarbage(string json)
    {
        var ex = Assert.Throws<TaxDocumentSnapshotException>(
            () => TaxDocumentSnapshotSerializer.Deserialize(json, TaxDocumentSnapshotSerializer.ComputeHash(json)));

        Assert.Equal(TaxDocumentSnapshotFailure.Malformed, ex.Failure);
    }

    [Fact]
    public void Deserialize_ShouldThrowMalformed_ForUnknownMembers()
    {
        var json = TaxDocumentSnapshotSerializer.Serialize(Samples.Receipt()).Replace("\"schema_version\":1", "\"schema_version\":1,\"extra\":true");

        var ex = Assert.Throws<TaxDocumentSnapshotException>(
            () => TaxDocumentSnapshotSerializer.Deserialize(json, TaxDocumentSnapshotSerializer.ComputeHash(json)));

        Assert.Equal(TaxDocumentSnapshotFailure.Malformed, ex.Failure);
    }

    [Fact]
    public void Exception_ShouldNotLeakSnapshotContent()
    {
        var json = TaxDocumentSnapshotSerializer.Serialize(Samples.Receipt());

        var ex = Assert.Throws<TaxDocumentSnapshotException>(() => TaxDocumentSnapshotSerializer.Deserialize(json + " ", "00"));

        Assert.DoesNotContain(Samples.BuyerTaxId, ex.ToString());
        Assert.DoesNotContain("สมชาย", ex.ToString());
    }
}
