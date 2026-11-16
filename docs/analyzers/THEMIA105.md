# THEMIA105 — Exception text passed to a logging call

**Category:** Themia.Privacy · **Severity:** Warning

A library exception message can carry personal data: a serializer, store or HTTP client often echoes the value it rejected.
Putting that text in a log line (a template argument, an interpolated or concatenated message) ships it to every sink and
keeps it as long as the logs live.

**Flagged**, inside the arguments of a logging call (`ILogger` extension methods, `ILogger.Log` / `BeginScope`, a `[LoggerMessage]`
method, a `LoggerMessage.Define` delegate):

- `ex.Message`, including `ex.InnerException.Message`, `ex.GetBaseException().Message` and an overridden `Message`
- `ex.ToString()`
- the exception object itself used as a value in a template argument, an interpolation hole or a concatenation:
  `LogError("failed {E}", ex)`, `$"failed {ex}"`, `"failed " + ex` (these format it through `ToString()`)

**Not flagged:** the exception argument (`LogError(ex, "...")`), `ex.GetType().Name`, `ex.HResult`, `ex.StackTrace`, a `Message` of a
non-exception type, and anything outside a logging call.

**Do this instead.** When the catch wraps a library, store or serializer call whose message can carry personal data, log a fixed
message, the ids and `ex.GetType().Name`. Pass `ex` as the exception argument only where the stack trace is needed.

> Passing `ex` as the exception argument does **not** keep personal data out of the log. A console or file sink renders
> `ex.ToString()`, message included, and `Themia.Exceptional` masks only credential-shaped values. THEMIA105 closes the path through
> the message template and its properties; it does not close the path through the exception field.

**Known gaps** (no dataflow): a message copied to a local or field first, a message passed through a helper (`Describe(ex)`),
`ex.Data`, and logger APIs other than Microsoft.Extensions.Logging.

**Deliberate use:** suppress with a justification: `#pragma warning disable THEMIA105` or
`[SuppressMessage("Themia.Privacy", "THEMIA105", Justification = "...")]`. Soften with
`dotnet_diagnostic.THEMIA105.severity = suggestion` in `.editorconfig`.
