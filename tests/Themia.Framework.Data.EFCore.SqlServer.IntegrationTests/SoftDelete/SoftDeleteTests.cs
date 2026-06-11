using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Data.EFCore;
using Xunit;

namespace Themia.Framework.Data.EFCore.SqlServer.IntegrationTests.SoftDelete;

/// <summary>
/// Integration tests for soft delete and restore functionality with real SQL Server.
/// </summary>
[Trait("Category", "Integration")]
public class SoftDeleteTests : IClassFixture<SoftDeleteTests.SqlServerFixture>
{
    private readonly SqlServerFixture fixture;

    public SoftDeleteTests(SqlServerFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task Delete_ConvertsSoftDelete_AndSetsDeletedFields()
    {
        await fixture.ResetDataAsync();

        int documentId;
        var beforeDelete = DateTimeOffset.UtcNow;

        await using (var context = fixture.CreateContextWithUser("creator"))
        {
            var document = new Document { Title = "Test Doc", Content = "Content" };
            context.Documents.Add(document);
            await context.SaveChangesAsync();
            documentId = document.Id;
        }

        await using (var context = fixture.CreateContextWithUser("deleter"))
        {
            var document = await context.Documents.FindAsync(documentId);
            Assert.NotNull(document);

            context.Documents.Remove(document);
            await context.SaveChangesAsync();

            var afterDelete = DateTimeOffset.UtcNow;

            Assert.True(document.IsDeleted);
            Assert.NotNull(document.DeletedAt);
            Assert.True(document.DeletedAt >= beforeDelete);
            Assert.True(document.DeletedAt <= afterDelete);
            Assert.Equal("deleter", document.DeletedBy);
        }
    }

    [Fact]
    public async Task SoftDeletedEntity_IsFilteredFromQueries()
    {
        await fixture.ResetDataAsync();

        int documentId;

        await using (var context = fixture.CreateContext())
        {
            var document = new Document { Title = "Test Doc", Content = "Content" };
            context.Documents.Add(document);
            await context.SaveChangesAsync();
            documentId = document.Id;
        }

        await using (var context = fixture.CreateContext())
        {
            var document = await context.Documents.FindAsync(documentId);
            Assert.NotNull(document);
            context.Documents.Remove(document);
            await context.SaveChangesAsync();
        }

        await using (var context = fixture.CreateContext())
        {
            var found = await context.Documents.FindAsync(documentId);
            Assert.Null(found);

            var allDocuments = await context.Documents.ToListAsync();
            Assert.Empty(allDocuments);
        }
    }

    [Fact]
    public async Task IgnoreQueryFilters_IncludesSoftDeletedEntities()
    {
        await fixture.ResetDataAsync();

        int documentId;

        await using (var context = fixture.CreateContext())
        {
            var document = new Document { Title = "Test Doc", Content = "Content" };
            context.Documents.Add(document);
            await context.SaveChangesAsync();
            documentId = document.Id;
        }

        await using (var context = fixture.CreateContext())
        {
            var document = await context.Documents.FindAsync(documentId);
            Assert.NotNull(document);
            context.Documents.Remove(document);
            await context.SaveChangesAsync();
        }

        await using (var context = fixture.CreateContext())
        {
            // Filtered query should be empty.
            var filtered = await context.Documents.ToListAsync();
            Assert.Empty(filtered);

            // IgnoreQueryFilters should include the soft-deleted document.
            var unfiltered = await context.Documents.IgnoreQueryFilters().ToListAsync();
            Assert.Single(unfiltered);
            Assert.True(unfiltered[0].IsDeleted);
        }
    }

    [Fact]
    public async Task MultipleDocuments_OnlyNonDeletedAreReturned()
    {
        await fixture.ResetDataAsync();

        await using (var context = fixture.CreateContext())
        {
            context.Documents.Add(new Document { Title = "Keep 1", Content = "Content" });
            context.Documents.Add(new Document { Title = "Keep 2", Content = "Content" });
            context.Documents.Add(new Document { Title = "Delete Me", Content = "Content" });
            await context.SaveChangesAsync();
        }

        await using (var context = fixture.CreateContext())
        {
            var toDelete = await context.Documents.FirstAsync(d => d.Title == "Delete Me");
            context.Documents.Remove(toDelete);
            await context.SaveChangesAsync();
        }

        await using (var context = fixture.CreateContext())
        {
            var documents = await context.Documents.ToListAsync();
            Assert.Equal(2, documents.Count);
            Assert.DoesNotContain(documents, d => d.Title == "Delete Me");
        }
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    public sealed class SqlServerFixture : IAsyncLifetime
    {
        private readonly MsSqlContainer container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04")
            .WithCleanUp(true)
            .Build();

        private string connectionString = string.Empty;

        public async Task InitializeAsync()
        {
            await container.StartAsync();
            connectionString = container.GetConnectionString();
            await EnsureSchemaAsync();
        }

        public async Task DisposeAsync() => await container.DisposeAsync();

        public TestSoftDeleteDbContext CreateContext(string? userId = null) =>
            new(GetOptions(), userId);

        public TestSoftDeleteDbContext CreateContextWithUser(string userId) =>
            new(GetOptions(), userId);

        public async Task ResetDataAsync()
        {
            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
            await context.Documents.IgnoreQueryFilters().ExecuteDeleteAsync();
        }

        private DbContextOptions<TestSoftDeleteDbContext> GetOptions() =>
            new DbContextOptionsBuilder<TestSoftDeleteDbContext>()
                .UseSqlServer(connectionString)
                .Options;

        private async Task EnsureSchemaAsync()
        {
            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
        }
    }

    // ── Test context ──────────────────────────────────────────────────────────

    public class TestSoftDeleteDbContext : ThemiaDbContext
    {
        private readonly string? userId;

        public TestSoftDeleteDbContext(DbContextOptions options, string? userId = null)
            : base(options, null, null)
        {
            this.userId = userId;
        }

        public DbSet<Document> Documents => Set<Document>();

        protected override string? CurrentUserId => userId;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Document>(entity =>
            {
                entity.ToTable("documents");
                entity.HasKey(d => d.Id);
                entity.Property(d => d.Title).IsRequired().HasMaxLength(200);
            });
            base.OnModelCreating(modelBuilder);
        }
    }

    // ── Entity ────────────────────────────────────────────────────────────────

    public class Document : ISoftDeletable
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public bool IsDeleted { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public string? DeletedBy { get; set; }
        public DateTimeOffset? RestoredAt { get; set; }
        public string? RestoredBy { get; set; }
    }
}
