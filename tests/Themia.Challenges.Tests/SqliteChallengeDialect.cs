using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Themia.Challenges.Tests;

/// <summary>
/// Real-SQLite dialect used to exercise <see cref="Internal.ChallengeService"/>'s orchestration without
/// Docker — the fake-dialect test double the design calls for, backed by an in-memory SQLite database
/// rather than a hand-rolled ADO.NET provider (same pattern as
/// <c>Themia.Exceptional.Tests.SqliteExceptionalDialect</c>). It implements every
/// <see cref="IChallengeDialect"/> statement's documented contract, translated to SQLite's dialect —
/// <c>IS</c>/<c>IS NOT</c> for null-safe equality (SQLite's native null-safe operators, unlike the three
/// shipped engines which each need a different idiom) and a real <c>UNIQUE</c> index plus
/// <c>ON CONFLICT ... DO UPDATE</c> for the window upsert.
/// </summary>
/// <remarks>
/// This proves <see cref="Internal.ChallengeService"/> binds parameters and reads results correctly
/// against a real ADO.NET provider, including that <see cref="IChallengeDialect.ConsumeSql"/>'s guarded
/// <c>UPDATE</c> reports 0 rows affected on a second call. It does <b>not</b> prove true concurrent-race
/// atomicity: every call in these tests runs sequentially against a single process, so two calls never
/// actually race for the same row the way two application instances against Postgres/MySQL/SQL Server
/// could. That proof is Task 8's integration tests against the real engines, per the task brief.
/// </remarks>
internal sealed class SqliteChallengeDialect : IChallengeDialect
{
    private readonly string connectionString;

    static SqliteChallengeDialect()
    {
        // SQLite stores GUIDs and DateTimeOffsets as TEXT; Dapper cannot convert string -> Guid or
        // string -> DateTimeOffset without a type handler for each.
        SqlMapper.AddTypeHandler(new GuidTypeHandler());
        SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
    }

    public SqliteChallengeDialect(string connectionString) => this.connectionString = connectionString;

    private sealed class GuidTypeHandler : SqlMapper.TypeHandler<Guid>
    {
        public override void SetValue(System.Data.IDbDataParameter parameter, Guid value) => parameter.Value = value.ToString();

        public override Guid Parse(object value) => Guid.Parse((string)value);
    }

    private sealed class DateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(System.Data.IDbDataParameter parameter, DateTimeOffset value) =>
            parameter.Value = value.ToString("O");

        public override DateTimeOffset Parse(object value) => DateTimeOffset.Parse((string)value);
    }

    public DbConnection CreateConnection() => new SqliteConnection(connectionString);

    /// <summary>Creates the <c>challenges</c> and <c>challenge_rate_windows</c> tables. Called once per
    /// test against the shared-cache in-memory database via a keep-alive connection.</summary>
    public static string CreateTablesSql => """
        CREATE TABLE IF NOT EXISTS challenges (
            id TEXT PRIMARY KEY,
            tenant_id TEXT NULL,
            key TEXT NOT NULL,
            purpose TEXT NOT NULL,
            secret_hash TEXT NOT NULL,
            secret_salt TEXT NOT NULL,
            token_hash TEXT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            expires_at TEXT NOT NULL,
            created_at TEXT NOT NULL,
            consumed_at TEXT NULL,
            refunded_at TEXT NULL
        );
        CREATE TABLE IF NOT EXISTS challenge_rate_windows (
            id TEXT PRIMARY KEY,
            tenant_id TEXT NULL,
            key TEXT NOT NULL,
            purpose TEXT NULL,
            window_start TEXT NOT NULL,
            count INTEGER NOT NULL DEFAULT 0
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_challenge_rate_windows
            ON challenge_rate_windows (COALESCE(tenant_id, ''), key, COALESCE(purpose, ''), window_start);
        """;

    public string InsertSql => """
        INSERT INTO challenges (id, tenant_id, key, purpose, secret_hash, secret_salt, token_hash, attempts, expires_at, created_at)
        VALUES (@Id, @TenantId, @Key, @Purpose, @SecretHash, @SecretSalt, @TokenHash, @Attempts, @ExpiresAt, @CreatedAt);
        """;

    // No LIMIT: returns every live row for the scope, matching the shipped dialects' contract
    // (IChallengeDialect.SelectLiveByScopeSql) so MaxLiveChallenges > 1 is actually verifiable.
    public string SelectLiveByScopeSql(ChallengeTenancy tenancy) => $"""
        SELECT * FROM challenges
        WHERE {Tenant(tenancy)} AND key = @Key AND purpose = @Purpose
          AND consumed_at IS NULL AND expires_at > @Now
        ORDER BY created_at DESC;
        """;

    public string SelectLiveByTokenHashSql => """
        SELECT * FROM challenges
        WHERE token_hash = @TokenHash AND consumed_at IS NULL AND expires_at > @Now
        ORDER BY created_at DESC LIMIT 1;
        """;

    // No liveness filter at all - used to classify why SelectLiveByScopeSql found nothing (never
    // issued vs. consumed vs. expired). See IChallengeDialect.SelectMostRecentByScopeSql's remarks.
    public string SelectMostRecentByScopeSql(ChallengeTenancy tenancy) => $"""
        SELECT * FROM challenges
        WHERE {Tenant(tenancy)} AND key = @Key AND purpose = @Purpose
        ORDER BY created_at DESC LIMIT 1;
        """;

    public string SelectByIdSql => """SELECT * FROM challenges WHERE id = @Id;""";

    public string MarkRefundedSql => """
        UPDATE challenges SET refunded_at = @Now WHERE id = @Id AND refunded_at IS NULL;
        """;

    public string ConsumeSql => """
        UPDATE challenges SET consumed_at = @ConsumedAt
        WHERE id = @Id AND consumed_at IS NULL AND expires_at > @Now;
        """;

    public string RecordAttemptSql => """
        UPDATE challenges SET attempts = attempts + 1 WHERE id = @Id AND consumed_at IS NULL;
        """;

    public string InvalidateLiveForScopeSql(ChallengeTenancy tenancy) => $"""
        UPDATE challenges SET consumed_at = @ConsumedAt
        WHERE {Tenant(tenancy)} AND key = @Key AND purpose = @Purpose
          AND consumed_at IS NULL AND expires_at > @Now;
        """;

    public string PurgeExpiredSql => """DELETE FROM challenges WHERE rowid IN (SELECT rowid FROM challenges WHERE expires_at < @OlderThan LIMIT @Batch);""";

    public string IncrementWindowSql(RateWindowBucket bucket)
    {
        var (tenant, purpose) = Bucket(bucket);
        return $"""
            INSERT INTO challenge_rate_windows (id, tenant_id, key, purpose, window_start, count)
            VALUES (@Id, @TenantId, @Key, @Purpose, @WindowStart, 1)
            ON CONFLICT (COALESCE(tenant_id, ''), key, COALESCE(purpose, ''), window_start)
            DO UPDATE SET count = count + 1
            RETURNING count;
            """;
    }

    public string DecrementWindowSql(RateWindowBucket bucket)
    {
        var (tenant, purpose) = Bucket(bucket);
        return $"""
            UPDATE challenge_rate_windows SET count = MAX(count - 1, 0)
            WHERE {tenant} AND key = @Key AND {purpose} AND window_start = @WindowStart;
            """;
    }

    private static string Tenant(ChallengeTenancy tenancy) =>
        tenancy == ChallengeTenancy.Tenant ? "tenant_id = @TenantId" : "tenant_id IS NULL";

    private static (string Tenant, string Purpose) Bucket(RateWindowBucket bucket) => bucket switch
    {
        RateWindowBucket.TenantAndPurpose => ("tenant_id = @TenantId", "purpose = @Purpose"),
        RateWindowBucket.TenantAllPurposes => ("tenant_id = @TenantId", "purpose IS NULL"),
        RateWindowBucket.PlatformAndPurpose => ("tenant_id IS NULL", "purpose = @Purpose"),
        RateWindowBucket.PlatformAllPurposes => ("tenant_id IS NULL", "purpose IS NULL"),
        _ => throw new ArgumentOutOfRangeException(nameof(bucket)),
    };

    public string PurgeElapsedWindowsSql => """DELETE FROM challenge_rate_windows WHERE rowid IN (SELECT rowid FROM challenge_rate_windows WHERE window_start < @OlderThan LIMIT @Batch);""";
}
