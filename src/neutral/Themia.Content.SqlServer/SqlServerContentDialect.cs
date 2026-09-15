using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Themia.Content.SqlServer;

/// <summary>SQL Server implementation of <see cref="IContentPageDialect"/> (Microsoft.Data.SqlClient).</summary>
public sealed class SqlServerContentDialect : IContentPageDialect
{
    private const int UniqueConstraintViolation = 2627;
    private const int DuplicateKeyInUniqueIndex = 2601;

    private const string PageColumns =
        "id AS Id, slug AS Slug, language AS Language, title AS Title, markdown AS Markdown, " +
        "current_version AS CurrentVersion, is_published AS IsPublished, created_at AS CreatedAt, " +
        "updated_at AS UpdatedAt, updated_by AS UpdatedBy";

    private const string RevisionColumns =
        "r.version AS Version, r.title AS Title, r.markdown AS Markdown, r.change_summary AS ChangeSummary, " +
        "r.created_at AS CreatedAt, r.created_by AS CreatedBy";

    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    /// <param name="connectionString">SQL Server connection string.</param>
    public SqlServerContentDialect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection() => new SqlConnection(connectionString);

    /// <inheritdoc />
    public bool IsDuplicateKey(DbException exception) =>
        exception is SqlException { Number: UniqueConstraintViolation or DuplicateKeyInUniqueIndex };

    /// <inheritdoc />
    public string InsertPageSql => """
        INSERT INTO content_pages (slug, language, title, markdown, current_version, is_published, created_at, updated_at, updated_by)
        VALUES (@Slug, @Language, @Title, @Markdown, 1, @IsPublished, @Now, @Now, @EditorId);
        """;

    /// <inheritdoc />
    public string ImportPageSql => """
        INSERT INTO content_pages (slug, language, title, markdown, current_version, is_published, created_at, updated_at, updated_by)
        VALUES (@Slug, @Language, @Title, @Markdown, @CurrentVersion, @IsPublished, @CreatedAt, @UpdatedAt, @UpdatedBy);
        """;

    /// <inheritdoc />
    public string UpdatePageIfVersionSql => """
        UPDATE content_pages
           SET title = @Title, markdown = @Markdown, is_published = @IsPublished,
               current_version = @ExpectedVersion + 1, updated_at = @Now, updated_by = @EditorId
         WHERE slug = @Slug AND language = @Language AND current_version = @ExpectedVersion;
        """;

    /// <inheritdoc />
    public string InsertRevisionSql => """
        INSERT INTO content_page_revisions (page_id, version, title, markdown, change_summary, created_at, created_by)
        VALUES (@PageId, @Version, @Title, @Markdown, @ChangeSummary, @CreatedAt, @CreatedBy);
        """;

    /// <inheritdoc />
    public string SelectPageSql =>
        $"SELECT {PageColumns} FROM content_pages WHERE slug = @Slug AND language = @Language;";

    /// <inheritdoc />
    public string SelectPublishedSql => $"""
        SELECT TOP (1) {PageColumns} FROM content_pages
         WHERE slug = @Slug AND is_published = 1 AND language IN (@Language, @Fallback)
         ORDER BY CASE WHEN language = @Language THEN 0 ELSE 1 END;
        """;

    /// <inheritdoc />
    public string SelectRevisionSql => $"""
        SELECT {RevisionColumns}
          FROM content_page_revisions r JOIN content_pages p ON p.id = r.page_id
         WHERE p.slug = @Slug AND p.language = @Language AND r.version = @Version;
        """;

    /// <inheritdoc />
    public string ListPagesSql => """
        SELECT slug AS Slug, language AS Language, title AS Title, current_version AS CurrentVersion,
               is_published AS IsPublished, updated_at AS UpdatedAt, updated_by AS UpdatedBy
          FROM content_pages
         ORDER BY slug, language
        OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
        """;

    /// <inheritdoc />
    public string CountPagesSql => "SELECT COUNT_BIG(*) FROM content_pages;";

    /// <inheritdoc />
    public string ListRevisionsSql => $"""
        SELECT {RevisionColumns}
          FROM content_page_revisions r JOIN content_pages p ON p.id = r.page_id
         WHERE p.slug = @Slug AND p.language = @Language
         ORDER BY r.version DESC
        OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
        """;

    /// <inheritdoc />
    public string CountRevisionsSql => """
        SELECT COUNT_BIG(*) FROM content_page_revisions r JOIN content_pages p ON p.id = r.page_id
         WHERE p.slug = @Slug AND p.language = @Language;
        """;
}
