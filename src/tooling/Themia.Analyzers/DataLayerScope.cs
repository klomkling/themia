using System;

namespace Themia.Analyzers;

/// <summary>
/// The Themia.Framework.Data.* assemblies legitimately own the raw primitives (repositories, the Dapper
/// connection context, the guarded ThemiaDbContext.Find overrides that call base.Find). The isolation
/// analyzers stay silent there and fire everywhere else — adopter code and Themia.Modules.* alike.
/// </summary>
internal static class DataLayerScope
{
    public static bool IsDataLayerAssembly(string? assemblyName) =>
        assemblyName is not null &&
        assemblyName.StartsWith("Themia.Framework.Data", StringComparison.Ordinal);
}
