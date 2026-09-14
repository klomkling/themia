namespace Themia.Content.Internal;

/// <summary>The key of one page.</summary>
internal readonly record struct PageKey(string Slug, string Language);

/// <summary>One <c>content_pages</c> row, bound by the dialect's column aliases.</summary>
internal sealed class ContentPageRow
{
    public long Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Markdown { get; set; } = string.Empty;
    public int CurrentVersion { get; set; }
    public bool IsPublished { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    public ContentPage ToPage() =>
        new(Slug, Language, Title, Markdown, CurrentVersion, IsPublished, CreatedAt, UpdatedAt, UpdatedBy);
}

/// <summary>One page-summary row.</summary>
internal sealed class ContentPageSummaryRow
{
    public string Slug { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int CurrentVersion { get; set; }
    public bool IsPublished { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    public ContentPageSummary ToSummary() =>
        new(Slug, Language, Title, CurrentVersion, IsPublished, UpdatedAt, UpdatedBy);
}

/// <summary>One <c>content_page_revisions</c> row.</summary>
internal sealed class ContentRevisionRow
{
    public int Version { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Markdown { get; set; } = string.Empty;
    public string? ChangeSummary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }

    public ContentPageRevision ToRevision() => new(Version, Title, Markdown, ChangeSummary, CreatedAt, CreatedBy);
}
