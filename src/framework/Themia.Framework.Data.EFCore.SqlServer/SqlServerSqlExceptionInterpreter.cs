using Microsoft.Data.SqlClient;
using Themia.Framework.Data.Abstractions.Exceptions;

namespace Themia.Framework.Data.EFCore.SqlServer;

/// <summary>
/// SQL Server <see cref="ISqlExceptionInterpreter"/>. <c>Microsoft.Data.SqlClient.SqlException</c> does not
/// surface a usable <c>SqlState</c> for engine errors, so detection matches the native error numbers:
/// 2627 (violation of a UNIQUE/PRIMARY KEY constraint) and 2601 (duplicate key in a unique index). EF Core
/// wraps the driver exception in a <c>DbUpdateException</c>, so the chain is walked to the inner cause.
/// </summary>
internal sealed class SqlServerSqlExceptionInterpreter : ISqlExceptionInterpreter
{
    /// <summary>Violation of a UNIQUE or PRIMARY KEY constraint.</summary>
    private const int UniqueConstraintViolation = 2627;

    /// <summary>Cannot insert duplicate key row in a unique index.</summary>
    private const int DuplicateKeyInUniqueIndex = 2601;

    /// <inheritdoc />
    public bool IsUniqueConstraintViolation(Exception? exception) =>
        ExceptionChain.Any(
            exception,
            current => current is SqlException { Number: UniqueConstraintViolation or DuplicateKeyInUniqueIndex });
}
