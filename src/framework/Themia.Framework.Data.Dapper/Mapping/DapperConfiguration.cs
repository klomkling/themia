using System.Data;
using Themia.Framework.Core.Abstractions.Tenancy;
using DapperLib = global::Dapper;

namespace Themia.Framework.Data.Dapper.Mapping;

internal static class DapperConfiguration
{
    private static readonly object Gate = new();
    private static volatile bool _configured;

    public static void EnsureConfigured()
    {
        if (_configured) return;
        // Mutates process-global Dapper state (DefaultTypeMap + the type-handler registry); a lock keeps
        // concurrent AddThemiaDapperCore calls from registering the handler more than once.
        lock (Gate)
        {
            if (_configured) return;
            DapperLib.DefaultTypeMap.MatchNamesWithUnderscores = true;

            // Dapper has no built-in mapping between the TenantId value object and the varchar tenant column,
            // so materializing an ITenantEntity row would throw "Invalid cast from 'System.String' to TenantId".
            // Register a handler so reads (string -> TenantId) and writes (TenantId -> string) round-trip. The
            // handler covers the non-null TenantId; Dapper assigns null directly to a nullable TenantId? member.
            DapperLib.SqlMapper.AddTypeHandler(new TenantIdTypeHandler());

            _configured = true;
        }
    }

    private sealed class TenantIdTypeHandler : DapperLib.SqlMapper.TypeHandler<TenantId>
    {
        public override TenantId Parse(object value) => new((string)value);

        public override void SetValue(IDbDataParameter parameter, TenantId value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.Value;
        }
    }
}
