using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Themia.Export;
using Themia.Export.Csv;
using Themia.Export.Excel;

namespace Themia.Modules.Export.Definitions;

/// <summary>Empty parameter type for definitions that take no filter.</summary>
public sealed class EmptyParams;

/// <summary>Convenience base: typed rows + typed, validated filter params. Deserializes
/// <see cref="ExportContext.ParametersJson"/> to <typeparamref name="TParams"/>, validates it, then
/// dispatches to the CSV or Excel writer per <see cref="ExportContext.Format"/>.</summary>
/// <typeparam name="TRow">The row type.</typeparam>
/// <typeparam name="TParams">The strongly-typed filter/scope parameters.</typeparam>
public abstract class ExportDefinition<TRow, TParams> : IExportDefinition
    where TParams : new()
{
    private readonly ICsvExporter csv;
    private readonly IExcelExporter excel;

    /// <summary>Creates the base with the injected neutral writers.</summary>
    protected ExportDefinition(ICsvExporter csv, IExcelExporter excel)
    {
        this.csv = csv ?? throw new ArgumentNullException(nameof(csv));
        this.excel = excel ?? throw new ArgumentNullException(nameof(excel));
    }

    /// <inheritdoc />
    public abstract string Key { get; }

    /// <inheritdoc />
    public virtual bool AllowsIncludeSoftDeleted => false;

    /// <summary>The columns for the given params/context.</summary>
    protected abstract IReadOnlyList<ExportColumn<TRow>> Columns(TParams parameters, ExportContext context);

    /// <summary>The rows for the given params/context. Apply filters/scope here.</summary>
    protected abstract Task<IReadOnlyList<TRow>> RowsAsync(TParams parameters, ExportContext context, CancellationToken ct);

    /// <summary>Optional report-header lines above the table.</summary>
    protected virtual IEnumerable<ReportHeader> Headers(TParams parameters, ExportContext context) => [];

    /// <inheritdoc />
    public async Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var parameters = Deserialize(context.ParametersJson);
        var rows = await RowsAsync(parameters, context, cancellationToken).ConfigureAwait(false);
        var columns = Columns(parameters, context);
        var headers = Headers(parameters, context);

        return context.Format switch
        {
            ExportFormat.Csv => csv.Export(rows, columns, headers, context.FileName),
            ExportFormat.Xlsx => excel.Export(rows, columns, options: null, headers, context.FileName),
            _ => throw new InvalidOperationException($"Unsupported export format '{context.Format}'."),
        };
    }

    private static TParams Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new TParams();
        }

        TParams value;
        try
        {
            value = JsonSerializer.Deserialize<TParams>(json) ?? new TParams();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Export parameters are not valid JSON.", ex);
        }

        try
        {
            Validator.ValidateObject(value, new ValidationContext(value), validateAllProperties: true);
        }
        catch (ValidationException ex)
        {
            throw new InvalidOperationException("Export parameters failed validation.", ex);
        }

        return value;
    }
}

/// <summary>Convenience base for a definition that takes no filter parameters.</summary>
/// <typeparam name="TRow">The row type.</typeparam>
public abstract class ExportDefinition<TRow> : ExportDefinition<TRow, EmptyParams>
{
    /// <summary>Creates the base with the injected neutral writers.</summary>
    protected ExportDefinition(ICsvExporter csv, IExcelExporter excel) : base(csv, excel) { }

    /// <summary>The columns (no parameters).</summary>
    protected abstract IReadOnlyList<ExportColumn<TRow>> Columns(ExportContext context);

    /// <summary>The rows (no parameters).</summary>
    protected abstract Task<IReadOnlyList<TRow>> RowsAsync(ExportContext context, CancellationToken ct);

    // RS0016: PublicApiAnalyzers v3.3.4 cannot resolve the API surface string for
    // 'protected sealed override' members in open generic classes. These are internal
    // dispatch bridges — not part of the meaningful API surface consumers implement.
#pragma warning disable RS0016 // Add public types and members to the declared API
    /// <inheritdoc />
    protected sealed override IReadOnlyList<ExportColumn<TRow>> Columns(EmptyParams parameters, ExportContext context) => Columns(context);

    /// <inheritdoc />
    protected sealed override Task<IReadOnlyList<TRow>> RowsAsync(EmptyParams parameters, ExportContext context, CancellationToken ct) => RowsAsync(context, ct);
#pragma warning restore RS0016
}
