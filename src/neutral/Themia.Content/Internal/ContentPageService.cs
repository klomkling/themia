using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Themia.Content.Internal;

/// <summary>The one implementation of the content rules, over any <see cref="IContentPageDialect"/>.</summary>
/// <remarks>
/// <para><b>One connection, one transaction, READ COMMITTED.</b> MySQL's default REPEATABLE READ reads a plain
/// <c>SELECT</c> from the snapshot taken at the transaction's first read; revert reads before its guarded
/// <c>UPDATE</c>, so the version it reported in a conflict was stale (measured on mysql 8.4.9).</para>
/// <para><b>Create guard:</b> the unique index on (slug, language), behind a savepoint so a lost race leaves the
/// transaction able to read the winner's version. <b>Update guard:</b> <c>current_version = @ExpectedVersion</c> in the
/// dialect's <c>UPDATE</c>. Neither is a read-then-compare.</para>
/// <para><b>Savepoints are used without checking <see cref="DbTransaction.SupportsSavepoints"/></b>, which
/// MySqlConnector and SqlClient leave <see langword="false"/> although both implement <c>Save</c> and
/// <c>Rollback(string)</c>.</para>
/// </remarks>
internal sealed class ContentPageService : IContentPageService
{
    internal const int MaxPageSize = 100;

    private const string CreateSavepoint = "themia_content_create";

    private readonly IContentPageDialect dialect;
    private readonly ContentOptions options;
    private readonly TimeProvider time;
    private readonly ILogger<ContentPageService> logger;

    public ContentPageService(IContentPageDialect dialect, ContentOptions options, TimeProvider time, ILogger<ContentPageService> logger)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        this.dialect = dialect;
        this.options = options;
        this.time = time;
        this.logger = logger;
    }

    public async Task<ContentPage?> GetPublishedAsync(string slug, string? language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(slug);

        var requested = ContentLanguage.Normalise(language);
        if (!options.IsConfigured(requested))
        {
            requested = options.NormalisedFallback;
        }

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<ContentPageRow>(new CommandDefinition(
            dialect.SelectPublishedSql,
            new { Slug = slug, Language = requested, Fallback = options.NormalisedFallback },
            cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToPage();
    }

    public async Task<ContentPage?> GetForEditAsync(string slug, string language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(slug);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var row = await SelectPageAsync(connection, null, new PageKey(slug, ContentLanguage.Normalise(language)), ct).ConfigureAwait(false);
        return row?.ToPage();
    }

    public async Task<PagedResult<ContentPageSummary>> ListAsync(int page, int limit, CancellationToken ct = default)
    {
        ValidatePaging(page, limit);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ContentPageSummaryRow>(new CommandDefinition(
            dialect.ListPagesSql, new { Offset = (page - 1) * limit, Limit = limit }, cancellationToken: ct)).ConfigureAwait(false);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            dialect.CountPagesSql, cancellationToken: ct)).ConfigureAwait(false);
        return new PagedResult<ContentPageSummary> { Items = rows.Select(r => r.ToSummary()).ToList(), Total = checked((int)total) };
    }

    public async Task<PagedResult<ContentPageRevision>> GetRevisionsAsync(
        string slug, string language, int page, int limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ValidatePaging(page, limit);

        var key = new PageKey(slug, ContentLanguage.Normalise(language));
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ContentRevisionRow>(new CommandDefinition(
            dialect.ListRevisionsSql,
            new { key.Slug, key.Language, Offset = (page - 1) * limit, Limit = limit },
            cancellationToken: ct)).ConfigureAwait(false);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            dialect.CountRevisionsSql, new { key.Slug, key.Language }, cancellationToken: ct)).ConfigureAwait(false);
        return new PagedResult<ContentPageRevision> { Items = rows.Select(r => r.ToRevision()).ToList(), Total = checked((int)total) };
    }

    public async Task<ContentSaveResult> SaveAsync(ContentPageSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);

        var errors = ContentPageValidator.Validate(save, options);
        if (errors.Count > 0)
        {
            return Logged(ContentSaveResult.Invalid(errors), save.Slug, save.Language, "save");
        }

        var key = new PageKey(save.Slug, ContentLanguage.Normalise(save.Language));
        var content = new PageContent(save.Title, save.Markdown, save.IsPublished);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        var result = save.ExpectedVersion == 0
            ? await CreateAsync(connection, transaction, key, content, save.ChangeSummary, save.EditorId, ct).ConfigureAwait(false)
            : await UpdateAsync(connection, transaction, key, content, save.ExpectedVersion, save.ChangeSummary, save.EditorId, ct).ConfigureAwait(false);

        await FinishAsync(transaction, result, ct).ConfigureAwait(false);
        return Logged(result, key.Slug, key.Language, "save");
    }

    public async Task<ContentSaveResult> RevertAsync(ContentPageRevert revert, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(revert);

        var errors = ContentPageValidator.Validate(revert, options);
        if (errors.Count > 0)
        {
            return Logged(ContentSaveResult.Invalid(errors), revert.Slug, revert.Language, "revert");
        }

        // The key carries the language into the save below. A revert that loses a key dimension writes one
        // language's body over another's row (coord #0130 [3]).
        var key = new PageKey(revert.Slug, ContentLanguage.Normalise(revert.Language));

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        var target = await connection.QuerySingleOrDefaultAsync<ContentRevisionRow>(new CommandDefinition(
            dialect.SelectRevisionSql,
            new { key.Slug, key.Language, Version = revert.TargetVersion },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);
        var current = target is null ? null : await SelectPageAsync(connection, transaction, key, ct).ConfigureAwait(false);

        var result = target is null || current is null
            ? ContentSaveResult.NotFound()
            : await UpdateAsync(
                connection,
                transaction,
                key,
                new PageContent(target.Title, target.Markdown, current.IsPublished),
                revert.ExpectedVersion,
                revert.ChangeSummary ?? $"Reverted to version {revert.TargetVersion}",
                revert.EditorId,
                ct).ConfigureAwait(false);

        await FinishAsync(transaction, result, ct).ConfigureAwait(false);
        return Logged(result, key.Slug, key.Language, "revert");
    }

    private async Task<ContentSaveResult> CreateAsync(
        DbConnection connection, DbTransaction transaction, PageKey key, PageContent content,
        string? changeSummary, string? editorId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await transaction.SaveAsync(CreateSavepoint, ct).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                dialect.InsertPageSql,
                new { key.Slug, key.Language, content.Title, content.Markdown, content.IsPublished, Now = now, EditorId = editorId },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (DbException exception) when (dialect.IsDuplicateKey(exception))
        {
            await transaction.RollbackAsync(CreateSavepoint, ct).ConfigureAwait(false);
            var existing = await SelectPageAsync(connection, transaction, key, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Inserting content page {key.Slug}/{key.Language} reported a duplicate key, but no such page could be read.");
            return ContentSaveResult.Conflict(existing.CurrentVersion, existing.UpdatedBy, existing.UpdatedAt);
        }

        var page = await SelectPageAsync(connection, transaction, key, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Content page {key.Slug}/{key.Language} was inserted but cannot be read back.");
        await InsertRevisionAsync(connection, transaction, page, changeSummary, now, editorId, ct).ConfigureAwait(false);
        return ContentSaveResult.Saved(page.ToPage());
    }

    private async Task<ContentSaveResult> UpdateAsync(
        DbConnection connection, DbTransaction transaction, PageKey key, PageContent content, int expectedVersion,
        string? changeSummary, string? editorId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            dialect.UpdatePageIfVersionSql,
            new
            {
                key.Slug,
                key.Language,
                content.Title,
                content.Markdown,
                content.IsPublished,
                ExpectedVersion = expectedVersion,
                Now = now,
                EditorId = editorId,
            },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        var page = await SelectPageAsync(connection, transaction, key, ct).ConfigureAwait(false);

        if (updated == 0)
        {
            return page is null
                ? ContentSaveResult.NotFound()
                : ContentSaveResult.Conflict(page.CurrentVersion, page.UpdatedBy, page.UpdatedAt);
        }

        if (page is null)
        {
            throw new InvalidOperationException($"Content page {key.Slug}/{key.Language} was updated but cannot be read back.");
        }

        await InsertRevisionAsync(connection, transaction, page, changeSummary, now, editorId, ct).ConfigureAwait(false);
        return ContentSaveResult.Saved(page.ToPage());
    }

    private Task InsertRevisionAsync(
        DbConnection connection, DbTransaction transaction, ContentPageRow page, string? changeSummary,
        DateTimeOffset now, string? editorId, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            dialect.InsertRevisionSql,
            new
            {
                PageId = page.Id,
                Version = page.CurrentVersion,
                page.Title,
                page.Markdown,
                ChangeSummary = changeSummary,
                CreatedAt = now,
                CreatedBy = editorId,
            },
            transaction,
            cancellationToken: ct));

    private Task<ContentPageRow?> SelectPageAsync(DbConnection connection, DbTransaction? transaction, PageKey key, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<ContentPageRow>(new CommandDefinition(
            dialect.SelectPageSql, new { key.Slug, key.Language }, transaction, cancellationToken: ct));

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = dialect.CreateConnection();
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task FinishAsync(DbTransaction transaction, ContentSaveResult result, CancellationToken ct)
    {
        if (result.Succeeded)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
        }
    }

    private static void ValidatePaging(int page, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, int.MaxValue / MaxPageSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPageSize);
    }

    // Slug, language and outcome only. A markdown body is author content and is never logged.
    private ContentSaveResult Logged(ContentSaveResult result, string? slug, string? language, string operation)
    {
        logger.LogInformation(
            "Content page {Slug}/{Language} {Operation} ended {Outcome}", slug, language, operation, result.Outcome);
        return result;
    }

    private readonly record struct PageContent(string Title, string Markdown, bool IsPublished);
}
