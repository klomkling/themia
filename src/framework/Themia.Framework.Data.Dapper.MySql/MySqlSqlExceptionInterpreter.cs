using MySqlConnector;
using Themia.Framework.Data.Abstractions.Exceptions;

namespace Themia.Framework.Data.Dapper.MySql;

/// <summary>
/// MySQL <see cref="ISqlExceptionInterpreter"/>. MySQL reports the generic <c>23000</c> integrity-constraint
/// SQLSTATE class for many faults (foreign-key, NOT-NULL, duplicate-key), so the ANSI-SQLSTATE default would
/// mis-classify those as unique violations. Detection instead matches the native duplicate-key error numbers:
/// 1062 (<see cref="MySqlErrorCode.DuplicateKeyEntry"/>, an unnamed/PK index) and 1586
/// (<see cref="MySqlErrorCode.DuplicateEntryWithKeyName"/>, ER_DUP_ENTRY_WITH_KEY_NAME, a named index).
/// </summary>
internal sealed class MySqlSqlExceptionInterpreter : ISqlExceptionInterpreter
{
    /// <inheritdoc />
    public bool IsUniqueConstraintViolation(Exception? exception) =>
        ExceptionChain.Any(
            exception,
            current => current is MySqlException
            {
                ErrorCode: MySqlErrorCode.DuplicateKeyEntry or MySqlErrorCode.DuplicateEntryWithKeyName,
            });
}
