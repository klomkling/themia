using Xunit;
using Themia.MultiTenancy.Stores;
using Themia.MultiTenancy.Tests.TestUtilities;

namespace Themia.MultiTenancy.Tests.Stores;

public class DapperTenantStoreTests
{
    [Fact]
    public async Task FindByIdentifierAsync_WithExistingTenant_ShouldReturnTenant()
    {
        using var db = await SqliteTestDb.CreateAsync();
        await db.SeedTenantsAsync(
            ("tenant-1", "acme", "Acme Corp", "production", "Server=localhost;Database=acme")
        );

        var store = new DapperTenantStore(db.GetConnectionFactory());

        var result = await store.FindByIdentifierAsync("acme");

        Assert.NotNull(result);
        Assert.Equal("tenant-1", result.Id);
        Assert.Equal("acme", result.Identifier);
        Assert.Equal("Acme Corp", result.Name);
        Assert.Equal("production", result.Environment);
        Assert.Equal("Server=localhost;Database=acme", result.ConnectionString);
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithNonExistingTenant_ShouldReturnNull()
    {
        using var db = await SqliteTestDb.CreateAsync();
        var store = new DapperTenantStore(db.GetConnectionFactory());

        var result = await store.FindByIdentifierAsync("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithMultipleTenants_ShouldReturnCorrectOne()
    {
        using var db = await SqliteTestDb.CreateAsync();
        await db.SeedTenantsAsync(
            ("tenant-1", "acme", "Acme Corp", "production", null),
            ("tenant-2", "globex", "Globex Corp", "staging", null),
            ("tenant-3", "initech", "Initech Corp", "development", null)
        );

        var store = new DapperTenantStore(db.GetConnectionFactory());

        var result = await store.FindByIdentifierAsync("globex");

        Assert.NotNull(result);
        Assert.Equal("tenant-2", result.Id);
        Assert.Equal("globex", result.Identifier);
        Assert.Equal("Globex Corp", result.Name);
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithNullFields_ShouldHandleGracefully()
    {
        using var db = await SqliteTestDb.CreateAsync();
        await db.SeedTenantsAsync(
            ("tenant-1", "minimal", null, null, null)
        );

        var store = new DapperTenantStore(db.GetConnectionFactory());

        var result = await store.FindByIdentifierAsync("minimal");

        Assert.NotNull(result);
        Assert.Equal("minimal", result.Identifier);
        Assert.Null(result.Name);
        Assert.Null(result.Environment);
        Assert.Null(result.ConnectionString);
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithSpecialCharacters_ShouldHandleCorrectly()
    {
        using var db = await SqliteTestDb.CreateAsync();
        await db.SeedTenantsAsync(
            ("tenant-1", "tenant-with-'quotes'", "Test Corp", null, null)
        );

        var store = new DapperTenantStore(db.GetConnectionFactory());

        var result = await store.FindByIdentifierAsync("tenant-with-'quotes'");

        Assert.NotNull(result);
        Assert.Equal("tenant-with-'quotes'", result.Identifier);
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithNullIdentifier_ShouldThrowArgumentException()
    {
        using var db = await SqliteTestDb.CreateAsync();
        var store = new DapperTenantStore(db.GetConnectionFactory());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await store.FindByIdentifierAsync(null!);
        });
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithEmptyIdentifier_ShouldThrowArgumentException()
    {
        using var db = await SqliteTestDb.CreateAsync();
        var store = new DapperTenantStore(db.GetConnectionFactory());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await store.FindByIdentifierAsync("");
        });
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithWhitespaceIdentifier_ShouldThrowArgumentException()
    {
        using var db = await SqliteTestDb.CreateAsync();
        var store = new DapperTenantStore(db.GetConnectionFactory());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await store.FindByIdentifierAsync("   ");
        });
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithCancellationToken_ShouldRespectCancellation()
    {
        using var db = await SqliteTestDb.CreateAsync();
        var store = new DapperTenantStore(db.GetConnectionFactory());
        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await store.FindByIdentifierAsync("acme", cts.Token);
        });
    }

    [Fact]
    public async Task FindByIdentifierAsync_MultipleCalls_ShouldReturnSameData()
    {
        using var db = await SqliteTestDb.CreateAsync();
        await db.SeedTenantsAsync(
            ("tenant-1", "acme", "Acme Corp", null, null)
        );

        var store = new DapperTenantStore(db.GetConnectionFactory());

        var result1 = await store.FindByIdentifierAsync("acme");
        var result2 = await store.FindByIdentifierAsync("acme");

        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(result2.Id, result1.Id);
        Assert.Equal(result2.Identifier, result1.Identifier);
    }

    [Fact]
    public void Constructor_WithNullConnectionFactory_ShouldThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            new DapperTenantStore(null!);
        });
    }

    [Fact]
    public async Task Constructor_WithNullTableName_ShouldThrowArgumentException()
    {
        using var db = await SqliteTestDb.CreateAsync();

        Assert.Throws<ArgumentException>(() =>
        {
            new DapperTenantStore(db.GetConnectionFactory(), null!);
        });
    }

    [Fact]
    public async Task Constructor_WithEmptyTableName_ShouldThrowArgumentException()
    {
        using var db = await SqliteTestDb.CreateAsync();

        Assert.Throws<ArgumentException>(() =>
        {
            new DapperTenantStore(db.GetConnectionFactory(), "");
        });
    }

    [Fact]
    public async Task FindByIdentifierAsync_WithCustomTableName_ShouldUseCorrectTable()
    {
        using var customDb = await SqliteTestDb.CreateAsync("custom_tenants");
        await customDb.SeedTenantsAsync(
            ("tenant-1", "acme", null, null, null)
        );

        var store = new DapperTenantStore(customDb.GetConnectionFactory(), "custom_tenants");

        var result = await store.FindByIdentifierAsync("acme");

        Assert.NotNull(result);
        Assert.Equal("acme", result.Identifier);
    }

    [Fact]
    public void DapperTenantStore_Query_ShouldContainNoEngineSpecificTopNClause()
    {
        // Guards the [INTRODUCED] portability fix: the catalog query must run on SQL Server too,
        // so it carries no LIMIT (MySQL/PostgreSQL/SQLite) nor TOP (SQL Server) clause.
        // Assert against the REAL query the store executes — one source of truth, no hard-coded copy.
        var query = DapperTenantStore.BuildCatalogQuery("tenants");

        Assert.DoesNotContain("LIMIT", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" TOP ", query, StringComparison.OrdinalIgnoreCase);
    }

    // Fix 6 — constructor rejects malicious/invalid table names (SQL injection guard).

    [Theory]
    [InlineData("tenants; DROP TABLE users--")]
    [InlineData("a b")]
    [InlineData("1tenants")]
    [InlineData("ten;ants")]
    public async Task Constructor_WithInvalidTableName_ShouldThrowArgumentException(string tableName)
    {
        using var db = await SqliteTestDb.CreateAsync();

        var ex = Assert.Throws<ArgumentException>(() =>
            new DapperTenantStore(db.GetConnectionFactory(), tableName));

        Assert.Equal("tableName", ex.ParamName);
    }

    [Fact]
    public async Task Constructor_WithValidDottedTableName_ShouldBeAccepted()
    {
        // A schema-qualified name like "dbo.tenants" is valid per the regex.
        using var db = await SqliteTestDb.CreateAsync("dbo_tenants"); // SQLite doesn't support schemas; just confirm no throw.
        Assert.NotNull(new DapperTenantStore(db.GetConnectionFactory(), "dbo.tenants"));
    }
}
