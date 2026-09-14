# Themia.Content Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `Themia.Content` — versioned, bilingual content pages as a neutral core, three engine packages and an optional ASP.NET Core package — in `0.26.0`, for ezy-assets by 2026-11-01 and for propertiezy's later adoption.

**Architecture:** Every rule (a save writes a revision, a stale version is refused, revert is a save of an old body, markdown is checked) lives once in `ContentPageService` in the core. Each engine package supplies only a connection, SQL text and duplicate-key detection through `IContentPageDialect`, following `Themia.Challenges`. The create guard is a unique index behind a savepoint; the update guard is `current_version = @ExpectedVersion` inside the `UPDATE`; every transaction runs at READ COMMITTED. Markdown rules run over Markdig's syntax tree; nothing is rendered on the server.

**Tech Stack:** .NET 8 + .NET 10, Dapper 2.1.79, FluentMigrator 8.0.1, Markdig 1.3.2, Npgsql 10.0.3, MySqlConnector 2.6.0, Microsoft.Data.SqlClient 6.1.5, xUnit 2.9.3, Testcontainers 4.13.0, ASP.NET Core minimal APIs + TestServer.

**Spec:** `docs/superpowers/specs/2026-09-14-themia-content-design.md` — read it before Task 1. Section numbers (§5, §7, …) below refer to it.

## Global Constraints

Every task's requirements include this section.

**Packages and layering**
- Five packaged projects, all `net8.0;net10.0`: `Themia.Content`, `Themia.Content.PostgreSql`, `Themia.Content.MySql`, `Themia.Content.SqlServer`, `Themia.Content.AspNetCore`. Source under `src/neutral/`.
- `Themia.Content` references Dapper, FluentMigrator, Markdig and `Microsoft.Extensions.*` abstractions only. **No database driver, no `Microsoft.AspNetCore.App`, no `Themia.Framework.*`, no EF Core.**
- Engine packages reference exactly one driver each. `Themia.Content.AspNetCore` references `Microsoft.AspNetCore.App` and the core only.
- Every packaged project tracks `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` (both start with the line `#nullable enable`), has XML docs on every public member, and builds with `TreatWarningsAsErrors=true` (set repo-wide).
- `System.Text.Json` only. `ILogger<T>` only. **Never log markdown bodies.**

**Schema and data rules (spec §4, §5)**
- Tables `content_pages` and `content_page_revisions`, literal names, identical on every engine, **never `InSchema(...)`**.
- Unique indexes `ux_content_pages_slug_language (slug, language)` and `ux_content_page_revisions_page_version (page_id, version)`.
- **Every transaction is opened with `IsolationLevel.ReadCommitted`.**
- `ExpectedVersion` is a required `int` (0 = create). `Language` is never defaulted on any write record.
- **Do not gate on `DbTransaction.SupportsSavepoints`** — it reports `false` on MySqlConnector and SqlClient, which support savepoints.
- Every timestamp comes from `TimeProvider.GetUtcNow()`, never from the database clock.
- SQL selects alias every column to its PascalCase property (`id AS Id`), following `PostgresAuditDialect`. **Never set `DefaultTypeMap.MatchNamesWithUnderscores`** — it is process-global and would change an adopter's own Dapper mapping.

**Markdown rules (spec §7, §8)**
- Allowed URL schemes are exactly `http`, `https`, `mailto`, `tel`, or no scheme. Fixed, not configurable.
- Markdig is used to parse only (`MarkdownPipelineBuilder().Build()`, no extensions).

**Tests**
- Every integration test class carries `[Trait("Category", "Integration")]`. PR CI filters `Category!=Integration` (`.github/workflows/ci.yml:144`); an untagged container test would start Docker in every PR shard.
- Integration tests use one container per engine per assembly through `[CollectionDefinition]` + `ICollectionFixture<T>`, following `tests/Themia.Challenges.IntegrationTests/ChallengeEngineFixtures.cs`.
- Tests within one engine's collection run sequentially, so a test may measure a before/after delta on shared tables. Tests must still use their own slugs: `$"p-{Guid.NewGuid():N}"`.
- **Every guard in spec §12's acceptance table must be seen failing.** Where a task says "falsify": copy the file first (`cp <file> /tmp/<name>.orig`), remove the guard, run the named test and record that it is red, then restore with `cp /tmp/<name>.orig <file>` and prove the restore with `diff`. **Never restore by retyping** — a restore typed from memory once lost a line that nothing noticed.
- A full `dotnet test Themia.sln --filter "Category!=Integration"` run is part of every task's acceptance, not only the new project's tests.

**Git (maintainer's standing rules — not optional)**
- Work on branch `feat/themia-content`, created from `main`.
- **No commit and no push to this repository Monday–Friday 09:00–18:00 local time.** Run `date "+%a %H:%M"` immediately before every `git commit` and `git push`. Inside the window, leave the work uncommitted and report it.
- Commit messages: `<type>: <subject>`, imperative, ≤ 72 characters. **Never add `Co-Authored-By`, `Claude-Session` or "Generated with" lines.** A `commit-msg` hook rejects them.

**Commands**
- Build: `dotnet build Themia.sln` from the repository root.
- Unit and ASP.NET Core tests: `dotnet test tests/<Project>/<Project>.csproj`.
- Integration tests need Docker: `dotnet test tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj --filter "FullyQualifiedName~<Class>"`.

---

## Build order and the 2026-10-15 checkpoint

Agreed with both consumers on coord #0130 [8]; recorded in spec §14.

| Block | Tasks | Delivers |
|---|---|---|
| 1 | 1–8 | Core + PostgreSQL (the engine that proves the core) |
| 2 | 9–11 | MySQL and SQL Server engines, dialect contract tests |
| 3 | 12–13 | `Themia.Content.AspNetCore` |
| 4 | 14–15 | Golden fixture; docs and registration for blocks 1–4 |
| — | Checkpoint | Go/no-go on coord #0130 by **2026-10-15** |
| 5 | 16–18 | Importer and adoption test (item 4) — separable |
| — | 19 | Release `0.26.0` |

Block 5 touches no file that blocks 1–4 depend on, except appending to `ContentServiceCollectionExtensions`, the core `PublicAPI.Unshipped.txt`, the README and the changelog. `IContentPageDialect.ImportPageSql` is added in Task 4 so the dialect interface does not change if block 5 ships a release later.

**How to answer the checkpoint.** On or before 2026-10-15, post on #0130:
- **GO** if Tasks 1–15 are merged and green, and Tasks 16–18 are merged or will be before 2026-10-25.
- **GO WITHOUT ITEM 4** if Tasks 1–15 are merged and green and Tasks 16–18 are not on track: release Tasks 1–15 as `0.26.0`, and Tasks 16–18 follow in `0.26.1`.
- **NO-GO** if Tasks 1–15 are not merged and green.

---

## File structure

```
src/neutral/Themia.Content/
  Themia.Content.csproj
  PublicAPI.Shipped.txt · PublicAPI.Unshipped.txt · README.md
  ContentOptions.cs                    configured languages + fallback; Validate()
  ContentPage.cs                       ContentPage, ContentPageSummary, ContentPageRevision records
  PagedResult.cs
  ContentPageSave.cs                   ContentPageSave, ContentPageRevert records
  ContentSaveResult.cs                 ContentSaveOutcome + ContentSaveResult + ContentValidationError
  ContentMarkdownRules.cs              ContentMarkdownViolation(+Kind) + the Markdig rule
  IContentPageService.cs
  IContentPageDialect.cs
  Migrations/ContentSchemaMigration.cs
  DependencyInjection/ContentServiceCollectionExtensions.cs
  Internal/ContentLanguage.cs          normalisation
  Internal/ContentPageValidator.cs     field rules
  Internal/ContentRows.cs              Dapper row classes
  Internal/ContentPageService.cs
  -- block 5 --
  IContentPageImporter.cs              import records, outcome, rules, result
  Internal/ContentPageImporter.cs

src/neutral/Themia.Content.PostgreSql/  PostgresContentDialect.cs · ServiceCollectionExtensions.cs
src/neutral/Themia.Content.MySql/       MySqlContentDialect.cs · ServiceCollectionExtensions.cs
src/neutral/Themia.Content.SqlServer/   SqlServerContentDialect.cs · ServiceCollectionExtensions.cs
src/neutral/Themia.Content.AspNetCore/  ContentAdminOptions.cs · ContentEndpoints.cs · ContentHttpResults.cs · ContentRequests.cs

tests/Themia.Content.Tests/             (net8.0;net10.0, no database)
  ContentOptionsTests.cs · ContentPageValidatorTests.cs · ContentMarkdownRulesTests.cs
  ContentServiceRegistrationTests.cs · CountingContentDialect.cs · ContentDialectContractTests.cs
  MarkdownDialectFixtureTests.cs · Fixtures/markdown-dialect.json
  -- block 5 --  ContentImporterValidationTests.cs

tests/Themia.Content.IntegrationTests/  (net10.0, Docker)
  EngineRegistration.cs · ContentEngineFixtures.cs · ContentTestData.cs · GatedContentDialect.cs
  ContentSchemaTests.cs · ContentSaveTests.cs · ContentReadTests.cs · ContentRevertTests.cs
  ContentConcurrencyTests.cs · ContentSeedingTests.cs · ContentSchemaProbeTests.cs
  -- block 5 --  ContentImportTests.cs · PropertiezyAdoptionTests.cs

tests/Themia.Content.AspNetCore.Tests/  (net10.0)
  FakeContentPageService.cs · ContentTestServer.cs · ContentPublicEndpointTests.cs · ContentAdminEndpointTests.cs
```

---
## Block 1 — core and PostgreSQL

### Task 1: Scaffold the core package and its options

**Files:**
- Modify: `Directory.Packages.props` (add Markdig after the `Dapper` line)
- Create: `src/neutral/Themia.Content/Themia.Content.csproj`
- Create: `src/neutral/Themia.Content/PublicAPI.Shipped.txt`, `src/neutral/Themia.Content/PublicAPI.Unshipped.txt`
- Create: `src/neutral/Themia.Content/ContentOptions.cs`
- Create: `src/neutral/Themia.Content/Internal/ContentLanguage.cs`
- Create: `tests/Themia.Content.Tests/Themia.Content.Tests.csproj`
- Test: `tests/Themia.Content.Tests/ContentOptionsTests.cs`
- Modify: `Themia.sln`

**Interfaces:**
- Produces: `public sealed class ContentOptions { IList<string> Languages { get; } string FallbackLanguage { get; set; } }` with `internal void Validate()`, `internal bool IsConfigured(string normalisedLanguage)`, `internal string NormalisedFallback { get; }`.
- Produces: `internal static class ContentLanguage { const int MaxLength = 35; static string Normalise(string? language); }` in namespace `Themia.Content.Internal`.

- [ ] **Step 1: Create the branch**

```bash
git switch main && git pull --ff-only && git switch -c feat/themia-content
```

- [ ] **Step 2: Pin Markdig centrally**

In `Directory.Packages.props`, directly after `<PackageVersion Include="Dapper" Version="2.1.79" />`, add:

```xml
    <!-- Content (0.26.0): CommonMark parser for Themia.Content's write-side markdown rules. Parse only, never
         render. BSD-2-Clause. No Dependabot manifest edit is needed: Manifest.csproj references @(PackageVersion). -->
    <PackageVersion Include="Markdig" Version="1.3.2" />
```

- [ ] **Step 3: Create the core project**

`src/neutral/Themia.Content/Themia.Content.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Neutral cross-framework package: MUST include net8.0 (cross-framework reuse). -->
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.Content</PackageId>
    <Description>Themia content pages — versioned, bilingual pages keyed by slug and language, with revision history, stale-version refusal, and raw HTML and unsafe link destinations refused at write. No database driver, ASP.NET Core or framework dependency.</Description>
    <PackageTags>themia;content;cms;markdown;pages</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Dapper" />
    <PackageReference Include="FluentMigrator" />
    <PackageReference Include="Markdig" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
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
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Content.Tests" />
    <InternalsVisibleTo Include="Themia.Content.IntegrationTests" />
  </ItemGroup>
</Project>
```

Both `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` contain exactly one line: `#nullable enable`.

- [ ] **Step 4: Create the unit-test project**

`tests/Themia.Content.Tests/Themia.Content.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
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
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/neutral/Themia.Content/Themia.Content.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Add both projects to the solution and check the folders**

```bash
grep -c '"2150E333-8FDC-42A3-9474-1A3956D46DE8"' Themia.sln > /tmp/sln-folders-before
dotnet sln Themia.sln add src/neutral/Themia.Content/Themia.Content.csproj tests/Themia.Content.Tests/Themia.Content.Tests.csproj
grep -c '"2150E333-8FDC-42A3-9474-1A3956D46DE8"' Themia.sln
cat /tmp/sln-folders-before
```

Expected: the two counts are equal — the projects were nested under the existing `src`/`neutral`/`tests` solution folders, not under duplicates. If the second count is larger, remove the duplicate folder entries `dotnet sln add` created and nest the projects under the existing ones, as `Themia.Challenges` is.

- [ ] **Step 6: Write the failing tests**

`tests/Themia.Content.Tests/ContentOptionsTests.cs`:

```csharp
using Xunit;

namespace Themia.Content.Tests;

public class ContentOptionsTests
{
    private static ContentOptions Options(string fallback, params string[] languages)
    {
        var options = new ContentOptions { FallbackLanguage = fallback };
        foreach (var language in languages)
        {
            options.Languages.Add(language);
        }

        return options;
    }

    [Fact]
    public void Validate_ShouldAccept_WhenFallbackIsOneOfTheLanguages() =>
        Options("th", "th", "en").Validate();

    [Fact]
    public void Validate_ShouldCompareNormalisedValues_WhenCaseAndWhitespaceDiffer() =>
        Options(" TH ", "th", "EN").Validate();

    [Fact]
    public void Validate_ShouldThrow_WhenNoLanguageIsConfigured()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Options("th").Validate());
        Assert.Contains("at least one language", error.Message);
    }

    [Fact]
    public void Validate_ShouldThrow_WhenALanguageIsBlank() =>
        Assert.Throws<InvalidOperationException>(() => Options("th", "th", "  ").Validate());

    [Fact]
    public void Validate_ShouldThrow_WhenALanguageAppearsTwiceAfterNormalising()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Options("th", "th", " TH ").Validate());
        Assert.Contains("twice", error.Message);
    }

    [Fact]
    public void Validate_ShouldThrow_WhenALanguageIsLongerThanTheColumn() =>
        Assert.Throws<InvalidOperationException>(() => Options("th", "th", new string('a', 36)).Validate());

    [Fact]
    public void Validate_ShouldThrow_WhenFallbackIsNotOneOfTheLanguages()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Options("fr", "th", "en").Validate());
        Assert.Contains("FallbackLanguage", error.Message);
    }

    [Fact]
    public void IsConfigured_ShouldMatchNormalisedLanguages()
    {
        var options = Options("th", "TH", "en");
        Assert.True(options.IsConfigured("th"));
        Assert.False(options.IsConfigured("fr"));
        Assert.Equal("th", options.NormalisedFallback);
    }
}
```

- [ ] **Step 7: Run the tests to see them fail**

Run: `dotnet test tests/Themia.Content.Tests/Themia.Content.Tests.csproj`
Expected: build fails — `ContentOptions` does not exist.

- [ ] **Step 8: Implement**

`src/neutral/Themia.Content/Internal/ContentLanguage.cs`:

```csharp
namespace Themia.Content.Internal;

/// <summary>The one place a language value is normalised, so storage, lookup and configuration agree.</summary>
internal static class ContentLanguage
{
    /// <summary>The width of the <c>language</c> column: the practical ceiling for a BCP 47 tag.</summary>
    public const int MaxLength = 35;

    /// <summary>Trims and lower-cases <paramref name="language"/>; <see langword="null"/> becomes empty.</summary>
    public static string Normalise(string? language) => (language ?? string.Empty).Trim().ToLowerInvariant();
}
```

`src/neutral/Themia.Content/ContentOptions.cs`:

```csharp
using Themia.Content.Internal;

namespace Themia.Content;

/// <summary>
/// The languages content pages may be written in, and the language a reader falls back to when the requested
/// one has no published page.
/// </summary>
public sealed class ContentOptions
{
    /// <summary>Every language a page may be saved in, for example <c>th</c> and <c>en</c>. Values are compared
    /// and stored trimmed and lower-cased.</summary>
    public IList<string> Languages { get; } = new List<string>();

    /// <summary>The language served when the requested one has no published page. Must be one of
    /// <see cref="Languages"/>.</summary>
    public string FallbackLanguage { get; set; } = string.Empty;

    /// <summary>The fallback language, normalised.</summary>
    internal string NormalisedFallback => ContentLanguage.Normalise(FallbackLanguage);

    /// <summary>Whether <paramref name="normalisedLanguage"/> is one of the configured languages.</summary>
    internal bool IsConfigured(string normalisedLanguage) =>
        Languages.Any(language => ContentLanguage.Normalise(language) == normalisedLanguage);

    /// <summary>Throws when the configuration cannot serve a page.</summary>
    /// <exception cref="InvalidOperationException">No language; a blank, over-long or repeated language; or a
    /// fallback that is not one of the languages.</exception>
    internal void Validate()
    {
        var normalised = Languages.Select(ContentLanguage.Normalise).ToList();

        if (normalised.Count == 0)
        {
            throw new InvalidOperationException("ContentOptions.Languages must contain at least one language.");
        }

        if (normalised.Any(language => language.Length == 0))
        {
            throw new InvalidOperationException("ContentOptions.Languages must not contain a blank language.");
        }

        if (normalised.Any(language => language.Length > ContentLanguage.MaxLength))
        {
            throw new InvalidOperationException(
                $"ContentOptions.Languages entries must be at most {ContentLanguage.MaxLength} characters.");
        }

        if (normalised.Distinct(StringComparer.Ordinal).Count() != normalised.Count)
        {
            throw new InvalidOperationException("ContentOptions.Languages must not contain the same language twice.");
        }

        if (!normalised.Contains(NormalisedFallback, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"ContentOptions.FallbackLanguage '{FallbackLanguage}' must be one of ContentOptions.Languages.");
        }
    }
}
```

- [ ] **Step 9: Record the public API**

Run: `dotnet build src/neutral/Themia.Content/Themia.Content.csproj`
Expected: RS0016 errors naming each new public member. Apply the analyzer's fix:

```bash
dotnet format analyzers src/neutral/Themia.Content/Themia.Content.csproj --diagnostics RS0016 --severity info
```

If `dotnet format` leaves any RS0016, append the symbol from each remaining error message to `PublicAPI.Unshipped.txt` verbatim. The file must then read:

```
#nullable enable
Themia.Content.ContentOptions
Themia.Content.ContentOptions.ContentOptions() -> void
Themia.Content.ContentOptions.FallbackLanguage.get -> string!
Themia.Content.ContentOptions.FallbackLanguage.set -> void
Themia.Content.ContentOptions.Languages.get -> System.Collections.Generic.IList<string!>!
```

- [ ] **Step 10: Run the tests to see them pass**

Run: `dotnet test tests/Themia.Content.Tests/Themia.Content.Tests.csproj`
Expected: 8 passed on net8.0 and 8 on net10.0.

- [ ] **Step 11: Commit** (check `date "+%a %H:%M"` first — see Global Constraints)

```bash
git add Directory.Packages.props Themia.sln src/neutral/Themia.Content tests/Themia.Content.Tests
git commit -m "feat(content): scaffold Themia.Content with language options"
```

---

### Task 2: Markdown rules over Markdig's syntax tree

**Files:**
- Create: `src/neutral/Themia.Content/ContentMarkdownRules.cs`
- Modify: `src/neutral/Themia.Content/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Content.Tests/ContentMarkdownRulesTests.cs`

**Interfaces:**
- Produces: `public enum ContentMarkdownViolationKind { RawHtml, DisallowedUrl }`
- Produces: `public sealed record ContentMarkdownViolation(ContentMarkdownViolationKind Kind, string Detail)`
- Produces: `public static class ContentMarkdownRules { static IReadOnlyList<ContentMarkdownViolation> Check(string markdown); static bool IsUrlAllowed(string? url); }`

Spec §7 is the requirement; §7's table is why this is a parser and not regular expressions. The render half (a web app's `marked` configuration) is the safety boundary; this rule exists so an author is told, instead of having markup silently dropped.

- [ ] **Step 1: Write the failing tests**

`tests/Themia.Content.Tests/ContentMarkdownRulesTests.cs`:

```csharp
using Xunit;

namespace Themia.Content.Tests;

public class ContentMarkdownRulesTests
{
    [Theory]
    [InlineData("## Cookies {#cookies}")]
    [InlineData("## คุกกี้ {#cookies}")]
    [InlineData("[x](https://a.example/p?q=1#f)")]
    [InlineData("[x](/privacy#cookies)")]
    [InlineData("[x](#cookies)")]
    [InlineData("[x](/a:b)")]
    [InlineData("<mailto:privacy@a.example>")]
    [InlineData("<a@b.com>")]
    [InlineData("[call](tel:+6621234567)")]
    [InlineData("```\n<b>x</b>\n```")]
    [InlineData("```\n<b>x</b>")]
    [InlineData("x ``a`<b>`` y")]
    [InlineData("use `<br>` here")]
    [InlineData("para\n\n    <b>x</b>\n")]
    [InlineData("> ```\n> <b>x</b>\n> ```")]
    public void Check_ShouldReturnNoViolation_WhenMarkdownIsSafe(string markdown) =>
        Assert.Empty(ContentMarkdownRules.Check(markdown));

    [Theory]
    [InlineData("a <script>x</script> b")]
    [InlineData("a <!-- hidden --> b")]
    [InlineData("<div>\nhello\n</div>")]
    [InlineData("`\n\n<b>x</b>\n\n`")]
    [InlineData("[<b>x</b>](https://a.example)")]
    public void Check_ShouldReportRawHtml_WhenMarkdownContainsHtmlOutsideCode(string markdown)
    {
        var violations = ContentMarkdownRules.Check(markdown);
        Assert.Contains(violations, v => v.Kind == ContentMarkdownViolationKind.RawHtml);
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](JAVASCRIPT:alert(1))")]
    [InlineData("[x](javascript&#58;alert(1))")]
    [InlineData("[x](javascript&colon;alert(1))")]
    [InlineData("[x](<javascript:alert(1)>)")]
    [InlineData("[x][r]\n\n[r]: javascript:alert(1)")]
    [InlineData("[x](vbscript:msgbox(1))")]
    [InlineData("![x](data:text/html;base64,PHNjcmlwdD4=)")]
    [InlineData("```\ncode\n````\n\n[x](javascript:alert(1))")]
    [InlineData("[x](java&#9;script:alert(1))")]
    [InlineData("[x](&#x6A&#x61vascript:alert(1))")]
    public void Check_ShouldReportDisallowedUrl_WhenADestinationHasAnUnsafeScheme(string markdown)
    {
        var violations = ContentMarkdownRules.Check(markdown);
        Assert.Contains(violations, v => v.Kind == ContentMarkdownViolationKind.DisallowedUrl);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("/a:b", true)]
    [InlineData("#cookies", true)]
    [InlineData("?q=1", true)]
    [InlineData("HTTPS://a.example", true)]
    [InlineData("mailto:privacy@a.example", true)]
    [InlineData("tel:+6621234567", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(" javascript:alert(1)", false)]
    [InlineData("java&#9;script:alert(1)", false)]
    [InlineData("&#x6A&#x61vascript:alert(1)", false)]
    [InlineData("javascript&colon;alert(1)", false)]
    [InlineData("vbscript:msgbox(1)", false)]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=", false)]
    [InlineData("java&#99999999;script:alert(1)", false)]
    public void IsUrlAllowed_ShouldAllowOnlyTheFourSchemesOrNone(string? url, bool expected) =>
        Assert.Equal(expected, ContentMarkdownRules.IsUrlAllowed(url));

    [Fact]
    public void Check_ShouldThrow_WhenMarkdownIsNull() =>
        Assert.Throws<ArgumentNullException>(() => ContentMarkdownRules.Check(null!));
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test tests/Themia.Content.Tests/Themia.Content.Tests.csproj --filter ContentMarkdownRulesTests`
Expected: build fails — `ContentMarkdownRules` does not exist.

- [ ] **Step 3: Implement**

`src/neutral/Themia.Content/ContentMarkdownRules.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Themia.Content;

/// <summary>The two ways markdown can be refused at write.</summary>
public enum ContentMarkdownViolationKind
{
    /// <summary>An HTML block or inline HTML outside code: a tag, a comment, a declaration or a processing
    /// instruction.</summary>
    RawHtml,

    /// <summary>A link, image, autolink or reference-definition destination whose scheme is not <c>http</c>,
    /// <c>https</c>, <c>mailto</c> or <c>tel</c>.</summary>
    DisallowedUrl,
}

/// <summary>One reason markdown was refused.</summary>
/// <param name="Kind">What was refused.</param>
/// <param name="Detail">The offending tag or destination, for the author's error message.</param>
public sealed record ContentMarkdownViolation(ContentMarkdownViolationKind Kind, string Detail);

/// <summary>
/// The write half of Themia.Content's markdown safety: markdown is parsed with Markdig and refused when its
/// syntax tree contains raw HTML or a destination with an unsafe scheme.
/// </summary>
/// <remarks>
/// <para><b>Refused, not sanitised.</b> A sanitiser over markdown corrupts it; refusing tells the author what is
/// wrong and leaves their text as written. It is an allow-list by construction — there is no list of dangerous
/// tags or schemes to keep current.</para>
/// <para><b>A parser, not regular expressions.</b> Removing code with regular expressions disagreed with
/// CommonMark in both directions: it rejected an indented code block and accepted a <c>javascript:</c> link after
/// a fence closed by a longer fence. Code blocks and code spans are not HTML nodes in Markdig's tree, so HTML
/// inside code is accepted and renders escaped.</para>
/// <para><b>This is not the safety boundary.</b> The web renderer drops raw HTML and refuses the same schemes
/// again at render, because this rule cannot fix rows saved before it existed.</para>
/// </remarks>
public static class ContentMarkdownRules
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto", "tel" };

    private static readonly Regex Scheme =
        new("^([a-z][a-z0-9+.-]*):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex HexEntity =
        new("&#x([0-9a-f]+);?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex DecimalEntity =
        new("&#([0-9]+);?", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>Every violation in <paramref name="markdown"/>; empty when it may be saved.</summary>
    /// <param name="markdown">The markdown source as the author wrote it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="markdown"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<ContentMarkdownViolation> Check(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var document = Markdown.Parse(markdown, Pipeline);
        var violations = new List<ContentMarkdownViolation>();

        foreach (var block in document.Descendants<HtmlBlock>())
        {
            violations.Add(new ContentMarkdownViolation(ContentMarkdownViolationKind.RawHtml, block.Lines.ToString()));
        }

        foreach (var inline in document.Descendants<HtmlInline>())
        {
            violations.Add(new ContentMarkdownViolation(ContentMarkdownViolationKind.RawHtml, inline.Tag));
        }

        foreach (var link in document.Descendants<LinkInline>())
        {
            AddIfDisallowed(link.Url, violations);
        }

        foreach (var autolink in document.Descendants<AutolinkInline>())
        {
            AddIfDisallowed(autolink.Url, violations);
        }

        foreach (var definition in document.Descendants<LinkReferenceDefinition>())
        {
            AddIfDisallowed(definition.Url, violations);
        }

        return violations;
    }

    /// <summary>
    /// Whether a destination may be linked: it has no scheme (a relative path, <c>/path</c>, <c>#fragment</c> or
    /// <c>?query</c>), or its scheme is <c>http</c>, <c>https</c>, <c>mailto</c> or <c>tel</c>.
    /// </summary>
    /// <remarks>
    /// Normalised the way a browser reads an attribute before deciding: HTML entities are decoded (with or
    /// without the terminating semicolon) and ASCII whitespace and control characters are removed, so
    /// <c>java&amp;#9;script:</c> is recognised as <c>javascript:</c>. Markdig has usually decoded entities already;
    /// decoding again can only make the check stricter. This mirrors <c>urlAllowed</c> in the renderer
    /// configuration pinned by the golden fixture.
    /// </remarks>
    /// <param name="url">The destination; <see langword="null"/> or empty is allowed.</param>
    public static bool IsUrlAllowed(string? url)
    {
        var normalised = RemoveWhitespaceAndControl(DecodeEntities(url ?? string.Empty));
        var match = Scheme.Match(normalised);
        return !match.Success || AllowedSchemes.Contains(match.Groups[1].Value);
    }

    private static void AddIfDisallowed(string? url, List<ContentMarkdownViolation> violations)
    {
        if (!IsUrlAllowed(url))
        {
            violations.Add(new ContentMarkdownViolation(ContentMarkdownViolationKind.DisallowedUrl, url ?? string.Empty));
        }
    }

    private static string DecodeEntities(string value)
    {
        var decoded = HexEntity.Replace(value, m => FromCodePoint(m.Groups[1].Value, NumberStyles.HexNumber));
        decoded = DecimalEntity.Replace(decoded, m => FromCodePoint(m.Groups[1].Value, NumberStyles.Integer));
        return decoded
            .Replace("&colon;", ":", StringComparison.OrdinalIgnoreCase)
            .Replace("&tab;", "\t", StringComparison.OrdinalIgnoreCase)
            .Replace("&newline;", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
    }

    // An out-of-range or surrogate code point decodes to nothing. That can only join the characters on either
    // side, which makes a disguised scheme easier to recognise, never harder.
    private static string FromCodePoint(string digits, NumberStyles style) =>
        int.TryParse(digits, style, CultureInfo.InvariantCulture, out var codePoint)
            && codePoint is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
                ? char.ConvertFromUtf32(codePoint)
                : string.Empty;

    private static string RemoveWhitespaceAndControl(string value) =>
        string.Concat(value.Where(c => c > ' ' && c != (char)127));
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Themia.Content.Tests/Themia.Content.Tests.csproj --filter ContentMarkdownRulesTests`
Expected: all pass on both frameworks. If an `InlineData` case fails, **do not change the expected verdict**: the verdicts come from spec §7–§8 and were checked against `marked` 18.0.11. Stop and report the input, Markdig's node types for it, and the verdict — a Markdig/`marked` disagreement is a spec decision.

- [ ] **Step 5: Record the public API** — build, apply `dotnet format analyzers … --diagnostics RS0016` as in Task 1 Step 9, and confirm the build is clean.

- [ ] **Step 6: Falsify the whitespace strip** (spec §12 acceptance)

```bash
cp src/neutral/Themia.Content/ContentMarkdownRules.cs /tmp/ContentMarkdownRules.cs.orig
```

Replace `var normalised = RemoveWhitespaceAndControl(DecodeEntities(url ?? string.Empty));` with `var normalised = DecodeEntities(url ?? string.Empty);`.

Run: `dotnet test tests/Themia.Content.Tests/Themia.Content.Tests.csproj --filter ContentMarkdownRulesTests`
Expected: FAIL — including `IsUrlAllowed` for `java&#9;script:alert(1)` and ` javascript:alert(1)`. Record the failing test names in the task report.

```bash
cp /tmp/ContentMarkdownRules.cs.orig src/neutral/Themia.Content/ContentMarkdownRules.cs
diff /tmp/ContentMarkdownRules.cs.orig src/neutral/Themia.Content/ContentMarkdownRules.cs && echo restored
dotnet test tests/Themia.Content.Tests/Themia.Content.Tests.csproj --filter ContentMarkdownRulesTests
```

Expected: `restored`, then all pass.

- [ ] **Step 7: Commit** (check the time first)

```bash
git add src/neutral/Themia.Content tests/Themia.Content.Tests
git commit -m "feat(content): refuse raw HTML and unsafe schemes over Markdig"
```

---

### Task 3: Page records, save results and field validation

**Files:**
- Create: `src/neutral/Themia.Content/ContentPage.cs`
- Create: `src/neutral/Themia.Content/PagedResult.cs`
- Create: `src/neutral/Themia.Content/ContentPageSave.cs`
- Create: `src/neutral/Themia.Content/ContentSaveResult.cs`
- Create: `src/neutral/Themia.Content/Internal/ContentPageValidator.cs`
- Modify: `src/neutral/Themia.Content/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Content.Tests/ContentPageValidatorTests.cs`

**Interfaces:**
- Consumes: `ContentOptions`, `ContentLanguage`, `ContentMarkdownRules` (Tasks 1–2).
- Produces:
  - `public sealed record ContentPage(string Slug, string Language, string Title, string Markdown, int CurrentVersion, bool IsPublished, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? UpdatedBy)`
  - `public sealed record ContentPageSummary(string Slug, string Language, string Title, int CurrentVersion, bool IsPublished, DateTimeOffset UpdatedAt, string? UpdatedBy)`
  - `public sealed record ContentPageRevision(int Version, string Title, string Markdown, string? ChangeSummary, DateTimeOffset CreatedAt, string? CreatedBy)`
  - `public sealed class PagedResult<T> { IReadOnlyList<T> Items { get; init; } int Total { get; init; } }`
  - `public sealed record ContentPageSave(string Slug, string Language, string Title, string Markdown, bool IsPublished, int ExpectedVersion, string? ChangeSummary, string? EditorId)`
  - `public sealed record ContentPageRevert(string Slug, string Language, int TargetVersion, int ExpectedVersion, string? ChangeSummary, string? EditorId)`
  - `public enum ContentSaveOutcome { Saved, Conflict, Invalid, NotFound }`
  - `public sealed record ContentValidationError(string Field, string Message)`
  - `public sealed class ContentSaveResult` — properties `Outcome`, `Page`, `CurrentVersion`, `CurrentUpdatedBy`, `CurrentUpdatedAt`, `Errors`, `Succeeded`; factories `Saved(ContentPage)`, `Conflict(int, string?, DateTimeOffset)`, `Invalid(IReadOnlyList<ContentValidationError>)`, `NotFound()`.
  - `internal static class ContentPageValidator { const int MaxSlugLength = 100, MaxTitleLength = 200, MaxChangeSummaryLength = 500, MaxEditorIdLength = 256; static IReadOnlyList<ContentValidationError> Validate(ContentPageSave, ContentOptions); static IReadOnlyList<ContentValidationError> Validate(ContentPageRevert, ContentOptions); static bool IsValidSlug(string?); }`
- Field names in `ContentValidationError.Field` are the JSON names: `slug`, `language`, `title`, `markdown`, `expectedVersion`, `targetVersion`, `changeSummary`, `editorId`.

- [ ] **Step 1: Write the failing tests**

`tests/Themia.Content.Tests/ContentPageValidatorTests.cs`:

```csharp
using Themia.Content.Internal;
using Xunit;

namespace Themia.Content.Tests;

public class ContentPageValidatorTests
{
    private static readonly ContentOptions Options = CreateOptions();

    private static ContentOptions CreateOptions()
    {
        var options = new ContentOptions { FallbackLanguage = "th" };
        options.Languages.Add("th");
        options.Languages.Add("en");
        return options;
    }

    private static ContentPageSave ValidSave() =>
        new("privacy-policy", "en", "Privacy", "# Privacy", IsPublished: true, ExpectedVersion: 0, ChangeSummary: null, EditorId: "user-1");

    private static string[] FieldsOf(IReadOnlyList<ContentValidationError> errors) =>
        errors.Select(e => e.Field).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Validate_ShouldReturnNoError_WhenSaveIsValid() =>
        Assert.Empty(ContentPageValidator.Validate(ValidSave(), Options));

    [Theory]
    [InlineData("")]
    [InlineData("Privacy")]
    [InlineData("privacy_policy")]
    [InlineData("-privacy")]
    [InlineData("privacy--policy")]
    public void Validate_ShouldReportSlug_WhenSlugIsNotLowercaseWithSingleHyphens(string slug) =>
        Assert.Equal(["slug"], FieldsOf(ContentPageValidator.Validate(ValidSave() with { Slug = slug }, Options)));

    [Fact]
    public void Validate_ShouldReportSlug_WhenSlugIsLongerThan100() =>
        Assert.Equal(["slug"], FieldsOf(ContentPageValidator.Validate(ValidSave() with { Slug = new string('a', 101) }, Options)));

    [Fact]
    public void Validate_ShouldAcceptLanguage_WhenItNormalisesToAConfiguredOne() =>
        Assert.Empty(ContentPageValidator.Validate(ValidSave() with { Language = " EN " }, Options));

    [Theory]
    [InlineData("fr")]
    [InlineData("")]
    public void Validate_ShouldReportLanguage_WhenItIsNotConfigured(string language) =>
        Assert.Equal(["language"], FieldsOf(ContentPageValidator.Validate(ValidSave() with { Language = language }, Options)));

    [Fact]
    public void Validate_ShouldReportTitle_WhenBlankOrTooLong()
    {
        Assert.Equal(["title"], FieldsOf(ContentPageValidator.Validate(ValidSave() with { Title = " " }, Options)));
        Assert.Equal(["title"], FieldsOf(ContentPageValidator.Validate(ValidSave() with { Title = new string('t', 201) }, Options)));
    }

    [Fact]
    public void Validate_ShouldReportMarkdown_WhenBlank() =>
        Assert.Equal(["markdown"], FieldsOf(ContentPageValidator.Validate(ValidSave() with { Markdown = "" }, Options)));

    [Fact]
    public void Validate_ShouldReportEachMarkdownViolation_OnTheMarkdownField()
    {
        var errors = ContentPageValidator.Validate(
            ValidSave() with { Markdown = "a <b>x</b> [y](javascript:alert(1))" }, Options);

        Assert.Equal(["markdown"], FieldsOf(errors));
        Assert.Contains(errors, e => e.Message.Contains("raw HTML", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("javascript:alert(1)", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ShouldReportExpectedVersion_WhenNegative() =>
        Assert.Equal(["expectedVersion"], FieldsOf(ContentPageValidator.Validate(ValidSave() with { ExpectedVersion = -1 }, Options)));

    [Fact]
    public void Validate_ShouldReportChangeSummaryAndEditorId_WhenTooLong() =>
        Assert.Equal(
            ["changeSummary", "editorId"],
            FieldsOf(ContentPageValidator.Validate(
                ValidSave() with { ChangeSummary = new string('c', 501), EditorId = new string('e', 257) }, Options)));

    [Fact]
    public void Validate_ShouldTreatNullStringsAsMissing_NotThrow()
    {
        var errors = ContentPageValidator.Validate(new ContentPageSave(null!, null!, null!, null!, true, 0, null, null), Options);
        Assert.Equal(["language", "markdown", "slug", "title"], FieldsOf(errors));
    }

    [Fact]
    public void ValidateRevert_ShouldRequireATargetAndAnExistingVersion()
    {
        var errors = ContentPageValidator.Validate(new ContentPageRevert("terms", "th", 0, 0, null, null), Options);
        Assert.Equal(["expectedVersion", "targetVersion"], FieldsOf(errors));
    }

    [Fact]
    public void ValidateRevert_ShouldReturnNoError_WhenValid() =>
        Assert.Empty(ContentPageValidator.Validate(new ContentPageRevert("terms", "th", 1, 2, "undo", "user-1"), Options));

    [Fact]
    public void Result_ShouldSucceedOnlyWhenSaved()
    {
        var page = new ContentPage("terms", "th", "T", "# T", 1, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);
        Assert.True(ContentSaveResult.Saved(page).Succeeded);
        Assert.False(ContentSaveResult.Conflict(2, "b", DateTimeOffset.UnixEpoch).Succeeded);
        Assert.False(ContentSaveResult.Invalid([new ContentValidationError("slug", "bad")]).Succeeded);
        Assert.False(ContentSaveResult.NotFound().Succeeded);
        Assert.Equal(2, ContentSaveResult.Conflict(2, "b", DateTimeOffset.UnixEpoch).CurrentVersion);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test tests/Themia.Content.Tests/Themia.Content.Tests.csproj --filter "ContentPageValidatorTests"`
Expected: build fails — the records do not exist.

- [ ] **Step 3: Implement the records**

`src/neutral/Themia.Content/ContentPage.cs`:

```csharp
namespace Themia.Content;

/// <summary>A content page in one language, as it currently stands.</summary>
/// <param name="Slug">The URL key, lowercase letters and digits separated by single hyphens.</param>
/// <param name="Language">The normalised language.</param>
/// <param name="Title">The page title.</param>
/// <param name="Markdown">The page body.</param>
/// <param name="CurrentVersion">The version of the latest revision; the value an editor sends back as its expected version.</param>
/// <param name="IsPublished">Whether readers are served this page.</param>
/// <param name="CreatedAt">When the page was first saved.</param>
/// <param name="UpdatedAt">When the page was last saved.</param>
/// <param name="UpdatedBy">Who last saved it, as supplied by the consumer.</param>
public sealed record ContentPage(
    string Slug, string Language, string Title, string Markdown, int CurrentVersion, bool IsPublished,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? UpdatedBy);

/// <summary>A content page without its body, for lists.</summary>
/// <param name="Slug">The URL key.</param>
/// <param name="Language">The normalised language.</param>
/// <param name="Title">The page title.</param>
/// <param name="CurrentVersion">The version of the latest revision.</param>
/// <param name="IsPublished">Whether readers are served this page.</param>
/// <param name="UpdatedAt">When the page was last saved.</param>
/// <param name="UpdatedBy">Who last saved it.</param>
public sealed record ContentPageSummary(
    string Slug, string Language, string Title, int CurrentVersion, bool IsPublished, DateTimeOffset UpdatedAt, string? UpdatedBy);

/// <summary>One immutable revision of a page.</summary>
/// <param name="Version">The version this revision created.</param>
/// <param name="Title">The title at that version.</param>
/// <param name="Markdown">The body at that version.</param>
/// <param name="ChangeSummary">The author's note, if any.</param>
/// <param name="CreatedAt">When the revision was written.</param>
/// <param name="CreatedBy">Who wrote it.</param>
public sealed record ContentPageRevision(
    int Version, string Title, string Markdown, string? ChangeSummary, DateTimeOffset CreatedAt, string? CreatedBy);
```

`src/neutral/Themia.Content/PagedResult.cs`:

```csharp
namespace Themia.Content;

/// <summary>One page of a list, and how many items the whole list holds.</summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>The items on this page.</summary>
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    /// <summary>The number of items across every page.</summary>
    public int Total { get; init; }
}
```

`src/neutral/Themia.Content/ContentPageSave.cs`:

```csharp
namespace Themia.Content;

/// <summary>A request to create or update one page in one language.</summary>
/// <remarks>
/// <para><b><see cref="ExpectedVersion"/> is required and never nullable.</b> A page that does not exist is
/// version 0, so one number covers create and update, and a caller that omits it compares 0 with the real version
/// and is refused instead of overwriting newer text.</para>
/// <para><b><see cref="Language"/> has no default.</b> A defaulted language let propertiezy's revert write the
/// English body over the Thai row (coord #0130 [3]).</para>
/// </remarks>
/// <param name="Slug">The URL key.</param>
/// <param name="Language">The language; normalised before it is stored.</param>
/// <param name="Title">The page title.</param>
/// <param name="Markdown">The page body; checked by <see cref="ContentMarkdownRules"/>.</param>
/// <param name="IsPublished">Whether readers are served the page.</param>
/// <param name="ExpectedVersion">0 to create; otherwise the version the editor loaded.</param>
/// <param name="ChangeSummary">An optional note stored on the revision.</param>
/// <param name="EditorId">Who is saving, as the consumer identifies users.</param>
public sealed record ContentPageSave(
    string Slug, string Language, string Title, string Markdown, bool IsPublished,
    int ExpectedVersion, string? ChangeSummary, string? EditorId);

/// <summary>A request to save an old revision's title and body as a new version.</summary>
/// <param name="Slug">The URL key.</param>
/// <param name="Language">The language of the page being reverted.</param>
/// <param name="TargetVersion">The revision whose title and body become the new version.</param>
/// <param name="ExpectedVersion">The version the editor loaded.</param>
/// <param name="ChangeSummary">An optional note; defaults to "Reverted to version N".</param>
/// <param name="EditorId">Who is reverting.</param>
public sealed record ContentPageRevert(
    string Slug, string Language, int TargetVersion, int ExpectedVersion, string? ChangeSummary, string? EditorId);
```

`src/neutral/Themia.Content/ContentSaveResult.cs`:

```csharp
namespace Themia.Content;

/// <summary>How a save or revert ended.</summary>
public enum ContentSaveOutcome
{
    /// <summary>A new version was written.</summary>
    Saved,

    /// <summary>The page is not at the expected version, or a page created at version 0 already exists. Nothing
    /// was written.</summary>
    Conflict,

    /// <summary>A field failed validation. Nothing was written and no connection was opened.</summary>
    Invalid,

    /// <summary>The page, or the revision a revert targets, does not exist. Nothing was written.</summary>
    NotFound,
}

/// <summary>One field that failed validation.</summary>
/// <param name="Field">The JSON field name.</param>
/// <param name="Message">A message for the author.</param>
public sealed record ContentValidationError(string Field, string Message);

/// <summary>The result of <see cref="IContentPageService.SaveAsync"/> or <see cref="IContentPageService.RevertAsync"/>.</summary>
public sealed class ContentSaveResult
{
    private ContentSaveResult(
        ContentSaveOutcome outcome, ContentPage? page, int? currentVersion, string? currentUpdatedBy,
        DateTimeOffset? currentUpdatedAt, IReadOnlyList<ContentValidationError> errors)
    {
        Outcome = outcome;
        Page = page;
        CurrentVersion = currentVersion;
        CurrentUpdatedBy = currentUpdatedBy;
        CurrentUpdatedAt = currentUpdatedAt;
        Errors = errors;
    }

    /// <summary>How the operation ended.</summary>
    public ContentSaveOutcome Outcome { get; }

    /// <summary>The page as saved, when <see cref="Outcome"/> is <see cref="ContentSaveOutcome.Saved"/>.</summary>
    public ContentPage? Page { get; }

    /// <summary>The page's version as it now stands, when <see cref="Outcome"/> is <see cref="ContentSaveOutcome.Conflict"/>.</summary>
    public int? CurrentVersion { get; }

    /// <summary>Who last saved the page, on a conflict — lets a client that retried after a lost response
    /// recognise its own write.</summary>
    public string? CurrentUpdatedBy { get; }

    /// <summary>When the page was last saved, on a conflict.</summary>
    public DateTimeOffset? CurrentUpdatedAt { get; }

    /// <summary>The validation errors, when <see cref="Outcome"/> is <see cref="ContentSaveOutcome.Invalid"/>.</summary>
    public IReadOnlyList<ContentValidationError> Errors { get; }

    /// <summary>Whether a new version was written.</summary>
    public bool Succeeded => Outcome == ContentSaveOutcome.Saved;

    /// <summary>A new version was written.</summary>
    /// <param name="page">The page as saved.</param>
    public static ContentSaveResult Saved(ContentPage page) =>
        new(ContentSaveOutcome.Saved, page, null, null, null, Array.Empty<ContentValidationError>());

    /// <summary>The page has moved on from the expected version.</summary>
    /// <param name="currentVersion">The page's current version.</param>
    /// <param name="updatedBy">Who last saved it.</param>
    /// <param name="updatedAt">When it was last saved.</param>
    public static ContentSaveResult Conflict(int currentVersion, string? updatedBy, DateTimeOffset updatedAt) =>
        new(ContentSaveOutcome.Conflict, null, currentVersion, updatedBy, updatedAt, Array.Empty<ContentValidationError>());

    /// <summary>Validation failed.</summary>
    /// <param name="errors">Every failing field.</param>
    public static ContentSaveResult Invalid(IReadOnlyList<ContentValidationError> errors) =>
        new(ContentSaveOutcome.Invalid, null, null, null, null, errors);

    /// <summary>The page or target revision does not exist.</summary>
    public static ContentSaveResult NotFound() =>
        new(ContentSaveOutcome.NotFound, null, null, null, null, Array.Empty<ContentValidationError>());
}
```

- [ ] **Step 4: Implement the validator**

`src/neutral/Themia.Content/Internal/ContentPageValidator.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Themia.Content.Internal;

/// <summary>Field rules for saves and reverts. Runs before any connection is opened.</summary>
internal static class ContentPageValidator
{
    public const int MaxSlugLength = 100;
    public const int MaxTitleLength = 200;
    public const int MaxChangeSummaryLength = 500;
    public const int MaxEditorIdLength = 256;

    private static readonly Regex SlugPattern =
        new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsValidSlug(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug.Length <= MaxSlugLength && SlugPattern.IsMatch(slug);

    public static IReadOnlyList<ContentValidationError> Validate(ContentPageSave save, ContentOptions options)
    {
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<ContentValidationError>();
        AddKeyErrors(save.Slug, save.Language, options, errors);
        AddTitleErrors(save.Title, errors);

        if (string.IsNullOrWhiteSpace(save.Markdown))
        {
            errors.Add(new ContentValidationError("markdown", "Markdown is required."));
        }
        else
        {
            errors.AddRange(ContentMarkdownRules.Check(save.Markdown).Select(Describe));
        }

        if (save.ExpectedVersion < 0)
        {
            errors.Add(new ContentValidationError(
                "expectedVersion", "Expected version must be 0 for a new page, or the version the editor loaded."));
        }

        AddOptionalErrors(save.ChangeSummary, save.EditorId, errors);
        return errors;
    }

    public static IReadOnlyList<ContentValidationError> Validate(ContentPageRevert revert, ContentOptions options)
    {
        ArgumentNullException.ThrowIfNull(revert);
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<ContentValidationError>();
        AddKeyErrors(revert.Slug, revert.Language, options, errors);

        if (revert.TargetVersion < 1)
        {
            errors.Add(new ContentValidationError("targetVersion", "Target version must be 1 or greater."));
        }

        if (revert.ExpectedVersion < 1)
        {
            errors.Add(new ContentValidationError(
                "expectedVersion", "Expected version must be the version the editor loaded; only an existing page can be reverted."));
        }

        AddOptionalErrors(revert.ChangeSummary, revert.EditorId, errors);
        return errors;
    }

    private static void AddKeyErrors(string? slug, string? language, ContentOptions options, List<ContentValidationError> errors)
    {
        if (!IsValidSlug(slug))
        {
            errors.Add(new ContentValidationError(
                "slug", $"Slug must be lowercase letters and digits separated by single hyphens, at most {MaxSlugLength} characters."));
        }

        if (!options.IsConfigured(ContentLanguage.Normalise(language)))
        {
            errors.Add(new ContentValidationError(
                "language", $"Language must be one of: {string.Join(", ", options.Languages.Select(ContentLanguage.Normalise))}."));
        }
    }

    private static void AddTitleErrors(string? title, List<ContentValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            errors.Add(new ContentValidationError("title", "Title is required."));
        }
        else if (title.Length > MaxTitleLength)
        {
            errors.Add(new ContentValidationError("title", $"Title must be at most {MaxTitleLength} characters."));
        }
    }

    private static void AddOptionalErrors(string? changeSummary, string? editorId, List<ContentValidationError> errors)
    {
        if (changeSummary?.Length > MaxChangeSummaryLength)
        {
            errors.Add(new ContentValidationError("changeSummary", $"Change summary must be at most {MaxChangeSummaryLength} characters."));
        }

        if (editorId?.Length > MaxEditorIdLength)
        {
            errors.Add(new ContentValidationError("editorId", $"Editor id must be at most {MaxEditorIdLength} characters."));
        }
    }

    private static ContentValidationError Describe(ContentMarkdownViolation violation) => violation.Kind switch
    {
        ContentMarkdownViolationKind.RawHtml => new ContentValidationError(
            "markdown",
            "Markdown must not contain raw HTML. Use markdown syntax, or put the markup in a code block to show it as an example."),
        ContentMarkdownViolationKind.DisallowedUrl => new ContentValidationError(
            "markdown",
            $"Links may use only http, https, mailto or tel, or no scheme. Refused: {violation.Detail}"),
#pragma warning disable CS8524 // Unnamed enum values: a new named kind must still break this switch at compile time.
    };
#pragma warning restore CS8524
}
```

- [ ] **Step 5: Run the tests, record the public API, run the whole unit project**

```bash
dotnet test tests/Themia.Content.Tests/Themia.Content.Tests.csproj --filter ContentPageValidatorTests
dotnet format analyzers src/neutral/Themia.Content/Themia.Content.csproj --diagnostics RS0016 --severity info
dotnet build src/neutral/Themia.Content/Themia.Content.csproj
dotnet test tests/Themia.Content.Tests/Themia.Content.Tests.csproj
```

Expected: validator tests pass; the build is clean; every test in the project passes on both frameworks.

- [ ] **Step 6: Commit** (check the time first)

```bash
git add src/neutral/Themia.Content tests/Themia.Content.Tests
git commit -m "feat(content): add page records, save results and field rules"
```

---

### Task 4: Schema, dialect contract and the PostgreSQL engine

**Files:**
- Create: `src/neutral/Themia.Content/IContentPageDialect.cs`
- Create: `src/neutral/Themia.Content/Migrations/ContentSchemaMigration.cs`
- Create: `src/neutral/Themia.Content.PostgreSql/Themia.Content.PostgreSql.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- Create: `src/neutral/Themia.Content.PostgreSql/PostgresContentDialect.cs`
- Create: `src/neutral/Themia.Content.PostgreSql/ServiceCollectionExtensions.cs`
- Create: `tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj`
- Create: `tests/Themia.Content.IntegrationTests/EngineRegistration.cs`
- Create: `tests/Themia.Content.IntegrationTests/ContentEngineFixtures.cs`
- Test: `tests/Themia.Content.IntegrationTests/ContentSchemaTests.cs`
- Test: `tests/Themia.Content.IntegrationTests/ContentSchemaProbeTests.cs`
- Modify: `Themia.sln`, `src/neutral/Themia.Content/PublicAPI.Unshipped.txt`

**Interfaces:**
- Produces `public interface IContentPageDialect` (namespace `Themia.Content`) with exactly these members. Parameter names are the contract.

| member | binds | returns |
|---|---|---|
| `DbConnection CreateConnection()` | | an unopened connection |
| `bool IsDuplicateKey(DbException exception)` | | true for a unique-constraint violation |
| `string InsertPageSql` | `@Slug @Language @Title @Markdown @IsPublished @Now @EditorId` | writes version 1; `created_at = updated_at = @Now` |
| `string ImportPageSql` | `@Slug @Language @Title @Markdown @CurrentVersion @IsPublished @CreatedAt @UpdatedAt @UpdatedBy` | for block 5 |
| `string UpdatePageIfVersionSql` | `@Slug @Language @Title @Markdown @IsPublished @ExpectedVersion @Now @EditorId` | affected rows: 1 or 0 |
| `string InsertRevisionSql` | `@PageId @Version @Title @Markdown @ChangeSummary @CreatedAt @CreatedBy` | |
| `string SelectPageSql` | `@Slug @Language` | `ContentPageRow` columns, any publish state |
| `string SelectPublishedSql` | `@Slug @Language @Fallback` | at most one `ContentPageRow` |
| `string SelectRevisionSql` | `@Slug @Language @Version` | `ContentRevisionRow` columns |
| `string ListPagesSql` | `@Offset @Limit` | `ContentPageSummaryRow` columns, ordered by slug then language |
| `string CountPagesSql` | | count |
| `string ListRevisionsSql` | `@Slug @Language @Offset @Limit` | `ContentRevisionRow` columns, newest version first |
| `string CountRevisionsSql` | `@Slug @Language` | count |

- Column aliases (every select): page rows `Id, Slug, Language, Title, Markdown, CurrentVersion, IsPublished, CreatedAt, UpdatedAt, UpdatedBy`; summary rows `Slug, Language, Title, CurrentVersion, IsPublished, UpdatedAt, UpdatedBy`; revision rows `Version, Title, Markdown, ChangeSummary, CreatedAt, CreatedBy`.
- Produces `public sealed class ContentSchemaMigration` (namespace `Themia.Content.Migrations`), `public static IServiceCollection AddThemiaContentPostgres(this IServiceCollection services, string connectionString)` (namespace `Themia.Content.PostgreSql`), `public sealed class PostgresContentDialect`.
- Produces, for later test tasks: `public abstract class ContentEngineFixture : IAsyncLifetime` with `ConnectionString`, `Dialect`, `Time` (`FakeTimeProvider`), `abstract IMigrationEngineAdapter MigrationAdapter`, `abstract Task<bool> TableExistsAsync(string name)`, `abstract Task<bool> IndexExistsAsync(string name)`; `PostgresContentFixture`; `[CollectionDefinition] PostgresContentCollection` with `const string Name = "Postgres Content"`.

- [ ] **Step 1: Write the dialect contract**

`src/neutral/Themia.Content/IContentPageDialect.cs`:

```csharp
using System.Data.Common;

namespace Themia.Content;

/// <summary>
/// Per-database strategy for the content store: a connection, duplicate-key detection, and every SQL statement
/// <see cref="IContentPageService"/> runs against the tables created by <see cref="Migrations.ContentSchemaMigration"/>.
/// Implemented once per engine package; this is those packages' only contact with the core.
/// </summary>
/// <remarks>
/// <para>Every statement is executed by Dapper with named parameters. The parameter names documented on each member
/// are the contract an implementation must bind. Every <c>SELECT</c> aliases each column to its PascalCase name
/// (<c>current_version AS CurrentVersion</c>); nothing relies on Dapper's global underscore matching, which would
/// change the adopter's own mappings.</para>
/// <para><b>Rules live in the service, not here.</b> A dialect never decides whether a save is allowed. In particular
/// <see cref="UpdatePageIfVersionSql"/> must compare against the editor's <c>@ExpectedVersion</c>, never against a
/// value the service read — a <c>WHERE</c> that compares the row with itself refuses nothing (coord #0130 [3]).</para>
/// </remarks>
public interface IContentPageDialect
{
    /// <summary>Creates an unopened connection.</summary>
    DbConnection CreateConnection();

    /// <summary>Whether <paramref name="exception"/> is a unique-constraint violation on this engine.</summary>
    /// <param name="exception">The exception a statement threw.</param>
    bool IsDuplicateKey(DbException exception);

    /// <summary>Inserts a page at version 1. Binds <c>@Slug @Language @Title @Markdown @IsPublished @Now @EditorId</c>;
    /// sets <c>created_at</c> and <c>updated_at</c> to <c>@Now</c>.</summary>
    string InsertPageSql { get; }

    /// <summary>Inserts a page with its history's version and timestamps, for the importer. Binds
    /// <c>@Slug @Language @Title @Markdown @CurrentVersion @IsPublished @CreatedAt @UpdatedAt @UpdatedBy</c>.</summary>
    string ImportPageSql { get; }

    /// <summary>Updates a page only if it is at <c>@ExpectedVersion</c>, setting <c>current_version</c> to
    /// <c>@ExpectedVersion + 1</c>. Binds <c>@Slug @Language @Title @Markdown @IsPublished @ExpectedVersion @Now @EditorId</c>.
    /// The affected row count is 1 when saved and 0 when the page is missing or has moved on.</summary>
    string UpdatePageIfVersionSql { get; }

    /// <summary>Inserts one revision. Binds <c>@PageId @Version @Title @Markdown @ChangeSummary @CreatedAt @CreatedBy</c>.</summary>
    string InsertRevisionSql { get; }

    /// <summary>Selects one page in any publish state. Binds <c>@Slug @Language</c>.</summary>
    string SelectPageSql { get; }

    /// <summary>Selects the published page in <c>@Language</c>, else in <c>@Fallback</c>, as one row at most, preferring
    /// <c>@Language</c>. Binds <c>@Slug @Language @Fallback</c>.</summary>
    string SelectPublishedSql { get; }

    /// <summary>Selects one revision of a page. Binds <c>@Slug @Language @Version</c>.</summary>
    string SelectRevisionSql { get; }

    /// <summary>Selects one page of page summaries ordered by slug then language. Binds <c>@Offset @Limit</c>.</summary>
    string ListPagesSql { get; }

    /// <summary>Counts every page.</summary>
    string CountPagesSql { get; }

    /// <summary>Selects one page of a page's revisions, newest version first. Binds <c>@Slug @Language @Offset @Limit</c>.</summary>
    string ListRevisionsSql { get; }

    /// <summary>Counts a page's revisions. Binds <c>@Slug @Language</c>.</summary>
    string CountRevisionsSql { get; }
}
```

- [ ] **Step 2: Write the migration**

`src/neutral/Themia.Content/Migrations/ContentSchemaMigration.cs`:

```csharp
using FluentMigrator;
using FluentMigrator.Builders.Create.Table;

namespace Themia.Content.Migrations;

/// <summary>Creates <c>content_pages</c> and <c>content_page_revisions</c> on PostgreSQL, MySQL and SQL Server.</summary>
/// <remarks>
/// <para><b>Literal table names on every engine, never <c>InSchema(...)</c></b> — FluentMigrator drops the schema on
/// MySQL, so a qualified name means something different per engine (see <c>ChallengeSchemaMigration</c>).</para>
/// <para><b>No tenant column.</b> Content is platform-level for both consumers (coord #0130).</para>
/// <para><b>Two unique indexes are guards, not optimisations.</b> <c>ux_content_pages_slug_language</c> is the only
/// thing that refuses a second create of the same page; <c>ux_content_page_revisions_page_version</c> refuses a second
/// revision with the same version from any writer that is not the service.</para>
/// </remarks>
[Migration(202609140001, "Themia.Content: create content_pages and content_page_revisions")]
public sealed class ContentSchemaMigration : Migration
{
    internal const string PagesTable = "content_pages";
    internal const string RevisionsTable = "content_page_revisions";

    private delegate ICreateTableColumnOptionOrWithColumnSyntax DateTimeType(ICreateTableColumnAsTypeSyntax column);

    /// <inheritdoc />
    public override void Up()
    {
        // Replay-safe (coord #0078): each Themia migration assembly has its own ledger and replays once on an
        // existing database. Every object is created in one block, so the anchor table's presence is the whole
        // migration's presence.
        if (Schema.Table(PagesTable).Exists())
        {
            return;
        }

        // MySQL's FluentMigrator generator has no DateTimeOffset, so MySQL stores DATETIME(6) in UTC.
        IfDatabase("postgresql").Delegate(() => CreateTables(c => c.AsDateTimeOffset()));
        IfDatabase("mysql").Delegate(() => CreateTables(c => c.AsCustom("DATETIME(6)")));
        IfDatabase("sqlserver").Delegate(() => CreateTables(c => c.AsDateTimeOffset()));

        IfDatabase(p =>
                !p.StartsWith("Postgres", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("MySql", StringComparison.OrdinalIgnoreCase) &&
                !p.StartsWith("SqlServer", StringComparison.OrdinalIgnoreCase))
            .Delegate(() => throw new NotSupportedException(
                "Themia.Content supports only PostgreSQL, MySQL, and SQL Server. The active database provider is not " +
                "supported; add a migration branch for it."));
    }

    /// <inheritdoc />
    public override void Down()
    {
        Delete.Table(RevisionsTable);
        Delete.Table(PagesTable);
    }

    private void CreateTables(DateTimeType dt)
    {
        var pages = Create.Table(PagesTable)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("slug").AsString(100).NotNullable()
            .WithColumn("language").AsString(35).NotNullable()
            .WithColumn("title").AsString(200).NotNullable()
            .WithColumn("markdown").AsString(int.MaxValue).NotNullable()
            .WithColumn("current_version").AsInt32().NotNullable()
            .WithColumn("is_published").AsBoolean().NotNullable();
        dt(pages.WithColumn("created_at")).NotNullable();
        dt(pages.WithColumn("updated_at")).NotNullable();
        pages.WithColumn("updated_by").AsString(256).Nullable();

        var revisions = Create.Table(RevisionsTable)
            .WithColumn("id").AsInt64().PrimaryKey().Identity()
            .WithColumn("page_id").AsInt64().NotNullable()
                .ForeignKey("fk_content_page_revisions_page", PagesTable, "id")
            .WithColumn("version").AsInt32().NotNullable()
            .WithColumn("title").AsString(200).NotNullable()
            .WithColumn("markdown").AsString(int.MaxValue).NotNullable()
            .WithColumn("change_summary").AsString(500).Nullable();
        dt(revisions.WithColumn("created_at")).NotNullable();
        revisions.WithColumn("created_by").AsString(256).Nullable();

        Create.Index("ux_content_pages_slug_language")
            .OnTable(PagesTable)
            .OnColumn("slug").Ascending()
            .OnColumn("language").Ascending()
            .WithOptions().Unique();

        Create.Index("ux_content_page_revisions_page_version")
            .OnTable(RevisionsTable)
            .OnColumn("page_id").Ascending()
            .OnColumn("version").Ascending()
            .WithOptions().Unique();
    }
}
```

- [ ] **Step 3: Create the PostgreSQL package**

`src/neutral/Themia.Content.PostgreSql/Themia.Content.PostgreSql.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Neutral cross-framework package: MUST include net8.0 (cross-framework reuse). -->
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.Content.PostgreSql</PackageId>
    <Description>PostgreSQL dialect + Npgsql driver + FluentMigrator runner for Themia.Content.</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Npgsql" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Content/Themia.Content.csproj" />
    <ProjectReference Include="../Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
    <ProjectReference Include="../Themia.Data.Migrations.PostgreSql/Themia.Data.Migrations.PostgreSql.csproj" />
    <ProjectReference Include="../Themia.Data.Probes/Themia.Data.Probes.csproj" />
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

`PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`: one line each, `#nullable enable`.

`src/neutral/Themia.Content.PostgreSql/PostgresContentDialect.cs`:

```csharp
using System.Data.Common;
using Npgsql;

namespace Themia.Content.PostgreSql;

/// <summary>PostgreSQL implementation of <see cref="IContentPageDialect"/> (Npgsql).</summary>
public sealed class PostgresContentDialect : IContentPageDialect
{
    private const string PageColumns =
        "id AS Id, slug AS Slug, language AS Language, title AS Title, markdown AS Markdown, " +
        "current_version AS CurrentVersion, is_published AS IsPublished, created_at AS CreatedAt, " +
        "updated_at AS UpdatedAt, updated_by AS UpdatedBy";

    private const string RevisionColumns =
        "r.version AS Version, r.title AS Title, r.markdown AS Markdown, r.change_summary AS ChangeSummary, " +
        "r.created_at AS CreatedAt, r.created_by AS CreatedBy";

    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    public PostgresContentDialect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection() => new NpgsqlConnection(connectionString);

    /// <inheritdoc />
    public bool IsDuplicateKey(DbException exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <inheritdoc />
    public string InsertPageSql => """
        INSERT INTO content_pages (slug, language, title, markdown, current_version, is_published, created_at, updated_at, updated_by)
        VALUES (@Slug, @Language, @Title, @Markdown, 1, @IsPublished, @Now, @Now, @EditorId);
        """;

    /// <inheritdoc />
    public string ImportPageSql => """
        INSERT INTO content_pages (slug, language, title, markdown, current_version, is_published, created_at, updated_at, updated_by)
        VALUES (@Slug, @Language, @Title, @Markdown, @CurrentVersion, @IsPublished, @CreatedAt, @UpdatedAt, @UpdatedBy);
        """;

    /// <inheritdoc />
    public string UpdatePageIfVersionSql => """
        UPDATE content_pages
           SET title = @Title, markdown = @Markdown, is_published = @IsPublished,
               current_version = @ExpectedVersion + 1, updated_at = @Now, updated_by = @EditorId
         WHERE slug = @Slug AND language = @Language AND current_version = @ExpectedVersion;
        """;

    /// <inheritdoc />
    public string InsertRevisionSql => """
        INSERT INTO content_page_revisions (page_id, version, title, markdown, change_summary, created_at, created_by)
        VALUES (@PageId, @Version, @Title, @Markdown, @ChangeSummary, @CreatedAt, @CreatedBy);
        """;

    /// <inheritdoc />
    public string SelectPageSql =>
        $"SELECT {PageColumns} FROM content_pages WHERE slug = @Slug AND language = @Language;";

    /// <inheritdoc />
    public string SelectPublishedSql => $"""
        SELECT {PageColumns} FROM content_pages
         WHERE slug = @Slug AND is_published = true AND language IN (@Language, @Fallback)
         ORDER BY CASE WHEN language = @Language THEN 0 ELSE 1 END
         LIMIT 1;
        """;

    /// <inheritdoc />
    public string SelectRevisionSql => $"""
        SELECT {RevisionColumns}
          FROM content_page_revisions r JOIN content_pages p ON p.id = r.page_id
         WHERE p.slug = @Slug AND p.language = @Language AND r.version = @Version;
        """;

    /// <inheritdoc />
    public string ListPagesSql => """
        SELECT slug AS Slug, language AS Language, title AS Title, current_version AS CurrentVersion,
               is_published AS IsPublished, updated_at AS UpdatedAt, updated_by AS UpdatedBy
          FROM content_pages
         ORDER BY slug, language
         LIMIT @Limit OFFSET @Offset;
        """;

    /// <inheritdoc />
    public string CountPagesSql => "SELECT COUNT(*) FROM content_pages;";

    /// <inheritdoc />
    public string ListRevisionsSql => $"""
        SELECT {RevisionColumns}
          FROM content_page_revisions r JOIN content_pages p ON p.id = r.page_id
         WHERE p.slug = @Slug AND p.language = @Language
         ORDER BY r.version DESC
         LIMIT @Limit OFFSET @Offset;
        """;

    /// <inheritdoc />
    public string CountRevisionsSql => """
        SELECT COUNT(*) FROM content_page_revisions r JOIN content_pages p ON p.id = r.page_id
         WHERE p.slug = @Slug AND p.language = @Language;
        """;
}
```

`src/neutral/Themia.Content.PostgreSql/ServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Themia.Content.Migrations;
using Themia.Data.Migrations;
using Themia.Data.Migrations.PostgreSql;
using Themia.Data.Probes;

namespace Themia.Content.PostgreSql;

/// <summary>DI entry point for the PostgreSQL-backed <c>Themia.Content</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PostgresContentDialect"/> as the <see cref="IContentPageDialect"/>, runs
    /// <see cref="ContentSchemaMigration"/> immediately, and adds a startup probe that fails the host when the two
    /// tables are not on the connection's <c>search_path</c>.
    /// </summary>
    /// <remarks>Call <c>AddThemiaContent</c> as well, in either order. Content shipped with application code must go
    /// through <c>IContentPageService.SaveAsync</c> after startup — never through a migration that writes these
    /// tables (see the package README).</remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    public static IServiceCollection AddThemiaContentPostgres(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<IContentPageDialect>(new PostgresContentDialect(connectionString));

        // Register on use, so the enum migration path stays resolvable for an adopter who calls only
        // engine-specific methods (coord #0126).
        MigrationEngineRegistry.Add(PostgresMigrationEngine.Adapter);

        ThemiaMigrations.Run(PostgresMigrationEngine.Adapter, connectionString, typeof(ContentSchemaMigration).Assembly);

        services.AddPostgresSchemaProbe(
            "Themia.Content",
            _ =>
            {
                var connection = new NpgsqlConnection(connectionString);
                connection.Open();
                return connection;
            },
            ["content_pages", "content_page_revisions"]);

        return services;
    }
}
```

- [ ] **Step 4: Create the integration-test project**

`tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj`:

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
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="Dapper" />
    <PackageReference Include="Npgsql" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" />
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/neutral/Themia.Content/Themia.Content.csproj" />
    <ProjectReference Include="../../src/neutral/Themia.Content.PostgreSql/Themia.Content.PostgreSql.csproj" />
    <ProjectReference Include="../../src/neutral/Themia.Data.Migrations.PostgreSql/Themia.Data.Migrations.PostgreSql.csproj" />
  </ItemGroup>
</Project>
```

`tests/Themia.Content.IntegrationTests/EngineRegistration.cs`:

```csharp
using System.Runtime.CompilerServices;
using Themia.Data.Migrations;
using Themia.Data.Migrations.PostgreSql;

namespace Themia.Content.IntegrationTests;

/// <summary>Registers the migration engine adapters this assembly's fixtures use. A module initializer is right in a
/// test assembly, which is always loaded; see <c>Themia.Challenges.IntegrationTests.EngineRegistration</c>.</summary>
internal static class EngineRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Register()
    {
        MigrationEngineRegistry.Add(PostgresMigrationEngine.Adapter);
    }
#pragma warning restore CA2255
}
```

`tests/Themia.Content.IntegrationTests/ContentEngineFixtures.cs`:

```csharp
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;
using Themia.Content.PostgreSql;
using Themia.Data.Migrations;
using Themia.Data.Migrations.PostgreSql;
using Xunit;

namespace Themia.Content.IntegrationTests;

/// <summary>
/// One real container per engine, shared by every test class for that engine through an xUnit collection fixture.
/// Registration goes through the same <c>AddThemiaContent&lt;Engine&gt;</c> path an adopter uses, which runs the
/// migration as a side effect. Tests in one collection run sequentially and use their own slugs.
/// </summary>
public abstract class ContentEngineFixture : IAsyncLifetime
{
    private ServiceProvider provider = null!;

    /// <summary>The container's connection string.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>The engine's dialect, for raw SQL no public API exposes.</summary>
    public IContentPageDialect Dialect { get; private set; } = null!;

    /// <summary>The clock every service in this fixture uses. Starts at a whole second so every engine stores it exactly.</summary>
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 14, 3, 0, 0, TimeSpan.Zero));

    /// <summary>The engine's migration adapter, for re-running the migration.</summary>
    public abstract IMigrationEngineAdapter MigrationAdapter { get; }

    /// <summary>Starts the container and returns its connection string.</summary>
    protected abstract Task<string> StartContainerAsync();

    /// <summary>Stops and disposes the container.</summary>
    protected abstract Task StopContainerAsync();

    /// <summary>Calls the engine package's registration method.</summary>
    protected abstract void RegisterEngine(IServiceCollection services, string connectionString);

    /// <summary>Whether a table named <paramref name="name"/> exists, read from the engine's catalog.</summary>
    public abstract Task<bool> TableExistsAsync(string name);

    /// <summary>Whether an index named <paramref name="name"/> exists, read from the engine's catalog.</summary>
    public abstract Task<bool> IndexExistsAsync(string name);

    /// <summary>Builds the service collection. Task 5 adds the core registration here.</summary>
    protected virtual void ConfigureServices(IServiceCollection services, string connectionString)
    {
        services.AddSingleton<TimeProvider>(Time);
        RegisterEngine(services, connectionString);
    }

    /// <summary>Resolves what tests use. Task 5 adds the service here.</summary>
    protected virtual void Resolve(IServiceProvider services) =>
        Dialect = services.GetRequiredService<IContentPageDialect>();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        ConnectionString = await StartContainerAsync();
        var services = new ServiceCollection();
        ConfigureServices(services, ConnectionString);
        provider = services.BuildServiceProvider();
        Resolve(provider);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await provider.DisposeAsync();
        await StopContainerAsync();
    }

    /// <summary>Runs a scalar query on a fresh connection.</summary>
    protected async Task<T> ScalarAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = Dialect.CreateConnection();
        await connection.OpenAsync();
        return (await connection.ExecuteScalarAsync<T>(sql, parameters))!;
    }
}

/// <summary>PostgreSQL: one <c>postgres:16-alpine</c> container for every PostgreSQL test class.</summary>
public sealed class PostgresContentFixture : ContentEngineFixture
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    /// <inheritdoc />
    public override IMigrationEngineAdapter MigrationAdapter => PostgresMigrationEngine.Adapter;

    /// <inheritdoc />
    protected override async Task<string> StartContainerAsync()
    {
        await container.StartAsync();
        return container.GetConnectionString();
    }

    /// <inheritdoc />
    protected override async Task StopContainerAsync() => await container.DisposeAsync();

    /// <inheritdoc />
    protected override void RegisterEngine(IServiceCollection services, string connectionString) =>
        services.AddThemiaContentPostgres(connectionString);

    /// <inheritdoc />
    public override Task<bool> TableExistsAsync(string name) =>
        ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = @Name);", new { Name = name });

    /// <inheritdoc />
    public override Task<bool> IndexExistsAsync(string name) =>
        ScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'public' AND indexname = @Name);", new { Name = name });
}

/// <summary>Ties every PostgreSQL test class to one <see cref="PostgresContentFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresContentCollection : ICollectionFixture<PostgresContentFixture>
{
    /// <summary>The collection name test classes reference.</summary>
    public const string Name = "Postgres Content";
}
```

If `PostgresMigrationEngine.Adapter` is not of type `IMigrationEngineAdapter`, use the adapter type the property actually declares (see `src/neutral/Themia.Data.Migrations.PostgreSql`) and adjust the abstract property to match.

- [ ] **Step 5: Write the schema tests**

`tests/Themia.Content.IntegrationTests/ContentSchemaTests.cs`:

```csharp
using Themia.Content.Migrations;
using Themia.Data.Migrations;
using Xunit;

namespace Themia.Content.IntegrationTests;

public abstract class ContentSchemaTests(ContentEngineFixture fixture)
{
    [Fact]
    public async Task Migration_ShouldCreateBothTables()
    {
        Assert.True(await fixture.TableExistsAsync("content_pages"));
        Assert.True(await fixture.TableExistsAsync("content_page_revisions"));
    }

    [Fact]
    public async Task Migration_ShouldCreateBothUniqueIndexes()
    {
        Assert.True(await fixture.IndexExistsAsync("ux_content_pages_slug_language"));
        Assert.True(await fixture.IndexExistsAsync("ux_content_page_revisions_page_version"));
    }

    [Fact]
    public void Migration_ShouldRunAgainWithoutError() =>
        ThemiaMigrations.Run(fixture.MigrationAdapter, fixture.ConnectionString, typeof(ContentSchemaMigration).Assembly);

    [Fact]
    public async Task Migration_ShouldRecordItselfInThisAssemblysOwnLedger()
    {
        var ledger = new ThemiaVersionTable(typeof(ContentSchemaMigration).Assembly).TableName;
        Assert.True(await fixture.TableExistsAsync(ledger), $"expected ledger table {ledger}");
    }
}

[Collection(PostgresContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresContentSchemaTests(PostgresContentFixture fixture) : ContentSchemaTests(fixture);
```

`tests/Themia.Content.IntegrationTests/ContentSchemaProbeTests.cs` (PostgreSQL only — `search_path` is a PostgreSQL concern):

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Content.Migrations;
using Themia.Content.PostgreSql;
using Themia.Data.Migrations;
using Themia.Data.Probes;
using Xunit;

namespace Themia.Content.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class ContentSchemaProbeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task Host_ShouldFailToStart_WhenTheContentTablesAreOffTheSearchPath()
    {
        var builder = new NpgsqlConnectionStringBuilder(container.GetConnectionString());

        await using (var seed = new NpgsqlConnection(builder.ConnectionString))
        {
            await seed.OpenAsync();
            await using var command = seed.CreateCommand();
            command.CommandText = "CREATE SCHEMA IF NOT EXISTS content_app";
            await command.ExecuteNonQueryAsync();
        }

        // Migrate on the default search_path, so the tables land in public; then point the application at a schema
        // that cannot see them. The probe, not the migration, is what this test is about.
        ThemiaMigrations.Run(MigrationEngine.Postgres, builder.ConnectionString, typeof(ContentSchemaMigration).Assembly);

        builder.SearchPath = "content_app";

        using var host = new HostBuilder()
            .ConfigureServices(services => services.AddThemiaContentPostgres(builder.ConnectionString))
            .Build();

        await Assert.ThrowsAsync<SchemaVisibilityException>(() => host.StartAsync());
    }
}
```

- [ ] **Step 6: Add the projects to the solution and run the tests**

```bash
dotnet sln Themia.sln add src/neutral/Themia.Content.PostgreSql/Themia.Content.PostgreSql.csproj tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj
dotnet format analyzers src/neutral/Themia.Content/Themia.Content.csproj --diagnostics RS0016 --severity info
dotnet format analyzers src/neutral/Themia.Content.PostgreSql/Themia.Content.PostgreSql.csproj --diagnostics RS0016 --severity info
dotnet build Themia.sln
dotnet test tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj --filter "FullyQualifiedName~ContentSchema"
```

Apply the solution-folder check from Task 1 Step 5. Expected: clean build; 5 passed (4 schema tests, 1 probe test).

- [ ] **Step 7: Falsify the probe test**

```bash
cp tests/Themia.Content.IntegrationTests/ContentSchemaProbeTests.cs /tmp/ContentSchemaProbeTests.cs.orig
```

Delete the line `builder.SearchPath = "content_app";`. Run the probe test. Expected: FAIL — the host starts. Record it, then restore with `cp` and prove it with `diff` as in Task 2 Step 6, and re-run to green.

- [ ] **Step 8: Commit** (check the time first)

```bash
git add Themia.sln src/neutral/Themia.Content src/neutral/Themia.Content.PostgreSql tests/Themia.Content.IntegrationTests
git commit -m "feat(content): add schema migration and PostgreSQL dialect"
```

---
### Task 5: The service — save, reads, and registration

**Files:**
- Create: `src/neutral/Themia.Content/IContentPageService.cs`
- Create: `src/neutral/Themia.Content/Internal/ContentRows.cs`
- Create: `src/neutral/Themia.Content/Internal/ContentPageService.cs`
- Create: `src/neutral/Themia.Content/DependencyInjection/ContentServiceCollectionExtensions.cs`
- Create: `tests/Themia.Content.Tests/CountingContentDialect.cs`
- Test: `tests/Themia.Content.Tests/ContentServiceRegistrationTests.cs`
- Modify: `tests/Themia.Content.IntegrationTests/ContentEngineFixtures.cs` (the two virtual methods)
- Create: `tests/Themia.Content.IntegrationTests/ContentTestData.cs`
- Test: `tests/Themia.Content.IntegrationTests/ContentSaveTests.cs`
- Test: `tests/Themia.Content.IntegrationTests/ContentReadTests.cs`
- Modify: `src/neutral/Themia.Content/PublicAPI.Unshipped.txt`

**Interfaces:**
- Consumes: everything from Tasks 1–4.
- Produces `public interface IContentPageService` exactly as spec §3:
  `GetPublishedAsync(string slug, string? language, CancellationToken ct = default) -> Task<ContentPage?>`,
  `GetForEditAsync(string slug, string language, CancellationToken ct = default) -> Task<ContentPage?>`,
  `ListAsync(int page, int limit, CancellationToken ct = default) -> Task<PagedResult<ContentPageSummary>>`,
  `GetRevisionsAsync(string slug, string language, int page, int limit, CancellationToken ct = default) -> Task<PagedResult<ContentPageRevision>>`,
  `SaveAsync(ContentPageSave save, CancellationToken ct = default) -> Task<ContentSaveResult>`,
  `RevertAsync(ContentPageRevert revert, CancellationToken ct = default) -> Task<ContentSaveResult>`.
- Produces `internal sealed class ContentPageService(IContentPageDialect dialect, ContentOptions options, TimeProvider time, ILogger<ContentPageService> logger) : IContentPageService` with `internal const int MaxPageSize = 100`.
- Produces `public static IServiceCollection AddThemiaContent(this IServiceCollection services, Action<ContentOptions> configure)` in namespace `Themia.Content.DependencyInjection`.
- Produces for tests: `ContentEngineFixture.Service`, `ContentEngineFixture.Options`, `ContentEngineFixture.NewService(IContentPageDialect dialect)`; `ContentTestData.NewSlug()`, `ContentTestData.Save(...)`.

The service implements revert in this task because the class must implement the whole interface; Task 6 writes the revert tests and proves them by falsification.

- [ ] **Step 1: Write the failing unit tests**

`tests/Themia.Content.Tests/CountingContentDialect.cs`:

```csharp
using System.Data.Common;

namespace Themia.Content.Tests;

/// <summary>A dialect that refuses to connect and counts attempts — proves a code path opens no connection. It says
/// nothing about SQL.</summary>
internal sealed class CountingContentDialect : IContentPageDialect
{
    public int ConnectionsRequested { get; private set; }

    public DbConnection CreateConnection()
    {
        ConnectionsRequested++;
        throw new InvalidOperationException("This test expected no connection to be opened.");
    }

    public bool IsDuplicateKey(DbException exception) => false;
    public string InsertPageSql => string.Empty;
    public string ImportPageSql => string.Empty;
    public string UpdatePageIfVersionSql => string.Empty;
    public string InsertRevisionSql => string.Empty;
    public string SelectPageSql => string.Empty;
    public string SelectPublishedSql => string.Empty;
    public string SelectRevisionSql => string.Empty;
    public string ListPagesSql => string.Empty;
    public string CountPagesSql => string.Empty;
    public string ListRevisionsSql => string.Empty;
    public string CountRevisionsSql => string.Empty;
}
```

`tests/Themia.Content.Tests/ContentServiceRegistrationTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Content.DependencyInjection;
using Themia.Content.Internal;
using Xunit;

namespace Themia.Content.Tests;

public class ContentServiceRegistrationTests
{
    private static void ThaiAndEnglish(ContentOptions options)
    {
        options.Languages.Add("th");
        options.Languages.Add("en");
        options.FallbackLanguage = "th";
    }

    private static ContentOptions Options()
    {
        var options = new ContentOptions();
        ThaiAndEnglish(options);
        return options;
    }

    [Fact]
    public void AddThemiaContent_ShouldThrowImmediately_WhenOptionsAreInvalid() =>
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddThemiaContent(_ => { }));

    [Fact]
    public void ResolvingTheService_ShouldNameTheEngineMethods_WhenNoDialectIsRegistered()
    {
        using var provider = new ServiceCollection().AddThemiaContent(ThaiAndEnglish).BuildServiceProvider();
        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IContentPageService>());
        Assert.Contains("AddThemiaContentPostgres", error.Message);
    }

    [Fact]
    public void ResolvingTheService_ShouldSucceed_WhenTheDialectIsRegisteredAfterTheCore()
    {
        var services = new ServiceCollection().AddThemiaContent(ThaiAndEnglish);
        services.AddSingleton<IContentPageDialect>(new CountingContentDialect());
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IContentPageService>());
    }

    [Fact]
    public void ResolvingTheService_ShouldSucceed_WhenTheDialectIsRegisteredBeforeTheCore()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IContentPageDialect>(new CountingContentDialect());
        services.AddThemiaContent(ThaiAndEnglish);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IContentPageService>());
    }

    [Fact]
    public async Task SaveAsync_ShouldReturnInvalidWithoutOpeningAConnection()
    {
        var dialect = new CountingContentDialect();
        var service = new ContentPageService(dialect, Options(), TimeProvider.System, NullLogger<ContentPageService>.Instance);

        var result = await service.SaveAsync(new ContentPageSave("Bad Slug", "th", "T", "# T", true, 0, null, null));

        Assert.Equal(ContentSaveOutcome.Invalid, result.Outcome);
        Assert.Equal(0, dialect.ConnectionsRequested);
    }

    [Fact]
    public async Task RevertAsync_ShouldReturnInvalidWithoutOpeningAConnection()
    {
        var dialect = new CountingContentDialect();
        var service = new ContentPageService(dialect, Options(), TimeProvider.System, NullLogger<ContentPageService>.Instance);

        var result = await service.RevertAsync(new ContentPageRevert("terms", "fr", 1, 2, null, null));

        Assert.Equal(ContentSaveOutcome.Invalid, result.Outcome);
        Assert.Equal(0, dialect.ConnectionsRequested);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task ListAsync_ShouldThrowBeforeConnecting_WhenPagingIsOutOfRange(int page, int limit)
    {
        var dialect = new CountingContentDialect();
        var service = new ContentPageService(dialect, Options(), TimeProvider.System, NullLogger<ContentPageService>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ListAsync(page, limit));
        Assert.Equal(0, dialect.ConnectionsRequested);
    }
}
```

Run: `dotnet test tests/Themia.Content.Tests/Themia.Content.Tests.csproj --filter ContentServiceRegistrationTests`
Expected: build fails — `IContentPageService` does not exist.

- [ ] **Step 2: Write the service interface**

`src/neutral/Themia.Content/IContentPageService.cs`:

```csharp
namespace Themia.Content;

/// <summary>Reads and writes content pages. Every rule — a save writes a revision, a stale version is refused, revert
/// is a save of an old body — lives here, once, for every engine.</summary>
/// <remarks>Content that ships with application code must also go through <see cref="SaveAsync"/>, from a startup step,
/// never through SQL written against the content tables: those rules exist nowhere else (see the package README).</remarks>
public interface IContentPageService
{
    /// <summary>The published page in <paramref name="language"/>, else in the configured fallback language, else
    /// <see langword="null"/>. A <see langword="null"/> or unconfigured language reads as the fallback.</summary>
    Task<ContentPage?> GetPublishedAsync(string slug, string? language, CancellationToken ct = default);

    /// <summary>The page in exactly <paramref name="language"/>, published or not. Never falls back: an editor who
    /// opens one language and is shown another saves it over the wrong row.</summary>
    Task<ContentPage?> GetForEditAsync(string slug, string language, CancellationToken ct = default);

    /// <summary>One page of page summaries, ordered by slug then language.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="limit">Items per page, 1 to 100.</param>
    /// <param name="ct">Cancellation.</param>
    Task<PagedResult<ContentPageSummary>> ListAsync(int page, int limit, CancellationToken ct = default);

    /// <summary>One page of a page's revisions, newest first.</summary>
    Task<PagedResult<ContentPageRevision>> GetRevisionsAsync(string slug, string language, int page, int limit, CancellationToken ct = default);

    /// <summary>Creates a page (<see cref="ContentPageSave.ExpectedVersion"/> 0) or updates one at the expected version,
    /// writing a revision either way.</summary>
    Task<ContentSaveResult> SaveAsync(ContentPageSave save, CancellationToken ct = default);

    /// <summary>Saves the title and body of <see cref="ContentPageRevert.TargetVersion"/> as a new version, keeping the
    /// page's publish state, in one transaction.</summary>
    Task<ContentSaveResult> RevertAsync(ContentPageRevert revert, CancellationToken ct = default);
}
```

- [ ] **Step 3: Write the row classes**

`src/neutral/Themia.Content/Internal/ContentRows.cs`:

```csharp
namespace Themia.Content.Internal;

/// <summary>The key of one page.</summary>
internal readonly record struct PageKey(string Slug, string Language);

/// <summary>One <c>content_pages</c> row, bound by the dialect's column aliases.</summary>
internal sealed class ContentPageRow
{
    public long Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Markdown { get; set; } = string.Empty;
    public int CurrentVersion { get; set; }
    public bool IsPublished { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    public ContentPage ToPage() =>
        new(Slug, Language, Title, Markdown, CurrentVersion, IsPublished, CreatedAt, UpdatedAt, UpdatedBy);
}

/// <summary>One page-summary row.</summary>
internal sealed class ContentPageSummaryRow
{
    public string Slug { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int CurrentVersion { get; set; }
    public bool IsPublished { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    public ContentPageSummary ToSummary() =>
        new(Slug, Language, Title, CurrentVersion, IsPublished, UpdatedAt, UpdatedBy);
}

/// <summary>One <c>content_page_revisions</c> row.</summary>
internal sealed class ContentRevisionRow
{
    public int Version { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Markdown { get; set; } = string.Empty;
    public string? ChangeSummary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedBy { get; set; }

    public ContentPageRevision ToRevision() => new(Version, Title, Markdown, ChangeSummary, CreatedAt, CreatedBy);
}
```

- [ ] **Step 4: Write the service**

`src/neutral/Themia.Content/Internal/ContentPageService.cs`:

```csharp
using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Themia.Content.Internal;

/// <summary>The one implementation of the content rules, over any <see cref="IContentPageDialect"/>.</summary>
/// <remarks>
/// <para><b>One connection, one transaction, READ COMMITTED.</b> MySQL's default REPEATABLE READ reads a plain
/// <c>SELECT</c> from the snapshot taken at the transaction's first read; revert reads before its guarded
/// <c>UPDATE</c>, so the version it reported in a conflict was stale (measured on mysql 8.4.9).</para>
/// <para><b>Create guard:</b> the unique index on (slug, language), behind a savepoint so a lost race leaves the
/// transaction able to read the winner's version. <b>Update guard:</b> <c>current_version = @ExpectedVersion</c> in the
/// dialect's <c>UPDATE</c>. Neither is a read-then-compare.</para>
/// <para><b>Savepoints are used without checking <see cref="DbTransaction.SupportsSavepoints"/></b>, which
/// MySqlConnector and SqlClient leave <see langword="false"/> although both implement <c>Save</c> and
/// <c>Rollback(string)</c>.</para>
/// </remarks>
internal sealed class ContentPageService : IContentPageService
{
    internal const int MaxPageSize = 100;

    private const string CreateSavepoint = "themia_content_create";

    private readonly IContentPageDialect dialect;
    private readonly ContentOptions options;
    private readonly TimeProvider time;
    private readonly ILogger<ContentPageService> logger;

    public ContentPageService(IContentPageDialect dialect, ContentOptions options, TimeProvider time, ILogger<ContentPageService> logger)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        this.dialect = dialect;
        this.options = options;
        this.time = time;
        this.logger = logger;
    }

    public async Task<ContentPage?> GetPublishedAsync(string slug, string? language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(slug);

        var requested = ContentLanguage.Normalise(language);
        if (!options.IsConfigured(requested))
        {
            requested = options.NormalisedFallback;
        }

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<ContentPageRow>(new CommandDefinition(
            dialect.SelectPublishedSql,
            new { Slug = slug, Language = requested, Fallback = options.NormalisedFallback },
            cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToPage();
    }

    public async Task<ContentPage?> GetForEditAsync(string slug, string language, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(slug);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var row = await SelectPageAsync(connection, null, new PageKey(slug, ContentLanguage.Normalise(language)), ct).ConfigureAwait(false);
        return row?.ToPage();
    }

    public async Task<PagedResult<ContentPageSummary>> ListAsync(int page, int limit, CancellationToken ct = default)
    {
        ValidatePaging(page, limit);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ContentPageSummaryRow>(new CommandDefinition(
            dialect.ListPagesSql, new { Offset = (page - 1) * limit, Limit = limit }, cancellationToken: ct)).ConfigureAwait(false);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            dialect.CountPagesSql, cancellationToken: ct)).ConfigureAwait(false);
        return new PagedResult<ContentPageSummary> { Items = rows.Select(r => r.ToSummary()).ToList(), Total = checked((int)total) };
    }

    public async Task<PagedResult<ContentPageRevision>> GetRevisionsAsync(
        string slug, string language, int page, int limit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ValidatePaging(page, limit);

        var key = new PageKey(slug, ContentLanguage.Normalise(language));
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ContentRevisionRow>(new CommandDefinition(
            dialect.ListRevisionsSql,
            new { key.Slug, key.Language, Offset = (page - 1) * limit, Limit = limit },
            cancellationToken: ct)).ConfigureAwait(false);
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            dialect.CountRevisionsSql, new { key.Slug, key.Language }, cancellationToken: ct)).ConfigureAwait(false);
        return new PagedResult<ContentPageRevision> { Items = rows.Select(r => r.ToRevision()).ToList(), Total = checked((int)total) };
    }

    public async Task<ContentSaveResult> SaveAsync(ContentPageSave save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(save);

        var errors = ContentPageValidator.Validate(save, options);
        if (errors.Count > 0)
        {
            return Logged(ContentSaveResult.Invalid(errors), save.Slug, save.Language, "save");
        }

        var key = new PageKey(save.Slug, ContentLanguage.Normalise(save.Language));
        var content = new PageContent(save.Title, save.Markdown, save.IsPublished);

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        var result = save.ExpectedVersion == 0
            ? await CreateAsync(connection, transaction, key, content, save.ChangeSummary, save.EditorId, ct).ConfigureAwait(false)
            : await UpdateAsync(connection, transaction, key, content, save.ExpectedVersion, save.ChangeSummary, save.EditorId, ct).ConfigureAwait(false);

        await FinishAsync(transaction, result, ct).ConfigureAwait(false);
        return Logged(result, key.Slug, key.Language, "save");
    }

    public async Task<ContentSaveResult> RevertAsync(ContentPageRevert revert, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(revert);

        var errors = ContentPageValidator.Validate(revert, options);
        if (errors.Count > 0)
        {
            return Logged(ContentSaveResult.Invalid(errors), revert.Slug, revert.Language, "revert");
        }

        // The key carries the language into the save below. A revert that loses a key dimension writes one
        // language's body over another's row (coord #0130 [3]).
        var key = new PageKey(revert.Slug, ContentLanguage.Normalise(revert.Language));

        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        var target = await connection.QuerySingleOrDefaultAsync<ContentRevisionRow>(new CommandDefinition(
            dialect.SelectRevisionSql,
            new { key.Slug, key.Language, Version = revert.TargetVersion },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);
        var current = target is null ? null : await SelectPageAsync(connection, transaction, key, ct).ConfigureAwait(false);

        var result = target is null || current is null
            ? ContentSaveResult.NotFound()
            : await UpdateAsync(
                connection,
                transaction,
                key,
                new PageContent(target.Title, target.Markdown, current.IsPublished),
                revert.ExpectedVersion,
                revert.ChangeSummary ?? $"Reverted to version {revert.TargetVersion}",
                revert.EditorId,
                ct).ConfigureAwait(false);

        await FinishAsync(transaction, result, ct).ConfigureAwait(false);
        return Logged(result, key.Slug, key.Language, "revert");
    }

    private async Task<ContentSaveResult> CreateAsync(
        DbConnection connection, DbTransaction transaction, PageKey key, PageContent content,
        string? changeSummary, string? editorId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await transaction.SaveAsync(CreateSavepoint, ct).ConfigureAwait(false);

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                dialect.InsertPageSql,
                new { key.Slug, key.Language, content.Title, content.Markdown, content.IsPublished, Now = now, EditorId = editorId },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (DbException exception) when (dialect.IsDuplicateKey(exception))
        {
            await transaction.RollbackAsync(CreateSavepoint, ct).ConfigureAwait(false);
            var existing = await SelectPageAsync(connection, transaction, key, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Inserting content page {key.Slug}/{key.Language} reported a duplicate key, but no such page could be read.");
            return ContentSaveResult.Conflict(existing.CurrentVersion, existing.UpdatedBy, existing.UpdatedAt);
        }

        var page = await SelectPageAsync(connection, transaction, key, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Content page {key.Slug}/{key.Language} was inserted but cannot be read back.");
        await InsertRevisionAsync(connection, transaction, page, changeSummary, now, editorId, ct).ConfigureAwait(false);
        return ContentSaveResult.Saved(page.ToPage());
    }

    private async Task<ContentSaveResult> UpdateAsync(
        DbConnection connection, DbTransaction transaction, PageKey key, PageContent content, int expectedVersion,
        string? changeSummary, string? editorId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            dialect.UpdatePageIfVersionSql,
            new
            {
                key.Slug,
                key.Language,
                content.Title,
                content.Markdown,
                content.IsPublished,
                ExpectedVersion = expectedVersion,
                Now = now,
                EditorId = editorId,
            },
            transaction,
            cancellationToken: ct)).ConfigureAwait(false);

        var page = await SelectPageAsync(connection, transaction, key, ct).ConfigureAwait(false);

        if (updated == 0)
        {
            return page is null
                ? ContentSaveResult.NotFound()
                : ContentSaveResult.Conflict(page.CurrentVersion, page.UpdatedBy, page.UpdatedAt);
        }

        if (page is null)
        {
            throw new InvalidOperationException($"Content page {key.Slug}/{key.Language} was updated but cannot be read back.");
        }

        await InsertRevisionAsync(connection, transaction, page, changeSummary, now, editorId, ct).ConfigureAwait(false);
        return ContentSaveResult.Saved(page.ToPage());
    }

    private Task InsertRevisionAsync(
        DbConnection connection, DbTransaction transaction, ContentPageRow page, string? changeSummary,
        DateTimeOffset now, string? editorId, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            dialect.InsertRevisionSql,
            new
            {
                PageId = page.Id,
                Version = page.CurrentVersion,
                page.Title,
                page.Markdown,
                ChangeSummary = changeSummary,
                CreatedAt = now,
                CreatedBy = editorId,
            },
            transaction,
            cancellationToken: ct));

    private Task<ContentPageRow?> SelectPageAsync(DbConnection connection, DbTransaction? transaction, PageKey key, CancellationToken ct) =>
        connection.QuerySingleOrDefaultAsync<ContentPageRow>(new CommandDefinition(
            dialect.SelectPageSql, new { key.Slug, key.Language }, transaction, cancellationToken: ct));

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = dialect.CreateConnection();
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task FinishAsync(DbTransaction transaction, ContentSaveResult result, CancellationToken ct)
    {
        if (result.Succeeded)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
        }
    }

    private static void ValidatePaging(int page, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, int.MaxValue / MaxPageSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxPageSize);
    }

    // Slug, language and outcome only. A markdown body is author content and is never logged.
    private ContentSaveResult Logged(ContentSaveResult result, string? slug, string? language, string operation)
    {
        logger.LogInformation(
            "Content page {Slug}/{Language} {Operation} ended {Outcome}", slug, language, operation, result.Outcome);
        return result;
    }

    private readonly record struct PageContent(string Title, string Markdown, bool IsPublished);
}
```

- [ ] **Step 5: Write the registration**

`src/neutral/Themia.Content/DependencyInjection/ContentServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Themia.Content.Internal;

namespace Themia.Content.DependencyInjection;

/// <summary>DI entry point for <c>Themia.Content</c>.</summary>
public static class ContentServiceCollectionExtensions
{
    /// <summary>
    /// Registers validated <see cref="ContentOptions"/>, <see cref="TimeProvider.System"/> (unless one is registered),
    /// logging, and <see cref="IContentPageService"/>. Does not register a dialect — call exactly one of
    /// <c>AddThemiaContentPostgres</c>, <c>AddThemiaContentMySql</c> or <c>AddThemiaContentSqlServer</c>, in either order.
    /// </summary>
    /// <remarks>The dialect is checked when <see cref="IContentPageService"/> is first resolved, not here, so every
    /// registration order works and a missing engine still fails loudly.</remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the languages and the fallback language.</param>
    /// <exception cref="InvalidOperationException">The configured options cannot serve a page.</exception>
    public static IServiceCollection AddThemiaContent(this IServiceCollection services, Action<ContentOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ContentOptions();
        configure(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddLogging();

        services.TryAddSingleton<IContentPageService>(provider => new ContentPageService(
            provider.GetService<IContentPageDialect>() ?? throw new InvalidOperationException(
                "No IContentPageDialect is registered. Call AddThemiaContentPostgres, AddThemiaContentMySql or " +
                "AddThemiaContentSqlServer as well as AddThemiaContent."),
            provider.GetRequiredService<ContentOptions>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<ContentPageService>>()));

        return services;
    }
}
```

- [ ] **Step 6: Run the unit tests**

Run: `dotnet test tests/Themia.Content.Tests/Themia.Content.Tests.csproj`
Expected: every test passes on both frameworks. Then `dotnet format analyzers src/neutral/Themia.Content/Themia.Content.csproj --diagnostics RS0016 --severity info` and a clean `dotnet build`.

- [ ] **Step 7: Give the fixture the service**

In `tests/Themia.Content.IntegrationTests/ContentEngineFixtures.cs`, add `using Microsoft.Extensions.Logging.Abstractions;`, `using Themia.Content.DependencyInjection;` and `using Themia.Content.Internal;`, then replace the two virtual methods:

```csharp
    /// <summary>The live service, resolved from DI exactly as an adopter resolves it.</summary>
    public IContentPageService Service { get; private set; } = null!;

    /// <summary>The resolved options: languages th and en, fallback th.</summary>
    public ContentOptions Options { get; private set; } = null!;

    /// <summary>A service over <paramref name="dialect"/> with this fixture's options and clock — for tests that wrap
    /// the real dialect.</summary>
    internal ContentPageService NewService(IContentPageDialect dialect) =>
        new(dialect, Options, Time, NullLogger<ContentPageService>.Instance);

    /// <summary>Builds the service collection through the adopter's registration path.</summary>
    protected virtual void ConfigureServices(IServiceCollection services, string connectionString)
    {
        services.AddSingleton<TimeProvider>(Time);
        services.AddThemiaContent(options =>
        {
            options.Languages.Add("th");
            options.Languages.Add("en");
            options.FallbackLanguage = "th";
        });
        RegisterEngine(services, connectionString);
    }

    /// <summary>Resolves what tests use.</summary>
    protected virtual void Resolve(IServiceProvider services)
    {
        Dialect = services.GetRequiredService<IContentPageDialect>();
        Service = services.GetRequiredService<IContentPageService>();
        Options = services.GetRequiredService<ContentOptions>();
    }
```

Add `<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />` to the integration-test project if the build asks for it.

- [ ] **Step 8: Write the shared test data**

`tests/Themia.Content.IntegrationTests/ContentTestData.cs`:

```csharp
namespace Themia.Content.IntegrationTests;

internal static class ContentTestData
{
    /// <summary>A slug no other test uses: lowercase hex after a prefix, so it passes the slug rule.</summary>
    public static string NewSlug() => $"p-{Guid.NewGuid():N}";

    public static ContentPageSave Save(
        string slug, string language, int expectedVersion, string markdown = "# Body", string title = "Title",
        bool isPublished = true, string? editorId = "editor-a", string? changeSummary = null) =>
        new(slug, language, title, markdown, isPublished, expectedVersion, changeSummary, editorId);
}
```

- [ ] **Step 9: Write the save tests**

`tests/Themia.Content.IntegrationTests/ContentSaveTests.cs`:

```csharp
using Xunit;
using static Themia.Content.IntegrationTests.ContentTestData;

namespace Themia.Content.IntegrationTests;

public abstract class ContentSaveTests(ContentEngineFixture fixture)
{
    [Fact]
    public async Task Create_ShouldWriteVersionOneAndRevisionOne()
    {
        var slug = NewSlug();

        var result = await fixture.Service.SaveAsync(Save(slug, "th", 0, "# One"));

        Assert.Equal(ContentSaveOutcome.Saved, result.Outcome);
        Assert.Equal(1, result.Page!.CurrentVersion);
        var revisions = await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20);
        Assert.Equal(1, revisions.Total);
        Assert.Equal("# One", revisions.Items[0].Markdown);
    }

    [Fact]
    public async Task Update_ShouldAdvanceTheVersionAndWriteARevision()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# One"));

        var result = await fixture.Service.SaveAsync(Save(slug, "th", 1, "# Two", changeSummary: "wording"));

        Assert.Equal(ContentSaveOutcome.Saved, result.Outcome);
        Assert.Equal(2, result.Page!.CurrentVersion);
        Assert.Equal("# Two", result.Page.Markdown);
        var revisions = await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20);
        Assert.Equal([2, 1], revisions.Items.Select(r => r.Version));
        Assert.Equal("wording", revisions.Items[0].ChangeSummary);
    }

    [Fact]
    public async Task Update_ShouldReturnTheCurrentStateAndWriteNothing_WhenTheExpectedVersionIsStale()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Service.SaveAsync(Save(slug, "th", 1, "# Newer", editorId: "editor-b"));
        var savedAt = fixture.Time.GetUtcNow();

        var result = await fixture.Service.SaveAsync(Save(slug, "th", 1, "# Stale", editorId: "editor-a"));

        Assert.Equal(ContentSaveOutcome.Conflict, result.Outcome);
        Assert.Equal(2, result.CurrentVersion);
        Assert.Equal("editor-b", result.CurrentUpdatedBy);
        Assert.Equal(savedAt, result.CurrentUpdatedAt);
        Assert.Equal("# Newer", (await fixture.Service.GetForEditAsync(slug, "th"))!.Markdown);
        Assert.Equal(2, (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20)).Total);
    }

    [Fact]
    public async Task Create_ShouldReturnConflictAndWriteNothing_WhenThePageAlreadyExists()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# First"));

        var result = await fixture.Service.SaveAsync(Save(slug, "th", 0, "# Second"));

        Assert.Equal(ContentSaveOutcome.Conflict, result.Outcome);
        Assert.Equal(1, result.CurrentVersion);
        Assert.Equal("# First", (await fixture.Service.GetForEditAsync(slug, "th"))!.Markdown);
    }

    [Fact]
    public async Task Update_ShouldReturnNotFound_WhenThePageDoesNotExist()
    {
        var result = await fixture.Service.SaveAsync(Save(NewSlug(), "th", 3));
        Assert.Equal(ContentSaveOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task PublishToggle_ShouldWriteARevision()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, isPublished: true));

        var result = await fixture.Service.SaveAsync(Save(slug, "th", 1, isPublished: false));

        Assert.False(result.Page!.IsPublished);
        Assert.Equal(2, (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20)).Total);
    }

    [Fact]
    public async Task Timestamps_ShouldRoundTripAsTheSameInstant()
    {
        var slug = NewSlug();
        var instant = fixture.Time.GetUtcNow();

        await fixture.Service.SaveAsync(Save(slug, "th", 0));
        var page = (await fixture.Service.GetForEditAsync(slug, "th"))!;
        var revision = (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20)).Items[0];

        Assert.Equal(instant.UtcDateTime, page.CreatedAt.UtcDateTime);
        Assert.Equal(instant.UtcDateTime, page.UpdatedAt.UtcDateTime);
        Assert.Equal(instant.UtcDateTime, revision.CreatedAt.UtcDateTime);
    }

    [Fact]
    public async Task Language_ShouldBeStoredNormalised()
    {
        var slug = NewSlug();

        await fixture.Service.SaveAsync(Save(slug, " EN ", 0));

        var page = await fixture.Service.GetForEditAsync(slug, "en");
        Assert.NotNull(page);
        Assert.Equal("en", page.Language);
    }
}

[Collection(PostgresContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresContentSaveTests(PostgresContentFixture fixture) : ContentSaveTests(fixture);
```

- [ ] **Step 10: Write the read tests**

`tests/Themia.Content.IntegrationTests/ContentReadTests.cs`:

```csharp
using Xunit;
using static Themia.Content.IntegrationTests.ContentTestData;

namespace Themia.Content.IntegrationTests;

public abstract class ContentReadTests(ContentEngineFixture fixture)
{
    [Fact]
    public async Task GetPublished_ShouldReturnTheRequestedLanguage()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# ไทย"));
        await fixture.Service.SaveAsync(Save(slug, "en", 0, "# English"));

        Assert.Equal("# English", (await fixture.Service.GetPublishedAsync(slug, "en"))!.Markdown);
    }

    [Fact]
    public async Task GetPublished_ShouldFallBack_WhenTheRequestedLanguageIsMissing()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# ไทย"));

        Assert.Equal("th", (await fixture.Service.GetPublishedAsync(slug, "en"))!.Language);
    }

    [Fact]
    public async Task GetPublished_ShouldFallBack_WhenTheRequestedLanguageIsUnpublished()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# ไทย"));
        await fixture.Service.SaveAsync(Save(slug, "en", 0, "# English", isPublished: false));

        Assert.Equal("th", (await fixture.Service.GetPublishedAsync(slug, "en"))!.Language);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fr")]
    public async Task GetPublished_ShouldReadAsTheFallback_WhenTheLanguageIsMissingOrUnconfigured(string? language)
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));
        await fixture.Service.SaveAsync(Save(slug, "en", 0));

        Assert.Equal("th", (await fixture.Service.GetPublishedAsync(slug, language))!.Language);
    }

    [Fact]
    public async Task GetPublished_ShouldReturnNull_WhenNoLanguageIsPublished()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, isPublished: false));

        Assert.Null(await fixture.Service.GetPublishedAsync(slug, "th"));
    }

    [Fact]
    public async Task GetForEdit_ShouldNeverFallBack()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));

        Assert.Null(await fixture.Service.GetForEditAsync(slug, "en"));
    }

    [Fact]
    public async Task List_ShouldCountEveryPageAndOrderBySlugThenLanguage()
    {
        var before = (await fixture.Service.ListAsync(1, 1)).Total;
        var slug = "a-" + NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));
        await fixture.Service.SaveAsync(Save(slug, "en", 0));

        var first = await fixture.Service.ListAsync(1, 100);

        Assert.Equal(before + 2, first.Total);
        var mine = first.Items.Where(s => s.Slug == slug).Select(s => s.Language).ToArray();
        Assert.Equal(["en", "th"], mine);
    }

    [Fact]
    public async Task Revisions_ShouldPageNewestFirst()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));
        await fixture.Service.SaveAsync(Save(slug, "th", 1));
        await fixture.Service.SaveAsync(Save(slug, "th", 2));

        var second = await fixture.Service.GetRevisionsAsync(slug, "th", 2, 2);

        Assert.Equal(3, second.Total);
        Assert.Equal([1], second.Items.Select(r => r.Version));
    }

    [Fact]
    public async Task Revisions_ShouldBeEmpty_WhenThePageDoesNotExist()
    {
        var result = await fixture.Service.GetRevisionsAsync(NewSlug(), "th", 1, 20);
        Assert.Equal(0, result.Total);
        Assert.Empty(result.Items);
    }
}

[Collection(PostgresContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresContentReadTests(PostgresContentFixture fixture) : ContentReadTests(fixture);
```

- [ ] **Step 11: Run the integration tests**

Run: `dotnet test tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj`
Expected: every test passes, including Task 4's.

- [ ] **Step 12: Falsify the stale-version guard**

```bash
cp src/neutral/Themia.Content.PostgreSql/PostgresContentDialect.cs /tmp/PostgresContentDialect.cs.orig
```

In `UpdatePageIfVersionSql`, change ` AND current_version = @ExpectedVersion;` to `;`. Run `--filter "FullyQualifiedName~ContentSaveTests"`. Expected: FAIL — at least `Update_ShouldReturnTheCurrentStateAndWriteNothing_WhenTheExpectedVersionIsStale`. Record it; restore with `cp` and prove with `diff`; re-run to green.

- [ ] **Step 13: Run every unit test in the repository**

Run: `dotnet test Themia.sln --filter "Category!=Integration"`
Expected: no failures anywhere.

- [ ] **Step 14: Commit** (check the time first)

```bash
git add src/neutral/Themia.Content tests/Themia.Content.Tests tests/Themia.Content.IntegrationTests
git commit -m "feat(content): save and read pages with a stale-version guard"
```

---

### Task 6: Revert

**Files:**
- Test: `tests/Themia.Content.IntegrationTests/ContentRevertTests.cs`

**Interfaces:**
- Consumes: `ContentEngineFixture.Service`, `ContentTestData` (Task 5), `IContentPageService.RevertAsync` (implemented in Task 5).

Revert was implemented in Task 5 so the service could implement its interface. These tests are therefore written after the code; Step 3 is what proves each one can fail.

- [ ] **Step 1: Write the tests**

`tests/Themia.Content.IntegrationTests/ContentRevertTests.cs`:

```csharp
using Xunit;
using static Themia.Content.IntegrationTests.ContentTestData;

namespace Themia.Content.IntegrationTests;

public abstract class ContentRevertTests(ContentEngineFixture fixture)
{
    [Fact]
    public async Task Revert_ShouldSaveTheTargetBodyAsANewVersion()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# One", title: "One"));
        await fixture.Service.SaveAsync(Save(slug, "th", 1, "# Two", title: "Two"));

        var result = await fixture.Service.RevertAsync(new ContentPageRevert(slug, "th", 1, 2, null, "editor-c"));

        Assert.Equal(ContentSaveOutcome.Saved, result.Outcome);
        Assert.Equal(3, result.Page!.CurrentVersion);
        Assert.Equal("# One", result.Page.Markdown);
        Assert.Equal("One", result.Page.Title);
        var newest = (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 1)).Items[0];
        Assert.Equal("Reverted to version 1", newest.ChangeSummary);
        Assert.Equal("editor-c", newest.CreatedBy);
    }

    [Fact]
    public async Task Revert_ShouldLeaveTheDefaultLanguageUntouched_WhenRevertingAnotherLanguage()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# ไทย"));
        await fixture.Service.SaveAsync(Save(slug, "en", 0, "# EN v1"));
        await fixture.Service.SaveAsync(Save(slug, "en", 1, "# EN v2"));
        var thaiBefore = await fixture.Service.GetForEditAsync(slug, "th");

        var result = await fixture.Service.RevertAsync(new ContentPageRevert(slug, "en", 1, 2, null, null));

        Assert.Equal(ContentSaveOutcome.Saved, result.Outcome);
        Assert.Equal("# EN v1", (await fixture.Service.GetForEditAsync(slug, "en"))!.Markdown);
        Assert.Equal(thaiBefore, await fixture.Service.GetForEditAsync(slug, "th"));
        Assert.Equal(1, (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20)).Total);
    }

    [Fact]
    public async Task Revert_ShouldKeepThePublishState()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# One", isPublished: true));
        await fixture.Service.SaveAsync(Save(slug, "th", 1, "# Two", isPublished: false));

        var result = await fixture.Service.RevertAsync(new ContentPageRevert(slug, "th", 1, 2, "undo", null));

        Assert.False(result.Page!.IsPublished);
        Assert.Equal("undo", (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 1)).Items[0].ChangeSummary);
    }

    [Fact]
    public async Task Revert_ShouldReturnConflictAndWriteNothing_WhenTheExpectedVersionIsStale()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));
        await fixture.Service.SaveAsync(Save(slug, "th", 1));

        var result = await fixture.Service.RevertAsync(new ContentPageRevert(slug, "th", 1, 1, null, null));

        Assert.Equal(ContentSaveOutcome.Conflict, result.Outcome);
        Assert.Equal(2, result.CurrentVersion);
        Assert.Equal(2, (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20)).Total);
    }

    [Fact]
    public async Task Revert_ShouldReturnNotFound_WhenTheTargetVersionDoesNotExist()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));

        var result = await fixture.Service.RevertAsync(new ContentPageRevert(slug, "th", 7, 1, null, null));

        Assert.Equal(ContentSaveOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Revert_ShouldReturnNotFound_WhenThePageDoesNotExist()
    {
        var result = await fixture.Service.RevertAsync(new ContentPageRevert(NewSlug(), "th", 1, 1, null, null));
        Assert.Equal(ContentSaveOutcome.NotFound, result.Outcome);
    }
}

[Collection(PostgresContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresContentRevertTests(PostgresContentFixture fixture) : ContentRevertTests(fixture);
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj --filter "FullyQualifiedName~ContentRevertTests"`
Expected: 6 passed.

- [ ] **Step 3: Falsify the language carried into the revert** (spec §12)

```bash
cp src/neutral/Themia.Content/Internal/ContentPageService.cs /tmp/ContentPageService.cs.orig
```

In `RevertAsync`, in the `UpdateAsync(` call, replace the argument `key,` with `key with { Language = options.NormalisedFallback },`. Run the revert tests. Expected: FAIL — `Revert_ShouldLeaveTheDefaultLanguageUntouched_WhenRevertingAnotherLanguage`. Record; restore with `cp`; prove with `diff`; re-run to green.

- [ ] **Step 4: Commit** (check the time first)

```bash
git add tests/Themia.Content.IntegrationTests/ContentRevertTests.cs
git commit -m "test(content): revert keeps publish state and other languages"
```

---

### Task 7: Concurrency — forced races, not hoped-for ones

**Files:**
- Create: `tests/Themia.Content.IntegrationTests/GatedContentDialect.cs`
- Test: `tests/Themia.Content.IntegrationTests/ContentConcurrencyTests.cs`

**Interfaces:**
- Consumes: `ContentEngineFixture.NewService(IContentPageDialect)`, `Dialect`, `Service` (Task 5).
- Produces: `internal sealed class GatedContentDialect(IContentPageDialect inner) : IContentPageDialect` with `Action? BeforeInsertPage` and `Action? BeforeUpdatePage`, each run when the service reads the matching SQL property — immediately before that statement executes.

A bare `Task.WhenAll` does not reliably make two calls overlap (see `RaceGatingChallengeDialect`'s remarks). These tests force the ordering. The revert race forces **revert to lose**, because only that ordering exposes a stale snapshot; a two-party barrier would leave the winner to chance and the test would detect the defect half the time.

- [ ] **Step 1: Write the gated dialect**

`tests/Themia.Content.IntegrationTests/GatedContentDialect.cs`:

```csharp
using System.Data.Common;

namespace Themia.Content.IntegrationTests;

/// <summary>Wraps a real dialect and runs a hook when the service reads the insert-page or update-page SQL, which it
/// does immediately before executing that statement on a real connection.</summary>
internal sealed class GatedContentDialect(IContentPageDialect inner) : IContentPageDialect
{
    public Action? BeforeInsertPage { get; init; }

    public Action? BeforeUpdatePage { get; init; }

    public DbConnection CreateConnection() => inner.CreateConnection();

    public bool IsDuplicateKey(DbException exception) => inner.IsDuplicateKey(exception);

    public string InsertPageSql
    {
        get
        {
            BeforeInsertPage?.Invoke();
            return inner.InsertPageSql;
        }
    }

    public string ImportPageSql => inner.ImportPageSql;

    public string UpdatePageIfVersionSql
    {
        get
        {
            BeforeUpdatePage?.Invoke();
            return inner.UpdatePageIfVersionSql;
        }
    }

    public string InsertRevisionSql => inner.InsertRevisionSql;
    public string SelectPageSql => inner.SelectPageSql;
    public string SelectPublishedSql => inner.SelectPublishedSql;
    public string SelectRevisionSql => inner.SelectRevisionSql;
    public string ListPagesSql => inner.ListPagesSql;
    public string CountPagesSql => inner.CountPagesSql;
    public string ListRevisionsSql => inner.ListRevisionsSql;
    public string CountRevisionsSql => inner.CountRevisionsSql;
}
```

- [ ] **Step 2: Write the tests**

`tests/Themia.Content.IntegrationTests/ContentConcurrencyTests.cs`:

```csharp
using System.Data.Common;
using Dapper;
using Xunit;
using static Themia.Content.IntegrationTests.ContentTestData;

namespace Themia.Content.IntegrationTests;

public abstract class ContentConcurrencyTests(ContentEngineFixture fixture)
{
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ConcurrentUpdates_ShouldSaveExactlyOnce_WhenBothExpectTheSameVersion()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));
        using var barrier = new Barrier(2);
        var gated = new GatedContentDialect(fixture.Dialect) { BeforeUpdatePage = () => barrier.SignalAndWait(GateTimeout) };
        var first = fixture.NewService(gated);
        var second = fixture.NewService(gated);

        var results = await Task.WhenAll(
            Task.Run(() => first.SaveAsync(Save(slug, "th", 1, "# A", editorId: "a"))),
            Task.Run(() => second.SaveAsync(Save(slug, "th", 1, "# B", editorId: "b"))));

        Assert.Single(results, r => r.Outcome == ContentSaveOutcome.Saved);
        var conflict = Assert.Single(results, r => r.Outcome == ContentSaveOutcome.Conflict);
        Assert.Equal(2, conflict.CurrentVersion);
        Assert.Equal(2, (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20)).Total);
    }

    [Fact]
    public async Task ConcurrentCreates_ShouldSaveExactlyOnce_AndReportVersionOneToTheLoser()
    {
        var slug = NewSlug();
        using var barrier = new Barrier(2);
        var gated = new GatedContentDialect(fixture.Dialect) { BeforeInsertPage = () => barrier.SignalAndWait(GateTimeout) };
        var first = fixture.NewService(gated);
        var second = fixture.NewService(gated);

        var results = await Task.WhenAll(
            Task.Run(() => first.SaveAsync(Save(slug, "th", 0, "# A"))),
            Task.Run(() => second.SaveAsync(Save(slug, "th", 0, "# B"))));

        Assert.Single(results, r => r.Outcome == ContentSaveOutcome.Saved);
        var conflict = Assert.Single(results, r => r.Outcome == ContentSaveOutcome.Conflict);
        Assert.Equal(1, conflict.CurrentVersion);
        Assert.Equal(1, (await fixture.Service.GetRevisionsAsync(slug, "th", 1, 20)).Total);
    }

    [Fact]
    public async Task RevertThatLosesARace_ShouldReportTheWinnersVersion()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0, "# One"));
        await fixture.Service.SaveAsync(Save(slug, "th", 1, "# Two"));

        using var revertReadsDone = new ManualResetEventSlim();
        using var rivalCommitted = new ManualResetEventSlim();
        var reverter = fixture.NewService(new GatedContentDialect(fixture.Dialect)
        {
            // Reached after revert's two plain reads and before its guarded UPDATE.
            BeforeUpdatePage = () =>
            {
                revertReadsDone.Set();
                Assert.True(rivalCommitted.Wait(GateTimeout), "rival save did not commit");
            },
        });

        var revert = Task.Run(() => reverter.RevertAsync(new ContentPageRevert(slug, "th", 1, 2, null, "reverter")));
        Assert.True(revertReadsDone.Wait(GateTimeout), "revert never reached its update");
        var rival = await fixture.Service.SaveAsync(Save(slug, "th", 2, "# Three", editorId: "rival"));
        rivalCommitted.Set();
        var result = await revert;

        Assert.Equal(ContentSaveOutcome.Saved, rival.Outcome);
        Assert.Equal(ContentSaveOutcome.Conflict, result.Outcome);
        Assert.Equal(3, result.CurrentVersion);
        Assert.Equal("rival", result.CurrentUpdatedBy);
    }

    [Fact]
    public async Task DuplicateRevision_ShouldBeRefusedByTheSchema_WhenWrittenOutsideTheService()
    {
        var slug = NewSlug();
        await fixture.Service.SaveAsync(Save(slug, "th", 0));

        await using var connection = fixture.Dialect.CreateConnection();
        await connection.OpenAsync();
        var pageId = await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM content_pages WHERE slug = @Slug AND language = @Language", new { Slug = slug, Language = "th" });

        await Assert.ThrowsAnyAsync<DbException>(() => connection.ExecuteAsync(fixture.Dialect.InsertRevisionSql, new
        {
            PageId = pageId,
            Version = 1,
            Title = "Duplicate",
            Markdown = "# Duplicate",
            ChangeSummary = (string?)null,
            CreatedAt = fixture.Time.GetUtcNow(),
            CreatedBy = (string?)null,
        }));
    }
}

[Collection(PostgresContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresContentConcurrencyTests(PostgresContentFixture fixture) : ContentConcurrencyTests(fixture);
```

- [ ] **Step 3: Run them**

Run: `dotnet test tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj --filter "FullyQualifiedName~ContentConcurrencyTests"`
Expected: 4 passed. **The concurrent-create test passing is the proof that Npgsql savepoints work here** (spec §5); a failure is a design finding, not a test to adjust.

- [ ] **Step 4: Falsify all three guards** (spec §12) — one at a time, each with `cp` backup, a recorded red run, `cp` restore and `diff`:

1. `PostgresContentDialect.UpdatePageIfVersionSql`: remove ` AND current_version = @ExpectedVersion`. Expected red: `ConcurrentUpdates_ShouldSaveExactlyOnce_WhenBothExpectTheSameVersion` (the second `UPDATE` succeeds and its revision insert then fails on the unique index, or both save).
2. `ContentSchemaMigration.CreateTables`: delete the `Create.Index("ux_content_pages_slug_language")` statement. Each test run starts a new container, so the migration runs fresh. Expected red: `ConcurrentCreates_ShouldSaveExactlyOnce_AndReportVersionOneToTheLoser`.
3. `ContentSchemaMigration.CreateTables`: delete the `Create.Index("ux_content_page_revisions_page_version")` statement. Expected red: `DuplicateRevision_ShouldBeRefusedByTheSchema_WhenWrittenOutsideTheService`.

PostgreSQL defaults to READ COMMITTED, so removing the isolation level cannot turn anything red here; that guard is falsified on MySQL in Task 9.

After the three restores, run the whole integration project to green.

- [ ] **Step 5: Commit** (check the time first)

```bash
git add tests/Themia.Content.IntegrationTests
git commit -m "test(content): force save, create and revert races"
```

---

### Task 8: Content shipped with code — the seeding path

**Files:**
- Test: `tests/Themia.Content.IntegrationTests/ContentSeedingTests.cs`
- Create: `src/neutral/Themia.Content/README.md`
- Modify: `docs/superpowers/specs/2026-09-14-themia-content-design.md` (§5, "Content shipped with code")

**Interfaces:**
- Consumes: `IContentPageService` (Task 5).
- Produces: the documented pattern — each content change a consumer ships declares `AppliesToVersion`, the version the page must be at before that change (0 when the change creates the page).

**Why this task pins a rule the spec left ambiguous.** Spec §5 and coord #0130 [6] say "a page at the version this code last wrote: save with that version". If "last wrote" moves forward after each write, a second run finds the page at the version just written and saves again — the step is not idempotent, which both texts claim it is. The rule that is idempotent and editor-safe is a **constant per change**: apply the change only when the page's version equals that change's `AppliesToVersion`. After it applies, the page has moved past it, so a re-run skips; an editor's save moves the page past it too, so the change never overwrites an editor. The tests below pin that rule, and Step 4 corrects the spec's wording.

- [ ] **Step 1: Write the tests**

`tests/Themia.Content.IntegrationTests/ContentSeedingTests.cs`:

```csharp
using Xunit;
using static Themia.Content.IntegrationTests.ContentTestData;

namespace Themia.Content.IntegrationTests;

/// <summary>The seeding pattern the package README documents, exercised against a real engine.</summary>
public abstract class ContentSeedingTests(ContentEngineFixture fixture)
{
    /// <summary>One content change shipped with application code.</summary>
    /// <param name="AppliesToVersion">The version the page must be at before this change; 0 creates it.</param>
    private sealed record ContentChange(string Slug, string Language, string Title, string Markdown, int AppliesToVersion);

    /// <summary>Applies <paramref name="change"/> when the page is at its <see cref="ContentChange.AppliesToVersion"/>,
    /// and does nothing otherwise. Returns whether a version was written.</summary>
    private static async Task<bool> ApplyAsync(IContentPageService service, ContentChange change)
    {
        var page = await service.GetForEditAsync(change.Slug, change.Language);
        if ((page?.CurrentVersion ?? 0) != change.AppliesToVersion)
        {
            return false;
        }

        var result = await service.SaveAsync(new ContentPageSave(
            change.Slug, change.Language, change.Title, change.Markdown, page?.IsPublished ?? true,
            change.AppliesToVersion, "Shipped with application code", "app"));
        return result.Succeeded;
    }

    [Fact]
    public async Task Change_ShouldCreateThePageOnce_WhenRunTwice()
    {
        var create = new ContentChange(NewSlug(), "th", "Terms", "# Terms", AppliesToVersion: 0);

        Assert.True(await ApplyAsync(fixture.Service, create));
        Assert.False(await ApplyAsync(fixture.Service, create));

        Assert.Equal(1, (await fixture.Service.GetRevisionsAsync(create.Slug, "th", 1, 20)).Total);
    }

    [Fact]
    public async Task LaterChange_ShouldApplyOnce_WhenThePageIsAtTheVersionTheEarlierChangeProduced()
    {
        var slug = NewSlug();
        var create = new ContentChange(slug, "th", "Terms", "# Terms v1", AppliesToVersion: 0);
        var reword = new ContentChange(slug, "th", "Terms", "# Terms v2", AppliesToVersion: 1);

        await ApplyAsync(fixture.Service, create);
        Assert.True(await ApplyAsync(fixture.Service, reword));
        Assert.False(await ApplyAsync(fixture.Service, reword));
        Assert.False(await ApplyAsync(fixture.Service, create));

        var page = await fixture.Service.GetForEditAsync(slug, "th");
        Assert.Equal(2, page!.CurrentVersion);
        Assert.Equal("# Terms v2", page.Markdown);
    }

    [Fact]
    public async Task LaterChange_ShouldLeaveThePageAlone_WhenAnEditorHasSavedSince()
    {
        var slug = NewSlug();
        await ApplyAsync(fixture.Service, new ContentChange(slug, "th", "Terms", "# Terms v1", AppliesToVersion: 0));
        await fixture.Service.SaveAsync(Save(slug, "th", 1, "# Edited by counsel", editorId: "editor"));

        var applied = await ApplyAsync(fixture.Service, new ContentChange(slug, "th", "Terms", "# Terms v2", AppliesToVersion: 1));

        Assert.False(applied);
        Assert.Equal("# Edited by counsel", (await fixture.Service.GetForEditAsync(slug, "th"))!.Markdown);
    }
}

[Collection(PostgresContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresContentSeedingTests(PostgresContentFixture fixture) : ContentSeedingTests(fixture);
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj --filter "FullyQualifiedName~ContentSeedingTests"`
Expected: 3 passed.

- [ ] **Step 3: Falsify the pattern's version check**

Copy the test file, then in `ApplyAsync` replace `if ((page?.CurrentVersion ?? 0) != change.AppliesToVersion)` with `if (page is not null && change.AppliesToVersion == 0)`. Run. Expected: FAIL — `LaterChange_ShouldApplyOnce…` (a re-run saves again) and `LaterChange_ShouldLeaveThePageAlone…` (the change overwrites the editor, or conflicts). Record; restore with `cp`; `diff`; re-run to green.

- [ ] **Step 4: Correct the spec's wording**

In `docs/superpowers/specs/2026-09-14-themia-content-design.md`, in "Content shipped with code — the same path, never SQL", replace the numbered list and the sentence after it with:

```markdown
Each content change the application ships declares **`AppliesToVersion`** — the version the page must be at before
the change, 0 when the change creates the page. The pattern needs no new API:

1. `GetForEditAsync(slug, language)`; a missing page is at version 0.
2. The page is at the change's `AppliesToVersion`: `SaveAsync` with `ExpectedVersion = AppliesToVersion`. A
   `Conflict` means another instance or an editor saved first; stop.
3. The page is at any other version: the change has already applied, or an editor has changed the page. Leave it.

`AppliesToVersion` is a constant of the change, not a value that moves after each write. A "last written version"
that advances would find the page at the version just written and save again. With a constant, a re-run skips
because the page has moved past it, and an editor's save moves the page past it too, so shipped content never
overwrites an editor. Both are integration tests (§12).
```

- [ ] **Step 5: Write the package README**

`src/neutral/Themia.Content/README.md`:

````markdown
# Themia.Content

Versioned, bilingual content pages keyed by slug and language — legal and static pages with revision history,
stale-version refusal, and raw HTML and unsafe link destinations refused at write.

## Registration

```csharp
services.AddThemiaContent(options =>
{
    options.Languages.Add("th");
    options.Languages.Add("en");
    options.FallbackLanguage = "th";
});
services.AddThemiaContentPostgres(connectionString); // or AddThemiaContentMySql / AddThemiaContentSqlServer
```

The engine method runs the schema migration immediately. Both tables are platform-level: there is no tenant column.

## Never write the content tables with SQL

Every rule lives in `IContentPageService`: a save writes a revision, advances the version, refuses a stale version
and checks the markdown. A migration or script that `INSERT`s or `UPDATE`s `content_pages` bypasses all of them. The
first revert or edit afterwards can then serve text that is in no revision — the defect coord #0132 describes in an
application that wrote its CMS content from migrations.

A migration also runs before `IContentPageService` can be resolved, so it could not call the service if it tried.

## Content shipped with code

Ship content through `SaveAsync` from a startup step that runs once the service resolves — a hosted service, or an
explicit call during boot. Give every content change a constant `AppliesToVersion`: the version the page must be at
before the change, 0 when the change creates the page.

```csharp
public sealed record ContentChange(string Slug, string Language, string Title, string Markdown, int AppliesToVersion);

public static async Task<bool> ApplyAsync(IContentPageService service, ContentChange change, CancellationToken ct)
{
    var page = await service.GetForEditAsync(change.Slug, change.Language, ct);
    if ((page?.CurrentVersion ?? 0) != change.AppliesToVersion)
    {
        return false; // already applied, or an editor has changed the page since
    }

    var result = await service.SaveAsync(new ContentPageSave(
        change.Slug, change.Language, change.Title, change.Markdown, page?.IsPublished ?? true,
        change.AppliesToVersion, "Shipped with application code", EditorId: "app"), ct);
    return result.Succeeded; // a Conflict means someone saved first: leave it
}

// Shipped in release 1:
new ContentChange("terms", "th", "ข้อกำหนด", termsV1, AppliesToVersion: 0);
// Shipped in release 2, rewording what release 1 created:
new ContentChange("terms", "th", "ข้อกำหนด", termsV2, AppliesToVersion: 1);
```

`AppliesToVersion` never changes after the change is written. Re-running the step is a no-op, and a change never
overwrites an editor's save.
````

- [ ] **Step 6: Commit** (check the time first)

```bash
git add tests/Themia.Content.IntegrationTests/ContentSeedingTests.cs src/neutral/Themia.Content/README.md docs/superpowers/specs/2026-09-14-themia-content-design.md
git commit -m "docs(content): pin the seeding rule to a constant per change"
```

---
## Block 2 — MySQL and SQL Server

### Task 9: MySQL engine

**Files:**
- Create: `src/neutral/Themia.Content.MySql/Themia.Content.MySql.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- Create: `src/neutral/Themia.Content.MySql/MySqlContentDialect.cs`
- Create: `src/neutral/Themia.Content.MySql/ServiceCollectionExtensions.cs`
- Modify: `tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj`
- Modify: `tests/Themia.Content.IntegrationTests/EngineRegistration.cs`
- Modify: `tests/Themia.Content.IntegrationTests/ContentEngineFixtures.cs` (append the MySQL fixture and collection)
- Modify: the six test files — `ContentSchemaTests.cs`, `ContentSaveTests.cs`, `ContentReadTests.cs`, `ContentRevertTests.cs`, `ContentConcurrencyTests.cs`, `ContentSeedingTests.cs` (append a MySQL subclass to each)
- Modify: `Themia.sln`

**Interfaces:**
- Consumes: `IContentPageDialect`, `ContentSchemaMigration`, `ContentEngineFixture` (Tasks 4–5).
- Produces: `public sealed class MySqlContentDialect`, `public static IServiceCollection AddThemiaContentMySql(this IServiceCollection services, string connectionString)` (namespace `Themia.Content.MySql`), `MySqlContentFixture`, `MySqlContentCollection` (`Name = "MySql Content"`).

- [ ] **Step 1: Add the MySQL test classes first**

Append to each of the six test files, naming the subclass after the file's abstract class (`MySqlContentSchemaTests`, `MySqlContentSaveTests`, `MySqlContentReadTests`, `MySqlContentRevertTests`, `MySqlContentConcurrencyTests`, `MySqlContentSeedingTests`):

```csharp
[Collection(MySqlContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlContentSaveTests(MySqlContentFixture fixture) : ContentSaveTests(fixture);
```

Append the fixture to `ContentEngineFixtures.cs` (add `using Testcontainers.MySql;`, `using Themia.Content.MySql;`, `using Themia.Data.Migrations.MySql;`):

```csharp
/// <summary>MySQL: one <c>mysql:8.4</c> container for every MySQL test class.</summary>
public sealed class MySqlContentFixture : ContentEngineFixture
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();

    /// <inheritdoc />
    public override IMigrationEngineAdapter MigrationAdapter => MySqlMigrationEngine.Adapter;

    /// <inheritdoc />
    protected override async Task<string> StartContainerAsync()
    {
        await container.StartAsync();
        return container.GetConnectionString();
    }

    /// <inheritdoc />
    protected override async Task StopContainerAsync() => await container.DisposeAsync();

    /// <inheritdoc />
    protected override void RegisterEngine(IServiceCollection services, string connectionString) =>
        services.AddThemiaContentMySql(connectionString);

    /// <inheritdoc />
    public override Task<bool> TableExistsAsync(string name) =>
        ScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @Name);",
            new { Name = name });

    /// <inheritdoc />
    public override Task<bool> IndexExistsAsync(string name) =>
        ScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.statistics WHERE table_schema = DATABASE() AND index_name = @Name);",
            new { Name = name });
}

/// <summary>Ties every MySQL test class to one <see cref="MySqlContentFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class MySqlContentCollection : ICollectionFixture<MySqlContentFixture>
{
    /// <summary>The collection name test classes reference.</summary>
    public const string Name = "MySql Content";
}
```

In `EngineRegistration.Register`, add `MigrationEngineRegistry.Add(MySqlMigrationEngine.Adapter);` with `using Themia.Data.Migrations.MySql;`.

In the integration-test project add `<PackageReference Include="Testcontainers.MySql" />`, `<PackageReference Include="MySqlConnector" />`, and project references to `../../src/neutral/Themia.Content.MySql/Themia.Content.MySql.csproj` and `../../src/neutral/Themia.Data.Migrations.MySql/Themia.Data.Migrations.MySql.csproj`.

Run: `dotnet build tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj`
Expected: build fails — `Themia.Content.MySql` does not exist.

- [ ] **Step 2: Create the MySQL package**

`src/neutral/Themia.Content.MySql/Themia.Content.MySql.csproj`: identical to the PostgreSQL project from Task 4 Step 3 except — `PackageId` `Themia.Content.MySql`; `Description` `MySQL dialect + MySqlConnector driver + FluentMigrator runner for Themia.Content.`; package reference `MySqlConnector` instead of `Npgsql`; project reference `../Themia.Data.Migrations.MySql/Themia.Data.Migrations.MySql.csproj` instead of the PostgreSQL one; **no** `Themia.Data.Probes` reference. The full file:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Neutral cross-framework package: MUST include net8.0 (cross-framework reuse). -->
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.Content.MySql</PackageId>
    <Description>MySQL dialect + MySqlConnector driver + FluentMigrator runner for Themia.Content.</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="MySqlConnector" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Content/Themia.Content.csproj" />
    <ProjectReference Include="../Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
    <ProjectReference Include="../Themia.Data.Migrations.MySql/Themia.Data.Migrations.MySql.csproj" />
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

Both PublicAPI files: `#nullable enable`.

`src/neutral/Themia.Content.MySql/MySqlContentDialect.cs`:

```csharp
using System.Data.Common;
using MySqlConnector;

namespace Themia.Content.MySql;

/// <summary>MySQL 8.0.13+ implementation of <see cref="IContentPageDialect"/> (MySqlConnector).</summary>
/// <remarks>
/// <para><b>Affected rows.</b> MySqlConnector reports found rows, not changed rows, by default. The service relies on
/// the count from <see cref="UpdatePageIfVersionSql"/>; the two agree here because the statement always changes
/// <c>current_version</c>.</para>
/// <para><b>Time zone.</b> <c>DATETIME(6)</c> stores no offset. The connection is pinned to
/// <see cref="MySqlDateTimeKind.Utc"/> so a value read back is the instant that was written on any host time zone;
/// <c>ContentSaveTests.Timestamps_ShouldRoundTripAsTheSameInstant</c> run under a non-UTC <c>TZ</c> is the proof.</para>
/// </remarks>
public sealed class MySqlContentDialect : IContentPageDialect
{
    private const string PageColumns =
        "id AS Id, slug AS Slug, language AS Language, title AS Title, markdown AS Markdown, " +
        "current_version AS CurrentVersion, is_published AS IsPublished, created_at AS CreatedAt, " +
        "updated_at AS UpdatedAt, updated_by AS UpdatedBy";

    private const string RevisionColumns =
        "r.version AS Version, r.title AS Title, r.markdown AS Markdown, r.change_summary AS ChangeSummary, " +
        "r.created_at AS CreatedAt, r.created_by AS CreatedBy";

    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    /// <param name="connectionString">MySQL connection string.</param>
    public MySqlContentDialect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = new MySqlConnectionStringBuilder(connectionString) { DateTimeKind = MySqlDateTimeKind.Utc }
            .ConnectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection() => new MySqlConnection(connectionString);

    /// <inheritdoc />
    public bool IsDuplicateKey(DbException exception) =>
        exception is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry };

    /// <inheritdoc />
    public string InsertPageSql => """
        INSERT INTO content_pages (slug, language, title, markdown, current_version, is_published, created_at, updated_at, updated_by)
        VALUES (@Slug, @Language, @Title, @Markdown, 1, @IsPublished, @Now, @Now, @EditorId);
        """;

    /// <inheritdoc />
    public string ImportPageSql => """
        INSERT INTO content_pages (slug, language, title, markdown, current_version, is_published, created_at, updated_at, updated_by)
        VALUES (@Slug, @Language, @Title, @Markdown, @CurrentVersion, @IsPublished, @CreatedAt, @UpdatedAt, @UpdatedBy);
        """;

    /// <inheritdoc />
    public string UpdatePageIfVersionSql => """
        UPDATE content_pages
           SET title = @Title, markdown = @Markdown, is_published = @IsPublished,
               current_version = @ExpectedVersion + 1, updated_at = @Now, updated_by = @EditorId
         WHERE slug = @Slug AND language = @Language AND current_version = @ExpectedVersion;
        """;

    /// <inheritdoc />
    public string InsertRevisionSql => """
        INSERT INTO content_page_revisions (page_id, version, title, markdown, change_summary, created_at, created_by)
        VALUES (@PageId, @Version, @Title, @Markdown, @ChangeSummary, @CreatedAt, @CreatedBy);
        """;

    /// <inheritdoc />
    public string SelectPageSql =>
        $"SELECT {PageColumns} FROM content_pages WHERE slug = @Slug AND language = @Language;";

    /// <inheritdoc />
    public string SelectPublishedSql => $"""
        SELECT {PageColumns} FROM content_pages
         WHERE slug = @Slug AND is_published = TRUE AND language IN (@Language, @Fallback)
         ORDER BY CASE WHEN language = @Language THEN 0 ELSE 1 END
         LIMIT 1;
        """;

    /// <inheritdoc />
    public string SelectRevisionSql => $"""
        SELECT {RevisionColumns}
          FROM content_page_revisions r JOIN content_pages p ON p.id = r.page_id
         WHERE p.slug = @Slug AND p.language = @Language AND r.version = @Version;
        """;

    /// <inheritdoc />
    public string ListPagesSql => """
        SELECT slug AS Slug, language AS Language, title AS Title, current_version AS CurrentVersion,
               is_published AS IsPublished, updated_at AS UpdatedAt, updated_by AS UpdatedBy
          FROM content_pages
         ORDER BY slug, language
         LIMIT @Limit OFFSET @Offset;
        """;

    /// <inheritdoc />
    public string CountPagesSql => "SELECT COUNT(*) FROM content_pages;";

    /// <inheritdoc />
    public string ListRevisionsSql => $"""
        SELECT {RevisionColumns}
          FROM content_page_revisions r JOIN content_pages p ON p.id = r.page_id
         WHERE p.slug = @Slug AND p.language = @Language
         ORDER BY r.version DESC
         LIMIT @Limit OFFSET @Offset;
        """;

    /// <inheritdoc />
    public string CountRevisionsSql => """
        SELECT COUNT(*) FROM content_page_revisions r JOIN content_pages p ON p.id = r.page_id
         WHERE p.slug = @Slug AND p.language = @Language;
        """;
}
```

`src/neutral/Themia.Content.MySql/ServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Content.Migrations;
using Themia.Data.Migrations;
using Themia.Data.Migrations.MySql;

namespace Themia.Content.MySql;

/// <summary>DI entry point for the MySQL-backed <c>Themia.Content</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="MySqlContentDialect"/> as the <see cref="IContentPageDialect"/> and runs
    /// <see cref="ContentSchemaMigration"/> immediately.</summary>
    /// <remarks>Call <c>AddThemiaContent</c> as well, in either order. Content shipped with application code must go
    /// through <c>IContentPageService.SaveAsync</c> after startup, never through SQL (see the package README).</remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">MySQL connection string.</param>
    public static IServiceCollection AddThemiaContentMySql(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<IContentPageDialect>(new MySqlContentDialect(connectionString));

        // Register on use (coord #0126).
        MigrationEngineRegistry.Add(MySqlMigrationEngine.Adapter);

        ThemiaMigrations.Run(MySqlMigrationEngine.Adapter, connectionString, typeof(ContentSchemaMigration).Assembly);

        return services;
    }
}
```

- [ ] **Step 3: Add to the solution, record the API, run the suite**

```bash
dotnet sln Themia.sln add src/neutral/Themia.Content.MySql/Themia.Content.MySql.csproj
dotnet format analyzers src/neutral/Themia.Content.MySql/Themia.Content.MySql.csproj --diagnostics RS0016 --severity info
dotnet build Themia.sln
dotnet test tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj
```

Apply the solution-folder check from Task 1 Step 5. Expected: every PostgreSQL and MySQL test passes. The MySQL concurrent-create test passing is the proof MySqlConnector savepoints work (spec §5). If a MySQL test fails with error 1213 (deadlock), **stop and report the test and the statement** — do not add a retry: Themia.Challenges needed one for a range scan, and whether this schema needs one is a finding, not an assumption.

- [ ] **Step 4: Falsify READ COMMITTED on MySQL** (spec §12)

```bash
cp src/neutral/Themia.Content/Internal/ContentPageService.cs /tmp/ContentPageService.cs.orig
```

In `RevertAsync` only, replace `connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)` with `connection.BeginTransactionAsync(ct)`.

Run: `dotnet test tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj --filter "FullyQualifiedName~MySqlContentConcurrencyTests.RevertThatLosesARace_ShouldReportTheWinnersVersion"`
Expected: FAIL — `CurrentVersion` is 2, not 3 (the stale snapshot). Record; restore with `cp`; `diff`; re-run to green.

- [ ] **Step 5: Decide whether the UTC kind is a real guard**

The dialect pins `DateTimeKind = MySqlDateTimeKind.Utc`. Prove it guards something, or remove it:

```bash
TZ=Asia/Bangkok dotnet test tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj --filter "FullyQualifiedName~MySqlContentSaveTests.Timestamps_ShouldRoundTripAsTheSameInstant"
cp src/neutral/Themia.Content.MySql/MySqlContentDialect.cs /tmp/MySqlContentDialect.cs.orig
```

Replace the constructor's assignment with `this.connectionString = connectionString;` and run the same `TZ=Asia/Bangkok` command.

- **Red** without it: restore with `cp`, `diff`, re-run to green, and record the failure in the task report. The setting stays.
- **Green** without it: the setting guards nothing on this driver version. Keep the edited constructor, delete the "Time zone" paragraph from the class remarks, and record why in the task report. A guard that cannot fail is not kept.

- [ ] **Step 6: Commit** (check the time first)

```bash
git add Themia.sln src/neutral/Themia.Content.MySql tests/Themia.Content.IntegrationTests
git commit -m "feat(content): add the MySQL engine"
```

---

### Task 10: SQL Server engine

**Files:**
- Create: `src/neutral/Themia.Content.SqlServer/Themia.Content.SqlServer.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- Create: `src/neutral/Themia.Content.SqlServer/SqlServerContentDialect.cs`
- Create: `src/neutral/Themia.Content.SqlServer/ServiceCollectionExtensions.cs`
- Modify: the integration-test project, `EngineRegistration.cs`, `ContentEngineFixtures.cs`, and the six test files (append a SQL Server subclass to each)
- Modify: `Themia.sln`

**Interfaces:**
- Produces: `public sealed class SqlServerContentDialect`, `public static IServiceCollection AddThemiaContentSqlServer(this IServiceCollection services, string connectionString)` (namespace `Themia.Content.SqlServer`), `SqlServerContentFixture`, `SqlServerContentCollection` (`Name = "SqlServer Content"`).

- [ ] **Step 1: Add the SQL Server test classes first**

Append to each of the six test files (`SqlServerContentSchemaTests`, `SqlServerContentSaveTests`, `SqlServerContentReadTests`, `SqlServerContentRevertTests`, `SqlServerContentConcurrencyTests`, `SqlServerContentSeedingTests`), for example:

```csharp
[Collection(SqlServerContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerContentSaveTests(SqlServerContentFixture fixture) : ContentSaveTests(fixture);
```

Append to `ContentEngineFixtures.cs` (add `using Testcontainers.MsSql;`, `using Themia.Content.SqlServer;`, `using Themia.Data.Migrations.SqlServer;`):

```csharp
/// <summary>SQL Server: one <c>mssql/server:2022-CU14-ubuntu-22.04</c> container for every SQL Server test class.</summary>
public sealed class SqlServerContentFixture : ContentEngineFixture
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    /// <inheritdoc />
    public override IMigrationEngineAdapter MigrationAdapter => SqlServerMigrationEngine.Adapter;

    /// <inheritdoc />
    protected override async Task<string> StartContainerAsync()
    {
        await container.StartAsync();
        return container.GetConnectionString();
    }

    /// <inheritdoc />
    protected override async Task StopContainerAsync() => await container.DisposeAsync();

    /// <inheritdoc />
    protected override void RegisterEngine(IServiceCollection services, string connectionString) =>
        services.AddThemiaContentSqlServer(connectionString);

    /// <inheritdoc />
    public override Task<bool> TableExistsAsync(string name) =>
        ScalarAsync<bool>(
            "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.tables WHERE name = @Name) THEN 1 ELSE 0 END AS bit);",
            new { Name = name });

    /// <inheritdoc />
    public override Task<bool> IndexExistsAsync(string name) =>
        ScalarAsync<bool>(
            "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = @Name) THEN 1 ELSE 0 END AS bit);",
            new { Name = name });
}

/// <summary>Ties every SQL Server test class to one <see cref="SqlServerContentFixture"/>.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerContentCollection : ICollectionFixture<SqlServerContentFixture>
{
    /// <summary>The collection name test classes reference.</summary>
    public const string Name = "SqlServer Content";
}
```

In `EngineRegistration.Register`, add `MigrationEngineRegistry.Add(SqlServerMigrationEngine.Adapter);` with `using Themia.Data.Migrations.SqlServer;`. In the integration-test project add `Testcontainers.MsSql`, `Microsoft.Data.SqlClient`, and project references to `Themia.Content.SqlServer` and `Themia.Data.Migrations.SqlServer`.

Run: `dotnet build tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj`
Expected: build fails — `Themia.Content.SqlServer` does not exist.

- [ ] **Step 2: Create the SQL Server package**

`src/neutral/Themia.Content.SqlServer/Themia.Content.SqlServer.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Neutral cross-framework package: MUST include net8.0 (cross-framework reuse). -->
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.Content.SqlServer</PackageId>
    <Description>SQL Server dialect + Microsoft.Data.SqlClient driver + FluentMigrator runner for Themia.Content.</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Microsoft.Data.SqlClient" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Content/Themia.Content.csproj" />
    <ProjectReference Include="../Themia.Data.Migrations/Themia.Data.Migrations.csproj" />
    <ProjectReference Include="../Themia.Data.Migrations.SqlServer/Themia.Data.Migrations.SqlServer.csproj" />
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

Both PublicAPI files: `#nullable enable`.

`src/neutral/Themia.Content.SqlServer/SqlServerContentDialect.cs`:

```csharp
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Themia.Content.SqlServer;

/// <summary>SQL Server implementation of <see cref="IContentPageDialect"/> (Microsoft.Data.SqlClient).</summary>
public sealed class SqlServerContentDialect : IContentPageDialect
{
    private const int UniqueConstraintViolation = 2627;
    private const int DuplicateKeyInUniqueIndex = 2601;

    private const string PageColumns =
        "id AS Id, slug AS Slug, language AS Language, title AS Title, markdown AS Markdown, " +
        "current_version AS CurrentVersion, is_published AS IsPublished, created_at AS CreatedAt, " +
        "updated_at AS UpdatedAt, updated_by AS UpdatedBy";

    private const string RevisionColumns =
        "r.version AS Version, r.title AS Title, r.markdown AS Markdown, r.change_summary AS ChangeSummary, " +
        "r.created_at AS CreatedAt, r.created_by AS CreatedBy";

    private readonly string connectionString;

    /// <summary>Creates the dialect over <paramref name="connectionString"/>.</summary>
    /// <param name="connectionString">SQL Server connection string.</param>
    public SqlServerContentDialect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = connectionString;
    }

    /// <inheritdoc />
    public DbConnection CreateConnection() => new SqlConnection(connectionString);

    /// <inheritdoc />
    public bool IsDuplicateKey(DbException exception) =>
        exception is SqlException { Number: UniqueConstraintViolation or DuplicateKeyInUniqueIndex };

    /// <inheritdoc />
    public string InsertPageSql => """
        INSERT INTO content_pages (slug, language, title, markdown, current_version, is_published, created_at, updated_at, updated_by)
        VALUES (@Slug, @Language, @Title, @Markdown, 1, @IsPublished, @Now, @Now, @EditorId);
        """;

    /// <inheritdoc />
    public string ImportPageSql => """
        INSERT INTO content_pages (slug, language, title, markdown, current_version, is_published, created_at, updated_at, updated_by)
        VALUES (@Slug, @Language, @Title, @Markdown, @CurrentVersion, @IsPublished, @CreatedAt, @UpdatedAt, @UpdatedBy);
        """;

    /// <inheritdoc />
    public string UpdatePageIfVersionSql => """
        UPDATE content_pages
           SET title = @Title, markdown = @Markdown, is_published = @IsPublished,
               current_version = @ExpectedVersion + 1, updated_at = @Now, updated_by = @EditorId
         WHERE slug = @Slug AND language = @Language AND current_version = @ExpectedVersion;
        """;

    /// <inheritdoc />
    public string InsertRevisionSql => """
        INSERT INTO content_page_revisions (page_id, version, title, markdown, change_summary, created_at, created_by)
        VALUES (@PageId, @Version, @Title, @Markdown, @ChangeSummary, @CreatedAt, @CreatedBy);
        """;

    /// <inheritdoc />
    public string SelectPageSql =>
        $"SELECT {PageColumns} FROM content_pages WHERE slug = @Slug AND language = @Language;";

    /// <inheritdoc />
    public string SelectPublishedSql => $"""
        SELECT TOP (1) {PageColumns} FROM content_pages
         WHERE slug = @Slug AND is_published = 1 AND language IN (@Language, @Fallback)
         ORDER BY CASE WHEN language = @Language THEN 0 ELSE 1 END;
        """;

    /// <inheritdoc />
    public string SelectRevisionSql => $"""
        SELECT {RevisionColumns}
          FROM content_page_revisions r JOIN content_pages p ON p.id = r.page_id
         WHERE p.slug = @Slug AND p.language = @Language AND r.version = @Version;
        """;

    /// <inheritdoc />
    public string ListPagesSql => """
        SELECT slug AS Slug, language AS Language, title AS Title, current_version AS CurrentVersion,
               is_published AS IsPublished, updated_at AS UpdatedAt, updated_by AS UpdatedBy
          FROM content_pages
         ORDER BY slug, language
        OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
        """;

    /// <inheritdoc />
    public string CountPagesSql => "SELECT COUNT_BIG(*) FROM content_pages;";

    /// <inheritdoc />
    public string ListRevisionsSql => $"""
        SELECT {RevisionColumns}
          FROM content_page_revisions r JOIN content_pages p ON p.id = r.page_id
         WHERE p.slug = @Slug AND p.language = @Language
         ORDER BY r.version DESC
        OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
        """;

    /// <inheritdoc />
    public string CountRevisionsSql => """
        SELECT COUNT_BIG(*) FROM content_page_revisions r JOIN content_pages p ON p.id = r.page_id
         WHERE p.slug = @Slug AND p.language = @Language;
        """;
}
```

`src/neutral/Themia.Content.SqlServer/ServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Content.Migrations;
using Themia.Data.Migrations;
using Themia.Data.Migrations.SqlServer;

namespace Themia.Content.SqlServer;

/// <summary>DI entry point for the SQL Server-backed <c>Themia.Content</c> store.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers <see cref="SqlServerContentDialect"/> as the <see cref="IContentPageDialect"/> and runs
    /// <see cref="ContentSchemaMigration"/> immediately.</summary>
    /// <remarks>Call <c>AddThemiaContent</c> as well, in either order. Content shipped with application code must go
    /// through <c>IContentPageService.SaveAsync</c> after startup, never through SQL (see the package README).</remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">SQL Server connection string.</param>
    public static IServiceCollection AddThemiaContentSqlServer(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.TryAddSingleton<IContentPageDialect>(new SqlServerContentDialect(connectionString));

        // Register on use (coord #0126).
        MigrationEngineRegistry.Add(SqlServerMigrationEngine.Adapter);

        ThemiaMigrations.Run(SqlServerMigrationEngine.Adapter, connectionString, typeof(ContentSchemaMigration).Assembly);

        return services;
    }
}
```

- [ ] **Step 3: Add to the solution, record the API, run the suite**

```bash
dotnet sln Themia.sln add src/neutral/Themia.Content.SqlServer/Themia.Content.SqlServer.csproj
dotnet format analyzers src/neutral/Themia.Content.SqlServer/Themia.Content.SqlServer.csproj --diagnostics RS0016 --severity info
dotnet build Themia.sln
dotnet test tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj
```

Apply the solution-folder check. Expected: every test on all three engines passes. The SQL Server concurrent-create test passing is the proof SqlClient savepoints work. A parse error naming `language` or `version` means one of them is reserved on this engine: quote it with `[...]` in this dialect only, and report it.

- [ ] **Step 4: Commit** (check the time first)

```bash
git add Themia.sln src/neutral/Themia.Content.SqlServer tests/Themia.Content.IntegrationTests
git commit -m "feat(content): add the SQL Server engine"
```

---

### Task 11: Dialect contract tests

**Files:**
- Modify: `tests/Themia.Content.Tests/Themia.Content.Tests.csproj` (reference the three engine projects)
- Test: `tests/Themia.Content.Tests/ContentDialectContractTests.cs`

**Interfaces:**
- Consumes: the three dialects (Tasks 4, 9, 10).

These check the SQL text itself, on every engine, without a database: the parameter contract, the column aliases, and the update guard's shape. A dialect that gets one wrong does not throw — it binds nothing, maps nothing, or refuses nothing.

- [ ] **Step 1: Reference the engines**

Add to the unit-test project:

```xml
    <ProjectReference Include="../../src/neutral/Themia.Content.PostgreSql/Themia.Content.PostgreSql.csproj" />
    <ProjectReference Include="../../src/neutral/Themia.Content.MySql/Themia.Content.MySql.csproj" />
    <ProjectReference Include="../../src/neutral/Themia.Content.SqlServer/Themia.Content.SqlServer.csproj" />
```

- [ ] **Step 2: Write the tests**

`tests/Themia.Content.Tests/ContentDialectContractTests.cs`:

```csharp
using System.Data.Common;
using System.Text.RegularExpressions;
using Themia.Content.MySql;
using Themia.Content.PostgreSql;
using Themia.Content.SqlServer;
using Xunit;

namespace Themia.Content.Tests;

public class ContentDialectContractTests
{
    public static IEnumerable<object[]> Dialects()
    {
        // Never opened: these tests inspect SQL text only.
        yield return new object[] { new PostgresContentDialect("Host=localhost;Database=content_contract") };
        yield return new object[] { new MySqlContentDialect("Server=localhost;Database=content_contract") };
        yield return new object[] { new SqlServerContentDialect("Server=localhost;Database=content_contract") };
    }

    private static readonly Dictionary<string, (Func<IContentPageDialect, string> Sql, string[] Parameters)> Statements = new()
    {
        ["InsertPageSql"] = (d => d.InsertPageSql, ["Slug", "Language", "Title", "Markdown", "IsPublished", "Now", "EditorId"]),
        ["ImportPageSql"] = (d => d.ImportPageSql, ["Slug", "Language", "Title", "Markdown", "CurrentVersion", "IsPublished", "CreatedAt", "UpdatedAt", "UpdatedBy"]),
        ["UpdatePageIfVersionSql"] = (d => d.UpdatePageIfVersionSql, ["Slug", "Language", "Title", "Markdown", "IsPublished", "ExpectedVersion", "Now", "EditorId"]),
        ["InsertRevisionSql"] = (d => d.InsertRevisionSql, ["PageId", "Version", "Title", "Markdown", "ChangeSummary", "CreatedAt", "CreatedBy"]),
        ["SelectPageSql"] = (d => d.SelectPageSql, ["Slug", "Language"]),
        ["SelectPublishedSql"] = (d => d.SelectPublishedSql, ["Slug", "Language", "Fallback"]),
        ["SelectRevisionSql"] = (d => d.SelectRevisionSql, ["Slug", "Language", "Version"]),
        ["ListPagesSql"] = (d => d.ListPagesSql, ["Offset", "Limit"]),
        ["CountPagesSql"] = (d => d.CountPagesSql, []),
        ["ListRevisionsSql"] = (d => d.ListRevisionsSql, ["Slug", "Language", "Offset", "Limit"]),
        ["CountRevisionsSql"] = (d => d.CountRevisionsSql, ["Slug", "Language"]),
    };

    private static readonly string[] PageAliases =
        ["AS Id", "AS Slug", "AS Language", "AS Title", "AS Markdown", "AS CurrentVersion", "AS IsPublished", "AS CreatedAt", "AS UpdatedAt", "AS UpdatedBy"];

    private static readonly string[] RevisionAliases =
        ["AS Version", "AS Title", "AS Markdown", "AS ChangeSummary", "AS CreatedAt", "AS CreatedBy"];

    private static readonly Regex Parameter = new("@([A-Za-z][A-Za-z0-9]*)", RegexOptions.CultureInvariant);

    [Theory]
    [MemberData(nameof(Dialects))]
    public void EveryStatement_ShouldBindExactlyTheServicesParameters(IContentPageDialect dialect)
    {
        foreach (var (name, (sql, expected)) in Statements)
        {
            var bound = Parameter.Matches(sql(dialect)).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
            Assert.True(expected.Order().SequenceEqual(bound), $"{dialect.GetType().Name}.{name} binds [{string.Join(", ", bound)}]");
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void UpdatePageIfVersionSql_ShouldGuardOnTheEditorsExpectedVersion(IContentPageDialect dialect) =>
        Assert.Matches(new Regex(@"WHERE[\s\S]*current_version\s*=\s*@ExpectedVersion", RegexOptions.IgnoreCase), dialect.UpdatePageIfVersionSql);

    [Theory]
    [MemberData(nameof(Dialects))]
    public void PageSelects_ShouldAliasEveryColumn(IContentPageDialect dialect)
    {
        foreach (var alias in PageAliases)
        {
            Assert.Contains(alias, dialect.SelectPageSql, StringComparison.Ordinal);
            Assert.Contains(alias, dialect.SelectPublishedSql, StringComparison.Ordinal);
        }

        foreach (var alias in RevisionAliases)
        {
            Assert.Contains(alias, dialect.SelectRevisionSql, StringComparison.Ordinal);
            Assert.Contains(alias, dialect.ListRevisionsSql, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Statements_ShouldNeitherSelectStarNorQualifyASchema(IContentPageDialect dialect)
    {
        foreach (var (name, (sql, _)) in Statements)
        {
            var text = sql(dialect);
            Assert.DoesNotContain("SELECT *", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(new Regex(@"\b(public|dbo)\.content_", RegexOptions.IgnoreCase), text);
        }
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void SelectPublishedSql_ShouldReturnAtMostOneRow(IContentPageDialect dialect) =>
        Assert.Matches(new Regex(@"LIMIT 1\b|TOP \(1\)", RegexOptions.IgnoreCase), dialect.SelectPublishedSql);

    [Theory]
    [MemberData(nameof(Dialects))]
    public void IsDuplicateKey_ShouldBeFalse_ForAnUnrelatedDatabaseError(IContentPageDialect dialect) =>
        Assert.False(dialect.IsDuplicateKey(new UnrelatedDbException()));

    private sealed class UnrelatedDbException : DbException;
}
```

- [ ] **Step 3: Run them**

Run: `dotnet test tests/Themia.Content.Tests/Themia.Content.Tests.csproj --filter ContentDialectContractTests`
Expected: all pass on both frameworks.

- [ ] **Step 4: Falsify the guard-shape test**

Copy `MySqlContentDialect.cs`, change its `WHERE … current_version = @ExpectedVersion` to `WHERE … current_version = @CurrentVersion`, run the contract tests. Expected: FAIL on both `EveryStatement_ShouldBindExactlyTheServicesParameters` and `UpdatePageIfVersionSql_ShouldGuardOnTheEditorsExpectedVersion` for MySQL only. Record; restore with `cp`; `diff`; re-run to green.

- [ ] **Step 5: Run every unit test in the repository and commit** (check the time first)

```bash
dotnet test Themia.sln --filter "Category!=Integration"
git add tests/Themia.Content.Tests
git commit -m "test(content): pin each dialect's SQL contract"
```

---
## Block 3 — `Themia.Content.AspNetCore`

### Task 12: The package and the public read endpoint

**Files:**
- Create: `src/neutral/Themia.Content.AspNetCore/Themia.Content.AspNetCore.csproj`, `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- Create: `src/neutral/Themia.Content.AspNetCore/ContentHttpResults.cs`
- Create: `src/neutral/Themia.Content.AspNetCore/ContentEndpoints.cs`
- Create: `tests/Themia.Content.AspNetCore.Tests/Themia.Content.AspNetCore.Tests.csproj`
- Create: `tests/Themia.Content.AspNetCore.Tests/FakeContentPageService.cs`
- Create: `tests/Themia.Content.AspNetCore.Tests/ContentTestServer.cs`
- Test: `tests/Themia.Content.AspNetCore.Tests/ContentPublicEndpointTests.cs`
- Modify: `Themia.sln`

**Interfaces:**
- Consumes: `IContentPageService`, `ContentPage`, `ContentSaveResult` (Tasks 3, 5).
- Produces: `public static class ContentEndpoints` (namespace `Themia.Content.AspNetCore`) with `public static RouteGroupBuilder MapThemiaContentPublicEndpoints(this IEndpointRouteBuilder endpoints, string prefix = "/pages")`.
- Produces: `internal static class ContentHttpResults` with `NotFound()`, `Unauthorized()`, `Forbidden()`, `Invalid(IDictionary<string, string[]>)`, `FromSave(ContentSaveResult)`, `Data(object)`, `List<T>(PagedResult<T>, int page, int limit)`.
- Produces for Task 13: `FakeContentPageService`, `ContentTestServer.StartAsync(...)`.

Responses: success is `{ "data": … }` (lists add `"meta": { page, limit, total }`); errors are RFC 7807 (spec §9).

- [ ] **Step 1: Create the test project and its helpers**

`tests/Themia.Content.AspNetCore.Tests/Themia.Content.AspNetCore.Tests.csproj`:

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
    <ProjectReference Include="../../src/neutral/Themia.Content.AspNetCore/Themia.Content.AspNetCore.csproj" />
    <ProjectReference Include="../../src/neutral/Themia.Content/Themia.Content.csproj" />
  </ItemGroup>
</Project>
```

`tests/Themia.Content.AspNetCore.Tests/FakeContentPageService.cs`:

```csharp
namespace Themia.Content.AspNetCore.Tests;

/// <summary>Records what the endpoints asked for and returns canned answers. Tests HTTP mapping only; the service's
/// behaviour is tested against real engines in Themia.Content.IntegrationTests.</summary>
internal sealed class FakeContentPageService : IContentPageService
{
    public int Calls { get; private set; }
    public ContentPage? Page { get; set; }
    public (string Slug, string? Language)? LastPublishedRequest { get; private set; }
    public ContentPageSave? LastSave { get; private set; }
    public ContentPageRevert? LastRevert { get; private set; }
    public (int Page, int Limit)? LastListRequest { get; private set; }
    public ContentSaveResult SaveResult { get; set; } = ContentSaveResult.NotFound();
    public PagedResult<ContentPageSummary> Summaries { get; set; } = new();
    public PagedResult<ContentPageRevision> Revisions { get; set; } = new();

    public static ContentPage SamplePage(string slug = "terms", string language = "th") =>
        new(slug, language, "Terms", "# Terms", 3, true,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero), "editor-a");

    public Task<ContentPage?> GetPublishedAsync(string slug, string? language, CancellationToken ct = default)
    {
        Calls++;
        LastPublishedRequest = (slug, language);
        return Task.FromResult(Page);
    }

    public Task<ContentPage?> GetForEditAsync(string slug, string language, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Page);
    }

    public Task<PagedResult<ContentPageSummary>> ListAsync(int page, int limit, CancellationToken ct = default)
    {
        Calls++;
        LastListRequest = (page, limit);
        return Task.FromResult(Summaries);
    }

    public Task<PagedResult<ContentPageRevision>> GetRevisionsAsync(string slug, string language, int page, int limit, CancellationToken ct = default)
    {
        Calls++;
        LastListRequest = (page, limit);
        return Task.FromResult(Revisions);
    }

    public Task<ContentSaveResult> SaveAsync(ContentPageSave save, CancellationToken ct = default)
    {
        Calls++;
        LastSave = save;
        return Task.FromResult(SaveResult);
    }

    public Task<ContentSaveResult> RevertAsync(ContentPageRevert revert, CancellationToken ct = default)
    {
        Calls++;
        LastRevert = revert;
        return Task.FromResult(SaveResult);
    }
}
```

`tests/Themia.Content.AspNetCore.Tests/ContentTestServer.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Themia.Content.AspNetCore.Tests;

internal static class ContentTestServer
{
    /// <summary>A request carrying this header is authenticated as the header's value (the NameIdentifier claim).</summary>
    public const string UserHeader = "X-Test-User";

    public static async Task<HttpClient> StartAsync(IContentPageService service, Action<IEndpointRouteBuilder> map)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(service);
                });
                web.Configure(app =>
                {
                    app.Use((context, next) =>
                    {
                        if (context.Request.Headers.TryGetValue(UserHeader, out var user))
                        {
                            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                                [new Claim(ClaimTypes.NameIdentifier, user.ToString())], authenticationType: "Test"));
                        }

                        return next(context);
                    });
                    app.UseRouting();
                    app.UseEndpoints(map);
                });
            })
            .StartAsync();
        return host.GetTestClient();
    }
}
```

- [ ] **Step 2: Write the failing public-endpoint tests**

`tests/Themia.Content.AspNetCore.Tests/ContentPublicEndpointTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Xunit;

namespace Themia.Content.AspNetCore.Tests;

public class ContentPublicEndpointTests
{
    [Fact]
    public async Task Get_ShouldReturnThePageInADataEnvelope_WhenPublished()
    {
        var service = new FakeContentPageService { Page = FakeContentPageService.SamplePage() };
        var client = await ContentTestServer.StartAsync(service, e => e.MapThemiaContentPublicEndpoints("/api/v1/pages"));

        var response = await client.GetAsync("/api/v1/pages/terms?lang=en");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("terms", json.RootElement.GetProperty("data").GetProperty("slug").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("data").GetProperty("currentVersion").GetInt32());
        Assert.Equal(("terms", "en"), service.LastPublishedRequest);
    }

    [Fact]
    public async Task Get_ShouldPassNoLanguage_WhenTheQueryHasNone()
    {
        var service = new FakeContentPageService { Page = FakeContentPageService.SamplePage() };
        var client = await ContentTestServer.StartAsync(service, e => e.MapThemiaContentPublicEndpoints("/api/v1/pages"));

        await client.GetAsync("/api/v1/pages/terms");

        Assert.Equal(("terms", (string?)null), service.LastPublishedRequest);
    }

    [Fact]
    public async Task Get_ShouldReturnProblemDetails404_WhenNoPageIsPublished()
    {
        var client = await ContentTestServer.StartAsync(new FakeContentPageService(), e => e.MapThemiaContentPublicEndpoints("/api/v1/pages"));

        var response = await client.GetAsync("/api/v1/pages/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }
}
```

Run: `dotnet test tests/Themia.Content.AspNetCore.Tests/Themia.Content.AspNetCore.Tests.csproj`
Expected: build fails — `Themia.Content.AspNetCore` does not exist.

- [ ] **Step 3: Create the package**

`src/neutral/Themia.Content.AspNetCore/Themia.Content.AspNetCore.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Neutral cross-framework package: MUST include net8.0 (cross-framework reuse). -->
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.Content.AspNetCore</PackageId>
    <Description>Optional minimal-API endpoints for Themia.Content: a public read with language fallback, and admin list, edit, revisions and revert routes behind a consumer-supplied, fail-closed authorization delegate.</Description>
    <PackageTags>themia;content;cms;aspnetcore</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Themia.Content/Themia.Content.csproj" />
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Content.AspNetCore.Tests" />
  </ItemGroup>
</Project>
```

Both PublicAPI files: `#nullable enable`.

`src/neutral/Themia.Content.AspNetCore/ContentHttpResults.cs`:

```csharp
using Microsoft.AspNetCore.Http;

namespace Themia.Content.AspNetCore;

/// <summary>The single map from content outcomes to HTTP. Success is <c>{ data, meta }</c>; errors are RFC 7807.</summary>
internal static class ContentHttpResults
{
    public static IResult Data(object value) => Results.Ok(new { data = value });

    public static IResult List<T>(PagedResult<T> result, int page, int limit) =>
        Results.Ok(new { data = result.Items, meta = new { page, limit, total = result.Total } });

    public static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Content page not found");

    public static IResult Unauthorized() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Authentication required");

    public static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not allowed to manage content pages");

    public static IResult Invalid(IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, statusCode: StatusCodes.Status422UnprocessableEntity);

    public static IResult FromSave(ContentSaveResult result) => result.Outcome switch
    {
        ContentSaveOutcome.Saved => Data(result.Page!),
        ContentSaveOutcome.Invalid => Invalid(result.Errors
            .GroupBy(e => e.Field, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal)),
        ContentSaveOutcome.Conflict => Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Version conflict",
            detail: "The page has been saved since this editor loaded it. Reload it and apply the change again.",
            extensions: new Dictionary<string, object?>
            {
                ["currentVersion"] = result.CurrentVersion,
                ["updatedBy"] = result.CurrentUpdatedBy,
                ["updatedAt"] = result.CurrentUpdatedAt,
            }),
        ContentSaveOutcome.NotFound => NotFound(),
#pragma warning disable CS8524 // Unnamed enum values: a new named outcome must still break this switch at compile time.
    };
#pragma warning restore CS8524
}
```

`src/neutral/Themia.Content.AspNetCore/ContentEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Themia.Content.AspNetCore;

/// <summary>Maps Themia.Content's HTTP endpoints. A consumer that keeps its own controllers maps neither.</summary>
public static class ContentEndpoints
{
    /// <summary>Maps <c>GET {prefix}/{slug}?lang=</c>: the published page in the requested language, else the fallback
    /// language, else 404. No authorization. Caching is the consumer's decision.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">The route prefix, including any API version segment (for example <c>/api/v1/pages</c>).</param>
    /// <returns>The route group, for further configuration.</returns>
    public static RouteGroupBuilder MapThemiaContentPublicEndpoints(this IEndpointRouteBuilder endpoints, string prefix = "/pages")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var group = endpoints.MapGroup(prefix);
        group.MapGet("{slug}", async (string slug, string? lang, IContentPageService service, CancellationToken ct) =>
        {
            var page = await service.GetPublishedAsync(slug, lang, ct).ConfigureAwait(false);
            return page is null ? ContentHttpResults.NotFound() : ContentHttpResults.Data(page);
        });
        return group;
    }
}
```

- [ ] **Step 4: Add to the solution, record the API, run the tests**

```bash
dotnet sln Themia.sln add src/neutral/Themia.Content.AspNetCore/Themia.Content.AspNetCore.csproj tests/Themia.Content.AspNetCore.Tests/Themia.Content.AspNetCore.Tests.csproj
dotnet format analyzers src/neutral/Themia.Content.AspNetCore/Themia.Content.AspNetCore.csproj --diagnostics RS0016 --severity info
dotnet build Themia.sln
dotnet test tests/Themia.Content.AspNetCore.Tests/Themia.Content.AspNetCore.Tests.csproj
```

Apply the solution-folder check. Expected: 3 passed.

- [ ] **Step 5: Commit** (check the time first)

```bash
git add Themia.sln src/neutral/Themia.Content.AspNetCore tests/Themia.Content.AspNetCore.Tests
git commit -m "feat(content): add the public content read endpoint"
```

---

### Task 13: Admin endpoints behind a fail-closed delegate

**Files:**
- Create: `src/neutral/Themia.Content.AspNetCore/ContentAdminOptions.cs`
- Create: `src/neutral/Themia.Content.AspNetCore/ContentRequests.cs`
- Modify: `src/neutral/Themia.Content.AspNetCore/ContentEndpoints.cs` (add the admin mapping)
- Test: `tests/Themia.Content.AspNetCore.Tests/ContentAdminEndpointTests.cs`
- Modify: `src/neutral/Themia.Content.AspNetCore/PublicAPI.Unshipped.txt`

**Interfaces:**
- Consumes: `ContentHttpResults`, `FakeContentPageService`, `ContentTestServer` (Task 12).
- Produces: `public sealed class ContentAdminOptions { Func<HttpContext, Task<bool>>? Authorize { get; set; } Func<HttpContext, string?> ResolveEditorId { get; set; } }`.
- Produces: `public static RouteGroupBuilder MapThemiaContentAdminEndpoints(this IEndpointRouteBuilder endpoints, ContentAdminOptions options, string prefix = "/admin/content")` with routes:
  `GET pages?page=&limit=`, `GET pages/{slug}/{language}`, `PUT pages/{slug}/{language}`, `GET pages/{slug}/{language}/revisions?page=&limit=`, `POST pages/{slug}/{language}/revert`.
- Produces: `internal sealed record SavePageRequest(string Title, string Markdown, bool IsPublished, int ExpectedVersion, string? ChangeSummary)`, `internal sealed record RevertPageRequest(int Version, int ExpectedVersion, string? ChangeSummary)`.

Refusals are 401 when unauthenticated and 403 when authenticated — not the route-hiding 404 the dashboards use, because this is a JSON API (spec §9). An unset or throwing `Authorize` refuses. A body that omits `expectedVersion` binds as 0, which for an existing page is a conflict — the fail-closed behaviour propertiezy shipped (coord #0130 [3]).

- [ ] **Step 1: Write the failing tests**

`tests/Themia.Content.AspNetCore.Tests/ContentAdminEndpointTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Themia.Content.AspNetCore.Tests;

public class ContentAdminEndpointTests
{
    private const string Prefix = "/api/v1/admin/content";

    private static readonly ContentAdminOptions AllowAll = new() { Authorize = _ => Task.FromResult(true) };

    private static Task<HttpClient> StartAsync(FakeContentPageService service, ContentAdminOptions options) =>
        ContentTestServer.StartAsync(service, e => e.MapThemiaContentAdminEndpoints(options, Prefix));

    private static HttpRequestMessage Request(HttpMethod method, string path, string? json = null, string? user = "user-7")
    {
        var request = new HttpRequestMessage(method, Prefix + path);
        if (user is not null)
        {
            request.Headers.Add(ContentTestServer.UserHeader, user);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private const string ValidBody = """{"title":"Terms","markdown":"# Terms","isPublished":true,"expectedVersion":3,"changeSummary":"wording"}""";

    [Fact]
    public async Task AdminRoutes_ShouldRefuseAnonymousWith401_WhenAuthorizeIsUnset()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, new ContentAdminOptions());

        var response = await client.SendAsync(Request(HttpMethod.Get, "/pages", user: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task AdminRoutes_ShouldRefuseAuthenticatedWith403_WhenAuthorizeIsUnset()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, new ContentAdminOptions());

        var response = await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/th", ValidBody));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task AdminRoutes_ShouldRefuseWith403_WhenAuthorizeReturnsFalse()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, new ContentAdminOptions { Authorize = _ => Task.FromResult(false) });

        var response = await client.SendAsync(Request(HttpMethod.Get, "/pages"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task AdminRoutes_ShouldFailClosed_WhenAuthorizeThrows()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, new ContentAdminOptions { Authorize = _ => throw new InvalidOperationException("broken") });

        var response = await client.SendAsync(Request(HttpMethod.Get, "/pages"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Put_ShouldSaveWithTheRouteKeyTheBodyAndTheEditorFromNameIdentifier()
    {
        var service = new FakeContentPageService { SaveResult = ContentSaveResult.Saved(FakeContentPageService.SamplePage()) };
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/en", ValidBody));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ContentPageSave("terms", "en", "Terms", "# Terms", true, 3, "wording", "user-7"), service.LastSave);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("terms", json.RootElement.GetProperty("data").GetProperty("slug").GetString());
    }

    [Fact]
    public async Task Put_ShouldUseResolveEditorId_WhenOverridden()
    {
        var service = new FakeContentPageService { SaveResult = ContentSaveResult.Saved(FakeContentPageService.SamplePage()) };
        var options = new ContentAdminOptions { Authorize = _ => Task.FromResult(true), ResolveEditorId = _ => "resolved" };
        var client = await StartAsync(service, options);

        await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/th", ValidBody));

        Assert.Equal("resolved", service.LastSave!.EditorId);
    }

    [Fact]
    public async Task Put_ShouldReturn422WithTheMarkdownField_WhenInvalid()
    {
        var service = new FakeContentPageService
        {
            SaveResult = ContentSaveResult.Invalid([new ContentValidationError("markdown", "Markdown must not contain raw HTML.")]),
        };
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/th", ValidBody));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Markdown must not contain raw HTML.", json.RootElement.GetProperty("errors").GetProperty("markdown")[0].GetString());
    }

    [Fact]
    public async Task Put_ShouldReturn409WithTheCurrentState_WhenConflict()
    {
        var updatedAt = new DateTimeOffset(2026, 9, 14, 3, 0, 0, TimeSpan.Zero);
        var service = new FakeContentPageService { SaveResult = ContentSaveResult.Conflict(4, "editor-b", updatedAt) };
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/th", ValidBody));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(4, json.RootElement.GetProperty("currentVersion").GetInt32());
        Assert.Equal("editor-b", json.RootElement.GetProperty("updatedBy").GetString());
        Assert.Equal(updatedAt, json.RootElement.GetProperty("updatedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Put_ShouldReturn404_WhenNotFound()
    {
        var client = await StartAsync(new FakeContentPageService { SaveResult = ContentSaveResult.NotFound() }, AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/th", ValidBody));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_ShouldReturn400AndNotSave_WhenTheBodyHasAnUnknownMember()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(
            HttpMethod.Put, "/pages/terms/th",
            """{"title":"T","markdown":"# T","isPublished":true,"expectedVersion":0,"bogus":1}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(service.LastSave);
    }

    [Fact]
    public async Task Put_ShouldSendExpectedVersionZero_WhenTheBodyOmitsIt()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, AllowAll);

        await client.SendAsync(Request(HttpMethod.Put, "/pages/terms/th", """{"title":"T","markdown":"# T","isPublished":true}"""));

        Assert.Equal(0, service.LastSave!.ExpectedVersion);
    }

    [Fact]
    public async Task List_ShouldReturnDataAndMeta()
    {
        var summary = new ContentPageSummary("terms", "th", "Terms", 2, true, DateTimeOffset.UnixEpoch, null);
        var service = new FakeContentPageService { Summaries = new PagedResult<ContentPageSummary> { Items = [summary], Total = 41 } };
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/pages?page=3&limit=20"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((3, 20), service.LastListRequest);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var meta = json.RootElement.GetProperty("meta");
        Assert.Equal(3, meta.GetProperty("page").GetInt32());
        Assert.Equal(20, meta.GetProperty("limit").GetInt32());
        Assert.Equal(41, meta.GetProperty("total").GetInt32());
        Assert.Equal("terms", json.RootElement.GetProperty("data")[0].GetProperty("slug").GetString());
    }

    [Fact]
    public async Task List_ShouldDefaultToPageOneAndTwentyItems()
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, AllowAll);

        await client.SendAsync(Request(HttpMethod.Get, "/pages"));

        Assert.Equal((1, 20), service.LastListRequest);
    }

    [Theory]
    [InlineData("/pages?limit=0", "limit")]
    [InlineData("/pages?limit=101", "limit")]
    [InlineData("/pages?page=0", "page")]
    [InlineData("/pages/terms/th/revisions?limit=101", "limit")]
    public async Task Paging_ShouldReturn422_WhenOutOfRange(string path, string field)
    {
        var service = new FakeContentPageService();
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Get, path));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task GetPage_ShouldReturn404_WhenMissing()
    {
        var client = await StartAsync(new FakeContentPageService(), AllowAll);

        var response = await client.SendAsync(Request(HttpMethod.Get, "/pages/terms/th"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Revert_ShouldMapTheBodysVersionToTheTargetVersion()
    {
        var service = new FakeContentPageService { SaveResult = ContentSaveResult.Saved(FakeContentPageService.SamplePage()) };
        var client = await StartAsync(service, AllowAll);

        var response = await client.SendAsync(Request(
            HttpMethod.Post, "/pages/terms/en/revert", """{"version":1,"expectedVersion":3,"changeSummary":null}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new ContentPageRevert("terms", "en", 1, 3, null, "user-7"), service.LastRevert);
    }
}
```

Run: `dotnet test tests/Themia.Content.AspNetCore.Tests/Themia.Content.AspNetCore.Tests.csproj --filter ContentAdminEndpointTests`
Expected: build fails — `ContentAdminOptions` does not exist.

- [ ] **Step 2: Implement the options and requests**

`src/neutral/Themia.Content.AspNetCore/ContentAdminOptions.cs`:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Themia.Content.AspNetCore;

/// <summary>Configures the admin endpoints mapped by <see cref="ContentEndpoints.MapThemiaContentAdminEndpoints"/>.</summary>
public sealed class ContentAdminOptions
{
    /// <summary>
    /// Decides whether a request may manage content pages. <b>Unset means every admin request is refused</b>, and so
    /// does a delegate that throws. Themia ships no permission catalog: check the consumer's own permission here — a
    /// policy is one <c>IAuthorizationService.AuthorizeAsync</c> call. The returned route group also accepts
    /// <c>RequireAuthorization(policy)</c>.
    /// </summary>
    public Func<HttpContext, Task<bool>>? Authorize { get; set; }

    /// <summary>Resolves the id stored as a revision's author. Defaults to the <see cref="ClaimTypes.NameIdentifier"/>
    /// claim.</summary>
    public Func<HttpContext, string?> ResolveEditorId { get; set; } =
        context => context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
```

`src/neutral/Themia.Content.AspNetCore/ContentRequests.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Themia.Content.AspNetCore;

/// <summary>The body of <c>PUT pages/{slug}/{language}</c>. Unknown members are refused.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SavePageRequest(string Title, string Markdown, bool IsPublished, int ExpectedVersion, string? ChangeSummary);

/// <summary>The body of <c>POST pages/{slug}/{language}/revert</c>. Unknown members are refused.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RevertPageRequest(int Version, int ExpectedVersion, string? ChangeSummary);
```

- [ ] **Step 3: Add the admin mapping**

In `ContentEndpoints.cs`, add `using Microsoft.Extensions.DependencyInjection;` and `using Microsoft.Extensions.Logging;`, and add to the class:

```csharp
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const string LoggerCategory = "Themia.Content.AspNetCore";

    /// <summary>
    /// Maps the admin routes under <paramref name="prefix"/>: <c>GET pages</c>, <c>GET pages/{slug}/{language}</c>,
    /// <c>PUT pages/{slug}/{language}</c>, <c>GET pages/{slug}/{language}/revisions</c> and
    /// <c>POST pages/{slug}/{language}/revert</c>.
    /// </summary>
    /// <remarks>Every route runs <see cref="ContentAdminOptions.Authorize"/> first and is refused — 401 when the request is
    /// unauthenticated, 403 otherwise — when it is unset, returns <see langword="false"/> or throws.</remarks>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="options">Authorization and editor resolution.</param>
    /// <param name="prefix">The route prefix, including any API version segment.</param>
    /// <returns>The route group, for further configuration.</returns>
    public static RouteGroupBuilder MapThemiaContentAdminEndpoints(
        this IEndpointRouteBuilder endpoints, ContentAdminOptions options, string prefix = "/admin/content")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var logger = endpoints.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory);
        if (options.Authorize is null)
        {
            logger?.LogWarning(
                "Content admin endpoints mounted at {Prefix} without an Authorize delegate; every request is refused.", prefix);
        }

        var group = endpoints.MapGroup(prefix);
        group.AddEndpointFilter((context, next) => AuthorizeAsync(context, next, options, logger));

        group.MapGet("pages", async (int? page, int? limit, IContentPageService service, CancellationToken ct) =>
            await PagedAsync(page, limit, (p, l) => service.ListAsync(p, l, ct)).ConfigureAwait(false));

        group.MapGet("pages/{slug}/{language}", async (string slug, string language, IContentPageService service, CancellationToken ct) =>
        {
            var found = await service.GetForEditAsync(slug, language, ct).ConfigureAwait(false);
            return found is null ? ContentHttpResults.NotFound() : ContentHttpResults.Data(found);
        });

        group.MapPut("pages/{slug}/{language}", async (
            string slug, string language, SavePageRequest body, HttpContext http, IContentPageService service, CancellationToken ct) =>
            ContentHttpResults.FromSave(await service.SaveAsync(
                new ContentPageSave(slug, language, body.Title, body.Markdown, body.IsPublished, body.ExpectedVersion,
                    body.ChangeSummary, options.ResolveEditorId(http)),
                ct).ConfigureAwait(false)));

        group.MapGet("pages/{slug}/{language}/revisions", async (
            string slug, string language, int? page, int? limit, IContentPageService service, CancellationToken ct) =>
            await PagedAsync(page, limit, (p, l) => service.GetRevisionsAsync(slug, language, p, l, ct)).ConfigureAwait(false));

        group.MapPost("pages/{slug}/{language}/revert", async (
            string slug, string language, RevertPageRequest body, HttpContext http, IContentPageService service, CancellationToken ct) =>
            ContentHttpResults.FromSave(await service.RevertAsync(
                new ContentPageRevert(slug, language, body.Version, body.ExpectedVersion, body.ChangeSummary, options.ResolveEditorId(http)),
                ct).ConfigureAwait(false)));

        return group;
    }

    private static async ValueTask<object?> AuthorizeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next, ContentAdminOptions options, ILogger? logger)
    {
        var http = context.HttpContext;
        bool allowed;
        try
        {
            allowed = options.Authorize is not null && await options.Authorize(http).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A broken delegate must not open the routes. Logged once here; the request is refused below.
            logger?.LogWarning(exception, "Content admin Authorize delegate threw; the request is refused.");
            allowed = false;
        }

        if (allowed)
        {
            return await next(context).ConfigureAwait(false);
        }

        return http.User.Identity?.IsAuthenticated == true ? ContentHttpResults.Forbidden() : ContentHttpResults.Unauthorized();
    }

    private static async Task<IResult> PagedAsync<T>(int? page, int? limit, Func<int, int, Task<PagedResult<T>>> query)
    {
        var p = page ?? 1;
        var l = limit ?? DefaultPageSize;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (p < 1 || p > int.MaxValue / MaxPageSize)
        {
            errors["page"] = ["Page must be 1 or greater."];
        }

        if (l < 1 || l > MaxPageSize)
        {
            errors["limit"] = [$"Limit must be between 1 and {MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            return ContentHttpResults.Invalid(errors);
        }

        return ContentHttpResults.List(await query(p, l).ConfigureAwait(false), p, l);
    }
```

- [ ] **Step 4: Record the API and run the tests**

```bash
dotnet format analyzers src/neutral/Themia.Content.AspNetCore/Themia.Content.AspNetCore.csproj --diagnostics RS0016 --severity info
dotnet build Themia.sln
dotnet test tests/Themia.Content.AspNetCore.Tests/Themia.Content.AspNetCore.Tests.csproj
```

Expected: every test passes. If `Put_ShouldReturn400AndNotSave_WhenTheBodyHasAnUnknownMember` returns 200, the attribute is not reaching the minimal-API serializer on this framework: stop and report, do not delete the test.

- [ ] **Step 5: Falsify the authorization filter**

Copy `ContentEndpoints.cs`, delete the `group.AddEndpointFilter(...)` line, run the admin tests. Expected: FAIL — the four `AdminRoutes_Should…` tests. Record; restore with `cp`; `diff`; re-run to green.

- [ ] **Step 6: Commit** (check the time first)

```bash
git add src/neutral/Themia.Content.AspNetCore tests/Themia.Content.AspNetCore.Tests
git commit -m "feat(content): add admin endpoints behind a fail-closed delegate"
```

---
## Block 4 — golden fixture and documentation

### Task 14: The markdown dialect fixture

**Files:**
- Create: `tests/Themia.Content.Tests/Fixtures/markdown-dialect.json`
- Modify: `tests/Themia.Content.Tests/Themia.Content.Tests.csproj` (copy the fixture to the output)
- Test: `tests/Themia.Content.Tests/MarkdownDialectFixtureTests.cs`
- Modify: `src/neutral/Themia.Content/README.md` (append the renderer contract)

**Interfaces:**
- Consumes: `ContentMarkdownRules.Check` (Task 2).
- Produces: the fixture both web apps copy byte-for-byte (spec §8). Entry shape: `{ "name", "status", "markdown", "write", "html" }`; `status` is `candidate` or `confirmed`; `write` is `accept` or `reject`.

Themia's suite verifies `write` for every entry. `html` is the output of `marked` 18.x with the configuration in spec §8; every entry ships as `candidate` and is promoted to `confirmed` only when both web apps reproduce it (the HMAC-vector lifecycle). Themia's CI never runs Node.

- [ ] **Step 1: Write the fixture**

`tests/Themia.Content.Tests/Fixtures/markdown-dialect.json` — the 24 seed entries of spec §8 and the 5 added in review, with `html` exactly as `marked` 18.0.11 produced it:

```json
{
  "$comment": "Themia.Content markdown dialect. 'write' is the verdict of ContentMarkdownRules.Check (asserted by Themia.Content.Tests). 'html' is marked 18.x with the renderer configuration in docs/superpowers/specs/2026-09-14-themia-content-design.md section 8; an entry becomes 'confirmed' only when both consumer web apps reproduce it byte-for-byte. Copy this file unchanged.",
  "renderer": "marked@18 with the Themia.Content renderer configuration",
  "entries": [
    { "name": "anchor", "status": "candidate", "markdown": "## Cookies {#cookies}", "write": "accept", "html": "<h2 id=\"cookies\">Cookies</h2>\n" },
    { "name": "thai anchor", "status": "candidate", "markdown": "## คุกกี้ {#cookies}", "write": "accept", "html": "<h2 id=\"cookies\">คุกกี้</h2>\n" },
    { "name": "https link", "status": "candidate", "markdown": "[x](https://a.example/p?q=1#f)", "write": "accept", "html": "<p><a href=\"https://a.example/p?q=1#f\">x</a></p>\n" },
    { "name": "relative link", "status": "candidate", "markdown": "[x](/privacy#cookies)", "write": "accept", "html": "<p><a href=\"/privacy#cookies\">x</a></p>\n" },
    { "name": "fragment link", "status": "candidate", "markdown": "[x](#cookies)", "write": "accept", "html": "<p><a href=\"#cookies\">x</a></p>\n" },
    { "name": "colon in relative path", "status": "candidate", "markdown": "[x](/a:b)", "write": "accept", "html": "<p><a href=\"/a:b\">x</a></p>\n" },
    { "name": "mailto autolink", "status": "candidate", "markdown": "<mailto:privacy@a.example>", "write": "accept", "html": "<p><a href=\"mailto:privacy@a.example\">mailto:privacy@a.example</a></p>\n" },
    { "name": "email autolink", "status": "candidate", "markdown": "<a@b.com>", "write": "accept", "html": "<p><a href=\"mailto:a@b.com\">a@b.com</a></p>\n" },
    { "name": "tel link", "status": "candidate", "markdown": "[call](tel:+6621234567)", "write": "accept", "html": "<p><a href=\"tel:+6621234567\">call</a></p>\n" },
    { "name": "javascript link", "status": "candidate", "markdown": "[x](javascript:alert(1))", "write": "reject", "html": "<p>x</p>\n" },
    { "name": "javascript upper", "status": "candidate", "markdown": "[x](JAVASCRIPT:alert(1))", "write": "reject", "html": "<p>x</p>\n" },
    { "name": "entity-encoded colon", "status": "candidate", "markdown": "[x](javascript&#58;alert(1))", "write": "reject", "html": "<p>x</p>\n" },
    { "name": "named-entity colon", "status": "candidate", "markdown": "[x](javascript&colon;alert(1))", "write": "reject", "html": "<p>x</p>\n" },
    { "name": "angle destination", "status": "candidate", "markdown": "[x](<javascript:alert(1)>)", "write": "reject", "html": "<p>x</p>\n" },
    { "name": "reference definition", "status": "candidate", "markdown": "[x][r]\n\n[r]: javascript:alert(1)", "write": "reject", "html": "<p>x</p>\n" },
    { "name": "vbscript link", "status": "candidate", "markdown": "[x](vbscript:msgbox(1))", "write": "reject", "html": "<p>x</p>\n" },
    { "name": "data image", "status": "candidate", "markdown": "![x](data:text/html;base64,PHNjcmlwdD4=)", "write": "reject", "html": "<p></p>\n" },
    { "name": "tab in scheme", "status": "candidate", "markdown": "[x](java\tscript:alert(1))", "write": "accept", "html": "<p>[x](java\tscript:alert(1))</p>\n" },
    { "name": "script inline", "status": "candidate", "markdown": "a <script>x</script> b", "write": "reject", "html": "<p>a x b</p>\n" },
    { "name": "html comment", "status": "candidate", "markdown": "a <!-- hidden --> b", "write": "reject", "html": "<p>a  b</p>\n" },
    { "name": "fence with html", "status": "candidate", "markdown": "```\n<b>x</b>\n```", "write": "accept", "html": "<pre><code>&lt;b&gt;x&lt;/b&gt;\n</code></pre>\n" },
    { "name": "unclosed fence", "status": "candidate", "markdown": "```\n<b>x</b>", "write": "accept", "html": "<pre><code>&lt;b&gt;x&lt;/b&gt;\n</code></pre>\n" },
    { "name": "double-tick code", "status": "candidate", "markdown": "x ``a`<b>`` y", "write": "accept", "html": "<p>x <code>a`&lt;b&gt;</code> y</p>\n" },
    { "name": "html in inline code", "status": "candidate", "markdown": "use `<br>` here", "write": "accept", "html": "<p>use <code>&lt;br&gt;</code> here</p>\n" },
    { "name": "indented code with html", "status": "candidate", "markdown": "para\n\n    <b>x</b>\n", "write": "accept", "html": "<p>para</p>\n<pre><code>&lt;b&gt;x&lt;/b&gt;\n</code></pre>\n" },
    { "name": "longer closing fence, then javascript link", "status": "candidate", "markdown": "```\ncode\n````\n\n[x](javascript:alert(1))", "write": "reject", "html": "<pre><code>code\n</code></pre>\n<p>x</p>\n" },
    { "name": "blockquote fence with html", "status": "candidate", "markdown": "> ```\n> <b>x</b>\n> ```", "write": "accept", "html": "<blockquote>\n<pre><code>&lt;b&gt;x&lt;/b&gt;\n</code></pre>\n</blockquote>\n" },
    { "name": "entity-encoded tab in scheme", "status": "candidate", "markdown": "[x](java&#9;script:alert(1))", "write": "reject", "html": "<p>x</p>\n" },
    { "name": "hex entities without semicolons", "status": "candidate", "markdown": "[x](&#x6A&#x61vascript:alert(1))", "write": "reject", "html": "<p>x</p>\n" }
  ]
}
```

In the unit-test project add:

```xml
  <ItemGroup>
    <None Update="Fixtures/markdown-dialect.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 2: Prove the `html` values were not mistyped**

The values above were typed from probe output. Regenerate them from the renderer before anything depends on them. In a scratch directory **outside the repository**:

```bash
mkdir -p /tmp/content-fixture-check && cd /tmp/content-fixture-check
echo '{"type":"module"}' > package.json
npm install --silent --no-audit --no-fund marked@18
cp "$OLDPWD/tests/Themia.Content.Tests/Fixtures/markdown-dialect.json" fixture.json
cat > check.mjs <<'EOF'
import { readFileSync } from 'node:fs';
import { Marked } from 'marked';

const ALLOWED_SCHEMES = new Set(['http', 'https', 'mailto', 'tel']);
const decodeEntities = s => s
  .replace(/&#x([0-9a-f]+);?/gi, (_, h) => String.fromCodePoint(parseInt(h, 16)))
  .replace(/&#([0-9]+);?/g, (_, d) => String.fromCodePoint(parseInt(d, 10)))
  .replace(/&colon;/gi, ':').replace(/&tab;/gi, '\t').replace(/&newline;/gi, '\n').replace(/&amp;/gi, '&');
const urlAllowed = raw => {
  const u = Array.from(decodeEntities(String(raw ?? '')))
    .filter(c => { const n = c.codePointAt(0) ?? 0; return n > 0x20 && n !== 0x7f; })
    .join('');
  const m = /^([a-z][a-z0-9+.-]*):/i.exec(u);
  return !m || ALLOWED_SCHEMES.has(m[1].toLowerCase());
};
const EXPLICIT_ANCHOR = /\s*\{#([a-z0-9][a-z0-9-]*)\}\s*$/i;
const cms = new Marked({ renderer: {
  html: () => '',
  heading(t) {
    const m = EXPLICIT_ANCHOR.exec(t.text);
    const h = this.parser.parseInline(t.tokens);
    return m ? `<h${t.depth} id="${m[1].toLowerCase()}">${h.replace(EXPLICIT_ANCHOR, '')}</h${t.depth}>\n` : `<h${t.depth}>${h}</h${t.depth}>\n`;
  },
  link(t) { return urlAllowed(t.href) ? false : this.parser.parseInline(t.tokens); },
  image(t) { return urlAllowed(t.href) ? false : ''; },
} });

let bad = 0;
for (const e of JSON.parse(readFileSync('fixture.json', 'utf8')).entries) {
  const html = cms.parse(e.markdown);
  if (html !== e.html) { bad++; console.log(`MISMATCH ${e.name}\n  fixture: ${JSON.stringify(e.html)}\n  marked:  ${JSON.stringify(html)}`); }
}
console.log(bad === 0 ? 'all html values match marked' : `${bad} mismatch(es)`);
EOF
node check.mjs; cd "$OLDPWD"
```

Expected: `all html values match marked`. On a mismatch, correct the fixture to the value `marked` produced and say so in the task report. Do not commit anything from `/tmp/content-fixture-check`.

- [ ] **Step 3: Write the failing test**

`tests/Themia.Content.Tests/MarkdownDialectFixtureTests.cs`:

```csharp
using System.Text.Json;
using Xunit;

namespace Themia.Content.Tests;

// The contract both consumer web apps copy. A failure here means Themia's write rule and the pinned renderer
// disagree about an input — a spec decision, not an expected value to adjust.
public class MarkdownDialectFixtureTests
{
    public sealed record Entry(string Name, string Status, string Markdown, string Write, string Html);

    private static IReadOnlyList<Entry> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "markdown-dialect.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => new Entry(
                e.GetProperty("name").GetString()!,
                e.GetProperty("status").GetString()!,
                e.GetProperty("markdown").GetString()!,
                e.GetProperty("write").GetString()!,
                e.GetProperty("html").GetString()!))
            .ToList();
    }

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var entry in Load())
        {
            data.Add(entry.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void WriteVerdict_ShouldMatchTheFixture(string name)
    {
        var entry = Load().Single(e => e.Name == name);
        var accepted = ContentMarkdownRules.Check(entry.Markdown).Count == 0;
        Assert.Equal(entry.Write == "accept", accepted);
    }

    [Fact]
    public void Fixture_ShouldBeWellFormed()
    {
        var entries = Load();
        Assert.Equal(29, entries.Count);
        Assert.Equal(entries.Count, entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, e => Assert.Contains(e.Status, new[] { "candidate", "confirmed" }));
        Assert.All(entries, e => Assert.Contains(e.Write, new[] { "accept", "reject" }));
    }
}
```

- [ ] **Step 4: Run it**

Run: `dotnet test tests/Themia.Content.Tests/Themia.Content.Tests.csproj --filter MarkdownDialectFixtureTests`
Expected: 30 passed per framework. "tab in scheme" is the one entry whose write verdict no earlier test runs through Markdig. If any entry fails, **stop and report** the entry and Markdig's node types for it — do not change the verdict; it is a spec decision.

- [ ] **Step 5: Append the renderer contract to the README**

Append to `src/neutral/Themia.Content/README.md`:

````markdown
## Rendering — the web half

Themia refuses raw HTML and unsafe link schemes at write. **The web renderer is still the safety boundary**: it must
drop raw HTML and refuse the same schemes again, because the write rule cannot fix rows saved before it existed.

Render with **`marked` 18.x and exactly this configuration**. The golden fixture pins its bytes; `markdown-it`
matched on 5 of 12 inputs and cannot reproduce it.

```ts
import { Marked } from 'marked';

const ALLOWED_SCHEMES = new Set(['http', 'https', 'mailto', 'tel']);

function decodeEntities(s: string): string {
  return s
    .replace(/&#x([0-9a-f]+);?/gi, (_, h) => String.fromCodePoint(parseInt(h, 16)))
    .replace(/&#([0-9]+);?/g, (_, d) => String.fromCodePoint(parseInt(d, 10)))
    .replace(/&colon;/gi, ':')
    .replace(/&tab;/gi, '\t')
    .replace(/&newline;/gi, '\n')
    .replace(/&amp;/gi, '&');
}

export function urlAllowed(raw: string | null | undefined): boolean {
  const u = Array.from(decodeEntities(String(raw ?? '')))
    .filter((c) => { const n = c.codePointAt(0) ?? 0; return n > 0x20 && n !== 0x7f; })
    .join('');
  const m = /^([a-z][a-z0-9+.-]*):/i.exec(u);
  return !m || ALLOWED_SCHEMES.has(m[1].toLowerCase());
}

const EXPLICIT_ANCHOR = /\s*\{#([a-z0-9][a-z0-9-]*)\}\s*$/i;

export const cmsMarkdown = new Marked({
  renderer: {
    html: () => '',
    heading(token) {
      const m = EXPLICIT_ANCHOR.exec(token.text);
      const html = this.parser.parseInline(token.tokens);
      return m
        ? `<h${token.depth} id="${m[1].toLowerCase()}">${html.replace(EXPLICIT_ANCHOR, '')}</h${token.depth}>\n`
        : `<h${token.depth}>${html}</h${token.depth}>\n`;
    },
    link(token) {
      return urlAllowed(token.href) ? false : this.parser.parseInline(token.tokens);
    },
    image(token) {
      return urlAllowed(token.href) ? false : '';
    },
  },
});
```

`marked` passes `href` to the renderer undecoded: `[x](javascript&#58;alert(1))` arrives as `javascript&#58;alert(1)`,
which a browser decodes inside the attribute. `urlAllowed` decodes entities first for that reason.

### The fixture

Copy `tests/Themia.Content.Tests/Fixtures/markdown-dialect.json` from the Themia repository **byte for byte** and
assert `cmsMarkdown.parse(entry.markdown) === entry.html` for every entry. Every entry ships as `candidate`; report on
coord when your renderer reproduces them, and they are promoted to `confirmed` once both applications have.
````

- [ ] **Step 6: Commit** (check the time first)

```bash
git add tests/Themia.Content.Tests src/neutral/Themia.Content/README.md
git commit -m "test(content): pin the markdown dialect as a golden fixture"
```

---

### Task 15: Documentation, registration, and a full run

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `README.md`
- Modify: `docs/themia-architecture-overview.md`
- Modify: `CLAUDE.md`

**Interfaces:** none — this task records blocks 1–4.

- [ ] **Step 1: CHANGELOG**

Under `## [Unreleased]` in `CHANGELOG.md`, add:

```markdown
### Added

- **`Themia.Content`** (+ `.PostgreSql` / `.MySql` / `.SqlServer`) — versioned, bilingual content pages keyed by slug
  and language, for legal and static pages (coord #0130). Every save writes an immutable revision; a stale
  `ExpectedVersion` is refused on create (a unique index behind a savepoint) and on update (the `UPDATE`'s own
  `WHERE`), on every engine, at READ COMMITTED. Revert saves an old revision's body as a new version and carries the
  language explicitly. Raw HTML and link destinations outside `http`/`https`/`mailto`/`tel` are refused at write,
  checked over Markdig's syntax tree. No tenant column and no module: the content is platform-level. Content that
  ships with application code goes through `SaveAsync`, never SQL — see the package README.
- **`Themia.Content.AspNetCore`** — optional public read (`{data}`, language fallback) and admin list/edit/revisions/
  revert routes (`{data, meta}`, RFC 7807, 409 carrying `currentVersion`, `updatedBy`, `updatedAt`) behind a
  consumer-supplied `Authorize` delegate that refuses when unset or throwing.
- **Markdown dialect golden fixture** — `tests/Themia.Content.Tests/Fixtures/markdown-dialect.json`, 29 entries pinning
  the write verdict (tested) and the HTML of the `marked` 18 renderer configuration in the package README (every entry
  `candidate` until both consumer web apps reproduce it).
```

- [ ] **Step 2: README package table**

```bash
grep -n "Themia.Modules.Notifications" README.md
```

In the capability table that lists `Themia.Modules.Pdf` and `Themia.Modules.Notifications`, add directly after the Notifications row:

```markdown
| Content pages (legal/static, versioned, bilingual) | `Themia.Content` + one `Themia.Content.{SqlServer\|PostgreSql\|MySql}`; optional `Themia.Content.AspNetCore` |
```

- [ ] **Step 3: Architecture overview**

In `docs/themia-architecture-overview.md`:

In the layered-architecture block, replace

```
                  Themia.Data.Migrations(+3 engines) | Themia.Data.Probes | Themia.DependencyInjection
```

with

```
                  Themia.Data.Migrations(+3 engines) | Themia.Data.Probes | Themia.DependencyInjection
                  Themia.Content(+3 engines/.AspNetCore)
```

In the §B table, directly after the `Themia.Modules.Messaging` row, add:

```markdown
| ~~`Themia.Modules.Content`~~ — no module; see `Themia.Content` (+ `.PostgreSql/.MySql/.SqlServer`, `.AspNetCore`) | propertiezy's production CMS pages, as reference — coord #0130 | ✅ **built** (0.26.0 — versioned bilingual pages; stale-version refusal on create and update; raw HTML and unsafe schemes refused at write over Markdig; golden renderer fixture. Platform-level content has no tenant state, so there is no module) |
```

In "Specs index", under **Modules & neutral capabilities**, after the `2026-09-08-themia-ai-design.md` line, add:

```markdown
- ✅ `2026-09-14-themia-content-design.md` — versioned bilingual content pages (0.26.0, coord #0130)
```

- [ ] **Step 4: CLAUDE.md**

In the `Architecture (big picture)` block of `CLAUDE.md`, replace

```
                                       Themia.Data.Migrations(3 engines) | Themia.Data.Probes | Themia.DependencyInjection
```

with

```
                                       Themia.Data.Migrations(3 engines) | Themia.Data.Probes | Themia.DependencyInjection
                                       Themia.Content(3 engines/.AspNetCore)
```

- [ ] **Step 5: Full clean build and every test**

```bash
dotnet build Themia.sln --no-incremental
dotnet test Themia.sln --filter "Category!=Integration"
dotnet test tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj
```

Expected: a clean build with no RS0016; every non-integration test in the repository passes; every Themia.Content integration test passes on all three engines. Record the passed counts and the wall-clock duration of each run in the task report — a count or duration that cannot fit the claim is the cheapest check that the run happened.

- [ ] **Step 6: Commit and open the pull request** (check the time first — pushing is a public git write too)

```bash
git add CHANGELOG.md README.md docs/themia-architecture-overview.md CLAUDE.md
git commit -m "docs(content): register Themia.Content in the catalog and changelog"
git push -u origin feat/themia-content
```

Open a pull request for blocks 1–4 against `main`. The body lists each falsification recorded in Tasks 2–13 (the guard removed and the test that turned red) and contains no attribution lines.

---

## Checkpoint — go/no-go by 2026-10-15

Before or on 2026-10-15, answer on coord #0130, using the rule in "Build order and the 2026-10-15 checkpoint" at the
top of this plan:

- **GO** — Tasks 1–15 merged and green, and Tasks 16–18 merged or certain to merge before 2026-10-25.
- **GO WITHOUT ITEM 4** — Tasks 1–15 merged and green, Tasks 16–18 not on track. Run Task 19 for blocks 1–4 now; Tasks
  16–18 ship in `0.26.1`.
- **NO-GO** — Tasks 1–15 not merged and green. Say what remains and when.

Sync coord and read the request's history index immediately before posting.

---
## Block 5 — adoption over an existing schema (item 4)

Separable: nothing in blocks 1–4 depends on it. If the checkpoint answered **GO WITHOUT ITEM 4**, these tasks target
`0.26.1`; `IContentPageDialect.ImportPageSql` already exists, so no dialect changes.

### Task 16: The importer

**Files:**
- Create: `src/neutral/Themia.Content/IContentPageImporter.cs`
- Create: `src/neutral/Themia.Content/Internal/ContentPageImporter.cs`
- Modify: `src/neutral/Themia.Content/DependencyInjection/ContentServiceCollectionExtensions.cs`
- Modify: `src/neutral/Themia.Content/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Content.Tests/ContentImporterValidationTests.cs`
- Modify: `tests/Themia.Content.IntegrationTests/ContentEngineFixtures.cs` (`Importer`, `ClearContentAsync`)
- Test: `tests/Themia.Content.IntegrationTests/ContentImportTests.cs`

**Interfaces:**
- Consumes: `IContentPageDialect` (`ImportPageSql`, `SelectPageSql`, `InsertRevisionSql`, `CountPagesSql`), `ContentPageValidator`, `ContentLanguage`, `ContentMarkdownRules`, `ContentPageRow`.
- Produces (namespace `Themia.Content`):
  - `public sealed record ContentRevisionImport(int Version, string Title, string Markdown, string? ChangeSummary, string? CreatedBy, DateTimeOffset CreatedAt)`
  - `public sealed record ContentPageImport(string Slug, string Language, string Title, string Markdown, int CurrentVersion, bool IsPublished, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? UpdatedBy, IReadOnlyList<ContentRevisionImport> Revisions)`
  - `public enum ContentImportOutcome { Imported, TargetNotEmpty, Invalid }`
  - `public enum ContentImportRule { DuplicateKey, InvalidSlugOrLanguage, FieldTooLong, NonContiguousRevisions, ContentDiffersFromCurrentRevision }`
  - `public sealed record ContentImportViolation(string Slug, string Language, ContentImportRule Rule)`
  - `public sealed class ContentImportResult` — `Outcome`, `Violations`, `ImportedPages`, `ImportedRevisions`, `PagesFailingCurrentRules`, `RevisionsFailingCurrentRules`, `Succeeded`
  - `public interface IContentPageImporter { Task<ContentImportResult> ImportAsync(IReadOnlyList<ContentPageImport> pages, CancellationToken ct = default); }`
- `FieldTooLong` is an addition to spec §10's rule list: without it, a title longer than the column fails inside the transaction as a database error instead of being reported with its page. Task 18 records it in the spec.

- [ ] **Step 1: Write the failing unit tests** — validation needs no database, and must open no connection when it refuses.

`tests/Themia.Content.Tests/ContentImporterValidationTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Content.Internal;
using Xunit;

namespace Themia.Content.Tests;

public class ContentImporterValidationTests
{
    private static readonly DateTimeOffset At = new(2026, 8, 19, 0, 0, 0, TimeSpan.Zero);

    private static ContentOptions Options()
    {
        var options = new ContentOptions { FallbackLanguage = "th" };
        options.Languages.Add("th");
        options.Languages.Add("en");
        return options;
    }

    private static ContentPageImport Page(string slug, string language, int currentVersion, string markdown, params (int Version, string Markdown)[] revisions) =>
        new(slug, language, "Title", markdown, currentVersion, true, At, At, null,
            revisions.Select(r => new ContentRevisionImport(r.Version, "Title", r.Markdown, null, null, At)).ToList());

    private static async Task<(ContentImportResult Result, CountingContentDialect Dialect)> ImportAsync(params ContentPageImport[] pages)
    {
        var dialect = new CountingContentDialect();
        var importer = new ContentPageImporter(dialect, Options(), NullLogger<ContentPageImporter>.Instance);
        return (await importer.ImportAsync(pages), dialect);
    }

    [Fact]
    public async Task Import_ShouldListEveryViolation_AndOpenNoConnection()
    {
        var (result, dialect) = await ImportAsync(
            Page("terms", "th", 1, "# Terms", (1, "# Terms")),
            Page("terms", " TH ", 1, "# Terms", (1, "# Terms")),
            Page("privacy", "fr", 1, "# P", (1, "# P")),
            Page("about", "th", 3, "# A3", (1, "# A1"), (3, "# A3")),
            Page("faq", "th", 1, "# Served text", (1, "# Placeholder")));

        Assert.Equal(ContentImportOutcome.Invalid, result.Outcome);
        Assert.Equal(0, dialect.ConnectionsRequested);
        Assert.Contains(result.Violations, v => v is { Slug: "terms", Rule: ContentImportRule.DuplicateKey });
        Assert.Contains(result.Violations, v => v is { Slug: "privacy", Rule: ContentImportRule.InvalidSlugOrLanguage });
        Assert.Contains(result.Violations, v => v is { Slug: "about", Rule: ContentImportRule.NonContiguousRevisions });
        Assert.Contains(result.Violations, v => v is { Slug: "faq", Rule: ContentImportRule.ContentDiffersFromCurrentRevision });
    }

    [Fact]
    public async Task Import_ShouldReportFieldTooLong_WhenARevisionTitleExceedsTheColumn()
    {
        var page = Page("terms", "th", 1, "# T", (1, "# T")) with
        {
            Revisions = [new ContentRevisionImport(1, new string('t', 201), "# T", null, null, At)],
        };

        var (result, _) = await ImportAsync(page);

        Assert.Contains(result.Violations, v => v.Rule == ContentImportRule.FieldTooLong);
    }

    [Fact]
    public async Task Import_ShouldCountHistoryThatFailsTodaysMarkdownRules_WhileRefusingForOtherReasons()
    {
        var (result, _) = await ImportAsync(
            Page("terms", "th", 2, "# T2", (1, "a <b>x</b>"), (2, "# T2")),
            Page("terms", "th", 1, "# Dup", (1, "# Dup")));

        Assert.Equal(1, result.RevisionsFailingCurrentRules);
        Assert.Equal(0, result.PagesFailingCurrentRules);
    }
}
```

Run: `dotnet test tests/Themia.Content.Tests/Themia.Content.Tests.csproj --filter ContentImporterValidationTests`
Expected: build fails — the import types do not exist.

- [ ] **Step 2: Write the public contract**

`src/neutral/Themia.Content/IContentPageImporter.cs`:

```csharp
namespace Themia.Content;

/// <summary>One revision of a page being imported, as the source system recorded it.</summary>
/// <param name="Version">The version this revision created.</param>
/// <param name="Title">The title at that version.</param>
/// <param name="Markdown">The body at that version.</param>
/// <param name="ChangeSummary">The source's change note, if any.</param>
/// <param name="CreatedBy">The source's author id, as a string.</param>
/// <param name="CreatedAt">When the source wrote it.</param>
public sealed record ContentRevisionImport(
    int Version, string Title, string Markdown, string? ChangeSummary, string? CreatedBy, DateTimeOffset CreatedAt);

/// <summary>One page being imported, with its whole history.</summary>
/// <param name="Slug">The URL key.</param>
/// <param name="Language">The language; normalised before it is stored.</param>
/// <param name="Title">The page's current title.</param>
/// <param name="Markdown">The page's current body — what readers are served.</param>
/// <param name="CurrentVersion">The source's current version.</param>
/// <param name="IsPublished">Whether readers are served the page.</param>
/// <param name="CreatedAt">When the source created the page.</param>
/// <param name="UpdatedAt">When the source last saved it.</param>
/// <param name="UpdatedBy">Who last saved it, as a string.</param>
/// <param name="Revisions">Every revision, version 1 through <paramref name="CurrentVersion"/>.</param>
public sealed record ContentPageImport(
    string Slug, string Language, string Title, string Markdown, int CurrentVersion, bool IsPublished,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? UpdatedBy, IReadOnlyList<ContentRevisionImport> Revisions);

/// <summary>How an import ended.</summary>
public enum ContentImportOutcome
{
    /// <summary>Every page and revision was written.</summary>
    Imported,

    /// <summary>The content tables already hold pages. An import is a one-time move, never a merge. Nothing was written.</summary>
    TargetNotEmpty,

    /// <summary>The input broke at least one rule. Nothing was written and no connection was opened.</summary>
    Invalid,
}

/// <summary>A rule an imported page broke.</summary>
public enum ContentImportRule
{
    /// <summary>The same slug and normalised language appear more than once.</summary>
    DuplicateKey,

    /// <summary>The slug fails the slug rule, or the language is not configured.</summary>
    InvalidSlugOrLanguage,

    /// <summary>A title, change summary or author id is longer than its column.</summary>
    FieldTooLong,

    /// <summary>The revisions are not exactly versions 1 through the current version.</summary>
    NonContiguousRevisions,

    /// <summary>The page's title or body differs from its current revision's. Importing would either publish the
    /// revision over the served text or store history that does not contain it. Record the served content as a new
    /// revision in the source first.</summary>
    ContentDiffersFromCurrentRevision,
}

/// <summary>One page that broke one rule.</summary>
/// <param name="Slug">The page's slug as given.</param>
/// <param name="Language">The page's language as given.</param>
/// <param name="Rule">The rule it broke.</param>
public sealed record ContentImportViolation(string Slug, string Language, ContentImportRule Rule);

/// <summary>The result of <see cref="IContentPageImporter.ImportAsync"/>.</summary>
public sealed class ContentImportResult
{
    private ContentImportResult(
        ContentImportOutcome outcome, IReadOnlyList<ContentImportViolation> violations, int importedPages,
        int importedRevisions, int pagesFailingCurrentRules, int revisionsFailingCurrentRules)
    {
        Outcome = outcome;
        Violations = violations;
        ImportedPages = importedPages;
        ImportedRevisions = importedRevisions;
        PagesFailingCurrentRules = pagesFailingCurrentRules;
        RevisionsFailingCurrentRules = revisionsFailingCurrentRules;
    }

    /// <summary>How the import ended.</summary>
    public ContentImportOutcome Outcome { get; }

    /// <summary>Every violation, when <see cref="Outcome"/> is <see cref="ContentImportOutcome.Invalid"/>.</summary>
    public IReadOnlyList<ContentImportViolation> Violations { get; }

    /// <summary>Pages written.</summary>
    public int ImportedPages { get; }

    /// <summary>Revisions written.</summary>
    public int ImportedRevisions { get; }

    /// <summary>Pages whose current body would be refused by <see cref="ContentMarkdownRules"/> today. Reported, never
    /// refused: history cannot be edited, and the next save of such a page will be asked to fix it.</summary>
    public int PagesFailingCurrentRules { get; }

    /// <summary>Revisions whose body would be refused by <see cref="ContentMarkdownRules"/> today. Reported, never refused.</summary>
    public int RevisionsFailingCurrentRules { get; }

    /// <summary>Whether everything was written.</summary>
    public bool Succeeded => Outcome == ContentImportOutcome.Imported;

    /// <summary>Everything was written.</summary>
    public static ContentImportResult Imported(int pages, int revisions, int pagesFailingCurrentRules, int revisionsFailingCurrentRules) =>
        new(ContentImportOutcome.Imported, Array.Empty<ContentImportViolation>(), pages, revisions, pagesFailingCurrentRules, revisionsFailingCurrentRules);

    /// <summary>The target already holds pages.</summary>
    public static ContentImportResult TargetNotEmpty() =>
        new(ContentImportOutcome.TargetNotEmpty, Array.Empty<ContentImportViolation>(), 0, 0, 0, 0);

    /// <summary>The input broke rules.</summary>
    public static ContentImportResult Invalid(
        IReadOnlyList<ContentImportViolation> violations, int pagesFailingCurrentRules, int revisionsFailingCurrentRules) =>
        new(ContentImportOutcome.Invalid, violations, 0, 0, pagesFailingCurrentRules, revisionsFailingCurrentRules);
}

/// <summary>A one-time move of pages and their history from another system into Themia.Content's tables.</summary>
/// <remarks>
/// <para>Themia never reads another system's tables: the consumer supplies the rows (see the package README for a
/// recipe). Every rule is checked before anything is written, in one transaction; any violation writes nothing.</para>
/// <para>Version numbers, timestamps, authors and change summaries are preserved; row ids are not.</para>
/// </remarks>
public interface IContentPageImporter
{
    /// <summary>Validates <paramref name="pages"/> and, when every rule holds and the content tables are empty, writes them.</summary>
    Task<ContentImportResult> ImportAsync(IReadOnlyList<ContentPageImport> pages, CancellationToken ct = default);
}
```

- [ ] **Step 3: Write the importer**

`src/neutral/Themia.Content/Internal/ContentPageImporter.cs`:

```csharp
using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;

namespace Themia.Content.Internal;

/// <summary>The one implementation of <see cref="IContentPageImporter"/>.</summary>
internal sealed class ContentPageImporter : IContentPageImporter
{
    private readonly IContentPageDialect dialect;
    private readonly ContentOptions options;
    private readonly ILogger<ContentPageImporter> logger;

    public ContentPageImporter(IContentPageDialect dialect, ContentOptions options, ILogger<ContentPageImporter> logger)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        this.dialect = dialect;
        this.options = options;
        this.logger = logger;
    }

    public async Task<ContentImportResult> ImportAsync(IReadOnlyList<ContentPageImport> pages, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pages);

        var pagesFailing = pages.Count(p => ContentMarkdownRules.Check(p.Markdown ?? string.Empty).Count > 0);
        var revisionsFailing = pages.SelectMany(p => p.Revisions ?? []).Count(r => ContentMarkdownRules.Check(r.Markdown ?? string.Empty).Count > 0);

        var violations = Validate(pages);
        if (violations.Count > 0)
        {
            logger.LogWarning("Content import refused with {ViolationCount} violations; nothing was written", violations.Count);
            return ContentImportResult.Invalid(violations, pagesFailing, revisionsFailing);
        }

        await using var connection = dialect.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        var existing = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            dialect.CountPagesSql, transaction: transaction, cancellationToken: ct)).ConfigureAwait(false);
        if (existing > 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            logger.LogWarning("Content import refused: the content tables already hold {PageCount} pages", existing);
            return ContentImportResult.TargetNotEmpty();
        }

        var revisionCount = 0;
        foreach (var page in pages)
        {
            var language = ContentLanguage.Normalise(page.Language);
            await connection.ExecuteAsync(new CommandDefinition(
                dialect.ImportPageSql,
                new
                {
                    page.Slug,
                    Language = language,
                    page.Title,
                    page.Markdown,
                    page.CurrentVersion,
                    page.IsPublished,
                    CreatedAt = page.CreatedAt.ToUniversalTime(),
                    UpdatedAt = page.UpdatedAt.ToUniversalTime(),
                    page.UpdatedBy,
                },
                transaction,
                cancellationToken: ct)).ConfigureAwait(false);

            var row = await connection.QuerySingleAsync<ContentPageRow>(new CommandDefinition(
                dialect.SelectPageSql, new { page.Slug, Language = language }, transaction, cancellationToken: ct)).ConfigureAwait(false);

            foreach (var revision in page.Revisions.OrderBy(r => r.Version))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    dialect.InsertRevisionSql,
                    new
                    {
                        PageId = row.Id,
                        revision.Version,
                        revision.Title,
                        revision.Markdown,
                        revision.ChangeSummary,
                        CreatedAt = revision.CreatedAt.ToUniversalTime(),
                        revision.CreatedBy,
                    },
                    transaction,
                    cancellationToken: ct)).ConfigureAwait(false);
                revisionCount++;
            }
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Content import wrote {PageCount} pages and {RevisionCount} revisions", pages.Count, revisionCount);
        return ContentImportResult.Imported(pages.Count, revisionCount, pagesFailing, revisionsFailing);
    }

    private List<ContentImportViolation> Validate(IReadOnlyList<ContentPageImport> pages)
    {
        var violations = new List<ContentImportViolation>();

        foreach (var group in pages.GroupBy(p => (p.Slug, Language: ContentLanguage.Normalise(p.Language))).Where(g => g.Count() > 1))
        {
            violations.Add(new ContentImportViolation(group.Key.Slug, group.Key.Language, ContentImportRule.DuplicateKey));
        }

        foreach (var page in pages)
        {
            Add(page, violations, ValidateOne(page));
        }

        return violations;
    }

    private IEnumerable<ContentImportRule> ValidateOne(ContentPageImport page)
    {
        if (!ContentPageValidator.IsValidSlug(page.Slug) || !options.IsConfigured(ContentLanguage.Normalise(page.Language)))
        {
            yield return ContentImportRule.InvalidSlugOrLanguage;
        }

        var revisions = page.Revisions ?? [];
        if (page.Title?.Length > ContentPageValidator.MaxTitleLength
            || page.UpdatedBy?.Length > ContentPageValidator.MaxEditorIdLength
            || revisions.Any(r => r.Title?.Length > ContentPageValidator.MaxTitleLength
                || r.ChangeSummary?.Length > ContentPageValidator.MaxChangeSummaryLength
                || r.CreatedBy?.Length > ContentPageValidator.MaxEditorIdLength))
        {
            yield return ContentImportRule.FieldTooLong;
        }

        var versions = revisions.Select(r => r.Version).OrderBy(v => v).ToList();
        if (page.CurrentVersion < 1 || !versions.SequenceEqual(Enumerable.Range(1, page.CurrentVersion)))
        {
            yield return ContentImportRule.NonContiguousRevisions;
            yield break;
        }

        var current = revisions.Single(r => r.Version == page.CurrentVersion);
        if (!string.Equals(current.Title, page.Title, StringComparison.Ordinal)
            || !string.Equals(current.Markdown, page.Markdown, StringComparison.Ordinal))
        {
            yield return ContentImportRule.ContentDiffersFromCurrentRevision;
        }
    }

    private static void Add(ContentPageImport page, List<ContentImportViolation> violations, IEnumerable<ContentImportRule> rules) =>
        violations.AddRange(rules.Select(rule => new ContentImportViolation(page.Slug, page.Language, rule)));
}
```

- [ ] **Step 4: Register it**

In `ContentServiceCollectionExtensions.AddThemiaContent`, after the `IContentPageService` registration, add:

```csharp
        services.TryAddSingleton<IContentPageImporter>(provider => new ContentPageImporter(
            provider.GetService<IContentPageDialect>() ?? throw new InvalidOperationException(
                "No IContentPageDialect is registered. Call AddThemiaContentPostgres, AddThemiaContentMySql or " +
                "AddThemiaContentSqlServer as well as AddThemiaContent."),
            provider.GetRequiredService<ContentOptions>(),
            provider.GetRequiredService<ILogger<ContentPageImporter>>()));
```

Run the unit tests, then `dotnet format analyzers src/neutral/Themia.Content/Themia.Content.csproj --diagnostics RS0016 --severity info` and a clean build. Expected: `ContentImporterValidationTests` pass.

- [ ] **Step 5: Give the fixture the importer**

In `ContentEngineFixture` (`ContentEngineFixtures.cs`): add a property `public IContentPageImporter Importer { get; private set; } = null!;`, add `Importer = services.GetRequiredService<IContentPageImporter>();` to `Resolve`, and add:

```csharp
    /// <summary>Deletes every page and revision. Import tests need empty tables; tests in one collection run sequentially,
    /// and no other test reads rows it did not create in the same test.</summary>
    public async Task ClearContentAsync()
    {
        await using var connection = Dialect.CreateConnection();
        await connection.OpenAsync();
        await connection.ExecuteAsync("DELETE FROM content_page_revisions;");
        await connection.ExecuteAsync("DELETE FROM content_pages;");
    }
```

- [ ] **Step 6: Write the integration tests**

`tests/Themia.Content.IntegrationTests/ContentImportTests.cs`:

```csharp
using Xunit;
using static Themia.Content.IntegrationTests.ContentTestData;

namespace Themia.Content.IntegrationTests;

public abstract class ContentImportTests(ContentEngineFixture fixture)
{
    private static readonly DateTimeOffset Created = new(2026, 8, 19, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = new(2026, 9, 12, 5, 30, 0, TimeSpan.FromHours(7));

    private static ContentPageImport TwoVersionPage(string slug) =>
        new(slug, "EN", "Privacy v2", "# Privacy v2", 2, true, Created, Updated, "4d9e1b2c-0000-0000-0000-000000000001",
        [
            new ContentRevisionImport(1, "Privacy", "# Privacy v1", null, "4d9e1b2c-0000-0000-0000-000000000001", Created),
            new ContentRevisionImport(2, "Privacy v2", "# Privacy v2", "counsel wording", "4d9e1b2c-0000-0000-0000-000000000002", Updated),
        ]);

    [Fact]
    public async Task Import_ShouldPreserveVersionsTimestampsAndAuthors()
    {
        await fixture.ClearContentAsync();
        var slug = NewSlug();

        var result = await fixture.Importer.ImportAsync([TwoVersionPage(slug)]);

        Assert.Equal(ContentImportOutcome.Imported, result.Outcome);
        Assert.Equal((1, 2), (result.ImportedPages, result.ImportedRevisions));
        var page = (await fixture.Service.GetForEditAsync(slug, "en"))!;
        Assert.Equal(2, page.CurrentVersion);
        Assert.Equal(Created.UtcDateTime, page.CreatedAt.UtcDateTime);
        Assert.Equal(Updated.UtcDateTime, page.UpdatedAt.UtcDateTime);
        var revisions = await fixture.Service.GetRevisionsAsync(slug, "en", 1, 20);
        Assert.Equal([2, 1], revisions.Items.Select(r => r.Version));
        Assert.Equal("counsel wording", revisions.Items[0].ChangeSummary);
        Assert.Equal("4d9e1b2c-0000-0000-0000-000000000002", revisions.Items[0].CreatedBy);
    }

    [Fact]
    public async Task ImportedPage_ShouldBeEditableAtItsImportedVersion()
    {
        await fixture.ClearContentAsync();
        var slug = NewSlug();
        await fixture.Importer.ImportAsync([TwoVersionPage(slug)]);

        var saved = await fixture.Service.SaveAsync(Save(slug, "en", 2, "# Privacy v3"));

        Assert.Equal(ContentSaveOutcome.Saved, saved.Outcome);
        Assert.Equal(3, saved.Page!.CurrentVersion);
    }

    [Fact]
    public async Task Import_ShouldWriteNothing_WhenAnyPageAlreadyExists()
    {
        await fixture.ClearContentAsync();
        await fixture.Service.SaveAsync(Save(NewSlug(), "th", 0));
        var slug = NewSlug();

        var result = await fixture.Importer.ImportAsync([TwoVersionPage(slug)]);

        Assert.Equal(ContentImportOutcome.TargetNotEmpty, result.Outcome);
        Assert.Null(await fixture.Service.GetForEditAsync(slug, "en"));
    }

    [Fact]
    public async Task Import_ShouldWriteNothing_WhenServedContentIsInNoRevision()
    {
        await fixture.ClearContentAsync();
        var slug = NewSlug();
        var diverged = new ContentPageImport(slug, "th", "Terms", "# Counsel text", 1, true, Created, Updated, null,
            [new ContentRevisionImport(1, "Terms", "# Placeholder", null, null, Created)]);

        var result = await fixture.Importer.ImportAsync([TwoVersionPage(NewSlug()), diverged]);

        Assert.Equal(ContentImportOutcome.Invalid, result.Outcome);
        var violation = Assert.Single(result.Violations);
        Assert.Equal((slug, ContentImportRule.ContentDiffersFromCurrentRevision), (violation.Slug, violation.Rule));
        Assert.Equal(0, (await fixture.Service.ListAsync(1, 1)).Total);
    }

    [Fact]
    public async Task Import_ShouldReportButNotRefuse_HistoryThatTodaysRulesWouldRefuse()
    {
        await fixture.ClearContentAsync();
        var slug = NewSlug();
        var page = new ContentPageImport(slug, "th", "T", "# T2", 2, true, Created, Updated, null,
        [
            new ContentRevisionImport(1, "T", "old <b>markup</b>", null, null, Created),
            new ContentRevisionImport(2, "T", "# T2", null, null, Updated),
        ]);

        var result = await fixture.Importer.ImportAsync([page]);

        Assert.Equal(ContentImportOutcome.Imported, result.Outcome);
        Assert.Equal(1, result.RevisionsFailingCurrentRules);
    }
}

[Collection(PostgresContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PostgresContentImportTests(PostgresContentFixture fixture) : ContentImportTests(fixture);

[Collection(MySqlContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MySqlContentImportTests(MySqlContentFixture fixture) : ContentImportTests(fixture);

[Collection(SqlServerContentCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SqlServerContentImportTests(SqlServerContentFixture fixture) : ContentImportTests(fixture);
```

Run: `dotnet test tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj`
Expected: every test on every engine passes — the import tests and every earlier test, since `ClearContentAsync` must not break them.

- [ ] **Step 7: Falsify the divergence rule** (spec §12)

Copy `ContentPageImporter.cs`; delete the `if (!string.Equals(current.Title …` block; run `ContentImporterValidationTests` and `ContentImportTests`. Expected: FAIL — `Import_ShouldListEveryViolation_AndOpenNoConnection` and `Import_ShouldWriteNothing_WhenServedContentIsInNoRevision`. Record; restore with `cp`; `diff`; re-run to green.

- [ ] **Step 8: Commit** (check the time first)

```bash
git add src/neutral/Themia.Content tests/Themia.Content.Tests tests/Themia.Content.IntegrationTests
git commit -m "feat(content): import pages and history, refusing divergent history"
```

---

### Task 17: The adoption test over propertiezy's schema

**Files:**
- Test: `tests/Themia.Content.IntegrationTests/PropertiezyAdoptionTests.cs`

**Interfaces:**
- Consumes: `AddThemiaContent`, `AddThemiaContentPostgres`, `IContentPageImporter`, `IContentPageService`.

This is the test coord #0130 item 4 asks for: a database that **already** holds page and revision rows, in the shape propertiezy's migrations leave it (`M202608020001`, `M202608180001`, `M202609140001`), including a page whose served text was changed without a revision, as seven of propertiezy's content migrations did. Its own container, so the Themia migration meets the existing tables exactly as it would in production.

Step 6 of the test runs the revision-recording SQL that coord #0132 gave propertiezy marked **"NOT RUN"**. If that step fails, the SQL posted to propertiezy is wrong: stop and report, so the coord request can be corrected.

- [ ] **Step 1: Write the test**

`tests/Themia.Content.IntegrationTests/PropertiezyAdoptionTests.cs`:

```csharp
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Themia.Content.DependencyInjection;
using Themia.Content.PostgreSql;
using Xunit;

namespace Themia.Content.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class PropertiezyAdoptionTests : IAsyncLifetime
{
    private const string CounselTerms = "# ข้อกำหนดการใช้บริการ\n\nข้อความที่ทนายตรวจแล้ว";
    private const string SeedPlaceholder = "# ข้อกำหนดการใช้บริการ\n\n[placeholder]";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task ExistingPropertiezyDatabase_ShouldAdoptWithoutChangingItsTables_AndImportOnlyAfterTheRevisionFix()
    {
        var connectionString = container.GetConnectionString();
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();

        // 1. propertiezy's schema and data, as its migrations leave them.
        await db.ExecuteAsync(PropertiezySchema);
        await db.ExecuteAsync(PropertiezyRows, new { CounselTerms, SeedPlaceholder });
        var before = await SnapshotAsync(db);

        // 2. Themia registers against the same database: its migration meets tables it did not create.
        using var provider = new ServiceCollection()
            .AddThemiaContent(o => { o.Languages.Add("th"); o.Languages.Add("en"); o.FallbackLanguage = "th"; })
            .AddThemiaContentPostgres(connectionString)
            .BuildServiceProvider();
        var importer = provider.GetRequiredService<IContentPageImporter>();
        var service = provider.GetRequiredService<IContentPageService>();

        Assert.Equal(before, await SnapshotAsync(db));
        Assert.True(await db.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_tables WHERE tablename = 'content_pages');"));

        // 3. Import through the README recipe: refused, naming only the page whose served text is in no revision.
        var refused = await importer.ImportAsync(await PropertiezyRecipe.ReadAsync(db));
        Assert.Equal(ContentImportOutcome.Invalid, refused.Outcome);
        var violation = Assert.Single(refused.Violations);
        Assert.Equal(("terms", "th", ContentImportRule.ContentDiffersFromCurrentRevision), (violation.Slug, violation.Language, violation.Rule));
        Assert.Equal(0, await db.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM content_pages;"));

        // 4. The fix propertiezy was given in coord #0132, marked "NOT RUN" there.
        await db.ExecuteAsync(RecordServedContentAsRevision);
        var afterFix = await SnapshotAsync(db);

        // 5. Import again: everything, and propertiezy's tables are untouched by it.
        var imported = await importer.ImportAsync(await PropertiezyRecipe.ReadAsync(db));
        Assert.Equal(ContentImportOutcome.Imported, imported.Outcome);
        Assert.Equal((2, 4), (imported.ImportedPages, imported.ImportedRevisions));
        Assert.Equal(afterFix, await SnapshotAsync(db));

        // 6. Readers get counsel's text byte for byte, and the revert that served the placeholder now cannot.
        Assert.Equal(CounselTerms, (await service.GetPublishedAsync("terms", "th"))!.Markdown);
        var revisions = await service.GetRevisionsAsync("terms", "th", 1, 20);
        Assert.Equal(CounselTerms, revisions.Items.Single(r => r.Version == 2).Markdown);
    }

    private static async Task<string> SnapshotAsync(NpgsqlConnection db)
    {
        var pages = await db.QueryAsync<string>("""SELECT row_to_json(t)::text FROM "CmsPages" t ORDER BY "CmsPageId";""");
        var revisions = await db.QueryAsync<string>("""SELECT row_to_json(t)::text FROM "CmsPageRevisions" t ORDER BY "CmsPageRevisionId";""");
        return string.Join("\n", pages.Concat(revisions));
    }

    private const string PropertiezySchema = """
        CREATE TABLE "CmsPages" (
            "CmsPageId" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            "Slug" varchar(100) NOT NULL,
            "Title" varchar(200) NOT NULL,
            "ContentMarkdown" text NOT NULL,
            "CurrentVersion" integer NOT NULL DEFAULT 1,
            "IsPublished" boolean NOT NULL DEFAULT true,
            "InsertDate" timestamptz NOT NULL DEFAULT now(),
            "UpdateDate" timestamptz NOT NULL DEFAULT now(),
            "UpdateUserId" uuid NULL,
            "Language" varchar(2) NOT NULL DEFAULT 'th',
            CONSTRAINT "UC_CmsPages_Slug_Language" UNIQUE ("Slug", "Language"));
        CREATE TABLE "CmsPageRevisions" (
            "CmsPageRevisionId" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            "CmsPageId" integer NOT NULL REFERENCES "CmsPages" ("CmsPageId") ON DELETE CASCADE,
            "Version" integer NOT NULL,
            "Title" varchar(200) NOT NULL,
            "ContentMarkdown" text NOT NULL,
            "ChangeSummary" varchar(500) NULL,
            "CreatedUserId" uuid NULL,
            "CreatedDate" timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT "UC_CmsPageRevisions_Page_Version" UNIQUE ("CmsPageId", "Version"));
        """;

    // terms/th: seeded at version 1, then its served text replaced by a content migration without a revision.
    // privacy/en: two consistent revisions, as an editor produces them.
    private const string PropertiezyRows = """
        INSERT INTO "CmsPages" ("Slug", "Language", "Title", "ContentMarkdown", "CurrentVersion", "IsPublished", "InsertDate", "UpdateDate")
        VALUES ('terms', 'th', 'ข้อกำหนด', @CounselTerms, 1, true, '2026-08-19T02:00:00Z', '2026-09-12T07:57:00Z');
        INSERT INTO "CmsPageRevisions" ("CmsPageId", "Version", "Title", "ContentMarkdown", "CreatedDate")
        VALUES (1, 1, 'ข้อกำหนด', @SeedPlaceholder, '2026-08-19T02:00:00Z');
        INSERT INTO "CmsPages" ("Slug", "Language", "Title", "ContentMarkdown", "CurrentVersion", "IsPublished", "InsertDate", "UpdateDate", "UpdateUserId")
        VALUES ('privacy', 'en', 'Privacy', '# Privacy v2', 2, true, '2026-08-19T02:00:00Z', '2026-09-12T06:34:00Z', '4d9e1b2c-0000-0000-0000-000000000002');
        INSERT INTO "CmsPageRevisions" ("CmsPageId", "Version", "Title", "ContentMarkdown", "CreatedUserId", "CreatedDate")
        VALUES (2, 1, 'Privacy', '# Privacy v1', '4d9e1b2c-0000-0000-0000-000000000001', '2026-08-19T02:00:00Z'),
               (2, 2, 'Privacy', '# Privacy v2', '4d9e1b2c-0000-0000-0000-000000000002', '2026-09-12T06:34:00Z');
        """;

    // Verbatim from coord #0132's example.
    private const string RecordServedContentAsRevision = """
        INSERT INTO "CmsPageRevisions" ("CmsPageId", "Version", "Title", "ContentMarkdown", "ChangeSummary", "CreatedDate")
        SELECT p."CmsPageId", p."CurrentVersion" + 1, p."Title", p."ContentMarkdown",
               'Content set by a migration, recorded as a revision', NOW()
          FROM "CmsPages" p
          JOIN "CmsPageRevisions" r ON r."CmsPageId" = p."CmsPageId" AND r."Version" = p."CurrentVersion"
         WHERE r."Title" IS DISTINCT FROM p."Title"
            OR r."ContentMarkdown" IS DISTINCT FROM p."ContentMarkdown";

        UPDATE "CmsPages" p
           SET "CurrentVersion" = p."CurrentVersion" + 1
         WHERE EXISTS (SELECT 1 FROM "CmsPageRevisions" r
                        WHERE r."CmsPageId" = p."CmsPageId" AND r."Version" = p."CurrentVersion" + 1);
        """;
}

/// <summary>The README's recipe for reading propertiezy's tables into import records. Consumer code: it names the
/// consumer's tables, so it lives here and in the README, never in the package.</summary>
internal static class PropertiezyRecipe
{
    private const string PagesSql = """
        SELECT "Slug", "Language", "Title", "ContentMarkdown", "CurrentVersion", "IsPublished", "InsertDate", "UpdateDate", "UpdateUserId"
          FROM "CmsPages" ORDER BY "CmsPageId";
        """;

    private const string RevisionsSql = """
        SELECT p."Slug", p."Language", r."Version", r."Title", r."ContentMarkdown", r."ChangeSummary", r."CreatedUserId", r."CreatedDate"
          FROM "CmsPageRevisions" r JOIN "CmsPages" p ON p."CmsPageId" = r."CmsPageId"
         ORDER BY p."CmsPageId", r."Version";
        """;

    public static async Task<IReadOnlyList<ContentPageImport>> ReadAsync(NpgsqlConnection db)
    {
        var pages = await db.QueryAsync<PageRow>(PagesSql);
        var revisions = (await db.QueryAsync<RevisionRow>(RevisionsSql)).ToLookup(r => (r.Slug, r.Language));

        return pages.Select(p => new ContentPageImport(
                p.Slug, p.Language, p.Title, p.ContentMarkdown, p.CurrentVersion, p.IsPublished,
                p.InsertDate, p.UpdateDate, p.UpdateUserId?.ToString(),
                revisions[(p.Slug, p.Language)]
                    .Select(r => new ContentRevisionImport(r.Version, r.Title, r.ContentMarkdown, r.ChangeSummary, r.CreatedUserId?.ToString(), r.CreatedDate))
                    .ToList()))
            .ToList();
    }

    private sealed class PageRow
    {
        public string Slug { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string ContentMarkdown { get; set; } = string.Empty;
        public int CurrentVersion { get; set; }
        public bool IsPublished { get; set; }
        public DateTimeOffset InsertDate { get; set; }
        public DateTimeOffset UpdateDate { get; set; }
        public Guid? UpdateUserId { get; set; }
    }

    private sealed class RevisionRow
    {
        public string Slug { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public int Version { get; set; }
        public string Title { get; set; } = string.Empty;
        public string ContentMarkdown { get; set; } = string.Empty;
        public string? ChangeSummary { get; set; }
        public Guid? CreatedUserId { get; set; }
        public DateTimeOffset CreatedDate { get; set; }
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj --filter PropertiezyAdoptionTests`
Expected: PASS. Note that `terms` has title `ข้อกำหนด` on both the page and revision 1, so only its markdown differs.

- [ ] **Step 3: Falsify the snapshot**

Copy the test file; insert `await db.ExecuteAsync("""UPDATE "CmsPages" SET "Title" = 'x' WHERE "Slug" = 'privacy';""");` on the line immediately **before** `Assert.Equal(before, await SnapshotAsync(db));`. Run. Expected: FAIL on the snapshot comparison — proving the comparison sees a change. Record; restore with `cp`; `diff`; re-run to green.

- [ ] **Step 4: Commit** (check the time first)

```bash
git add tests/Themia.Content.IntegrationTests/PropertiezyAdoptionTests.cs
git commit -m "test(content): adopt a database that already holds CMS rows"
```

---

### Task 18: Document the importer

**Files:**
- Modify: `src/neutral/Themia.Content/README.md`
- Modify: `CHANGELOG.md`
- Modify: `docs/superpowers/specs/2026-09-14-themia-content-design.md` (§10)

- [ ] **Step 1: README** — append:

````markdown
## Importing from an existing schema

`IContentPageImporter` moves pages and their history from another system into empty content tables, once. Themia never
reads the other system's tables; you read them and hand over the rows. Every rule is checked before anything is
written, and any violation writes nothing:

| rule | refused as |
|---|---|
| the content tables already hold pages | `TargetNotEmpty` |
| a slug and language appear twice; an invalid slug or unconfigured language | `DuplicateKey`, `InvalidSlugOrLanguage` |
| a field longer than its column | `FieldTooLong` |
| revisions are not exactly 1 through the current version | `NonContiguousRevisions` |
| the page's title or body is not its current revision's | `ContentDiffersFromCurrentRevision` |

The last rule refuses rather than repairs. A page whose served text is in no revision has history that cannot be
trusted; record the served text as a new revision in the source system first, then import.

For a PostgreSQL source shaped like propertiezy's `CmsPages` / `CmsPageRevisions`:

```csharp
const string PagesSql = """
    SELECT "Slug", "Language", "Title", "ContentMarkdown", "CurrentVersion", "IsPublished", "InsertDate", "UpdateDate", "UpdateUserId"
      FROM "CmsPages" ORDER BY "CmsPageId";
    """;
const string RevisionsSql = """
    SELECT p."Slug", p."Language", r."Version", r."Title", r."ContentMarkdown", r."ChangeSummary", r."CreatedUserId", r."CreatedDate"
      FROM "CmsPageRevisions" r JOIN "CmsPages" p ON p."CmsPageId" = r."CmsPageId"
     ORDER BY p."CmsPageId", r."Version";
    """;
```

Map each page row and its revisions to `ContentPageImport` / `ContentRevisionImport` (author `Guid`s become strings)
and call `ImportAsync`. `tests/Themia.Content.IntegrationTests/PropertiezyAdoptionTests.cs` in the Themia repository is
this recipe, run against a database that already holds rows.
````

- [ ] **Step 2: CHANGELOG** — under `## [Unreleased]` → `### Added`, add:

```markdown
- **`Themia.Content` importer** — `IContentPageImporter` moves pages and history from another system into empty content
  tables in one transaction, preserving versions, timestamps and authors. Refuses, listing every violation and writing
  nothing, when a page's served content is not its current revision (coord #0130 item 4). Adoption over a database that
  already holds CMS rows is tested (`PropertiezyAdoptionTests`).
```

- [ ] **Step 3: Spec** — in §10's rules table, add a row after the `(slug, language)` row:

```markdown
| titles, change summaries and author ids fit their columns | `Invalid` (`FieldTooLong`) |
```

and in §10's code block add `FieldTooLong` to `ContentImportRule`, with the note: "Added during planning: without it, an
over-long title fails inside the transaction as a database error instead of being reported against its page."

- [ ] **Step 4: Commit** (check the time first)

```bash
git add src/neutral/Themia.Content/README.md CHANGELOG.md docs/superpowers/specs/2026-09-14-themia-content-design.md
git commit -m "docs(content): document the importer and its refusal rule"
```

---

### Task 19: Release 0.26.0

**Files:**
- Modify: `Directory.Build.props`
- Modify: `CHANGELOG.md`

Run after the checkpoint: after Task 18 for **GO**, or directly after Task 15 for **GO WITHOUT ITEM 4**.

- [ ] **Step 1: Confirm the whole suite on a clean tree**

```bash
git status --short
dotnet build Themia.sln --no-incremental
dotnet test Themia.sln --filter "Category!=Integration"
dotnet test tests/Themia.Content.IntegrationTests/Themia.Content.IntegrationTests.csproj
```

Expected: an empty status, a clean build, every test green. Record counts and durations.

- [ ] **Step 2: Version and changelog heading**

In `Directory.Build.props`, set `<Version>0.26.0</Version>`. In `CHANGELOG.md`, rename the populated `## [Unreleased]` section to `## [0.26.0] - YYYY-MM-DD` (today's date) and add an empty `## [Unreleased]` above it.

- [ ] **Step 3: Commit, push, and open the release pull request** (check the time first)

```bash
git add Directory.Build.props CHANGELOG.md
git commit -m "chore: release 0.26.0"
git push
```

Follow the release procedure in `docs/superpowers/specs/2026-06-01-themia-release-strategy-design.md` for tagging after merge. After the packages are on NuGet, set coord #0130 to `released` with version `0.26.0`, naming what shipped and — for **GO WITHOUT ITEM 4** — when the importer follows.

---

## Self-review

Run before handing the plan over; fix inline.

**Spec coverage.** §2 package shape → Tasks 1, 4, 9, 10, 12. §3 API → Tasks 1, 3, 5. §4 schema, dialect, ledger, registration → Task 4 (+9, 10). §5 write path, READ COMMITTED, savepoint, revert, publish, seeding → Tasks 5–8 (+9 for the MySQL isolation proof). §6 read path → Task 5. §7 markdown rules → Task 2. §8 renderer and fixture → Task 14. §9 AspNetCore → Tasks 12–13. §10 importer → Tasks 16–18. §11 errors and logging → Task 5 (`Logged`, propagation), Task 13 (filter). §12 acceptance table → falsification steps in Tasks 2, 5, 6, 7, 9, 11, 13, 16, 17. §13 findings → coord #0132, and Task 17 Step 1 verifies the SQL sent there. §14 release and checkpoint → Task 15, Checkpoint, Task 19.

**Deviations from the spec, each recorded where it happens.** Seeding semantics pinned to a constant `AppliesToVersion` (Task 8 corrects §5). `ContentImportRule.FieldTooLong` added (Task 18 updates §10). PostgreSQL lands with the core as block 1's proving engine, where coord #0130 [8] listed all engines as block 2 — no effect on the checkpoint, since blocks 1–4 still carry the whole ezy-assets scope.

**Names used across tasks.** `IContentPageDialect` members (Task 4 table) are the only SQL names any later task uses. `ContentEngineFixture.Service`, `Options`, `NewService`, `Importer`, `ClearContentAsync`, `Time`, `Dialect`, `ConnectionString`, `MigrationAdapter`, `TableExistsAsync`, `IndexExistsAsync`. Collections `PostgresContentCollection`, `MySqlContentCollection`, `SqlServerContentCollection`. `ContentTestData.NewSlug`, `ContentTestData.Save`.
