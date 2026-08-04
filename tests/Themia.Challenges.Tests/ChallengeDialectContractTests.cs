using System.Text.RegularExpressions;
using Themia.Challenges;
using Themia.Challenges.MySql;
using Themia.Challenges.PostgreSql;
using Themia.Challenges.SqlServer;
using Xunit;

namespace Themia.Challenges.Tests;

/// <summary>
/// Asserts the parts of <see cref="IChallengeDialect"/> that are not just "implement this string" —
/// that <see cref="IChallengeDialect.ConsumeSql"/> is a single conditional statement rather than a
/// read followed by a write, that the reserved-word <c>key</c> column is always quoted, and that
/// nullable-column predicates never use a bare <c>=</c> against a parameter that may itself be
/// <see langword="null"/>. A dialect that gets any of these wrong doesn't throw — it silently drops
/// rows (see the type-level remarks on <see cref="IChallengeDialect"/>) or fails to parse on two of
/// the three engines, which is exactly the class of regression a contract test exists to catch before
/// it reaches a real database.
/// </summary>
/// <remarks>
/// <see cref="AllDialects"/> carries <see cref="PostgresChallengeDialect"/> as of Task 4, which
/// replaced the illustrative reference stub this theory ran against under Task 3 — a real
/// implementation makes a stub-only case redundant. <see cref="MySqlChallengeDialect"/> was appended
/// under Task 5 the same way. <see cref="SqlServerChallengeDialect"/> was appended under Task 6,
/// completing all three engines.
/// </remarks>
public class ChallengeDialectContractTests
{
    /// <summary>Every <c>challenges</c> statement that predicates on <c>tenant_id</c>, paired with the
    /// tenancy it was asked for. <see cref="IChallengeDialect.InsertSql"/> mentions the column too, but
    /// only in a <c>VALUES</c> list rather than a predicate, so it is deliberately excluded.</summary>
    private static IEnumerable<(string Name, ChallengeTenancy Tenancy, string Sql)> TenancyStatements(IChallengeDialect d)
    {
        foreach (var tenancy in new[] { ChallengeTenancy.Tenant, ChallengeTenancy.Platform })
        {
            yield return (nameof(IChallengeDialect.SelectLiveByScopeSql), tenancy, d.SelectLiveByScopeSql(tenancy));
            yield return (nameof(IChallengeDialect.SelectMostRecentByScopeSql), tenancy, d.SelectMostRecentByScopeSql(tenancy));
            yield return (nameof(IChallengeDialect.InvalidateLiveForScopeSql), tenancy, d.InvalidateLiveForScopeSql(tenancy));
        }
    }

    /// <summary>Every <c>challenge_rate_windows</c> statement, once per bucket shape.</summary>
    private static IEnumerable<(string Name, RateWindowBucket Bucket, string Sql)> BucketStatements(IChallengeDialect d)
    {
        foreach (var bucket in Enum.GetValues<RateWindowBucket>())
        {
            yield return (nameof(IChallengeDialect.IncrementWindowSql), bucket, d.IncrementWindowSql(bucket));
            yield return (nameof(IChallengeDialect.DecrementWindowSql), bucket, d.DecrementWindowSql(bucket));
        }
    }

    public static IEnumerable<object[]> AllDialects()
    {
        // Never opened by this theory (it only inspects SQL text properties), so an unreachable host
        // is fine here.
        yield return new object[] { new PostgresChallengeDialect("Host=localhost;Database=themia_challenges_test") };
        yield return new object[] { new MySqlChallengeDialect("Server=localhost;Database=themia_challenges_test") };
        yield return new object[] { new SqlServerChallengeDialect("Server=localhost;Database=themia_challenges_test") };
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public void ConsumeSql_ShouldBeConditional(IChallengeDialect dialect)
    {
        var sql = dialect.ConsumeSql;

        Assert.Contains("UPDATE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("consumed_at IS NULL", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>key</c> is a reserved word on MySQL and SQL Server (see the type-level remarks on
    /// <see cref="IChallengeDialect"/>): every reference to the column must be quoted
    /// (<c>"key"</c>/<c>`key`</c>/<c>[key]</c>), or the statement fails to parse on two of the three
    /// engines.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllDialects))]
    public void KeyColumn_ShouldAlwaysBeQuoted(IChallengeDialect dialect)
    {
        foreach (var (name, sql) in AllStatements(dialect))
        {
            // "DUPLICATE KEY UPDATE" is stripped alongside the quoted forms: it is MySQL's own fixed
            // upsert-clause keyword phrase (see MySqlChallengeDialect.IncrementWindowSql), not a
            // reference to the `key` column, so a bare "KEY" inside it is not the defect this theory
            // hunts for.
            var withoutQuotedKey = Regex.Replace(
                sql,
                "\"key\"|`key`|\\[key\\]|DUPLICATE\\s+KEY\\s+UPDATE",
                string.Empty,
                RegexOptions.IgnoreCase);

            // (?<!@) excludes the @Key Dapper parameter name — a legitimate bare "key" that is not
            // the column and needs no quoting.
            Assert.False(
                Regex.IsMatch(withoutQuotedKey, @"(?<!@)\bkey\b", RegexOptions.IgnoreCase),
                $"{name} references an unquoted 'key' column: {sql}");
        }
    }

    /// <summary>
    /// A statement asked for a <see langword="null"/> shape must say <c>IS NULL</c> and must not compare
    /// that column to its parameter at all.
    /// </summary>
    /// <remarks>
    /// This replaces an earlier theory that required the null-safe comparison forms
    /// (<c>IS NOT DISTINCT FROM</c> / <c>&lt;=&gt;</c> / the OR-guard) on every one of these statements.
    /// Those were correct and are now wrong: all three are non-sargable, so the indexes
    /// <c>ChallengeSchemaMigration</c> creates could never be seeked through them — measured on
    /// PostgreSQL 16 over 200 000 rows as a sequential scan at 16.2 ms where the shape-specific
    /// predicate is an index scan at 0.042 ms, on a statement <c>IssueAsync</c> runs two or three times
    /// per call. The SQL is now selected per shape instead (<see cref="ChallengeTenancy"/>,
    /// <see cref="RateWindowBucket"/>).
    /// <para>
    /// The defect the old theory guarded is unchanged and still guarded here, just expressed for the new
    /// shape: a <see langword="null"/> shape that emits <c>column = @Param</c> matches zero rows in
    /// silence — no error, every platform-level challenge and every per-key ceiling row simply invisible.
    /// The per-key ceiling is what bounds an SMS bill, so getting it wrong disables that protection with
    /// nothing to see in a log.
    /// </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllDialects))]
    public void NullShapes_ShouldTestForNull_NeverCompareToTheParameter(IChallengeDialect dialect)
    {
        foreach (var (name, tenancy, sql) in TenancyStatements(dialect))
        {
            AssertShape(sql, "tenant_id", "@TenantId", isNull: tenancy == ChallengeTenancy.Platform, $"{name}/{tenancy}");
        }

        foreach (var (name, bucket, sql) in BucketStatements(dialect))
        {
            var tenantIsNull = bucket is RateWindowBucket.PlatformAndPurpose or RateWindowBucket.PlatformAllPurposes;
            var purposeIsNull = bucket is RateWindowBucket.TenantAllPurposes or RateWindowBucket.PlatformAllPurposes;

            AssertShape(sql, "tenant_id", "@TenantId", tenantIsNull, $"{name}/{bucket}");
            AssertShape(sql, "purpose", "@Purpose", purposeIsNull, $"{name}/{bucket}");
        }
    }

    /// <summary>
    /// No statement may keep a null-safe comparison form. They are the exact forms that defeat the
    /// indexes, so one surviving in a dialect is the regression this whole change exists to prevent —
    /// and it would be invisible, since the SQL stays correct and only the plan degrades.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllDialects))]
    public void NoStatement_ShouldUseANonSargableNullSafeComparison(IChallengeDialect dialect)
    {
        var all = TenancyStatements(dialect).Select(x => ($"{x.Name}/{x.Tenancy}", x.Sql))
            .Concat(BucketStatements(dialect).Select(x => ($"{x.Name}/{x.Bucket}", x.Sql)));

        foreach (var (name, sql) in all)
        {
            Assert.DoesNotContain("IS NOT DISTINCT FROM", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<=>", sql, StringComparison.Ordinal);
            Assert.False(
                Regex.IsMatch(sql, @"IS\s+NULL\s+AND\s+@\w+\s+IS\s+NULL", RegexOptions.IgnoreCase),
                $"{name} still carries the SQL Server OR-guard, which is non-sargable: {sql}");
        }
    }

    // A column whose shape is NULL must be tested with IS NULL and never compared to its parameter;
    // a column whose shape is bound must be compared and never tested for NULL.
    private static void AssertShape(string sql, string column, string parameter, bool isNull, string what)
    {
        var comparesToParameter = Regex.IsMatch(sql, $@"\b{column}\s*=\s*{Regex.Escape(parameter)}\b", RegexOptions.IgnoreCase);
        var testsForNull = Regex.IsMatch(sql, $@"\b{column}\s+IS\s+NULL\b", RegexOptions.IgnoreCase);

        if (isNull)
        {
            Assert.True(testsForNull, $"{what}: {column} shape is NULL but the statement never tests {column} IS NULL: {sql}");
            Assert.False(comparesToParameter, $"{what}: {column} shape is NULL but the statement compares {column} = {parameter}, which matches zero rows in silence: {sql}");
        }
        else
        {
            Assert.True(comparesToParameter, $"{what}: {column} shape is bound but the statement never compares {column} = {parameter}: {sql}");
        }
    }

    private static IEnumerable<(string Name, string Sql)> AllStatements(IChallengeDialect dialect)
    {
        yield return (nameof(IChallengeDialect.InsertSql), dialect.InsertSql);
        yield return (nameof(IChallengeDialect.SelectLiveByScopeSql), dialect.SelectLiveByScopeSql(ChallengeTenancy.Tenant));
        yield return (nameof(IChallengeDialect.SelectLiveByScopeSql), dialect.SelectLiveByScopeSql(ChallengeTenancy.Platform));
        yield return (nameof(IChallengeDialect.SelectLiveByTokenHashSql), dialect.SelectLiveByTokenHashSql);
        yield return (nameof(IChallengeDialect.SelectMostRecentByScopeSql), dialect.SelectMostRecentByScopeSql(ChallengeTenancy.Tenant));
        yield return (nameof(IChallengeDialect.SelectMostRecentByScopeSql), dialect.SelectMostRecentByScopeSql(ChallengeTenancy.Platform));
        yield return (nameof(IChallengeDialect.ConsumeSql), dialect.ConsumeSql);
        yield return (nameof(IChallengeDialect.RecordAttemptSql), dialect.RecordAttemptSql);
        yield return (nameof(IChallengeDialect.InvalidateLiveForScopeSql), dialect.InvalidateLiveForScopeSql(ChallengeTenancy.Tenant));
        yield return (nameof(IChallengeDialect.PurgeExpiredSql), dialect.PurgeExpiredSql);
        foreach (var bucket in Enum.GetValues<RateWindowBucket>())
        {
            yield return (nameof(IChallengeDialect.IncrementWindowSql), dialect.IncrementWindowSql(bucket));
            yield return (nameof(IChallengeDialect.DecrementWindowSql), dialect.DecrementWindowSql(bucket));
        }
        yield return (nameof(IChallengeDialect.PurgeElapsedWindowsSql), dialect.PurgeElapsedWindowsSql);
    }

    /// <summary>
    /// True when <paramref name="sql"/> compares <paramref name="column"/> to <paramref name="param"/>
    /// using one of the three forms documented on <see cref="IChallengeDialect"/>'s type-level remarks:
    /// PostgreSQL's <c>IS NOT DISTINCT FROM</c>, MySQL's <c>&lt;=&gt;</c>, or SQL Server's
    /// <c>(a = b OR (a IS NULL AND b IS NULL))</c> guard (or its <c>EXISTS ... INTERSECT</c>
    /// equivalent). Checked as "one of these idioms is present", not "a bare '=' is absent", because
    /// the SQL Server guard form legitimately contains a bare <c>column = param</c> as half of its
    /// <c>OR</c> — banning that substring outright would fail the exact form the interface docs
    /// recommend for that engine.
    /// </summary>
    private static bool IsNullSafeComparison(string sql, string column, string param)
    {
        var col = Regex.Escape(column);
        var par = Regex.Escape(param);
        string[] idioms =
        [
            $@"{col}\s+IS\s+NOT\s+DISTINCT\s+FROM\s+{par}\b", // PostgreSQL
            $@"{col}\s*<=>\s*{par}\b", // MySQL
            $@"\(\s*{col}\s*=\s*{par}\s+OR\s+\(\s*{col}\s+IS\s+NULL\s+AND\s+{par}\s+IS\s+NULL\s*\)\s*\)", // SQL Server guard
            $@"EXISTS\s*\(\s*SELECT\s+{col}\s+INTERSECT\s+SELECT\s+{par}\s*\)", // SQL Server INTERSECT
        ];

        return idioms.Any(pattern => Regex.IsMatch(sql, pattern, RegexOptions.IgnoreCase));
    }
}
