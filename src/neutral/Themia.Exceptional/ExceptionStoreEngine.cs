using System.Data;
using Dapper;

namespace Themia.Exceptional;

/// <summary>Dapper-backed <see cref="IExceptionStore"/>. All DB-specific SQL comes from the injected dialect.</summary>
public sealed class ExceptionStoreEngine : IExceptionStore
{
    private readonly IExceptionalSqlDialect dialect;
    private readonly TimeSpan rollupPeriod;

    /// <summary>Creates the engine over <paramref name="dialect"/> using <paramref name="options"/>.</summary>
    /// <param name="dialect">Provider strategy.</param>
    /// <param name="options">Configuration including the rollup window. Must not be <see langword="null"/>.</param>
    public ExceptionStoreEngine(IExceptionalSqlDialect dialect, ExceptionalOptions options)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RollupPeriod, TimeSpan.Zero);
        this.dialect = dialect;
        this.rollupPeriod = options.RollupPeriod;
    }

    /// <inheritdoc />
    public async Task LogAsync(ExceptionEntry entry, CancellationToken cancellationToken = default)
    {
        // Normalize DateTime kinds to Utc so Npgsql accepts them as timestamptz without throwing.
        entry.CreationDate = ToUtc(entry.CreationDate);
        entry.LastLogDate = ToUtc(entry.LastLogDate);
        if (entry.DeletionDate is { } del) entry.DeletionDate = ToUtc(del);

        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ErrorHash);

        // UPDATE-then-(if-0)-INSERT is intentionally non-transactional. Under concurrent logging of
        // the same ErrorHash, two callers can both see 0 rows updated and both insert, yielding
        // split rollup rows. This is the accepted tradeoff: no data loss, no hot-path lock, and
        // duplicates naturally merge into the next rollup window.
        await using var connection = dialect.CreateConnection();
        var rolledUp = await connection.ExecuteAsync(new CommandDefinition(
            dialect.RollupSql, dialect.BuildRollupParameters(entry, rollupPeriod), cancellationToken: cancellationToken));

        if (rolledUp == 0)
            await connection.ExecuteAsync(new CommandDefinition(
                dialect.InsertSql, dialect.BuildInsertParameters(entry), cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task<ExceptionEntry?> GetAsync(Guid guid, CancellationToken cancellationToken = default)
    {
        await using var connection = dialect.CreateConnection();
        var entry = await connection.QuerySingleOrDefaultAsync<ExceptionEntry>(
            new CommandDefinition(dialect.GetByGuidSql, new { Guid = guid }, cancellationToken: cancellationToken));
        return entry is null ? null : NormalizeKinds(entry);
    }

    /// <inheritdoc />
    public async Task<PagedResult<ExceptionEntry>> ListAsync(ExceptionFilter filter, CancellationToken cancellationToken = default)
    {
        await using var connection = dialect.CreateConnection();
        var pageSize = Math.Clamp(filter.PageSize, 1, 1000);
        var args = ToArgs(filter);
        args.Add("Offset", (Math.Max(1, filter.Page) - 1) * pageSize);
        args.Add("PageSize", pageSize);

        var items = (await connection.QueryAsync<ExceptionEntry>(
            new CommandDefinition(dialect.ListSql, args, cancellationToken: cancellationToken))).AsList();
        foreach (var item in items)
            NormalizeKinds(item);
        var total = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(dialect.CountSql, args, cancellationToken: cancellationToken));

        return new PagedResult<ExceptionEntry> { Items = items, Total = total };
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(ExceptionFilter filter, CancellationToken cancellationToken = default)
    {
        await using var connection = dialect.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(dialect.CountSql, ToArgs(filter), cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public Task<bool> ProtectAsync(Guid guid, CancellationToken cancellationToken = default)
        => ExecuteAffectsRow(dialect.ProtectSql, new { Guid = guid }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteAsync(Guid guid, CancellationToken cancellationToken = default)
        => ExecuteAffectsRow(dialect.SoftDeleteSql, dialect.BuildSoftDeleteParameters(guid, DateTime.UtcNow), cancellationToken);

    /// <inheritdoc />
    public Task<bool> HardDeleteAsync(Guid guid, CancellationToken cancellationToken = default)
        => ExecuteAffectsRow(dialect.HardDeleteSql, new { Guid = guid }, cancellationToken);

    /// <inheritdoc />
    public async Task<int> PurgeAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = dialect.CreateConnection();
        return await connection.ExecuteAsync(new CommandDefinition(
            dialect.PurgeSql, dialect.BuildPurgeParameters(ToUtc(olderThanUtc)), cancellationToken: cancellationToken));
    }

    private async Task<bool> ExecuteAffectsRow(string sql, object args, CancellationToken cancellationToken)
    {
        await using var connection = dialect.CreateConnection();
        return await connection.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: cancellationToken)) > 0;
    }

    private DynamicParameters ToArgs(ExceptionFilter filter)
    {
        // Explicit DbType on nullable string parameters is required for Npgsql 6+: when a
        // value is null Npgsql cannot infer the PostgreSQL column type and throws "could not determine
        // data type". Temporal binding is delegated to the dialect so each provider uses the correct DbType.
        var args = new DynamicParameters();
        args.Add("ApplicationName", filter.ApplicationName, DbType.String);
        args.Add("TenantId", filter.TenantId, DbType.String);
        dialect.AddTemporalFilters(args, ToUtc(filter.From), ToUtc(filter.To));
        args.Add("Search", string.IsNullOrWhiteSpace(filter.Search) ? null : $"%{filter.Search}%", DbType.String);
        args.Add("IncludeDeleted", filter.IncludeDeleted);
        return args;
    }

    // Converts to UTC by Kind: Local is converted (ToUniversalTime), Unspecified is assumed UTC
    // (dashboard/dates are UTC wall-clock). SpecifyKind alone would mis-label a Local value and skew the instant.
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static DateTime? ToUtc(DateTime? value) => value is { } v ? ToUtc(v) : null;

    // The store always persists UTC instants, but tz-naive columns (MySQL DATETIME, SqlServer datetime2)
    // materialize as Kind=Unspecified while Postgres timestamptz returns Kind=Utc. Label read-back values
    // as Utc so all engines return consistent Kind (callers can safely use the value as a UTC instant).
    private static ExceptionEntry NormalizeKinds(ExceptionEntry entry)
    {
        entry.CreationDate = DateTime.SpecifyKind(entry.CreationDate, DateTimeKind.Utc);
        entry.LastLogDate = DateTime.SpecifyKind(entry.LastLogDate, DateTimeKind.Utc);
        if (entry.DeletionDate is { } del) entry.DeletionDate = DateTime.SpecifyKind(del, DateTimeKind.Utc);
        return entry;
    }
}
