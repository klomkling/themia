# Themia.AspNetCore Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `Themia.AspNetCore` — a standalone, framework-neutral package providing a typed exception hierarchy and an RFC-7807 ProblemDetails middleware for ASP.NET Core apps.

**Architecture:** Pure ASP.NET Core. A small abstract `ThemiaException` base + sealed domain exceptions (HTTP-agnostic). A middleware catches them and writes `application/problem+json` with `traceId`/`errorCode`/metadata extensions, mapping each type to a status code. No dependency on `Themia.Framework.*`, no DB, no Serenity — so it ships before the framework rename and is usable by any app (incl. PowerACC, net8).

**Tech Stack:** .NET 8 + .NET 10 (`net8.0;net10.0`), `Microsoft.AspNetCore.App` FrameworkReference, System.Text.Json, xUnit, `Microsoft.AspNetCore.TestHost`.

---

## File Structure

```
Packages/themia/
  Themia.sln
  Directory.Build.props              # shared: nullable, langversion, warnings-as-errors, TFMs
  Directory.Packages.props           # central package versions
  src/neutral/Themia.AspNetCore/
    Themia.AspNetCore.csproj
    Exceptions/ThemiaException.cs       # abstract base (ErrorCode, Metadata)
    Exceptions/ValidationException.cs   # + PropertyName  → 400
    Exceptions/NotFoundException.cs     # → 404
    Exceptions/ConflictException.cs     # → 409
    Exceptions/ForbiddenException.cs    # → 403
    Exceptions/UnauthorizedException.cs # → 401
    Exceptions/ExternalServiceException.cs # + ServiceName → 503
    ProblemDetailsMiddleware.cs         # catch → ProblemDetails
    ApplicationBuilderExtensions.cs     # UseThemiaProblemDetails()
    PublicAPI.Shipped.txt
    PublicAPI.Unshipped.txt
  tests/Themia.AspNetCore.Tests/
    Themia.AspNetCore.Tests.csproj
    ProblemDetailsMiddlewareTests.cs    # unit (DefaultHttpContext)
    UseThemiaProblemDetailsTests.cs     # integration (TestServer)
```

Design notes:
- **Exceptions are HTTP-agnostic** — no `StatusCode` property. The middleware owns the type→status map. This keeps the exceptions usable in non-HTTP contexts and the mapping in one place.
- **No `ErrorCodes` constants shipped** — `ErrorCode` is a free `string?`; consumers define their own codes (app-domain stays out of the framework, per spec MINOR 8).

---

### Task 1: Scaffold the Themia solution + Themia.AspNetCore project skeleton

**Files:**
- **Already exists** (created with CI/CD setup): `Packages/themia/Directory.Build.props`, `LICENSE`, `.github/`
- Create: `Packages/themia/Directory.Packages.props`
- Create: `Packages/themia/src/neutral/Themia.AspNetCore/Themia.AspNetCore.csproj`
- Create: `Packages/themia/src/neutral/Themia.AspNetCore/PublicAPI.Shipped.txt` (empty)
- Create: `Packages/themia/src/neutral/Themia.AspNetCore/PublicAPI.Unshipped.txt` (empty)
- Create: `Packages/themia/tests/Themia.AspNetCore.Tests/Themia.AspNetCore.Tests.csproj`
- Create: `Packages/themia/Themia.sln`

- [ ] **Step 1: `Directory.Build.props` already exists — verify, do not recreate**

It was created with the CI/CD setup and holds compiler settings, the **shared `<Version>0.1.0</Version>`** (all packages release together), and package metadata (Authors/Company/MIT/RepositoryUrl/symbols). **It deliberately does NOT set `TargetFrameworks`** — TFMs vary per layer (neutral cores `net8.0;net10.0`, framework/modules `net10.0`), so each csproj sets its own. Confirm it is present:

Run: `test -f Packages/themia/Directory.Build.props && echo OK`
Expected: `OK`. (If missing, see the CI/CD setup; do not invent a new one.)

- [ ] **Step 2: Create `Directory.Packages.props`**

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageVersion Include="xunit" Version="2.9.2" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageVersion Include="Microsoft.AspNetCore.TestHost" Version="8.0.8" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Create `src/neutral/Themia.AspNetCore/Themia.AspNetCore.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- Neutral cross-framework package: MUST include net8.0 (PowerACC reuse). -->
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <PackageId>Themia.AspNetCore</PackageId>
    <Description>Typed exceptions + RFC-7807 ProblemDetails middleware for ASP.NET Core. Framework-neutral.</Description>
    <!-- Version is inherited from Directory.Build.props (shared 0.1.0). Do not set it here. -->
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="PublicAPI.Shipped.txt" />
    <AdditionalFiles Include="PublicAPI.Unshipped.txt" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create empty `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`**

Both files: a single line containing `#nullable enable`

- [ ] **Step 5: Create `tests/Themia.AspNetCore.Tests/Themia.AspNetCore.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <IsPackable>false</IsPackable>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.AspNetCore.TestHost" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/neutral/Themia.AspNetCore/Themia.AspNetCore.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Create the solution and add projects**

Run:
```bash
cd Packages/themia
dotnet new sln -n Themia
dotnet sln add src/neutral/Themia.AspNetCore/Themia.AspNetCore.csproj
dotnet sln add tests/Themia.AspNetCore.Tests/Themia.AspNetCore.Tests.csproj
```

- [ ] **Step 7: Verify the solution restores and builds (empty)**

Run: `dotnet build Packages/themia/Themia.sln`
Expected: Build succeeded, 0 warnings, 0 errors (both net8.0 and net10.0).

- [ ] **Step 8: Commit**

```bash
git add Packages/themia
git commit -m "chore: scaffold Themia solution and Themia.AspNetCore project"
```

---

### Task 2: `ThemiaException` base

**Files:**
- Create: `Packages/themia/src/neutral/Themia.AspNetCore/Exceptions/ThemiaException.cs`
- Test: `Packages/themia/tests/Themia.AspNetCore.Tests/ExceptionTests.cs`

- [ ] **Step 1: Write the failing test**

In `ExceptionTests.cs`:
```csharp
using System.Collections.Generic;
using Themia.AspNetCore.Exceptions;
using Xunit;

namespace Themia.AspNetCore.Tests;

public sealed class ExceptionTests
{
    [Fact]
    public void ThemiaException_carries_message_errorCode_metadata()
    {
        var meta = new Dictionary<string, object?> { ["k"] = "v" };
        var ex = new NotFoundException("nope", errorCode: "E1", metadata: meta);

        Assert.Equal("nope", ex.Message);
        Assert.Equal("E1", ex.ErrorCode);
        Assert.Equal("v", ex.Metadata!["k"]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Packages/themia/Themia.sln`
Expected: FAIL — `NotFoundException`/`ThemiaException` not defined (compile error).

- [ ] **Step 3: Write `ThemiaException`**

```csharp
using System;
using System.Collections.Generic;

namespace Themia.AspNetCore.Exceptions;

/// <summary>Base type for Themia domain exceptions surfaced as RFC-7807 problem responses.</summary>
public abstract class ThemiaException : Exception
{
    /// <summary>Creates a new <see cref="ThemiaException"/>.</summary>
    protected ThemiaException(
        string message,
        string? errorCode = null,
        IReadOnlyDictionary<string, object?>? metadata = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        Metadata = metadata;
    }

    /// <summary>Optional machine-readable error code. Consumers define their own values.</summary>
    public string? ErrorCode { get; }

    /// <summary>Optional extra key/values surfaced as ProblemDetails extensions.</summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; }
}
```

(Task 3 creates `NotFoundException`; this test will compile once Task 3's `NotFoundException` exists. If implementing strictly in order, add a minimal `NotFoundException` stub now and flesh it out in Task 3.)

- [ ] **Step 4: Add minimal `NotFoundException` stub so the test compiles**

Create `Exceptions/NotFoundException.cs`:
```csharp
using System.Collections.Generic;

namespace Themia.AspNetCore.Exceptions;

/// <summary>Requested resource does not exist. Maps to HTTP 404.</summary>
public sealed class NotFoundException(
    string message,
    string? errorCode = null,
    IReadOnlyDictionary<string, object?>? metadata = null)
    : ThemiaException(message, errorCode, metadata);
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Packages/themia/Themia.sln`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Packages/themia
git commit -m "feat: add ThemiaException base and NotFoundException"
```

---

### Task 3: Remaining typed exceptions

**Files:**
- Create: `Exceptions/ValidationException.cs`, `ConflictException.cs`, `ForbiddenException.cs`, `UnauthorizedException.cs`, `ExternalServiceException.cs`
- Test: append to `ExceptionTests.cs`

- [ ] **Step 1: Write the failing test (append to `ExceptionTests.cs`)**

```csharp
    [Fact]
    public void Validation_and_external_carry_extra_fields()
    {
        var v = new ValidationException("Email", "bad", errorCode: "INVALID");
        Assert.Equal("Email", v.PropertyName);
        Assert.Equal("INVALID", v.ErrorCode);

        var e = new ExternalServiceException("payments", "down");
        Assert.Equal("payments", e.ServiceName);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Packages/themia/Themia.sln`
Expected: FAIL — `ValidationException`/`ExternalServiceException` not defined.

- [ ] **Step 3: Implement the five exceptions**

`Exceptions/ValidationException.cs`:
```csharp
using System.Collections.Generic;

namespace Themia.AspNetCore.Exceptions;

/// <summary>Input failed validation. Maps to HTTP 400.</summary>
public sealed class ValidationException(
    string propertyName,
    string message,
    string? errorCode = null,
    IReadOnlyDictionary<string, object?>? metadata = null)
    : ThemiaException(message, errorCode, metadata)
{
    /// <summary>The offending property/field name.</summary>
    public string PropertyName { get; } = propertyName;
}
```

`Exceptions/ConflictException.cs`:
```csharp
using System.Collections.Generic;

namespace Themia.AspNetCore.Exceptions;

/// <summary>State conflict (e.g. duplicate). Maps to HTTP 409.</summary>
public sealed class ConflictException(
    string message,
    string? errorCode = null,
    IReadOnlyDictionary<string, object?>? metadata = null)
    : ThemiaException(message, errorCode, metadata);
```

`Exceptions/ForbiddenException.cs`:
```csharp
using System.Collections.Generic;

namespace Themia.AspNetCore.Exceptions;

/// <summary>Authenticated but not permitted. Maps to HTTP 403.</summary>
public sealed class ForbiddenException(
    string message,
    string? errorCode = null,
    IReadOnlyDictionary<string, object?>? metadata = null)
    : ThemiaException(message, errorCode, metadata);
```

`Exceptions/UnauthorizedException.cs`:
```csharp
using System.Collections.Generic;

namespace Themia.AspNetCore.Exceptions;

/// <summary>Not authenticated. Maps to HTTP 401.</summary>
public sealed class UnauthorizedException(
    string message,
    string? errorCode = null,
    IReadOnlyDictionary<string, object?>? metadata = null)
    : ThemiaException(message, errorCode, metadata);
```

`Exceptions/ExternalServiceException.cs`:
```csharp
using System;

namespace Themia.AspNetCore.Exceptions;

/// <summary>A downstream dependency failed. Maps to HTTP 503.</summary>
public sealed class ExternalServiceException(
    string serviceName,
    string message,
    Exception? innerException = null)
    : ThemiaException(message, innerException: innerException)
{
    /// <summary>Name of the failing downstream service.</summary>
    public string ServiceName { get; } = serviceName;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Packages/themia/Themia.sln`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Packages/themia
git commit -m "feat: add typed Themia exceptions (validation/conflict/forbidden/unauthorized/external)"
```

---

### Task 4: `ProblemDetailsMiddleware` — status mapping for the four 4xx domain exceptions

**Files:**
- Create: `Packages/themia/src/neutral/Themia.AspNetCore/ProblemDetailsMiddleware.cs`
- Test: `Packages/themia/tests/Themia.AspNetCore.Tests/ProblemDetailsMiddlewareTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.AspNetCore.Exceptions;
using Xunit;

namespace Themia.AspNetCore.Tests;

public sealed class ProblemDetailsMiddlewareTests
{
    private static async Task<(int status, JsonElement body)> InvokeWith(Exception toThrow)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = "/x";
        ctx.Response.Body = new MemoryStream();

        var mw = new ProblemDetailsMiddleware(
            _ => throw toThrow,
            NullLogger<ProblemDetailsMiddleware>.Instance);
        await mw.InvokeAsync(ctx);

        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        var json = await reader.ReadToEndAsync();
        return (ctx.Response.StatusCode, JsonDocument.Parse(json).RootElement.Clone());
    }

    [Theory]
    [InlineData(typeof(NotFoundException), 404)]
    [InlineData(typeof(ConflictException), 409)]
    [InlineData(typeof(ForbiddenException), 403)]
    [InlineData(typeof(UnauthorizedException), 401)]
    public async Task Maps_domain_exception_to_status(Type exType, int expected)
    {
        var ex = (Exception)Activator.CreateInstance(exType, "boom", null, null)!;
        var (status, body) = await InvokeWith(ex);

        Assert.Equal(expected, status);
        Assert.Equal(expected, body.GetProperty("status").GetInt32());
        Assert.Equal("boom", body.GetProperty("detail").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Packages/themia/Themia.sln --filter ProblemDetailsMiddlewareTests`
Expected: FAIL — `ProblemDetailsMiddleware` not defined.

- [ ] **Step 3: Implement the middleware (4xx domain cases + helper)**

```csharp
using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Themia.AspNetCore.Exceptions;

namespace Themia.AspNetCore;

/// <summary>Catches <see cref="ThemiaException"/>s (and unhandled exceptions) and writes RFC-7807 responses.</summary>
public sealed class ProblemDetailsMiddleware(
    RequestDelegate next,
    ILogger<ProblemDetailsMiddleware> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Processes the request, translating thrown exceptions into problem responses.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        try
        {
            await next(context);
        }
        catch (NotFoundException ex) { await Write(context, 404, "Not Found", ex, traceId, LogLevel.Warning); }
        catch (ConflictException ex) { await Write(context, 409, "Conflict", ex, traceId, LogLevel.Warning); }
        catch (ForbiddenException ex) { await Write(context, 403, "Forbidden", ex, traceId, LogLevel.Warning); }
        catch (UnauthorizedException ex) { await Write(context, 401, "Unauthorized", ex, traceId, LogLevel.Warning); }
    }

    private async Task Write(HttpContext ctx, int status, string title, ThemiaException ex, string traceId, LogLevel level)
    {
        logger.Log(level, ex, "{Title} for {Method} {Path} (TraceId: {TraceId})",
            title, ctx.Request.Method, ctx.Request.Path, traceId);

        ctx.Response.ContentType = "application/problem+json";
        ctx.Response.StatusCode = status;

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = ex.Message,
            Instance = ctx.Request.Path,
        };
        problem.Extensions["traceId"] = traceId;
        if (ex.ErrorCode is not null) problem.Extensions["errorCode"] = ex.ErrorCode;
        if (ex.Metadata is not null)
            foreach (var pair in ex.Metadata)
                problem.Extensions[pair.Key] = pair.Value;

        await ctx.Response.WriteAsync(JsonSerializer.Serialize(problem, Json));
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Packages/themia/Themia.sln --filter ProblemDetailsMiddlewareTests`
Expected: PASS (4 theory cases).

- [ ] **Step 5: Commit**

```bash
git add Packages/themia
git commit -m "feat: ProblemDetailsMiddleware maps 4xx domain exceptions"
```

---

### Task 5: Validation (400 + field), ExternalService (503), unknown (500), and extensions

**Files:**
- Modify: `Packages/themia/src/neutral/Themia.AspNetCore/ProblemDetailsMiddleware.cs`
- Test: append to `ProblemDetailsMiddlewareTests.cs`

- [ ] **Step 1: Write the failing tests (append)**

```csharp
    [Fact]
    public async Task Validation_returns_400_with_field_and_errorCode()
    {
        var (status, body) = await InvokeWith(new ValidationException("Email", "bad", errorCode: "INVALID"));

        Assert.Equal(400, status);
        Assert.Equal("INVALID", body.GetProperty("errorCode").GetString());
        Assert.Equal("bad", body.GetProperty("errors").GetProperty("Email")[0].GetString());
    }

    [Fact]
    public async Task ExternalService_returns_503()
    {
        var (status, _) = await InvokeWith(new ExternalServiceException("payments", "down"));
        Assert.Equal(503, status);
    }

    [Fact]
    public async Task Unknown_exception_returns_500_without_leaking_message()
    {
        var (status, body) = await InvokeWith(new InvalidOperationException("secret internals"));
        Assert.Equal(500, status);
        Assert.DoesNotContain("secret internals", body.GetProperty("detail").GetString());
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Packages/themia/Themia.sln --filter ProblemDetailsMiddlewareTests`
Expected: FAIL — validation/external/unknown not yet handled (no catch clause → exception propagates).

- [ ] **Step 3: Extend the middleware**

Add these catch clauses in `InvokeAsync` **before** any broader clause, in this order — `ValidationException` and `ExternalServiceException` first (they are not caught by the four existing clauses), then a final `Exception` fallback:

```csharp
        catch (ValidationException ex) { await WriteValidation(context, ex, traceId); }
        catch (ExternalServiceException ex)
        {
            logger.LogError(ex, "External service {Service} failed for {Method} {Path} (TraceId: {TraceId})",
                ex.ServiceName, context.Request.Method, context.Request.Path, traceId);
            await Write(context, 503, "Service unavailable", ex, traceId, LogLevel.Error);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path} (TraceId: {TraceId})",
                context.Request.Method, context.Request.Path, traceId);
            await WriteGeneric(context, 500, "Server error", "An unexpected error occurred.", traceId);
        }
```

Note: `ExternalServiceException` logs at Error then calls the shared `Write` (which also logs at the passed level) — to avoid double logging, call `Write(context, 503, "Service unavailable", ex, traceId, LogLevel.Error)` directly and drop the separate `logger.LogError` line above it. Final form of that clause:
```csharp
        catch (ExternalServiceException ex) { await Write(context, 503, "Service unavailable", ex, traceId, LogLevel.Error); }
```

Add the two helpers:
```csharp
    private async Task WriteValidation(HttpContext ctx, ValidationException ex, string traceId)
    {
        logger.LogWarning(ex, "Validation error for {Method} {Path} (TraceId: {TraceId})",
            ctx.Request.Method, ctx.Request.Path, traceId);

        ctx.Response.ContentType = "application/problem+json";
        ctx.Response.StatusCode = 400;

        var problem = new ValidationProblemDetails(
            new Dictionary<string, string[]> { [ex.PropertyName] = [ex.Message] })
        {
            Status = 400,
            Title = "Validation error",
            Detail = ex.Message,
            Instance = ctx.Request.Path,
        };
        problem.Extensions["traceId"] = traceId;
        if (ex.ErrorCode is not null) problem.Extensions["errorCode"] = ex.ErrorCode;
        if (ex.Metadata is not null)
            foreach (var pair in ex.Metadata) problem.Extensions[pair.Key] = pair.Value;

        await ctx.Response.WriteAsync(JsonSerializer.Serialize(problem, Json));
    }

    private async Task WriteGeneric(HttpContext ctx, int status, string title, string detail, string traceId)
    {
        ctx.Response.ContentType = "application/problem+json";
        ctx.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail, Instance = ctx.Request.Path };
        problem.Extensions["traceId"] = traceId;
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(problem, Json));
    }
```

Add `using System.Collections.Generic;` to the file.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Packages/themia/Themia.sln --filter ProblemDetailsMiddlewareTests`
Expected: PASS (all cases incl. earlier 4xx).

- [ ] **Step 5: Commit**

```bash
git add Packages/themia
git commit -m "feat: ProblemDetailsMiddleware handles validation/external/unknown + extensions"
```

---

### Task 6: `UseThemiaProblemDetails()` extension + integration test

**Files:**
- Create: `Packages/themia/src/neutral/Themia.AspNetCore/ApplicationBuilderExtensions.cs`
- Test: `Packages/themia/tests/Themia.AspNetCore.Tests/UseThemiaProblemDetailsTests.cs`

- [ ] **Step 1: Write the failing integration test**

```csharp
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Themia.AspNetCore;
using Themia.AspNetCore.Exceptions;
using Xunit;

namespace Themia.AspNetCore.Tests;

public sealed class UseThemiaProblemDetailsTests
{
    [Fact]
    public async Task Middleware_translates_exception_end_to_end()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app =>
                {
                    app.UseThemiaProblemDetails();
                    app.Run(_ => throw new NotFoundException("missing"));
                }))
            .StartAsync();

        var response = await host.GetTestClient().GetAsync("/anything");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Packages/themia/Themia.sln --filter UseThemiaProblemDetailsTests`
Expected: FAIL — `UseThemiaProblemDetails` not defined.

- [ ] **Step 3: Implement the extension**

```csharp
using Microsoft.AspNetCore.Builder;

namespace Themia.AspNetCore;

/// <summary>Registration helpers for Themia's ProblemDetails middleware.</summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>Adds <see cref="ProblemDetailsMiddleware"/> to the pipeline. Place it early, before MVC/endpoints.</summary>
    public static IApplicationBuilder UseThemiaProblemDetails(this IApplicationBuilder app)
        => app.UseMiddleware<ProblemDetailsMiddleware>();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Packages/themia/Themia.sln --filter UseThemiaProblemDetailsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Packages/themia
git commit -m "feat: add UseThemiaProblemDetails() extension"
```

---

### Task 7: Finalize PublicAPI + full build/test on both TFMs

**Files:**
- Modify: `Packages/themia/src/neutral/Themia.AspNetCore/PublicAPI.Unshipped.txt`

- [ ] **Step 1: Build to surface any `RS0016` (undocumented public API) diagnostics**

Run: `dotnet build Packages/themia/Themia.sln`
Expected: Build succeeds. If the PublicApiAnalyzer package is wired into the repo, it will list public members to add to `PublicAPI.Unshipped.txt`. If the analyzer is NOT present in this solution, skip the PublicAPI edits — the empty files are fine — and proceed.

- [ ] **Step 2: Populate `PublicAPI.Unshipped.txt`**

Add (after the `#nullable enable` line) every public member, e.g.:
```
Themia.AspNetCore.ApplicationBuilderExtensions
static Themia.AspNetCore.ApplicationBuilderExtensions.UseThemiaProblemDetails(Microsoft.AspNetCore.Builder.IApplicationBuilder! app) -> Microsoft.AspNetCore.Builder.IApplicationBuilder!
Themia.AspNetCore.ProblemDetailsMiddleware
Themia.AspNetCore.ProblemDetailsMiddleware.ProblemDetailsMiddleware(Microsoft.AspNetCore.Http.RequestDelegate! next, Microsoft.Extensions.Logging.ILogger<Themia.AspNetCore.ProblemDetailsMiddleware!>! logger) -> void
Themia.AspNetCore.ProblemDetailsMiddleware.InvokeAsync(Microsoft.AspNetCore.Http.HttpContext! context) -> System.Threading.Tasks.Task!
Themia.AspNetCore.Exceptions.ThemiaException
Themia.AspNetCore.Exceptions.ThemiaException.ErrorCode.get -> string?
Themia.AspNetCore.Exceptions.ThemiaException.Metadata.get -> System.Collections.Generic.IReadOnlyDictionary<string!, object?>?
Themia.AspNetCore.Exceptions.ValidationException
Themia.AspNetCore.Exceptions.ValidationException.PropertyName.get -> string!
Themia.AspNetCore.Exceptions.NotFoundException
Themia.AspNetCore.Exceptions.ConflictException
Themia.AspNetCore.Exceptions.ForbiddenException
Themia.AspNetCore.Exceptions.UnauthorizedException
Themia.AspNetCore.Exceptions.ExternalServiceException
Themia.AspNetCore.Exceptions.ExternalServiceException.ServiceName.get -> string!
```
(Adjust to the analyzer's exact expected entries — constructors of the exception types included if it asks.)

- [ ] **Step 3: Full verification on both target frameworks**

Run:
```bash
dotnet build Packages/themia/Themia.sln --no-incremental
dotnet test Packages/themia/Themia.sln
```
Expected: Build succeeds for net8.0 and net10.0; all tests pass on both TFMs; 0 warnings (warnings-as-errors on).

- [ ] **Step 4: Commit**

```bash
git add Packages/themia
git commit -m "chore: finalize Themia.AspNetCore public API surface"
```

---

## Notes for the implementer

- **Catch ordering matters.** In `InvokeAsync`, the concrete `catch` clauses must precede the final `catch (Exception)`. `ValidationException`/`ExternalServiceException` are not subtypes of the four 4xx exceptions, so their order among the concrete clauses is free — but all concrete clauses must come before `catch (Exception)`.
- **Do not add a `StatusCode` to the exceptions.** The type→status map lives only in the middleware (one place).
- **Do not introduce `Newtonsoft.Json`** — this package is System.Text.Json only.
- **No `Console.Error.WriteLine`** anywhere — log via `ILogger` only (this is the deliberate fix vs. the ezy-assets source).
