using System.Data;
using Dapper;

namespace Themia.Exceptional;

/// <summary>
/// Builds Dapper parameters for the write-side store operations. Dialects own write-parameter
/// construction (so each can bind temporal columns with the correct provider <see cref="DbType"/>)
/// but delegate here so the column lists live in exactly one place.
/// </summary>
/// <remarks>
/// The <c>temporalDbType</c> parameter on each method is the provider's correct type for the
/// <c>datetime</c> columns: <see cref="DbType.DateTime2"/> on SQL Server (the default
/// <see cref="DbType.DateTime"/> inference rounds to ~3.33 ms and silently truncates a
/// <c>datetime2</c> column); <see langword="null"/> on PostgreSQL/MySQL where the default binding
/// already matches the column type.
/// </remarks>
public static class ExceptionStoreParameters
{
    /// <summary>Parameters for the INSERT of a new exception row (all columns).</summary>
    public static DynamicParameters Insert(ExceptionEntry entry, DbType? temporalDbType)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var p = new DynamicParameters();
        p.Add("Guid", entry.Guid);
        p.Add("ApplicationName", entry.ApplicationName);
        p.Add("MachineName", entry.MachineName);
        p.Add("Type", entry.Type);
        p.Add("Source", entry.Source);
        p.Add("Message", entry.Message);
        p.Add("Detail", entry.Detail);
        p.Add("Host", entry.Host);
        p.Add("Url", entry.Url);
        p.Add("HttpMethod", entry.HttpMethod);
        p.Add("IpAddress", entry.IpAddress);
        p.Add("StatusCode", entry.StatusCode);
        p.Add("ErrorHash", entry.ErrorHash);
        p.Add("DuplicateCount", entry.DuplicateCount);
        p.Add("TenantId", entry.TenantId);
        p.Add("RequestBody", entry.RequestBody);
        p.Add("RequestContext", entry.RequestContext);
        p.Add("IsProtected", entry.IsProtected);
        AddTemporal(p, "CreationDate", entry.CreationDate, temporalDbType);
        AddTemporal(p, "LastLogDate", entry.LastLogDate, temporalDbType);
        AddTemporal(p, "DeletionDate", entry.DeletionDate, temporalDbType);
        return p;
    }

    /// <summary>Parameters for the rollup UPDATE (increments DuplicateCount for a matching recent row).</summary>
    public static DynamicParameters Rollup(ExceptionEntry entry, TimeSpan rollupPeriod, DbType? temporalDbType)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var p = new DynamicParameters();
        p.Add("ErrorHash", entry.ErrorHash);
        p.Add("ApplicationName", entry.ApplicationName);
        AddTemporal(p, "RollupSince", entry.CreationDate - rollupPeriod, temporalDbType);
        AddTemporal(p, "LastLogDate", entry.LastLogDate, temporalDbType);
        return p;
    }

    /// <summary>Parameters for the soft-delete UPDATE.</summary>
    public static DynamicParameters SoftDelete(Guid guid, DateTime deletionDateUtc, DbType? temporalDbType)
    {
        var p = new DynamicParameters();
        p.Add("Guid", guid);
        AddTemporal(p, "DeletionDate", deletionDateUtc, temporalDbType);
        return p;
    }

    /// <summary>Parameters for the purge DELETE.</summary>
    public static DynamicParameters Purge(DateTime olderThanUtc, DbType? temporalDbType)
    {
        var p = new DynamicParameters();
        AddTemporal(p, "OlderThan", olderThanUtc, temporalDbType);
        return p;
    }

    private static void AddTemporal(DynamicParameters p, string name, DateTime? value, DbType? temporalDbType)
    {
        if (temporalDbType is { } dbType)
            p.Add(name, value, dbType);
        else
            p.Add(name, value);
    }
}
