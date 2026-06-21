# Themia.Pdf (Neutral Rendering Core) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `Themia.Pdf` — a neutral, stateless cross-cutting package that merges an HTML template with a model (Handlebars.Net) and prints HTML → PDF bytes via headless Chromium (PuppeteerSharp).

**Architecture:** One package `src/neutral/Themia.Pdf` (TFM `net8.0;net10.0`), two single-purpose interfaces (`IHtmlTemplateRenderer`, `IPdfRenderer`) with `internal` implementations, two options records (`PdfRenderOptions` per-render, `ThemiaPdfOptions` process-wide), and an `AddThemiaPdf` DI extension. PuppeteerSharp types are kept off the public surface behind a small `PdfPaperFormat` enum. No framework/EF/Dapper/ASP.NET dependency.

**Tech Stack:** .NET 8 + .NET 10, PuppeteerSharp 20.0.5, Handlebars.Net 2.1.6, xUnit, Microsoft.Extensions.{DependencyInjection,Logging}.Abstractions.

**Spec:** `docs/superpowers/specs/2026-06-21-themia-pdf-neutral-core-design.md`

---

## File Structure

**Production (`src/neutral/Themia.Pdf/`):**
- `Themia.Pdf.csproj` — package definition, TFM, deps, PublicAPI additional files.
- `AssemblyInfo.cs` — `InternalsVisibleTo("Themia.Pdf.Tests")`.
- `PdfPaperFormat.cs` — public enum (A3/A4/Letter/Legal/Tabloid).
- `PdfRenderOptions.cs` — public per-render options.
- `ThemiaPdfOptions.cs` — public process-wide provisioning/config options.
- `IHtmlTemplateRenderer.cs` — public interface.
- `IPdfRenderer.cs` — public interface.
- `HandlebarsHtmlTemplateRenderer.cs` — `internal` Handlebars impl.
- `PuppeteerPdfRenderer.cs` — `internal` PuppeteerSharp impl (managed browser, `IAsyncDisposable`).
- `ThemiaPdfServiceCollectionExtensions.cs` — public `AddThemiaPdf` (namespace `Microsoft.Extensions.DependencyInjection`).
- `PublicAPI.Shipped.txt` (empty), `PublicAPI.Unshipped.txt` (curated).

**Tests:**
- `tests/Themia.Pdf.Tests/` — fast unit tests, no Chromium.
- `tests/Themia.Pdf.IntegrationTests/` — Chromium-requiring render tests.

**Repo-wide edits:**
- `Directory.Packages.props` — add `PuppeteerSharp` 20.0.5 + `Microsoft.Extensions.Logging.Abstractions` 10.0.9.
- `Directory.Build.props` — `<Version>` `0.5.10 → 0.6.0`.
- `CHANGELOG.md` — Added entry.
- `Themia.sln` — add the three new projects (via `dotnet sln add`).

---

### Task 1: Scaffold the package and add dependencies

**Files:**
- Modify: `Directory.Packages.props`
- Create: `src/neutral/Themia.Pdf/Themia.Pdf.csproj`
- Create: `src/neutral/Themia.Pdf/AssemblyInfo.cs`
- Create: `src/neutral/Themia.Pdf/PublicAPI.Shipped.txt`
- Create: `src/neutral/Themia.Pdf/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Pin the new packages**

In `Directory.Packages.props`, add two `<PackageVersion>` lines inside the existing `<ItemGroup>` (next to the `Handlebars.Net` pin at line ~103 and the `Microsoft.Extensions.*` pins at line ~29):

```xml
<PackageVersion Include="PuppeteerSharp" Version="20.0.5" />
<PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.9" />
```

(`Handlebars.Net` 2.1.6 and `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.9 are already pinned — do not duplicate.)

- [ ] **Step 2: Create the csproj**

Create `src/neutral/Themia.Pdf/Themia.Pdf.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <RootNamespace>Themia.Pdf</RootNamespace>
    <PackageId>Themia.Pdf</PackageId>
    <Description>Themia neutral PDF rendering core — merge an HTML template with a model (Handlebars.Net) and print HTML to PDF via headless Chromium (PuppeteerSharp). Stateless; no tenant or database dependency.</Description>
    <PackageTags>themia;pdf;html;handlebars;puppeteer;rendering</PackageTags>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Handlebars.Net" />
    <PackageReference Include="PuppeteerSharp" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>

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
</Project>
```

- [ ] **Step 3: Create AssemblyInfo and empty PublicAPI files**

Create `src/neutral/Themia.Pdf/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Themia.Pdf.Tests")]
```

Create `src/neutral/Themia.Pdf/PublicAPI.Shipped.txt` as an empty file (no content).

Create `src/neutral/Themia.Pdf/PublicAPI.Unshipped.txt` with a single line:

```
#nullable enable
```

- [ ] **Step 4: Add the project to the solution and build**

Run:
```bash
cd /Users/sarawut/GitHub/Idevs/single-repo/Packages/themia
dotnet sln Themia.sln add src/neutral/Themia.Pdf/Themia.Pdf.csproj
dotnet build src/neutral/Themia.Pdf/Themia.Pdf.csproj
```
Expected: build succeeds (empty package, RS0016 not yet triggered — no public members).

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props src/neutral/Themia.Pdf Themia.sln
git commit -m "feat: scaffold Themia.Pdf neutral package"
```

---

### Task 2: `PdfPaperFormat` enum and `PdfRenderOptions`

**Files:**
- Create: `src/neutral/Themia.Pdf/PdfPaperFormat.cs`
- Create: `src/neutral/Themia.Pdf/PdfRenderOptions.cs`
- Create: `tests/Themia.Pdf.Tests/Themia.Pdf.Tests.csproj`
- Create: `tests/Themia.Pdf.Tests/PdfRenderOptionsTests.cs`

- [ ] **Step 1: Create the unit test project**

Create `tests/Themia.Pdf.Tests/Themia.Pdf.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <!-- Brings Microsoft.Extensions.DependencyInjection + .Logging (AddLogging, ServiceCollection,
         NullLogger) without extra package pins, matching Themia.Quartz.Tests. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
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
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/neutral/Themia.Pdf/Themia.Pdf.csproj" />
  </ItemGroup>
</Project>
```

Then: `dotnet sln Themia.sln add tests/Themia.Pdf.Tests/Themia.Pdf.Tests.csproj`

- [ ] **Step 2: Write the failing test**

Create `tests/Themia.Pdf.Tests/PdfRenderOptionsTests.cs`:

```csharp
using Themia.Pdf;
using Xunit;

namespace Themia.Pdf.Tests;

public sealed class PdfRenderOptionsTests
{
    [Fact]
    public void Defaults_MatchPortedEzyValues()
    {
        var o = new PdfRenderOptions();

        Assert.Equal(PdfPaperFormat.A4, o.PaperFormat);
        Assert.True(o.PrintBackground);
        Assert.Equal("20mm", o.MarginTop);
        Assert.Equal("20mm", o.MarginBottom);
        Assert.Equal("15mm", o.MarginLeft);
        Assert.Equal("15mm", o.MarginRight);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Themia.Pdf.Tests/Themia.Pdf.Tests.csproj --filter PdfRenderOptionsTests`
Expected: FAIL — `PdfRenderOptions` / `PdfPaperFormat` do not exist (compile error).

- [ ] **Step 4: Implement the enum and options**

Create `src/neutral/Themia.Pdf/PdfPaperFormat.cs`:

```csharp
namespace Themia.Pdf;

/// <summary>Paper size for a rendered PDF. Maps internally onto the PuppeteerSharp paper format.</summary>
public enum PdfPaperFormat
{
    /// <summary>A4 (210 × 297 mm). The default.</summary>
    A4 = 0,

    /// <summary>A3 (297 × 420 mm).</summary>
    A3,

    /// <summary>US Letter (8.5 × 11 in).</summary>
    Letter,

    /// <summary>US Legal (8.5 × 14 in).</summary>
    Legal,

    /// <summary>Tabloid (11 × 17 in).</summary>
    Tabloid,
}
```

Create `src/neutral/Themia.Pdf/PdfRenderOptions.cs`:

```csharp
namespace Themia.Pdf;

/// <summary>
/// Per-render output options for <see cref="IPdfRenderer"/>. Defaults match the ported ezy-assets
/// contract renderer: A4, printed backgrounds, 20 mm top/bottom and 15 mm left/right margins.
/// </summary>
public sealed class PdfRenderOptions
{
    /// <summary>Paper size. Default <see cref="PdfPaperFormat.A4"/>.</summary>
    public PdfPaperFormat PaperFormat { get; set; } = PdfPaperFormat.A4;

    /// <summary>Whether CSS backgrounds are printed. Default <see langword="true"/>.</summary>
    public bool PrintBackground { get; set; } = true;

    /// <summary>Top margin (CSS length, e.g. "20mm"). Default "20mm".</summary>
    public string MarginTop { get; set; } = "20mm";

    /// <summary>Bottom margin (CSS length). Default "20mm".</summary>
    public string MarginBottom { get; set; } = "20mm";

    /// <summary>Left margin (CSS length). Default "15mm".</summary>
    public string MarginLeft { get; set; } = "15mm";

    /// <summary>Right margin (CSS length). Default "15mm".</summary>
    public string MarginRight { get; set; } = "15mm";
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Themia.Pdf.Tests/Themia.Pdf.Tests.csproj --filter PdfRenderOptionsTests`
Expected: PASS.

- [ ] **Step 6: Record public API**

Append to `src/neutral/Themia.Pdf/PublicAPI.Unshipped.txt`:

```
Themia.Pdf.PdfPaperFormat
Themia.Pdf.PdfPaperFormat.A3 = 1 -> Themia.Pdf.PdfPaperFormat
Themia.Pdf.PdfPaperFormat.A4 = 0 -> Themia.Pdf.PdfPaperFormat
Themia.Pdf.PdfPaperFormat.Legal = 3 -> Themia.Pdf.PdfPaperFormat
Themia.Pdf.PdfPaperFormat.Letter = 2 -> Themia.Pdf.PdfPaperFormat
Themia.Pdf.PdfPaperFormat.Tabloid = 4 -> Themia.Pdf.PdfPaperFormat
Themia.Pdf.PdfRenderOptions
Themia.Pdf.PdfRenderOptions.PdfRenderOptions() -> void
Themia.Pdf.PdfRenderOptions.MarginBottom.get -> string!
Themia.Pdf.PdfRenderOptions.MarginBottom.set -> void
Themia.Pdf.PdfRenderOptions.MarginLeft.get -> string!
Themia.Pdf.PdfRenderOptions.MarginLeft.set -> void
Themia.Pdf.PdfRenderOptions.MarginRight.get -> string!
Themia.Pdf.PdfRenderOptions.MarginRight.set -> void
Themia.Pdf.PdfRenderOptions.MarginTop.get -> string!
Themia.Pdf.PdfRenderOptions.MarginTop.set -> void
Themia.Pdf.PdfRenderOptions.PaperFormat.get -> Themia.Pdf.PdfPaperFormat
Themia.Pdf.PdfRenderOptions.PaperFormat.set -> void
Themia.Pdf.PdfRenderOptions.PrintBackground.get -> bool
Themia.Pdf.PdfRenderOptions.PrintBackground.set -> void
```

Run `dotnet build src/neutral/Themia.Pdf/Themia.Pdf.csproj --no-incremental` and confirm no RS0016 for these types. (If the analyzer reports a different exact signature, copy its suggested text verbatim — the analyzer is the authority on format.)

- [ ] **Step 7: Commit**

```bash
git add src/neutral/Themia.Pdf tests/Themia.Pdf.Tests Themia.sln
git commit -m "feat: add PdfPaperFormat and PdfRenderOptions"
```

---

### Task 3: `ThemiaPdfOptions`

**Files:**
- Create: `src/neutral/Themia.Pdf/ThemiaPdfOptions.cs`
- Test: `tests/Themia.Pdf.Tests/ThemiaPdfOptionsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/Themia.Pdf.Tests/ThemiaPdfOptionsTests.cs`:

```csharp
using Themia.Pdf;
using Xunit;

namespace Themia.Pdf.Tests;

public sealed class ThemiaPdfOptionsTests
{
    [Fact]
    public void Defaults_AreFaithfulToEzy()
    {
        var o = new ThemiaPdfOptions();

        Assert.Null(o.ExecutablePath);
        Assert.False(o.DisableAutoDownload);
        Assert.True(o.Headless);
        Assert.Null(o.ConfigureHandlebars);
        Assert.Equal(
            new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" },
            o.LaunchArgs);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Pdf.Tests/Themia.Pdf.Tests.csproj --filter ThemiaPdfOptionsTests`
Expected: FAIL — `ThemiaPdfOptions` does not exist.

- [ ] **Step 3: Implement the options**

Create `src/neutral/Themia.Pdf/ThemiaPdfOptions.cs`:

```csharp
using HandlebarsDotNet;

namespace Themia.Pdf;

/// <summary>
/// Process-wide configuration for Themia PDF rendering, bound once at <c>AddThemiaPdf</c> time.
/// Controls how the headless Chromium browser is provisioned and lets consumers extend the
/// Handlebars template engine.
/// </summary>
public sealed class ThemiaPdfOptions
{
    /// <summary>
    /// Path to a Chrome/Chromium executable to launch. When set, this browser is used and no
    /// download occurs. When <see langword="null"/> (default), provisioning falls back to
    /// <see cref="DisableAutoDownload"/>.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// When <see langword="false"/> (default) and no <see cref="ExecutablePath"/> is set, Chromium is
    /// downloaded on first render. When <see langword="true"/>, auto-download is skipped and an
    /// <see cref="ExecutablePath"/> is required — otherwise the first render throws
    /// <see cref="System.InvalidOperationException"/>.
    /// </summary>
    public bool DisableAutoDownload { get; set; }

    /// <summary>Chromium launch arguments. Defaults to the sandbox/dev-shm flags ported from ezy-assets.</summary>
    public string[] LaunchArgs { get; set; } =
        ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"];

    /// <summary>Whether Chromium runs headless. Default <see langword="true"/>.</summary>
    public bool Headless { get; set; } = true;

    /// <summary>
    /// Optional hook to register custom helpers or partials on the Handlebars engine at construction.
    /// The built-in <c>emptyIfNull</c> helper is always registered first.
    /// </summary>
    public Action<IHandlebars>? ConfigureHandlebars { get; set; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Themia.Pdf.Tests/Themia.Pdf.Tests.csproj --filter ThemiaPdfOptionsTests`
Expected: PASS.

- [ ] **Step 5: Record public API**

Append to `src/neutral/Themia.Pdf/PublicAPI.Unshipped.txt`:

```
Themia.Pdf.ThemiaPdfOptions
Themia.Pdf.ThemiaPdfOptions.ThemiaPdfOptions() -> void
Themia.Pdf.ThemiaPdfOptions.ConfigureHandlebars.get -> System.Action<HandlebarsDotNet.IHandlebars!>?
Themia.Pdf.ThemiaPdfOptions.ConfigureHandlebars.set -> void
Themia.Pdf.ThemiaPdfOptions.DisableAutoDownload.get -> bool
Themia.Pdf.ThemiaPdfOptions.DisableAutoDownload.set -> void
Themia.Pdf.ThemiaPdfOptions.ExecutablePath.get -> string?
Themia.Pdf.ThemiaPdfOptions.ExecutablePath.set -> void
Themia.Pdf.ThemiaPdfOptions.Headless.get -> bool
Themia.Pdf.ThemiaPdfOptions.Headless.set -> void
Themia.Pdf.ThemiaPdfOptions.LaunchArgs.get -> string![]!
Themia.Pdf.ThemiaPdfOptions.LaunchArgs.set -> void
```

Run `dotnet build src/neutral/Themia.Pdf/Themia.Pdf.csproj --no-incremental`; confirm no RS0016 (copy analyzer-suggested signatures verbatim if they differ).

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Pdf tests/Themia.Pdf.Tests
git commit -m "feat: add ThemiaPdfOptions browser/engine configuration"
```

---

### Task 4: `IHtmlTemplateRenderer` + `HandlebarsHtmlTemplateRenderer`

**Files:**
- Create: `src/neutral/Themia.Pdf/IHtmlTemplateRenderer.cs`
- Create: `src/neutral/Themia.Pdf/HandlebarsHtmlTemplateRenderer.cs`
- Test: `tests/Themia.Pdf.Tests/HandlebarsHtmlTemplateRendererTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Themia.Pdf.Tests/HandlebarsHtmlTemplateRendererTests.cs`:

```csharp
using HandlebarsDotNet;
using Themia.Pdf;
using Xunit;

namespace Themia.Pdf.Tests;

public sealed class HandlebarsHtmlTemplateRendererTests
{
    [Fact]
    public void Render_MergesModelIntoTemplate()
    {
        var sut = new HandlebarsHtmlTemplateRenderer(new ThemiaPdfOptions());

        var html = sut.Render("<p>{{name}}</p>", new { name = "Ada" });

        Assert.Equal("<p>Ada</p>", html);
    }

    [Fact]
    public void Render_EmptyIfNullHelper_RendersEmptyForNull()
    {
        var sut = new HandlebarsHtmlTemplateRenderer(new ThemiaPdfOptions());

        var html = sut.Render("[{{emptyIfNull missing}}]", new { missing = (string?)null });

        Assert.Equal("[]", html);
    }

    [Fact]
    public void Render_ConfigureHandlebars_RegistersCustomHelper()
    {
        var options = new ThemiaPdfOptions
        {
            ConfigureHandlebars = hbs => hbs.RegisterHelper(
                "shout",
                (output, _, args) => output.WriteSafeString(args[0]?.ToString()?.ToUpperInvariant() ?? "")),
        };
        var sut = new HandlebarsHtmlTemplateRenderer(options);

        var html = sut.Render("{{shout name}}", new { name = "hi" });

        Assert.Equal("HI", html);
    }

    [Fact]
    public void Render_NullTemplate_Throws()
    {
        var sut = new HandlebarsHtmlTemplateRenderer(new ThemiaPdfOptions());

        Assert.Throws<ArgumentNullException>(() => sut.Render(null!, new { }));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Themia.Pdf.Tests/Themia.Pdf.Tests.csproj --filter HandlebarsHtmlTemplateRendererTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the interface and renderer**

Create `src/neutral/Themia.Pdf/IHtmlTemplateRenderer.cs`:

```csharp
namespace Themia.Pdf;

/// <summary>Merges an HTML template with a data model and returns the resulting HTML string.</summary>
public interface IHtmlTemplateRenderer
{
    /// <summary>
    /// Compiles <paramref name="template"/> as a Handlebars template and renders it against
    /// <paramref name="model"/>.
    /// </summary>
    /// <param name="template">The Handlebars HTML template body.</param>
    /// <param name="model">The data model the template is rendered against.</param>
    /// <returns>The rendered HTML.</returns>
    string Render(string template, object model);
}
```

Create `src/neutral/Themia.Pdf/HandlebarsHtmlTemplateRenderer.cs`:

```csharp
using HandlebarsDotNet;

namespace Themia.Pdf;

/// <summary>Handlebars.Net-backed <see cref="IHtmlTemplateRenderer"/>. Thread-safe for rendering.</summary>
internal sealed class HandlebarsHtmlTemplateRenderer : IHtmlTemplateRenderer
{
    private readonly IHandlebars _hbs;

    public HandlebarsHtmlTemplateRenderer(ThemiaPdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _hbs = Handlebars.Create();
        _hbs.RegisterHelper("emptyIfNull", (output, _, arguments) =>
            output.WriteSafeString(arguments.Length > 0 ? arguments[0]?.ToString() ?? string.Empty : string.Empty));

        options.ConfigureHandlebars?.Invoke(_hbs);
    }

    public string Render(string template, object model)
    {
        ArgumentNullException.ThrowIfNull(template);

        // ponytail: compiles per call (port-faithful with ezy). Add a bounded compiled-template
        // cache keyed by `template` if profiling shows compile cost matters.
        var compiled = _hbs.Compile(template);
        return compiled(model);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Themia.Pdf.Tests/Themia.Pdf.Tests.csproj --filter HandlebarsHtmlTemplateRendererTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Record public API**

Append to `src/neutral/Themia.Pdf/PublicAPI.Unshipped.txt`:

```
Themia.Pdf.IHtmlTemplateRenderer
Themia.Pdf.IHtmlTemplateRenderer.Render(string! template, object! model) -> string!
```

(The `internal` `HandlebarsHtmlTemplateRenderer` is not on the public surface.) Run
`dotnet build src/neutral/Themia.Pdf/Themia.Pdf.csproj --no-incremental`; confirm no RS0016.

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Pdf tests/Themia.Pdf.Tests
git commit -m "feat: add Handlebars HTML template renderer"
```

---

### Task 5: `IPdfRenderer` + `PuppeteerPdfRenderer`

**Files:**
- Create: `src/neutral/Themia.Pdf/IPdfRenderer.cs`
- Create: `src/neutral/Themia.Pdf/PuppeteerPdfRenderer.cs`
- Test (unit): `tests/Themia.Pdf.Tests/PuppeteerPdfRendererTests.cs`

- [ ] **Step 1: Write the failing unit test (provisioning precedence — no Chromium needed)**

Create `tests/Themia.Pdf.Tests/PuppeteerPdfRendererTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Pdf;
using Xunit;

namespace Themia.Pdf.Tests;

public sealed class PuppeteerPdfRendererTests
{
    [Fact]
    public async Task RenderHtmlAsync_AutoDownloadDisabledWithoutExecutablePath_Throws()
    {
        var options = new ThemiaPdfOptions { DisableAutoDownload = true, ExecutablePath = null };
        await using var sut = new PuppeteerPdfRenderer(options, NullLogger<PuppeteerPdfRenderer>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.RenderHtmlAsync("<p>hi</p>"));

        Assert.Contains("ExecutablePath", ex.Message);
    }

    [Fact]
    public async Task RenderHtmlAsync_NullHtml_Throws()
    {
        await using var sut = new PuppeteerPdfRenderer(new ThemiaPdfOptions(), NullLogger<PuppeteerPdfRenderer>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.RenderHtmlAsync(null!));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Pdf.Tests/Themia.Pdf.Tests.csproj --filter PuppeteerPdfRendererTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement the interface and renderer**

Create `src/neutral/Themia.Pdf/IPdfRenderer.cs`:

```csharp
namespace Themia.Pdf;

/// <summary>Prints HTML to a PDF using headless Chromium.</summary>
public interface IPdfRenderer
{
    /// <summary>Renders <paramref name="html"/> to PDF bytes.</summary>
    /// <param name="html">The HTML document to print.</param>
    /// <param name="options">Output options; <see langword="null"/> uses defaults.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The PDF as a byte array.</returns>
    Task<byte[]> RenderHtmlAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default);
}
```

Create `src/neutral/Themia.Pdf/PuppeteerPdfRenderer.cs`:

```csharp
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using PuppeteerSharp.Media;

namespace Themia.Pdf;

/// <summary>
/// PuppeteerSharp-backed <see cref="IPdfRenderer"/>. Lazily launches a single headless Chromium
/// browser (guarded by a semaphore), reuses it across renders, and disposes it on
/// <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class PuppeteerPdfRenderer : IPdfRenderer, IAsyncDisposable
{
    private readonly ThemiaPdfOptions _options;
    private readonly ILogger<PuppeteerPdfRenderer> _logger;
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private IBrowser? _browser;

    public PuppeteerPdfRenderer(ThemiaPdfOptions options, ILogger<PuppeteerPdfRenderer> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<byte[]> RenderHtmlAsync(string html, PdfRenderOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        var opts = options ?? new PdfRenderOptions();

        var browser = await EnsureBrowserAsync(ct).ConfigureAwait(false);

        await using var page = await browser.NewPageAsync().ConfigureAwait(false);
        await page.SetContentAsync(html).ConfigureAwait(false);
        return await page.PdfDataAsync(ToPdfOptions(opts)).ConfigureAwait(false);
    }

    private async Task<IBrowser> EnsureBrowserAsync(CancellationToken ct)
    {
        if (_browser is { IsConnected: true })
        {
            return _browser;
        }

        await _browserLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_browser is { IsConnected: true })
            {
                return _browser;
            }

            _browser = await LaunchAsync().ConfigureAwait(false);
            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private async Task<IBrowser> LaunchAsync()
    {
        var launchOptions = new LaunchOptions
        {
            Headless = _options.Headless,
            Args = _options.LaunchArgs,
        };

        if (!string.IsNullOrEmpty(_options.ExecutablePath))
        {
            launchOptions.ExecutablePath = _options.ExecutablePath;
        }
        else if (!_options.DisableAutoDownload)
        {
            _logger.LogInformation("Themia.Pdf: downloading/launching Chromium…");
            await new BrowserFetcher().DownloadAsync().ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException(
                "Themia.Pdf: no Chromium available. Set ThemiaPdfOptions.ExecutablePath, or leave " +
                "DisableAutoDownload=false to allow the runtime download.");
        }

        var browser = await Puppeteer.LaunchAsync(launchOptions).ConfigureAwait(false);
        _logger.LogInformation("Themia.Pdf: Chromium launched.");
        return browser;
    }

    private static PdfOptions ToPdfOptions(PdfRenderOptions o) => new()
    {
        Format = o.PaperFormat switch
        {
            PdfPaperFormat.A3 => PaperFormat.A3,
            PdfPaperFormat.Letter => PaperFormat.Letter,
            PdfPaperFormat.Legal => PaperFormat.Legal,
            PdfPaperFormat.Tabloid => PaperFormat.Tabloid,
            _ => PaperFormat.A4,
        },
        PrintBackground = o.PrintBackground,
        MarginOptions = new MarginOptions
        {
            Top = o.MarginTop,
            Bottom = o.MarginBottom,
            Left = o.MarginLeft,
            Right = o.MarginRight,
        },
    };

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync().ConfigureAwait(false);
            _browser.Dispose();
        }

        _browserLock.Dispose();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Themia.Pdf.Tests/Themia.Pdf.Tests.csproj --filter PuppeteerPdfRendererTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Record public API**

Append to `src/neutral/Themia.Pdf/PublicAPI.Unshipped.txt`:

```
Themia.Pdf.IPdfRenderer
Themia.Pdf.IPdfRenderer.RenderHtmlAsync(string! html, Themia.Pdf.PdfRenderOptions? options = null, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<byte[]!>!
```

Run `dotnet build src/neutral/Themia.Pdf/Themia.Pdf.csproj --no-incremental`; confirm no RS0016 (copy analyzer-suggested signature verbatim if it differs).

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Pdf tests/Themia.Pdf.Tests
git commit -m "feat: add PuppeteerSharp PDF renderer with managed browser lifecycle"
```

---

### Task 6: `AddThemiaPdf` DI extension

**Files:**
- Create: `src/neutral/Themia.Pdf/ThemiaPdfServiceCollectionExtensions.cs`
- Test: `tests/Themia.Pdf.Tests/ThemiaPdfServiceCollectionExtensionsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/Themia.Pdf.Tests/ThemiaPdfServiceCollectionExtensionsTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Themia.Pdf;
using Xunit;

namespace Themia.Pdf.Tests;

public sealed class ThemiaPdfServiceCollectionExtensionsTests
{
    private static ServiceProvider Build(Action<ThemiaPdfOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaPdf(configure);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddThemiaPdf_RegistersBothRenderers()
    {
        using var sp = Build();

        Assert.NotNull(sp.GetService<IHtmlTemplateRenderer>());
        Assert.NotNull(sp.GetService<IPdfRenderer>());
    }

    [Fact]
    public void AddThemiaPdf_RenderersAreSingletons()
    {
        using var sp = Build();

        Assert.Same(sp.GetService<IPdfRenderer>(), sp.GetService<IPdfRenderer>());
        Assert.Same(sp.GetService<IHtmlTemplateRenderer>(), sp.GetService<IHtmlTemplateRenderer>());
    }

    [Fact]
    public void AddThemiaPdf_AppliesConfigure()
    {
        using var sp = Build(o => o.ExecutablePath = "/usr/bin/chromium");

        var options = sp.GetRequiredService<ThemiaPdfOptions>();
        Assert.Equal("/usr/bin/chromium", options.ExecutablePath);
    }

    [Fact]
    public void AddThemiaPdf_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaPdf();
        services.AddThemiaPdf();
        using var sp = services.BuildServiceProvider();

        Assert.Single(sp.GetServices<IPdfRenderer>());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Themia.Pdf.Tests/Themia.Pdf.Tests.csproj --filter ThemiaPdfServiceCollectionExtensionsTests`
Expected: FAIL — `AddThemiaPdf` does not exist.

- [ ] **Step 3: Implement the extension**

Create `src/neutral/Themia.Pdf/ThemiaPdfServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Pdf;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>DI registration for the Themia PDF rendering core.</summary>
public static class ThemiaPdfServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IHtmlTemplateRenderer"/> and <see cref="IPdfRenderer"/> as singletons,
    /// along with a configured <see cref="ThemiaPdfOptions"/>. Idempotent.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of <see cref="ThemiaPdfOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddThemiaPdf(
        this IServiceCollection services,
        Action<ThemiaPdfOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ThemiaPdfOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<IHtmlTemplateRenderer, HandlebarsHtmlTemplateRenderer>();
        services.TryAddSingleton<IPdfRenderer, PuppeteerPdfRenderer>();

        return services;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Themia.Pdf.Tests/Themia.Pdf.Tests.csproj --filter ThemiaPdfServiceCollectionExtensionsTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Record public API**

Append to `src/neutral/Themia.Pdf/PublicAPI.Unshipped.txt`:

```
Microsoft.Extensions.DependencyInjection.ThemiaPdfServiceCollectionExtensions
static Microsoft.Extensions.DependencyInjection.ThemiaPdfServiceCollectionExtensions.AddThemiaPdf(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, System.Action<Themia.Pdf.ThemiaPdfOptions!>? configure = null) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

Run `dotnet build src/neutral/Themia.Pdf/Themia.Pdf.csproj --no-incremental`; confirm no RS0016.

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Pdf tests/Themia.Pdf.Tests
git commit -m "feat: add AddThemiaPdf DI extension"
```

---

### Task 7: Chromium integration test (gated)

**Files:**
- Create: `tests/Themia.Pdf.IntegrationTests/Themia.Pdf.IntegrationTests.csproj`
- Create: `tests/Themia.Pdf.IntegrationTests/PdfRenderingIntegrationTests.cs`
- Create: `tests/Themia.Pdf.IntegrationTests/README.md`

- [ ] **Step 1: Create the integration test project**

Create `tests/Themia.Pdf.IntegrationTests/Themia.Pdf.IntegrationTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <!-- NullLogger / logging abstractions via the shared framework, matching the unit test project. -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
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
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/neutral/Themia.Pdf/Themia.Pdf.csproj" />
  </ItemGroup>
</Project>
```

Then: `dotnet sln Themia.sln add tests/Themia.Pdf.IntegrationTests/Themia.Pdf.IntegrationTests.csproj`

- [ ] **Step 2: Write the integration test**

The `PdfRenderer` here uses the default options (auto-download Chromium on first use). The test asserts real PDF output and single-browser reuse.

Create `tests/Themia.Pdf.IntegrationTests/PdfRenderingIntegrationTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Pdf;
using Xunit;

namespace Themia.Pdf.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class PdfRenderingIntegrationTests
{
    [Fact]
    public async Task RenderHtmlAsync_ProducesPdfBytes()
    {
        await using var renderer = new PuppeteerPdfRenderer(new ThemiaPdfOptions(), NullLogger<PuppeteerPdfRenderer>.Instance);

        var bytes = await renderer.RenderHtmlAsync("<h1>Hello PDF</h1>");

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        // PDF magic header: "%PDF-"
        Assert.Equal(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, bytes[..5]);
    }

    [Fact]
    public async Task RenderHtmlAsync_ConcurrentRenders_ReuseSingleBrowser()
    {
        await using var renderer = new PuppeteerPdfRenderer(new ThemiaPdfOptions(), NullLogger<PuppeteerPdfRenderer>.Instance);

        var results = await Task.WhenAll(
            renderer.RenderHtmlAsync("<p>1</p>"),
            renderer.RenderHtmlAsync("<p>2</p>"),
            renderer.RenderHtmlAsync("<p>3</p>"));

        Assert.All(results, b => Assert.True(b.Length > 0));
    }

    [Fact]
    public async Task EndToEnd_TemplateThenPdf()
    {
        var template = new HandlebarsHtmlTemplateRenderer(new ThemiaPdfOptions());
        await using var pdf = new PuppeteerPdfRenderer(new ThemiaPdfOptions(), NullLogger<PuppeteerPdfRenderer>.Instance);

        var html = template.Render("<h1>{{title}}</h1>", new { title = "Report" });
        var bytes = await pdf.RenderHtmlAsync(html);

        Assert.True(bytes.Length > 0);
    }
}
```

Note: this project references internals (`PuppeteerPdfRenderer`, `HandlebarsHtmlTemplateRenderer`). Add `InternalsVisibleTo("Themia.Pdf.IntegrationTests")` to `src/neutral/Themia.Pdf/AssemblyInfo.cs`:

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Themia.Pdf.Tests")]
[assembly: InternalsVisibleTo("Themia.Pdf.IntegrationTests")]
```

- [ ] **Step 3: Document the Chromium requirement**

Create `tests/Themia.Pdf.IntegrationTests/README.md`:

```markdown
# Themia.Pdf.IntegrationTests

These tests launch headless Chromium via PuppeteerSharp. On first run they download Chromium
(~150 MB) to the PuppeteerSharp cache directory; subsequent runs reuse it. They require:

- Network access on first run (or a pre-provisioned Chromium / a `ThemiaPdfOptions.ExecutablePath`).
- A Linux runner needs the usual Chromium shared libraries.

Run only this suite: `dotnet test tests/Themia.Pdf.IntegrationTests`.
Filter by trait: `dotnet test --filter Category=Integration`.
```

- [ ] **Step 4: Run the integration tests**

Run: `dotnet test tests/Themia.Pdf.IntegrationTests/Themia.Pdf.IntegrationTests.csproj`
Expected: PASS (3 tests). First run downloads Chromium; allow extra time.

- [ ] **Step 5: Commit**

```bash
git add tests/Themia.Pdf.IntegrationTests src/neutral/Themia.Pdf/AssemblyInfo.cs Themia.sln
git commit -m "test: add Themia.Pdf Chromium integration tests"
```

---

### Task 8: Version bump, changelog, full build/test

**Files:**
- Modify: `Directory.Build.props` (line ~26)
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<Version>0.5.10</Version>` to `<Version>0.6.0</Version>`.

- [ ] **Step 2: Add the changelog entry**

In `CHANGELOG.md`, add a new section at the top (matching the existing format, with today's date 2026-06-21):

```markdown
## [0.6.0] - 2026-06-21

### Added
- `Themia.Pdf` — neutral HTML→PDF rendering core. `IHtmlTemplateRenderer` (Handlebars.Net template
  merge) and `IPdfRenderer` (PuppeteerSharp headless-Chromium HTML→PDF) with a managed browser
  lifecycle, configurable Chromium provisioning (`ExecutablePath` / `DisableAutoDownload`), and an
  `AddThemiaPdf` DI extension. Targets `net8.0;net10.0`. First Phase-2 package. (ported from
  ezy-assets `ContractPdfService`)
```

- [ ] **Step 3: Full solution build (clean) and test**

Run:
```bash
cd /Users/sarawut/GitHub/Idevs/single-repo/Packages/themia
dotnet build Themia.sln --no-incremental
dotnet test tests/Themia.Pdf.Tests/Themia.Pdf.Tests.csproj
```
Expected: solution builds with no warnings/errors (TreatWarningsAsErrors); all `Themia.Pdf.Tests` pass. (Integration tests run separately per Task 7.)

- [ ] **Step 4: Commit**

```bash
git add Directory.Build.props CHANGELOG.md
git commit -m "chore: release 0.6.0 — Themia.Pdf neutral rendering core"
```

---

## Post-implementation (controller, not a task step)

- Open the PR for `feature/themia-pdf-core`; run the standard review passes (`/code-review`,
  `/pr-review-toolkit:review-pr`, `/agy-review`); address findings.
- Log the coord request (ezy → `Themia.Pdf`) and move it accepted → in_progress → released on merge.
- Then start sub-project #2: `Themia.Modules.Pdf` tenant/global template store (separate
  brainstorm → spec → plan cycle).
