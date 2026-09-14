using System.Data.Common;
using MySqlConnector;

namespace Themia.Content.MySql;

/// <summary>MySQL 8.0.13+ implementation of <see cref="IContentPageDialect"/> (MySqlConnector).</summary>
/// <remarks>
/// <para><b>Affected rows.</b> MySqlConnector reports found rows, not changed rows, by default. The service relies on
/// the count from <see cref="UpdatePageIfVersionSql"/>; the two agree here because the statement always changes
/// <c>current_version</c>.</para>
/// <para><b>Time zone.</b> <c>DATETIME(6)</c> stores no offset. The connection is pinned to
/// <see cref="MySqlDateTimeKind.Utc"/> so a value read back is the instant that was written on any host time zone;
/// <c>ContentSaveTests.Timestamps_ShouldRoundTripAsTheSameInstant</c> run under a non-UTC <c>TZ</c> is the proof.</para>
/// </remarks>
public sealed class MySqlContentDialect : IContentPageDialect
{
    private const string PageColumns =
        "id AS Id, slug AS Slug, language AS Language, title AS Title, markdown AS Markdown, " +
        "current_version AS CurrentVersion, is_published AS IsPublished, created_at AS CreatedAt, " +
        "updated_at AS UpdatedAt, updated_by AS UpdatedBy";

    private const string RevisionColumns =
        "r.version AS Version, r.title AS Title, r.markdown AS Markdown, r.change_summary AS ChangeSummary, " +
        "r.created_at AS CreatedAt, r.created_by AS CreatedBy";

    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    /// <param name="connectionString">MySQL connection string.</param>
    public MySqlContentDialect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = new MySqlConnectionStringBuilder(connectionString) { DateTimeKind = MySqlDateTimeKind.Utc }
            .ConnectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection() => new MySqlConnection(connectionString);

    /// <inheritdoc />
    public bool IsDuplicateKey(DbException exception) =>
        exception is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry };

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
        SELECT {PageColumns} FROM content_pages
         WHERE slug = @Slug AND is_published = TRUE AND language IN (@Language, @Fallback)
         ORDER BY CASE WHEN language = @Language THEN 0 ELSE 1 END
         LIMIT 1;
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
         LIMIT @Limit OFFSET @Offset;
        """;

    /// <inheritdoc />
    public string CountPagesSql => "SELECT COUNT(*) FROM content_pages;";

    /// <inheritdoc />
    public string ListRevisionsSql => $"""
        SELECT {RevisionColumns}
          FROM content_page_revisions r JOIN content_pages p ON p.id = r.page_id
         WHERE p.slug = @Slug AND p.language = @Language
         ORDER BY r.version DESC
         LIMIT @Limit OFFSET @Offset;
        """;

    /// <inheritdoc />
    public string CountRevisionsSql => """
        SELECT COUNT(*) FROM content_page_revisions r JOIN content_pages p ON p.id = r.page_id
         WHERE p.slug = @Slug AND p.language = @Language;
        """;
}
