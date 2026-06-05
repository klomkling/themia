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

    public void Dispose() => keepAlive.Dispose();
}
