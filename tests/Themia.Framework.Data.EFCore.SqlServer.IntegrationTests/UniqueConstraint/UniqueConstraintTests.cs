using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Data.Abstractions.Exceptions;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.EFCore;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.SqlServer;
using Xunit;

namespace Themia.Framework.Data.EFCore.SqlServer.IntegrationTests.UniqueConstraint;

/// <summary>
/// A row that violates a UNIQUE index must surface as the framework's typed
/// <see cref="UniqueConstraintException"/> from the EF Core peer on SQL Server. The EF unit of work wraps
/// the driver error in a <c>DbUpdateException</c>; the SQL Server interpreter classifies it via
/// <c>SqlException.Number</c> 2627/2601 (SqlClient surfaces no SqlState), exercised through the DI path.
/// </summary>
[Trait("Category", "Integration")]
[Collection(SqlServerIntegrationCollection.Name)]
public sealed class UniqueConstraintTests : IClassFixture<UniqueConstraintTests.Fixture>
{
    private readonly Fixture fixture;

    public UniqueConstraintTests(Fixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task EfCore_DuplicateUniqueColumn_ThrowsUniqueConstraintException()
    {
        await fixture.ResetDataAsync();
        await using var provider = fixture.BuildProvider();

        await InsertAsync(provider, "SKU-1");

        await Assert.ThrowsAsync<UniqueConstraintException>(() => InsertAsync(provider, "SKU-1"));
    }

    private static async Task InsertAsync(IServiceProvider provider, string code)
    {
        await using var scope = provider.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Gadget, int>>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await repo.AddAsync(new Gadget { Code = code });
        await uow.SaveChangesAsync();
    }

    public sealed class Fixture : IAsyncLifetime
    {
        private readonly SharedSqlServerContainerFixture sharedContainer;
        private string connectionString = string.Empty;

        public Fixture(SharedSqlServerContainerFixture sharedContainer) => this.sharedContainer = sharedContainer;

        public async Task InitializeAsync()
        {
            connectionString = sharedContainer.GetConnectionString("ef_unique_constraint");
            await using var provider = BuildProvider();
            await using var scope = provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<GadgetDbContext>();
            await context.Database.EnsureCreatedAsync();
        }

        public Task DisposeAsync() => Task.CompletedTask;

        public ServiceProvider BuildProvider()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = connectionString })
                .Build();
            var services = new ServiceCollection();
            services.AddThemiaSqlServer<GadgetDbContext>(configuration);
            services.AddThemiaDataRepositories<GadgetDbContext>();
            return services.BuildServiceProvider();
        }

        public async Task ResetDataAsync()
        {
            await using var provider = BuildProvider();
            await using var scope = provider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<GadgetDbContext>();
            await context.Gadgets.ExecuteDeleteAsync();
        }
    }

    public sealed class GadgetDbContext : ThemiaDbContext
    {
        public GadgetDbContext(DbContextOptions<GadgetDbContext> options) : base(options, null, null) { }

        public DbSet<Gadget> Gadgets => Set<Gadget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Gadget>(entity =>
            {
                entity.ToTable("gadgets");
                entity.HasKey(g => g.Id);
                entity.Property(g => g.Code).IsRequired().HasMaxLength(100);
                entity.HasIndex(g => g.Code).IsUnique();
            });
            base.OnModelCreating(modelBuilder);
        }
    }

    public sealed class Gadget : Entity<int>
    {
        public string Code { get; set; } = string.Empty;
    }
}
