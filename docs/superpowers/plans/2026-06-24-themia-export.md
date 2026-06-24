# Themia.Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build two stateless neutral-core packages — `Themia.Export` (typed export contract + CSV writer) and `Themia.Export.Excel` (ClosedXML `.xlsx` backend) — that turn a typed column set + row collection into downloadable file bytes.

**Architecture:** A neutral contract (`ExportColumn<T>` with `Func<T,object?>` selectors, `ReportHeader`, `ExportResult`) plus a single C# aggregate engine shared by both writers, so CSV and Excel always produce identical summary numbers. The Excel writer styles by range/column (never per cell) and sizes columns by font-free estimation by default — deliberately omitting the full-sheet `AdjustToContents()` that made the Idevs exporter slow.

**Tech Stack:** .NET (`net8.0;net10.0` neutral cores), C# 12, ClosedXML (Excel), xUnit. Central Package Management, PublicAPI analyzers.

**Spec:** `docs/superpowers/specs/2026-06-24-themia-export-design.md`

## Global Constraints

- **Target frameworks:** both packages multi-target `net8.0;net10.0` (neutral cores — the net8 leg is mandatory for PowerACC reuse). Test projects are `net10.0` only.
- **`Directory.Build.props` sets these globally — never repeat them in a csproj:** `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `GenerateDocumentationFile=true`, and the shared `<Version>`. A clean build (`dotnet build --no-incremental`) reports undocumented public members as `RS0016`; every public type/member needs an XML doc comment and a `PublicAPI.Unshipped.txt` entry.
- **CPM:** package versions live in `Directory.Packages.props`; csproj `<PackageReference>` carries **no** `Version`.
- **System.Text.Json only — never Newtonsoft.** Log via `ILogger<T>` only (not relevant here — these are pure transforms with no logging). No reflection in the hot path (selectors are typed delegates).
- **DI extensions** use `Microsoft.Extensions.DependencyInjection.Extensions` `TryAdd*` and guard args with `ArgumentNullException.ThrowIfNull`.
- **Aggregate semantics (verbatim from spec — both writers MUST match):** a value participates in a numeric aggregate iff it converts via `Convert.ToDecimal(value, CultureInfo.InvariantCulture)`; `null` is skipped; a non-null value that fails to convert throws `InvalidOperationException` at export time. `Count` = non-null count. `Average` = sum/count, blank when count 0. `Min`/`Max` over participating values, blank when none. `Label` = the column title text. `None` = blank.

---

## File Structure

**`src/neutral/Themia.Export/`** (contract + CSV, zero third-party deps)
- `Themia.Export.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`, `AssemblyInfo.cs`
- `ColumnAlignment.cs`, `AggregateKind.cs`, `ExportColumn.cs`, `ReportHeader.cs`, `ExportResult.cs`
- `Internal/RowProjector.cs`, `Internal/AggregateComputer.cs` (both `internal`)
- `Csv/ICsvExporter.cs`, `Csv/CsvExporter.cs`
- `DependencyInjection/ExportServiceCollectionExtensions.cs`

**`src/neutral/Themia.Export.Excel/`** (ClosedXML backend)
- `Themia.Export.Excel.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`, `AssemblyInfo.cs`
- `ColumnWidthMode.cs`, `ExcelExportOptions.cs`
- `Excel/IExcelExporter.cs`, `Excel/ExcelExporter.cs`
- `DependencyInjection/ExcelExportServiceCollectionExtensions.cs`

**Tests** (`net10.0`)
- `tests/Themia.Export.Tests/` — aggregate engine + CSV
- `tests/Themia.Export.Excel.Tests/` — Excel round-trip

---

## Task 1: `Themia.Export` skeleton + contract types

**Files:**
- Create: `Directory.Packages.props` (modify — add ClosedXML pin)
- Create: `src/neutral/Themia.Export/Themia.Export.csproj`
- Create: `src/neutral/Themia.Export/PublicAPI.Shipped.txt`, `.../PublicAPI.Unshipped.txt`
- Create: `src/neutral/Themia.Export/ColumnAlignment.cs`, `AggregateKind.cs`, `ExportColumn.cs`, `ReportHeader.cs`, `ExportResult.cs`
- Create: `tests/Themia.Export.Tests/Themia.Export.Tests.csproj`, `.../ContractTypeTests.cs`

**Interfaces:**
- Produces: `ExportColumn<T>` { `string Title`, `Func<T,object?> Value`, `string? NumberFormat`, `ColumnAlignment Alignment`, `double? Width`, `AggregateKind Aggregate` }; `enum ColumnAlignment { Auto, Left, Center, Right }`; `enum AggregateKind { None, Label, Sum, Count, Average, Min, Max }`; `readonly record struct ReportHeader(string Line)`; `sealed record ExportResult(byte[] Content, string ContentType, string FileName)`.

- [ ] **Step 1: Pin ClosedXML in CPM.** Add to `Directory.Packages.props` inside the existing `<ItemGroup>` of `<PackageVersion>` entries:

```xml
<PackageVersion Include="ClosedXML" Version="0.105.0" />
```
(0.105.0 targets netstandard2.0/2.1; runs on both net8.0 and net10.0.)

- [ ] **Step 2: Create the project file** `src/neutral/Themia.Export/Themia.Export.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Neutral cross-framework package: MUST include net8.0 (PowerACC reuse). -->
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.Export</PackageId>
    <Description>Neutral tabular-data export contract and CSV writer: typed columns, report headers, aggregate summary rows. No Serenity, no framework dependency. Excel backend ships separately as Themia.Export.Excel.</Description>
    <PackageTags>themia;export;csv;report;spreadsheet</PackageTags>
    <!-- Version inherited from Directory.Build.props. Do not set it here. -->
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Export.Excel" />
    <InternalsVisibleTo Include="Themia.Export.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create `PublicAPI.Shipped.txt`** with exactly one line:

```text
#nullable enable
```

- [ ] **Step 4: Create the contract type files.**

`ColumnAlignment.cs`:
```csharp
namespace Themia.Export;

/// <summary>Horizontal alignment for an export column's cells.</summary>
public enum ColumnAlignment
{
    /// <summary>Let the backend choose (numbers right, text left).</summary>
    Auto,
    /// <summary>Left-align.</summary>
    Left,
    /// <summary>Center-align.</summary>
    Center,
    /// <summary>Right-align.</summary>
    Right,
}
```

`AggregateKind.cs`:
```csharp
namespace Themia.Export;

/// <summary>The summary-row operation for a column. See the aggregate semantics in the design spec.</summary>
public enum AggregateKind
{
    /// <summary>No summary cell for this column.</summary>
    None,
    /// <summary>Write the column title as literal text (e.g. "Total").</summary>
    Label,
    /// <summary>Sum of the numeric values.</summary>
    Sum,
    /// <summary>Count of non-null values.</summary>
    Count,
    /// <summary>Arithmetic mean of the numeric values.</summary>
    Average,
    /// <summary>Smallest numeric value.</summary>
    Min,
    /// <summary>Largest numeric value.</summary>
    Max,
}
```

`ExportColumn.cs`:
```csharp
namespace Themia.Export;

/// <summary>Describes one export column: a typed value selector plus presentation and aggregate intent.
/// No reflection — the consumer maps its own row type <typeparamref name="T"/> to columns.</summary>
/// <typeparam name="T">The row type.</typeparam>
public sealed class ExportColumn<T>
{
    /// <summary>The column header text.</summary>
    public required string Title { get; init; }

    /// <summary>Extracts the cell value from a row. May return <see langword="null"/>.</summary>
    public required Func<T, object?> Value { get; init; }

    /// <summary>An Excel/.NET number-format string (e.g. <c>"#,##0.00"</c>); applied in Excel only.</summary>
    public string? NumberFormat { get; init; }

    /// <summary>Horizontal alignment; defaults to <see cref="ColumnAlignment.Auto"/>.</summary>
    public ColumnAlignment Alignment { get; init; } = ColumnAlignment.Auto;

    /// <summary>Explicit column width; <see langword="null"/> means the backend sizes it.</summary>
    public double? Width { get; init; }

    /// <summary>The summary-row operation; defaults to <see cref="AggregateKind.None"/>.</summary>
    public AggregateKind Aggregate { get; init; } = AggregateKind.None;
}
```

`ReportHeader.cs`:
```csharp
namespace Themia.Export;

/// <summary>A title line rendered above the table (merged across the column span in Excel; padded to the
/// column count in CSV so the file stays rectangular).</summary>
/// <param name="Line">The header text.</param>
public readonly record struct ReportHeader(string Line);
```

`ExportResult.cs`:
```csharp
namespace Themia.Export;

/// <summary>A produced export file. The host streams it, e.g.
/// <c>Results.File(r.Content, r.ContentType, r.FileName)</c>; this type carries no ASP.NET dependency.</summary>
/// <param name="Content">The file bytes.</param>
/// <param name="ContentType">The MIME content type.</param>
/// <param name="FileName">The suggested download file name.</param>
public sealed record ExportResult(byte[] Content, string ContentType, string FileName);
```

- [ ] **Step 5: Populate `PublicAPI.Unshipped.txt`** with the public surface added above:

```text
#nullable enable
Themia.Export.AggregateKind
Themia.Export.AggregateKind.Average = 4 -> Themia.Export.AggregateKind
Themia.Export.AggregateKind.Count = 3 -> Themia.Export.AggregateKind
Themia.Export.AggregateKind.Label = 1 -> Themia.Export.AggregateKind
Themia.Export.AggregateKind.Max = 6 -> Themia.Export.AggregateKind
Themia.Export.AggregateKind.Min = 5 -> Themia.Export.AggregateKind
Themia.Export.AggregateKind.None = 0 -> Themia.Export.AggregateKind
Themia.Export.AggregateKind.Sum = 2 -> Themia.Export.AggregateKind
Themia.Export.ColumnAlignment
Themia.Export.ColumnAlignment.Auto = 0 -> Themia.Export.ColumnAlignment
Themia.Export.ColumnAlignment.Center = 2 -> Themia.Export.ColumnAlignment
Themia.Export.ColumnAlignment.Left = 1 -> Themia.Export.ColumnAlignment
Themia.Export.ColumnAlignment.Right = 3 -> Themia.Export.ColumnAlignment
Themia.Export.ExportColumn<T>
Themia.Export.ExportColumn<T>.Aggregate.get -> Themia.Export.AggregateKind
Themia.Export.ExportColumn<T>.Aggregate.init -> void
Themia.Export.ExportColumn<T>.Alignment.get -> Themia.Export.ColumnAlignment
Themia.Export.ExportColumn<T>.Alignment.init -> void
Themia.Export.ExportColumn<T>.ExportColumn() -> void
Themia.Export.ExportColumn<T>.NumberFormat.get -> string?
Themia.Export.ExportColumn<T>.NumberFormat.init -> void
Themia.Export.ExportColumn<T>.Title.get -> string!
Themia.Export.ExportColumn<T>.Title.init -> void
Themia.Export.ExportColumn<T>.Value.get -> System.Func<T, object?>!
Themia.Export.ExportColumn<T>.Value.init -> void
Themia.Export.ExportColumn<T>.Width.get -> double?
Themia.Export.ExportColumn<T>.Width.init -> void
Themia.Export.ExportResult
Themia.Export.ExportResult.Content.get -> byte[]!
Themia.Export.ExportResult.Content.init -> void
Themia.Export.ExportResult.ContentType.get -> string!
Themia.Export.ExportResult.ContentType.init -> void
Themia.Export.ExportResult.ExportResult(byte[]! Content, string! ContentType, string! FileName) -> void
Themia.Export.ExportResult.FileName.get -> string!
Themia.Export.ExportResult.FileName.init -> void
Themia.Export.ReportHeader
Themia.Export.ReportHeader.Line.get -> string!
Themia.Export.ReportHeader.Line.init -> void
Themia.Export.ReportHeader.ReportHeader(string! Line) -> void
```
(If a clean build reports `RS0016`/`RS0037` for records' compiler-generated members, run the analyzer code-fix "Add to public API" or copy the exact symbol from the diagnostic. Records also emit `Deconstruct`, `Equals`, `GetHashCode`, `==`, `!=`, `ToString`, `<Clone>$` members — accept the analyzer's suggested lines verbatim.)

- [ ] **Step 6: Create the test project** `tests/Themia.Export.Tests/Themia.Export.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/neutral/Themia.Export/Themia.Export.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 7: Write the contract sanity test** `tests/Themia.Export.Tests/ContractTypeTests.cs`:

```csharp
using Themia.Export;
using Xunit;

namespace Themia.Export.Tests;

public sealed class ContractTypeTests
{
    [Fact]
    public void ExportColumn_defaults_are_none_and_auto()
    {
        var col = new ExportColumn<string> { Title = "Name", Value = s => s };

        Assert.Equal(AggregateKind.None, col.Aggregate);
        Assert.Equal(ColumnAlignment.Auto, col.Alignment);
        Assert.Null(col.NumberFormat);
        Assert.Null(col.Width);
        Assert.Equal("hi", col.Value("hi"));
    }

    [Fact]
    public void ExportResult_carries_bytes_type_and_name()
    {
        var r = new ExportResult([1, 2, 3], "text/csv", "x.csv");

        Assert.Equal([1, 2, 3], r.Content);
        Assert.Equal("text/csv", r.ContentType);
        Assert.Equal("x.csv", r.FileName);
    }
}
```

- [ ] **Step 8: Add both projects to the solution and build.**

Run:
```bash
dotnet sln Themia.sln add src/neutral/Themia.Export/Themia.Export.csproj
dotnet sln Themia.sln add tests/Themia.Export.Tests/Themia.Export.Tests.csproj
dotnet build src/neutral/Themia.Export/Themia.Export.csproj --no-incremental
dotnet test tests/Themia.Export.Tests/Themia.Export.Tests.csproj
```
Expected: build `0 Warning(s) 0 Error(s)` (fix any `RS0016` by adding the reported line to `PublicAPI.Unshipped.txt`); test PASS (2 passed).

- [ ] **Step 9: Commit.**

```bash
git add Directory.Packages.props src/neutral/Themia.Export tests/Themia.Export.Tests Themia.sln
git commit -m "feat: add Themia.Export contract types + CPM pin for ClosedXML"
```

---

## Task 2: Aggregate engine (internal)

**Files:**
- Create: `src/neutral/Themia.Export/Internal/RowProjector.cs`, `src/neutral/Themia.Export/Internal/AggregateComputer.cs`
- Test: `tests/Themia.Export.Tests/AggregateComputerTests.cs`

**Interfaces:**
- Consumes: `ExportColumn<T>`, `AggregateKind` (Task 1).
- Produces (internal, visible to `Themia.Export.Excel` + `Themia.Export.Tests`):
  - `RowProjector.Project<T>(IEnumerable<T> rows, IReadOnlyList<ExportColumn<T>> columns) -> object?[][]` (one `object?[]` per row, length = column count).
  - `AggregateComputer.Compute(AggregateKind kind, string title, IEnumerable<object?> values) -> object?` (returns boxed `decimal` for numeric kinds, the `title` string for `Label`, or `null` for `None`/empty).

- [ ] **Step 1: Write the failing test** `tests/Themia.Export.Tests/AggregateComputerTests.cs`:

```csharp
using Themia.Export.Internal;
using Xunit;

namespace Themia.Export.Tests;

public sealed class AggregateComputerTests
{
    [Fact]
    public void Sum_folds_numeric_skips_null()
    {
        object? r = AggregateComputer.Compute(AggregateKind.Sum, "T", new object?[] { 10, null, 2.5m });
        Assert.Equal(12.5m, r);
    }

    [Fact]
    public void Count_counts_non_null()
    {
        Assert.Equal(2m, AggregateComputer.Compute(AggregateKind.Count, "T", new object?[] { 1, null, "x" }));
    }

    [Fact]
    public void Average_blank_when_empty()
    {
        Assert.Null(AggregateComputer.Compute(AggregateKind.Average, "T", new object?[] { null, null }));
    }

    [Fact]
    public void Min_and_Max_over_numeric()
    {
        Assert.Equal(2m, AggregateComputer.Compute(AggregateKind.Min, "T", new object?[] { 5, 2, 9 }));
        Assert.Equal(9m, AggregateComputer.Compute(AggregateKind.Max, "T", new object?[] { 5, 2, 9 }));
    }

    [Fact]
    public void Label_returns_title_None_returns_null()
    {
        Assert.Equal("Total", AggregateComputer.Compute(AggregateKind.Label, "Total", new object?[] { 1 }));
        Assert.Null(AggregateComputer.Compute(AggregateKind.None, "T", new object?[] { 1 }));
    }

    [Fact]
    public void Non_numeric_in_numeric_aggregate_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AggregateComputer.Compute(AggregateKind.Sum, "Amount", new object?[] { 1, "oops" }));
        Assert.Contains("Amount", ex.Message);
    }
}
```

- [ ] **Step 2: Run to verify it fails.**

Run: `dotnet test tests/Themia.Export.Tests/Themia.Export.Tests.csproj --filter AggregateComputerTests`
Expected: FAIL — `RowProjector`/`AggregateComputer` do not exist.

- [ ] **Step 3: Implement `AggregateComputer.cs`:**

```csharp
using System.Globalization;

namespace Themia.Export.Internal;

/// <summary>Computes a column's summary-row cell. Shared by the CSV and Excel writers so both formats
/// produce identical numbers (no Excel SUM() formulas).</summary>
internal static class AggregateComputer
{
    /// <summary>Computes the summary cell for one column.</summary>
    /// <returns>A boxed <see cref="decimal"/> for numeric kinds, the <paramref name="title"/> for
    /// <see cref="AggregateKind.Label"/>, or <see langword="null"/> for <see cref="AggregateKind.None"/>
    /// or when no values participate.</returns>
    /// <exception cref="InvalidOperationException">A non-null value in a numeric aggregate is not convertible to a number.</exception>
    public static object? Compute(AggregateKind kind, string title, IEnumerable<object?> values)
    {
        if (kind == AggregateKind.None)
        {
            return null;
        }

        if (kind == AggregateKind.Label)
        {
            return title;
        }

        if (kind == AggregateKind.Count)
        {
            return (decimal)values.Count(v => v is not null);
        }

        var numbers = new List<decimal>();
        foreach (var value in values)
        {
            if (value is null)
            {
                continue;
            }

            try
            {
                numbers.Add(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                throw new InvalidOperationException(
                    $"Column '{title}' has an {kind} aggregate but value '{value}' is not numeric.", ex);
            }
        }

        if (numbers.Count == 0)
        {
            return null;
        }

        return kind switch
        {
            AggregateKind.Sum => numbers.Sum(),
            AggregateKind.Average => numbers.Sum() / numbers.Count,
            AggregateKind.Min => numbers.Min(),
            AggregateKind.Max => numbers.Max(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled aggregate kind."),
        };
    }
}
```

- [ ] **Step 4: Implement `RowProjector.cs`:**

```csharp
namespace Themia.Export.Internal;

/// <summary>Projects rows into a dense <c>object?[][]</c> matrix (one array per row) via the typed
/// selectors, in a single pass. No reflection.</summary>
internal static class RowProjector
{
    /// <summary>Materializes <paramref name="rows"/> into a matrix using each column's selector.</summary>
    public static object?[][] Project<T>(IEnumerable<T> rows, IReadOnlyList<ExportColumn<T>> columns)
    {
        var colCount = columns.Count;
        var matrix = new List<object?[]>();
        foreach (var row in rows)
        {
            var cells = new object?[colCount];
            for (var c = 0; c < colCount; c++)
            {
                cells[c] = columns[c].Value(row);
            }

            matrix.Add(cells);
        }

        return matrix.ToArray();
    }
}
```

- [ ] **Step 5: Run the tests.**

Run: `dotnet test tests/Themia.Export.Tests/Themia.Export.Tests.csproj --filter AggregateComputerTests`
Expected: PASS (6 passed).

- [ ] **Step 6: Commit.**

```bash
git add src/neutral/Themia.Export/Internal tests/Themia.Export.Tests/AggregateComputerTests.cs
git commit -m "feat: add shared aggregate engine + row projector for Themia.Export"
```

---

## Task 3: CSV exporter

**Files:**
- Create: `src/neutral/Themia.Export/Csv/ICsvExporter.cs`, `src/neutral/Themia.Export/Csv/CsvExporter.cs`
- Create: `src/neutral/Themia.Export/DependencyInjection/ExportServiceCollectionExtensions.cs`
- Modify: `src/neutral/Themia.Export/PublicAPI.Unshipped.txt`
- Modify: `src/neutral/Themia.Export/Themia.Export.csproj` (add `Microsoft.Extensions.DependencyInjection.Abstractions`)
- Test: `tests/Themia.Export.Tests/CsvExporterTests.cs`

**Interfaces:**
- Consumes: `RowProjector.Project`, `AggregateComputer.Compute`, `ExportColumn<T>`, `ReportHeader`, `ExportResult`.
- Produces: `ICsvExporter.Export<T>(IEnumerable<T> rows, IReadOnlyList<ExportColumn<T>> columns, IEnumerable<ReportHeader>? headers = null, string? fileName = null) -> ExportResult`; `CsvExporter : ICsvExporter`; `ExportServiceCollectionExtensions.AddThemiaExport(this IServiceCollection) -> IServiceCollection`.

- [ ] **Step 1: Add the DI abstraction reference** to `Themia.Export.csproj` (new `<PackageReference>` in the existing analyzer `<ItemGroup>` or a fresh one):

```xml
<PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
```
Verify `Microsoft.Extensions.DependencyInjection.Abstractions` already has a `<PackageVersion>` in `Directory.Packages.props` (it is used across the repo); if not, add one matching the repo's pinned `Microsoft.Extensions.*` version.

- [ ] **Step 2: Write the failing test** `tests/Themia.Export.Tests/CsvExporterTests.cs`:

```csharp
using System.Text;
using Themia.Export;
using Themia.Export.Csv;
using Xunit;

namespace Themia.Export.Tests;

public sealed class CsvExporterTests
{
    private sealed record Sale(string Product, decimal Amount);

    private static readonly ExportColumn<Sale>[] Columns =
    [
        new() { Title = "Product", Value = s => s.Product, Aggregate = AggregateKind.Label },
        new() { Title = "Amount", Value = s => s.Amount, Aggregate = AggregateKind.Sum },
    ];

    private static string Text(ExportResult r) =>
        new UTF8Encoding(false).GetString(r.Content.AsSpan(GetBomLength(r.Content)));

    private static int GetBomLength(byte[] b) =>
        b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;

    [Fact]
    public void Writes_header_data_and_summary_rows()
    {
        var rows = new[] { new Sale("Apple", 10m), new Sale("Pear", 5m) };

        var result = new CsvExporter().Export(rows, Columns);

        Assert.Equal("text/csv", result.ContentType);
        var lines = Text(result).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Product,Amount", lines[0]);
        Assert.Equal("Apple,10", lines[1]);
        Assert.Equal("Pear,5", lines[2]);
        Assert.Equal("Product,15", lines[3]); // Label echoes the title; Sum = 15
    }

    [Fact]
    public void Quotes_fields_with_comma_quote_or_newline()
    {
        var cols = new ExportColumn<string>[] { new() { Title = "V", Value = s => s } };
        var rows = new[] { "a,b", "he said \"hi\"", "line1\nline2" };

        var lines = Text(new CsvExporter().Export(rows, cols)).Split("\r\n");

        Assert.Equal("\"a,b\"", lines[1]);
        Assert.Equal("\"he said \"\"hi\"\"\"", lines[2]);
        Assert.Equal("\"line1\nline2\"", lines[3]);
    }

    [Fact]
    public void Report_headers_are_padded_to_column_count()
    {
        var rows = new[] { new Sale("Apple", 10m) };
        var headers = new ReportHeader[] { new("My Report") };

        var first = Text(new CsvExporter().Export(rows, Columns, headers)).Split("\r\n")[0];

        Assert.Equal("My Report,", first); // 2 columns => one trailing empty field
    }

    [Fact]
    public void Empty_rows_still_emit_title_and_summary()
    {
        var lines = Text(new CsvExporter().Export(Array.Empty<Sale>(), Columns)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Product,Amount", lines[0]);
        Assert.Equal("Product,", lines[1]); // Label title; Sum over no rows => blank
    }

    [Fact]
    public void Starts_with_utf8_bom_for_excel_thai_support()
    {
        var bytes = new CsvExporter().Export(new[] { new Sale("กล้วย", 1m) }, Columns).Content;
        Assert.True(bytes is [0xEF, 0xBB, 0xBF, ..]);
    }
}
```

- [ ] **Step 3: Run to verify it fails.**

Run: `dotnet test tests/Themia.Export.Tests/Themia.Export.Tests.csproj --filter CsvExporterTests`
Expected: FAIL — `ICsvExporter`/`CsvExporter` do not exist.

- [ ] **Step 4: Implement `Csv/ICsvExporter.cs`:**

```csharp
namespace Themia.Export.Csv;

/// <summary>Writes a row collection to a CSV file (RFC 4180), with optional report-header lines and a
/// computed summary row. CSV is data-only — <see cref="ExportColumn{T}.NumberFormat"/> is not applied.</summary>
public interface ICsvExporter
{
    /// <summary>Exports <paramref name="rows"/> as CSV.</summary>
    /// <param name="rows">The data rows.</param>
    /// <param name="columns">The column descriptors (at least one).</param>
    /// <param name="headers">Optional title lines above the table.</param>
    /// <param name="fileName">Optional download name; defaults to <c>report-{timestamp}.csv</c>.</param>
    /// <returns>The produced CSV file.</returns>
    ExportResult Export<T>(
        IEnumerable<T> rows,
        IReadOnlyList<ExportColumn<T>> columns,
        IEnumerable<ReportHeader>? headers = null,
        string? fileName = null);
}
```

- [ ] **Step 5: Implement `Csv/CsvExporter.cs`:**

```csharp
using System.Globalization;
using System.Text;
using Themia.Export.Internal;

namespace Themia.Export.Csv;

/// <summary>Default <see cref="ICsvExporter"/>. Stateless and thread-safe.</summary>
public sealed class CsvExporter : ICsvExporter
{
    private const string CsvContentType = "text/csv";

    /// <inheritdoc />
    public ExportResult Export<T>(
        IEnumerable<T> rows,
        IReadOnlyList<ExportColumn<T>> columns,
        IEnumerable<ReportHeader>? headers = null,
        string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("At least one column is required.", nameof(columns));
        }

        var colCount = columns.Count;
        var matrix = RowProjector.Project(rows, columns);
        var sb = new StringBuilder();

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                // Pad to the column count so every line has the same field count (rectangular CSV).
                var fields = new string[colCount];
                fields[0] = header.Line;
                for (var i = 1; i < colCount; i++)
                {
                    fields[i] = string.Empty;
                }

                AppendLine(sb, fields.Select(Quote));
            }
        }

        AppendLine(sb, columns.Select(c => Quote(c.Title)));

        foreach (var cells in matrix)
        {
            AppendLine(sb, cells.Select(v => Quote(Render(v))));
        }

        if (columns.Any(c => c.Aggregate != AggregateKind.None))
        {
            var summary = new string[colCount];
            for (var c = 0; c < colCount; c++)
            {
                var value = AggregateComputer.Compute(columns[c].Aggregate, columns[c].Title, Column(matrix, c));
                summary[c] = Quote(Render(value));
            }

            AppendLine(sb, summary);
        }

        // UTF-8 BOM so Excel detects the encoding (correct rendering of Thai/non-ASCII content).
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(sb.ToString());
        return new ExportResult(bytes, CsvContentType, fileName ?? DefaultFileName());
    }

    private static IEnumerable<object?> Column(object?[][] matrix, int c)
    {
        foreach (var row in matrix)
        {
            yield return row[c];
        }
    }

    private static string Render(object? value) => value switch
    {
        null => string.Empty,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Quote(string field)
    {
        if (field.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static void AppendLine(StringBuilder sb, IEnumerable<string> fields)
    {
        sb.Append(string.Join(',', fields)).Append("\r\n");
    }

    private static string DefaultFileName() =>
        "report-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".csv";
}
```

- [ ] **Step 6: Implement `DependencyInjection/ExportServiceCollectionExtensions.cs`:**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Export.Csv;

namespace Themia.Export.DependencyInjection;

/// <summary>DI entry point for the neutral export contract.</summary>
public static class ExportServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ICsvExporter"/> (stateless singleton).</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddThemiaExport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICsvExporter, CsvExporter>();
        return services;
    }
}
```

- [ ] **Step 7: Add the new public API lines** to `src/neutral/Themia.Export/PublicAPI.Unshipped.txt`:

```text
Themia.Export.Csv.CsvExporter
Themia.Export.Csv.CsvExporter.CsvExporter() -> void
Themia.Export.Csv.CsvExporter.Export<T>(System.Collections.Generic.IEnumerable<T>! rows, System.Collections.Generic.IReadOnlyList<Themia.Export.ExportColumn<T>!>! columns, System.Collections.Generic.IEnumerable<Themia.Export.ReportHeader>? headers = null, string? fileName = null) -> Themia.Export.ExportResult!
Themia.Export.Csv.ICsvExporter
Themia.Export.Csv.ICsvExporter.Export<T>(System.Collections.Generic.IEnumerable<T>! rows, System.Collections.Generic.IReadOnlyList<Themia.Export.ExportColumn<T>!>! columns, System.Collections.Generic.IEnumerable<Themia.Export.ReportHeader>? headers = null, string? fileName = null) -> Themia.Export.ExportResult!
Themia.Export.DependencyInjection.ExportServiceCollectionExtensions
static Themia.Export.DependencyInjection.ExportServiceCollectionExtensions.AddThemiaExport(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

- [ ] **Step 8: Build and test.**

Run:
```bash
dotnet build src/neutral/Themia.Export/Themia.Export.csproj --no-incremental
dotnet test tests/Themia.Export.Tests/Themia.Export.Tests.csproj
```
Expected: build `0 Warning(s) 0 Error(s)`; all tests PASS. Fix any `RS0016` by adding the reported symbol to `PublicAPI.Unshipped.txt`.

- [ ] **Step 9: Commit.**

```bash
git add src/neutral/Themia.Export tests/Themia.Export.Tests
git commit -m "feat: add CSV exporter + AddThemiaExport for Themia.Export"
```

---

## Task 4: `Themia.Export.Excel` skeleton + options

**Files:**
- Create: `src/neutral/Themia.Export.Excel/Themia.Export.Excel.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- Create: `src/neutral/Themia.Export.Excel/ColumnWidthMode.cs`, `ExcelExportOptions.cs`, `Excel/IExcelExporter.cs`
- Create: `tests/Themia.Export.Excel.Tests/Themia.Export.Excel.Tests.csproj`

**Interfaces:**
- Consumes: `ExportColumn<T>`, `ReportHeader`, `ExportResult` (Task 1); `ClosedXML.Excel.XLTableTheme`.
- Produces: `enum ColumnWidthMode { Estimate, Measure, None }`; `ExcelExportOptions` { `string SheetName`, `XLTableTheme? TableTheme`, `string FontName`, `bool FreezeHeaderRow`, `ColumnWidthMode WidthMode`, `int WidthSampleRows` }; `IExcelExporter.Export<T>(IEnumerable<T> rows, IReadOnlyList<ExportColumn<T>> columns, ExcelExportOptions? options = null, IEnumerable<ReportHeader>? headers = null, string? fileName = null) -> ExportResult`.

- [ ] **Step 1: Create the project** `src/neutral/Themia.Export.Excel/Themia.Export.Excel.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.Export.Excel</PackageId>
    <Description>ClosedXML .xlsx backend for Themia.Export: typed columns, themed tables, report headers, and computed summary rows. Styles by range/column and sizes columns without full-sheet auto-fit.</Description>
    <PackageTags>themia;export;excel;xlsx;closedxml;report</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Export/Themia.Export.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="ClosedXML" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Export.Excel.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create `PublicAPI.Shipped.txt`** with one line: `#nullable enable`.

- [ ] **Step 3: Create `ColumnWidthMode.cs`:**

```csharp
namespace Themia.Export.Excel;

/// <summary>How the Excel writer sizes columns that have no explicit <see cref="Themia.Export.ExportColumn{T}.Width"/>.</summary>
public enum ColumnWidthMode
{
    /// <summary>Font-free width estimated from sampled cell character lengths. Deterministic; CI-safe. Default.</summary>
    Estimate,
    /// <summary>ClosedXML glyph measurement over the sampled rows (needs font metrics).</summary>
    Measure,
    /// <summary>Leave default widths.</summary>
    None,
}
```

- [ ] **Step 4: Create `ExcelExportOptions.cs`:**

```csharp
using ClosedXML.Excel;

namespace Themia.Export.Excel;

/// <summary>Workbook-level options for an Excel export.</summary>
public sealed class ExcelExportOptions
{
    /// <summary>The worksheet name. Defaults to <c>"Sheet1"</c>.</summary>
    public string SheetName { get; init; } = "Sheet1";

    /// <summary>The ClosedXML table theme. <see langword="null"/> uses <c>TableStyleMedium2</c>.</summary>
    public XLTableTheme? TableTheme { get; init; }

    /// <summary>The workbook font. Defaults to <c>"Calibri"</c>.</summary>
    public string FontName { get; init; } = "Calibri";

    /// <summary>Freeze the rows through the table header. Defaults to <see langword="true"/>.</summary>
    public bool FreezeHeaderRow { get; init; } = true;

    /// <summary>Column-sizing strategy. Defaults to <see cref="ColumnWidthMode.Estimate"/>.</summary>
    public ColumnWidthMode WidthMode { get; init; } = ColumnWidthMode.Estimate;

    /// <summary>Rows sampled when sizing columns (Estimate/Measure). Defaults to 50.</summary>
    public int WidthSampleRows { get; init; } = 50;
}
```

- [ ] **Step 5: Create `Excel/IExcelExporter.cs`:**

```csharp
using Themia.Export;

namespace Themia.Export.Excel;

/// <summary>Writes a row collection to an <c>.xlsx</c> workbook with a themed table, optional report
/// headers, per-column number format/alignment, and a computed summary row.</summary>
public interface IExcelExporter
{
    /// <summary>Exports <paramref name="rows"/> as an Excel workbook.</summary>
    /// <param name="rows">The data rows.</param>
    /// <param name="columns">The column descriptors (at least one).</param>
    /// <param name="options">Workbook options; <see langword="null"/> uses defaults.</param>
    /// <param name="headers">Optional title lines above the table.</param>
    /// <param name="fileName">Optional download name; defaults to <c>report-{timestamp}.xlsx</c>.</param>
    /// <returns>The produced workbook.</returns>
    /// <exception cref="InvalidOperationException">A non-numeric value appears in an aggregated column.</exception>
    ExportResult Export<T>(
        IEnumerable<T> rows,
        IReadOnlyList<ExportColumn<T>> columns,
        ExcelExportOptions? options = null,
        IEnumerable<ReportHeader>? headers = null,
        string? fileName = null);
}
```

- [ ] **Step 6: Populate `PublicAPI.Unshipped.txt`:**

```text
#nullable enable
Themia.Export.Excel.ColumnWidthMode
Themia.Export.Excel.ColumnWidthMode.Estimate = 0 -> Themia.Export.Excel.ColumnWidthMode
Themia.Export.Excel.ColumnWidthMode.Measure = 1 -> Themia.Export.Excel.ColumnWidthMode
Themia.Export.Excel.ColumnWidthMode.None = 2 -> Themia.Export.Excel.ColumnWidthMode
Themia.Export.Excel.ExcelExportOptions
Themia.Export.Excel.ExcelExportOptions.ExcelExportOptions() -> void
Themia.Export.Excel.ExcelExportOptions.FontName.get -> string!
Themia.Export.Excel.ExcelExportOptions.FontName.init -> void
Themia.Export.Excel.ExcelExportOptions.FreezeHeaderRow.get -> bool
Themia.Export.Excel.ExcelExportOptions.FreezeHeaderRow.init -> void
Themia.Export.Excel.ExcelExportOptions.SheetName.get -> string!
Themia.Export.Excel.ExcelExportOptions.SheetName.init -> void
Themia.Export.Excel.ExcelExportOptions.TableTheme.get -> ClosedXML.Excel.XLTableTheme?
Themia.Export.Excel.ExcelExportOptions.TableTheme.init -> void
Themia.Export.Excel.ExcelExportOptions.WidthMode.get -> Themia.Export.Excel.ColumnWidthMode
Themia.Export.Excel.ExcelExportOptions.WidthMode.init -> void
Themia.Export.Excel.ExcelExportOptions.WidthSampleRows.get -> int
Themia.Export.Excel.ExcelExportOptions.WidthSampleRows.init -> void
Themia.Export.Excel.IExcelExporter
Themia.Export.Excel.IExcelExporter.Export<T>(System.Collections.Generic.IEnumerable<T>! rows, System.Collections.Generic.IReadOnlyList<Themia.Export.ExportColumn<T>!>! columns, Themia.Export.Excel.ExcelExportOptions? options = null, System.Collections.Generic.IEnumerable<Themia.Export.ReportHeader>? headers = null, string? fileName = null) -> Themia.Export.ExportResult!
```

- [ ] **Step 7: Create the test project** `tests/Themia.Export.Excel.Tests/Themia.Export.Excel.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/neutral/Themia.Export.Excel/Themia.Export.Excel.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 8: Add to solution and build.**

Run:
```bash
dotnet sln Themia.sln add src/neutral/Themia.Export.Excel/Themia.Export.Excel.csproj
dotnet sln Themia.sln add tests/Themia.Export.Excel.Tests/Themia.Export.Excel.Tests.csproj
dotnet build src/neutral/Themia.Export.Excel/Themia.Export.Excel.csproj --no-incremental
```
Expected: build `0 Warning(s) 0 Error(s)`.

- [ ] **Step 9: Commit.**

```bash
git add src/neutral/Themia.Export.Excel tests/Themia.Export.Excel.Tests Themia.sln
git commit -m "feat: add Themia.Export.Excel skeleton + options"
```

---

## Task 5: Excel exporter

**Files:**
- Create: `src/neutral/Themia.Export.Excel/Excel/ExcelExporter.cs`
- Create: `src/neutral/Themia.Export.Excel/DependencyInjection/ExcelExportServiceCollectionExtensions.cs`
- Modify: `src/neutral/Themia.Export.Excel/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Export.Excel.Tests/ExcelExporterTests.cs`

**Interfaces:**
- Consumes: `RowProjector.Project`, `AggregateComputer.Compute` (internal, visible here), `ExcelExportOptions`, `IExcelExporter`, ClosedXML API.
- Produces: `ExcelExporter : IExcelExporter`; `ExcelExportServiceCollectionExtensions.AddThemiaExcelExport(this IServiceCollection) -> IServiceCollection`.

- [ ] **Step 1: Write the failing test** `tests/Themia.Export.Excel.Tests/ExcelExporterTests.cs`:

```csharp
using ClosedXML.Excel;
using Themia.Export;
using Themia.Export.Csv;
using Themia.Export.Excel;
using Xunit;

namespace Themia.Export.Excel.Tests;

public sealed class ExcelExporterTests
{
    private sealed record Sale(string Product, decimal Amount);

    private static readonly ExportColumn<Sale>[] Columns =
    [
        new() { Title = "Product", Value = s => s.Product, Aggregate = AggregateKind.Label },
        new() { Title = "Amount", Value = s => s.Amount, NumberFormat = "#,##0.00", Aggregate = AggregateKind.Sum },
    ];

    private static readonly Sale[] Rows = [new("Apple", 10m), new("Pear", 5m)];

    private static IXLWorksheet Open(ExportResult r)
    {
        using var ms = new MemoryStream(r.Content);
        return new XLWorkbook(ms).Worksheet(1);
    }

    [Fact]
    public void Writes_titles_data_and_summary_with_number_format()
    {
        var result = new ExcelExporter().Export(Rows, Columns);

        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", result.ContentType);
        var ws = Open(result);
        Assert.Equal("Product", ws.Cell(1, 1).GetString());
        Assert.Equal("Amount", ws.Cell(1, 2).GetString());
        Assert.Equal(10m, ws.Cell(2, 2).GetValue<decimal>());
        Assert.Equal("#,##0.00", ws.Cell(2, 2).Style.NumberFormat.Format);
        // Summary row is directly below the 2 data rows (row 4): Label echoes title, Sum = 15.
        Assert.Equal("Product", ws.Cell(4, 1).GetString());
        Assert.Equal(15m, ws.Cell(4, 2).GetValue<decimal>());
    }

    [Fact]
    public void Summary_matches_csv_numbers_exactly()
    {
        var excel = new ExcelExporter().Export(Rows, Columns);
        var ws = Open(excel);
        var excelSum = ws.Cell(4, 2).GetValue<decimal>();

        // Same input through the CSV writer must yield the same summary number.
        var csv = new CsvExporter().Export(Rows, Columns);
        var csvText = System.Text.Encoding.UTF8.GetString(csv.Content);
        Assert.Contains("Product,15", csvText);
        Assert.Equal(15m, excelSum);
    }

    [Fact]
    public void Creates_a_themed_table_and_freezes_header()
    {
        var ws = Open(new ExcelExporter().Export(Rows, Columns));
        Assert.NotEmpty(ws.Tables);
        Assert.True(ws.SheetView.SplitRow > 0); // header frozen
    }

    [Fact]
    public void Report_headers_render_above_the_table()
    {
        var ws = Open(new ExcelExporter().Export(Rows, Columns, headers: new ReportHeader[] { new("My Report") }));
        Assert.Equal("My Report", ws.Cell(1, 1).GetString());
        Assert.Equal("Product", ws.Cell(2, 1).GetString()); // table header pushed down one row
    }

    [Fact]
    public void Non_numeric_in_aggregated_column_throws()
    {
        var cols = new ExportColumn<string>[]
        {
            new() { Title = "Amount", Value = s => s, Aggregate = AggregateKind.Sum },
        };
        Assert.Throws<InvalidOperationException>(() => new ExcelExporter().Export(new[] { "oops" }, cols));
    }

    [Fact]
    public void Large_export_completes_in_estimate_mode()
    {
        // 5000 rows, default WidthMode.Estimate => no glyph measurement, no full-sheet auto-fit.
        var rows = Enumerable.Range(0, 5000).Select(i => new Sale("P" + i, i)).ToArray();

        var ws = Open(new ExcelExporter().Export(rows, Columns));

        // Layout: title row 1, data rows 2..5001, summary row 5002.
        Assert.Equal("P4999", ws.Cell(5001, 1).GetString());
        Assert.Equal(12_497_500m, ws.Cell(5002, 2).GetValue<decimal>()); // Sum of 0..4999
    }
}
```

- [ ] **Step 2: Run to verify it fails.**

Run: `dotnet test tests/Themia.Export.Excel.Tests/Themia.Export.Excel.Tests.csproj`
Expected: FAIL — `ExcelExporter` does not exist.

- [ ] **Step 3: Implement `Excel/ExcelExporter.cs`:**

```csharp
using System.Globalization;
using ClosedXML.Excel;
using Themia.Export;
using Themia.Export.Internal;

namespace Themia.Export.Excel;

/// <summary>Default <see cref="IExcelExporter"/>. Stateless and thread-safe — a fresh
/// <see cref="XLWorkbook"/> per call. Styles by range/column (never per cell) and sizes columns
/// without full-sheet auto-fit.</summary>
public sealed class ExcelExporter : IExcelExporter
{
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <inheritdoc />
    public ExportResult Export<T>(
        IEnumerable<T> rows,
        IReadOnlyList<ExportColumn<T>> columns,
        ExcelExportOptions? options = null,
        IEnumerable<ReportHeader>? headers = null,
        string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("At least one column is required.", nameof(columns));
        }

        options ??= new ExcelExportOptions();
        var colCount = columns.Count;
        var matrix = RowProjector.Project(rows, columns);
        var headerList = headers?.ToList() ?? [];

        using var workbook = new XLWorkbook();
        workbook.Style.Font.FontName = options.FontName;
        var ws = workbook.Worksheets.Add(options.SheetName);

        // 1) Report header lines (merged across the columns).
        for (var h = 0; h < headerList.Count; h++)
        {
            ws.Cell(h + 1, 1).Value = headerList[h].Line;
            ws.Range(h + 1, 1, h + 1, colCount).Merge();
        }

        var titleRow = headerList.Count + 1;
        var firstDataRow = titleRow + 1;
        var rowCount = matrix.Length;
        var lastDataRow = firstDataRow + rowCount - 1; // == titleRow when rowCount == 0

        // 2) Title row.
        for (var c = 0; c < colCount; c++)
        {
            ws.Cell(titleRow, c + 1).Value = columns[c].Title;
        }

        // 3) Bulk-insert the data matrix in one call (no per-cell loop).
        if (rowCount > 0)
        {
            ws.Cell(firstDataRow, 1).InsertData(matrix);
        }

        // 4) Themed table over the title + data block (header only when there are no data rows).
        var tableRange = ws.Range(titleRow, 1, Math.Max(titleRow, lastDataRow), colCount);
        var table = tableRange.CreateTable();
        table.Theme = options.TableTheme ?? XLTableTheme.TableStyleMedium2;

        // 5) Per-column number format + alignment, applied once to the data range (O(columns)).
        if (rowCount > 0)
        {
            for (var c = 0; c < colCount; c++)
            {
                var range = ws.Range(firstDataRow, c + 1, lastDataRow, c + 1).Style;
                if (!string.IsNullOrEmpty(columns[c].NumberFormat))
                {
                    range.NumberFormat.Format = columns[c].NumberFormat;
                }

                var alignment = Map(columns[c].Alignment);
                if (alignment is { } a)
                {
                    range.Alignment.Horizontal = a;
                }
            }
        }

        // 6) Summary row (literals from the shared engine), directly below the table.
        if (columns.Any(c => c.Aggregate != AggregateKind.None))
        {
            var summaryRow = Math.Max(titleRow, lastDataRow) + 1;
            for (var c = 0; c < colCount; c++)
            {
                var value = AggregateComputer.Compute(columns[c].Aggregate, columns[c].Title, Column(matrix, c));
                var cell = ws.Cell(summaryRow, c + 1);
                switch (value)
                {
                    case decimal d:
                        cell.Value = d;
                        if (!string.IsNullOrEmpty(columns[c].NumberFormat))
                        {
                            cell.Style.NumberFormat.Format = columns[c].NumberFormat;
                        }

                        break;
                    case string s:
                        cell.Value = s;
                        break;
                }
            }

            var summary = ws.Range(summaryRow, 1, summaryRow, colCount).Style;
            summary.Font.Bold = true;
            summary.Border.TopBorder = XLBorderStyleValues.Thin;
        }

        // 7) Column widths.
        ApplyWidths(ws, columns, matrix, firstDataRow, options);

        if (options.FreezeHeaderRow)
        {
            ws.SheetView.FreezeRows(titleRow);
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return new ExportResult(stream.ToArray(), XlsxContentType, fileName ?? DefaultFileName());
    }

    private static IEnumerable<object?> Column(object?[][] matrix, int c)
    {
        foreach (var row in matrix)
        {
            yield return row[c];
        }
    }

    private static XLAlignmentHorizontalValues? Map(ColumnAlignment alignment) => alignment switch
    {
        ColumnAlignment.Left => XLAlignmentHorizontalValues.Left,
        ColumnAlignment.Center => XLAlignmentHorizontalValues.Center,
        ColumnAlignment.Right => XLAlignmentHorizontalValues.Right,
        _ => null, // Auto: leave ClosedXML's type-based default.
    };

    private static void ApplyWidths<T>(
        IXLWorksheet ws,
        IReadOnlyList<ExportColumn<T>> columns,
        object?[][] matrix,
        int firstDataRow,
        ExcelExportOptions options)
    {
        const double charFactor = 1.1;
        const double minWidth = 8;
        const double maxWidth = 80;
        var sample = Math.Min(options.WidthSampleRows, matrix.Length);

        for (var c = 0; c < columns.Count; c++)
        {
            var column = ws.Column(c + 1);
            if (columns[c].Width is { } explicitWidth)
            {
                column.Width = explicitWidth;
                continue;
            }

            switch (options.WidthMode)
            {
                case ColumnWidthMode.None:
                    break;

                case ColumnWidthMode.Measure when sample > 0:
                    column.AdjustToContents(firstDataRow, firstDataRow + sample - 1);
                    break;

                case ColumnWidthMode.Estimate:
                    var maxLen = columns[c].Title.Length;
                    for (var r = 0; r < sample; r++)
                    {
                        var text = matrix[r][c]?.ToString();
                        if (text is not null && text.Length > maxLen)
                        {
                            maxLen = text.Length;
                        }
                    }

                    column.Width = Math.Clamp(maxLen * charFactor, minWidth, maxWidth);
                    break;
            }
        }
    }

    private static string DefaultFileName() =>
        "report-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".xlsx";
}
```

- [ ] **Step 4: Implement `DependencyInjection/ExcelExportServiceCollectionExtensions.cs`:**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Themia.Export.Excel.DependencyInjection;

/// <summary>DI entry point for the ClosedXML Excel backend.</summary>
public static class ExcelExportServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IExcelExporter"/> (stateless singleton).</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    public static IServiceCollection AddThemiaExcelExport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IExcelExporter, ExcelExporter>();
        return services;
    }
}
```

- [ ] **Step 5: Add the new public API lines** to `src/neutral/Themia.Export.Excel/PublicAPI.Unshipped.txt`:

```text
Themia.Export.Excel.DependencyInjection.ExcelExportServiceCollectionExtensions
Themia.Export.Excel.ExcelExporter
Themia.Export.Excel.ExcelExporter.ExcelExporter() -> void
Themia.Export.Excel.ExcelExporter.Export<T>(System.Collections.Generic.IEnumerable<T>! rows, System.Collections.Generic.IReadOnlyList<Themia.Export.ExportColumn<T>!>! columns, Themia.Export.Excel.ExcelExportOptions? options = null, System.Collections.Generic.IEnumerable<Themia.Export.ReportHeader>? headers = null, string? fileName = null) -> Themia.Export.ExportResult!
static Themia.Export.Excel.DependencyInjection.ExcelExportServiceCollectionExtensions.AddThemiaExcelExport(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

- [ ] **Step 6: Build and test.**

Run:
```bash
dotnet build src/neutral/Themia.Export.Excel/Themia.Export.Excel.csproj --no-incremental
dotnet test tests/Themia.Export.Excel.Tests/Themia.Export.Excel.Tests.csproj
```
Expected: build `0 Warning(s) 0 Error(s)`; all tests PASS (6 passed). If `ws.SheetView.SplitRow` is not the property your ClosedXML version exposes for frozen rows, read the freeze state via the equivalent pane property the build/IntelliSense surfaces — the assertion's intent is "header row is frozen."

- [ ] **Step 7: Commit.**

```bash
git add src/neutral/Themia.Export.Excel tests/Themia.Export.Excel.Tests
git commit -m "feat: add ClosedXML Excel exporter + AddThemiaExcelExport"
```

---

## Task 6: Docs — CHANGELOG + architecture catalog

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `docs/themia-architecture-overview.md` (the `Themia.Modules.Export` catalog row + the Idevs disposition row)

- [ ] **Step 1: Add a CHANGELOG entry.** Under `## [Unreleased]` add:

```markdown
### Added
- **`Themia.Export`** — neutral tabular-data export contract + CSV writer: typed `ExportColumn<T>`
  selectors, report headers, and computed summary rows (`AggregateKind`). `net8.0;net10.0`, no
  framework dependency, Serenity-free. `AddThemiaExport()` registers `ICsvExporter`.
- **`Themia.Export.Excel`** — ClosedXML `.xlsx` backend over the same contract: themed tables,
  per-column number format/alignment, computed summary rows, and font-free column sizing (no
  full-sheet auto-fit). `AddThemiaExcelExport()` registers `IExcelExporter`.
```

- [ ] **Step 2: Update the catalog row** in `docs/themia-architecture-overview.md`. Change the `Themia.Modules.Export` row's status from `⬜ to-spec` to reflect the realized shape:

```markdown
| `Themia.Export` + `Themia.Export.Excel` | **Idevs** `IReportBaseModel`/`IdevsExportRequest`/ClosedXML (Excel), de-Serenity-ized | ✅ **built** (0.7.x — two stateless neutral cores: typed columns, CSV + xlsx, computed summary rows; no tenant module — the transform is stateless) |
```
And in the Idevs disposition table, update the `Models/IReportBaseModel`/`IdevsExportRequest`/`IdevsContentResult` (ClosedXML) row's target to `Themia.Export` / `Themia.Export.Excel`.

- [ ] **Step 3: Full-solution build + test to confirm nothing regressed.**

Run:
```bash
dotnet build Themia.sln --no-incremental
dotnet test Themia.sln --filter "Category!=Integration"
```
Expected: build `0 Warning(s) 0 Error(s)`; all unit tests PASS (including the new Export + Export.Excel suites).

- [ ] **Step 4: Commit.**

```bash
git add CHANGELOG.md docs/themia-architecture-overview.md
git commit -m "docs: record Themia.Export + Themia.Export.Excel in changelog and catalog"
```

---

## Notes for the implementer

- **PublicAPI churn:** records and enums emit compiler-generated public members the analyzer wants listed (`Deconstruct`, `Equals`, `==`, `ToString`, `<Clone>$`, `value___` for enums, etc.). When a clean build reports `RS0016`, copy the exact symbol string from the diagnostic into `PublicAPI.Unshipped.txt` — do not hand-guess them. The lines given in each task cover the hand-authored surface; let the analyzer tell you about the rest.
- **Version:** do not bump `<Version>` — release tooling owns the version at release time. The catalog text says "0.7.x" as a forward reference only.
- **No `Themia.Export.AspNetCore`:** hosts stream `ExportResult` with one line (`Results.File(...)`). Don't add an ASP.NET package (YAGNI).
- **`fileName` default uses `DateTime.Now`:** tests that assert file names pass an explicit `fileName` to stay deterministic.
