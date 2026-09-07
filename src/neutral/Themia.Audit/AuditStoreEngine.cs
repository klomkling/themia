using System.Data;
using System.Data.Common;
using Dapper;

namespace Themia.Audit;

/// <summary>
/// Dapper-backed <see cref="IAuditStore"/>. All DB-specific SQL comes from the injected
/// <see cref="IAuditDialect"/>; this engine never opens a connection itself (design §7) — every
/// method runs against the caller's own <see cref="DbConnection"/>.
/// </summary>
public sealed class AuditStoreEngine : IAuditStore
{
    // Registered once, process-wide, the first time this engine is touched. Without it, reading
    // occurred_at back on MySQL corrupts the instant: MySqlConnector returns DATETIME(6) as a
    // Kind=Unspecified DateTime, and the .NET explicit DateTime -> DateTimeOffset conversion operator
    // treats an Unspecified Kind as LOCAL time, stamping the row with whatever UTC offset the process
    // happens to be running under instead of zero. PostgreSQL (Npgsql, timestamptz) and SQL Server
    // (datetimeoffset) are unaffected by this handler: Parse below passes an already-correct
    // DateTimeOffset straight through, and re-labelling an already-Kind=Utc DateTime as UTC is a no-op —
    // so this is safe to register even though all three engines share one Dapper-global type registry in
    // this project's tests. SetValue is an unmodified passthrough: writes already round-trip correctly
    // on every engine without a handler, so this only changes how a value already in hand is *read*.
    static AuditStoreEngine()
    {
        SqlMapper.AddTypeHandler(new OccurredAtTypeHandler());
    }

    // Portable on every engine: event_uid carries a unique index (see AuditSchemaMigration), so no
    // LIMIT/TOP is needed to guarantee at most one row, and plain column names need no engine-specific
    // quoting. Kept out of IAuditDialect because it needs no per-engine variation.
    private const string GetByEventUidSql = """
        SELECT id AS Id, event_uid AS EventUid, tenant_id AS TenantId, category AS Category,
               event_type AS EventType, outcome AS Outcome, actor_id AS ActorId, actor_name AS ActorName,
               entity_type AS EntityType, entity_id AS EntityId, occurred_at AS OccurredAt,
               ip_address AS IpAddress, user_agent AS UserAgent, correlation_id AS CorrelationId,
               reason AS Reason, data AS Data
        FROM themia_audit_events
        WHERE event_uid = @EventUid;
        """;

    private readonly IAuditDialect dialect;

    /// <summary>Creates the engine over <paramref name="dialect"/>.</summary>
    public AuditStoreEngine(IAuditDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        this.dialect = dialect;
    }

    /// <inheritdoc />
    public async Task<long> WriteAsync(AuditEntry entry, DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(connection);

        // Truncate framework-captured fields, then reject anything still over its column width —
        // Normalize() before Validate() so a caller cannot fail on an over-length UserAgent/IpAddress,
        // only on the adopter-named fields Validate() never truncates.
        var normalized = entry.Normalize();
        normalized.Validate();

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            dialect.InsertSql, ToInsertParameters(normalized), transaction, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task<PagedResult<AuditEntry>> QueryAsync(AuditQuery query, DbConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(connection);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 1000);
        var args = ToFilterParameters(query);

        // Computed in `long`: `page` is clamped only at 1, not at any upper bound, so `(page - 1) *
        // pageSize` in `int` arithmetic overflows past ~2.147B and wraps negative for a large page/
        // pageSize combination, which every engine rejects as an invalid OFFSET/LIMIT. The `long` result
        // can never itself be negative here (page - 1 >= 0, pageSize >= 1), but it is clamped at zero
        // anyway so the intent reads at the call site rather than relying on that invariant silently.
        var skip = Math.Max(0L, (long)(page - 1) * pageSize);
        args.Add("Skip", skip);
        args.Add("Take", pageSize);

        var rows = (await connection.QueryAsync<AuditEntryRow>(new CommandDefinition(
            dialect.SelectPageSql, args, cancellationToken: cancellationToken))).AsList();

        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            dialect.CountSql, args, cancellationToken: cancellationToken));

        return new PagedResult<AuditEntry>
        {
            Items = rows.Select(ToEntry).ToList(),
            Total = total,
        };
    }

    /// <inheritdoc />
    public async Task<AuditEntry?> GetAsync(Guid eventUid, DbConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var row = await connection.QuerySingleOrDefaultAsync<AuditEntryRow>(new CommandDefinition(
            GetByEventUidSql, new { EventUid = eventUid }, cancellationToken: cancellationToken));

        return row is null ? null : ToEntry(row);
    }

    /// <inheritdoc />
    public async Task<int> PurgeAsync(DateTimeOffset olderThan, DbConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return await connection.ExecuteAsync(new CommandDefinition(
            dialect.PurgeSql, new { OlderThan = olderThan }, cancellationToken: cancellationToken));
    }

    private static DynamicParameters ToInsertParameters(AuditEntry entry)
    {
        var p = new DynamicParameters();
        p.Add("EventUid", entry.EventUid);
        p.Add("TenantId", entry.TenantId);
        p.Add("Category", (short)entry.Category);
        p.Add("EventType", entry.EventType);
        p.Add("Outcome", (short)entry.Outcome);
        p.Add("ActorId", entry.ActorId);
        p.Add("ActorName", entry.ActorName);
        p.Add("EntityType", entry.EntityType);
        p.Add("EntityId", entry.EntityId);
        p.Add("OccurredAt", entry.OccurredAt);
        p.Add("IpAddress", entry.IpAddress);
        p.Add("UserAgent", entry.UserAgent);
        p.Add("CorrelationId", entry.CorrelationId);
        p.Add("Reason", entry.Reason);
        p.Add("Data", entry.Data);
        return p;
    }

    // Explicit DbType on every parameter — required for Npgsql 6+: when a filter value is null, Npgsql
    // cannot infer the PostgreSQL type of a "@Param IS NULL OR column = @Param"-shaped comparison the
    // way it can for a plain INSERT target column, and throws "could not determine data type of
    // parameter" instead. Mirrors Themia.Exceptional.ExceptionStoreEngine.ToArgs.
    private static DynamicParameters ToFilterParameters(AuditQuery query)
    {
        var p = new DynamicParameters();
        p.Add("TenantId", query.TenantId, DbType.String);
        p.Add("HostLevelOnly", query.HostLevelOnly, DbType.Boolean);
        p.Add("ActorId", query.ActorId, DbType.String);
        p.Add("EntityType", query.EntityType, DbType.String);
        p.Add("EntityId", query.EntityId, DbType.String);
        p.Add("Category", (short?)query.Category, DbType.Int16);
        p.Add("Outcome", (short?)query.Outcome, DbType.Int16);
        p.Add("From", query.From, DbType.DateTimeOffset);
        p.Add("To", query.To, DbType.DateTimeOffset);
        return p;
    }

    // AuditEntry.Data is `internal init`: callers outside this assembly can never set it (§8's "the
    // recorder serializes, callers never supply a string" invariant), but AuditStoreEngine lives in the
    // same assembly, so materializing a row read back from storage is allowed to set it here.
    private static AuditEntry ToEntry(AuditEntryRow row) => new()
    {
        EventUid = row.EventUid,
        TenantId = row.TenantId,
        Category = (AuditCategory)row.Category,
        EventType = row.EventType,
        Outcome = (AuditOutcome)row.Outcome,
        ActorId = row.ActorId,
        ActorName = row.ActorName,
        EntityType = row.EntityType,
        EntityId = row.EntityId,
        OccurredAt = row.OccurredAt,
        IpAddress = row.IpAddress,
        UserAgent = row.UserAgent,
        CorrelationId = row.CorrelationId,
        Reason = row.Reason,
        Data = row.Data,
    };

    /// <summary>
    /// Row mapping is BY NAME — every column in <see cref="GetByEventUidSql"/> and each dialect's
    /// <see cref="IAuditDialect.SelectPageSql"/> is aliased to match one of these property names exactly
    /// — never by ordinal position. Five consecutive nullable strings
    /// (<see cref="ActorId"/>/<see cref="ActorName"/>/<see cref="EntityType"/>/<see cref="EntityId"/>/<see cref="Reason"/>)
    /// make a positional transposition invisible; this is the defect that shipped in 0.21.4's outbox
    /// dialects.
    /// </summary>
    private sealed class AuditEntryRow
    {
        public long Id { get; set; }

        public Guid EventUid { get; set; }

        public string? TenantId { get; set; }

        public short Category { get; set; }

        public string EventType { get; set; } = string.Empty;

        public short Outcome { get; set; }

        public string? ActorId { get; set; }

        public string? ActorName { get; set; }

        public string? EntityType { get; set; }

        public string? EntityId { get; set; }

        public DateTimeOffset OccurredAt { get; set; }

        public string? IpAddress { get; set; }

        public string? UserAgent { get; set; }

        public string? CorrelationId { get; set; }

        public string? Reason { get; set; }

        public string? Data { get; set; }
    }

    // See the static constructor above for why this exists.
    private sealed class OccurredAtTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => throw new InvalidCastException(
                $"Cannot convert '{value?.GetType().Name ?? "null"}' to {nameof(DateTimeOffset)}."),
        };

        // Unmodified passthrough: every engine already accepts a DateTimeOffset parameter correctly
        // without a handler, so writing is left exactly as Dapper's own default would do it.
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) => parameter.Value = value;
    }
}
