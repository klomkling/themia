# Exception alerts (Telegram)

`Themia.Exceptional` can tell a chat when a new kind of error shows up. It is off by default.

```csharp
services.AddThemiaExceptionalAlerts(o => configuration.GetSection("Exceptional:Alerts").Bind(o));
```

Call it after `AddThemiaExceptionalCore` and a provider call (`AddThemiaExceptionalPostgres`, `...MySql`, `...SqlServer`).
With `Enabled` false nothing is registered. With `Enabled` true the options are validated at startup and the host
refuses to start on a missing token, chat id or admin link, or on a `Lease` shorter than the longest possible send.

## Configuration (suggested section `Exceptional:Alerts`)

| Key | Default | Notes |
|---|---|---|
| `Enabled` | `false` | Master switch |
| `BotToken` | none | Secret. Keep it in a secret store or env var, never in source |
| `ChatId` | none | Chat or channel id |
| `TopicId` | none | Optional forum topic (`message_thread_id`) |
| `EnvironmentName` | host environment | Shown in the message |
| `AdminUrlTemplate` | none | Must contain `{guid}` |
| `Interval` | 1 min | Job period (+-10% jitter) |
| `QuietPeriod` | 24 h | A hash quiet this long that returns alerts as "reappeared" |
| `Lookback` | 1 h | Only rows seen within this window are considered. Errors older than this when the job (re)starts are not alerted |
| `CountThreshold` | 50 | `DuplicateCount` that triggers an alert within one rollup |
| `MaxMessagesPerHour` | 12 | Best effort across instances |
| `MaxGroupsPerMessage` | 10 | Rest are left for the next run ("+N more") |
| `Lease`, `MaxAttempts` | 5 min, 5 | A claimed batch not marked sent within `Lease` is retried by any instance, up to `MaxAttempts`, then failed with one ERROR log |
| `SendTimeout`, `MaxSendRetries`, `MaxRetryAfter`, `RetryBaseDelay` | 10 s, 3, 60 s, 1 s | Retries only for 5xx, timeouts and 429 |

Environment-variable form (standard .NET binding): `Exceptional__Alerts__BotToken`, `Exceptional__Alerts__ChatId`, and so on.

## Behaviour to know

- **Permanent rejections drop the groups.** A 4xx other than 429 (for example a wrong chat id) marks the batch failed and logs one error; those groups are not retried.
- **The hourly cap is best-effort across instances.** The count is read before claiming, so N instances can exceed it by up to N-1 messages in one tick.
- **The bot token is part of the request URL.** The named client removes the default HTTP loggers and uses a `SocketsHttpHandler` with `ActivityHeadersPropagator = null`, which keeps the request off HttpClient tracing (verified on net8 and net10: no `System.Net.Http` activity and no `HttpHandlerDiagnosticListener` event, so OpenTelemetry's HttpClient instrumentation records no `url.full`). A primary handler set through `ConfigureHttpClientDefaults` does not apply to this client. Replacing the client's primary handler (for example `ConfigurePrimaryHttpMessageHandler` on `Themia.Exceptional.Telegram`) undoes this; keep that handler's `ActivityHeadersPropagator` null or exclude `api.telegram.org` from tracing yourself. The `System.Net.Http` EventSource (`RequestStart`, visible to an attached EventPipe or `EventListener` session) still reports host and path on both TFMs and cannot be turned off in process, so do not enable that EventSource in production. Outbound proxy or APM URL logging is outside the process and still needs the same exclusion.
- **MariaDB is not supported** (untested; see the repository's multi-database note).
- **Delivery is at-least-once.** If an instance crashes between the Telegram call and recording it, one digest can repeat.
- **`Threshold` needs rollups.** `ExceptionalOptions.RollupPeriod = 0` makes `DuplicateCount` always 1, so that trigger never fires.
  A threshold alert for one hash is sent at most once per `QuietPeriod`.
- **Retention.** Host-side purge should keep rows longer than `QuietPeriod`; otherwise an old hash is labelled "new" instead of "reappeared".
- **Shared database.** Candidates are limited to this app's `ApplicationName` and to rows that are not soft-deleted.
- **Privacy.** The digest carries only application, environment, exception type, count, first/last seen and the admin link. It never
  includes the exception message, stack trace, request body or context, query string, IP address or tenant: open the link to read
  the detail inside your own network. The type name is still masked (e-mail-like text, long digit runs, token-like runs) and flattened.
- **Custom dialects.** A custom `IExceptionalSqlDialect` must also implement `IExceptionAlertSqlDialect`; otherwise startup fails with a clear message.
- **Migration.** `AddExceptionAlertState` is additive and forward-only; it creates `ExceptionAlertState` and `IX_Exceptions_App_LastLog`.
