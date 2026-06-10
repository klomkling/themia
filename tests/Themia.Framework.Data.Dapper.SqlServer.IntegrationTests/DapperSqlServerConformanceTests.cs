using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.Conformance;
using Themia.Framework.Data.Dapper.SqlServer.DependencyInjection;
using Xunit;

namespace Themia.Framework.Data.Dapper.SqlServer.IntegrationTests;

/// <summary>Runs the shared data-layer contract against the Dapper-on-MySQL provider.</summary>
[Trait("Category", "Integration")]
public sealed class DapperSqlServerConformanceTests(SqlServerContainerFixture fixture)
    : DataLayerConformanceTests, IClassFixture<SqlServerContainerFixture>
{
    /// <inheritdoc />
    protected override Task<ConformanceScope> NewScopeAsync(TenantId? tenant)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = fixture.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext(tenant));
        services.AddThemiaDapperSqlServer(configuration);

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();

        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var filter = scope.ServiceProvider.GetRequiredService<IDataFilterScope>();

        return Task.FromResult(new ConformanceScope(provider, scope, repo, uow, filter));
    }

    /// <inheritdoc />
    protected override Task ResetAsync() => fixture.ResetAsync();

    /// <summary>
    /// Audit user columns (CreatedBy on insert, LastModifiedBy on update) are stamped from the ambient
    /// <see cref="ICurrentUserAccessor"/> by the Dapper unit of work.
    /// </summary>
    [Fact]
    public async Task AuditUser_IsStamped_OnInsertAndUpdate()
    {
        await ResetAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = fixture.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddThemiaDapperSqlServer(configuration);
        services.AddSingleton<ICurrentUserAccessor>(new StubCurrentUser("user-42"));   // override the null default
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var widget = new Widget { Name = "audited", Quantity = 1 };
        widget.SetId(Guid.NewGuid());
        await repo.AddAsync(widget);
        await uow.SaveChangesAsync();
        Assert.Equal("user-42", (await repo.GetByIdAsync(widget.Id))!.CreatedBy);

        widget.Quantity = 2;
        repo.Update(widget);
        await uow.SaveChangesAsync();
        Assert.Equal("user-42", (await repo.GetByIdAsync(widget.Id))!.LastModifiedBy);
    }

    /// <summary>
    /// A no-tenant (system) scope cannot soft-delete a tenant-owned row: Dapper scopes the soft-delete to
    /// global (tenant_id IS NULL) rows when no tenant is ambient, so the cross-tenant delete matches 0 rows
    /// and throws <see cref="ConcurrencyException"/>. Parity with the PostgreSQL/MySQL integration projects.
    /// </summary>
    [Fact]
    public async Task NoTenantScope_CannotSoftDelete_TenantOwnedRow()
    {
        await ResetAsync();

        Guid id;
        await using (var a = await NewScopeAsync(new TenantId("a")))
        {
            var w = new Widget { Name = "owned", Quantity = 1 };
            w.SetId(Guid.NewGuid());
            id = w.Id;
            await a.Repo.AddAsync(w);
            await a.Uow.SaveChangesAsync();
        }

        await using (var system = await NewScopeAsync(null))
        {
            var detached = new Widget { Name = "owned", Quantity = 1 };
            detached.SetId(id);
            system.Repo.Remove(detached);
            // WHERE id = @id AND tenant_id IS NULL matches 0 rows, so the cross-tenant delete fails loud.
            await Assert.ThrowsAsync<ConcurrencyException>(() => system.Uow.SaveChangesAsync());
        }

        await using var check = await NewScopeAsync(new TenantId("a"));
        Assert.NotNull(await check.Repo.GetByIdAsync(id));   // the tenant row survived the cross-tenant delete attempt
    }

    /// <summary>
    /// The <c>DateTimeOffset</c> ⇄ <c>datetime2(7)</c> handler round-trips a known UTC instant with microsecond
    /// precision and re-labels it UTC — guarding against offset corruption (e.g. on a non-UTC agent) and
    /// sub-second precision loss that the existence-only audit facts would not catch.
    /// </summary>
    [Fact]
    public async Task AuditTimestamp_RoundTripsUtc_WithMicrosecondPrecision()
    {
        await ResetAsync();

        // A fixed UTC instant whose fractional part is exactly microsecond-aligned (6_543_210 ticks = 654_321 µs).
        var stamped = new DateTimeOffset(2026, 6, 10, 9, 8, 7, TimeSpan.Zero).AddTicks(6_543_210);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = fixture.ConnectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(stamped));   // stamps audit fields deterministically
        services.AddThemiaDapperSqlServer(configuration);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Widget, Guid>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var widget = new Widget { Name = "ts", Quantity = 1 };
        widget.SetId(Guid.NewGuid());
        await repo.AddAsync(widget);
        await uow.SaveChangesAsync();

        var loaded = await repo.GetByIdAsync(widget.Id);
        Assert.NotNull(loaded);
        Assert.Equal(TimeSpan.Zero, loaded!.CreatedAt.Offset);   // the handler re-labels the stored value UTC
        var deltaTicks = Math.Abs((loaded.CreatedAt.UtcDateTime - stamped.UtcDateTime).Ticks);
        Assert.True(deltaTicks <= TimeSpan.TicksPerMicrosecond,
            $"CreatedAt round-trip lost precision: expected ~{stamped.UtcDateTime:O}, got {loaded.CreatedAt.UtcDateTime:O}");
    }

    private sealed class StubCurrentUser(string? userId) : ICurrentUserAccessor
    {
        public string? UserId { get; } = userId;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
