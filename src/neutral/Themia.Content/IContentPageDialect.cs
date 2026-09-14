using System.Data.Common;

namespace Themia.Content;

/// <summary>
/// Per-database strategy for the content store: a connection, duplicate-key detection, and every SQL statement
/// <see cref="IContentPageService"/> runs against the tables created by <see cref="Migrations.ContentSchemaMigration"/>.
/// Implemented once per engine package; this is those packages' only contact with the core.
/// </summary>
/// <remarks>
/// <para>Every statement is executed by Dapper with named parameters. The parameter names documented on each member
/// are the contract an implementation must bind. Every <c>SELECT</c> aliases each column to its PascalCase name
/// (<c>current_version AS CurrentVersion</c>); nothing relies on Dapper's global underscore matching, which would
/// change the adopter's own mappings.</para>
/// <para><b>Rules live in the service, not here.</b> A dialect never decides whether a save is allowed. In particular
/// <see cref="UpdatePageIfVersionSql"/> must compare against the editor's <c>@ExpectedVersion</c>, never against a
/// value the service read — a <c>WHERE</c> that compares the row with itself refuses nothing (coord #0130 [3]).</para>
/// </remarks>
public interface IContentPageDialect
{
    /// <summary>Creates an unopened connection.</summary>
    DbConnection CreateConnection();

    /// <summary>Whether <paramref name="exception"/> is a unique-constraint violation on this engine.</summary>
    /// <param name="exception">The exception a statement threw.</param>
    bool IsDuplicateKey(DbException exception);

    /// <summary>Inserts a page at version 1. Binds <c>@Slug @Language @Title @Markdown @IsPublished @Now @EditorId</c>;
    /// sets <c>created_at</c> and <c>updated_at</c> to <c>@Now</c>.</summary>
    string InsertPageSql { get; }

    /// <summary>Inserts a page with its history's version and timestamps, for the importer. Binds
    /// <c>@Slug @Language @Title @Markdown @CurrentVersion @IsPublished @CreatedAt @UpdatedAt @UpdatedBy</c>.</summary>
    string ImportPageSql { get; }

    /// <summary>Updates a page only if it is at <c>@ExpectedVersion</c>, setting <c>current_version</c> to
    /// <c>@ExpectedVersion + 1</c>. Binds <c>@Slug @Language @Title @Markdown @IsPublished @ExpectedVersion @Now @EditorId</c>.
    /// The affected row count is 1 when saved and 0 when the page is missing or has moved on.</summary>
    string UpdatePageIfVersionSql { get; }

    /// <summary>Inserts one revision. Binds <c>@PageId @Version @Title @Markdown @ChangeSummary @CreatedAt @CreatedBy</c>.</summary>
    string InsertRevisionSql { get; }

    /// <summary>Selects one page in any publish state. Binds <c>@Slug @Language</c>.</summary>
    string SelectPageSql { get; }

    /// <summary>Selects the published page in <c>@Language</c>, else in <c>@Fallback</c>, as one row at most, preferring
    /// <c>@Language</c>. Binds <c>@Slug @Language @Fallback</c>.</summary>
    string SelectPublishedSql { get; }

    /// <summary>Selects one revision of a page. Binds <c>@Slug @Language @Version</c>.</summary>
    string SelectRevisionSql { get; }

    /// <summary>Selects one page of page summaries ordered by slug then language. Binds <c>@Offset @Limit</c>.</summary>
    string ListPagesSql { get; }

    /// <summary>Counts every page.</summary>
    string CountPagesSql { get; }

    /// <summary>Selects one page of a page's revisions, newest version first. Binds <c>@Slug @Language @Offset @Limit</c>.</summary>
    string ListRevisionsSql { get; }

    /// <summary>Counts a page's revisions. Binds <c>@Slug @Language</c>.</summary>
    string CountRevisionsSql { get; }
}
