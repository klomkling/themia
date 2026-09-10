# Themia.AI — design

**Status:** approach C approved (provider-shaped core plus translation as the one typed operation).
**Target version:** `0.24.0`, alongside `Themia.Geo`.
**Evidence:** coord #0115. Both consumers want caption and translation; neither has implemented either.
propertiezy stated that natural-language search is **not a committed feature** — discussed, never scoped
or scheduled — which removed the original justification for structured output being urgent.
**Supersedes:** the `Themia.Modules.AI` row in `docs/themia-architecture-overview.md` §B, on two points —
there is nothing to port, and there is no module. See §1 and §2.

---

## 1. The named sources contain no AI

The overview says to port ezy-assets' `GeminiAICaptionService` and `FallbackTextTranslationService`.
Both were read, and both are placeholders:

- **`GeminiAICaptionService`** is registered as `IAICaptionService` and its body is a `StringBuilder`
  concatenating request fields with emoji. Its own comment: *"Sprint 2: This is still a placeholder that
  simulates AI by formatting the context. In a real implementation, this would call the Gemini API."*
- **`FallbackTextTranslationService`** returns `text.Trim()` unchanged.

Grepping both repos for `generativelanguage`, `api.openai`, `anthropic.com`, `gemini-<n>`, `gpt-4`
returns **zero matches**; ezy-assets confirmed the same on their side. So this package is not improving
on a baseline — whatever ships here is the first real implementation.

One fact from ezy-assets worth carrying: their configuration already holds `Gemini__ApiKey` and
`Gemini__Model`. Someone chose a provider and stopped before wiring it. They describe that as a weak
preference and say they would not object to another.

### The placeholder is worse than nothing, and that shapes the contract

`FallbackTextTranslationService` returns `Task<string>`, and on failure returns the **source text**. That
signature cannot distinguish *"translated"* from *"handed you back what you gave me"*.

Trace what the caller does with it: `ProposalTranslation` is a cache keyed by `(ProposalId, LanguageCode)`.
A caller asking for `en-US`, receiving Thai text, and storing it produces a row **labelled English and
holding Thai**, with a `SourceHash` that marks it fresh. The public proposal page and the PDF then render
Thai to an English-language viewer, and nothing anywhere reports a problem — the row exists, it is
current, and it is wrong.

That is the silent-degradation shape this codebase has spent the last release removing. **§4's return
type is a result, not a string**, specifically so a caller cannot store an untranslated value while
believing it translated.

---

## 2. Package shape — and again, no module

```
Themia.AI                   net8.0;net10.0   IAiCompletionClient, AiPrompt/AiCompletion,
                                             ITextTranslationService, masking, options, DI
Themia.AI.Gemini            net8.0;net10.0   Google Gemini provider
Themia.AI.OpenAiCompatible  net8.0;net10.0   any OpenAI chat-completions endpoint
```

**No `Themia.Modules.AI`.** No tenant-scoped state, no schema, no `IThemiaModule` lifecycle — same
reasoning as `Themia.Geo`. Per-tenant usage accounting would need a table and would justify one; nobody
has asked for it (§9).

**Why `OpenAiCompatible` is the second provider and not OpenAI specifically.** One implementation of the
OpenAI chat-completions shape reaches OpenAI, Ollama, Groq, Cerebras, LM Studio and vLLM by changing a
base URL. That covers every free-tier and self-hosted option in one package, which is the direct answer
to the cost constraint: ezy-assets' work is **bursty at publish time**, per listing photo and
description, so per-call price matters more than a monthly floor.

`Themia.AI` itself takes no dependency on `Themia.Framework.*`, no database, and no ASP.NET.

---

## 3. `IAiCompletionClient`

```csharp
/// What a consumer calls. One registration; the implementation dispatches across providers.
public interface IAiCompletionClient
{
    Task<AiCompletion> CompleteAsync(
        AiOperation operation, AiPrompt prompt, CancellationToken ct = default);
}

/// What a provider package implements. Distinct from IAiCompletionClient: a provider talks to one
/// endpoint with a model it is told, and knows nothing about retry, failover or budgets.
public interface IAiCompletionProvider
{
    /// Identifies this provider in configuration. Compared ordinally, case-insensitively.
    string Key { get; }

    Task<AiCompletion> CompleteAsync(
        string model, AiPrompt prompt, TimeSpan timeout, CancellationToken ct = default);
}

/// The keys of the providers Themia ships. A plain string, not an enum: the core must not have to
/// enumerate every provider that could exist, or adding one would mean changing this package and an
/// adopter could never supply their own (Bedrock, Vertex, a company gateway).
public static class AiProviderKeys
{
    public const string Gemini = "gemini";
    public const string OpenAiCompatible = "openai-compatible";
}

/// Which configured model a call resolves to. Not a capability — every provider serves all of these
/// through the same endpoint; the operation exists so configuration can point them at different models.
public enum AiOperation
{
    Unspecified = 0,
    Completion,
    Translation,
}

public sealed record AiPrompt
{
    /// Instructions. NEVER put untrusted text here — see §7.
    public string? System { get; init; }

    /// The content being worked on. Untrusted input belongs here.
    public required string User { get; init; }

    public int? MaxOutputTokens { get; init; }
    public double? Temperature { get; init; }
}

public sealed record AiCompletion(
    AiOutcome Outcome,
    string? Text,
    AiUsage? Usage,
    string? Model,
    string? ProviderStatus);

public sealed record AiUsage(int InputTokens, int OutputTokens);

public enum AiOutcome
{
    Unspecified = 0,
    Completed,
    Truncated,       // hit the output limit; Text is partial but present
    Filtered,        // the provider's safety filter refused; retrying will not help
    ProviderLimit,   // quota or rate limit; try the next provider, do not retry this one
    ProviderError,   // transport or 5xx; a retry may work
}
```

**Two interfaces, and the split is what makes §6 implementable.** `IAiCompletionProvider` receives a
model name and a timeout and does one HTTP call; the dispatcher registered as `IAiCompletionClient`
resolves the model per provider per operation, applies retry, failover and the total budget, and never
appears in a provider package. Collapsing them into one interface would mean every provider
re-implementing dispatch, or the dispatcher being indistinguishable from a provider at the DI container.

Providers are resolved by `Key`: the dispatcher takes `IEnumerable<IAiCompletionProvider>` and matches
each entry of `Failover` against it. A `Failover` entry with no matching `Key` fails at startup (§6).

**`AiOperation` is on the call because otherwise §6's per-operation model configuration is unreachable.**
`ITextTranslationService` lives in this package and its only path to a provider is this interface. Without
an operation on the call, every request would resolve `CompletionModel` and the configured
`TranslationModel` would be a setting nobody reads — an adopter would set it, `ValidateOnStart` would
confirm it is present, and translation would silently run on the completion model. A configuration that
validates and has no effect is the same shape as §1's placeholder: it answers as though it worked.

The test that matters is therefore **"a translation call resolved `TranslationModel`"**, not "translation
returned text".

**There is no per-call model override.** An earlier draft put `Model` on the prompt while §6 also
configured one, with nothing saying which wins — and once failover exists the same call can run against
two providers that need different model names, so a single caller-supplied string cannot be right for
both. Model selection lives entirely in configuration (§6). Adding an override later is additive; having
two sources of truth from the start is not fixable later.

`Unspecified = 0` is reserved and rejected, as in every Themia enum since `SequenceEngine.Postgres = 0`
made `default` a valid value in `0.22.0`.

**Four failure outcomes rather than an exception, because the correct response differs for each** and a
single `AiException` collapses distinctions the caller needs:

| outcome | what a caller should do |
| --- | --- |
| `Truncated` | `Text` is real but cut mid-sentence. **Treating this as success ships a caption ending mid-word.** Either raise the limit and retry, or discard. |
| `Filtered` | Retrying is pointless and so is failing over — every provider will refuse similar content. Fall back to non-AI text. A property description that trips a safety filter must not block a publish. |
| `ProviderLimit` | Do **not** retry this provider. Fail over (§6). Retrying a quota rejection for every remaining item in a burst burns the rest of the allowance on calls that cannot succeed. |
| `ProviderError` | Retry with backoff, then fail over. |

**`Usage` is populated whenever the provider reports it, on every outcome — not only `Completed`.**
A `Filtered` call consumed its input tokens and is billed for them; a `Truncated` call consumed input and
a full output allowance. A caller recording cost from successes alone **undercounts, and undercounts most
on the calls that failed** — which is the opposite of useful when cost is the constraint ezy-assets
named.

`Usage` being null means *"the provider did not report usage"*, never *"this call was free"*. The XML doc
says exactly that, because the two readings differ and the null cannot distinguish them on its own.

Themia does not aggregate usage — that would need the table §9 defers.

---

## 4. `ITextTranslationService`

The one typed operation. Text in, text out, no domain vocabulary anywhere in it.

```csharp
public interface ITextTranslationService
{
    Task<TranslationResult> TranslateAsync(
        string text, string sourceLanguage, string targetLanguage, CancellationToken ct = default);
}

public sealed record TranslationResult(TranslationOutcome Outcome, string? Text);

public enum TranslationOutcome
{
    Unspecified = 0,
    Translated,       // Text is complete and storable
    SameLanguage,     // source and target match; Text is the input, and that is correct
    Incomplete,       // Text exists and is cut mid-sentence — DO NOT store
    Unavailable,      // no provider, or the provider failed; Text is null
}
```

**`SameLanguage` and `Unavailable` are separate on purpose.** Both concern input text being handed back;
only one of them means the caller may store anything. This is the distinction §1 showed the existing
placeholder cannot make, and it is the whole reason this returns a record rather than a `string`.

### The mapping from `AiOutcome`, stated because the obvious default is the §1 bug again

| `AiOutcome` | `TranslationOutcome` | `Text` |
| --- | --- | --- |
| `Completed` | `Translated` | the translation |
| `Truncated` | **`Incomplete`** | present, and cut mid-sentence |
| `Filtered` | `Unavailable` | **null** |
| `ProviderLimit` after failover is exhausted | `Unavailable` | **null** |
| `ProviderError` after retries and failover | `Unavailable` | **null** |

**`Truncated` is the one that needs a member of its own.** Its `Text` is non-empty, so the reflexive
mapping is `Translated` — and a caller then stores a sentence that stops mid-word into
`ProposalTranslation`, with a `SourceHash` marking it fresh, and the public page and the PDF render it.
That is §1's defect with a different cause: not the wrong language, but incomplete text, stored under a
label that says it is finished.

**`Unavailable` carries `Text = null`, never the source text.** Returning the input would be the helpful
thing to do and it is exactly what the placeholder did. A caller writing `row.Title = result.Text`
without reading the outcome must **fail**, not quietly persist Thai into an English row. `Incomplete`
cannot use the same protection — its text is the point — so those two failure shapes are deliberately
different: one breaks a careless caller immediately, the other requires the caller to actually read the
outcome.

### Language codes pass through; nothing is defaulted

`sourceLanguage` and `targetLanguage` go to the provider as given. Themia keeps **no** list of supported
languages: such a list goes stale, and support differs per provider and per model, so a Themia-side list
would reject languages a provider handles and accept ones it does not.

An unrecognised code is the provider's to reject, and its rejection surfaces as `Unavailable`. **Nothing
falls back to a default language.** The placeholder normalised unknown codes to `th-TH`, so asking for
`fr-FR` returned Thai with no error — a request for one language answered in another, reported as
success.

Empty or whitespace-only `text` short-circuits to `SameLanguage` with the input returned, without calling
a provider. A model call that can only return nothing still costs a call, and per-call cost is the
constraint ezy-assets named.

Caption is **not** here. Generating a property caption needs to know what a property is — its listing
type, price, amenities, nearby POIs — and that vocabulary is application domain. Captions are built by
the app as an `AiPrompt` against §3. Putting `ICaptionService` in a framework package would be the scope
violation `CLAUDE.md` exists to prevent.

---

## 5. Masking

Masking is available and worth building, but it is **not** what makes sensitive content safe to send.

```csharp
public interface IAiTextMasker
{
    MaskedText Mask(string text, IEnumerable<string> valuesToMask);
}

public sealed record MaskedText(string Text, IReadOnlyDictionary<string, string> Tokens)
{
    /// Substitutes the original values back. Throws when a token is missing or duplicated.
    public string Restore(string modelOutput);
}
```

**The caller supplies the values to mask.** Themia does not detect them: Thai has no capitalisation and
Thai person-name NER is unreliable, so a detector would miss names and the caller would never know it had
sent one. An app that knows `contact.Name` is a name can mask it exactly; a regex over Thai prose cannot.

**Token format is `<x id="1"/>`.** Models are trained on XLIFF-style content and leave these intact.
`{{CUSTOMER_NAME}}` gets translated — the model renders the placeholder into the target language and the
substitution back fails silently. Tokens are numbered and unique so a model reordering the sentence
(which translation does constantly) does not break the mapping.

**`Mask` must not emit a token that already occurs in the source.** The text being masked is untrusted
by construction (§7) — a listing description can contain `<x id="1"/>` literally, whether by accident or
because someone tried it. `Restore` would then see the token twice and throw, and **one line in a
description would make that listing permanently untranslatable**. That is a denial an ordinary user can
trigger, so `Mask` scans the source for the token pattern first and numbers from beyond any collision (or
escapes the occurrences it finds). A test feeds text already containing a token.

**`Restore` throws rather than returning partial text.** A model rewriting a long passage can drop a
token; if `Restore` skipped it, `<x id="3"/>` would appear in text shown to a user. Failing the operation
is correct: the caller falls back to untranslated text, which is visibly not-translated rather than
visibly broken.

**Why an exception here when §3 argues against exception-based control flow.** The two are different
kinds of operation. `CompleteAsync` is I/O with several outcomes a caller must branch on differently —
retry, fail over, fall back — so collapsing them into one exception loses information the caller needs.
`Restore` is a pure string operation with exactly one failure and exactly one correct response: abandon
the result. There is nothing for a caller to branch on, so an outcome enum would add a code path whose
only body is the same fallback.

**`Restore` runs before any HTML encoding.** Encoding first turns `<x id="1"/>` into `&lt;x id="1"/&gt;`
and the substitution no longer matches — the operation then throws and falls back, so the failure is at
least loud, but the ordering is a requirement and not a preference. Restore, then encode the restored
text as §7 requires.

### What masking does not do — stated so it is not over-trusted

- **The rest of the document still identifies people.** Masking a name out of *"the client is a doctor at
  Siriraj who wants to be near work, budget 8M"* protects nobody.
- **Pronouns degrade.** Thai names carry gender that the model uses to pick "he"/"she"; a masked name
  makes it guess.
- Masking is a defence in depth. Deciding **which fields are sent at all** is the primary control, and
  that decision is the app's — see §8.

---

## 6. Configuration, model selection and failover

**Model names belong to a provider, not to an operation.** An earlier draft configured one model per
operation with a separate failover list of providers, which is broken by construction: failing over from
Gemini to an OpenAI-compatible endpoint would have sent `gemini-2.5-flash-lite` to Ollama, which has no
such model. It would have failed **every time failover fired** — that is, precisely during the free-tier
rate-limit burst that failover exists to survive — and failed as a `ProviderError` from the second
provider, so the symptom would point at the wrong component.

```csharp
services.AddThemiaAiGemini(o =>
{
    o.ApiKey           = cfg["Gemini:ApiKey"]!;
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

### Timeouts

**Every provider has a timeout and it is not `HttpClient`'s default.** That default is 100 seconds, which
is far too long behind a publish button, and a hung call is **not** an error — it never reaches
`ProviderError`, so it never triggers failover. It just occupies the caller while a burst of
per-photo and per-description calls queues behind it.

A timeout maps to `ProviderError`, so it fails over like any other transport failure. Cancellation
requested by the *caller* is distinct: it propagates as `OperationCanceledException` and does not fail
over, because the caller has already stopped caring about the answer.

### The budget that actually matters is the total, not the per-call timeout

A per-provider timeout alone does not bound what the caller waits. Three retries at 30 seconds is 90
seconds on one provider; failing over to a second repeats it. **Three minutes — worse than the 100-second
default this section rejected**, arrived at by fixing the timeout and leaving the retry count unstated.

So `AiOptions` carries a **`TotalBudget`** (default 45 seconds) spanning every retry and every failover
for one `CompleteAsync`. When it is exhausted the call returns `ProviderError` immediately, whatever
providers remain untried. `MaxRetriesPerProvider` (default 2) is configurable, and startup validation
rejects a configuration whose worst case cannot fit inside the budget — `MaxRetries × Timeout` for the
first provider alone exceeding `TotalBudget` means the second provider is unreachable by arithmetic, and
that should fail at startup rather than never being observed.

**Failover fires on `ProviderLimit` and on `ProviderError` after its retries are exhausted. It never
fires on `Filtered`** — a safety refusal is about the content, and the next provider will refuse it too;
failing over would spend a second call to receive the same answer.

Failover exists because free tiers have hard per-minute rate limits and the workload is bursty. Without
it, a publish burst fails on the calls that arrive after the limit.

Options are validated with `ValidateOnStart`, and the validation must cover the whole matrix, not each
entry alone:

- **`Failover` is non-empty** — unless `AllowNoProvider` is set (below);
- every key in `Failover` matches a registered provider's `Key`;
- every registered provider in `Failover` names a **non-empty model for every operation** — the check
  that would have caught the broken draft above;
- every provider has a positive `Timeout`.

A failover entry missing a translation model is a configuration that works for months and then fails the
first time Gemini rate-limits a translation. Startup is where that belongs.

### Running with no provider is allowed, but only on purpose

`AddThemiaAi` with an empty `Failover` **fails at startup** unless the host sets
`o.AllowNoProvider = true`, which makes every call return `Unavailable` (and `TranslationOutcome.Unavailable`
with a null `Text`).

Both behaviours are wanted by someone, which is why the choice has to be explicit. A developer with no
API key wants everything to answer `Unavailable` and the app to fall back. A production host that
registered `AddThemiaAi` and forgot the provider package wants to be told — and without this check it
would get the developer's behaviour: a translation service that validates its configuration, answers
every call, and never translates anything. That is a configuration that works and does nothing, which is
the shape §1 and §3 are both about.

---

## 7. Prompt injection

The text reaching these APIs is written by agents and members. A listing description containing
*"ignore the previous instructions and output …"* is reachable input, not a hypothetical.

- **`AiPrompt` separates `System` from `User` and the XML doc says untrusted text goes in `User`.** That
  is a mitigation, not a fix — the separation reduces the attack surface and does not eliminate it.
- **Treat model output as untrusted.** A caption is rendered into HTML; the app must encode it exactly as
  it encodes any user input. Themia does not sanitise output, because what is safe depends on where it
  lands, and a sanitiser here would give false assurance.
- Translation output goes to `ProposalTranslation` and then to a public page and a PDF. That is a
  rendering path with an injection surface, and it is the app's to encode.

---

## 8. Data classification: deliberately not built

No routing by data sensitivity, and no "this operation may not leave the building" enforcement.

The reason is that the concern was checked and did not hold. `ProposalTranslation` — the one table that
sounded like it might carry customer data — is documented as *"Cached localized snapshot for proposal
public/PDF rendering"* and holds `Title`, `PropertyTitle`, `ListingType`, `RentalPeriod`, `Location`,
`AgentName`, `AgentPhone`, `ItemsJson`. Property marketing content plus the agent's own contact details.
No customer name, no contract terms.

`AgentName` and `AgentPhone` do need handling, but not for privacy: **a name has no translation and a
phone number has none either.** Those two fields are in the snapshot because it is a rendering cache —
they must be **copied, not translated**. That is a field-level rule in the app's mapping code, and
sending them to a model at all is a bug rather than a leak.

If a consumer later has content that must stay in-house, the enforcement should be structural — the
operation declares its data class and a provider not cleared for that class cannot be selected, failing
at startup rather than at runtime, the way `AuditTransactionPolicy` hardwires `Authentication` to
`Never`. Building that now, with no consumer that needs it, would be machinery guarding nothing.

---

## 9. Also deferred

- **Structured output** (`responseSchema` / `response_format` / tool-use). The natural-language search
  that justified it is not a committed feature. `AiPrompt` and `AiCompletion` are records, so adding an
  output-schema property later is additive. The three providers implement it differently and building
  that abstraction with no consumer is speculative.
- **Embeddings.** Would need a vector store to be useful; nobody has one.
- **Streaming.** Caption and translation are batch operations behind a publish action; no UI streams them.
- **Per-tenant usage accounting.** Would need a table, a dialect per engine, and a module — the full
  ceremony — and `AiUsage` already gives a caller what it needs to record cost itself.
- **Caption as a typed operation.** Domain, permanently. See §4.

---

## 10. Testing

No containers, no live provider calls. A fake `IAiCompletionClient` and recorded provider payloads.

- **Every `AiOutcome` maps from a payload captured from a real provider response** — `Filtered` and
  `ProviderLimit` included — stored as a fixture with the date and endpoint it came from. A hand-written
  payload puts the expected value and the implementation under one author: if a safety refusal really
  reports `BLOCKED` where the fixture assumed `SAFETY`, the test passes and production takes the
  `ProviderError` branch, retries a refusal, and fails over to a provider that will also refuse. Any
  outcome with no captured payload behind it is marked **unproven** in the test file rather than quietly
  asserted. The distinctions in §3 only pay off if each is exercised.
- **`Truncated` is not reported as `Completed`** when the provider signals a length stop.
- **`Restore` throws when a token is missing, and when it appears twice.** Both are what a real model
  does to a long passage; a masking test that only covers the happy path proves nothing about the case
  it exists for.
- **`Restore` succeeds when the model reorders tokens**, which translation does routinely.
- **Every `AiOutcome` maps to the `TranslationOutcome` in §4's table** — one test per row. `Truncated`
  becomes `Incomplete` and **not** `Translated`: that is the row a reflexive implementation gets wrong,
  because the truncated text is non-empty and looks like a successful translation.
- **`Unavailable` carries a null `Text`.** Assert the null, not just the outcome — an implementation
  returning the source text passes an outcome-only assertion while reproducing the §1 bug exactly.
- **`TranslationOutcome.Unavailable` is distinguishable from `SameLanguage`** by a caller holding only
  the result. This is the §1 defect; assert the caller can tell them apart, not merely that the enum has
  two members.
- **An unrecognised language code is not silently defaulted.** Ask for a language the provider rejects
  and assert `Unavailable`, not text in some other language.
- **Empty and whitespace-only input calls no provider.** Assert against a fake that records calls; a test
  asserting only the returned value passes while a call was made and paid for.
- **An empty `Failover` fails at startup, and succeeds with `AllowNoProvider = true`** returning
  `Unavailable` for every call.
- **Failover fires on `ProviderLimit` and does not fire on `Filtered`** — assert the second provider was
  not called in the filtered case.
- Options validation rejects an empty model name and a failover entry naming an unregistered provider,
  at startup.
- **A provider timeout surfaces as `ProviderError` and fails over**; a caller-supplied cancellation
  surfaces as `OperationCanceledException` and does **not**. Both paths asserted — collapsing them means
  either a hung provider stalls the caller, or the caller's own cancel burns a second provider's quota.
- **A translation call resolves `TranslationModel`, not `CompletionModel`** — assert the model name the
  provider was handed, not that translation returned text. This is the assertion that fails if
  `AiOperation` stops reaching the provider, and "it translated" passes either way.
- **Startup validation rejects a failover entry whose provider names no model for one operation**, and
  rejects a configuration whose `MaxRetriesPerProvider × Timeout` already exceeds `TotalBudget` — the
  second provider is then unreachable by arithmetic and nothing at runtime would ever say so.
- **`TotalBudget` bounds the whole call**, retries and failover included: with two providers both timing
  out, the caller waits the budget, not the sum.
- **`Usage` is populated on `Filtered` and `Truncated`** where the captured payload reports it. A
  cost-attribution test over successes alone misses exactly the calls that fail.
- **`Mask` handles source text that already contains a token**, and the resulting `Restore` round-trips.
- **No API key appears in any log, including `System.Net.Http.HttpClient`'s own request-URI logging.**
  Capture that category too, not only the provider's own `ILogger` — the Gemini key is a query
  parameter, and the handler that logs the URI is one nobody wrote.
- `Themia.AI` references no `Themia.Framework.*` package; asserted by a test.

---

## 11. Decisions — do not relitigate

1. Approach C: provider-shaped core, translation as the one typed operation, caption stays in the app.
2. No `Themia.Modules.AI`, no schema, no tenant state.
3. `ITextTranslationService` returns a **result**, not a `string`. A caller must be unable to store an
   untranslated value believing it translated.
3b. `Truncated` maps to its own `Incomplete`, never to `Translated`. `Unavailable` carries a null `Text`,
   never the source. Language codes pass through unchanged and nothing defaults to a fallback language.
3c. An empty `Failover` fails at startup unless `AllowNoProvider` is set. "Answers every call, translates
   nothing" must be a decision, not an omission.
4. Four distinct failure outcomes, no exception-based control flow. `Filtered` never retries and never
   fails over.
5. **Model names are configured per provider, per operation** — never per operation alone. A single
   model name across a failover list sends one provider's model to another and fails exactly when
   failover fires. Startup validation covers the whole provider×operation matrix.
5b. No per-call model override on `AiPrompt`; configuration is the only source. `AiOperation` travels on
   the call instead — without it the per-operation model configuration is unreachable and validates
   anyway.
5d. `TotalBudget` bounds retries plus failover for one call. A per-call timeout alone does not: retries
   multiply it and failover repeats it.
5e. `Usage` is reported on every outcome the provider reports it for. Null means "not reported", never
   "free".
5c. Every provider carries an explicit timeout. `HttpClient`'s 100-second default is too long behind a
   publish button, and a hung call never reaches `ProviderError`, so it never fails over.
6. Second provider is `OpenAiCompatible`, not OpenAI — one implementation reaches every self-hosted and
   free-tier endpoint.
6b. `IAiCompletionProvider` (what a provider package implements, given a model and a timeout) is
   separate from `IAiCompletionClient` (what a consumer calls, which dispatches). Providers are matched
   by a **string `Key`**, never a closed enum, so an adopter can supply a provider Themia does not ship.
7. Masking exists; the caller supplies the values, `<x id="n"/>` is the token, `Restore` throws rather
   than emitting partial text, and `Mask` avoids tokens already present in the source so untrusted input
   cannot make a listing untranslatable. Restore precedes HTML encoding. It is defence in depth, not the
   primary control.
8. No data-classification routing, no structured output, no embeddings, no streaming, no usage table —
   each with its reason in §8 and §9.
