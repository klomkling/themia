using Dapper;
using Microsoft.Data.Sqlite;
using Themia.Exceptional;
using Xunit;

namespace Themia.Exceptional.Tests;

public class ExceptionStoreEngineTests : IDisposable
{
    private readonly SqliteConnection keepAlive;
    private readonly ExceptionStoreEngine engine;
    private readonly string connString;

    public ExceptionStoreEngineTests()
    {
        // Shared-cache in-memory DB; keepAlive keeps the schema alive for the test's lifetime.
        connString = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        keepAlive = new SqliteConnection(connString);
        keepAlive.Open();
        keepAlive.Execute(SqliteExceptionalDialect.CreateTableSql);
        engine = new ExceptionStoreEngine(new SqliteExceptionalDialect(connString));
    }

    private static ExceptionEntry NewEntry(string hash = "h1", string app = "App")
    {
        var now = DateTime.UtcNow;
        return new ExceptionEntry
        {
            Guid = Guid.NewGuid(), ApplicationName = app, MachineName = "M", Type = "T",
            Message = "m", Detail = "{}", ErrorHash = hash, DuplicateCount = 1,
            CreationDate = now, LastLogDate = now,
        };
    }

    [Fact]
    public async Task LogAsync_Inserts_WhenNoDuplicate()
    {
        await engine.LogAsync(NewEntry());

        var count = await engine.CountAsync(new ExceptionFilter());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task LogAsync_RollsUp_DuplicateWithinPeriod()
    {
        var first = NewEntry();
        await engine.LogAsync(first);
        await engine.LogAsync(NewEntry()); // same hash → rollup

        var page = await engine.ListAsync(new ExceptionFilter());
        Assert.Single(page.Items);
        Assert.Equal(2, page.Items[0].DuplicateCount);
    }

    [Fact]
    public async Task GetAsync_RoundTripsFullJson()
    {
        var entry = NewEntry();
        entry.Detail = """{"Message":"boom"}""";
        await engine.LogAsync(entry);

        var loaded = await engine.GetAsync(entry.Guid);
        Assert.NotNull(loaded);
        Assert.Equal("""{"Message":"boom"}""", loaded!.Detail);
    }

    [Fact]
    public async Task DeleteAsync_SoftDeletes_AndHidesFromDefaultList()
    {
        var entry = NewEntry();
        await engine.LogAsync(entry);

        Assert.True(await engine.DeleteAsync(entry.Guid));

        Assert.Equal(0, await engine.CountAsync(new ExceptionFilter()));
        Assert.Equal(1, await engine.CountAsync(new ExceptionFilter { IncludeDeleted = true }));
    }

    [Fact]
    public async Task ProtectAsync_PreventsPurge()
    {
        var old = NewEntry();
        old.CreationDate = DateTime.UtcNow.AddDays(-30);
        await engine.LogAsync(old);
        await engine.ProtectAsync(old.Guid);

        var removed = await engine.PurgeAsync(DateTime.UtcNow.AddDays(-1));

        Assert.Equal(0, removed);
        Assert.NotNull(await engine.GetAsync(old.Guid));
    }

    [Fact]
    public async Task HardDeleteAsync_RemovesRow()
    {
        var entry = NewEntry();
        await engine.LogAsync(entry);

        Assert.True(await engine.HardDeleteAsync(entry.Guid));
        Assert.Null(await engine.GetAsync(entry.Guid));
    }

    [Fact]
    public async Task LogAsync_Inserts_NewRow_WhenExistingIsOlderThanRollupPeriod()
    {
        var shortWindow = new ExceptionStoreEngine(new SqliteExceptionalDialect(connString), TimeSpan.FromMinutes(5));

        var old = NewEntry();
        old.CreationDate = DateTime.UtcNow.AddMinutes(-10); // outside the 5-min window
        old.LastLogDate = old.CreationDate;
        await shortWindow.LogAsync(old);

        var fresh = NewEntry(); // same hash, CreationDate = now
        await shortWindow.LogAsync(fresh);

        // RollupSince = fresh.CreationDate - 5min; old.CreationDate is 10min back → outside window → new insert
        var count = await shortWindow.CountAsync(new ExceptionFilter());
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ListAsync_FiltersByDateRange()
    {
        var oldE = NewEntry("a");
        oldE.CreationDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        oldE.LastLogDate = oldE.CreationDate;
        var midE = NewEntry("b");
        midE.CreationDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        midE.LastLogDate = midE.CreationDate;
        await engine.LogAsync(oldE);
        await engine.LogAsync(midE);

        var page = await engine.ListAsync(new ExceptionFilter { From = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) });

        Assert.Single(page.Items);
        Assert.Equal("b", page.Items[0].ErrorHash);
    }

    [Fact]
    public async Task LogAsync_ConvertsLocalKindTimestampToUtcInstant()
    {
        // A Kind=Local timestamp must be CONVERTED (ToUniversalTime), not merely re-labeled, so the
        // stored instant is correct. TZ-independent: on a UTC machine ToUniversalTime is a no-op and the
        // assertion still holds; on an offset machine it catches a re-label (SpecifyKind) regression.
        var local = DateTime.Now; // Kind=Local
        var expectedUtc = local.ToUniversalTime();
        var entry = NewEntry("local");
        entry.CreationDate = local;
        entry.LastLogDate = local;

        await engine.LogAsync(entry);
        var loaded = await engine.GetAsync(entry.Guid);

        Assert.NotNull(loaded);
        Assert.Equal(expectedUtc, loaded!.CreationDate, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ListAsync_ClampsPageSizeZeroToOne()
    {
        await engine.LogAsync(NewEntry("clamp-h1"));

        var page = await engine.ListAsync(new ExceptionFilter { PageSize = 0 });

        Assert.Single(page.Items);
    }

    [Fact]
    public async Task LogAsync_ThrowsArgumentException_WhenErrorHashIsEmpty()
    {
        var entry = NewEntry();
        entry.ErrorHash = "";

        await Assert.ThrowsAsync<ArgumentException>(() => engine.LogAsync(entry));
    }

    [Fact]
    public async Task ListAsync_Paging_ReturnsCorrectPageAndTotal()
    {
        await engine.LogAsync(NewEntry("pg-h1"));
        await engine.LogAsync(NewEntry("pg-h2"));
        await engine.LogAsync(NewEntry("pg-h3"));

        var page1 = await engine.ListAsync(new ExceptionFilter { PageSize = 2, Page = 1 });
        var page2 = await engine.ListAsync(new ExceptionFilter { PageSize = 2, Page = 2 });

        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(3, page1.Total);
        Assert.Single(page2.Items);
        Assert.Equal(3, page2.Total);
    }

    [Fact]
    public async Task PurgeAsync_RemovesOldUnprotected_LeavesRecent()
    {
        var old = NewEntry("purge-old");
        old.CreationDate = DateTime.UtcNow.AddDays(-30);
        old.LastLogDate = old.CreationDate;
        await engine.LogAsync(old);

        var recent = NewEntry("purge-recent");
        await engine.LogAsync(recent);

        var removed = await engine.PurgeAsync(DateTime.UtcNow.AddDays(-1));

        Assert.Equal(1, removed);
        Assert.NotNull(await engine.GetAsync(recent.Guid));
        Assert.Null(await engine.GetAsync(old.Guid));
    }

    [Fact]
    public async Task DeleteAsync_IsIdempotent_AndNewEntryWithSameHashIsActive()
    {
        var entry = NewEntry("idem-hash");
        await engine.LogAsync(entry);

        Assert.True(await engine.DeleteAsync(entry.Guid));
        Assert.False(await engine.DeleteAsync(entry.Guid));

        // A new entry with the same hash should insert a fresh active row.
        var fresh = NewEntry("idem-hash");
        await engine.LogAsync(fresh);

        var nonDeletedCount = await engine.CountAsync(new ExceptionFilter());
        Assert.Equal(1, nonDeletedCount);

        var withDeletedCount = await engine.CountAsync(new ExceptionFilter { IncludeDeleted = true });
        Assert.Equal(2, withDeletedCount);
    }

    [Fact]
    public async Task ListAsync_FiltersBySearch_ReturnsMatchingRowOnly()
    {
        var matchEntry = NewEntry("search-h1");
        matchEntry.Message = "UniqueSearchTerm42";
        await engine.LogAsync(matchEntry);

        var otherEntry = NewEntry("search-h2");
        otherEntry.Message = "SomethingElse";
        await engine.LogAsync(otherEntry);

        var page = await engine.ListAsync(new ExceptionFilter { Search = "UniqueSearchTerm" });

        Assert.Single(page.Items);
        Assert.Equal("search-h1", page.Items[0].ErrorHash);
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenDialectIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new ExceptionStoreEngine(null!));
    }

    [Fact]
    public void Constructor_ThrowsArgumentOutOfRange_WhenRollupPeriodNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ExceptionStoreEngine(new SqliteExceptionalDialect(connString), TimeSpan.FromSeconds(-1)));
    }

    public void Dispose() => keepAlive.Dispose();
}
