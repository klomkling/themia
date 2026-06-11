using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;
using Xunit;

namespace Themia.Framework.Data.EFCore.SqlServer.IntegrationTests.Naming;

/// <summary>
/// Verifies the naming split: Themia framework columns are snake_case; adopter-declared columns
/// keep EF SQL Server defaults (PascalCase). Schema is built via <c>EnsureCreatedAsync</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerIntegrationCollection.Name)]
public class NamingConventionTests : IClassFixture<NamingConventionTests.SqlServerFixture>
{
    private readonly SqlServerFixture fixture;

    public NamingConventionTests(SqlServerFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task FrameworkColumns_AreSnakeCase()
    {
        var columns = await fixture.GetColumnNamesAsync();

        // Framework-owned columns — always snake_case regardless of provider.
        Assert.Contains("tenant_id", columns);
        Assert.Contains("created_at", columns);
        Assert.Contains("created_by", columns);
        Assert.Contains("last_modified_at", columns);
        Assert.Contains("last_modified_by", columns);
        Assert.Contains("is_deleted", columns);
        Assert.Contains("deleted_at", columns);
        Assert.Contains("deleted_by", columns);
        Assert.Contains("restored_at", columns);
        Assert.Contains("restored_by", columns);
        Assert.Contains("row_version", columns);
        Assert.Contains("id", columns);
    }

    [Fact]
    public async Task AdopterColumn_IsPascalCase_NotSnakeCase()
    {
        var columns = await fixture.GetColumnNamesAsync();

        // Adopter-declared property "Name" is NOT global-snake-cased on SQL Server.
        Assert.Contains("Name", columns);
        Assert.DoesNotContain("name", columns);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    public sealed class SqlServerFixture : IAsyncLifetime
    {
        private readonly SharedSqlServerContainerFixture sharedContainer;
        private string connectionString = string.Empty;

        public SqlServerFixture(SharedSqlServerContainerFixture sharedContainer)
        {
            this.sharedContainer = sharedContainer;
        }

        public async Task InitializeAsync()
        {
            connectionString = sharedContainer.GetConnectionString("ef_naming");
            await using var ctx = CreateContext();
            await ctx.Database.EnsureCreatedAsync();
        }

        public Task DisposeAsync() => Task.CompletedTask;

        public TestWidgetDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<TestWidgetDbContext>()
                .UseSqlServer(connectionString)
                .Options);

        /// <summary>
        /// Returns the column names for the Widgets table as reported by INFORMATION_SCHEMA.
        /// </summary>
        public async Task<IReadOnlyList<string>> GetColumnNamesAsync()
        {
            await using var ctx = CreateContext();
            var tableName = ctx.Model
                .FindEntityType(typeof(Widget))!
                .GetTableName()!;

            var columns = await ctx.Database
                .SqlQuery<string>($"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = {tableName}")
                .ToListAsync();

            return columns;
        }
    }

    // ── Test context ──────────────────────────────────────────────────────────

    public class TestWidgetDbContext : ThemiaDbContext
    {
        public TestWidgetDbContext(DbContextOptions options)
            : base(options, null, null)
        {
        }

        public DbSet<Widget> Widgets => Set<Widget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Widget>(entity =>
            {
                entity.HasKey(w => w.Id);
                entity.Property(w => w.Name).IsRequired().HasMaxLength(200);
            });
            base.OnModelCreating(modelBuilder);
        }
    }

    // ── Entity: exercises all four framework markers ──────────────────────────

    public class Widget : SoftDeletableEntity<int>, ITenantEntity, IConcurrencyAware
    {
        /// <summary>Tenant identifier — framework-owned column (snake_case: tenant_id).</summary>
        public TenantId? TenantId { get; set; }

        /// <summary>Adopter column — EF default on SQL Server (PascalCase: Name).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Concurrency token — framework-owned column (snake_case: row_version).</summary>
        public byte[]? RowVersion { get; set; }
    }
}
