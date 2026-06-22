using Themia.Exceptional;
using Xunit;

namespace Themia.Exceptional.Conformance;

/// <summary>
/// Provider-agnostic behavioral contract for <see cref="IExceptionStore"/>. Each engine's integration
/// test class derives from this and supplies a <see cref="Store"/> backed by a live, migrated container,
/// so the shared CRUD/rollup/soft-delete/protect/purge behaviors are asserted identically on every engine.
/// </summary>
public abstract class ExceptionStoreConformanceTests
{
    /// <summary>A store backed by a live, migrated database for the engine under test.</summary>
    protected abstract IExceptionStore Store { get; }

    protected static ExceptionEntry NewEntry(string hash = "h1")
    {
        var now = DateTime.UtcNow;
        return new ExceptionEntry
        {
            Guid = Guid.NewGuid(), ApplicationName = "App", MachineName = "M", Type = "System.Exception",
            Message = "boom", Detail = """{"Message":"boom"}""", ErrorHash = hash, DuplicateCount = 1,
            CreationDate = now, LastLogDate = now,
        };
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Log_And_Get_RoundTrip()
    {
        var entry = NewEntry();
        await Store.LogAsync(entry);

        var loaded = await Store.GetAsync(entry.Guid);

        Assert.NotNull(loaded);
        Assert.Equal(entry.Guid, loaded!.Guid);
        Assert.Equal("App", loaded.ApplicationName);
        Assert.Equal("boom", loaded.Message);
        Assert.Equal("""{"Message":"boom"}""", loaded.Detail);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Log_Duplicate_Rollups()
    {
        var store = Store;
        var e1 = NewEntry("dup");
        await store.LogAsync(e1);
        var e2 = NewEntry("dup");
        e2.ApplicationName = "App";
        await store.LogAsync(e2);

        var loaded = await store.GetAsync(e1.Guid);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.DuplicateCount);

        // Rollup must increment in place, not split into a second row.
        var page = await store.ListAsync(new ExceptionFilter());
        Assert.Single(page.Items);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SoftDelete_HidesFromList()
    {
        var entry = NewEntry("del");
        await Store.LogAsync(entry);

        Assert.True(await Store.DeleteAsync(entry.Guid), "DeleteAsync should return true for a row it soft-deleted.");

        var page = await Store.ListAsync(new ExceptionFilter());

        Assert.DoesNotContain(page.Items, i => i.Guid == entry.Guid);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SoftDelete_VisibleWithIncludeDeleted()
    {
        var entry = NewEntry("del2");
        await Store.LogAsync(entry);
        await Store.DeleteAsync(entry.Guid);

        var page = await Store.ListAsync(new ExceptionFilter { IncludeDeleted = true });

        Assert.Contains(page.Items, i => i.Guid == entry.Guid);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CountAsync_ReturnsCorrectCount()
    {
        var store = Store;
        var e = NewEntry("cnt");
        await store.LogAsync(e);

        Assert.True(await store.CountAsync(new ExceptionFilter { ApplicationName = "App" }) >= 1);
        Assert.True(await store.CountAsync(new ExceptionFilter { IncludeDeleted = true }) >= 1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Protect_PreventsPurge()
    {
        var entry = NewEntry("prot");
        entry.CreationDate = DateTime.UtcNow.AddDays(-30);
        await Store.LogAsync(entry);
        await Store.ProtectAsync(entry.Guid);

        var removed = await Store.PurgeAsync(DateTime.UtcNow.AddDays(-1));

        Assert.Equal(0, removed);
        Assert.NotNull(await Store.GetAsync(entry.Guid));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task HardDelete_RemovesRow()
    {
        var entry = NewEntry("hard");
        await Store.LogAsync(entry);

        Assert.True(await Store.HardDeleteAsync(entry.Guid));
        Assert.Null(await Store.GetAsync(entry.Guid));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListAsync_FiltersByDateRange()
    {
        var store = Store;
        var oldE = NewEntry("range-old");
        oldE.CreationDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        oldE.LastLogDate = oldE.CreationDate;
        var midE = NewEntry("range-mid");
        midE.CreationDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        midE.LastLogDate = midE.CreationDate;
        await store.LogAsync(oldE);
        await store.LogAsync(midE);

        var page = await store.ListAsync(new ExceptionFilter { From = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) });

        Assert.Single(page.Items);
        Assert.Equal("range-mid", page.Items[0].ErrorHash);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListAsync_FiltersByDateRange_KindUnspecified_DoesNotThrow()
    {
        var store = Store;
        var entry = NewEntry("unspec");
        entry.CreationDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        entry.LastLogDate = entry.CreationDate;
        await store.LogAsync(entry);

        // Kind=Unspecified should be coerced to Utc by ToUtc() — must not throw.
        var page = await store.ListAsync(new ExceptionFilter { From = new DateTime(2026, 5, 1) });

        Assert.Single(page.Items);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Insert_Then_Get_RoundTrips_WithRequestBody()
    {
        var store = Store;
        var entry = NewEntry("body");
        entry.RequestBody = "{\"key\":\"value\"}";
        await store.LogAsync(entry);

        var loaded = await store.GetAsync(entry.Guid);

        Assert.NotNull(loaded);
        Assert.Equal("{\"key\":\"value\"}", loaded!.RequestBody);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Insert_And_Get_RoundTripsRequestContext()
    {
        var store = Store;
        var entry = ExceptionEntryFactory.FromException(new InvalidOperationException("ctx"), "IntegrationApp");
        entry.RequestContext = "{\"headers\":{\"User-Agent\":\"Edge\"},\"cookies\":{}}";
        await store.LogAsync(entry);

        var loaded = await store.GetAsync(entry.Guid);

        Assert.NotNull(loaded);
        Assert.Equal(entry.RequestContext, loaded!.RequestContext);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListAsync_FiltersByDateRange_KindLocal_DoesNotThrow_AndReturnsRow()
    {
        var store = Store;
        var knownUtc = new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc);
        var entry = NewEntry("local-kind");
        entry.CreationDate = knownUtc;
        entry.LastLogDate = knownUtc;
        await store.LogAsync(entry);

        // Build a Local-kind From that is earlier than the entry's CreationDate.
        var localFrom = DateTime.SpecifyKind(knownUtc.AddHours(-1).ToLocalTime(), DateTimeKind.Local);
        var page = await store.ListAsync(new ExceptionFilter { From = localFrom });

        Assert.True(page.Items.Count >= 1);
        Assert.Contains(page.Items, i => i.ErrorHash == "local-kind");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetAsync_ReturnsUtcKindTimestamps()
    {
        var store = Store;
        var knownUtc = new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc);
        var entry = NewEntry("utc-kind");
        entry.CreationDate = knownUtc;
        entry.LastLogDate = knownUtc;
        await store.LogAsync(entry);

        var loaded = await store.GetAsync(entry.Guid);

        Assert.NotNull(loaded);
        Assert.Equal(DateTimeKind.Utc, loaded!.CreationDate.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.LastLogDate.Kind);
        Assert.True((loaded.CreationDate - knownUtc).Duration() < TimeSpan.FromSeconds(1));
        Assert.True((loaded.LastLogDate - knownUtc).Duration() < TimeSpan.FromSeconds(1));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LogAsync_DuplicateGuid_ViolatesUniqueIndex()
    {
        // Rollup keys on ErrorHash; a different hash forces an INSERT, so the same Guid must
        // collide with the unique IX_Exceptions_Guid index.
        var store = Store;
        var first = NewEntry("guid-uniq-1");
        await store.LogAsync(first);

        var duplicate = NewEntry("guid-uniq-2");
        duplicate.Guid = first.Guid;

        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => store.LogAsync(duplicate));
    }
}
