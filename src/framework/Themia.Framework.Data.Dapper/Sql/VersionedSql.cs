using System.Text.RegularExpressions;
using SqlKata;

namespace Themia.Framework.Data.Dapper.Sql;

/// <summary>Builds the <c>concurrency_version = concurrency_version + 1</c> SET value used by writes that
/// bump an <c>IVersionedEntity</c> without checking its version (set-based updates, soft delete).</summary>
internal static partial class VersionedSql
{
    /// <summary>The increment expression for <paramref name="column"/>. The column comes from the entity
    /// mapping, never from input; it is still restricted to a plain lower-case identifier because the
    /// expression is emitted unquoted (valid and identical on SQL Server, PostgreSQL and MySQL).</summary>
    internal static UnsafeLiteral Increment(string column)
    {
        if (!PlainIdentifier().IsMatch(column))
            throw new InvalidOperationException(
                $"The concurrency column '{column}' must be a plain lower-case identifier (letters, digits, underscore).");
        return new UnsafeLiteral($"{column} + 1");
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex PlainIdentifier();
}
