using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Themia.Framework.Core.Abstractions.Entities;
using Xunit;

namespace Themia.Framework.Data.EFCore.IntegrationTests.SoftDelete;

/// <summary>
/// Integration tests for soft delete and restore functionality with real PostgreSQL.
/// </summary>
[Trait("Category", "Integration")]
public class SoftDeleteIntegrationTests : IClassFixture<SoftDeleteIntegrationTests.PostgresFixture>
{
    private readonly PostgresFixture fixture;

    public SoftDeleteIntegrationTests(PostgresFixture fixture)
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
            var allDocuments = await context.Documents
                .IgnoreQueryFilters()
                .ToListAsync();

            Assert.Single(allDocuments);
            var document = allDocuments.First();
            Assert.True(document.IsDeleted);
            Assert.Equal(documentId, document.Id);
        }
    }

    [Fact]
    public async Task Restore_ClearsSoftDeleteFlags_AndSetsRestoredFields()
    {
        await fixture.ResetDataAsync();

        int documentId;

        await using (var context = fixture.CreateContextWithUser("creator"))
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

        var beforeRestore = DateTimeOffset.UtcNow;

        await using (var context = fixture.CreateContextWithUser("restorer"))
        {
            var document = await context.Documents
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(d => d.Id == documentId);

            Assert.NotNull(document);
            Assert.True(document.IsDeleted);

            // Restore the document
            document.IsDeleted = false;
            document.RestoredAt = DateTimeOffset.UtcNow;
            document.RestoredBy = "restorer";

            await context.SaveChangesAsync();

            var afterRestore = DateTimeOffset.UtcNow;

            Assert.False(document.IsDeleted);
            Assert.NotNull(document.RestoredAt);
            Assert.True(document.RestoredAt >= beforeRestore);
            Assert.True(document.RestoredAt <= afterRestore);
            Assert.Equal("restorer", document.RestoredBy);
        }

        // Verify restored document is visible in queries
        await using (var context = fixture.CreateContext())
        {
            var document = await context.Documents.FindAsync(documentId);
            Assert.NotNull(document);
            Assert.False(document.IsDeleted);
        }
    }

    [Fact]
    public async Task MultipleDeleteRestore_TracksHistory()
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

        // First delete
        await using (var context = fixture.CreateContextWithUser("deleter1"))
        {
            var document = await context.Documents.FindAsync(documentId);
            Assert.NotNull(document);
            context.Documents.Remove(document);
            await context.SaveChangesAsync();
        }

        // First restore
        await using (var context = fixture.CreateContextWithUser("restorer1"))
        {
            var document = await context.Documents
                .IgnoreQueryFilters()
                .FirstAsync(d => d.Id == documentId);

            document.IsDeleted = false;
            document.RestoredAt = DateTimeOffset.UtcNow;
            document.RestoredBy = "restorer1";
            await context.SaveChangesAsync();
        }

        // Second delete
        await using (var context = fixture.CreateContextWithUser("deleter2"))
        {
            var document = await context.Documents.FindAsync(documentId);
            Assert.NotNull(document);
            context.Documents.Remove(document);
            await context.SaveChangesAsync();
        }

        // Verify final state
        await using (var context = fixture.CreateContext())
        {
            var document = await context.Documents
                .IgnoreQueryFilters()
                .FirstAsync(d => d.Id == documentId);

            Assert.True(document.IsDeleted);
            Assert.Equal("deleter2", document.DeletedBy);
            // Note: In a real scenario, you might want to track restore history separately
            Assert.Equal("restorer1", document.RestoredBy);
        }
    }

    public sealed class PostgresFixture : IAsyncLifetime
    {
        private readonly PostgreSqlContainer container;
        private string connectionString = string.Empty;

        public PostgresFixture()
        {
            container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("themia_softdelete_tests")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .WithCleanUp(true)
                .Build();
        }

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

        private DbContextOptions<TestSoftDeleteDbContext> GetOptions()
        {
            return new DbContextOptionsBuilder<TestSoftDeleteDbContext>()
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .Options;
        }

        private async Task EnsureSchemaAsync()
        {
            await using var context = CreateContext();
            await context.Database.EnsureCreatedAsync();
        }
    }

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
