# Themia.Export — Design

**Status:** approved (brainstorm)
**Date:** 2026-06-24
**Phase:** 2 (Productivity) — the last Phase-2 deliverable (after Notifications ✅, Pdf)
**Source:** Idevs `IReportBaseModel` / `IdevsExportRequest` / `IdevsContentResult` / `IdevsExcelExporter` (ClosedXML), de-Serenity-ized.

## Goal

A neutral, Serenity-free **tabular-data → file** export library: typed columns + rows in, file
bytes out. Excel (`.xlsx`, ClosedXML) and CSV. Stateless — no persistence, no schema, no
`IThemiaModule`.

## Boundary (what it is / is not)

- **Is:** a pure transform of a row collection + column descriptors into a downloadable file.
- **Is not:** document/layout rendering (contracts, invoices) — that is `Themia.Modules.Pdf`.
  Export must never grow a PDF path, or the two overlap. A PDF *table* is composed Export-model →
  Pdf, not produced here.
- **Is not:** persisted (no saved templates) or asynchronous (no queued large exports). Both were
  considered and dropped as YAGNI; the contract is shaped so either is additive later.

## Why not port the Idevs exporter verbatim

`IdevsExcelExporter` is deeply Serenity-coupled (`Serenity.Data.IRow`,
`Serenity.Reporting.ReportColumn`/`TabularDataReport`/`IDataOnlyReport`, `FastMember` reflection) and
its `Generate` calls `worksheet.Columns().AdjustToContents()` — a full-sheet auto-fit that measures
every cell in every column. That auto-fit is the dominant cost of a styled ~2000-row export (the
observed slowness), not the row count. Themia keeps the *useful* pieces (report header lines,
aggregate/summary row, type-based number/date formatting, table themes) on a clean typed contract,
and **drops the full-sheet auto-fit**.

## Packaging

Two **neutral cores**, `net8.0;net10.0`, Serenity-free, no framework dependency (consumable by
net8 PowerACC):

| Package | Responsibility | Third-party deps |
|---|---|---|
| `Themia.Export` | Contract types + **CSV** writer | none |
| `Themia.Export.Excel` | **ClosedXML** Excel backend (references `Themia.Export`) | ClosedXML |

Rationale: CSV-only hosts never pull ClosedXML; a faster Excel backend (MiniExcel / OpenXML
`OpenXmlWriter`) can replace `Themia.Export.Excel` later behind the same contract without breaking
consumers. Mirrors the per-backend split of `Themia.Exceptional.*`.

## Contract — `Themia.Export`

```csharp
namespace Themia.Export;

public enum ColumnAlignment { Auto, Left, Center, Right }

public enum AggregateKind { None, Label, Sum, Count, Average, Min, Max }

/// <summary>A single export column: a typed value selector plus presentation + aggregate intent.
/// No reflection — the consumer maps its own DTO/projection to columns.</summary>
public sealed class ExportColumn<T>
{
    public required string Title { get; init; }
    public required Func<T, object?> Value { get; init; }
    public string? NumberFormat { get; init; }   // Excel/.NET number-format string, e.g. "#,##0.00"
    public ColumnAlignment Alignment { get; init; } = ColumnAlignment.Auto;
    public double? Width { get; init; }           // explicit column width; null => smart width
    public AggregateKind Aggregate { get; init; } = AggregateKind.None;
}

/// <summary>A title line rendered above the table (merged across the column span).</summary>
public readonly record struct ReportHeader(string Line);

/// <summary>Format-agnostic export output. The host streams it
/// (e.g. <c>Results.File(r.Content, r.ContentType, r.FileName)</c>); no ASP.NET coupling here.</summary>
public sealed record ExportResult(byte[] Content, string ContentType, string FileName);

public interface ICsvExporter
{
    ExportResult Export<T>(
        IEnumerable<T> rows,
        IReadOnlyList<ExportColumn<T>> columns,
        IEnumerable<ReportHeader>? headers = null,
        string? fileName = null);   // caller-supplied download name; extension owned by the backend
}
```

CSV writer behavior:
- Optional report-header lines at the top (one per `ReportHeader`).
- Title row from `ExportColumn.Title`.
- One line per row; values from the typed selector, rendered **invariant-culture**. `NumberFormat`
  is an Excel-presentation concern and is **ignored in CSV** (CSV is a data-interchange format; Excel
  format strings such as `#,##0.00;[Red]…` are not .NET format strings, so applying them is unsafe).
  RFC 4180 quoting: quote when the value contains `,` `"` CR or LF; escape `"` as `""`.
- Summary row at the end computed from each column's `Aggregate` (`Label` writes literal text such as
  "Total"; numeric aggregates compute over the column's values; `None` leaves the cell blank).
- Content type `text/csv`; file name `report-yyyyMMddHHmmss.csv` unless the caller overrides
  (overload/option — see below).

## Excel backend — `Themia.Export.Excel`

```csharp
namespace Themia.Export.Excel;

/// <summary>Built-in ClosedXML table styles (ported subset of the Idevs TableTheme enum).</summary>
public enum ExcelTableTheme { None, Light, Medium, Dark }

public sealed class ExcelExportOptions
{
    public string SheetName { get; init; } = "Sheet1";
    public ExcelTableTheme Theme { get; init; } = ExcelTableTheme.Medium;
    public string FontName { get; init; } = "Calibri";
    public bool FreezeHeaderRow { get; init; } = true;
    public int WidthSampleRows { get; init; } = 50;   // smart width sample size; 0 => no auto-fit
}

public interface IExcelExporter
{
    ExportResult Export<T>(
        IEnumerable<T> rows,
        IReadOnlyList<ExportColumn<T>> columns,
        ExcelExportOptions? options = null,
        IEnumerable<ReportHeader>? headers = null,
        string? fileName = null);   // caller-supplied download name; extension owned by the backend
}
```

### Construction order (the fast path — style by range/column, never per cell)

1. **Project once** — materialize rows into an `object?[rowCount][colCount]` matrix via the typed
   selectors in a single pass. No reflection; no re-enumeration of the source.
2. **Report headers** — write each `ReportHeader.Line` at the top, merged across the column span.
3. **Table** — write the title row, then bulk-insert the matrix with a single
   `cell.InsertData(matrix)` (not a cell-by-cell loop), and wrap the block as a ClosedXML `Table`
   with the chosen `ExcelTableTheme` so header styling + banding come from the theme.
4. **Column styling, once per column** — set `ws.Column(c).Style.NumberFormat.Format` and
   `.Alignment.Horizontal` from each `ExportColumn`. Total style calls are O(columns), independent of
   row count.
5. **Summary row** — for columns whose `Aggregate != None`, emit one bottom row:
   `Sum/Average/Min/Max/Count` as ClosedXML **formulas** (`SUM(C2:C{last})`, etc.) so Excel computes
   them and they survive edits; `Label` writes literal text. Bold via the single summary-row range.
6. **Widths** — explicit `Width` where provided; otherwise **smart width** measuring only the first
   `WidthSampleRows` rows (default 50). **Never** `Columns().AdjustToContents()` over the whole
   sheet. `WidthSampleRows = 0` disables fitting.
7. Freeze the header row when `FreezeHeaderRow`; save the workbook to `byte[]`.

Content type `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`; file name
`report-yyyyMMddHHmmss.xlsx` unless overridden.

### Why this is fast

Every styling operation is applied to a **column or a range**, so total work is O(columns) for
styling plus O(rows) for the single bulk data insert — versus the Idevs path's O(rows×columns)
auto-fit measurement. A 2000-row styled export drops from seconds to well under a second.

## Dependency injection

- `Themia.Export`: `services.AddThemiaExport()` registers `ICsvExporter` (stateless singleton).
- `Themia.Export.Excel`: `services.AddThemiaExcelExport()` registers `IExcelExporter` (stateless
  singleton).

Both exporters are pure and thread-safe; no scoped state. The host owns streaming the `ExportResult`.

## Error handling

- Guard clauses: null `rows`/`columns` → `ArgumentNullException`; empty `columns` → `ArgumentException`.
- Empty `rows` is valid: report headers + title row + empty body + a summary row of zero/blank.
- Selector or number-format exceptions propagate to the caller (the host endpoint is the boundary).
  No silent swallow; a pure transform does not log.

## File naming

Default `report-{yyyyMMddHHmmss}.{ext}` (invariant culture). A caller-supplied name is accepted via
an optional parameter on the exporter methods (e.g. `string? fileName = null`) so hosts can name the
download; extension/content-type are still owned by the backend.

## Testing

- **`Themia.Export.Tests`** (pure unit): CSV golden output — RFC 4180 quoting (embedded `,` `"` CR
  LF), summary-row math for each `AggregateKind`, empty-rows case, number-format application, typed
  selector invoked once per row.
- **`Themia.Export.Excel.Tests`**: build a workbook, **re-open it with ClosedXML**, and assert cell
  values, column-level number-format strings, summary **formulas** present, table + theme created,
  header frozen, widths set without full-sheet auto-fit. Plus a **perf guard** — a 5,000-row export
  completes under a small time budget (proves the no-auto-fit path stays fast), tagged
  `[Trait("Category","Performance")]` so CI can scope it.

## Out of scope (YAGNI — additive later, no contract break)

- Grouping / subtotals (the Idevs `GROUP` aggregate).
- Streaming API for very large exports (>100k rows) — current design buffers to `byte[]`.
- Additional backends (MiniExcel / OpenXML for speed; ODS / TSV / JSON formats).
- Persisted export templates; asynchronous / queued exports written to Storage with a Notifications
  ping.

## Catalog / roadmap

Closes the Phase-2 Productivity set. Updates `Themia.Modules.Export` row in
`docs/themia-architecture-overview.md` — note the realized shape is **two neutral cores
(`Themia.Export` + `Themia.Export.Excel`), not a tenant-aware module**, since the transform is
stateless.
