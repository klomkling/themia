# Themia.AI

A provider-agnostic AI completion client (`IAiCompletionClient`) with retry, failover and a total
wall-clock budget, plus one typed operation built on top of it: `ITextTranslationService`. A
reversible text masker (`IAiTextMasker`) is available as defence in depth for whatever gets sent.

No database, no tenant state, no `IThemiaModule` — there is no `Themia.Modules.AI`. Targets `net8.0`
and `net10.0`, and takes no dependency on `Themia.Framework.*`, no database package, and no ASP.NET.

A provider package does the actual HTTP call: `Themia.AI.Gemini` (`AddThemiaAiGemini`) for Google
Gemini, `Themia.AI.OpenAiCompatible` (`AddThemiaAiOpenAiCompatible`) for any endpoint that speaks the
OpenAI chat-completions shape — OpenAI itself, Ollama, Groq, Cerebras, LM Studio, vLLM. One provider
package reaches every free-tier and self-hosted option by changing a base URL.

## The first real implementation, not a port

The ezy-assets services this replaces — `GeminiAICaptionService` and
`FallbackTextTranslationService` — are both placeholders: the caption service concatenates request
fields with emoji into a `StringBuilder` ("Sprint 2: this is still a placeholder that simulates AI by
formatting the context"), and the translation service returns `text.Trim()` unchanged. Neither repo
makes a real call to any AI provider. So this package is not an upgrade over a working baseline —
whatever ships here is the first real implementation either consumer has had.

## Two write surfaces

- **`IAiCompletionClient.CompleteAsync(AiOperation, AiPrompt, CancellationToken)`** — the general
  surface. One registration; the implementation (`FailoverCompletionClient`) resolves the configured
  model per provider per `AiOperation`, retries transient failures, fails over to the next configured
  provider, and bounds the whole call to `AiOptions.TotalBudget`.
- **`ITextTranslationService.TranslateAsync(text, sourceLanguage, targetLanguage, CancellationToken)`**
  — the one typed operation built on top of the first. Text in, text out, no domain vocabulary.

## Why translation is typed and caption is not

Translation needs nothing about what the text *is* — source language, target language, text. Caption
generation needs to know what a property is: listing type, price, amenities, nearby points of
interest. That vocabulary is application domain, and `Themia.AI` staying framework-neutral means it
must not know it — see `CLAUDE.md`'s framework/app boundary. An app builds a caption prompt itself as
an `AiPrompt` and calls `IAiCompletionClient.CompleteAsync(AiOperation.Completion, prompt, ct)`
directly; `Themia.AI` gives it the dispatch, retry, failover and budget, not the prompt.

## `AiOutcome` — what a caller does with each one

| `AiOutcome` | meaning | what to do |
| --- | --- | --- |
| `Completed` | success | use `Text` |
| `Truncated` | hit the output token limit; `Text` is real but cut mid-sentence | **treating this as success ships a caption ending mid-word.** Raise the limit and retry, or discard |
| `Filtered` | the provider's safety filter refused | do not retry, do not fail over — every provider will refuse similar content. Fall back to non-AI text |
| `ProviderLimit` | quota or rate limit | do not retry this provider; the dispatcher fails over automatically |
| `ProviderError` | transport, timeout or 5xx | the dispatcher retries with backoff, then fails over, automatically |

`Usage` is populated whenever the provider reports it, **on every outcome, not only `Completed`** — a
`Filtered` call still consumed and was billed for its input tokens. `Usage == null` means "the
provider did not report usage", never "this call was free".

## `TranslationOutcome` — which ones are storable

| `AiOutcome` | `TranslationOutcome` | `Text` | storable? |
| --- | --- | --- | --- |
| `Completed` | `Translated` | the translation | **yes** |
| — (same language, no provider call) | `SameLanguage` | the input, unchanged | **yes** — correct to store as-is |
| `Truncated` | `Incomplete` | present, cut mid-sentence | **no** |
| `Filtered` | `Unavailable` | `null` | no |
| `ProviderLimit` (after failover is exhausted) | `Unavailable` | `null` | no |
| `ProviderError` (after retries and failover) | `Unavailable` | `null` | no |

Two rows are easy to get wrong by taking the obvious shortcut, so both are deliberate:

- **`Truncated` → `Incomplete` gets its own member rather than folding into `Translated`.** Its `Text`
  is non-empty, so the reflexive mapping is "this looks done" — and a caller that only checks
  `Text != null` stores a sentence that stops mid-word under a label that says it is finished.
- **`Unavailable` carries `Text = null`, never the source text.** Returning the input on failure is
  the *helpful* thing to do, and it is exactly what `FallbackTextTranslationService` did — which is
  how a caller ended up storing Thai into a row labelled `en-US`, current and wrong, with nothing
  reporting it. A caller that writes `row.Title = result.Text` without reading `Outcome` first must
  fail immediately on `Unavailable`, not silently persist the wrong language. `Incomplete` cannot use
  the same protection, because its text *is* the point — so the two failure shapes are deliberately
  different: one breaks a careless caller immediately, the other requires the caller to actually read
  `Outcome`.

Empty or whitespace-only `text` short-circuits to `SameLanguage` with the input returned, without
calling a provider — a call that can only return nothing still costs a call. Language codes are
passed to the provider exactly as given; `Themia.AI` keeps no list of supported languages, because
such a list goes stale and support differs per provider and per model. An unrecognised code is the
provider's to reject, and its rejection surfaces as `Unavailable` — nothing is defaulted to a
fallback language.

## Masking is defence in depth, not the primary control

```csharp
var masked = masker.Mask("บ้านของคุณสมชาย ติดต่อ 081-234-5678", ["สมชาย", "081-234-5678"]);
var completion = await client.CompleteAsync(AiOperation.Translation, new AiPrompt { User = masked.Text }, ct);
var restored = masked.Restore(completion.Text!);
```

`IAiTextMasker.Mask` replaces caller-supplied values with numbered, XLIFF-style tokens
(`<x id="1"/>`) before text reaches a model, and `MaskedText.Restore` substitutes the originals back
afterwards. **The caller supplies the values to mask — `Themia.AI` does not detect them.** Thai has no
capitalisation and Thai person-name detection is unreliable, so a detector would miss names silently;
an app that knows a field is `contact.Name` can mask it exactly, which a regex over Thai prose cannot.

Masking is not the decision that makes content safe to send. **The app decides which fields are sent
to a provider at all** — that is the primary control. Masking only protects the specific values it is
given inside text the app already decided to send, and it has real limits even there: the rest of the
document can still identify someone (masking a name out of "the client is a doctor at Siriraj who
wants to be near work, budget 8M" protects nobody), and pronouns degrade once a name is gone (Thai
names carry gender the model uses to pick "he"/"she"; a masked name makes it guess).

`Restore` throws rather than returning partial text — a model that drops or duplicates a token is
treated as a hard failure so the caller falls back to untranslated text (visibly not-translated)
rather than showing a user a leftover `<x id="3"/>` (visibly broken).

## Configuration and failover

```csharp
services.AddThemiaAiGemini(o =>
{
    o.ApiKey           = configuration["Gemini:ApiKey"]!;
    o.CompletionModel  = "gemini-2.5-flash-lite";
    o.TranslationModel = "gemini-2.5-flash-lite";
    o.Timeout          = TimeSpan.FromSeconds(30);
});

services.AddThemiaAiOpenAiCompatible(o =>
{
    o.BaseUrl          = new Uri("http://homelab:11434/v1");
    o.CompletionModel  = "qwen2.5:7b";
    o.TranslationModel = "qwen2.5:7b";
    o.Timeout          = TimeSpan.FromSeconds(30);
});

services.AddThemiaAi(o =>
{
    // Order is preference. Every entry must be registered and must name a model for every operation.
    o.Failover = [AiProviderKeys.Gemini, AiProviderKeys.OpenAiCompatible];
});
```

A model name belongs to a **provider**, not to an operation — failing over from Gemini to an
OpenAI-compatible endpoint must never send Gemini's model name to it. `AiOptions.TotalBudget`
(default 45 seconds) bounds every retry and every failover for one `CompleteAsync` call; a per-provider
timeout alone does not, because retries multiply it and failover repeats it. `ValidateOnStart`
validates the whole provider × operation matrix at startup, not each setting alone — a `Failover`
entry with no `TranslationModel` is a configuration that works for months and fails the first time
that provider is asked to translate something.

### `AllowNoProvider`

`AddThemiaAi` with an empty `Failover` **fails at startup** unless `AllowNoProvider` is set:

```csharp
services.AddThemiaAi(o => { o.Failover = []; o.AllowNoProvider = true; });
```

Both behaviours are wanted by someone, which is why the choice has to be explicit. A developer with
no API key wants every call to answer `ProviderError` / `TranslationOutcome.Unavailable` and the app
to fall back gracefully. A production host that registered `AddThemiaAi` and forgot to add a provider
package wants to be told at startup — without this flag it would silently get the developer's
behaviour: a service that validates its configuration, answers every call, and never actually
translates or completes anything.

## Registration is `TryAdd`-based

`AddThemiaAiGemini` / `AddThemiaAiOpenAiCompatible` register their own `IAiCompletionProvider`;
`AddThemiaAi` registers `IAiCompletionClient`, `IAiTextMasker` and `ITextTranslationService` with
`TryAdd*`, so a caller's own registration of any of these — made before or after the call — wins, and
calling `AddThemiaAi` more than once is a no-op the second time rather than a duplicate registration.
