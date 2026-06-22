using System.Data;
using Dapper;
using Themia.Exceptional;
using Xunit;

namespace Themia.Exceptional.Tests;

public class ExceptionStoreParametersTests
{
    private static ExceptionEntry SampleEntry() => new()
    {
        Guid = Guid.NewGuid(),
        ApplicationName = "App",
        MachineName = "M",
        Type = "System.Exception",
        Message = "boom",
        Detail = "stack",
        ErrorHash = "hash",
        DuplicateCount = 1,
        CreationDate = new DateTime(2026, 6, 6, 1, 2, 3, DateTimeKind.Utc),
        LastLogDate = new DateTime(2026, 6, 6, 1, 2, 3, DateTimeKind.Utc),
    };

    // Note: Dapper does not expose per-parameter DbType publicly, so these tests assert the full
    // column set is bound; the actual DateTime2 binding behavior is covered by the SqlServer
    // integration test Insert_PreservesSubMillisecondPrecision_OnDateTime2.
    [Fact]
    public void Insert_WithDateTime2_BindsAllColumns()
    {
        var p = ExceptionStoreParameters.Insert(SampleEntry(), DbType.DateTime2);
        var names = p.ParameterNames.ToHashSet();
        Assert.Contains("CreationDate", names);
        Assert.Contains("LastLogDate", names);
        Assert.Contains("DeletionDate", names);
        Assert.Contains("Guid", names);
        Assert.Contains("RequestBody", names);
    }

    [Fact]
    public void Insert_WithNullTemporalDbType_StillBindsAllColumns()
    {
        var p = ExceptionStoreParameters.Insert(SampleEntry(), temporalDbType: null);
        Assert.Contains("CreationDate", p.ParameterNames);
        Assert.Contains("Detail", p.ParameterNames);
    }

    [Fact]
    public void Rollup_BindsErrorHashAppRollupSinceAndLastLogDate()
    {
        var p = ExceptionStoreParameters.Rollup(SampleEntry(), TimeSpan.FromHours(1), DbType.DateTime2);
        var names = p.ParameterNames.ToHashSet();
        Assert.Contains("ErrorHash", names);
        Assert.Contains("ApplicationName", names);
        Assert.Contains("RollupSince", names);
        Assert.Contains("LastLogDate", names);
    }

    [Fact]
    public void SoftDelete_BindsGuidAndDeletionDate()
    {
        var p = ExceptionStoreParameters.SoftDelete(Guid.NewGuid(), DateTime.UtcNow, DbType.DateTime2);
        var names = p.ParameterNames.ToHashSet();
        Assert.Contains("Guid", names);
        Assert.Contains("DeletionDate", names);
    }

    [Fact]
    public void Purge_BindsOlderThan()
    {
        var p = ExceptionStoreParameters.Purge(DateTime.UtcNow, DbType.DateTime2);
        Assert.Contains("OlderThan", p.ParameterNames);
    }

    [Fact]
    public void Insert_IncludesRequestContext()
    {
        var entry = new ExceptionEntry { RequestContext = "{\"headers\":{}}" };

        var p = ExceptionStoreParameters.Insert(entry, temporalDbType: null);

        Assert.Contains("RequestContext", p.ParameterNames);
        Assert.Equal("{\"headers\":{}}", p.Get<string?>("RequestContext"));
    }
}
