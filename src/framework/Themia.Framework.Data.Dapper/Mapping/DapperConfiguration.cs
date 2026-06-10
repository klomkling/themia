using System.Data;
using Themia.Framework.Core.Abstractions.Tenancy;
using DapperLib = global::Dapper;

namespace Themia.Framework.Data.Dapper.Mapping;

internal static class DapperConfiguration
{
    private static readonly object Gate = new();
    private static volatile bool _configured;
    private static string? _engineName;

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

    /// <summary>
    /// Claims the single per-process engine slot and, for engines that need it, installs the shared UTC
    /// <see cref="DateTimeOffset"/> type handler with the engine's parameter <paramref name="dateTimeOffsetDbType"/>.
    /// Called once per engine DI registration.
    /// </summary>
    /// <param name="engineName">Display name of the engine claiming the process (e.g. "SQL Server").</param>
    /// <param name="dateTimeOffsetDbType">
    /// The <see cref="DbType"/> to bind <see cref="DateTimeOffset"/> audit parameters as (SQL Server
    /// <c>datetime2</c> -> <see cref="DbType.DateTime2"/>, MySQL <c>DATETIME</c> -> <see cref="DbType.DateTime"/>),
    /// or <see langword="null"/> for engines whose driver surfaces <see cref="DateTimeOffset"/> natively
    /// (PostgreSQL/Npgsql) and so need no handler.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// A different engine was already registered in this process. Dapper's type-handler registry is
    /// process-global, so two engines binding <see cref="DateTimeOffset"/> with different <see cref="DbType"/>s
    /// would silently corrupt one engine's timestamp writes — this guard fails loudly instead.
    /// </exception>
    public static void ConfigureEngine(string engineName, DbType? dateTimeOffsetDbType)
    {
        lock (Gate)
        {
            if (_engineName is { } existing)
            {
                if (!string.Equals(existing, engineName, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Themia Dapper is already configured for the '{existing}' engine in this process; " +
                        $"cannot also register the '{engineName}' engine. Dapper's type-handler registry is " +
                        "process-global, so only one Themia Dapper engine may be registered per process.");
                return;   // idempotent: the same engine re-registering (e.g. one provider per DI scope)
            }

            if (dateTimeOffsetDbType is { } dbType)
                DapperLib.SqlMapper.AddTypeHandler(new UtcDateTimeOffsetTypeHandler(dbType));

            _engineName = engineName;
        }
    }

    /// <summary>Test-only: clears the process-global engine slot so the <see cref="ConfigureEngine"/> guard can
    /// be exercised in isolation. Not part of the normal runtime flow.</summary>
    internal static void ResetEngineRegistrationForTests()
    {
        lock (Gate) _engineName = null;
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

    /// <summary>
    /// Shared handler for engines (MySQL, SQL Server) whose drivers return tz-naive <see cref="DateTime"/> for
    /// the audit timestamp columns: treat the stored value as UTC on read, and write the UTC instant back as the
    /// engine's configured <see cref="DbType"/>. PostgreSQL/Npgsql surface <see cref="DateTimeOffset"/> natively
    /// and register no handler. Process-global (Dapper requirement) — see <see cref="ConfigureEngine"/>.
    /// </summary>
    private sealed class UtcDateTimeOffsetTypeHandler(DbType dbType) : DapperLib.SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => throw new InvalidCastException($"Cannot convert '{value?.GetType().Name ?? "null"}' to DateTimeOffset.")
        };

        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            parameter.DbType = dbType;
            parameter.Value = value.UtcDateTime;
        }
    }
}
