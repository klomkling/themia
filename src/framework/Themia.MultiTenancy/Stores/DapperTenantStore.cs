using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.Stores;

/// <summary>
/// Tenant store backed by a relational database using Dapper.
/// Assumes a table with columns: id, identifier, name, environment, connection_string, properties (json optional).
/// </summary>
public sealed class DapperTenantStore : ITenantStore
{
    private static readonly Regex TableNameRegex = new(@"^[a-zA-Z_][a-zA-Z0-9_\.]*$", RegexOptions.Compiled);
    private readonly Func<CancellationToken, Task<IDbConnection>> _connectionFactory;
    private readonly string _tableName;

    /// <summary>
    /// Initializes a new instance of the <see cref="DapperTenantStore"/> class.
    /// </summary>
    public DapperTenantStore(Func<CancellationToken, Task<IDbConnection>> connectionFactory, string tableName = "tenants")
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

        if (string.IsNullOrWhiteSpace(tableName))
        {
            throw new ArgumentException("Table name is required", nameof(tableName));
        }

        if (!TableNameRegex.IsMatch(tableName))
        {
            throw new ArgumentException(
                "Table name contains invalid characters. Only letters, numbers, underscores, and dots are allowed.",
                nameof(tableName));
        }

        _tableName = tableName;
    }

    /// <inheritdoc />
    public async Task<TenantInfo?> FindByIdentifierAsync(string identifier, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier cannot be null or whitespace", nameof(identifier));
        }

        using var connection = await _connectionFactory(cancellationToken).ConfigureAwait(false);
        var sql = BuildCatalogQuery(_tableName);

        var command = new CommandDefinition(sql, new { identifier }, cancellationToken: cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync(command).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        string id = row.id;
        string ident = row.identifier;
        string? name = row.name;
        string? env = row.environment;
        string? conn = row.connection_string;

        return new TenantInfo(id, ident, name, env, conn);
    }

    /// <summary>
    /// Builds the catalog lookup query the store executes. Carries no engine-specific row-limit
    /// clause (no LIMIT, no TOP) so it stays portable across SQL Server, MySQL, and PostgreSQL.
    /// Single source of truth shared by <see cref="FindByIdentifierAsync"/> and the portability guard test.
    /// </summary>
    internal static string BuildCatalogQuery(string tableName) =>
        $"SELECT id, identifier, name, environment, connection_string FROM {tableName} WHERE identifier = @identifier";
}
