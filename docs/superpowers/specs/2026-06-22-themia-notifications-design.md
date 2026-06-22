# Themia Notifications (multi-channel dispatcher) — Design

**Status:** Approved (brainstorming) — hardened after a /scrutinize pass — ready for implementation plan
**Date:** 2026-06-22
**Scrutiny revisions (2026-06-22):** templating no longer depends on `Themia.Pdf` (avoids dragging
PuppeteerSharp/Chromium into notification consumers); drain signal fires post-commit and poll-interval
is the documented latency floor in multi-instance; v1 SMS is fire-and-forget only (provider-managed OTP
stays in Identity); per-engine outbox claim strategy specified; in-app writes directly (not via outbox).
**Origin:** ezy-assets notification surface (port + generalize). Drivers: ezy has **no production email
sender** (only `LoggerEmailService`), a real SMS provider (`Sms2ProOTPService`), and logging-only
workflow/notification dispatch (`IWorkflowNotificationSink` / `INotificationDispatcher`).
**Target version:** 0.6.2 (PATCH within the 0.6 Phase-2 milestone — the Notifications module of the
Notifications/Pdf/Export trio, continuing the 0.6.0 Pdf / 0.6.1 Exceptional cadence).

---

## Goal

A neutral, multi-channel **notification dispatcher** for Themia: send **email / SMS / in-app**
(push as a seam) through pluggable providers, with bodies templated by the shipped Themia.Pdf
Handlebars renderer, **all sends recorded in a transactional outbox** and delivered by a
**near-real-time background drainer** with retry/backoff. Generalizes ezy's bespoke
`IEmailService` / `IOTPService` (the SMS-send half) / `IWorkflowNotificationSink` /
`INotificationDispatcher` into framework infra.

## Scope

**One combined spec** (the maintainer's explicit choice), but **internally layered** into two
packages so it can be built and reviewed in phases:

- **`Themia.Notifications`** — neutral, stateless sending core (contracts, senders, providers, templating).
- **`Themia.Modules.Notifications`** — tenant/stateful machinery (outbox, drainer, dispatcher,
  preferences, in-app store, per-tenant provider config).

### In scope

- Channel senders: `IEmailSender`, `ISmsSender`, **`IPushSender` (seam only, no provider in v1)**.
- Built-in providers: **SMTP** email + **logger/noop** dev stubs; an **HTTP-SMS provider base**.
- `NotificationMessage` model + templating via Themia.Pdf `IHtmlTemplateRenderer` (Handlebars).
- **Transactional outbox** (FluentMigrator schema across SQL Server / MySQL / PostgreSQL).
- **Near-real-time drainer** (signaled hosted `BackgroundService` + periodic poll; lease-based claim;
  exponential backoff; `scheduled_for` future sends).
- `INotificationDispatcher` routing events → channels via **per-tenant/user preferences**.
- **In-app** channel = persisted, queryable notification records.
- **Per-tenant provider config** (SMTP/SMS creds per tenant, global fallback).
- Tenant isolation on outbox / in-app / preferences via the framework filter (EF + Dapper peers).

### Out of scope (deferred seams / YAGNI)

- **Push provider** (FCM/APNs device-token management) — `IPushSender` defined, no concrete provider.
- **SaaS provider integrations** (SendGrid / Sms2Pro / FCM) — thin consumer-supplied or follow-on
  packages, not built here.
- **OTP — generation, verification, AND provider-managed OTP send — stay in Identity/ezy, fully out of
  scope.** v1 SMS is **fire-and-forget plain messages only** (enqueue → drain → send, no synchronous
  return to the caller). ezy's real SMS path (`Sms2ProOTPService`) is *provider-managed*:
  `SendProviderManagedOTPAsync` returns a `ReferenceCode`/`Token` **synchronously** and the same provider
  verifies it — so it can neither be split "Identity generates / Notifications sends" nor routed through
  the async outbox (the caller needs the provider's reference in its response). Notifications does not
  attempt to model OTP; it sends ordinary SMS a caller hands it.
- Domain events, recipient resolution, business "when to send" — stay in **ezy** (app domain).
- Bulk/campaign features, analytics, click tracking.

### Scope-guard check

Sending (email/SMS/push), provider abstraction, templating, the outbox + drainer, and tenant-scoped
preferences/config are cross-cutting infra (any multi-tenant app needs them) ✅. The specific business
events + recipient resolution + OTP logic are app/identity domain and stay out.

---

## Architecture

```
src/neutral/Themia.Notifications/            net8.0;net10.0   (stateless core)
  Abstractions/ IEmailSender, ISmsSender, IPushSender, INotificationTemplateRenderer
  NotificationMessage.cs, NotificationResult.cs, NotificationChannel.cs
  Providers/ SmtpEmailSender, LoggerEmailSender(noop), HttpSmsSenderBase, LoggerSmsSender
  Templating/ INotificationTemplateRenderer, HandlebarsNotificationRenderer  (Handlebars.Net directly)
  DependencyInjection/ ThemiaNotificationsServiceCollectionExtensions  (AddThemiaNotifications)
  PublicAPI.*.txt

src/modules/Themia.Modules.Notifications/    net10.0          (tenant/stateful)
  NotificationsModule.cs : ThemiaModuleBase
  Entities/ OutboxMessage, InAppNotification, NotificationPreference, TenantProviderConfig
  Migrations/ NotificationsSchemaMigration.cs  (IfDatabase × 3)
  Outbox/ IOutboxStore, OutboxDrainer (BackgroundService), DrainSignal
  Dispatch/ INotificationDispatcher, NotificationDispatcher, PreferenceResolver
  Stores/ Ef*Store / Dapper*Store (peers), Mapping/, EntityConfiguration/
  DependencyInjection/ AddThemiaNotificationsModule(...)
  PublicAPI.*.txt
```

**Dependencies:** core depends on **`Handlebars.Net`** (directly — already pinned in the repo) +
DI/Logging abstractions. It deliberately does **not** depend on `Themia.Pdf`: `IHtmlTemplateRenderer`
lives in the `Themia.Pdf` package alongside `IPdfRenderer`, which references **PuppeteerSharp**, so a
dependency would transitively pull a ~150 MB Chromium-downloading library into every email/SMS
consumer. Both packages use the same engine (Handlebars.Net) via their own thin renderers; if the
duplication ever matters, extract a shared `Themia.Templating` package both reference (future, YAGNI).
Module depends on the core + `Themia.Framework.Core` (entities/tenancy),
`Themia.Framework.Data.EFCore`/`.Dapper`, `Themia.Data.Migrations`, and the ASP.NET hosting
abstractions (for the `BackgroundService` drainer).

---

## Components

### Core — senders, message, templating

```csharp
public enum NotificationChannel { Email, Sms, InApp, Push }

public sealed class NotificationMessage
{
    public NotificationChannel Channel { get; init; }
    public string Recipient { get; init; }          // email addr / phone / user id
    public string? Subject { get; init; }
    public string? Body { get; init; }              // pre-rendered, OR use Template+Model
    public string? Template { get; init; }          // Handlebars source (rendered if Body null)
    public object? Model { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public interface IEmailSender { Task<NotificationResult> SendAsync(NotificationMessage m, CancellationToken ct = default); }
public interface ISmsSender   { Task<NotificationResult> SendAsync(NotificationMessage m, CancellationToken ct = default); }
public interface IPushSender  { Task<NotificationResult> SendAsync(NotificationMessage m, CancellationToken ct = default); } // seam
```

- Senders are **stateless, provider-backed**. On provider failure they **throw** — the drainer owns
  retry; senders don't retry themselves (THEMIA101: no log-and-rethrow).
- Bodies render through `HandlebarsNotificationRenderer`, which uses **`Handlebars.Net` directly** — the
  same engine the Pdf core uses, but **not** via the `Themia.Pdf` package (which would drag
  PuppeteerSharp/Chromium in; see Dependencies). `Body` set ⇒ used verbatim; else `Template` + `Model`
  merged.
- Built-in providers: `SmtpEmailSender` (System.Net.Mail/MailKit — chosen in the plan), `LoggerEmailSender`,
  `HttpSmsSenderBase` (a base for HTTP SMS providers like Sms2Pro), `LoggerSmsSender`.

### Module — transactional outbox

`OutboxMessage` (tenant-scoped, `SoftDeletableEntity`-style audit): `Id, TenantId, Channel, Recipient,
Subject, Body, Status (pending|sending|sent|failed|dead), Attempts, NextAttemptAt, ScheduledFor?,
LeaseOwner?, LeaseExpiresAt?, CreatedAt, SentAt?, LastError`. Schema via FluentMigrator with
`IfDatabase("postgresql"/"mysql"/"sqlserver")` + the unsupported-provider guard; index on
`(status, next_attempt_at)` for the drain/claim query and `(tenant_id, ...)` for isolation.

**Enqueue is transactional:** `IOutboxStore.EnqueueAsync` writes the row in the **caller's
`IUnitOfWork` transaction**, so a notification commits atomically with the work that triggered it
(no "sent but the transaction rolled back"). *Verified feasible on both peers:* the Dapper UoW flushes
staged writes on the ambient `IDapperConnectionContext.CurrentTransaction` (`DapperUnitOfWork.SaveChangesAsync`
reuses an in-flight transaction), and the mediator `TransactionBehavior` supplies that ambient
transaction; EF uses the same `DbContext`/`SaveChanges`. The outbox store must resolve from the **same
request scope** as the caller so it shares that connection/context.

### Module — near-real-time drainer

`OutboxDrainer : BackgroundService`:
- **Signaled drain (single-instance optimization):** `DrainSignal` (an in-process channel/event) is
  kicked **after the enqueuing transaction commits** (not at enqueue time — pre-commit the row isn't
  visible to the drainer's connection and would be missed). On signal the drainer wakes and drains.
- **Latency floor:** the signal is **in-process**, so it only lowers latency on the *same* instance. In
  a **multi-instance** deployment a request handled by instance A doesn't wake the drainer on instance
  B, so **delivery latency is bounded by the poll interval** there. This is the documented contract:
  near-real-time, not synchronous — sub-poll latency is best-effort (single-instance), not guaranteed.
  (Latency-critical flows that truly need "now" — e.g. provider-managed OTP — are out of scope; see §7.)
- **Periodic poll:** every *N* seconds (config, default ~5s) — the real backstop for retries,
  `ScheduledFor` future-dated rows, and cross-instance delivery.
- **Claim with a lease — per engine (the correctness core):** rows are claimed atomically so concurrent
  drainers across instances never double-send, using the engine's skip-locked idiom via the dialect
  (`IfDatabase`):
  - **PostgreSQL / MySQL 8+ / MariaDB:** `SELECT id ... WHERE status='pending' AND next_attempt_at<=now
    ORDER BY next_attempt_at LIMIT @batch FOR UPDATE SKIP LOCKED`, then `UPDATE ... SET status='sending',
    lease_owner=@owner, lease_expires_at=@exp` in the same transaction.
  - **SQL Server:** `UPDATE TOP(@batch) Notifications.Outbox WITH (READPAST, UPDLOCK, ROWLOCK)
    SET status='sending', lease_owner=@owner, lease_expires_at=@exp OUTPUT inserted.* WHERE status='pending'
    AND next_attempt_at<=@now` (single statement; `READPAST` skips locked rows).
  - A **stale lease** (`lease_expires_at < now` while still `sending` — a crashed drainer) is reclaimable
    on the next poll. Columns `lease_owner`, `lease_expires_at` added to `OutboxMessage`.
- **Backoff:** failed send → `Attempts++`, `NextAttemptAt = now + backoff(Attempts)` (exponential,
  capped), `LastError` set; past a max-attempts cap → `dead`. Success → `sent` + `SentAt`.
- Dispatches by `Channel` to the matching `I*Sender`, resolving the **per-tenant provider config**.
  (In-app is **not** drained — see below.)

### Module — dispatcher, preferences, in-app, per-tenant config

- `INotificationDispatcher.DispatchAsync(notification, ct)` → `PreferenceResolver` resolves the
  recipient set + enabled channels (+ locale) from **`NotificationPreference`** (per-tenant, optional
  per-user) → for each **external** channel (email/SMS/push) **enqueues one `OutboxMessage`**.
- **In-app** channel: written **directly** as an **`InAppNotification`** record (tenant/user, title,
  body, read flag, created) within the dispatch transaction — **not** routed through the outbox/drainer.
  In-app has no external provider to retry and is durable the moment it's written, so the outbox hop
  (and a second copy of the body) would be pure overhead. The outbox + drainer exist only for channels
  with a flaky external provider.
- **`TenantProviderConfig`**: per-tenant SMTP/SMS credentials + from-address/sender-id, resolved at
  send time with a global-config fallback.
- All four entities are tenant-isolated through the framework filter (EF query filter / Dapper
  `TenantPredicate`), runnable on either data peer.

---

## Error handling & logging

- Provider/send failure inside the drainer → recorded on the outbox row (retry/backoff/`dead`), **not**
  a thrown request error. The triggering request only fails if the *enqueue* fails.
- `OperationCanceledException` propagates as cancellation; the drainer honors the host stop token.
- THEMIA101: no log-and-rethrow; the drainer logs send failures once at `Warning`/`Error` with context
  (tenant, channel, attempt) — **never** credentials or full recipient PII.
- `ILogger<T>` only; `System.Text.Json` only.

---

## Public API surface (PublicAPI analyzer)

Core: `NotificationChannel`, `NotificationMessage`, `NotificationResult`, `IEmailSender`, `ISmsSender`,
`IPushSender`, `INotificationTemplateRenderer`, `SmtpEmailSender`+options, `AddThemiaNotifications`.
Module: `INotificationDispatcher`, the four entities, `NotificationsModule`, options, and the
`AddThemiaNotifications…` module extension. Concrete drainer/stores/resolvers are `internal`.
All XML-documented; `PublicAPI.Unshipped.txt` curated; clean under `TreatWarningsAsErrors`.

---

## Testing strategy

**Unit (no DB/network):** Handlebars body rendering (Body-verbatim vs Template+Model); channel routing
+ preference resolution (enabled/disabled channels, per-user override, locale); backoff math + max-cap
→ `dead`; lease-claim logic; provider failure surfaces as a throw senders don't swallow.

**Integration (Testcontainers, gated):** outbox **enqueue → drain → sent** round-trip across SQL Server
/ MySQL / PostgreSQL; transactional enqueue rolls back with the caller's UoW; **concurrency** — two
drainers don't double-send a row (lease); `ScheduledFor` future row isn't sent early; SMTP send via a
local test SMTP server; in-app channel persists a record.

CI note: needs the Testcontainers engines + a test SMTP server; gated like the other heavy suites.

---

## Versioning, changelog, coord

- Bump `Directory.Build.props` `<Version>` `0.6.1 → 0.6.2` (Notifications module, PATCH within the 0.6
  Phase-2 milestone).
- CHANGELOG **Added** — `Themia.Notifications` (neutral senders + templating) and
  `Themia.Modules.Notifications` (transactional outbox + near-real-time drainer + dispatcher +
  preferences + in-app + per-tenant provider config).
- Coord: file an ezy → Themia.Notifications request (drivers: production email sender + generalized
  dispatch), advanced through the cycle on release.

---

## Future improvements (not v1)

- Push provider (FCM/APNs) behind the `IPushSender` seam; SaaS email/SMS provider packages
  (SendGrid / Sms2Pro / Twilio) as thin add-ons.
- DB-backed / per-tenant **template store** (mirrors the cancelled Pdf template-store question — only if
  a driver appears; templates are caller-supplied Handlebars until then).
- Bulk/campaign sends, digest batching, click/open tracking, a notifications dashboard.
- Quartz integration for purely-scheduled campaigns (the hot-path drainer stays a hosted service).
