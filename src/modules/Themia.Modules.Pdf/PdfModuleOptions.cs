namespace Themia.Modules.Pdf;

/// <summary>Configuration for the PDF module.</summary>
public sealed class PdfModuleOptions
{
    /// <summary>Name of the connection string (from configuration) the module connects to.</summary>
    public string ConnectionStringName { get; set; } = "Default";
}
