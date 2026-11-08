using Npgsql;

namespace Themia.SecondFactor.PostgreSql;

/// <summary>
/// Holds the data source built from the connection string passed to <c>AddThemiaSecondFactorPostgres</c>, so the stores
/// never resolve an <see cref="NpgsqlDataSource"/> the adopter registered for a different database.
/// </summary>
internal sealed class SecondFactorDataSource(string connectionString) : IAsyncDisposable
{
    public NpgsqlDataSource DataSource { get; } = NpgsqlDataSource.Create(connectionString);

    public ValueTask DisposeAsync() => DataSource.DisposeAsync();
}
