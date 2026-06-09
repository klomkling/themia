using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.MySql.DependencyInjection;
using Xunit;

namespace Themia.Framework.Data.Dapper.MySql.IntegrationTests;

/// <summary>
/// Entity with a store-generated AUTO_INCREMENT integer key. No assignment — the key is left at 0 so MySQL
/// generates it and the UoW reads it back via LAST_INSERT_ID().
/// </summary>
public class Gadget : AuditableEntity<int>, ITenantEntity
{
    /// <summary>The owning tenant, stamped by the data layer on insert.</summary>
    public TenantId? TenantId { get; set; }

    /// <summary>The gadget name.</summary>
    public string Name { get; set; } = "";
}

/// <summary>
/// Verifies the MySQL store-generated-key path: an AUTO_INCREMENT int key is populated back onto the entity
/// after save via SqlKata's native LAST_INSERT_ID() (store-generated UUID is PostgreSQL-only — no MySQL RETURNING).
/// </summary>
[Trait("Category", "Integration")]
public sealed class MySqlStoreGeneratedKeyTests(MySqlContainerFixture fixture) : IClassFixture<MySqlContainerFixture>
{
    [Fact]
    public async Task Add_WithAutoIncrementKey_PopulatesKeyAfterSave()
    {
        await using (var setup = new MySqlConnection(fixture.ConnectionString))
        {
            await setup.OpenAsync();
            await using var cmd = setup.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS gadgets (
                    id               BIGINT        NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    tenant_id        VARCHAR(100)  NULL,
                    name             VARCHAR(200)  NOT NULL,
                    created_at       DATETIME(6)   NOT NULL,
                    created_by       VARCHAR(100)  NULL,
                    last_modified_at DATETIME(6)   NULL,
                    last_modified_by VARCHAR(100)  NULL
                )
                """;
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = "TRUNCATE TABLE gadgets";
            await cmd.ExecuteNonQueryAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = fixture.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddThemiaDapperMySql(configuration);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Gadget, int>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var gadget = new Gadget { Name = "gadget-zero" };
        Assert.Equal(0, gadget.Id);

        await repo.AddAsync(gadget);
        await uow.SaveChangesAsync();

        Assert.NotEqual(0, gadget.Id);   // populated from LAST_INSERT_ID()

        var fetched = await repo.GetByIdAsync(gadget.Id);
        Assert.NotNull(fetched);
        Assert.Equal("gadget-zero", fetched!.Name);
    }
}
