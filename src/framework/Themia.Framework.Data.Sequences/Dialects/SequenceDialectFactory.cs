namespace Themia.Framework.Data.Sequences.Dialects;

/// <summary>Resolves the <see cref="ISequenceDialect"/> for a <see cref="SequenceEngine"/>.</summary>
public static class SequenceDialectFactory
{
    /// <summary>Returns the dialect for <paramref name="engine"/>.</summary>
    /// <param name="engine">The configured engine.</param>
    /// <returns>The dialect.</returns>
    /// <exception cref="NotSupportedException"><paramref name="engine"/> is not a supported engine.</exception>
    public static ISequenceDialect For(SequenceEngine engine) => engine switch
    {
        SequenceEngine.Unspecified => throw new NotSupportedException(
            "SequenceOptions.Engine was never set. Choose PostgreSQL, MySQL or SQL Server, or supply "
            + "a custom ISequenceDialect."),

        SequenceEngine.Postgres => new PostgresSequenceDialect(),
        SequenceEngine.MySql => new MySqlSequenceDialect(),
        SequenceEngine.SqlServer => new SqlServerSequenceDialect(),

        // Exhaustive on purpose: a new engine must break this build rather than fall into a default and
        // silently allocate against the wrong SQL.
        _ => throw new NotSupportedException(
            $"Themia sequences do not support engine '{engine}'. Supported: PostgreSQL, MySQL 8.0.13+, "
            + "SQL Server. Supply a custom ISequenceDialect for anything else."),
    };
}
