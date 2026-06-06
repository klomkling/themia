# Vendored Code — SilkierQuartz

## Source

- Repository: https://github.com/maikebing/SilkierQuartz
- Upstream commit SHA: `4b974e080d369c588194e84642a9be875175f3fd`
- Licence: MIT (see `THIRD-PARTY-NOTICES/SilkierQuartz-LICENSE.txt`)

## What is vendored (Task 2)

Only the **RecentHistory plugin** from `src/Quartz.Plugins.RecentHistory/`:

| Upstream file | Vendored to | Notes |
|---|---|---|
| `IExecutionHistoryStore.cs` | `History/IExecutionHistoryStore.cs` | Includes `ExecutionHistoryEntry` and `JobStats` |
| `Impl/InProcExecutionHistoryStore.cs` | `History/InProcExecutionHistoryStore.cs` | Dictionary-backed in-memory store |
| `ExecutionHistoryPlugin.cs` | `History/ExecutionHistoryPlugin.cs` | `ISchedulerPlugin` + `IJobListener` |
| `Extensions.cs` | `History/SchedulerContextExtensions.cs` | Scheduler context helpers |

**NOT vendored from the upstream plugin:**
- `ExecutionHistoryStoreOptions.cs` — builder for the relational store; not needed
- `Impl/RelationalExecutionHistoryStore.cs` — Dapper/ADO relational store; not needed
- `ExecutionHistoryStoreServiceCollectionExtensions` (if present) — not needed

The EF Core history store provider is NOT vendored. The `Themia.Modules.Scheduling` module
provides the EF-backed store that implements `IExecutionHistoryStore`.

## Namespace re-mapping

| Upstream namespace | Themia namespace |
|---|---|
| `Quartz.Plugins.RecentHistory` | `Themia.Quartz` |
| `Quartz.Plugins.RecentHistory.Impl` | `Themia.Quartz` |

The dashboard namespace (Task 3) will follow: `SilkierQuartz` → `Themia.Quartz.Dashboard`.

## Deviations from upstream

1. **Stable context key**: `SchedulerContextExtensions` uses the constant string
   `"Themia.Quartz.IExecutionHistoryStore"` instead of `typeof(IExecutionHistoryStore).FullName`.
   This means re-namespacing cannot silently shift the key and break existing schedulers.

2. **AuthenticateController dropped** (Task 3): The SilkierQuartz dashboard
   `AuthenticateController` will not be vendored. Authentication is handled via an
   `Authorize` delegate registered on `ThemiaQuartzOptions`, keeping Themia auth-agnostic.

3. **Nullable-clean**: All vendored files have nullable reference types enabled and are
   warning-free under `TreatWarningsAsErrors=true`.
