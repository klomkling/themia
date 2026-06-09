using System.Data;
using DapperLib = global::Dapper;

namespace Themia.Framework.Data.Dapper.MySql;

internal static class MySqlDapperConfiguration
{
    private static readonly object Gate = new();
    private static volatile bool _configured;

    public static void EnsureConfigured()
    {
        if (_configured) return;
        lock (Gate)
        {
            if (_configured) return;
            // MySQL DATETIME is tz-naive: MySqlConnector returns DateTime, not DateTimeOffset. Map the audit
            // DateTimeOffset properties by treating the stored value as UTC; write the UTC instant back.
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
