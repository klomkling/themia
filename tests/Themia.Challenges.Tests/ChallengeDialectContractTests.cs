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
    /// <summary>The statements that predicate on <c>tenant_id</c> against <c>@TenantId</c>, per the
    /// SQL each dialect actually emits — <see cref="IChallengeDialect.InsertSql"/> also mentions both
    /// but only in a <c>VALUES</c> list, not a predicate, so it is deliberately excluded here.</summary>
    private static readonly (string Name, Func<IChallengeDialect, string> Select)[] TenantPredicateStatements =
    [
        (nameof(IChallengeDialect.SelectLiveByScopeSql), d => d.SelectLiveByScopeSql),
        (nameof(IChallengeDialect.SelectMostRecentByScopeSql), d => d.SelectMostRecentByScopeSql),
        (nameof(IChallengeDialect.InvalidateLiveForScopeSql), d => d.InvalidateLiveForScopeSql),
        (nameof(IChallengeDialect.IncrementWindowSql), d => d.IncrementWindowSql),
        (nameof(IChallengeDialect.SelectWindowCountsSql), d => d.SelectWindowCountsSql),
        (nameof(IChallengeDialect.DecrementWindowSql), d => d.DecrementWindowSql),
    ];

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
    /// <c>tenant_id</c> is nullable on every table it appears on, and the interface's type-level
    /// remarks state <c>@TenantId</c> may always be <see langword="null"/> (a platform-level
    /// challenge). Plain <c>=</c> never matches <c>NULL</c>, so every predicate on <c>tenant_id</c>
    /// must use one of the three documented null-safe forms instead — this does not error if missed,
    /// it just makes every platform-level row invisible to the query.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllDialects))]
    public void TenantId_ShouldUseNullSafeComparison(IChallengeDialect dialect)
    {
        foreach (var (name, select) in TenantPredicateStatements)
        {
            var sql = select(dialect);

            Assert.True(
                IsNullSafeComparison(sql, "tenant_id", "@TenantId"),
                $"{name} compares tenant_id to @TenantId without one of the documented null-safe forms: {sql}");
        }
    }

    /// <summary>
    /// <c>purpose</c> is nullable only on <c>challenge_rate_windows</c>, and only
    /// <see cref="IChallengeDialect.IncrementWindowSql"/> and
    /// <see cref="IChallengeDialect.DecrementWindowSql"/> document <c>@Purpose</c> as sometimes
    /// <see langword="null"/> (the per-key ceiling row — the layer that bounds the SMS bill). Every
    /// other member's <c>@Purpose</c> is always a concrete purpose string, so plain <c>=</c> there is
    /// correct, not a bug; scoping this assertion to just these two members avoids a false positive
    /// against <see cref="IChallengeDialect.SelectWindowCountsSql"/>'s intentional
    /// <c>purpose = @Purpose</c> per-scope leg.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllDialects))]
    public void Purpose_ShouldUseNullSafeComparison_OnIncrementAndDecrementWindow(IChallengeDialect dialect)
    {
        Assert.True(
            IsNullSafeComparison(dialect.IncrementWindowSql, "purpose", "@Purpose"),
            $"IncrementWindowSql compares purpose to @Purpose without one of the documented null-safe forms: {dialect.IncrementWindowSql}");
        Assert.True(
            IsNullSafeComparison(dialect.DecrementWindowSql, "purpose", "@Purpose"),
            $"DecrementWindowSql compares purpose to @Purpose without one of the documented null-safe forms: {dialect.DecrementWindowSql}");
    }

    private static IEnumerable<(string Name, string Sql)> AllStatements(IChallengeDialect dialect)
    {
        yield return (nameof(IChallengeDialect.InsertSql), dialect.InsertSql);
        yield return (nameof(IChallengeDialect.SelectLiveByScopeSql), dialect.SelectLiveByScopeSql);
        yield return (nameof(IChallengeDialect.SelectLiveByTokenHashSql), dialect.SelectLiveByTokenHashSql);
        yield return (nameof(IChallengeDialect.SelectMostRecentByScopeSql), dialect.SelectMostRecentByScopeSql);
        yield return (nameof(IChallengeDialect.ConsumeSql), dialect.ConsumeSql);
        yield return (nameof(IChallengeDialect.RecordAttemptSql), dialect.RecordAttemptSql);
        yield return (nameof(IChallengeDialect.InvalidateLiveForScopeSql), dialect.InvalidateLiveForScopeSql);
        yield return (nameof(IChallengeDialect.PurgeExpiredSql), dialect.PurgeExpiredSql);
        yield return (nameof(IChallengeDialect.IncrementWindowSql), dialect.IncrementWindowSql);
        yield return (nameof(IChallengeDialect.SelectWindowCountsSql), dialect.SelectWindowCountsSql);
        yield return (nameof(IChallengeDialect.DecrementWindowSql), dialect.DecrementWindowSql);
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
