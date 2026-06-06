# Themia Scheduling Implementation Plan — `Themia.Quartz` + `Themia.Modules.Scheduling`

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the Themia scheduling capability in two layers — a framework-neutral `Themia.Quartz` (vendored SilkierQuartz dashboard + Quartz wiring + in-memory execution-history store, host-supplied auth) and a net10 `Themia.Modules.Scheduling` module (EF-backed global/admin history store + module lifecycle).

**Architecture:** Vendor `maikebing/SilkierQuartz` source into `src/neutral/Themia.Quartz` (net8.0;net10.0), re-namespaced `SilkierQuartz` → `Themia.Quartz.Dashboard` and `Quartz.Plugins.RecentHistory` → `Themia.Quartz`. Drop the cookie/`AuthenticateController` auth and replace it with a single host-supplied `Func<HttpContext, Task<bool>> Authorize` delegate (default-deny). Public API is `AddThemiaQuartz(...)` + `MapThemiaQuartz()`. The module (`src/modules/Themia.Modules.Scheduling`, net10.0) provides `EfExecutionHistoryStore : IExecutionHistoryStore` (global, no `TenantId`) over `Themia.Framework.Data.EFCore` and a `SchedulingModule : ThemiaModuleBase` that wires Quartz + `AddThemiaQuartz` + runs the EF migration.

**Tech stack:** .NET 8 + .NET 10; Quartz 3.18.0; Handlebars.Net 2.1.6; JsonSubTypes 2.0.1; Newtonsoft.Json 13.0.4 (vendored internal detail — see policy note); EF Core 10; xUnit + `WebApplicationFactory` + Testcontainers.

**Upstream pin:** clone `maikebing/SilkierQuartz`. Prefer the `v10.0.0` tag; if absent, pin commit `4b974e080d369c588194e84642a9be875175f3fd` (master, validated for this plan). Record the exact SHA in `THIRD-PARTY-NOTICES/`.

**Cross-cutting conventions (every task):** nullable enabled; `TreatWarningsAsErrors`; `GenerateDocumentationFile`; central package management (`Directory.Packages.props`); `PublicAPI.Shipped/Unshipped.txt` on the neutral core. **`System.Text.Json` is the Themia default — but vendored SilkierQuartz uses Newtonsoft.Json + Handlebars.Net internally; per the design spec (MINOR 6) this is an ACCEPTED trade-off: keep Newtonsoft as an internal implementation detail, NEVER expose it on `Themia.Quartz`'s public API. Do NOT migrate the vendored dashboard off Newtonsoft.** `ILogger<T>` only in authored code (no `Console.*`). Conventional commits, no co-author trailers.

**Build/test:**
```bash
dotnet build Themia.sln -c Release                                   # net8.0 + net10.0
dotnet test Themia.sln -c Release --filter "Category!=Integration"   # unit + WebApplicationFactory smoke
dotnet test Themia.sln -c Release --filter Category=Integration      # Testcontainers (Docker)
```

---

## File Structure

**`Themia.Quartz` (neutral core, `src/neutral/Themia.Quartz/`)**
- `Themia.Quartz.csproj` — net8.0;net10.0, FrameworkReference `Microsoft.AspNetCore.App`, the 4 package refs, embedded-resource globs.
- `History/` — vendored RecentHistory plugin re-namespaced to `Themia.Quartz`: `IExecutionHistoryStore.cs` (+`ExecutionHistoryEntry`,`JobStats`), `ExecutionHistoryPlugin.cs`, `InProcExecutionHistoryStore.cs`, `SchedulerContextExtensions.cs`.
- `Dashboard/` — vendored `src/SilkierQuartz/` re-namespaced `Themia.Quartz.Dashboard`: `Controllers/` (minus `AuthenticateController`), `Models/`, `Helpers/`, `HostedService/`, `TypeHandlers/`, `Views/` (minus `Authenticate/Login.hbs`), `Content/`.
- `ThemiaQuartzOptions.cs` — replaces `SilkierQuartzOptions` + `SilkierQuartzAuthenticationOptions`; adds `Authorize` delegate.
- `ServiceCollectionExtensions.cs` — `AddThemiaQuartz(...)`.
- `ApplicationBuilderExtensions.cs` — `MapThemiaQuartz()` (+ legacy `UseThemiaQuartz` internals).
- `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`.
- `THIRD-PARTY-NOTICES/SilkierQuartz-LICENSE.txt` (+ a `VENDORING.md` recording the SHA + changes).

**`Themia.Quartz.Tests` (`tests/Themia.Quartz.Tests/`)** — net8.0;net10.0: `InProcExecutionHistoryStoreTests.cs`, `ThemiaQuartzOptionsTests.cs`, `DashboardSmokeTests.cs` (WebApplicationFactory).

**`Themia.Modules.Scheduling` (`src/modules/Themia.Modules.Scheduling/`, net10.0)**
- `Themia.Modules.Scheduling.csproj` — deps `Themia.Quartz`, `Themia.Framework.Core`, `Themia.Framework.Data.EFCore`.
- `ExecutionHistoryRecord.cs` — EF entity (global; no `TenantId`).
- `SchedulingDbContext.cs` — `ThemiaDbContext`-derived (history table only) + FluentMigrator/EF migration.
- `EfExecutionHistoryStore.cs` — `IExecutionHistoryStore` over EF.
- `SchedulingModule.cs` — `ThemiaModuleBase`.
- `PublicAPI.*`.

**`Themia.Modules.Scheduling.IntegrationTests` (`tests/`)** — module lifecycle + `EfExecutionHistoryStore` round-trip (Testcontainers).

---

## Task 1: Scaffold `Themia.Quartz` project + dependency pins

**Files:**
- Create: `src/neutral/Themia.Quartz/Themia.Quartz.csproj`
- Create: `src/neutral/Themia.Quartz/PublicAPI.Shipped.txt` (empty), `PublicAPI.Unshipped.txt` (empty)
- Modify: `Directory.Packages.props` (add version pins), `Themia.sln`

- [ ] **Step 1.1: Pin packages.** In `Directory.Packages.props` add (inside the existing `<ItemGroup>` of `<PackageVersion>`s, alphabetical):
```xml
<PackageVersion Include="Quartz" Version="3.18.0" />
<PackageVersion Include="Handlebars.Net" Version="2.1.6" />
<PackageVersion Include="JsonSubTypes" Version="2.0.1" />
<PackageVersion Include="Newtonsoft.Json" Version="13.0.4" />
```
(If any already exists, leave it.) `Microsoft.Extensions.FileProviders.Embedded`/`.Physical` are provided by the `Microsoft.AspNetCore.App` FrameworkReference — do NOT add them as packages.

- [ ] **Step 1.2: Create the csproj** at `src/neutral/Themia.Quartz/Themia.Quartz.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <!-- Vendored SilkierQuartz ships XML-doc-light source; suppress CS1591 ONLY for the vendored
         Dashboard/History trees is not possible per-folder, so document authored public members and
         keep CS1591 on. Vendored public types that lack docs are made `internal` where possible
         (Task 3); any that must stay public get a one-line doc during re-namespacing. -->
    <RootNamespace>Themia.Quartz</RootNamespace>
    <GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Quartz" />
    <PackageReference Include="Handlebars.Net" />
    <PackageReference Include="JsonSubTypes" />
    <PackageReference Include="Newtonsoft.Json" />
  </ItemGroup>

  <!-- Embedded resources (vendored in Task 3). Names auto-derive from RootNamespace + path:
       e.g. Dashboard/Views/Scheduler/Index.hbs → Themia.Quartz.Dashboard.Views.Scheduler.Index.hbs -->
  <ItemGroup>
    <EmbeddedResource Include="Dashboard\Content\**" />
    <EmbeddedResource Include="Dashboard\Views\**" />
    <EmbeddedResource Include="Dashboard\TypeHandlers\*.hbs" />
    <EmbeddedResource Include="Dashboard\TypeHandlers\*.js" />
  </ItemGroup>
</Project>
```
> Note: `Directory.Build.props` already sets `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors`, `GenerateDocumentationFile`. The vendored code may not be null-clean or warning-clean. **If vendored warnings block the build**, add `<PropertyGroup><Nullable>annotations</Nullable></PropertyGroup>` scoped via a `Directory.Build.props` override is NOT allowed (it's solution-wide). Instead, set `<NoWarn>` for the specific vendored-only warning IDs (e.g. `CS1591;CS8618;CS8625;...`) on THIS csproj only, and add a comment that these are concessions to vendored third-party source. Authored files (Task 4/5) must still be warning-clean — keep `<NoWarn>` minimal and document each ID.

- [ ] **Step 1.3:** `dotnet sln Themia.sln add src/neutral/Themia.Quartz/Themia.Quartz.csproj`. Create empty `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` (copy the header format from `src/neutral/Themia.Exceptional/PublicAPI.Shipped.txt` — usually just `#nullable enable` or empty).

- [ ] **Step 1.4: Verify empty build.** `dotnet build src/neutral/Themia.Quartz -c Release` → succeeds (empty project, both TFMs restore Quartz/Handlebars).

- [ ] **Step 1.5: Commit.**
```bash
git add Directory.Packages.props src/neutral/Themia.Quartz Themia.sln
git commit -m "feat(quartz): scaffold Themia.Quartz neutral core project + pin Quartz/Handlebars deps"
```

---

## Task 2: Vendor the RecentHistory plugin (history store contract + in-memory store)

This is the seam the dashboard reads from. Vendor it FIRST so the dashboard (Task 3) compiles against the renamed contract.

**Files:**
- Create: `src/neutral/Themia.Quartz/History/IExecutionHistoryStore.cs`, `ExecutionHistoryPlugin.cs`, `InProcExecutionHistoryStore.cs`, `SchedulerContextExtensions.cs`
- Create: `src/neutral/Themia.Quartz/THIRD-PARTY-NOTICES/SilkierQuartz-LICENSE.txt`, `VENDORING.md`
- Test: `tests/Themia.Quartz.Tests/InProcExecutionHistoryStoreTests.cs`

- [ ] **Step 2.1: Clone upstream** to a temp dir (record the SHA):
```bash
git clone https://github.com/maikebing/SilkierQuartz.git /tmp/silkierquartz
git -C /tmp/silkierquartz checkout v10.0.0 2>/dev/null || echo "v10.0.0 tag absent — staying on master"
git -C /tmp/silkierquartz rev-parse HEAD   # record this SHA in VENDORING.md
```

- [ ] **Step 2.2: Copy the licence + record provenance.** Copy `/tmp/silkierquartz/LICENSE` → `src/neutral/Themia.Quartz/THIRD-PARTY-NOTICES/SilkierQuartz-LICENSE.txt`. Create `VENDORING.md` stating: source repo, the recorded SHA, MIT licence, the re-namespacing applied (`SilkierQuartz`→`Themia.Quartz.Dashboard`, `Quartz.Plugins.RecentHistory`→`Themia.Quartz`), and the deviations (AuthenticateController dropped; auth replaced by `Authorize` delegate).

- [ ] **Step 2.3: Write the failing in-memory store test** at `tests/Themia.Quartz.Tests/InProcExecutionHistoryStoreTests.cs` (this also forces the contract shape):
```csharp
using Themia.Quartz;
using Xunit;

namespace Themia.Quartz.Tests;

public class InProcExecutionHistoryStoreTests
{
    private static ExecutionHistoryEntry Entry(string fireId, string job, string trigger, DateTimeOffset fired) => new()
    {
        FireInstanceId = fireId, SchedulerName = "S", SchedulerInstanceId = "I",
        Job = job, Trigger = trigger, ActualFireTimeUtc = fired,
    };

    [Fact]
    public async Task Save_Then_Get_RoundTrips()
    {
        var store = new InProcExecutionHistoryStore { SchedulerName = "S" };
        var e = Entry("f1", "g.j", "g.t", DateTimeOffset.UtcNow);
        await store.Save(e);
        Assert.Equal("g.j", (await store.Get("f1"))!.Job);
    }

    [Fact]
    public async Task FilterLast_ReturnsMostRecentForScheduler()
    {
        var store = new InProcExecutionHistoryStore { SchedulerName = "S" };
        await store.Save(Entry("f1", "g.j", "g.t", DateTimeOffset.UtcNow.AddMinutes(-2)));
        await store.Save(Entry("f2", "g.j", "g.t", DateTimeOffset.UtcNow));
        var last = (await store.FilterLast(1)).ToList();
        Assert.Single(last);
        Assert.Equal("f2", last[0].FireInstanceId);
    }

    [Fact]
    public async Task IncrementCounters_Tracked()
    {
        var store = new InProcExecutionHistoryStore { SchedulerName = "S" };
        await store.IncrementTotalJobsExecuted();
        await store.IncrementTotalJobsExecuted();
        await store.IncrementTotalJobsFailed();
        Assert.Equal(2, await store.GetTotalJobsExecuted());
        Assert.Equal(1, await store.GetTotalJobsFailed());
    }
}
```
(Create the test project `tests/Themia.Quartz.Tests/Themia.Quartz.Tests.csproj` mirroring `tests/Themia.Exceptional.Tests/*.csproj` — `net8.0;net10.0`, xUnit, `<ProjectReference>` to `Themia.Quartz`; add to sln.) Run: `dotnet test tests/Themia.Quartz.Tests -c Release` → FAILS to compile (types missing).

- [ ] **Step 2.4: Vendor the plugin source.** Copy these from `/tmp/silkierquartz/src/Quartz.Plugins.RecentHistory/` into `src/neutral/Themia.Quartz/History/`, then re-namespace `Quartz.Plugins.RecentHistory` → `Themia.Quartz` and `Quartz.Plugins.RecentHistory.Impl` → `Themia.Quartz`:
  - `IExecutionHistoryStore.cs` (carries `IExecutionHistoryStore` + `ExecutionHistoryEntry` + `JobStats`) — the contract is (verbatim shape, namespace changed to `Themia.Quartz`):
```csharp
namespace Themia.Quartz;

public interface IExecutionHistoryStore
{
    string SchedulerName { get; set; }
    Task<ExecutionHistoryEntry?> Get(string fireInstanceId);
    Task Save(ExecutionHistoryEntry entry);
    Task Purge();
    Task<IEnumerable<ExecutionHistoryEntry>> FilterLastOfEveryJob(int limitPerJob);
    Task<IEnumerable<ExecutionHistoryEntry>> FilterLastOfEveryTrigger(int limitPerTrigger);
    Task<IEnumerable<ExecutionHistoryEntry>> FilterLast(int limit);
    Task<int> GetTotalJobsExecuted();
    Task<int> GetTotalJobsFailed();
    Task IncrementTotalJobsExecuted();
    Task IncrementTotalJobsFailed();
}
```
  Plus `ExecutionHistoryEntry` (properties: `FireInstanceId`, `SchedulerInstanceId`, `SchedulerName`, `Job`, `Trigger`, `ScheduledFireTimeUtc` (`DateTimeOffset?`), `ActualFireTimeUtc` (`DateTimeOffset`), `Recovering` (`bool`), `Vetoed` (`bool`), `FinishedTimeUtc` (`DateTimeOffset?`), `ExceptionMessage` (`string?`)) and `JobStats` (`TotalJobsExecuted`, `TotalJobsFailed`) — copy verbatim, namespace `Themia.Quartz`, make reference-type string members nullable to satisfy `Nullable`.
  - `Impl/InProcExecutionHistoryStore.cs` → `InProcExecutionHistoryStore.cs` (namespace `Themia.Quartz`) — copy verbatim (the `Dictionary`-backed purge-retains-last-10-per-trigger impl).
  - `ExecutionHistoryPlugin.cs` (the `ISchedulerPlugin, IJobListener` that writes entries on job execution) — copy, namespace `Themia.Quartz`.
  - `Extensions.cs` → `SchedulerContextExtensions.cs` — `SetExecutionHistoryStore`/`GetExecutionHistoryStore`. **Keep the context key STABLE and explicit** to avoid the rename changing it: use a `const string` rather than `typeof(IExecutionHistoryStore).FullName`:
```csharp
namespace Themia.Quartz;

public static class SchedulerContextExtensions
{
    // Stable key — decoupled from the type's namespace so re-namespacing can't shift it.
    private const string Key = "Themia.Quartz.IExecutionHistoryStore";

    public static void SetExecutionHistoryStore(this Quartz.SchedulerContext context, IExecutionHistoryStore store)
        => context[Key] = store;

    public static IExecutionHistoryStore? GetExecutionHistoryStore(this Quartz.SchedulerContext context)
        => context.TryGetValue(Key, out var v) ? v as IExecutionHistoryStore : null;
}
```
  Do NOT vendor the 4 EF Core provider stores or the Dapper `ExecutionHistoryStoreOptions` — the module (Task 8) provides the EF store.

- [ ] **Step 2.5: Run the store tests.** `dotnet test tests/Themia.Quartz.Tests -c Release --filter "FullyQualifiedName~InProcExecutionHistoryStore"` → 3 pass on both TFMs.

- [ ] **Step 2.6: Commit.**
```bash
git add src/neutral/Themia.Quartz/History src/neutral/Themia.Quartz/THIRD-PARTY-NOTICES src/neutral/Themia.Quartz/VENDORING.md tests/Themia.Quartz.Tests Themia.sln
git commit -m "feat(quartz): vendor RecentHistory store contract + in-memory store (re-namespaced Themia.Quartz)"
```

---

## Task 3: Vendor the dashboard source (re-namespaced, AuthenticateController dropped)

Bulk-mechanical. The goal of THIS task is a COMPILING dashboard tree wired to the Task-2 store contract, with auth still in its original shape (the `Authorize`-delegate transform is Task 4 — keep them separate so a compile failure is attributable).

**Files:** Create `src/neutral/Themia.Quartz/Dashboard/**` (Controllers, Models, Helpers, HostedService, TypeHandlers, Views, Content).

- [ ] **Step 3.1: Copy the dashboard tree.**
```bash
mkdir -p src/neutral/Themia.Quartz/Dashboard
cp -R /tmp/silkierquartz/src/SilkierQuartz/Controllers src/neutral/Themia.Quartz/Dashboard/
cp -R /tmp/silkierquartz/src/SilkierQuartz/Models src/neutral/Themia.Quartz/Dashboard/
cp -R /tmp/silkierquartz/src/SilkierQuartz/Helpers src/neutral/Themia.Quartz/Dashboard/
cp -R /tmp/silkierquartz/src/SilkierQuartz/HostedService src/neutral/Themia.Quartz/Dashboard/
cp -R /tmp/silkierquartz/src/SilkierQuartz/TypeHandlers src/neutral/Themia.Quartz/Dashboard/
cp -R /tmp/silkierquartz/src/SilkierQuartz/Views src/neutral/Themia.Quartz/Dashboard/
cp -R /tmp/silkierquartz/src/SilkierQuartz/Content src/neutral/Themia.Quartz/Dashboard/
# Authorization helpers (requirement/handler) — copy too; simplified in Task 4:
cp -R /tmp/silkierquartz/src/SilkierQuartz/Authorization src/neutral/Themia.Quartz/Dashboard/ 2>/dev/null || true
# Top-level dashboard .cs (Services.cs, ViewFileSystemFactory.cs, etc.) — copy the loose files:
cp /tmp/silkierquartz/src/SilkierQuartz/*.cs src/neutral/Themia.Quartz/Dashboard/ 2>/dev/null || true
```

- [ ] **Step 3.2: DROP auth-by-controller artifacts.**
```bash
rm -f src/neutral/Themia.Quartz/Dashboard/Controllers/AuthenticateController.cs
rm -rf src/neutral/Themia.Quartz/Dashboard/Views/Authenticate
```
Also delete the upstream `ServiceCollectionExtensions.cs` / `ApplicationBuilderExtensions.cs` if they were copied by the `*.cs` glob (we author replacements in Task 5): `rm -f src/neutral/Themia.Quartz/Dashboard/ServiceCollectionExtensions.cs src/neutral/Themia.Quartz/Dashboard/ApplicationBuilderExtensions.cs` and the upstream `SilkierQuartzOptions.cs`/`SilkierQuartzAuthenticationOptions.cs` (replaced in Task 4): `rm -f src/neutral/Themia.Quartz/Dashboard/SilkierQuartzOptions.cs src/neutral/Themia.Quartz/Dashboard/SilkierQuartzAuthenticationOptions.cs`. (If the `Configuration/` subdir holds these, target there instead — verify with `grep -rl "class SilkierQuartzOptions"`.)

- [ ] **Step 3.3: Re-namespace `.cs` files.** Replace the namespace root across the copied dashboard `.cs` files: `SilkierQuartz` → `Themia.Quartz.Dashboard`, and references to the history plugin `Quartz.Plugins.RecentHistory` → `Themia.Quartz`. Use a scoped replace (review the diff after):
```bash
cd src/neutral/Themia.Quartz/Dashboard
# namespace + using statements for the dashboard root:
grep -rl --include='*.cs' 'SilkierQuartz' . | xargs sed -i '' -E 's/\bSilkierQuartz\b/Themia.Quartz.Dashboard/g'
# history plugin references:
grep -rl --include='*.cs' 'Quartz\.Plugins\.RecentHistory' . | xargs sed -i '' -E 's/Quartz\.Plugins\.RecentHistory/Themia.Quartz/g'
cd -
```
> CAUTION: the `\bSilkierQuartz\b` replace will also rename type names like `SilkierQuartzOptions` → `Themia.Quartz.DashboardOptions` inside references. That's acceptable transitionally because Task 4 replaces the options type entirely; but to avoid churn, FIRST delete the options/auth/extension files (Step 3.2) so only their *references* remain, then after Task 4 those references point at `ThemiaQuartzOptions`. If the sed over-reaches on type identifiers, prefer an IDE "rename namespace" or a more targeted `namespace SilkierQuartz` → `namespace Themia.Quartz.Dashboard` + `using SilkierQuartz` → `using Themia.Quartz.Dashboard` pair of replaces. Review `git diff` before building.

- [ ] **Step 3.4: Fix the 2 hard-coded embedded-resource prefix strings.** Grep and update:
```bash
grep -rn '"SilkierQuartz.Views"\|"SilkierQuartz.Content"' src/neutral/Themia.Quartz/Dashboard
```
Change `"SilkierQuartz.Views"` → `"Themia.Quartz.Dashboard.Views"` and `"SilkierQuartz.Content"` → `"Themia.Quartz.Dashboard.Content"` (in `ViewFileSystemFactory.cs` and the file-server helper, respectively). These must match the auto-derived resource names from the csproj globs (RootNamespace `Themia.Quartz` + folder `Dashboard/Content/...` → `Themia.Quartz.Dashboard.Content....`).

- [ ] **Step 3.5: Quarantine the dropped-auth references temporarily.** The controllers reference `[Authorize(Policy = SilkierQuartzAuthenticationOptions.AuthorizationPolicyName)]` etc. Since Task 4 reworks auth, for THIS task make the tree compile by leaving the vendored `Authorization/` requirement+handler in place and keeping a minimal `ThemiaQuartzOptions` shim is premature — instead, **defer the build-green checkpoint to Task 4**. At the end of Task 3, run `dotnet build` and EXPECT errors only about the removed options/auth types; capture the error list. (If the controllers don't reference the deleted options type directly — they read `Services` from `HttpContext.Items` — the tree may compile already; in that case proceed to a clean build here.)

- [ ] **Step 3.6: Commit the vendored tree** (even if not yet building — it's an intermediate, and Task 4 completes it; note the state in the message):
```bash
git add src/neutral/Themia.Quartz/Dashboard
git commit -m "feat(quartz): vendor SilkierQuartz dashboard source (re-namespaced Themia.Quartz.Dashboard; AuthenticateController dropped) [wip: auth seam in next task]"
```
> If the spec's "frequent commits, each green" discipline is preferred over a WIP commit, MERGE Task 3 and Task 4 into one task and commit once green. The implementer may choose; note which was done.

---

## Task 4: `ThemiaQuartzOptions` + host-supplied `Authorize` seam (default-deny)

Replace `SilkierQuartzOptions` + `SilkierQuartzAuthenticationOptions` + the cookie/policy auth with a single neutral options type carrying a `Func<HttpContext, Task<bool>> Authorize` delegate.

**Files:**
- Create: `src/neutral/Themia.Quartz/ThemiaQuartzOptions.cs`
- Modify: vendored `Authorization/` requirement+handler (simplify), the `Services` factory, controllers' `[Authorize]` usage, `HandlebarsHelpers` logout-menu check.
- Test: `tests/Themia.Quartz.Tests/ThemiaQuartzOptionsTests.cs`

- [ ] **Step 4.1: Author `ThemiaQuartzOptions`** (carries the dashboard rendering options that controllers/views need + the auth delegate). Port the non-auth properties from upstream `SilkierQuartzOptions` verbatim (Logo, CustomStyleSheet, CustomFavicon, ProductName, VirtualPathRoot, BasePath, Scheduler, StandardTypes/DefaultSelectedType, DefaultDateFormat/DefaultTimeFormat/UseLocalTime via DateTimeSettings, CronExpressionOptions, EnableEdit), and ADD:
```csharp
using Microsoft.AspNetCore.Http;
namespace Themia.Quartz;

public sealed class ThemiaQuartzOptions
{
    // ... vendored non-auth properties (Logo, VirtualPathRoot="/jobs", ProductName, Scheduler,
    //     StandardTypes, Default*Format, UseLocalTime, CronExpressionOptions {DayOfWeekStartIndexZero=false},
    //     EnableEdit, etc.) ported from SilkierQuartzOptions ...

    /// <summary>
    /// Host-supplied authorization gate for every dashboard request. Returns <see langword="true"/> to allow.
    /// When <see langword="null"/> the dashboard is DENY-ALL (403) — the host MUST supply this to grant access.
    /// </summary>
    public Func<HttpContext, Task<bool>>? Authorize { get; set; }
}
```
Keep the upstream `StandardTypes` constructor population verbatim (the TypeHandler list order matters). Document all public members (CS1591).

- [ ] **Step 4.2: Simplify the authorization handler** to consult `Authorize` (default-deny). Replace the vendored `SilkierQuartzDefaultAuthorizationHandler`/requirement with a single middleware-or-handler that, per request, calls `options.Authorize?.Invoke(httpContext) ?? Task.FromResult(false)` and short-circuits with 403 when false. Simplest: an authorization middleware registered in `MapThemiaQuartz` (Task 5) rather than the MVC policy. Remove `[Authorize(Policy=...)]` attributes from the vendored controllers (the middleware now gates the whole `VirtualPathRoot`). Update `HandlebarsHelpers` to hide the (now-removed) logout menu item — drop the logout nav entry entirely.

- [ ] **Step 4.3: Write the options/auth test** `tests/Themia.Quartz.Tests/ThemiaQuartzOptionsTests.cs`:
```csharp
using Themia.Quartz;
using Xunit;

namespace Themia.Quartz.Tests;

public class ThemiaQuartzOptionsTests
{
    [Fact]
    public void Authorize_DefaultsToNull_MeaningDenyAll()
    {
        var o = new ThemiaQuartzOptions();
        Assert.Null(o.Authorize);  // null = deny; MapThemiaQuartz enforces 403 (covered in DashboardSmokeTests)
    }

    [Fact]
    public void VirtualPathRoot_DefaultsToJobs()
    {
        Assert.Equal("/jobs", new ThemiaQuartzOptions().VirtualPathRoot);
    }

    [Fact]
    public void CronExpressionOptions_DayOfWeekStartIndexZero_IsFalse()
    {
        Assert.False(new ThemiaQuartzOptions().CronExpressionOptions.DayOfWeekStartIndexZero);
    }
}
```

- [ ] **Step 4.4: Build green.** `dotnet build src/neutral/Themia.Quartz -c Release` → 0 errors both TFMs (resolve any remaining references to deleted upstream types). Run `dotnet test tests/Themia.Quartz.Tests -c Release --filter "FullyQualifiedName~ThemiaQuartzOptions"` → 3 pass.

- [ ] **Step 4.5: Commit.**
```bash
git add src/neutral/Themia.Quartz tests/Themia.Quartz.Tests
git commit -m "feat(quartz): ThemiaQuartzOptions + host-supplied Authorize delegate (default-deny); drop cookie auth"
```

---

## Task 5: `AddThemiaQuartz()` / `MapThemiaQuartz()` public API + store bridge

**Files:**
- Create: `src/neutral/Themia.Quartz/ServiceCollectionExtensions.cs`, `ApplicationBuilderExtensions.cs`
- Modify: `PublicAPI.Unshipped.txt`

- [ ] **Step 5.1: Author `AddThemiaQuartz`** (namespace `Microsoft.Extensions.DependencyInjection`, mirroring the Themia convention). Port the non-auth registration from upstream `AddSilkierQuartz` (register `ThemiaQuartzOptions` singleton, the Quartz hosted service / controllers / view engine, job auto-discovery if retained), but DROP the cookie `AddAuthentication().AddCookie(...)` and the MVC policy. Register the controllers as application parts from the `Themia.Quartz` assembly:
```csharp
namespace Microsoft.Extensions.DependencyInjection;

public static class ThemiaQuartzServiceCollectionExtensions
{
    /// <summary>Registers the Themia Quartz dashboard services. The host owns the <see cref="Quartz.IScheduler"/>.</summary>
    public static IServiceCollection AddThemiaQuartz(this IServiceCollection services, Action<Themia.Quartz.ThemiaQuartzOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new Themia.Quartz.ThemiaQuartzOptions();
        configure(options);
        services.AddSingleton(options);
        // MVC + Newtonsoft (vendored view models require it), controllers from this assembly,
        // the embedded Handlebars view engine — port from upstream AddSilkierQuartz minus auth.
        services.AddControllersWithViews().AddNewtonsoftJson()
            .AddApplicationPart(typeof(Themia.Quartz.ThemiaQuartzOptions).Assembly);
        return services;
    }
}
```

- [ ] **Step 5.2: Author `MapThemiaQuartz`** (namespace `Microsoft.AspNetCore.Builder`) — port `UseSilkierQuartz` minus auth, plus the `Authorize`-delegate gate middleware and the embedded static-content file server. Bridge the DI `IExecutionHistoryStore` (if registered) into the scheduler context:
```csharp
namespace Microsoft.AspNetCore.Builder;

public static class ThemiaQuartzApplicationBuilderExtensions
{
    public static IEndpointRouteBuilder MapThemiaQuartz(this IEndpointRouteBuilder endpoints)
    {
        var options = endpoints.ServiceProvider.GetRequiredService<Themia.Quartz.ThemiaQuartzOptions>();
        // 1. resolve scheduler (options.Scheduler or DI IScheduler);
        // 2. bridge store: if DI has IExecutionHistoryStore, scheduler.Context.SetExecutionHistoryStore(store);
        // 3. deny-all gate over VirtualPathRoot: a terminal middleware that calls options.Authorize;
        // 4. UseFileServer for embedded Dashboard/Content at {VirtualPathRoot}/Content;
        // 5. MapControllerRoute("ThemiaQuartz", "{VirtualPathRoot}/{controller=Scheduler}/{action=Index}").
        // Port the bodies from upstream UseSilkierQuartz, dropping the Authenticate route.
        return endpoints;
    }
}
```
> The store bridge belongs wherever the scheduler is available at startup; if `MapThemiaQuartz` runs before the scheduler starts, do the `SetExecutionHistoryStore` in the Quartz hosted-service startup instead (port upstream `QuartzHostedService.StartAsync`'s `GetService<IExecutionHistoryStore>()` → `Context.SetExecutionHistoryStore`). Keep that bridge.

- [ ] **Step 5.3: PublicAPI.** Clean build → add every reported RS0016 public member (`AddThemiaQuartz`, `MapThemiaQuartz`, `ThemiaQuartzOptions` + its public members, `IExecutionHistoryStore` + members, `ExecutionHistoryEntry`, `JobStats`, `InProcExecutionHistoryStore`, `SchedulerContextExtensions`, `ExecutionHistoryPlugin`) to `PublicAPI.Unshipped.txt`.

- [ ] **Step 5.4: Build green** both TFMs, 0 warnings. Commit:
```bash
git add src/neutral/Themia.Quartz
git commit -m "feat(quartz): AddThemiaQuartz/MapThemiaQuartz public API + execution-history store bridge"
```

---

## Task 6: Dashboard smoke tests (`WebApplicationFactory`, net8 + net10)

**Files:** Create `tests/Themia.Quartz.Tests/DashboardSmokeTests.cs` + a minimal test host (`WebApplicationFactory<TEntryPoint>` over a tiny inline app, or a test `Program`).

- [ ] **Step 6.1: Build a minimal test app** that calls `AddThemiaQuartz` (with an in-memory scheduler + `InProcExecutionHistoryStore`) and `MapThemiaQuartz`, parameterized so `Authorize` can be set per test. Use `WebApplicationFactory` with a custom `Program`/`IStartup` (mirror `tests/Themia.Framework.AspNetCore.Tests` host setup).

- [ ] **Step 6.2: Write smoke tests:**
```csharp
[Fact] [Trait("Category","Integration")]  // WebApplicationFactory boots ASP.NET; keep out of pure-unit lane if needed
public async Task Unauthorized_WhenAuthorizeReturnsFalse_Returns403() { /* Authorize = _ => Task.FromResult(false); GET /jobs → 403 */ }

[Fact]
public async Task SchedulerIndex_WhenAuthorized_Returns200_AndHtml() { /* Authorize = _ => Task.FromResult(true); GET /jobs → 200, body contains the dashboard shell */ }

[Fact]
public async Task EmbeddedContent_IsServed() { /* GET /jobs/Content/Scripts/<known-asset> → 200, non-empty */ }
```
Pick a real asset name from `Dashboard/Content/Scripts/` for the third test (verify the exact filename in the vendored tree). These prove the embedded-resource re-namespacing (the spec's open item #1) actually works end-to-end. **If embedded content 404s**, the resource-prefix strings (Step 3.4) or the csproj globs (Step 1.2) are wrong — fix there; this test is the validation gate for the whole vendoring approach.

- [ ] **Step 6.3:** `dotnet test tests/Themia.Quartz.Tests -c Release` → all pass on net8.0 AND net10.0. Commit:
```bash
git add tests/Themia.Quartz.Tests
git commit -m "test(quartz): WebApplicationFactory dashboard smoke (routes, deny-all 403, embedded content) on net8+net10"
```

---

## Task 7: Scaffold `Themia.Modules.Scheduling`

**Files:** Create `src/modules/Themia.Modules.Scheduling/Themia.Modules.Scheduling.csproj` + `PublicAPI.*`; modify `Themia.sln`.

- [ ] **Step 7.1: Confirm the module base exists.** `grep -rl "class ThemiaModuleBase\|interface IThemiaModule" src/framework` — it should be in `Themia.Framework.Core`. If absent, STOP and report NEEDS_CONTEXT (the module contract is a prerequisite). Read its shape (`ConfigureServices`, `InitializeAsync`) to match Task 9.

- [ ] **Step 7.2: Create the csproj** (`net10.0`):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\neutral\Themia.Quartz\Themia.Quartz.csproj" />
    <ProjectReference Include="..\..\framework\Themia.Framework.Core\Themia.Framework.Core.csproj" />
    <ProjectReference Include="..\..\framework\Themia.Framework.Data.EFCore\Themia.Framework.Data.EFCore.csproj" />
  </ItemGroup>
</Project>
```
Add to sln; create `PublicAPI.Shipped.txt`/`Unshipped.txt`. `dotnet build src/modules/Themia.Modules.Scheduling -c Release` → succeeds (empty).

- [ ] **Step 7.3: Commit.** `git commit -m "feat(scheduling): scaffold Themia.Modules.Scheduling module project"`

---

## Task 8: `EfExecutionHistoryStore` (global/admin EF store) + entity + migration

**Files:** Create `ExecutionHistoryRecord.cs`, `SchedulingDbContext.cs`, a migration, `EfExecutionHistoryStore.cs`. Test: integration round-trip.

- [ ] **Step 8.1: Entity** `ExecutionHistoryRecord` — mirrors `ExecutionHistoryEntry`, **no `TenantId`** (execution history is global/admin per spec MAJOR 2). `FireInstanceId` is the PK (string). Map the `DateTimeOffset?`/`DateTimeOffset` fields; index `(SchedulerName, Trigger, ActualFireTimeUtc)` for the `FilterLastOfEveryTrigger`/`FilterLast` queries.

- [ ] **Step 8.2: `SchedulingDbContext : ThemiaDbContext`** — single `DbSet<ExecutionHistoryRecord>`. Because the entity has no `TenantId` and isn't `ITenantEntity`, the base tenant filter does not apply (verify against `ThemiaDbContext.ApplyTenantQueryFilters`, which only filters `ITenantEntity`). Configure the entity (PK, indexes, column types) in `OnModelCreating` (call `base` first).

- [ ] **Step 8.3: Migration** — provide the `Exceptions`-style schema creation. Reuse the project's migration mechanism (FluentMigrator if `Themia.Exceptional` set the precedent, else EF migrations). Match whatever `Themia.Framework.Data.EFCore`/module convention exists — `grep` for how other modules/the EFCore layer run migrations and follow it. Forward-only.

- [ ] **Step 8.4: `EfExecutionHistoryStore : IExecutionHistoryStore`** — implement all 12 members over `SchedulingDbContext` (or a pooled factory). `FilterLastOfEveryTrigger(n)`/`FilterLastOfEveryJob(n)` group by Trigger/Job and take top-n by `ActualFireTimeUtc desc` per group (use `AsNoTracking`; project to `ExecutionHistoryEntry`). `Save` = upsert by `FireInstanceId`. `Purge` may be a no-op or a retention delete (document choice). The job-executed/failed counters: persist as a tiny stats row or compute via `COUNT` — pick one and document. Use `ILogger<EfExecutionHistoryStore>`.

- [ ] **Step 8.5: Integration test** (`tests/Themia.Modules.Scheduling.IntegrationTests`, Testcontainers Postgres — mirror `Themia.Exceptional.PostgreSql.IntegrationTests` container setup): save entries, assert `Get`, `FilterLast`, `FilterLastOfEveryTrigger`, and the counters round-trip. `[Trait("Category","Integration")]`.

- [ ] **Step 8.6:** Build + test (Docker). Commit:
```bash
git commit -m "feat(scheduling): EF-backed global EfExecutionHistoryStore + entity + migration"
```

---

## Task 9: `SchedulingModule` (lifecycle: wire Quartz + AddThemiaQuartz + migrate)

**Files:** Create `SchedulingModule.cs`. Test: module lifecycle.

- [ ] **Step 9.1: `SchedulingModule : ThemiaModuleBase`** — `ConfigureServices`: register Quartz (host owns the scheduler), `services.AddThemiaQuartz(o => { o.VirtualPathRoot = "/jobs"; o.Authorize = ctx => <Themia-claims check>; })`, and `services.AddSingleton<IExecutionHistoryStore, EfExecutionHistoryStore>()`. `InitializeAsync`: run the EF/FluentMigrator migration for the history table. Match the exact `ThemiaModuleBase` member signatures found in Step 7.1.

- [ ] **Step 9.2: Lifecycle test** (`tests/Themia.Modules.Scheduling.IntegrationTests`): register the module via `AddThemiaModule<SchedulingModule>()` + `InitializeThemiaModulesAsync()` against a Testcontainers DB; assert the migration ran (history table exists) and `IExecutionHistoryStore` resolves to `EfExecutionHistoryStore`. Follow the module-test pattern used elsewhere (Step 7.1 reading).

- [ ] **Step 9.3:** Build + test. Commit:
```bash
git commit -m "feat(scheduling): SchedulingModule wires Quartz + dashboard + EF history store; runs migration on init"
```

---

## Task 10: Docs, PublicAPI finalize, vulnerability scan

- [ ] **Step 10.1:** `dotnet list package --vulnerable --include-transitive 2>&1 | grep -i -A2 vulnerab` — review (esp. Newtonsoft.Json 13.0.4, Quartz 3.18.0). Document any unfixable transitive in `VENDORING.md`.
- [ ] **Step 10.2:** Update `CHANGELOG.md` `[Unreleased]` with **Added**: `Themia.Quartz` (neutral Quartz dashboard, host-supplied auth, in-memory history store) and `Themia.Modules.Scheduling` (EF global history store + module). Note the Newtonsoft.Json vendoring trade-off under a "Notes".
- [ ] **Step 10.3:** Update `docs/themia-architecture-overview.md` status table rows for Scheduling/`Themia.Quartz` → ✅ shipped.
- [ ] **Step 10.4:** `dotnet build Themia.sln -c Release --no-incremental` → 0 warnings; `dotnet test Themia.sln -c Release --filter "Category!=Integration"` green; (Docker) integration green. Commit:
```bash
git commit -m "docs(scheduling): changelog + architecture status; vulnerability scan notes"
```

---

## Final verification (after all tasks)
- [ ] `dotnet build Themia.sln -c Release --no-incremental` → **0 warnings** (both TFMs; vendored `<NoWarn>` minimal + documented).
- [ ] `dotnet test Themia.sln -c Release --filter "Category!=Integration"` → green (incl. net8 + net10 dashboard smoke).
- [ ] (Docker) `dotnet test Themia.sln -c Release --filter Category=Integration` → green (EF store + module lifecycle).
- [ ] Embedded dashboard content served (Task 6 — the open-item-#1 gate). Routes resolve; deny-all → 403; authorized → 200.
- [ ] `THIRD-PARTY-NOTICES/SilkierQuartz-LICENSE.txt` present; `VENDORING.md` records the pinned SHA + deviations.
- [ ] Dispatch a final code review over the branch before opening the PR.

**Release:** new minor — bump `<Version>` → **0.4.0** (new packages/capability) in the release PR + promote CHANGELOG.

---

## Self-Review

**Spec coverage** (vs `2026-06-01-themia-quartz-scheduling-design.md`): `Themia.Quartz` neutral core (Tasks 1–6); vendored RecentHistory plugin + its `IExecutionHistoryStore` reused as the contract, NOT a parallel interface (Task 2 — addresses MAJOR 4); drop `AuthenticateController`, host-supplied `Authorize` delegate default-deny (Tasks 3–4 — auth seam); `ThemiaQuartzOptions` (Task 4); `AddThemiaQuartz`/`MapThemiaQuartz` (Task 5); Newtonsoft internal-only (policy note + Task 5); embedded-resource re-namespacing validated by smoke test (Task 6 — open item #1); net8+net10 for the core (Task 1, tested Task 6). Module: `SchedulingModule : ThemiaModuleBase` (Task 9), `EfExecutionHistoryStore` GLOBAL/no-TenantId (Task 8 — addresses MAJOR 2), migration on `InitializeAsync` (Task 9), `.Scheduling` naming (open item #3 — adopted). Quartz pinned 3.18.0 (open item #2 — pinned, not floated). Testing + vulnerability scan (Tasks 6/8/9/10).

**Placeholder scan:** the vendoring tasks (2, 3) are intentionally specified as exact shell operations + the precise edits (2 prefix strings, deletions, namespace replace) rather than pasting ~150 upstream files — this is the correct granularity for a bulk-vendor op, not a placeholder. Authored code (options, extensions, store, module) is shown in full or with an exact port-from-upstream instruction naming the source member. Two genuine soft spots flagged inline for the implementer: (a) Task 3 sed over-reach on type identifiers → mitigated by deleting options/auth files first + review-the-diff; (b) Task 8 migration mechanism + counter persistence → instructed to match the existing repo convention via grep rather than inventing one. Both name the concrete resolution path.

**Type consistency:** `IExecutionHistoryStore` (12 members) defined in Task 2 is the same contract implemented by `InProcExecutionHistoryStore` (Task 2) and `EfExecutionHistoryStore` (Task 8) and bridged in Task 5. `ThemiaQuartzOptions` (Task 4) is consumed by `AddThemiaQuartz` (Task 5) and `SchedulingModule` (Task 9). `ExecutionHistoryEntry` (Task 2) ↔ `ExecutionHistoryRecord` EF entity (Task 8) mapping is explicit.

**Risk note:** Tasks 3–4 (vendor + auth seam) are the highest-risk; the user opted for full-plan-now over a spike, so Task 6's embedded-content smoke test is the explicit validation gate — if it fails, the fix is localized to Step 1.2 globs / Step 3.4 prefix strings. Consider executing Tasks 3+4 as a single green-checkpoint unit (noted in Task 3.6).
