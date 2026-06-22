# Themia.Notifications (neutral sending core) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `Themia.Notifications` — a neutral, stateless package that sends notifications over channel senders (`IEmailSender` / `ISmsSender` / `IPushSender`) with bodies templated by Handlebars.Net, plus an SMTP email provider, an HTTP-SMS provider base, dev logger stubs, and an `AddThemiaNotifications` DI extension.

**Architecture:** One package `src/neutral/Themia.Notifications` (TFM `net8.0;net10.0`), mirroring the `Themia.Pdf` neutral core. Stateless senders + a Handlebars renderer (Handlebars.Net **directly** — NOT via `Themia.Pdf`, which would drag in PuppeteerSharp/Chromium). No framework / EF / Dapper / database dependency. The stateful machinery (outbox, drainer, dispatcher, preferences, in-app, per-tenant config) is a **separate plan** (`Themia.Modules.Notifications`) built next.

**Tech Stack:** .NET 8 + .NET 10, Handlebars.Net (already pinned), `System.Net.Mail` (BCL) for SMTP, `HttpClient` for HTTP-SMS, xUnit, Microsoft.Extensions.{DependencyInjection,Logging}.Abstractions.

**Spec:** `docs/superpowers/specs/2026-06-22-themia-notifications-design.md` (this plan implements the **neutral-core layer** only).

**Version note:** This is sub-project #1 of the Notifications spec. With the phased build, the core ships at **0.6.2** and the module ships at **0.6.3** (single-version monorepo; the spec's "0.6.2" referred to Notifications-as-one). Confirm with the maintainer at release if both should land in one version instead.

---

## File Structure

**Production (`src/neutral/Themia.Notifications/`):**
- `Themia.Notifications.csproj` — package def, TFM, deps (Handlebars.Net + DI/Logging abstractions), PublicAPI.
- `AssemblyInfo.cs` — `InternalsVisibleTo("Themia.Notifications.Tests")`.
- `NotificationChannel.cs` — public enum (Email/Sms/InApp/Push).
- `NotificationMessage.cs` — public sealed class (init-only): channel, recipient, subject, body, template, model, metadata.
- `NotificationResult.cs` — public sealed class: success flag, provider message id, error.
- `INotificationTemplateRenderer.cs` — public interface (`Render(template, model) -> string`).
- `HandlebarsNotificationRenderer.cs` — `internal` Handlebars.Net impl.
- `IEmailSender.cs`, `ISmsSender.cs`, `IPushSender.cs` — public sender interfaces.
- `Providers/LoggerEmailSender.cs`, `Providers/LoggerSmsSender.cs` — `internal` dev stubs (log + success).
- `Providers/SmtpEmailSender.cs` + `SmtpEmailOptions.cs` — `internal` sender + public options (System.Net.Mail).
- `Providers/HttpSmsSenderBase.cs` — public `abstract` base for HTTP SMS providers.
- `ThemiaNotificationsOptions.cs` — public options (ConfigureHandlebars hook).
- `ThemiaNotificationsServiceCollectionExtensions.cs` — public `AddThemiaNotifications` (namespace `Microsoft.Extensions.DependencyInjection`).
- `PublicAPI.Shipped.txt` (empty), `PublicAPI.Unshipped.txt` (curated).

**Tests (`tests/Themia.Notifications.Tests/`):** unit tests, no network (SMTP via pickup-directory).

**Repo-wide:** `Themia.sln` — add the two projects; `Directory.Build.props` `<Version>` `0.6.1 → 0.6.2`; `CHANGELOG.md`.

---

### Task 1: Scaffold the package

**Files:**
- Create: `src/neutral/Themia.Notifications/Themia.Notifications.csproj`
- Create: `src/neutral/Themia.Notifications/AssemblyInfo.cs`
- Create: `src/neutral/Themia.Notifications/PublicAPI.Shipped.txt` (empty)
- Create: `src/neutral/Themia.Notifications/PublicAPI.Unshipped.txt` (header only)

(`Handlebars.Net` and the MS abstractions are already pinned in `Directory.Packages.props` — no new pins; SMTP uses `System.Net.Mail` from the BCL.)

- [ ] **Step 1: Create the csproj**

`src/neutral/Themia.Notifications/Themia.Notifications.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <RootNamespace>Themia.Notifications</RootNamespace>
    <PackageId>Themia.Notifications</PackageId>
    <Description>Themia neutral notification sending core — channel senders (email/SMS/push) with Handlebars.Net templating, an SMTP provider, an HTTP-SMS base, and dev logger stubs. Stateless; no tenant or database dependency.</Description>
    <PackageTags>themia;notifications;email;sms;smtp;handlebars</PackageTags>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Handlebars.Net" />
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

- [ ] **Step 2: AssemblyInfo + PublicAPI files**

`src/neutral/Themia.Notifications/AssemblyInfo.cs`:
```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Themia.Notifications.Tests")]
```

`src/neutral/Themia.Notifications/PublicAPI.Shipped.txt`: empty file.
`src/neutral/Themia.Notifications/PublicAPI.Unshipped.txt`:
```
#nullable enable
```

- [ ] **Step 3: Add to the solution**

Run: `dotnet sln Themia.sln add src/neutral/Themia.Notifications/Themia.Notifications.csproj`

- [ ] **Step 4: Verify it builds**

Run: `dotnet build src/neutral/Themia.Notifications/Themia.Notifications.csproj`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/neutral/Themia.Notifications Themia.sln
git commit -m "feat: scaffold Themia.Notifications neutral package"
```

---

### Task 2: Message model — channel, message, result

**Files:**
- Create: `src/neutral/Themia.Notifications/NotificationChannel.cs`
- Create: `src/neutral/Themia.Notifications/NotificationMessage.cs`
- Create: `src/neutral/Themia.Notifications/NotificationResult.cs`
- Modify: `src/neutral/Themia.Notifications/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Notifications.Tests/NotificationMessageTests.cs` (created with the test project in this task)

- [ ] **Step 1: Create the test project**

`tests/Themia.Notifications.Tests/Themia.Notifications.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <!-- DI + Logging (AddLogging, ServiceCollection, NullLogger) via the shared framework, matching Themia.Pdf.Tests. -->
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
    <ProjectReference Include="../../src/neutral/Themia.Notifications/Themia.Notifications.csproj" />
  </ItemGroup>
</Project>
```
Then: `dotnet sln Themia.sln add tests/Themia.Notifications.Tests/Themia.Notifications.Tests.csproj`

- [ ] **Step 2: Write the failing test**

`tests/Themia.Notifications.Tests/NotificationMessageTests.cs`:
```csharp
using Themia.Notifications;
using Xunit;

namespace Themia.Notifications.Tests;

public sealed class NotificationMessageTests
{
    [Fact]
    public void Message_HoldsInitOnlyValues()
    {
        var m = new NotificationMessage
        {
            Channel = NotificationChannel.Email,
            Recipient = "a@b.com",
            Subject = "Hi",
            Template = "<p>{{name}}</p>",
            Model = new { name = "Sam" },
        };

        Assert.Equal(NotificationChannel.Email, m.Channel);
        Assert.Equal("a@b.com", m.Recipient);
        Assert.Equal("Hi", m.Subject);
        Assert.Null(m.Body);
    }

    [Fact]
    public void Result_SuccessAndFailureFactories()
    {
        var ok = NotificationResult.Success("id-1");
        var bad = NotificationResult.Failure("smtp down");

        Assert.True(ok.Succeeded);
        Assert.Equal("id-1", ok.ProviderMessageId);
        Assert.False(bad.Succeeded);
        Assert.Equal("smtp down", bad.Error);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Themia.Notifications.Tests --filter NotificationMessageTests`
Expected: FAIL — types don't exist.

- [ ] **Step 4: Implement the model**

`NotificationChannel.cs`:
```csharp
namespace Themia.Notifications;

/// <summary>The delivery channel for a notification.</summary>
public enum NotificationChannel
{
    /// <summary>Email.</summary>
    Email = 0,
    /// <summary>SMS / text message.</summary>
    Sms = 1,
    /// <summary>In-app notification record.</summary>
    InApp = 2,
    /// <summary>Mobile/web push (provider seam; no built-in provider).</summary>
    Push = 3,
}
```

`NotificationMessage.cs`:
```csharp
namespace Themia.Notifications;

/// <summary>A single notification to send. Either <see cref="Body"/> is pre-rendered, or
/// <see cref="Template"/> + <see cref="Model"/> are merged by an <see cref="INotificationTemplateRenderer"/>.</summary>
public sealed class NotificationMessage
{
    /// <summary>The delivery channel.</summary>
    public NotificationChannel Channel { get; init; }

    /// <summary>The recipient address (email address, phone number, or user id for in-app).</summary>
    public string Recipient { get; init; } = string.Empty;

    /// <summary>Subject line (email); ignored by channels without a subject.</summary>
    public string? Subject { get; init; }

    /// <summary>Pre-rendered body. When set, it is used verbatim and <see cref="Template"/> is ignored.</summary>
    public string? Body { get; init; }

    /// <summary>Handlebars template source, merged with <see cref="Model"/> when <see cref="Body"/> is null.</summary>
    public string? Template { get; init; }

    /// <summary>The model merged into <see cref="Template"/>.</summary>
    public object? Model { get; init; }

    /// <summary>Optional channel/provider metadata (e.g. cc, sender id).</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
```

`NotificationResult.cs`:
```csharp
namespace Themia.Notifications;

/// <summary>The outcome of a send attempt.</summary>
public sealed class NotificationResult
{
    private NotificationResult(bool succeeded, string? providerMessageId, string? error)
    {
        Succeeded = succeeded;
        ProviderMessageId = providerMessageId;
        Error = error;
    }

    /// <summary>Whether the provider accepted the message.</summary>
    public bool Succeeded { get; }

    /// <summary>The provider's message id, when it returns one.</summary>
    public string? ProviderMessageId { get; }

    /// <summary>The failure description when <see cref="Succeeded"/> is false.</summary>
    public string? Error { get; }

    /// <summary>Creates a success result.</summary>
    public static NotificationResult Success(string? providerMessageId = null) => new(true, providerMessageId, null);

    /// <summary>Creates a failure result.</summary>
    public static NotificationResult Failure(string error) => new(false, null, error);
}
```

- [ ] **Step 5: Record public API**

Append to `PublicAPI.Unshipped.txt`:
```
Themia.Notifications.NotificationChannel
Themia.Notifications.NotificationChannel.Email = 0 -> Themia.Notifications.NotificationChannel
Themia.Notifications.NotificationChannel.Sms = 1 -> Themia.Notifications.NotificationChannel
Themia.Notifications.NotificationChannel.InApp = 2 -> Themia.Notifications.NotificationChannel
Themia.Notifications.NotificationChannel.Push = 3 -> Themia.Notifications.NotificationChannel
Themia.Notifications.NotificationMessage
Themia.Notifications.NotificationMessage.NotificationMessage() -> void
Themia.Notifications.NotificationMessage.Channel.get -> Themia.Notifications.NotificationChannel
Themia.Notifications.NotificationMessage.Channel.init -> void
Themia.Notifications.NotificationMessage.Recipient.get -> string!
Themia.Notifications.NotificationMessage.Recipient.init -> void
Themia.Notifications.NotificationMessage.Subject.get -> string?
Themia.Notifications.NotificationMessage.Subject.init -> void
Themia.Notifications.NotificationMessage.Body.get -> string?
Themia.Notifications.NotificationMessage.Body.init -> void
Themia.Notifications.NotificationMessage.Template.get -> string?
Themia.Notifications.NotificationMessage.Template.init -> void
Themia.Notifications.NotificationMessage.Model.get -> object?
Themia.Notifications.NotificationMessage.Model.init -> void
Themia.Notifications.NotificationMessage.Metadata.get -> System.Collections.Generic.IReadOnlyDictionary<string!, string!>?
Themia.Notifications.NotificationMessage.Metadata.init -> void
Themia.Notifications.NotificationResult
Themia.Notifications.NotificationResult.Succeeded.get -> bool
Themia.Notifications.NotificationResult.ProviderMessageId.get -> string?
Themia.Notifications.NotificationResult.Error.get -> string?
static Themia.Notifications.NotificationResult.Success(string? providerMessageId = null) -> Themia.Notifications.NotificationResult!
static Themia.Notifications.NotificationResult.Failure(string! error) -> Themia.Notifications.NotificationResult!
```
(The analyzer is the authority on exact text — if a clean build reports RS0016/RS0017, make the file match what it prints.)

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/Themia.Notifications.Tests --filter NotificationMessageTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/neutral/Themia.Notifications tests/Themia.Notifications.Tests
git commit -m "feat: add Notifications message model (channel, message, result)"
```

---

### Task 3: Template renderer (Handlebars.Net, directly)

**Files:**
- Create: `src/neutral/Themia.Notifications/INotificationTemplateRenderer.cs`
- Create: `src/neutral/Themia.Notifications/ThemiaNotificationsOptions.cs`
- Create: `src/neutral/Themia.Notifications/HandlebarsNotificationRenderer.cs`
- Modify: `PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Notifications.Tests/HandlebarsNotificationRendererTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Themia.Notifications;
using Xunit;

namespace Themia.Notifications.Tests;

public sealed class HandlebarsNotificationRendererTests
{
    private static HandlebarsNotificationRenderer New() => new(new ThemiaNotificationsOptions());

    [Fact]
    public void Render_MergesTemplateAndModel()
    {
        var html = New().Render("<p>Hi {{name}}</p>", new { name = "Sam" });
        Assert.Equal("<p>Hi Sam</p>", html);
    }

    [Fact]
    public void Render_NullTemplate_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => New().Render(null!, new { }));
    }

    [Fact]
    public void Render_ConfigureHandlebarsHook_RegistersHelper()
    {
        var opts = new ThemiaNotificationsOptions
        {
            ConfigureHandlebars = hb => hb.RegisterHelper("shout",
                (output, _, args) => output.WriteSafeString(args[0]?.ToString()!.ToUpperInvariant())),
        };
        var html = new HandlebarsNotificationRenderer(opts).Render("{{shout name}}", new { name = "hi" });
        Assert.Equal("HI", html);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Notifications.Tests --filter HandlebarsNotificationRendererTests`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Implement**

`INotificationTemplateRenderer.cs`:
```csharp
namespace Themia.Notifications;

/// <summary>Merges a Handlebars template with a model into a notification body.</summary>
public interface INotificationTemplateRenderer
{
    /// <summary>Compiles <paramref name="template"/> as Handlebars and renders it against <paramref name="model"/>.</summary>
    string Render(string template, object model);
}
```

`ThemiaNotificationsOptions.cs`:
```csharp
using HandlebarsDotNet;

namespace Themia.Notifications;

/// <summary>Process-wide configuration for the notification core.</summary>
public sealed class ThemiaNotificationsOptions
{
    /// <summary>Hook to register custom Handlebars helpers/partials at construction. Default <see langword="null"/>.</summary>
    public Action<IHandlebars>? ConfigureHandlebars { get; set; }
}
```

`HandlebarsNotificationRenderer.cs`:
```csharp
using HandlebarsDotNet;

namespace Themia.Notifications;

/// <summary>Handlebars.Net-backed <see cref="INotificationTemplateRenderer"/>. Thread-safe for rendering.
/// Uses Handlebars.Net directly (not via Themia.Pdf, which would pull in PuppeteerSharp/Chromium).</summary>
internal sealed class HandlebarsNotificationRenderer : INotificationTemplateRenderer
{
    private readonly IHandlebars _hbs;

    public HandlebarsNotificationRenderer(ThemiaNotificationsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _hbs = Handlebars.Create();
        options.ConfigureHandlebars?.Invoke(_hbs);
    }

    public string Render(string template, object model)
    {
        ArgumentNullException.ThrowIfNull(template);
        // ponytail: compiles per call. Add a bounded compiled-template cache keyed by `template` if profiling shows it matters.
        return _hbs.Compile(template)(model);
    }
}
```

- [ ] **Step 4: Record public API**

Append to `PublicAPI.Unshipped.txt`:
```
Themia.Notifications.INotificationTemplateRenderer
Themia.Notifications.INotificationTemplateRenderer.Render(string! template, object! model) -> string!
Themia.Notifications.ThemiaNotificationsOptions
Themia.Notifications.ThemiaNotificationsOptions.ThemiaNotificationsOptions() -> void
Themia.Notifications.ThemiaNotificationsOptions.ConfigureHandlebars.get -> System.Action<HandlebarsDotNet.IHandlebars!>?
Themia.Notifications.ThemiaNotificationsOptions.ConfigureHandlebars.set -> void
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Themia.Notifications.Tests --filter HandlebarsNotificationRendererTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Notifications tests/Themia.Notifications.Tests
git commit -m "feat: add Handlebars notification renderer (direct, no Pdf dependency)"
```

---

### Task 4: Sender interfaces + logger stubs

**Files:**
- Create: `src/neutral/Themia.Notifications/IEmailSender.cs`, `ISmsSender.cs`, `IPushSender.cs`
- Create: `src/neutral/Themia.Notifications/Providers/LoggerEmailSender.cs`, `Providers/LoggerSmsSender.cs`
- Modify: `PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Notifications.Tests/LoggerSenderTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Notifications;
using Themia.Notifications.Providers;
using Xunit;

namespace Themia.Notifications.Tests;

public sealed class LoggerSenderTests
{
    [Fact]
    public async Task LoggerEmail_ReturnsSuccess()
    {
        var sut = new LoggerEmailSender(NullLogger<LoggerEmailSender>.Instance);
        var r = await sut.SendAsync(new NotificationMessage { Channel = NotificationChannel.Email, Recipient = "a@b.com", Body = "hi" });
        Assert.True(r.Succeeded);
    }

    [Fact]
    public async Task LoggerSms_ReturnsSuccess()
    {
        var sut = new LoggerSmsSender(NullLogger<LoggerSmsSender>.Instance);
        var r = await sut.SendAsync(new NotificationMessage { Channel = NotificationChannel.Sms, Recipient = "+100", Body = "hi" });
        Assert.True(r.Succeeded);
    }

    [Fact]
    public async Task LoggerEmail_NullMessage_Throws()
    {
        var sut = new LoggerEmailSender(NullLogger<LoggerEmailSender>.Instance);
        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.SendAsync(null!));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Notifications.Tests --filter LoggerSenderTests`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Implement interfaces + stubs**

`IEmailSender.cs`:
```csharp
namespace Themia.Notifications;

/// <summary>Sends an email notification via a configured provider.</summary>
public interface IEmailSender
{
    /// <summary>Sends <paramref name="message"/>. Throws on provider failure (callers/drainer own retry).</summary>
    Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default);
}
```
`ISmsSender.cs` and `IPushSender.cs`: identical shape with `<summary>` for SMS and push respectively (push is a provider seam — no built-in provider in v1).

`Providers/LoggerEmailSender.cs`:
```csharp
using Microsoft.Extensions.Logging;

namespace Themia.Notifications.Providers;

/// <summary>Development <see cref="IEmailSender"/> that logs instead of sending. Never contacts a server.</summary>
internal sealed class LoggerEmailSender(ILogger<LoggerEmailSender> logger) : IEmailSender
{
    public Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        logger.LogInformation("Themia.Notifications (logger email): to {Recipient} subject {Subject}", message.Recipient, message.Subject);
        return Task.FromResult(NotificationResult.Success());
    }
}
```
`Providers/LoggerSmsSender.cs`: same shape, `ILogger<LoggerSmsSender>`, implements `ISmsSender`, logs `to {Recipient}`.

- [ ] **Step 4: Record public API**

Append the three interfaces (+ their `SendAsync`) to `PublicAPI.Unshipped.txt`. The logger stubs are `internal` — not recorded.
```
Themia.Notifications.IEmailSender
Themia.Notifications.IEmailSender.SendAsync(Themia.Notifications.NotificationMessage! message, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<Themia.Notifications.NotificationResult!>!
Themia.Notifications.ISmsSender
Themia.Notifications.ISmsSender.SendAsync(Themia.Notifications.NotificationMessage! message, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<Themia.Notifications.NotificationResult!>!
Themia.Notifications.IPushSender
Themia.Notifications.IPushSender.SendAsync(Themia.Notifications.NotificationMessage! message, System.Threading.CancellationToken cancellationToken = default(System.Threading.CancellationToken)) -> System.Threading.Tasks.Task<Themia.Notifications.NotificationResult!>!
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Themia.Notifications.Tests --filter LoggerSenderTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Notifications tests/Themia.Notifications.Tests
git commit -m "feat: add sender interfaces and logger stubs"
```

---

### Task 5: SMTP email sender (System.Net.Mail)

**Files:**
- Create: `src/neutral/Themia.Notifications/Providers/SmtpEmailOptions.cs`
- Create: `src/neutral/Themia.Notifications/Providers/SmtpEmailSender.cs`
- Modify: `PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Notifications.Tests/SmtpEmailSenderTests.cs`

`SmtpEmailSender` renders the body (via `INotificationTemplateRenderer` when `Template` is set) and sends through `System.Net.Mail.SmtpClient`. It is unit-tested with **pickup-directory delivery** (`SmtpDeliveryMethod.SpecifiedPickupDirectory`) — the client writes a `.eml` file instead of hitting a server, so the test asserts the file contents with no SMTP server.

- [ ] **Step 1: Write the failing test**

```csharp
using Themia.Notifications;
using Themia.Notifications.Providers;
using Xunit;

namespace Themia.Notifications.Tests;

public sealed class SmtpEmailSenderTests
{
    [Fact]
    public async Task Send_WritesEmlToPickupDirectory_WithRenderedBody()
    {
        var dir = Directory.CreateTempSubdirectory("themia-smtp-").FullName;
        try
        {
            var options = new SmtpEmailOptions
            {
                Host = "localhost",
                FromAddress = "noreply@themia.test",
                PickupDirectory = dir, // test-only delivery: write .eml instead of connecting
            };
            var sut = new SmtpEmailSender(options, new HandlebarsNotificationRenderer(new ThemiaNotificationsOptions()));

            var result = await sut.SendAsync(new NotificationMessage
            {
                Channel = NotificationChannel.Email,
                Recipient = "user@example.com",
                Subject = "Welcome {{name}}",
                Template = "<p>Hello {{name}}</p>",
                Model = new { name = "Sam" },
            });

            Assert.True(result.Succeeded);
            var eml = Directory.EnumerateFiles(dir, "*.eml").Single();
            var text = await File.ReadAllTextAsync(eml);
            Assert.Contains("user@example.com", text);
            Assert.Contains("Hello Sam", text);     // template rendered
            Assert.Contains("Welcome Sam", text);   // subject rendered too
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Send_NullMessage_Throws()
    {
        var sut = new SmtpEmailSender(new SmtpEmailOptions { Host = "localhost", FromAddress = "x@y.z" },
            new HandlebarsNotificationRenderer(new ThemiaNotificationsOptions()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.SendAsync(null!));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Notifications.Tests --filter SmtpEmailSenderTests`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Implement options + sender**

`Providers/SmtpEmailOptions.cs`:
```csharp
namespace Themia.Notifications.Providers;

/// <summary>SMTP provider configuration for <see cref="SmtpEmailSender"/>.</summary>
public sealed class SmtpEmailOptions
{
    /// <summary>SMTP host. Required (ignored when <see cref="PickupDirectory"/> is set).</summary>
    public string Host { get; set; } = string.Empty;
    /// <summary>SMTP port. Default 25.</summary>
    public int Port { get; set; } = 25;
    /// <summary>Use STARTTLS/SSL. Default <see langword="true"/>.</summary>
    public bool UseSsl { get; set; } = true;
    /// <summary>Username for SMTP auth. Null for anonymous.</summary>
    public string? UserName { get; set; }
    /// <summary>Password for SMTP auth.</summary>
    public string? Password { get; set; }
    /// <summary>The From address. Required.</summary>
    public string FromAddress { get; set; } = string.Empty;
    /// <summary>The From display name. Optional.</summary>
    public string? FromDisplayName { get; set; }
    /// <summary>Whether bodies are HTML. Default <see langword="true"/>.</summary>
    public bool IsBodyHtml { get; set; } = true;
    /// <summary>When set, emails are written as <c>.eml</c> files to this directory instead of sent
    /// (System.Net.Mail pickup-directory delivery). For tests / local dev.</summary>
    public string? PickupDirectory { get; set; }
}
```

`Providers/SmtpEmailSender.cs`:
```csharp
using System.Net;
using System.Net.Mail;

namespace Themia.Notifications.Providers;

/// <summary><see cref="IEmailSender"/> over <c>System.Net.Mail.SmtpClient</c>. Renders the body from
/// <see cref="NotificationMessage.Template"/> + <see cref="NotificationMessage.Model"/> when no
/// pre-rendered <see cref="NotificationMessage.Body"/> is supplied.</summary>
internal sealed class SmtpEmailSender(SmtpEmailOptions options, INotificationTemplateRenderer renderer) : IEmailSender
{
    public async Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var body = message.Body ?? (message.Template is not null ? renderer.Render(message.Template, message.Model ?? new { }) : string.Empty);
        var subject = message.Subject is not null && message.Model is not null && message.Body is null
            ? renderer.Render(message.Subject, message.Model)   // subject may also be a template
            : message.Subject ?? string.Empty;

        using var mail = new MailMessage
        {
            From = new MailAddress(options.FromAddress, options.FromDisplayName),
            Subject = subject,
            Body = body,
            IsBodyHtml = options.IsBodyHtml,
        };
        mail.To.Add(message.Recipient);

        using var client = CreateClient();
        await client.SendMailAsync(mail, cancellationToken).ConfigureAwait(false);
        return NotificationResult.Success();
    }

    private SmtpClient CreateClient()
    {
        if (!string.IsNullOrEmpty(options.PickupDirectory))
        {
            return new SmtpClient
            {
                DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory,
                PickupDirectoryLocation = options.PickupDirectory,
            };
        }

        var client = new SmtpClient(options.Host, options.Port) { EnableSsl = options.UseSsl };
        if (!string.IsNullOrEmpty(options.UserName))
            client.Credentials = new NetworkCredential(options.UserName, options.Password);
        return client;
    }
}
```

- [ ] **Step 4: Record public API**

Append `SmtpEmailOptions` (+ its properties) to `PublicAPI.Unshipped.txt`. `SmtpEmailSender` is `internal` — not recorded. (Let a clean build's RS0016 output dictate exact lines.)

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Themia.Notifications.Tests --filter SmtpEmailSenderTests`
Expected: PASS (the `.eml` is written + contains the rendered subject/body).

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Notifications tests/Themia.Notifications.Tests
git commit -m "feat: add SMTP email sender (System.Net.Mail, pickup-dir testable)"
```

---

### Task 6: HTTP SMS sender base

**Files:**
- Create: `src/neutral/Themia.Notifications/Providers/HttpSmsSenderBase.cs`
- Modify: `PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Notifications.Tests/HttpSmsSenderBaseTests.cs`

A template-method base for HTTP SMS providers (e.g. Sms2Pro): subclasses build the request and interpret the response; the base owns the POST + error mapping. Consumers supply the concrete provider; the base is unit-tested via a fake subclass + a stub `HttpMessageHandler`.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Net;
using Themia.Notifications;
using Themia.Notifications.Providers;
using Xunit;

namespace Themia.Notifications.Tests;

public sealed class HttpSmsSenderBaseTests
{
    private sealed class StubHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
    }

    private sealed class FakeSmsSender(HttpClient http) : HttpSmsSenderBase(http)
    {
        protected override HttpRequestMessage BuildRequest(NotificationMessage m)
            => new(HttpMethod.Post, "https://sms.test/send") { Content = new StringContent($"{m.Recipient}:{m.Body}") };
        protected override NotificationResult Interpret(HttpStatusCode status, string responseBody)
            => status == HttpStatusCode.OK ? NotificationResult.Success(responseBody) : NotificationResult.Failure(responseBody);
    }

    [Fact]
    public async Task Send_PostsAndInterpretsSuccess()
    {
        var sut = new FakeSmsSender(new HttpClient(new StubHandler(HttpStatusCode.OK, "msg-123")));
        var r = await sut.SendAsync(new NotificationMessage { Channel = NotificationChannel.Sms, Recipient = "+100", Body = "hi" });
        Assert.True(r.Succeeded);
        Assert.Equal("msg-123", r.ProviderMessageId);
    }

    [Fact]
    public async Task Send_InterpretsFailureFromStatus()
    {
        var sut = new FakeSmsSender(new HttpClient(new StubHandler(HttpStatusCode.BadGateway, "down")));
        var r = await sut.SendAsync(new NotificationMessage { Channel = NotificationChannel.Sms, Recipient = "+100", Body = "hi" });
        Assert.False(r.Succeeded);
        Assert.Equal("down", r.Error);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Notifications.Tests --filter HttpSmsSenderBaseTests`
Expected: FAIL — type doesn't exist.

- [ ] **Step 3: Implement the base**

`Providers/HttpSmsSenderBase.cs`:
```csharp
using System.Net;

namespace Themia.Notifications.Providers;

/// <summary>Base for HTTP-based SMS providers. Subclasses build the provider request and interpret the
/// response; this base owns the POST + read. Reuse one <see cref="HttpClient"/> (e.g. via
/// <c>IHttpClientFactory</c>) per the .NET HttpClient guidance.</summary>
public abstract class HttpSmsSenderBase(HttpClient httpClient) : ISmsSender
{
    /// <summary>Builds the provider-specific HTTP request for <paramref name="message"/>.</summary>
    protected abstract HttpRequestMessage BuildRequest(NotificationMessage message);

    /// <summary>Maps the provider response to a <see cref="NotificationResult"/>.</summary>
    protected abstract NotificationResult Interpret(HttpStatusCode status, string responseBody);

    /// <inheritdoc />
    public async Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var request = BuildRequest(message);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Interpret(response.StatusCode, body);
    }
}
```

- [ ] **Step 4: Record public API**

Append `HttpSmsSenderBase` (the public abstract type, its constructor, the two protected abstract methods, and the public `SendAsync`) to `PublicAPI.Unshipped.txt` per the analyzer's output.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Themia.Notifications.Tests --filter HttpSmsSenderBaseTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Notifications tests/Themia.Notifications.Tests
git commit -m "feat: add HTTP SMS sender base (template method)"
```

---

### Task 7: DI extension

**Files:**
- Create: `src/neutral/Themia.Notifications/ThemiaNotificationsServiceCollectionExtensions.cs`
- Modify: `PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Notifications.Tests/ThemiaNotificationsServiceCollectionExtensionsTests.cs`

`AddThemiaNotifications` registers the renderer + the options, and the **logger stubs as default senders** (`TryAdd`, so a host that registers a real `IEmailSender`/`ISmsSender` first wins). It does not force SMTP/HTTP-SMS (those are wired explicitly by the host with their options). Idempotent.

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Themia.Notifications;
using Xunit;

namespace Themia.Notifications.Tests;

public sealed class ThemiaNotificationsServiceCollectionExtensionsTests
{
    [Fact]
    public void Registers_RendererAndDefaultSenders()
    {
        var sp = new ServiceCollection().AddLogging().AddThemiaNotifications().BuildServiceProvider();
        Assert.NotNull(sp.GetService<INotificationTemplateRenderer>());
        Assert.NotNull(sp.GetService<IEmailSender>());
        Assert.NotNull(sp.GetService<ISmsSender>());
    }

    [Fact]
    public void Configure_IsApplied_AndIdempotent()
    {
        var services = new ServiceCollection().AddLogging();
        var called = 0;
        services.AddThemiaNotifications(o => { called++; o.ConfigureHandlebars = _ => { }; });
        services.AddThemiaNotifications(); // second call must not duplicate registrations
        var sp = services.BuildServiceProvider();

        Assert.Equal(1, sp.GetServices<INotificationTemplateRenderer>().Count());
        Assert.Equal(1, called);
    }

    [Fact]
    public void HostSupplied_EmailSender_Wins()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IEmailSender, FakeEmail>();   // host registers first
        services.AddThemiaNotifications();                  // TryAdd must not override
        var sp = services.BuildServiceProvider();
        Assert.IsType<FakeEmail>(sp.GetRequiredService<IEmailSender>());
    }

    private sealed class FakeEmail : IEmailSender
    {
        public Task<NotificationResult> SendAsync(NotificationMessage m, CancellationToken ct = default) => Task.FromResult(NotificationResult.Success());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Themia.Notifications.Tests --filter ThemiaNotificationsServiceCollectionExtensionsTests`
Expected: FAIL — `AddThemiaNotifications` doesn't exist.

- [ ] **Step 3: Implement**

`ThemiaNotificationsServiceCollectionExtensions.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Notifications;
using Themia.Notifications.Providers;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>DI registration for the Themia notification core.</summary>
public static class ThemiaNotificationsServiceCollectionExtensions
{
    /// <summary>Registers the notification renderer + options and the logger-stub senders as defaults
    /// (<c>TryAdd</c> — a host-registered real sender wins). Idempotent.</summary>
    public static IServiceCollection AddThemiaNotifications(
        this IServiceCollection services,
        Action<ThemiaNotificationsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ThemiaNotificationsOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<INotificationTemplateRenderer, HandlebarsNotificationRenderer>();
        services.TryAddSingleton<IEmailSender, LoggerEmailSender>();
        services.TryAddSingleton<ISmsSender, LoggerSmsSender>();
        return services;
    }
}
```

- [ ] **Step 4: Record public API**

Append to `PublicAPI.Unshipped.txt`:
```
Microsoft.Extensions.DependencyInjection.ThemiaNotificationsServiceCollectionExtensions
static Microsoft.Extensions.DependencyInjection.ThemiaNotificationsServiceCollectionExtensions.AddThemiaNotifications(this Microsoft.Extensions.DependencyInjection.IServiceCollection! services, System.Action<Themia.Notifications.ThemiaNotificationsOptions!>? configure = null) -> Microsoft.Extensions.DependencyInjection.IServiceCollection!
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Themia.Notifications.Tests --filter ThemiaNotificationsServiceCollectionExtensionsTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/neutral/Themia.Notifications tests/Themia.Notifications.Tests
git commit -m "feat: add AddThemiaNotifications DI extension"
```

---

### Task 8: Version, changelog, full build/test

**Files:**
- Modify: `Directory.Build.props` (line ~26)
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<Version>0.6.1</Version>` to `<Version>0.6.2</Version>`.

- [ ] **Step 2: Add the changelog entry**

In `CHANGELOG.md`, add under `## [Unreleased]`:
```markdown
## [0.6.2] - 2026-06-22

### Added
- `Themia.Notifications` — neutral notification sending core. `NotificationMessage` model, channel
  senders (`IEmailSender` / `ISmsSender` / `IPushSender` seam), an `INotificationTemplateRenderer`
  (Handlebars.Net, used directly — no PuppeteerSharp/Chromium coupling), an SMTP email provider
  (`SmtpEmailSender` + `SmtpEmailOptions`, `System.Net.Mail`), an HTTP-SMS provider base
  (`HttpSmsSenderBase`), logger dev stubs, and an `AddThemiaNotifications` DI extension. Targets
  `net8.0;net10.0`. First slice of the Notifications module (the tenant-aware outbox/dispatcher follows
  in `Themia.Modules.Notifications`).
```

- [ ] **Step 3: Full clean build + test**

Run:
```bash
cd /Users/sarawut/GitHub/Idevs/single-repo/Packages/themia
dotnet build Themia.sln --no-incremental
dotnet test tests/Themia.Notifications.Tests
```
Expected: solution builds with 0 warnings (TreatWarningsAsErrors); all `Themia.Notifications.Tests` pass on both TFMs.

- [ ] **Step 4: Commit**

```bash
git add Directory.Build.props CHANGELOG.md
git commit -m "chore: release 0.6.2 — Themia.Notifications neutral sending core"
```

---

## Post-implementation (controller, not a task step)

- Open the PR for the core; run the standard review passes (`/code-review`, `/pr-review-toolkit:review-pr`, `/agy-review`); address findings.
- File the coord request (ezy → `Themia.Notifications`: a production email sender) and advance it on release.
- Then start the **module plan** (`Themia.Modules.Notifications`: outbox + near-real-time drainer with the per-engine claim, dispatcher, preferences, in-app, per-tenant provider config) as its own writing-plans cycle — it ships at **0.6.3**.
