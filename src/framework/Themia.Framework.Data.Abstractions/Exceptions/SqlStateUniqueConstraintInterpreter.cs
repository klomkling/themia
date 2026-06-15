using System.Data.Common;

namespace Themia.Framework.Data.Abstractions.Exceptions;

/// <summary>
/// Default, provider-agnostic <see cref="ISqlExceptionInterpreter"/> that detects a unique-constraint
/// violation from the ANSI <c>SQLSTATE</c> on <see cref="DbException"/>. Covers any driver that populates
/// <see cref="DbException.SqlState"/>: PostgreSQL/Npgsql (<c>23505</c>) and MySQL/MySqlConnector
/// (<c>23000</c>, the generic integrity-constraint class MySQL reports for duplicate keys). Keeps the base
/// data packages free of any concrete-provider reference.
/// </summary>
public sealed class SqlStateUniqueConstraintInterpreter : ISqlExceptionInterpreter
{
    /// <summary>SQLSTATE for a unique-index/unique-constraint violation (PostgreSQL and the SQL standard).</summary>
    private const string UniqueViolation = "23505";

    /// <summary>SQLSTATE class MySQL reports for a duplicate-key (error 1062) integrity violation.</summary>
    private const string IntegrityConstraintViolation = "23000";

    /// <inheritdoc />
    public bool IsUniqueConstraintViolation(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: UniqueViolation or IntegrityConstraintViolation })
            {
                return true;
            }
        }

        return false;
    }
}
