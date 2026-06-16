using System.Data.Common;

namespace Themia.Framework.Data.Abstractions.Exceptions;

/// <summary>
/// Default, provider-agnostic <see cref="ISqlExceptionInterpreter"/> that detects a unique-constraint
/// violation from the ANSI <c>SQLSTATE</c> on <see cref="DbException"/>. Covers PostgreSQL/Npgsql
/// (<c>23505</c>) and any driver that reports the <em>specific</em> <c>23505</c> unique-violation SQLSTATE.
/// MySQL is handled by a provider-specific interpreter, because MySQL reports the generic <c>23000</c>
/// integrity-constraint class (shared by foreign-key, NOT-NULL and other faults) rather than a
/// unique-specific SQLSTATE. Keeps the base data packages free of any concrete-provider reference.
/// </summary>
public sealed class SqlStateUniqueConstraintInterpreter : ISqlExceptionInterpreter
{
    /// <summary>SQLSTATE for a unique-index/unique-constraint violation (PostgreSQL and the SQL standard).</summary>
    private const string UniqueViolation = "23505";

    /// <inheritdoc />
    public bool IsUniqueConstraintViolation(Exception? exception) =>
        ExceptionChain.Any(exception, current => current is DbException { SqlState: UniqueViolation });
}
