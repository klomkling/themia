# Exceptions Dashboard (0.5.8) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A mountable, self-rendered, read-only exceptions dashboard (`/exceptions` list + detail) in a new `Themia.Exceptional.AspNetCore` package, backed by the existing `IExceptionStore`.

**Architecture:** Server-rendered HTML (no Razor/SPA) over `IExceptionStore`. A pure internal `DashboardHtml` renderer (every value HTML-encoded) is unit-tested in isolation; minimal-API endpoints (`MapThemiaExceptional`) wire it up with a fail-closed `Authorize` predicate and are integration-tested via `TestServer` + an in-memory store fake.

**Tech Stack:** .NET (`net8.0;net10.0`), minimal APIs, `System.Net.WebUtility` for encoding, xUnit + `Microsoft.AspNetCore.TestHost`, PublicAPI analyzer (RS0016), central version in `Directory.Build.props`. `TreatWarningsAsErrors=true`.

**Spec:** `docs/superpowers/specs/2026-06-20-themia-exceptional-dashboard-design.md`

All commands run from `Packages/themia/`.

---

## File Structure

| File | Responsibility | Action |
|------|----------------|--------|
| `src/neutral/Themia.Exceptional.AspNetCore/Themia.Exceptional.AspNetCore.csproj` | New package | Create |
| `.../PublicAPI.Shipped.txt` / `.../PublicAPI.Unshipped.txt` | PublicAPI tracking | Create |
| `.../AssemblyInfo.cs` | `InternalsVisibleTo` the test project | Create |
| `.../DashboardHtml.cs` | Internal pure HTML renderer (`Enc`/`Page`/`List`/`Detail`) | Create |
| `.../ExceptionalDashboardOptions.cs` | Options (`Authorize`, paging, title, ShowRequestBody) | Create |
| `.../ExceptionalDashboardEndpoints.cs` | `MapThemiaExceptional` + auth gate + list/detail handlers | Create |
| `tests/Themia.Exceptional.AspNetCore.Tests/Themia.Exceptional.AspNetCore.Tests.csproj` | Test project | Create |
| `tests/Themia.Exceptional.AspNetCore.Tests/DashboardHtmlTests.cs` | Renderer unit tests (incl. XSS) | Create |
| `tests/Themia.Exceptional.AspNetCore.Tests/FakeExceptionStore.cs` | In-memory `IExceptionStore` | Create |
| `tests/Themia.Exceptional.AspNetCore.Tests/ExceptionalDashboardTests.cs` | Endpoint integration tests | Create |
| `Directory.Build.props` | Version bump to 0.5.8 | Modify |

---

## Task 1: Scaffold the package and test project

**Files:** all under `src/neutral/Themia.Exceptional.AspNetCore/` and `tests/Themia.Exceptional.AspNetCore.Tests/`.

- [ ] **Step 1: Create the library csproj** `src/neutral/Themia.Exceptional.AspNetCore/Themia.Exceptional.AspNetCore.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.Exceptional.AspNetCore</PackageId>
    <Description>Themia.Exceptional ASP.NET Core dashboard — a mountable, self-rendered, read-only /exceptions UI (list + detail) over IExceptionStore, with a fail-closed Authorize hook.</Description>
    <PackageTags>themia;exceptional;dashboard;aspnetcore;diagnostics</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Exceptional/Themia.Exceptional.csproj" />
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Create PublicAPI files**

`src/neutral/Themia.Exceptional.AspNetCore/PublicAPI.Shipped.txt`:
```text
#nullable enable
```
`src/neutral/Themia.Exceptional.AspNetCore/PublicAPI.Unshipped.txt`:
```text
#nullable enable
```

- [ ] **Step 3: Create `AssemblyInfo.cs`** (exposes internals to the test project for the `DashboardHtml` unit tests):

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Themia.Exceptional.AspNetCore.Tests")]
```

- [ ] **Step 4: Create the test csproj** `tests/Themia.Exceptional.AspNetCore.Tests/Themia.Exceptional.AspNetCore.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Microsoft.AspNetCore.TestHost" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="coverlet.collector">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/neutral/Themia.Exceptional.AspNetCore/Themia.Exceptional.AspNetCore.csproj" />
    <ProjectReference Include="../../src/neutral/Themia.Exceptional/Themia.Exceptional.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Add both projects to the solution**

```bash
dotnet sln Themia.sln add src/neutral/Themia.Exceptional.AspNetCore/Themia.Exceptional.AspNetCore.csproj
dotnet sln Themia.sln add tests/Themia.Exceptional.AspNetCore.Tests/Themia.Exceptional.AspNetCore.Tests.csproj
```
Expected: "Project ... added to the solution." twice.

- [ ] **Step 6: Build the empty library**

Run: `dotnet build src/neutral/Themia.Exceptional.AspNetCore/Themia.Exceptional.AspNetCore.csproj --no-incremental`
Expected: Build succeeded, 0 warnings, 0 errors (both TFMs).

- [ ] **Step 7: Commit**

```bash
git add src/neutral/Themia.Exceptional.AspNetCore tests/Themia.Exceptional.AspNetCore.Tests Themia.sln
git commit -m "build: scaffold Themia.Exceptional.AspNetCore package + test project"
```

---

## Task 2: `DashboardHtml` renderer (pure, HTML-encoded)

**Files:**
- Test: `tests/Themia.Exceptional.AspNetCore.Tests/DashboardHtmlTests.cs`
- Create: `src/neutral/Themia.Exceptional.AspNetCore/DashboardHtml.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Exceptional.AspNetCore.Tests/DashboardHtmlTests.cs`:

```csharp
using System.Collections.Generic;
using Themia.Exceptional;
using Themia.Exceptional.AspNetCore;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

public class DashboardHtmlTests
{
    private static ExceptionEntry Entry(string message) => new()
    {
        Guid = Guid.NewGuid(),
        ApplicationName = "app",
        Type = "System.Exception",
        Message = message,
        Detail = "{}",
        DuplicateCount = 1,
    };

    [Fact]
    public void List_EncodesMessage_NoRawScript()
    {
        var items = new List<ExceptionEntry> { Entry("<script>alert(1)</script>") };
        var filter = new ExceptionFilter { Page = 1, PageSize = 50 };

        var html = DashboardHtml.List("Exceptions", "/exceptions", items, total: 1, filter);

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }

    [Fact]
    public void List_LinksToDetailByGuid()
    {
        var e = Entry("boom");
        var html = DashboardHtml.List("Exceptions", "/exceptions", new List<ExceptionEntry> { e }, 1, new ExceptionFilter());

        Assert.Contains($"/exceptions/{e.Guid}", html);
    }

    [Fact]
    public void Detail_EncodesRequestBody_AndRespectsShowFlag()
    {
        var e = Entry("boom");
        e.RequestBody = "<script>steal()</script>";

        var shown = DashboardHtml.Detail("Exceptions", "/exceptions", e, showRequestBody: true);
        Assert.Contains("&lt;script&gt;steal()&lt;/script&gt;", shown);
        Assert.DoesNotContain("<script>steal()</script>", shown);

        var hidden = DashboardHtml.Detail("Exceptions", "/exceptions", e, showRequestBody: false);
        Assert.DoesNotContain("steal()", hidden);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Exceptional.AspNetCore.Tests/Themia.Exceptional.AspNetCore.Tests.csproj --filter "FullyQualifiedName~DashboardHtmlTests"`
Expected: FAIL — compile error, `DashboardHtml` does not exist.

- [ ] **Step 3: Write the renderer**

Create `src/neutral/Themia.Exceptional.AspNetCore/DashboardHtml.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Text;
using Themia.Exceptional;

namespace Themia.Exceptional.AspNetCore;

/// <summary>Pure, self-contained HTML rendering for the exceptions dashboard. Every dynamic value is
/// HTML-encoded — exception data (messages, request bodies, URLs) is attacker-influenceable.</summary>
internal static class DashboardHtml
{
    private const string Style =
        "<style>body{font:14px system-ui,sans-serif;margin:1rem}table{border-collapse:collapse;width:100%}" +
        "th,td{border:1px solid #ddd;padding:4px 8px;text-align:left;vertical-align:top}th{background:#f5f5f5}" +
        "pre{background:#f8f8f8;padding:8px;overflow:auto;white-space:pre-wrap}a{color:#0366d6}</style>";

    public static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    public static string Page(string title, string body) =>
        $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{Enc(title)}</title>{Style}</head><body>{body}</body></html>";

    public static string List(string title, string path, IReadOnlyList<ExceptionEntry> items, int total, ExceptionFilter filter)
    {
        var sb = new StringBuilder();
        sb.Append("<h1>").Append(Enc(title)).Append("</h1>");

        sb.Append("<form method=\"get\" action=\"").Append(Enc(path)).Append("\">")
          .Append("<input name=\"q\" value=\"").Append(Enc(filter.Search)).Append("\" placeholder=\"search\"> ")
          .Append("<input name=\"app\" value=\"").Append(Enc(filter.ApplicationName)).Append("\" placeholder=\"app\"> ")
          .Append("<input name=\"tenant\" value=\"").Append(Enc(filter.TenantId)).Append("\" placeholder=\"tenant\"> ")
          .Append("<button type=\"submit\">Filter</button></form>");

        sb.Append("<table><tr><th>Last log</th><th>App</th><th>Type</th><th>Message</th><th>Status</th><th>Count</th><th>Tenant</th></tr>");
        foreach (var e in items)
        {
            sb.Append("<tr>")
              .Append("<td>").Append(Enc(e.LastLogDate.ToString("u", CultureInfo.InvariantCulture))).Append("</td>")
              .Append("<td>").Append(Enc(e.ApplicationName)).Append("</td>")
              .Append("<td><a href=\"").Append(Enc(path)).Append('/').Append(e.Guid).Append("\">").Append(Enc(e.Type)).Append("</a></td>")
              .Append("<td>").Append(Enc(e.Message)).Append("</td>")
              .Append("<td>").Append(Enc(e.StatusCode?.ToString(CultureInfo.InvariantCulture))).Append("</td>")
              .Append("<td>").Append(e.DuplicateCount).Append("</td>")
              .Append("<td>").Append(Enc(e.TenantId)).Append("</td>")
              .Append("</tr>");
        }
        sb.Append("</table>");

        var hasPrev = filter.Page > 1;
        var hasNext = (long)filter.Page * filter.PageSize < total;
        sb.Append("<p>");
        if (hasPrev)
        {
            sb.Append("<a href=\"").Append(Enc(path)).Append("?page=").Append(filter.Page - 1)
              .Append("&pageSize=").Append(filter.PageSize).Append("\">Prev</a> ");
        }
        sb.Append("Page ").Append(filter.Page).Append(" (").Append(total).Append(" total) ");
        if (hasNext)
        {
            sb.Append("<a href=\"").Append(Enc(path)).Append("?page=").Append(filter.Page + 1)
              .Append("&pageSize=").Append(filter.PageSize).Append("\">Next</a>");
        }
        sb.Append("</p>");

        return Page(title, sb.ToString());
    }

    public static string Detail(string title, string path, ExceptionEntry e, bool showRequestBody)
    {
        var sb = new StringBuilder();
        sb.Append("<p><a href=\"").Append(Enc(path)).Append("\">&larr; back</a></p>");
        sb.Append("<h1>").Append(Enc(e.Type)).Append("</h1>");
        sb.Append("<p>").Append(Enc(e.Message)).Append("</p>");

        sb.Append("<table>");
        Row(sb, "Guid", e.Guid.ToString());
        Row(sb, "Application", e.ApplicationName);
        Row(sb, "Machine", e.MachineName);
        Row(sb, "Tenant", e.TenantId);
        Row(sb, "Status", e.StatusCode?.ToString(CultureInfo.InvariantCulture));
        Row(sb, "Method", e.HttpMethod);
        Row(sb, "Url", e.Url);
        Row(sb, "Host", e.Host);
        Row(sb, "IP", e.IpAddress);
        Row(sb, "Source", e.Source);
        Row(sb, "Count", e.DuplicateCount.ToString(CultureInfo.InvariantCulture));
        Row(sb, "Created", e.CreationDate.ToString("u", CultureInfo.InvariantCulture));
        Row(sb, "Last log", e.LastLogDate.ToString("u", CultureInfo.InvariantCulture));
        Row(sb, "Protected", e.IsProtected.ToString());
        sb.Append("</table>");

        sb.Append("<h2>Detail</h2><pre>").Append(Enc(e.Detail)).Append("</pre>");
        if (showRequestBody && e.RequestBody is not null)
        {
            sb.Append("<h2>Request body</h2><pre>").Append(Enc(e.RequestBody)).Append("</pre>");
        }

        return Page(title, sb.ToString());
    }

    private static void Row(StringBuilder sb, string key, string? value) =>
        sb.Append("<tr><th>").Append(Enc(key)).Append("</th><td>").Append(Enc(value)).Append("</td></tr>");
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Themia.Exceptional.AspNetCore.Tests/Themia.Exceptional.AspNetCore.Tests.csproj --filter "FullyQualifiedName~DashboardHtmlTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/neutral/Themia.Exceptional.AspNetCore/DashboardHtml.cs tests/Themia.Exceptional.AspNetCore.Tests/DashboardHtmlTests.cs
git commit -m "feat: add DashboardHtml renderer (HTML-encoded list + detail)"
```

---

## Task 3: Options, endpoints, auth gate (integration)

**Files:**
- Create: `.../ExceptionalDashboardOptions.cs`, `.../ExceptionalDashboardEndpoints.cs`
- Test: `tests/Themia.Exceptional.AspNetCore.Tests/FakeExceptionStore.cs`, `.../ExceptionalDashboardTests.cs`
- Modify: `src/neutral/Themia.Exceptional.AspNetCore/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Write the in-memory store fake**

Create `tests/Themia.Exceptional.AspNetCore.Tests/FakeExceptionStore.cs`:

```csharp
using Themia.Exceptional;

namespace Themia.Exceptional.AspNetCore.Tests;

/// <summary>In-memory IExceptionStore for endpoint tests; records the last filter passed to ListAsync.</summary>
internal sealed class FakeExceptionStore : IExceptionStore
{
    private readonly List<ExceptionEntry> _entries;

    public FakeExceptionStore(params ExceptionEntry[] entries) => _entries = entries.ToList();

    public ExceptionFilter? LastFilter { get; private set; }

    public Task<PagedResult<ExceptionEntry>> ListAsync(ExceptionFilter filter, CancellationToken cancellationToken = default)
    {
        LastFilter = filter;
        return Task.FromResult(new PagedResult<ExceptionEntry> { Items = _entries, Total = _entries.Count });
    }

    public Task<ExceptionEntry?> GetAsync(Guid guid, CancellationToken cancellationToken = default) =>
        Task.FromResult(_entries.FirstOrDefault(e => e.Guid == guid));

    public Task LogAsync(ExceptionEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<int> CountAsync(ExceptionFilter filter, CancellationToken cancellationToken = default) => Task.FromResult(_entries.Count);
    public Task<bool> ProtectAsync(Guid guid, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> DeleteAsync(Guid guid, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> HardDeleteAsync(Guid guid, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<int> PurgeAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default) => Task.FromResult(0);
}
```

- [ ] **Step 2: Write the failing endpoint tests**

Create `tests/Themia.Exceptional.AspNetCore.Tests/ExceptionalDashboardTests.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.Exceptional;
using Themia.Exceptional.AspNetCore;
using Xunit;

namespace Themia.Exceptional.AspNetCore.Tests;

public class ExceptionalDashboardTests
{
    private static readonly Guid KnownGuid = new("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static ExceptionEntry Sample(string message = "boom") => new()
    {
        Guid = KnownGuid,
        ApplicationName = "app",
        Type = "System.Exception",
        Message = message,
        Detail = "{}",
        DuplicateCount = 1,
    };

    private static async Task<HttpClient> ServerAsync(IExceptionStore store, Action<ExceptionalDashboardOptions>? configure)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddSingleton(store);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapThemiaExceptional("/exceptions", configure));
                });
            })
            .StartAsync();
        return host.GetTestClient();
    }

    [Fact]
    public async Task List_NoAuthorize_Returns404()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample()), configure: null);
        var res = await client.GetAsync("/exceptions");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task List_AuthorizeFalse_Returns404()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample()), o => o.Authorize = _ => Task.FromResult(false));
        var res = await client.GetAsync("/exceptions");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task List_Authorized_Returns200_WithRows()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync("/exceptions");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("System.Exception", body);
        Assert.Contains($"/exceptions/{KnownGuid}", body);
    }

    [Fact]
    public async Task List_EncodesMessage_NoRawScript()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample("<script>alert(1)</script>")), o => o.Authorize = _ => Task.FromResult(true));
        var body = await (await client.GetAsync("/exceptions")).Content.ReadAsStringAsync();
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", body);
        Assert.DoesNotContain("<script>alert(1)</script>", body);
    }

    [Fact]
    public async Task List_FlowsFilterAndClampsPageSize()
    {
        var store = new FakeExceptionStore(Sample());
        var client = await ServerAsync(store, o => { o.Authorize = _ => Task.FromResult(true); o.MaxPageSize = 100; });

        await client.GetAsync("/exceptions?q=boom&app=app&tenant=t1&page=2&pageSize=9999");

        Assert.NotNull(store.LastFilter);
        Assert.Equal("boom", store.LastFilter!.Search);
        Assert.Equal("app", store.LastFilter.ApplicationName);
        Assert.Equal("t1", store.LastFilter.TenantId);
        Assert.Equal(2, store.LastFilter.Page);
        Assert.Equal(100, store.LastFilter.PageSize); // clamped to MaxPageSize
    }

    [Fact]
    public async Task Detail_KnownGuid_Returns200()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync($"/exceptions/{KnownGuid}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("System.Exception", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Detail_UnknownGuid_Returns404()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample()), o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync($"/exceptions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Detail_Unauthorized_Returns404()
    {
        var client = await ServerAsync(new FakeExceptionStore(Sample()), o => o.Authorize = _ => Task.FromResult(false));
        var res = await client.GetAsync($"/exceptions/{KnownGuid}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Detail_ShowRequestBodyFalse_OmitsBody()
    {
        var entry = Sample();
        entry.RequestBody = "secret-token-xyz";
        var client = await ServerAsync(new FakeExceptionStore(entry), o => { o.Authorize = _ => Task.FromResult(true); o.ShowRequestBody = false; });
        var body = await (await client.GetAsync($"/exceptions/{KnownGuid}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-token-xyz", body);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Themia.Exceptional.AspNetCore.Tests/Themia.Exceptional.AspNetCore.Tests.csproj --filter "FullyQualifiedName~ExceptionalDashboardTests"`
Expected: FAIL — compile error, `ExceptionalDashboardOptions` / `MapThemiaExceptional` do not exist.

- [ ] **Step 4: Write the options**

Create `src/neutral/Themia.Exceptional.AspNetCore/ExceptionalDashboardOptions.cs`:

```csharp
using Microsoft.AspNetCore.Http;

namespace Themia.Exceptional.AspNetCore;

/// <summary>Configuration for the mountable exceptions dashboard.</summary>
public sealed class ExceptionalDashboardOptions
{
    /// <summary>Gate run for every dashboard request. When <c>null</c>, all requests are denied
    /// (fail-closed) — the dashboard cannot be served without an explicit predicate.</summary>
    public Func<HttpContext, Task<bool>>? Authorize { get; set; }

    /// <summary>Rows per page when the request omits <c>pageSize</c>. Default 50.</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>Hard upper bound on rows per page (clamps the <c>pageSize</c> query param). Default 200.</summary>
    public int MaxPageSize { get; set; } = 200;

    /// <summary>Page heading and document title. Default "Exceptions".</summary>
    public string Title { get; set; } = "Exceptions";

    /// <summary>Whether the detail view renders the captured request body (sensitive; only ever shown
    /// behind <see cref="Authorize"/>). Default <c>true</c>.</summary>
    public bool ShowRequestBody { get; set; } = true;
}
```

- [ ] **Step 5: Write the endpoints**

Create `src/neutral/Themia.Exceptional.AspNetCore/ExceptionalDashboardEndpoints.cs`:

```csharp
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Themia.Exceptional;

namespace Themia.Exceptional.AspNetCore;

/// <summary>Mounts the self-rendered, read-only exceptions dashboard.</summary>
public static class ExceptionalDashboardEndpoints
{
    /// <summary>Maps the exceptions dashboard (list at <paramref name="path"/>, detail at
    /// <c>{path}/{guid}</c>) and returns the route group. Access is governed by
    /// <see cref="ExceptionalDashboardOptions.Authorize"/> (fail-closed when unset).</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">Route prefix (default <c>/exceptions</c>).</param>
    /// <param name="configure">Optional configuration callback.</param>
    /// <returns>The route group for further configuration.</returns>
    public static RouteGroupBuilder MapThemiaExceptional(
        this IEndpointRouteBuilder endpoints,
        string path = "/exceptions",
        Action<ExceptionalDashboardOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = new ExceptionalDashboardOptions();
        configure?.Invoke(options);

        if (options.Authorize is null)
        {
            var logger = endpoints.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("Themia.Exceptional.AspNetCore");
            logger?.LogWarning(
                "Exceptions dashboard mounted at {Path} without an Authorize predicate; all requests are denied.", path);
        }

        var group = endpoints.MapGroup(path);
        group.MapGet("/", (HttpContext ctx, IExceptionStore store, CancellationToken ct) => HandleListAsync(ctx, store, options, path, ct));
        group.MapGet("/{guid:guid}", (Guid guid, HttpContext ctx, IExceptionStore store, CancellationToken ct) => HandleDetailAsync(ctx, store, options, path, guid, ct));
        return group;
    }

    private static async Task HandleListAsync(HttpContext ctx, IExceptionStore store, ExceptionalDashboardOptions options, string path, CancellationToken ct)
    {
        if (!await AuthorizedAsync(ctx, options).ConfigureAwait(false)) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }

        var filter = BuildFilter(ctx.Request.Query, options);
        var result = await store.ListAsync(filter, ct).ConfigureAwait(false);
        await WriteHtmlAsync(ctx, DashboardHtml.List(options.Title, path, result.Items, result.Total, filter), ct).ConfigureAwait(false);
    }

    private static async Task HandleDetailAsync(HttpContext ctx, IExceptionStore store, ExceptionalDashboardOptions options, string path, Guid guid, CancellationToken ct)
    {
        if (!await AuthorizedAsync(ctx, options).ConfigureAwait(false)) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }

        var entry = await store.GetAsync(guid, ct).ConfigureAwait(false);
        if (entry is null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }

        await WriteHtmlAsync(ctx, DashboardHtml.Detail(options.Title, path, entry, options.ShowRequestBody), ct).ConfigureAwait(false);
    }

    private static async Task<bool> AuthorizedAsync(HttpContext ctx, ExceptionalDashboardOptions options) =>
        options.Authorize is not null && await options.Authorize(ctx).ConfigureAwait(false);

    private static ExceptionFilter BuildFilter(IQueryCollection query, ExceptionalDashboardOptions options)
    {
        var filter = new ExceptionFilter
        {
            Page = ParseInt(query["page"], 1, 1, int.MaxValue),
            PageSize = ParseInt(query["pageSize"], options.DefaultPageSize, 1, options.MaxPageSize),
            Search = NullIfEmpty(query["q"]),
            ApplicationName = NullIfEmpty(query["app"]),
            TenantId = NullIfEmpty(query["tenant"]),
            IncludeDeleted = string.Equals(query["includeDeleted"], "true", StringComparison.OrdinalIgnoreCase),
        };
        if (DateTime.TryParse(query["from"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var from)) filter.From = from;
        if (DateTime.TryParse(query["to"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var to)) filter.To = to;
        return filter;
    }

    private static int ParseInt(StringValues raw, int fallback, int min, int max)
    {
        var value = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        return Math.Clamp(value, min, max);
    }

    private static string? NullIfEmpty(StringValues raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.ToString();

    private static Task WriteHtmlAsync(HttpContext ctx, string html, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        return ctx.Response.WriteAsync(html, Encoding.UTF8, ct);
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Themia.Exceptional.AspNetCore.Tests/Themia.Exceptional.AspNetCore.Tests.csproj --filter "FullyQualifiedName~ExceptionalDashboardTests"`
Expected: PASS (9 tests). If `List_NoAuthorize_Returns404` instead returns 200/500 for `GET /exceptions`, the group root pattern needs adjusting — change `group.MapGet("/", ...)` to `group.MapGet("", ...)` and re-run.

- [ ] **Step 7: Update PublicAPI**

Append to `src/neutral/Themia.Exceptional.AspNetCore/PublicAPI.Unshipped.txt`:

```text
Themia.Exceptional.AspNetCore.ExceptionalDashboardEndpoints
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions.ExceptionalDashboardOptions() -> void
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions.Authorize.get -> System.Func<Microsoft.AspNetCore.Http.HttpContext!, System.Threading.Tasks.Task<bool>!>?
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions.Authorize.set -> void
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions.DefaultPageSize.get -> int
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions.DefaultPageSize.set -> void
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions.MaxPageSize.get -> int
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions.MaxPageSize.set -> void
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions.ShowRequestBody.get -> bool
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions.ShowRequestBody.set -> void
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions.Title.get -> string!
Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions.Title.set -> void
static Themia.Exceptional.AspNetCore.ExceptionalDashboardEndpoints.MapThemiaExceptional(this Microsoft.AspNetCore.Routing.IEndpointRouteBuilder! endpoints, string! path = "/exceptions", System.Action<Themia.Exceptional.AspNetCore.ExceptionalDashboardOptions!>? configure = null) -> Microsoft.AspNetCore.Routing.RouteGroupBuilder!
```

Then run `dotnet build src/neutral/Themia.Exceptional.AspNetCore/Themia.Exceptional.AspNetCore.csproj --no-incremental`; expect 0 warnings / 0 errors. If RS0016/RS0037 fire, reconcile each line to the exact signature the diagnostic prints and rebuild until clean.

- [ ] **Step 8: Commit**

```bash
git add src/neutral/Themia.Exceptional.AspNetCore/ExceptionalDashboardOptions.cs src/neutral/Themia.Exceptional.AspNetCore/ExceptionalDashboardEndpoints.cs src/neutral/Themia.Exceptional.AspNetCore/PublicAPI.Unshipped.txt tests/Themia.Exceptional.AspNetCore.Tests/FakeExceptionStore.cs tests/Themia.Exceptional.AspNetCore.Tests/ExceptionalDashboardTests.cs
git commit -m "feat: add MapThemiaExceptional dashboard endpoints (fail-closed auth, list + detail)"
```

---

## Task 4: Version bump to 0.5.8 and full verification

**Files:** Modify `Directory.Build.props`.

- [ ] **Step 1: Bump the shared version**

In `Directory.Build.props`, change the version line (inside the `Label="Version"` PropertyGroup):

```xml
    <Version>0.5.8</Version>
```

(from `<Version>0.5.7</Version>`)

- [ ] **Step 2: Full clean build (all TFMs)**

Run: `dotnet build Themia.sln --no-incremental`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 3: Full test run**

Run: `dotnet test Themia.sln`
Expected: All tests pass, including the new `DashboardHtmlTests` and `ExceptionalDashboardTests`.

- [ ] **Step 4: Commit**

```bash
git add Directory.Build.props
git commit -m "chore: bump version to 0.5.8 (exceptions dashboard)"
```

---

## Self-Review

**1. Spec coverage:**
- §2 new package (`net8.0;net10.0`, FrameworkReference, ProjectReference Themia.Exceptional, PublicAPI) → Task 1. ✓
- §3 public API (`ExceptionalDashboardOptions` with Authorize/DefaultPageSize/MaxPageSize/Title/ShowRequestBody; `MapThemiaExceptional` returning RouteGroupBuilder) → Task 3. ✓ **Note: `Authorize` is `Func<HttpContext, Task<bool>>` (matches the existing Quartz dashboard convention), not `ValueTask<bool>` as the spec draft showed — spec updated to match.**
- §4 endpoints (list with ExceptionFilter query parsing/paging; detail by guid, 404 when missing) → Task 3. ✓
- §5 auth fail-closed (404 unset/denied; warn when Authorize null) → Task 3. **Note: warn is logged once at mount time (eager) rather than per-request — simpler and equally visible; spec updated.** ✓
- §6 rendering/security (server HTML, mandatory `Enc`, self-contained inline style, no scripts, GET-only) → Tasks 2, 3; XSS encoding driven by tests in both. ✓
- §7 error handling (404 paths; page/pageSize clamp; bad from/to/includeDeleted ignored; store exceptions propagate) → Task 3 (`BuildFilter`, handlers). ✓
- §8 out of scope (no state-changing actions/JSON/charts) → not implemented, correctly. ✓
- §9 testing (auth 404s; list 200 + rows; filter flow + pageSize clamp; detail 200/404; ShowRequestBody=false; XSS encoded) → Tasks 2, 3. ✓
- Version 0.5.8 → Task 4. ✓

**2. Placeholder scan:** No TBD/TODO/"handle edge cases"; every code/PublicAPI/command step is literal. ✓

**3. Type consistency:** `ExceptionalDashboardOptions` members (`Authorize: Func<HttpContext,Task<bool>>?`, `DefaultPageSize`, `MaxPageSize`, `Title`, `ShowRequestBody`) consistent across the class, endpoints, tests, and PublicAPI. `DashboardHtml.List(string,string,IReadOnlyList<ExceptionEntry>,int,ExceptionFilter)` and `DashboardHtml.Detail(string,string,ExceptionEntry,bool)` signatures match between Task 2 def, Task 2 tests, and Task 3 callers. `IExceptionStore` fake implements the exact interface from `IExceptionStore.cs` (ListAsync/GetAsync/LogAsync/CountAsync/ProtectAsync/DeleteAsync/HardDeleteAsync/PurgeAsync). `ExceptionFilter` members (`Page`/`PageSize`/`Search`/`ApplicationName`/`TenantId`/`From`/`To`/`IncludeDeleted`) and `PagedResult<T>` (`Items`/`Total`) match the real types. `MapThemiaExceptional` signature matches between code, tests, and PublicAPI. ✓

**Spec edits applied during this review:** `Authorize` type `ValueTask<bool>`→`Task<bool>`; warn-once changed from per-request to eager-at-mount. (Made directly in the spec file for consistency.)
