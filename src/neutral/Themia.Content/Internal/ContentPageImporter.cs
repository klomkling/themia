using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Themia.Content.Internal;

/// <summary>The one implementation of <see cref="IContentPageImporter"/>.</summary>
internal sealed class ContentPageImporter : IContentPageImporter
{
    private readonly IContentPageDialect dialect;
    private readonly ContentOptions options;
    private readonly ILogger<ContentPageImporter> logger;

    public ContentPageImporter(IContentPageDialect dialect, ContentOptions options, ILogger<ContentPageImporter> logger)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        this.dialect = dialect;
        this.options = options;
        this.logger = logger;
    }

    public async Task<ContentImportResult> ImportAsync(IReadOnlyList<ContentPageImport> pages, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        EnsureNoNullFields(pages);

        var pagesFailing = pages.Count(p => ContentMarkdownRules.Check(p.Markdown ?? string.Empty).Count > 0);
        var revisionsFailing = pages.SelectMany(p => p.Revisions ?? []).Count(r => ContentMarkdownRules.Check(r.Markdown ?? string.Empty).Count > 0);

        var violations = Validate(pages);
        if (violations.Count > 0)
        {
            logger.LogWarning("Content import refused with {ViolationCount} violations; nothing was written", violations.Count);
            return ContentImportResult.Invalid(violations, pagesFailing, revisionsFailing);
        }

        await using var connection = dialect.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        var existing = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            dialect.CountPagesSql, transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);
        if (existing > 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            logger.LogWarning("Content import refused: the content tables already hold {PageCount} pages", existing);
            return ContentImportResult.TargetNotEmpty();
        }

        var revisionCount = 0;
        foreach (var page in pages)
        {
            var language = ContentLanguage.Normalise(page.Language);
            await connection.ExecuteAsync(new CommandDefinition(
                dialect.ImportPageSql,
                new
                {
                    page.Slug,
                    Language = language,
                    page.Title,
                    page.Markdown,
                    page.CurrentVersion,
                    page.IsPublished,
                    CreatedAt = page.CreatedAt.ToUniversalTime(),
                    UpdatedAt = page.UpdatedAt.ToUniversalTime(),
                    page.UpdatedBy,
                },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);

            var row = await connection.QuerySingleAsync<ContentPageRow>(new CommandDefinition(
                dialect.SelectPageSql, new { page.Slug, Language = language }, transaction, cancellationToken: ct)).ConfigureAwait(false);

            foreach (var revision in page.Revisions.OrderBy(r => r.Version))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    dialect.InsertRevisionSql,
                    new
                    {
                        PageId = row.Id,
                        revision.Version,
                        revision.Title,
                        revision.Markdown,
                        revision.ChangeSummary,
                        CreatedAt = revision.CreatedAt.ToUniversalTime(),
                        revision.CreatedBy,
                    },
                    transaction,
                    cancellationToken: ct)).ConfigureAwait(false);
                revisionCount++;
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Content import wrote {PageCount} pages and {RevisionCount} revisions", pages.Count, revisionCount);
        return ContentImportResult.Imported(pages.Count, revisionCount, pagesFailing, revisionsFailing);
    }

    private List<ContentImportViolation> Validate(IReadOnlyList<ContentPageImport> pages)
    {
        var violations = new List<ContentImportViolation>();

        foreach (var group in pages.GroupBy(p => (p.Slug, Language: ContentLanguage.Normalise(p.Language))).Where(g => g.Count() > 1))
        {
            violations.Add(new ContentImportViolation(group.Key.Slug, group.Key.Language, ContentImportRule.DuplicateKey));
        }

        foreach (var page in pages)
        {
            Add(page, violations, ValidateOne(page));
        }

        return violations;
    }

    private IEnumerable<ContentImportRule> ValidateOne(ContentPageImport page)
    {
        if (!ContentPageValidator.IsValidSlug(page.Slug) || !options.IsConfigured(ContentLanguage.Normalise(page.Language)))
        {
            yield return ContentImportRule.InvalidSlugOrLanguage;
        }

        var revisions = page.Revisions ?? [];
        if (page.Title?.Length > ContentPageValidator.MaxTitleLength
            || page.UpdatedBy?.Length > ContentPageValidator.MaxEditorIdLength
            || revisions.Any(r => r.Title?.Length > ContentPageValidator.MaxTitleLength
                || r.ChangeSummary?.Length > ContentPageValidator.MaxChangeSummaryLength
                || r.CreatedBy?.Length > ContentPageValidator.MaxEditorIdLength))
        {
            yield return ContentImportRule.FieldTooLong;
        }

        var versions = revisions.Select(r => r.Version).OrderBy(v => v).ToList();
        if (page.CurrentVersion < 1 || !versions.SequenceEqual(Enumerable.Range(1, page.CurrentVersion)))
        {
            yield return ContentImportRule.NonContiguousRevisions;
            yield break;
        }

        var current = revisions.Single(r => r.Version == page.CurrentVersion);
        if (!string.Equals(current.Title, page.Title, StringComparison.Ordinal)
            || !string.Equals(current.Markdown, page.Markdown, StringComparison.Ordinal))
        {
            yield return ContentImportRule.ContentDiffersFromCurrentRevision;
        }
    }

    private static void Add(ContentPageImport page, List<ContentImportViolation> violations, IEnumerable<ContentImportRule> rules) =>
        violations.AddRange(rules.Select(rule => new ContentImportViolation(page.Slug, page.Language, rule)));

    /// <summary>Throws when a page, a revision, or a required text field is missing. These are programmer errors —
    /// a null here means the caller built its rows wrong — not import rules, so they are refused before any rule
    /// is checked and before any connection is opened.</summary>
    private static void EnsureNoNullFields(IReadOnlyList<ContentPageImport> pages)
    {
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            if (page is null)
            {
                throw new ArgumentException($"pages[{i}] is null", nameof(pages));
            }

            if (page.Title is null)
            {
                throw new ArgumentException($"pages[{i}].Title is null", nameof(pages));
            }

            if (page.Markdown is null)
            {
                throw new ArgumentException($"pages[{i}].Markdown is null", nameof(pages));
            }

            var revisions = page.Revisions;
            if (revisions is null)
            {
                continue;
            }

            for (var j = 0; j < revisions.Count; j++)
            {
                var revision = revisions[j];
                if (revision is null)
                {
                    throw new ArgumentException($"pages[{i}].Revisions[{j}] is null", nameof(pages));
                }

                if (revision.Title is null)
                {
                    throw new ArgumentException($"pages[{i}].Revisions[{j}].Title is null", nameof(pages));
                }

                if (revision.Markdown is null)
                {
                    throw new ArgumentException($"pages[{i}].Revisions[{j}].Markdown is null", nameof(pages));
                }
            }
        }
    }
}
