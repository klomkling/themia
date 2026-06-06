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

    [Fact]
    public void Insert_WithDateTime2_SetsDateTime2OnTemporalColumns()
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
}
