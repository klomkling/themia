using Npgsql;
using Themia.Data.Migrations.PostgreSql;
using Xunit;

namespace Themia.Data.Migrations.Tests;

/// <summary>
/// Npgsql sets <c>IsTransient</c> on some <see cref="PostgresException"/> server answers (55P03, 40001,
/// 40P01, 53300) that a new connection does not cure, so the adapter decides by SQLSTATE instead.
/// </summary>
public class PostgresTransientFailureTests
{
    private readonly PostgresMigrationEngineAdapter _adapter = new();

    [Theory]
    [InlineData("08000")]
    [InlineData("08001")]
    [InlineData("08003")]
    [InlineData("08006")]
    [InlineData("57P01")]
    [InlineData("57P03")]
    public void IsTransientFailure_IsTrue_ForConnectionClassServerStates(string sqlState) =>
        Assert.True(_adapter.IsTransientFailure(Server(sqlState)));

    [Theory]
    [InlineData("55P03")]
    [InlineData("40001")]
    [InlineData("40P01")]
    [InlineData("53300")]
    [InlineData("57014")]
    [InlineData("57P02")]
    [InlineData("28P01")]
    [InlineData("3D000")]
    public void IsTransientFailure_IsFalse_ForOtherServerStates(string sqlState) =>
        Assert.False(_adapter.IsTransientFailure(Server(sqlState)));

    [Fact]
    public void IsTransientFailure_IsFalse_ForAServerErrorWrappedByANpgsqlException() =>
        Assert.False(_adapter.IsTransientFailure(new NpgsqlException("wrapped", Server("55P03"))));

    [Fact]
    public void IsTransientFailure_IsTrue_ForANpgsqlExceptionTheDriverFlagsTransient() =>
        Assert.True(_adapter.IsTransientFailure(new NpgsqlException("dropped", new IOException("reset"))));

    [Fact]
    public void IsTransientFailure_IsFalse_ForANpgsqlExceptionTheDriverDoesNotFlag() =>
        Assert.False(_adapter.IsTransientFailure(new NpgsqlException("other", new InvalidOperationException())));

    [Fact]
    public void IsTransientFailure_IsFalse_ForAnUnrelatedException() =>
        Assert.False(_adapter.IsTransientFailure(new InvalidOperationException()));

    private static PostgresException Server(string sqlState) =>
        new("server error", "ERROR", "ERROR", sqlState);
}
