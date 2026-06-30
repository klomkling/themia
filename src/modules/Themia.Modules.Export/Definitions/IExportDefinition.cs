using Themia.Export;

namespace Themia.Modules.Export.Definitions;

/// <summary>A keyed, app-registered export. The persisted job stores only the key + parameters + format;
/// the definition reconstructs rows and columns at run time. No delegates are ever serialized.</summary>
public interface IExportDefinition
{
    /// <summary>The stable lookup key, e.g. <c>"sales-report"</c>.</summary>
    string Key { get; }

    /// <summary>Whether this definition permits the <c>IncludeSoftDeleted</c> opt-in. Default false.</summary>
    bool AllowsIncludeSoftDeleted { get; }

    /// <summary>Produces the file for the given context.</summary>
    /// <exception cref="InvalidOperationException">Parameters are invalid, or a non-numeric value hits an aggregate.</exception>
    Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken);
}
