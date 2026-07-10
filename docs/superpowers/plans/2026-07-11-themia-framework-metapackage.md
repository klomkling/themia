# Themia.Framework Metapackage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship an assembly-less `Themia.Framework` NuGet metapackage (9 framework dependencies, no data peer) plus a README "Which packages do I reference?" section, so adopters bootstrap with two references instead of assembling 6–7 core packages.

**Architecture:** One new packable csproj at `src/framework/Themia.Framework/` containing only `ProjectReference`s (`IncludeBuildOutput=false`), added to `Themia.sln` so the existing `dotnet pack Themia.sln` release step publishes it automatically at the shared version. One new test project proves (a) the packed nupkg has exactly the intended dependency list and no `lib/` payload, and (b) the quickstart claim — metapackage + one data-peer package — actually builds and boots a multi-tenant host.

**Tech Stack:** .NET 10, xunit, `Microsoft.AspNetCore.TestHost`, `System.IO.Compression` (nupkg inspection).

**Spec:** `docs/superpowers/specs/2026-07-11-themia-framework-metapackage-design.md`

## Global Constraints

- `TreatWarningsAsErrors=true` repo-wide — any warning fails the build.
- Central package management: NEVER add a `Version=` attribute on a `PackageReference`; versions live in `Directory.Packages.props` (all packages this plan uses are already there).
- Single shared version in `Directory.Build.props` `<Version>` — do not version the metapackage independently.
- `System.Text.Json` only; `ILogger<T>` only (no `Console.*`) — not expected to matter here (no runtime code), listed for completeness.
- The metapackage must NOT reference any `Themia.Framework.Data.EFCore*` or `Themia.Framework.Data.Dapper*` package — peer-neutrality is the design's core invariant.
- Work happens on branch `feature/framework-metapackage` (already exists, spec committed).
- Run commands from the repo root: `/Users/sarawut/GitHub/Idevs/single-repo/Packages/themia`.

---

### Task 1: `Themia.Framework` metapackage project + pack-shape test

**Files:**
- Create: `src/framework/Themia.Framework/Themia.Framework.csproj`
- Create: `tests/Themia.Framework.Tests/Themia.Framework.Tests.csproj`
- Create: `tests/Themia.Framework.Tests/MetaPackagePackTests.cs`
- Modify: `Themia.sln` (via `dotnet sln add`, not hand-editing)

**Interfaces:**
- Consumes: existing framework projects under `src/framework/` (reference-only).
- Produces: packable project `src/framework/Themia.Framework/Themia.Framework.csproj` with `PackageId` `Themia.Framework`; test project `tests/Themia.Framework.Tests/` that Task 2 adds a second test file to.

- [ ] **Step 1: Create the test project and add both projects to the solution**

Create `tests/Themia.Framework.Tests/Themia.Framework.Tests.csproj` (mirrors `tests/Themia.Framework.Core.Tests`, plus TestHost for Task 2):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.AspNetCore.TestHost" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
  </ItemGroup>
  <ItemGroup>
    <!-- Deliberately ONLY the quickstart references: the metapackage + one data peer.
         This is the compile-level proof of the README "two references" claim (Task 2
         adds the runtime proof). Do not add other Themia ProjectReferences here. -->
    <ProjectReference Include="../../src/framework/Themia.Framework/Themia.Framework.csproj" />
    <ProjectReference Include="../../src/framework/Themia.Framework.Data.EFCore.SqlServer/Themia.Framework.Data.EFCore.SqlServer.csproj" />
  </ItemGroup>
</Project>
```

Note: the referenced `src/framework/Themia.Framework/Themia.Framework.csproj` does not exist yet — that is the failing state for Step 2. Register both paths in the solution now so later builds see them:

```bash
dotnet sln Themia.sln add src/framework/Themia.Framework/Themia.Framework.csproj
dotnet sln Themia.sln add tests/Themia.Framework.Tests/Themia.Framework.Tests.csproj
```

(If `dotnet sln add` refuses to add the not-yet-existing metapackage csproj, do Step 3's file creation first, then re-run the two commands, then continue with Step 2 — the test must still be seen failing before the pack shape is trusted.)

- [ ] **Step 2: Write the failing pack-shape test**

Create `tests/Themia.Framework.Tests/MetaPackagePackTests.cs`:

```csharp
using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

namespace Themia.Framework.Tests;

public sealed class MetaPackagePackTests
{
    private static readonly string[] ExpectedDependencyIds =
    [
        "Themia.Caching",
        "Themia.Framework.AspNetCore",
        "Themia.Framework.Core",
        "Themia.Framework.Data.Abstractions",
        "Themia.Logging",
        "Themia.Mediator",
        "Themia.MultiTenancy",
        "Themia.MultiTenancy.Mediator",
        "Themia.Services",
    ];

    [Fact]
    public void Pack_ProducesDependencyOnlyNupkg_WithExactExpectedDependencies()
    {
        var repoRoot = FindRepoRoot();
        var project = Path.Combine(repoRoot, "src", "framework", "Themia.Framework", "Themia.Framework.csproj");
        var outDir = Path.Combine(Path.GetTempPath(), $"themia-metapack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            RunDotnet($"pack \"{project}\" --output \"{outDir}\"", repoRoot);

            // Assembly-less: exactly one .nupkg, and no symbols package (IncludeSymbols
            // is overridden to false — there is no assembly to produce symbols for).
            var nupkg = Assert.Single(Directory.GetFiles(outDir, "*.nupkg"));
            Assert.Empty(Directory.GetFiles(outDir, "*.snupkg"));

            using var zip = ZipFile.OpenRead(nupkg);
            Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("lib/", StringComparison.Ordinal));

            var nuspecEntry = zip.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
            using var nuspecStream = nuspecEntry.Open();
            var nuspec = XDocument.Load(nuspecStream);

            // Local-name matching sidesteps the nuspec XML namespace.
            var dependencyIds = nuspec.Descendants()
                .Where(e => e.Name.LocalName == "dependency")
                .Select(e => (string?)e.Attribute("id"))
                .Where(id => id is not null)
                .Select(id => id!)
                .Distinct()
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(ExpectedDependencyIds, dependencyIds);
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Themia.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate Themia.sln above the test base directory.");
    }

    private static void RunDotnet(string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"dotnet {arguments} failed ({process.ExitCode}):\n{stdout}\n{stderr}");
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

```bash
dotnet test Themia.sln --filter MetaPackagePackTests 2>&1 | tail -20
```

Expected: build FAILURE (the metapackage csproj referenced by the test project does not exist yet). That is the failing state — the test cannot pass without the deliverable.

- [ ] **Step 4: Create the metapackage csproj**

Create `src/framework/Themia.Framework/Themia.Framework.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>Themia.Framework</PackageId>
    <Description>Themia framework metapackage — one reference for the framework core set (Core, Logging, Caching, Services, MultiTenancy, Mediator, MultiTenancy.Mediator, Data.Abstractions, AspNetCore integration). Deliberately excludes the data peer: add exactly one Themia.Framework.Data.EFCore.* or Themia.Framework.Data.Dapper.* package to complete the stack.</Description>
    <PackageTags>themia;framework;metapackage;aspnetcore;multi-tenancy;mediator</PackageTags>
    <!-- Assembly-less metapackage: ship dependencies only, no lib/ payload. -->
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <!-- No assembly -> no symbols; override the repo-wide IncludeSymbols=true (snupkg). -->
    <IncludeSymbols>false</IncludeSymbols>
    <!-- No compiled output per TFM is intentional for a metapackage. -->
    <NoWarn>$(NoWarn);NU5128</NoWarn>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <!--
      The framework core set. Some entries are transitively implied (Mediator already
      pulls Caching/Logging/Core) but are listed explicitly anyway — a metapackage's
      dependency list is its documentation.
      INVARIANT: no Themia.Framework.Data.EFCore*/Dapper* reference — the adopter picks
      exactly one peer+provider package themselves (see the spec).
    -->
    <ProjectReference Include="../Themia.Framework.Core/Themia.Framework.Core.csproj" />
    <ProjectReference Include="../Themia.Logging/Themia.Logging.csproj" />
    <ProjectReference Include="../Themia.Caching/Themia.Caching.csproj" />
    <ProjectReference Include="../Themia.Services/Themia.Services.csproj" />
    <ProjectReference Include="../Themia.MultiTenancy/Themia.MultiTenancy.csproj" />
    <ProjectReference Include="../Themia.Mediator/Themia.Mediator.csproj" />
    <ProjectReference Include="../Themia.MultiTenancy.Mediator/Themia.MultiTenancy.Mediator.csproj" />
    <ProjectReference Include="../Themia.Framework.Data.Abstractions/Themia.Framework.Data.Abstractions.csproj" />
    <ProjectReference Include="../Themia.Framework.AspNetCore/Themia.Framework.AspNetCore.csproj" />
  </ItemGroup>
</Project>
```

If Step 1's `dotnet sln add` was deferred for the metapackage csproj, run it now:

```bash
dotnet sln Themia.sln add src/framework/Themia.Framework/Themia.Framework.csproj
```

- [ ] **Step 5: Run the test to verify it passes**

```bash
dotnet test Themia.sln --filter MetaPackagePackTests 2>&1 | tail -10
```

Expected: PASS (1 test). If the dependency-id assertion fails, compare the actual list in the failure message against `ExpectedDependencyIds` — a mismatch means a ProjectReference is missing/extra in the metapackage csproj, not that the test list should be edited to match.

- [ ] **Step 6: Full solution build sanity check**

```bash
dotnet build Themia.sln 2>&1 | tail -5
```

Expected: `Build succeeded` with 0 warnings (TreatWarningsAsErrors would fail otherwise). This catches RS0016/NU5128-style surprises across the whole solution, not just the new projects.

- [ ] **Step 7: Commit**

```bash
git add src/framework/Themia.Framework/ tests/Themia.Framework.Tests/ Themia.sln
git commit -m "feat: add Themia.Framework metapackage with pack-shape test"
```

---

### Task 2: Bootstrap smoke test (quickstart claim)

**Files:**
- Create: `tests/Themia.Framework.Tests/MetaPackageBootstrapTests.cs`
- Test: same file (it is the test)

**Interfaces:**
- Consumes: the test project from Task 1 (its ONLY Themia references are the metapackage + `Themia.Framework.Data.EFCore.SqlServer`); existing public APIs `AddThemiaAspNetCore()`, `AddThemiaMultiTenancy(configureOptions, configure, useDefaultStrategies)`, `UseThemia()`, `MultiTenancyBuilder.UseHeaderStrategy()`, `.SeedTenants(...)`, `TenantInfo(string id, string identifier)`, `ITenantAccessor.Current?.Identifier` — all verified against `tests/Themia.Framework.AspNetCore.Tests/UseThemiaPipelineTests.cs`.
- Produces: nothing consumed later; this is the runtime proof of the README quickstart.

- [ ] **Step 1: Write the failing (compiling-but-new) smoke test**

Create `tests/Themia.Framework.Tests/MetaPackageBootstrapTests.cs`. The pattern is copied from `tests/Themia.Framework.AspNetCore.Tests/UseThemiaPipelineTests.cs` (`StartHostAsync` + header-tenant assertion) — the point is that it compiles and runs with the metapackage as the only framework reference:

```csharp
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.Framework.AspNetCore.Extensions;
using Themia.MultiTenancy;
using Themia.MultiTenancy.Abstractions;
using Xunit;

namespace Themia.Framework.Tests;

/// <summary>
/// Runtime proof of the README quickstart: referencing only the Themia.Framework
/// metapackage + one data peer, the Themia bootstrap (AddThemiaAspNetCore +
/// AddThemiaMultiTenancy + UseThemia) builds a host that serves a request and
/// resolves the tenant from the header strategy.
/// </summary>
public sealed class MetaPackageBootstrapTests
{
    [Fact]
    public async Task Quickstart_BootsHostAndResolvesTenant_WithMetapackageAndOnePeer()
    {
        string? observedIdentifier = null;
        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddThemiaAspNetCore();
                    services.AddThemiaMultiTenancy(
                        configure: b => b.UseHeaderStrategy().SeedTenants([new TenantInfo("1", "acme")]));
                });
                webHost.Configure(app =>
                {
                    app.UseThemia();
                    app.Run(context =>
                    {
                        observedIdentifier = context.RequestServices
                            .GetRequiredService<ITenantAccessor>().Current?.Identifier;
                        return Task.CompletedTask;
                    });
                });
            })
            .StartAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Tenant-ID", "acme");
        var response = await host.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("acme", observedIdentifier);
    }
}
```

- [ ] **Step 2: Run the new test**

```bash
dotnet test Themia.sln --filter MetaPackageBootstrapTests 2>&1 | tail -10
```

Expected: PASS (1 test). There is no red step for behavior here — the framework APIs already work; what this test pins is the *reference surface*. The meaningful failure mode to be aware of: a compile error in this file means the metapackage's dependency set does NOT cover the quickstart (e.g. a missing ProjectReference in the metapackage) — fix the metapackage csproj (and `ExpectedDependencyIds` in `MetaPackagePackTests` if the set legitimately changes), never by adding extra ProjectReferences to the test project.

- [ ] **Step 3: Run the whole new test project**

```bash
dotnet test Themia.sln --filter "FullyQualifiedName~Themia.Framework.Tests" 2>&1 | tail -10
```

Expected: PASS (2 tests: pack-shape + bootstrap).

- [ ] **Step 4: Commit**

```bash
git add tests/Themia.Framework.Tests/MetaPackageBootstrapTests.cs
git commit -m "test: bootstrap smoke proving metapackage + one peer boots a tenant-resolving host"
```

---

### Task 3: README "Which packages do I reference?" section

**Files:**
- Modify: `README.md` (insert a new `##` section immediately BEFORE the `## Building` heading, currently line 54)

**Interfaces:**
- Consumes: package names as they exist in `src/` (re-verify with `ls src/framework src/modules src/neutral` before writing — the spec's matrix was already corrected once against reality).
- Produces: the docs deliverable; no code consumes it.

- [ ] **Step 1: Insert the section**

Add to `README.md`, immediately before `## Building`:

```markdown
## Which packages do I reference?

Themia ships many small packages on purpose — native DB drivers (SqlClient/Npgsql/MySqlConnector),
heavy optional dependencies (AWS SDK, ClosedXML), and the EF-vs-Dapper data-peer choice are kept in
separate packages so your app only pulls what it actually uses. You never assemble them by hand:

### Quickstart (multi-tenant web app)

Two references:

1. `Themia.Framework` — the framework core set (Core, Logging, Caching, Services, MultiTenancy,
   Mediator, Data.Abstractions, ASP.NET Core integration) in one metapackage.
2. Exactly one data peer+provider package — your conscious choice, never made for you:
   - `Themia.Framework.Data.EFCore.SqlServer` or `Themia.Framework.Data.EFCore.PostgreSql`
   - `Themia.Framework.Data.Dapper.SqlServer`, `Themia.Framework.Data.Dapper.PostgreSql`, or
     `Themia.Framework.Data.Dapper.MySql`
   - (No `EFCore.MySql` yet — there is no EF Core 10 MySQL provider.)

Then bootstrap: `services.AddThemiaAspNetCore()` + `services.AddThemiaMultiTenancy(...)` +
`app.UseThemia()` (see the smoke test in `tests/Themia.Framework.Tests/MetaPackageBootstrapTests.cs`
for a minimal working host).

Non-web apps (workers, consoles): skip the metapackage — it includes the ASP.NET Core integration —
and reference the individual `Themia.*` packages you need instead.

### Adding features

| Scenario | Add |
|---|---|
| Identity (users/roles/JWT/external login) | `Themia.Modules.Identity.AspNetCore` (umbrella — pulls the Identity core, tokens, and external-auth packages) |
| Scheduling (persistent Quartz) | `Themia.Modules.Scheduling` |
| Storage (incl. S3) | `Themia.Modules.Storage` |
| Async/scheduled export (CSV + xlsx) | `Themia.Modules.Export` — heaviest umbrella: also pulls the Storage, Scheduling, and Notifications modules |
| PDF templates + rendering | `Themia.Modules.Pdf` |
| Exception logging + dashboard | `Themia.Exceptional` + one `Themia.Exceptional.{SqlServer\|PostgreSql\|MySql}` + `Themia.Exceptional.AspNetCore` |
| Notifications | `Themia.Modules.Notifications` + one `Themia.Modules.Notifications.{SqlServer\|PostgreSql\|MySql}` |

> **Peer-coupling caveat:** some modules (`Identity`, `Storage`, `Notifications`, `Export`)
> currently reference both the EF Core and Dapper data peers (tracked follow-up,
> `docs/2026-06-14-identity-followups.md`), so adding them pulls both stacks regardless of the
> peer you chose. The metapackage itself never does.
```

- [ ] **Step 2: Verify the claims against the tree**

```bash
ls src/framework src/modules src/neutral | grep -iE 'export|storage|notif|exceptional|identity|pdf|scheduling'
grep -oE 'Include="[^"]*\.csproj"' src/modules/Themia.Modules.Export/Themia.Modules.Export.csproj
```

Expected: every package named in the section exists; Export's references confirm the "heaviest umbrella" note (it lists `Themia.Modules.Storage`, `Themia.Modules.Scheduling`, `Themia.Modules.Notifications`). If anything diverges, fix the README text — not the code.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: add 'Which packages do I reference?' quickstart + scenario matrix to README"
```

---

### Task 4: CHANGELOG + shared version bump (release trigger)

**Files:**
- Modify: `CHANGELOG.md` (new section above `## [0.7.2]`, below `## [Unreleased]`)
- Modify: `Directory.Build.props` (`<Version>0.7.2</Version>` → `<Version>0.8.0</Version>`)

**Interfaces:**
- Consumes: nothing from earlier tasks (text-only).
- Produces: the version `release.yml` reads on merge to main — merging this PR auto-publishes 0.8.0 (all 48 packages) to NuGet.

- [ ] **Step 1: Add the CHANGELOG entry**

Insert into `CHANGELOG.md` directly under the `## [Unreleased]` section (above `## [0.7.2]`), using minor-bump semantics (new package = new feature, mirroring 0.7.0 for the Pdf module):

```markdown
## [0.8.0] - 2026-07-11

### Added

- `Themia.Framework` metapackage — assembly-less bundle of the framework core set (Core, Logging,
  Caching, Services, MultiTenancy, Mediator, MultiTenancy.Mediator, Data.Abstractions,
  Framework.AspNetCore). Deliberately excludes the data peer: adopters add exactly one
  `Themia.Framework.Data.{EFCore|Dapper}.{provider}` package. Quickstart = two references.
- README "Which packages do I reference?" section — quickstart + scenario matrix + peer-coupling
  caveat.
```

(Adjust the date to the actual commit date if different.)

- [ ] **Step 2: Bump the shared version**

In `Directory.Build.props`, change:

```xml
    <Version>0.7.2</Version>
```

to:

```xml
    <Version>0.8.0</Version>
```

- [ ] **Step 3: Full build + full test suite**

```bash
dotnet build Themia.sln --no-incremental 2>&1 | tail -5
dotnet test Themia.sln 2>&1 | tail -10
```

Expected: build succeeded, 0 warnings; all tests pass. (`--no-incremental` surfaces RS0016 PublicAPI diagnostics; the metapackage has no public API surface so none should appear.)

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md Directory.Build.props
git commit -m "chore: bump shared version to 0.8.0 for the Themia.Framework metapackage"
```

---

## Completion

After all tasks: push the branch and open a PR (`feature/framework-metapackage` → `main`). Merging auto-triggers `release.yml`, which packs the solution (now including `Themia.Framework`), publishes 0.8.0 to NuGet, tags `v0.8.0`, and creates the GitHub Release. Note for the PR body: 47 → 48 packages; the new one is dependency-only.
