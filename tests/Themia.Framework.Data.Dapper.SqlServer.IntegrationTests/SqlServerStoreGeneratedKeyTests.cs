using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.SqlServer.DependencyInjection;
using Xunit;

namespace Themia.Framework.Data.Dapper.SqlServer.IntegrationTests;

/// <summary>
/// Entity with a store-generated IDENTITY integer key. No assignment — the key is left at 0 so SQL Server
/// generates it and the UoW reads it back via scope_identity().
/// </summary>
public class Gadget : AuditableEntity<int>, ITenantEntity
{
    /// <summary>The owning tenant, stamped by the data layer on insert.</summary>
    public TenantId? TenantId { get; set; }

    /// <summary>The gadget name.</summary>
    public string Name { get; set; } = "";
}

/// <summary>
/// Verifies the SQL Server store-generated-key path: an IDENTITY int key is populated back onto the entity
/// after save via SqlKata's native scope_identity() (store-generated Guid is PostgreSQL-only — no SQL Server
/// RETURNING). SCOPE_IDENTITY returns numeric(38,0) -> decimal, which ConvertKey widens to int. The
/// <c>gadgets</c> table intentionally omits soft-delete columns because <see cref="Gadget"/> extends
/// <see cref="AuditableEntity{TKey}"/>, not <c>SoftDeletableEntity</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqlServerStoreGeneratedKeyTests(SqlServerContainerFixture fixture) : IClassFixture<SqlServerContainerFixture>
{
    [Fact]
    public async Task Add_WithIdentityKey_PopulatesKeyAfterSave()
    {
        await using (var setup = new SqlConnection(fixture.ConnectionString))
        {
            await setup.OpenAsync();
            await using var cmd = setup.CreateCommand();
            cmd.CommandText = """
                IF OBJECT_ID(N'gadgets', N'U') IS NULL
                CREATE TABLE gadgets (
                    id               INT            NOT NULL IDENTITY(1,1) PRIMARY KEY,
                    tenant_id        NVARCHAR(100)  NULL,
                    name             NVARCHAR(200)  NOT NULL,
                    created_at       DATETIME2(7)   NOT NULL,
                    created_by       NVARCHAR(100)  NULL,
                    last_modified_at DATETIME2(7)   NULL,
                    last_modified_by NVARCHAR(100)  NULL
                )
                """;
            await cmd.ExecuteNonQueryAsync();
            // DELETE (not TRUNCATE) keeps the test independent of prior IDENTITY state; the assertion checks != 0.
            cmd.CommandText = "DELETE FROM gadgets";
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
        services.AddThemiaDapperSqlServer(configuration);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Gadget, int>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var gadget = new Gadget { Name = "gadget-zero" };
        Assert.Equal(0, gadget.Id);

        await repo.AddAsync(gadget);
        await uow.SaveChangesAsync();

        Assert.NotEqual(0, gadget.Id);   // populated from scope_identity()

        var fetched = await repo.GetByIdAsync(gadget.Id);
        Assert.NotNull(fetched);
        Assert.Equal("gadget-zero", fetched!.Name);
    }
}
