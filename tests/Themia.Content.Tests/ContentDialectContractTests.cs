using System.Data.Common;
using System.Text.RegularExpressions;
using Themia.Content.MySql;
using Themia.Content.PostgreSql;
using Themia.Content.SqlServer;
using Xunit;

namespace Themia.Content.Tests;

public class ContentDialectContractTests
{
    public static IEnumerable<object[]> Dialects()
    {
        // Never opened: these tests inspect SQL text only.
        yield return new object[] { new PostgresContentDialect("Host=localhost;Database=content_contract") };
        yield return new object[] { new MySqlContentDialect("Server=localhost;Database=content_contract") };
        yield return new object[] { new SqlServerContentDialect("Server=localhost;Database=content_contract") };
    }

    private static readonly Dictionary<string, (Func<IContentPageDialect, string> Sql, string[] Parameters)> Statements = new()
    {
        ["InsertPageSql"] = (d => d.InsertPageSql, ["Slug", "Language", "Title", "Markdown", "IsPublished", "Now", "EditorId"]),
        ["ImportPageSql"] = (d => d.ImportPageSql, ["Slug", "Language", "Title", "Markdown", "CurrentVersion", "IsPublished", "CreatedAt", "UpdatedAt", "UpdatedBy"]),
        ["UpdatePageIfVersionSql"] = (d => d.UpdatePageIfVersionSql, ["Slug", "Language", "Title", "Markdown", "IsPublished", "ExpectedVersion", "Now", "EditorId"]),
        ["InsertRevisionSql"] = (d => d.InsertRevisionSql, ["PageId", "Version", "Title", "Markdown", "ChangeSummary", "CreatedAt", "CreatedBy"]),
        ["SelectPageSql"] = (d => d.SelectPageSql, ["Slug", "Language"]),
        ["SelectPublishedSql"] = (d => d.SelectPublishedSql, ["Slug", "Language", "Fallback"]),
        ["SelectRevisionSql"] = (d => d.SelectRevisionSql, ["Slug", "Language", "Version"]),
        ["ListPagesSql"] = (d => d.ListPagesSql, ["Offset", "Limit"]),
        ["CountPagesSql"] = (d => d.CountPagesSql, []),
        ["ListRevisionsSql"] = (d => d.ListRevisionsSql, ["Slug", "Language", "Offset", "Limit"]),
        ["CountRevisionsSql"] = (d => d.CountRevisionsSql, ["Slug", "Language"]),
    };

    private static readonly string[] PageAliases =
        ["AS Id", "AS Slug", "AS Language", "AS Title", "AS Markdown", "AS CurrentVersion", "AS IsPublished", "AS CreatedAt", "AS UpdatedAt", "AS UpdatedBy"];

    private static readonly string[] RevisionAliases =
        ["AS Version", "AS Title", "AS Markdown", "AS ChangeSummary", "AS CreatedAt", "AS CreatedBy"];

    private static readonly Regex Parameter = new("@([A-Za-z][A-Za-z0-9]*)", RegexOptions.CultureInvariant);

    [Theory]
    [MemberData(nameof(Dialects))]
    public void EveryStatement_ShouldBindExactlyTheServicesParameters(IContentPageDialect dialect)
    {
        foreach (var (name, (sql, expected)) in Statements)
        {
            var bound = Parameter.Matches(sql(dialect)).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
            Assert.True(expected.Order().SequenceEqual(bound), $"{dialect.GetType().Name}.{name} binds [{string.Join(", ", bound)}]");
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void UpdatePageIfVersionSql_ShouldGuardOnTheEditorsExpectedVersion(IContentPageDialect dialect) =>
        Assert.Matches(new Regex(@"WHERE[\s\S]*current_version\s*=\s*@ExpectedVersion", RegexOptions.IgnoreCase), dialect.UpdatePageIfVersionSql);

    [Theory]
    [MemberData(nameof(Dialects))]
    public void PageSelects_ShouldAliasEveryColumn(IContentPageDialect dialect)
    {
        foreach (var alias in PageAliases)
        {
            Assert.Contains(alias, dialect.SelectPageSql, StringComparison.Ordinal);
            Assert.Contains(alias, dialect.SelectPublishedSql, StringComparison.Ordinal);
        }

        foreach (var alias in RevisionAliases)
        {
            Assert.Contains(alias, dialect.SelectRevisionSql, StringComparison.Ordinal);
            Assert.Contains(alias, dialect.ListRevisionsSql, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Statements_ShouldNeitherSelectStarNorQualifyASchema(IContentPageDialect dialect)
    {
        foreach (var (name, (sql, _)) in Statements)
        {
            var text = sql(dialect);
            Assert.DoesNotContain("SELECT *", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(new Regex(@"\b(public|dbo)\.content_", RegexOptions.IgnoreCase), text);
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void SelectPublishedSql_ShouldReturnAtMostOneRow(IContentPageDialect dialect) =>
        Assert.Matches(new Regex(@"LIMIT 1\b|TOP \(1\)", RegexOptions.IgnoreCase), dialect.SelectPublishedSql);

    [Theory]
    [MemberData(nameof(Dialects))]
    public void IsDuplicateKey_ShouldBeFalse_ForAnUnrelatedDatabaseError(IContentPageDialect dialect) =>
        Assert.False(dialect.IsDuplicateKey(new UnrelatedDbException()));

    private sealed class UnrelatedDbException : DbException;
}
