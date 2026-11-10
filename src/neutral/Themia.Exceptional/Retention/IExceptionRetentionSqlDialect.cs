namespace Themia.Exceptional.Retention;

/// <summary>
/// Batched purge SQL for the retention sweep. Separate from <see cref="IExceptionalSqlDialect"/> so existing custom
/// dialects keep compiling; retention needs a provider that implements this too.
/// </summary>
public interface IExceptionRetentionSqlDialect
{
    /// <summary>
    /// Deletes at most @BatchSize unprotected rows whose <c>CreationDate</c> is before @OlderThan, oldest first where the
    /// engine allows, and returns the affected row count. @OlderThan is bound by
    /// <see cref="IExceptionalSqlDialect.BuildPurgeParameters"/>; @BatchSize is an integer.
    /// </summary>
    string PurgeBatchSql { get; }
}
