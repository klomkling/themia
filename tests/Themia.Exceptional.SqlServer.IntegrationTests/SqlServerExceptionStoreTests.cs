using Themia.Exceptional.Conformance;

using Xunit;

namespace Themia.Exceptional.SqlServer.IntegrationTests;

[Collection(SqlServerExceptionalCollection.Name)]
[Trait("Category", "Integration")]
public class SqlServerExceptionStoreTests(SqlServerExceptionalFixture fixture) : ExceptionStoreConformanceTests
{
    // No GuidFormat quirk — uniqueidentifier round-trips natively through Microsoft.Data.SqlClient.
    private ExceptionStoreEngine Engine => new(new SqlServerExceptionalDialect(fixture.ConnectionString), new ExceptionalOptions { ApplicationName = "App" });

    protected override IExceptionStore Store => Engine;

    protected override Task ResetStoreAsync() => fixture.ResetAsync();

    // Engine-specific: datetime2 preserves sub-millisecond ticks — unique to SQL Server.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Insert_PreservesSubMillisecondPrecision_OnDateTime2()
    {
        var store = Engine;
        var precise = new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        var guid = Guid.NewGuid();
        await store.LogAsync(new ExceptionEntry
        {
            Guid = guid, ApplicationName = "App", MachineName = "M", Type = "T",
            Message = "m", Detail = "d", ErrorHash = Guid.NewGuid().ToString("N"),
            DuplicateCount = 1, CreationDate = precise, LastLogDate = precise,
        });

        var read = await store.GetAsync(guid);
        Assert.NotNull(read);
        Assert.Equal(precise.Ticks % TimeSpan.TicksPerMillisecond, read!.CreationDate.Ticks % TimeSpan.TicksPerMillisecond);
    }
}
