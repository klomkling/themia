using System.Data;
using DapperLib = global::Dapper;

namespace Themia.Framework.Data.Dapper.SqlServer;

internal static class SqlServerDapperConfiguration
{
    private static readonly object Gate = new();
    private static volatile bool _configured;

    public static void EnsureConfigured()
    {
        if (_configured) return;
        lock (Gate)
        {
            if (_configured) return;
            // SQL Server datetime2 is tz-naive: SqlClient returns DateTime, not DateTimeOffset. Map the audit
            // DateTimeOffset properties by treating the stored value as UTC; write the UTC instant back as
            // DbType.DateTime2 (full precision/range, vs MySQL's DbType.DateTime).
            // Assumption — ONE Dapper engine per application/process: SqlMapper.AddTypeHandler is process-global,
            // registered only here by AddThemiaDapperSqlServer. The PostgreSQL engine registers no DateTimeOffset
            // handler (Npgsql surfaces DateTimeOffset natively); the MySQL engine registers a DbType.DateTime
            // variant. Loading two Dapper engines in one process is NOT supported: this handler's SQL-Server
            // SetValue would then also apply to the other engine's writes, which is incorrect.
            DapperLib.SqlMapper.AddTypeHandler(new DateTimeOffsetTypeHandler());
            _configured = true;
        }
    }

    private sealed class DateTimeOffsetTypeHandler : DapperLib.SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => throw new InvalidCastException($"Cannot convert '{value?.GetType().Name ?? "null"}' to DateTimeOffset.")
        };

        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = DbType.DateTime2;
            parameter.Value = value.UtcDateTime;
        }
    }
}
