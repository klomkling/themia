using DapperLib = global::Dapper;

namespace Themia.Framework.Data.Dapper.Mapping;

internal static class DapperConfiguration
{
    private static bool _configured;

    public static void EnsureConfigured()
    {
        if (_configured) return;
        DapperLib.DefaultTypeMap.MatchNamesWithUnderscores = true;
        _configured = true;
    }
}
