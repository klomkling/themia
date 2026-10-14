using Xunit;

namespace Themia.ETax.Data.Tests;

public sealed class TaxDocumentTableMapTests
{
    private static TaxDocumentTableMap Map(Func<TaxDocumentTableMap, TaxDocumentTableMap>? change = null)
    {
        var map = new TaxDocumentTableMap { Table = "tax_documents", SentStateValue = 3, CancelledStateValue = 5 };
        return change is null ? map : change(map);
    }

    [Fact]
    public void Validate_ShouldAcceptTheDefaults() => Map().Validate();

    [Theory]
    [InlineData("tax_documents; DROP TABLE x")]
    [InlineData("tax\"documents")]
    [InlineData("1documents")]
    [InlineData("tax documents")]
    [InlineData("")]
    [InlineData("a_table_name_that_is_far_too_long_to_derive_names_from")]
    public void Validate_ShouldRejectATableThatIsNotAPlainIdentifier(string table) =>
        Assert.Throws<ArgumentException>(() => Map(m => m with { Table = table }).Validate());

    [Fact]
    public void Validate_ShouldRejectAnInjectedColumnOrSchema()
    {
        Assert.Throws<ArgumentException>(() => Map(m => m with { StateColumn = "state\" OR 1=1 --" }).Validate());
        Assert.Throws<ArgumentException>(() => Map(m => m with { Schema = "public; --" }).Validate());
        Assert.Throws<ArgumentException>(() => Map(m => m with { ImmutableColumns = ["snapshot_json", "x'y"] }).Validate());
    }

    [Fact]
    public void Validate_ShouldRequireTheSnapshotColumnToBeImmutable_SoClearingItIsRejected() =>
        Assert.Throws<ArgumentException>(() => Map(m => m with { ImmutableColumns = ["document_number"] }).Validate());

    [Fact]
    public void Validate_ShouldAcceptPascalCaseNames() =>
        Map(m => m with
        {
            Table = "OpsTaxDocuments",
            IdColumn = "Id",
            SellerIdColumn = "SellerId",
            StateColumn = "State",
            ReplacedByColumn = "ReplacedByDocumentId",
            SnapshotJsonColumn = "SnapshotJson",
            ImmutableColumns = ["Id", "SnapshotJson", "DocumentNumber"],
        }).Validate();

    [Theory]
    [InlineData(3, "3")]
    [InlineData(5L, "5")]
    [InlineData("Sent", "'Sent'")]
    [InlineData("state_3", "'state_3'")]
    public void Literal_ShouldRenderNumbersAndWordLikeStrings(object value, string expected) =>
        Assert.Equal(expected, TaxDocumentTableMap.Literal(value, "v"));

    [Theory]
    [InlineData("$themia$; DROP TABLE x; --")]
    [InlineData("Sent$themia$")]
    [InlineData("a$b")]
    [InlineData("O'Hara")]
    [InlineData("a b")]
    [InlineData("")]
    public void Literal_ShouldRejectAStringThatCouldEscapeTheFunctionBody(string value) =>
        Assert.Throws<ArgumentException>(() => TaxDocumentTableMap.Literal(value, "v"));

    [Fact]
    public void Literal_ShouldRejectAStringLongerThan63Characters()
    {
        Assert.Equal("'" + new string('a', 63) + "'", TaxDocumentTableMap.Literal(new string('a', 63), "v"));
        Assert.Throws<ArgumentException>(() => TaxDocumentTableMap.Literal(new string('a', 64), "v"));
    }

    [Fact]
    public void Validate_ShouldRejectStateValuesOfDifferentKinds() =>
        Assert.Throws<ArgumentException>(() => Map(m => m with { SentStateValue = 3, CancelledStateValue = "Cancelled" }).Validate());

    [Fact]
    public void Validate_ShouldAcceptTheLongestTable_AndRejectOneCharacterMore()
    {
        var longest = new string('t', TaxDocumentTableMap.MaxTableLength);

        Map(m => m with { Table = longest }).Validate();
        Assert.Throws<ArgumentException>(() => Map(m => m with { Table = longest + "t" }).Validate());
        Assert.Equal(39, TaxDocumentTableMap.MaxTableLength);
    }

    [Fact]
    public void GeneratedNames_ShouldAllFitPostgreSqlsLimit_ForTheLongestTable()
    {
        var map = Map(m => m with { Table = new string('t', TaxDocumentTableMap.MaxTableLength) });

        Assert.Equal(6, map.GeneratedNames.Count);
        Assert.All(map.GeneratedNames, name => Assert.True(name.Length <= 63, $"{name.Length}: {name}"));
        Assert.Contains(map.GeneratedNames, name => name.Length == 63);   // the foreign key is the binding one
    }

    [Fact]
    public void Literal_ShouldRenderAnEnumAsItsNumber() =>
        Assert.Equal("3", TaxDocumentTableMap.Literal(Themia.ETax.TaxDocumentState.Sent, "v"));

    [Fact]
    public void Literal_ShouldRejectAnythingElse()
    {
        Assert.Throws<ArgumentException>(() => TaxDocumentTableMap.Literal(1.5m, "v"));
        Assert.Throws<ArgumentException>(() => TaxDocumentTableMap.Literal(new object(), "v"));
    }

    [Fact]
    public void QualifiedNames_ShouldFollowTheSearchPathUnlessASchemaIsGiven()
    {
        Assert.Equal("\"tax_documents\"", Map().QualifiedTable);
        Assert.Equal("\"s1\".\"tax_documents\"", Map(m => m with { Schema = "s1" }).QualifiedTable);
        Assert.DoesNotContain("public", PostgresTaxDocumentGuardSql.Immutability(Map()).Concat(PostgresTaxDocumentGuardSql.SameSellerKey(Map())).Aggregate("", (a, b) => a + b), StringComparison.OrdinalIgnoreCase);
    }
}
