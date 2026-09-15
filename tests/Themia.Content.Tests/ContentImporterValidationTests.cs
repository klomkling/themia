using Microsoft.Extensions.Logging.Abstractions;
using Themia.Content.Internal;
using Xunit;

namespace Themia.Content.Tests;

public class ContentImporterValidationTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 19, 0, 0, 0, TimeSpan.Zero);

    private static ContentOptions Options()
    {
        var options = new ContentOptions { FallbackLanguage = "th" };
        options.Languages.Add("th");
        options.Languages.Add("en");
        return options;
    }

    private static ContentPageImport Page(string slug, string language, int currentVersion, string markdown, params (int Version, string Markdown)[] revisions) =>
        new(slug, language, "Title", markdown, currentVersion, true, At, At, null,
            revisions.Select(r => new ContentRevisionImport(r.Version, "Title", r.Markdown, null, null, At)).ToList());

    private static async Task<(ContentImportResult Result, CountingContentDialect Dialect)> ImportAsync(params ContentPageImport[] pages)
    {
        var dialect = new CountingContentDialect();
        var importer = new ContentPageImporter(dialect, Options(), NullLogger<ContentPageImporter>.Instance);
        return (await importer.ImportAsync(pages), dialect);
    }

    [Fact]
    public async Task Import_ShouldListEveryViolation_AndOpenNoConnection()
    {
        var (result, dialect) = await ImportAsync(
            Page("terms", "th", 1, "# Terms", (1, "# Terms")),
            Page("terms", " TH ", 1, "# Terms", (1, "# Terms")),
            Page("privacy", "fr", 1, "# P", (1, "# P")),
            Page("about", "th", 3, "# A3", (1, "# A1"), (3, "# A3")),
            Page("faq", "th", 1, "# Served text", (1, "# Placeholder")));

        Assert.Equal(ContentImportOutcome.Invalid, result.Outcome);
        Assert.Equal(0, dialect.ConnectionsRequested);
        Assert.Contains(result.Violations, v => v is { Slug: "terms", Rule: ContentImportRule.DuplicateKey });
        Assert.Contains(result.Violations, v => v is { Slug: "privacy", Rule: ContentImportRule.InvalidSlugOrLanguage });
        Assert.Contains(result.Violations, v => v is { Slug: "about", Rule: ContentImportRule.NonContiguousRevisions });
        Assert.Contains(result.Violations, v => v is { Slug: "faq", Rule: ContentImportRule.ContentDiffersFromCurrentRevision });
    }

    [Fact]
    public async Task Import_ShouldReportFieldTooLong_WhenARevisionTitleExceedsTheColumn()
    {
        var page = Page("terms", "th", 1, "# T", (1, "# T")) with
        {
            Revisions = [new ContentRevisionImport(1, new string('t', 201), "# T", null, null, At)],
        };

        var (result, _) = await ImportAsync(page);

        Assert.Contains(result.Violations, v => v.Rule == ContentImportRule.FieldTooLong);
    }

    [Fact]
    public async Task Import_ShouldCountHistoryThatFailsTodaysMarkdownRules_WhileRefusingForOtherReasons()
    {
        var (result, _) = await ImportAsync(
            Page("terms", "th", 2, "# T2", (1, "a <b>x</b>"), (2, "# T2")),
            Page("terms", "th", 1, "# Dup", (1, "# Dup")));

        Assert.Equal(1, result.RevisionsFailingCurrentRules);
        Assert.Equal(0, result.PagesFailingCurrentRules);
    }

    [Fact]
    public async Task Import_ShouldReportContentDiffers_WhenOnlyTheTitleDiffersFromTheCurrentRevision()
    {
        var page = Page("terms", "th", 1, "# T", (1, "# T")) with { Title = "Different Title" };

        var (result, dialect) = await ImportAsync(page);

        Assert.Equal(ContentImportOutcome.Invalid, result.Outcome);
        Assert.Contains(result.Violations, v => v is { Slug: "terms", Rule: ContentImportRule.ContentDiffersFromCurrentRevision });
        Assert.Equal(0, dialect.ConnectionsRequested);
    }

    [Fact]
    public async Task Import_ShouldThrow_WhenARevisionElementIsNull()
    {
        var dialect = new CountingContentDialect();
        var importer = new ContentPageImporter(dialect, Options(), NullLogger<ContentPageImporter>.Instance);
        var page = Page("terms", "th", 1, "# T", (1, "# T")) with
        {
            Revisions = [new ContentRevisionImport(1, "Title", "# T", null, null, At), null!],
        };

        await Assert.ThrowsAsync<ArgumentException>(() => importer.ImportAsync([page]));

        Assert.Equal(0, dialect.ConnectionsRequested);
    }

    [Fact]
    public async Task Import_ShouldThrow_WhenAPageMarkdownIsNull()
    {
        var dialect = new CountingContentDialect();
        var importer = new ContentPageImporter(dialect, Options(), NullLogger<ContentPageImporter>.Instance);
        var page = Page("terms", "th", 1, "# T", (1, "# T")) with { Markdown = null! };

        await Assert.ThrowsAsync<ArgumentException>(() => importer.ImportAsync([page]));

        Assert.Equal(0, dialect.ConnectionsRequested);
    }
}
