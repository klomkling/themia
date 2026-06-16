namespace Themia.Framework.Data.Abstractions.Exceptions;

/// <summary>
/// Provider-specific strategy that classifies a raw database exception. Both data layers (EF Core and
/// Dapper) inject this so a single typed framework exception (<see cref="UniqueConstraintException"/>)
/// surfaces from a unique-violation regardless of engine or driver.
/// </summary>
/// <remarks>
/// The default <see cref="SqlStateUniqueConstraintInterpreter"/> covers any driver that populates the
/// ANSI <c>SQLSTATE</c> on <see cref="System.Data.Common.DbException"/> (PostgreSQL/Npgsql <c>23505</c>,
/// MySQL/MySqlConnector <c>23000</c>). SQL Server's <c>Microsoft.Data.SqlClient</c> does not surface a
/// usable <c>SqlState</c> for engine errors, so its provider package registers an implementation that
/// matches the native error numbers (2627 / 2601).
/// </remarks>
public interface ISqlExceptionInterpreter
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="exception"/> (or any exception in its
    /// inner-exception chain) represents a violated unique or primary-key constraint.
    /// </summary>
    /// <param name="exception">The raw exception thrown by the database call; may be <see langword="null"/>.</param>
    bool IsUniqueConstraintViolation(Exception? exception);
}
