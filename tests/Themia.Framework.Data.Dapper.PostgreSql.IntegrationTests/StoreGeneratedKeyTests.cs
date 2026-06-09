using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.PostgreSql.DependencyInjection;
using Xunit;

namespace Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests;

/// <summary>
/// Entity with a store-generated UUID primary key (DEFAULT gen_random_uuid()).
/// No SetId — the key is left at Guid.Empty so the database generates it.
/// </summary>
public class Gizmo : AuditableEntity<Guid>, ITenantEntity
{
    /// <summary>The owning tenant, stamped by the data layer on insert.</summary>
    public TenantId? TenantId { get; set; }

    /// <summary>The gizmo name.</summary>
    public string Name { get; set; } = "";
}

/// <summary>
/// Focused integration tests for the store-generated-key path in <c>DapperUnitOfWork</c>.
/// Verifies that <c>ConvertKey</c> correctly handles the Guid type returned by Npgsql's RETURNING clause
/// (which was broken with <c>Convert.ChangeType</c> since Guid is not IConvertible).
/// </summary>
[Trait("Category", "Integration")]
public sealed class StoreGeneratedKeyTests(PostgresContainerFixture fixture) : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task Add_WithStoreGeneratedGuidKey_PopulatesKeyAfterSave()
    {
        // Arrange: create the gizmos table (store-generated uuid default)
        await using (var setup = new NpgsqlConnection(fixture.ConnectionString))
        {
            await setup.OpenAsync();
            await using var cmd = setup.CreateCommand();
            cmd.CommandText = """
                CREATE EXTENSION IF NOT EXISTS pgcrypto;
                CREATE TABLE IF NOT EXISTS gizmos (
                    id               uuid          PRIMARY KEY DEFAULT gen_random_uuid(),
                    tenant_id        varchar(100)  NULL,
                    name             varchar(200)  NOT NULL,
                    created_at       timestamptz   NOT NULL,
                    created_by       varchar(100)  NULL,
                    last_modified_at timestamptz   NULL,
                    last_modified_by varchar(100)  NULL
                );
                TRUNCATE TABLE gizmos;
                """;
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
        services.AddThemiaDapperPostgres(configuration);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Gizmo, Guid>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var gizmo = new Gizmo { Name = "widget-zero" };
        Assert.Equal(Guid.Empty, gizmo.Id);

        // Act
        await repo.AddAsync(gizmo);
        await uow.SaveChangesAsync();

        // Assert: store-generated key was written back via KeySetter + ConvertKey
        Assert.NotEqual(Guid.Empty, gizmo.Id);

        var fetched = await repo.GetByIdAsync(gizmo.Id);
        Assert.NotNull(fetched);
        Assert.Equal("widget-zero", fetched.Name);
    }
}
