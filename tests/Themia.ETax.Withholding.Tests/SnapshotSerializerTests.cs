using Xunit;

namespace Themia.ETax.Withholding.Tests;

public class SnapshotSerializerTests
{
    [Fact]
    public void Serialize_ShouldBeDeterministic_AndAscii()
    {
        var json = WithholdingSnapshotSerializer.Serialize(Samples.Valid());

        Assert.Equal(json, WithholdingSnapshotSerializer.Serialize(Samples.Valid()));
        Assert.All(json, c => Assert.True(c < 128, "non-ASCII character in persisted snapshot"));
        Assert.StartsWith("{\"schema_version\":1,", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTrip_ShouldRestoreTheSnapshot_ThroughTheHash()
    {
        var snapshot = Samples.Valid();
        var json = WithholdingSnapshotSerializer.Serialize(snapshot);
        var restored = WithholdingSnapshotSerializer.Deserialize(json, WithholdingSnapshotSerializer.ComputeHash(json));

        Assert.Equal(json, WithholdingSnapshotSerializer.Serialize(restored));
        Assert.Equal("สมชาย ใจดี", restored.Payee.Name);
        Assert.Equal(new DateOnly(2026, 10, 14), restored.Rows[0].DatePaid);
        Assert.Equal(WithholdingFilingForm.Pnd3, restored.FilingForm);
    }

    [Fact]
    public void RoundTrip_ShouldKeepOptionalSections()
    {
        var snapshot = Samples.Valid() with
        {
            BookNumber = "5",
            FilingSequence = 12,
            Condition = WithholdingCondition.Other,
            ConditionOther = "อื่นๆ ตัวอย่าง",
            Funds = new WithholdingFunds { SocialSecuritySatang = 75_000 },
            Replacement = new TaxDocumentReplacement
            {
                ReplacesDocumentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                OriginalDocumentNumber = "WHT-2569-000100",
                OriginalIssueDate = new DateOnly(2026, 10, 1),
                Reason = "ชื่อผิด",
            },
        };
        var json = WithholdingSnapshotSerializer.Serialize(snapshot);
        var restored = WithholdingSnapshotSerializer.Deserialize(json, WithholdingSnapshotSerializer.ComputeHash(json));

        Assert.Equal(json, WithholdingSnapshotSerializer.Serialize(restored));
        Assert.Equal(75_000, restored.Funds!.SocialSecuritySatang);
        Assert.Equal("WHT-2569-000100", restored.Replacement!.OriginalDocumentNumber);
    }

    [Fact]
    public void Serialize_ShouldOmitNulls_AndNameEnumsByString()
    {
        var json = WithholdingSnapshotSerializer.Serialize(Samples.Valid());

        Assert.DoesNotContain("book_number", json, StringComparison.Ordinal);
        Assert.Contains("\"filing_form\":\"Pnd3\"", json, StringComparison.Ordinal);
        Assert.Contains("\"condition\":\"WithheldAtSource\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_ShouldRefuse_AWrongHash()
    {
        var json = WithholdingSnapshotSerializer.Serialize(Samples.Valid());
        var ex = Assert.Throws<TaxDocumentSnapshotException>(() => WithholdingSnapshotSerializer.Deserialize(json, new string('0', 64)));

        Assert.Equal(TaxDocumentSnapshotFailure.HashMismatch, ex.Failure);
    }

    [Fact]
    public void Deserialize_ShouldRefuse_AnUnknownSchemaVersion()
    {
        var json = WithholdingSnapshotSerializer.Serialize(Samples.Valid()).Replace("\"schema_version\":1", "\"schema_version\":2", StringComparison.Ordinal);
        var ex = Assert.Throws<TaxDocumentSnapshotException>(() => WithholdingSnapshotSerializer.Deserialize(json, WithholdingSnapshotSerializer.ComputeHash(json)));

        Assert.Equal(TaxDocumentSnapshotFailure.UnsupportedSchemaVersion, ex.Failure);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"schema_version\":1,\"unknown_field\":true}")]
    public void Deserialize_ShouldRefuse_MalformedText(string json)
    {
        var ex = Assert.Throws<TaxDocumentSnapshotException>(() => WithholdingSnapshotSerializer.Deserialize(json, WithholdingSnapshotSerializer.ComputeHash(json)));

        Assert.Equal(TaxDocumentSnapshotFailure.Malformed, ex.Failure);
    }

    [Fact]
    public void Deserialize_ShouldNotLeakSnapshotContent_IntoTheMessage()
    {
        var json = WithholdingSnapshotSerializer.Serialize(Samples.Valid()).Replace("\"amount_paid_satang\":1000000", "\"amount_paid_satang\":\"x\"", StringComparison.Ordinal);
        var ex = Assert.Throws<TaxDocumentSnapshotException>(() => WithholdingSnapshotSerializer.Deserialize(json, WithholdingSnapshotSerializer.ComputeHash(json)));

        Assert.DoesNotContain(Samples.ValidPayeeTaxId, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\"x\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComputeHash_ShouldBeLowercaseSha256Hex() =>
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", WithholdingSnapshotSerializer.ComputeHash("abc"));
}
