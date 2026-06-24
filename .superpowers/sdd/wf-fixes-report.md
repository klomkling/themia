# wf-fixes-report — high-effort review batch

Branch: `feat/themia-export-spec`  
Date: 2026-06-24

## Items applied

| # | Description | Files changed | Test |
|---|---|---|---|
| 1 | Duplicate-title guard in `ExcelExporter.Export` — `HashSet<string>(OrdinalIgnoreCase)` before workbook build; throws `ArgumentException` naming the dup | `ExcelExporter.cs` | Added `Duplicate_column_title_throws_argument_exception` |
| 2 | Shared invariant cell renderer — new `CellText.Invariant(object?)` in `Themia.Export/Internal/CellText.cs`; `CsvExporter.Render` deleted and replaced; `ExcelExporter.ApplyWidths` Estimate branch switched from `?.ToString()` to `CellText.Invariant(…).Length` | `CellText.cs` (new), `CsvExporter.cs`, `ExcelExporter.cs` | No new test (internal; covered by existing CSV render tests) |
| 3 | Measure mode empty-export fix — `case ColumnWidthMode.Measure when sample > 0:` replaced with unconditional `case ColumnWidthMode.Measure:` that measures `titleRow..titleRow` when `sample == 0` | `ExcelExporter.cs` | Covered by existing `Empty_rows_writes_header_only_table_and_blank_summary` + `Measure_mode_width_accounts_for_title_row` |
| 4 | Title-row alignment — header cells now receive `Alignment.Horizontal` from the column's `Alignment` property | `ExcelExporter.cs` | Added `Title_cell_carries_column_alignment` |
| 5 | `RowProjector.Project` fast path — `TryGetNonEnumeratedCount` allocates `new object?[n][]` and fills by index when count is known; falls back to `List<object?[]>` otherwise | `RowProjector.cs` | Behavior-identical; covered by all existing tests |
| 6 | `Column` helper consolidated — private `Column(matrix, c)` deleted from both `CsvExporter` and `ExcelExporter`; moved to `RowProjector.Column(matrix, index)` (internal static, accessible to `Themia.Export.Excel` via existing `InternalsVisibleTo`) | `RowProjector.cs`, `CsvExporter.cs`, `ExcelExporter.cs` | No new test; all existing aggregate tests exercise this path |
| 7 | "Byte-identical" claim corrected — `AggregateComputer` class `<summary>` updated to say "computed decimal value agrees" and explains rendering divergence; `ExcelExporterTests.Summary_matches_csv_numbers_exactly` renamed to `Summary_value_matches_csv_computed_value` with clarifying comment | `AggregateComputer.cs`, `ExcelExporterTests.cs` | Existing test preserved |
| 8 | Default filename millisecond precision — `yyyyMMddHHmmss` → `yyyyMMddHHmmssfff` in both `CsvExporter.DefaultFileName` and `ExcelExporter.DefaultFileName`; `fileName` param doc updated on `ICsvExporter` and `IExcelExporter` | `CsvExporter.cs`, `ExcelExporter.cs`, `ICsvExporter.cs`, `IExcelExporter.cs` | No test (pure timestamp format change) |
| 9 | Intentional-materialization comment — added to `RowProjector` class `<summary>` | `RowProjector.cs` | n/a |

## PublicAPI changes

None. All new/moved members are `internal`. `CellText.Invariant` and `RowProjector.Column` are
internal to `Themia.Export` (visible to `Themia.Export.Excel` and `Themia.Export.Tests` via the
existing `<InternalsVisibleTo>` in the `.csproj`). No entries added to `PublicAPI.Unshipped.txt`.

## Build result

`dotnet build Themia.sln --no-incremental` → **0 warnings, 0 errors**

## Test results

| Suite | Total | Passed | Failed |
|---|---|---|---|
| `Themia.Export.Tests` | 17 | 17 | 0 |
| `Themia.Export.Excel.Tests` | 16 | 16 | 0 |

## Judgment calls

- Item 2 (`CellText.Invariant`): `CsvExporter` still carries `using System.Globalization` because `DefaultFileName` uses `CultureInfo.InvariantCulture` for the timestamp format — the using is not dead.
- Item 3 (`Measure` empty fix): the `when sample > 0` guard was removed entirely from the `case` arm, not just loosened, so an empty export no longer silently falls through to the default (no-op). `ColumnWidthMode.None` case is unchanged.
- Item 8: `DateTime.Now` retained in `DefaultFileName` as per the task instructions ("DateTime.Now is fine here — this is production code").
