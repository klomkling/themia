namespace Themia.Modules.Pdf;

/// <summary>Thrown when no tenant-owned or global template exists for a requested key.
/// HTTP-agnostic — an ASP.NET Core middleware owns any status mapping (project convention).</summary>
public sealed class TemplateNotFoundException(string key)
    : Exception($"No PDF template found for key '{key}' (tenant-owned or global).")
{
    /// <summary>The unresolved template key.</summary>
    public string Key { get; } = key;
}
