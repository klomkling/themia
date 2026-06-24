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

**Dependency:** ClosedXML is **not yet** in `Directory.Packages.props` — the plan's first task pins it
there (central package management; the csproj carries no version). It must target a version supporting
`net8.0;net10.0`.

**Known tradeoff (boxing):** `Func<T, object?>` boxes value-type cell values. Negligible at the target
scale (2–5k rows); if a consumer ever pushes ~100k rows this is the first thing to revisit (e.g. a
typed-column variant). Documented, not solved — YAGNI until measured.

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
- Optional summary row at the end, computed in C# per the shared **Aggregate semantics** below
  (`Label` writes literal text such as "Total"; numeric aggregates fold the column's values; `None`
  leaves the cell blank).
- **Rectangular output:** every emitted line has exactly N fields. Report-header lines put their text
  in field 1 and pad the remaining N-1 fields empty, so the file stays RFC-4180-valid (no ragged rows).
- Content type `text/csv`; file name `report-yyyyMMddHHmmss.csv` unless the caller overrides via the
  `fileName` parameter.

## Aggregate semantics (shared by both writers — single engine)

Aggregates are computed **once, in C#**, for both CSV and Excel, so the two formats can never disagree.
No Excel `SUM()` formulas are emitted — the summary cell holds a pre-computed literal.

- A column value participates in a numeric aggregate (`Sum`/`Average`/`Min`/`Max`) iff it converts via
  `Convert.ToDecimal(value, CultureInfo.InvariantCulture)`. `null` is **skipped**. A non-null value
  that fails to convert throws `InvalidOperationException` at export time (fail fast — a non-numeric
  value in a summed column is a caller bug, not silently dropped).
- `Count` = number of **non-null** values in the column.
- `Average` = `Sum / Count` over the participating (non-null, numeric) values; `Count == 0` ⇒ blank.
- `Min`/`Max` over the participating values; none ⇒ blank.
- `Label` ⇒ the column's literal title text in the summary cell (e.g. "Total"); `None` ⇒ blank.
- The computed numeric result is rendered with the column's `NumberFormat` in Excel (literal cell,
  the format applies as a normal cell style) and invariant-culture in CSV.

## Excel backend — `Themia.Export.Excel`

```csharp
namespace Themia.Export.Excel;
// using ClosedXML.Excel;  // XLTableTheme is exposed directly — this package already depends on ClosedXML.

public enum ColumnWidthMode
{
    Estimate,   // default: font-free width from sampled char lengths (deterministic, CI-safe)
    Measure,    // opt-in: ClosedXML AdjustToContents glyph measurement (needs font metrics)
    None,       // leave default widths
}

public sealed class ExcelExportOptions
{
    public string SheetName { get; init; } = "Sheet1";
    /// <summary>Any ClosedXML table theme by full name (≈60 values, parity with the Idevs TableTheme
    /// set). Null ⇒ a sensible default (TableStyleMedium2). Exposed directly because this package
    /// already references ClosedXML; it does not leak into the neutral Themia.Export contract.</summary>
    public XLTableTheme? TableTheme { get; init; }
    public string FontName { get; init; } = "Calibri";
    public bool FreezeHeaderRow { get; init; } = true;
    public ColumnWidthMode WidthMode { get; init; } = ColumnWidthMode.Estimate;
    public int WidthSampleRows { get; init; } = 50;   // rows sampled to size columns (Estimate/Measure)
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
   `cell.InsertData(matrix)` (not a cell-by-cell loop), and wrap the **header + data** block as a
   ClosedXML `Table` with the chosen `XLTableTheme` so header styling + banding come from the theme.
   The table's native totals row (`ShowTotalsRow`) is **not** used — see step 5.
4. **Column styling, once per column** — set `ws.Column(c).Style.NumberFormat.Format` and
   `.Alignment.Horizontal` from each `ExportColumn`. Number format + table theme compose (theme owns
   colors/borders, the column style owns number format). Total style calls are O(columns),
   independent of row count.
5. **Summary row** — if any column has `Aggregate != None`, write **one literal row directly below
   the table** (outside the table range): values are the C#-computed aggregates from the *Aggregate
   semantics* above (no Excel formulas — so CSV and Excel match exactly). Bold + top border via the
   single summary-row range; numeric cells carry the column `NumberFormat`.
6. **Widths** — explicit `ExportColumn.Width` always wins. Otherwise per `WidthMode`:
   `Estimate` (default) sizes each column from the **max character length** over the first
   `WidthSampleRows` rows × a per-char factor (clamped to a max) — pure arithmetic, no font metrics,
   deterministic on Linux CI; `Measure` opts into `column.AdjustToContents(firstRow, firstRow +
   WidthSampleRows)` (glyph measurement, needs fonts); `None` leaves defaults. **Never**
   `Columns().AdjustToContents()` over the whole sheet.
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
  values, column-level number-format strings, the **summary row holds computed literals** (matching
  the CSV writer's numbers exactly for the same input — the cross-format equality is the key
  assertion), table + theme created, header frozen, and (in `Estimate` mode) widths set with **no
  font measurement**. A non-numeric value in an aggregated column throws `InvalidOperationException`.
- **Perf is guaranteed structurally, not by a wall-clock assert** (a timing budget on a shared CI
  runner is inherently flaky and — under `--filter "Category!=Integration"` — would run in the release
  gate). The structural guarantee: a 5,000-row export in `Estimate` mode completes and **calls no
  full-sheet auto-fit / no glyph measurement** (the slow path is simply absent from the code).
  A real timing benchmark, if wanted, lives as a local/manual `BenchmarkDotNet` harness outside the
  test gate — never as a CI assertion.

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
