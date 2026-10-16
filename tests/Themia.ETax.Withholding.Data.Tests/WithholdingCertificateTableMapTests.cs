using Themia.ETax.Data;
using Xunit;

namespace Themia.ETax.Withholding.Data.Tests;

public sealed class WithholdingCertificateTableMapTests
{
    private static WithholdingCertificateTableMap Map(Func<WithholdingCertificateTableMap, WithholdingCertificateTableMap>? change = null)
    {
        var map = new WithholdingCertificateTableMap { Table = "withholding_certificates", IssuedStateValue = 1, CancelledStateValue = 2 };
        return change is null ? map : change(map);
    }

    [Fact]
    public void ToTaxDocumentTableMap_ShouldAcceptTheDefaults() => Map().ToTaxDocumentTableMap().Validate();

    [Fact]
    public void ToTaxDocumentTableMap_ShouldNameThePayerColumnAsTheGuardsSellerColumn_AndIssuedAsTheStateThatLeavesOnlyByReplacement()
    {
        var shared = Map().ToTaxDocumentTableMap();

        Assert.Equal("payer_id", shared.SellerIdColumn);
        Assert.Equal("replaced_by_certificate_id", shared.ReplacedByColumn);
        Assert.Equal(1, shared.SentStateValue);
        Assert.Equal(2, shared.CancelledStateValue);
    }

    [Fact]
    public void ToTaxDocumentTableMap_ShouldGuardEveryIdentityNumberAndSnapshotColumnByDefault_AndNoKindColumn()
    {
        var columns = Map().ToTaxDocumentTableMap().ImmutableColumns;

        Assert.Equal(
            new[]
            {
                "id", "payer_id", "snapshot_json", "source_event_id", "revision", "document_number", "issue_date",
                "snapshot_sha256", "snapshot_schema_version", "template_version", "signature_sha256",
            }.Order(),
            columns.Order());
        Assert.DoesNotContain("kind", columns);
    }

    [Fact]
    public void ToTaxDocumentTableMap_ShouldFollowRenamedColumns_InTheDefaultImmutableList()
    {
        var shared = Map(m => m with { IdColumn = "Id", PayerIdColumn = "PayerId", SnapshotJsonColumn = "SnapshotJson" }).ToTaxDocumentTableMap();

        Assert.Contains("Id", shared.ImmutableColumns);
        Assert.Contains("PayerId", shared.ImmutableColumns);
        Assert.Contains("SnapshotJson", shared.ImmutableColumns);
        Assert.DoesNotContain("payer_id", shared.ImmutableColumns);
    }

    [Fact]
    public void ToTaxDocumentTableMap_ShouldUseTheListTheAppGives()
    {
        var shared = Map(m => m with { ImmutableColumns = ["snapshot_json", "Number"] }).ToTaxDocumentTableMap();

        Assert.Equal(["snapshot_json", "Number"], shared.ImmutableColumns);
    }

    [Fact]
    public void Validation_ShouldStillRejectInjectedNamesAndAnImmutableListWithoutTheSnapshotColumn()
    {
        Assert.Throws<ArgumentException>(() => Map(m => m with { Table = "x; DROP TABLE y" }).ToTaxDocumentTableMap().Validate());
        Assert.Throws<ArgumentException>(() => Map(m => m with { PayerIdColumn = "payer\" --" }).ToTaxDocumentTableMap().Validate());
        Assert.Throws<ArgumentException>(() => Map(m => m with { ImmutableColumns = ["document_number"] }).ToTaxDocumentTableMap().Validate());
        Assert.Throws<ArgumentException>(() => Map(m => m with { CancelledStateValue = "Cancelled" }).ToTaxDocumentTableMap().Validate());
    }

    [Fact]
    public void ToTaxDocumentTableMap_ShouldFollowRenamedKeyColumns_InTheDefaultImmutableList()
    {
        var shared = Map(m => m with { SourceEventIdColumn = "SourceEventId", RevisionColumn = "Revision" }).ToTaxDocumentTableMap();

        Assert.Contains("SourceEventId", shared.ImmutableColumns);
        Assert.Contains("Revision", shared.ImmutableColumns);
        Assert.DoesNotContain("source_event_id", shared.ImmutableColumns);
        Assert.DoesNotContain("revision", shared.ImmutableColumns);
    }

    [Fact]
    public void ValidateIssueKey_ShouldAcceptTheDefaults_AndNameTheIndexAfterTheTable()
    {
        var map = Map();

        map.ValidateIssueKey();

        Assert.Equal("ux_withholding_certificates_source_revision", map.IssueKeyIndexName);
    }

    [Theory]
    [InlineData("x; DROP TABLE y")]
    [InlineData("1table")]
    [InlineData("")]
    [InlineData("a_table_name_that_is_far_too_long_to_derive_names_from")]
    public void ValidateIssueKey_ShouldRejectATableThatIsNotAPlainIdentifier(string table) =>
        Assert.Throws<ArgumentException>(() => Map(m => m with { Table = table }).ValidateIssueKey());

    [Fact]
    public void ValidateIssueKey_ShouldRejectInjectedSchemaOrKeyColumns()
    {
        Assert.Throws<ArgumentException>(() => Map(m => m with { Schema = "public; --" }).ValidateIssueKey());
        Assert.Throws<ArgumentException>(() => Map(m => m with { SourceEventIdColumn = "a\" --" }).ValidateIssueKey());
        Assert.Throws<ArgumentException>(() => Map(m => m with { RevisionColumn = "r)" }).ValidateIssueKey());
    }

    [Fact]
    public void EveryNameDerivedFromTheLongestAllowedTable_ShouldStayWithinThe63CharacterLimit()
    {
        var table = new string('t', WithholdingCertificateTableMap.MaxTableLength);
        var map = Map(m => m with { Table = table });

        map.ValidateIssueKey();
        map.ToTaxDocumentTableMap().Validate();
        Assert.True(map.IssueKeyIndexName.Length <= WithholdingCertificateTableMap.MaxIdentifierLength);
    }

    [Fact]
    public void ApplyIssueKey_ShouldRejectNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => WithholdingCertificateGuards.ApplyIssueKey(null!, Map()));
        Assert.Throws<ArgumentNullException>(() => WithholdingCertificateGuards.ApplyIssueKey(null!, null!));
    }

    [Fact]
    public void ApplyImmutability_ShouldRejectNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => WithholdingCertificateGuards.ApplyImmutability(null!, null!));
        Assert.Throws<ArgumentNullException>(() => WithholdingCertificateGuards.ApplySamePayerReplacementKey(null!, null!));
    }
}
