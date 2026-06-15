using MySqlConnector;
using Themia.Framework.Data.Abstractions.Exceptions;

namespace Themia.Framework.Data.Dapper.MySql;

/// <summary>
/// MySQL <see cref="ISqlExceptionInterpreter"/>. MySQL reports the generic <c>23000</c> integrity-constraint
/// SQLSTATE class for many faults (foreign-key, NOT-NULL, duplicate-key), so the ANSI-SQLSTATE default would
/// mis-classify those as unique violations. Detection instead matches the native duplicate-key error
/// (1062 / <see cref="MySqlErrorCode.DuplicateKeyEntry"/>).
/// </summary>
internal sealed class MySqlSqlExceptionInterpreter : ISqlExceptionInterpreter
{
    /// <inheritdoc />
    public bool IsUniqueConstraintViolation(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry })
            {
                return true;
            }
        }

        return false;
    }
}
