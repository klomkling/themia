using Dapper;

namespace Themia.Exceptional.Retention;

internal sealed class DapperRetentionPurger : IRetentionPurger
{
    private readonly IExceptionalSqlDialect dialect;
    private readonly IExceptionRetentionSqlDialect retentionDialect;

    public DapperRetentionPurger(IExceptionalSqlDialect dialect)
    {
        this.dialect = dialect;
        retentionDialect = dialect as IExceptionRetentionSqlDialect
            ?? throw new InvalidOperationException(
                $"Exceptional retention needs a dialect that implements {nameof(IExceptionRetentionSqlDialect)}; {dialect.GetType().Name} does not.");
    }

    public async Task<int> PurgeBatchAsync(DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken)
    {
        var args = dialect.BuildPurgeParameters(DateTime.SpecifyKind(olderThanUtc, DateTimeKind.Utc));
        args.Add("BatchSize", batchSize);
        await using var connection = dialect.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(new CommandDefinition(
            retentionDialect.PurgeBatchSql, args, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
