using Themia.Challenges;
using Themia.Challenges.PostgreSql;
using Xunit;

namespace Themia.Challenges.Tests;

/// <summary>
/// Asserts the one part of <see cref="IChallengeDialect"/> that is not just "implement this string" —
/// that <see cref="IChallengeDialect.ConsumeSql"/> is a single conditional statement, not a read
/// followed by a write. A dialect whose <c>ConsumeSql</c> does not carry its own guard makes atomicity
/// the caller's problem, which is exactly how a read-then-write regression gets in later.
/// </summary>
/// <remarks>
/// <see cref="AllDialects"/> carries <see cref="PostgresChallengeDialect"/> as of Task 4, which
/// replaced the illustrative reference stub this theory ran against under Task 3 — a real
/// implementation makes a stub-only case redundant. Tasks 5-6 append the MySQL and SQL Server
/// dialects the same way, one `yield return` per engine, alongside this one.
/// </remarks>
public class ChallengeDialectContractTests
{
    public static IEnumerable<object[]> AllDialects()
    {
        // Never opened by this theory (it only inspects SQL text properties), so an unreachable host
        // is fine here.
        yield return new object[] { new PostgresChallengeDialect("Host=localhost;Database=themia_challenges_test") };
        // Tasks 5-6 append one `yield return new object[] { new <Provider>ChallengeDialect(...) };`
        // per engine as MySQL and SQL Server land.
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
}
