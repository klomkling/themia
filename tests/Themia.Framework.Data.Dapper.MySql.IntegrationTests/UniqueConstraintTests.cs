using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Conformance;
using Themia.Framework.Data.Dapper.MySql.DependencyInjection;
using Xunit;

namespace Themia.Framework.Data.Dapper.MySql.IntegrationTests;

/// <summary>
/// A duplicate primary key (a unique constraint) must surface as the framework's typed
/// <see cref="UniqueConstraintException"/> from the Dapper peer on MySQL, while a different integrity fault
/// (e.g. a NOT-NULL violation) must NOT. MySQL reports the generic <c>23000</c> SQLSTATE class for both, so
/// detection relies on the MySQL-specific interpreter matching the native duplicate-key error number (1062).
/// </summary>
[Trait("Category", "Integration")]
public sealed class UniqueConstraintTests(MySqlContainerFixture fixture) : IClassFixture<MySqlContainerFixture>
{
    [Fact]
    public async Task Dapper_DuplicatePrimaryKey_ThrowsUniqueConstraintException()
    {
        await fixture.ResetAsync();
        await using var provider = BuildProvider();

        var id = Guid.NewGuid();
        await InsertWidgetAsync(provider, id, "first");

        await Assert.ThrowsAsync<UniqueConstraintException>(() => InsertWidgetAsync(provider, id, "duplicate"));
    }

    /// <summary>
    /// Regression for the 23000-class bug: a NOT-NULL violation shares MySQL's generic <c>23000</c> SQLSTATE
    /// with duplicate keys, but is not a unique violation. It must surface as the raw provider exception, never
    /// as <see cref="UniqueConstraintException"/> (which an insert-and-catch consumer would silently swallow).
    /// </summary>
    [Fact]
    public async Task Dapper_NotNullViolation_DoesNotThrowUniqueConstraintException()
    {
        await fixture.ResetAsync();
        await using var provider = BuildProvider();

        // name is declared NOT NULL; inserting a null name triggers MySQL error 1048 (still SQLSTATE 23000).
        var ex = await Assert.ThrowsAsync<MySqlException>(() => InsertWidgetAsync(provider, Guid.NewGuid(), null!));
        Assert.NotEqual(MySqlErrorCode.DuplicateKeyEntry, ex.ErrorCode);
    }

    /// <summary>
    /// A duplicate on a NAMED unique index (not the PK) is the case MySQL/MariaDB may report as error 1586
    /// (<see cref="MySqlErrorCode.DuplicateEntryWithKeyName"/>, ER_DUP_ENTRY_WITH_KEY_NAME) instead of 1062.
    /// The interpreter must classify whichever code the engine emits as a unique violation, so the typed
    /// <see cref="UniqueConstraintException"/> contract holds for named-index duplicates too.
    /// </summary>
    [Fact]
    public async Task NamedUniqueIndexDuplicate_IsUniqueConstraintViolation()
    {
        await fixture.ResetAsync();
        await using var provider = BuildProvider();
        var interpreter = provider.GetRequiredService<ISqlExceptionInterpreter>();

        await using var connection = new MySqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, "DROP TABLE IF EXISTS named_index_widgets");
        await ExecuteAsync(
            connection,
            "CREATE TABLE named_index_widgets (id INT NOT NULL AUTO_INCREMENT PRIMARY KEY, code VARCHAR(50) NOT NULL, CONSTRAINT uq_named_index_widgets_code UNIQUE (code))");
        await ExecuteAsync(connection, "INSERT INTO named_index_widgets (code) VALUES ('dup')");

        var ex = await Assert.ThrowsAsync<MySqlException>(
            () => ExecuteAsync(connection, "INSERT INTO named_index_widgets (code) VALUES ('dup')"));

        // Engine may emit 1062 or 1586 for a named-index duplicate; both must be classified as unique.
        Assert.True(
            ex.ErrorCode is MySqlErrorCode.DuplicateKeyEntry or MySqlErrorCode.DuplicateEntryWithKeyName,
            $"Expected a duplicate-entry error code, got {ex.ErrorCode}.");
        Assert.True(interpreter.IsUniqueConstraintViolation(ex));
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = fixture.ConnectionString })
            .Build();
        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddThemiaDapperMySql(configuration);
        return services.BuildServiceProvider();
    }

    private static async Task InsertWidgetAsync(IServiceProvider provider, Guid id, string name)
    {
        await using var scope = provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var widget = new Widget { Name = name, Quantity = 1 };
        widget.SetId(id);
        await repo.AddAsync(widget);
        await uow.SaveChangesAsync();
    }
}
