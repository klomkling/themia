using Microsoft.Extensions.Configuration;
using Themia.MultiTenancy.Abstractions;

namespace Themia.Framework.Data.Dapper.Connection;

/// <summary>
/// Resolves the connection string a Dapper engine should open: the ambient tenant's connection string when one
/// is available (<see cref="ITenantAccessor"/>), otherwise the <c>"Default"</c> connection string. Shared by the
/// per-engine <see cref="IDapperConnectionFactory"/> implementations so the resolution rule lives in one place.
/// Internal framework plumbing — the engine packages reach it via <c>InternalsVisibleTo</c>.
/// </summary>
internal static class DapperConnectionString
{
    /// <summary>The configuration key of the fallback connection string used when no tenant string is resolved.</summary>
    public const string DefaultConnectionName = "Default";

    /// <summary>
    /// Resolves the tenant connection string (<see cref="ITenantAccessor.Current"/>'s, when present) or the
    /// <c>"Default"</c> connection string. Throws when neither is available.
    /// </summary>
    public static string Resolve(IConfiguration configuration, IServiceProvider serviceProvider)
    {
        var tenantCs = (serviceProvider.GetService(typeof(ITenantAccessor)) as ITenantAccessor)?.Current?.ConnectionString;
        if (!string.IsNullOrWhiteSpace(tenantCs)) return tenantCs;

        var cs = configuration.GetConnectionString(DefaultConnectionName);
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                $"No tenant connection string was resolved and connection string '{DefaultConnectionName}' was not found or is empty.");

        return cs;
    }
}
