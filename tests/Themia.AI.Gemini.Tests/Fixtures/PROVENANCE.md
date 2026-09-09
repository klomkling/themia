# Themia.AI.Gemini test fixtures

Every file here is the *exact* body a stub handler feeds `GeminiCompletionProvider`, so it is
strict JSON with no comment header. Provenance lives here instead, one section per fixture — the
provider parses with `System.Text.Json` defaults, which reject comments, exactly as the real
endpoint's bytes would be parsed.

> **Why these were not captured.** The plan for this package does not require a provider API key for any
> task: every provider test runs against a stub handler, and a fixture that cannot be captured is to be
> derived from the vendor's published reference and marked as such, rather than blocking the task. That
> decision is recorded in `.superpowers/sdd/2026-09-08-themia-ai/progress.md` (Task 5). Earlier drafts of
> these headers cited it as "Themia.AI plan Ruling 2", which is not findable — the rulings live in the
> execution ledger, not in the plan document.
>
> **This is still an open gate:** one live call against the real Gemini endpoint would replace all five of
> these with real captures, and any difference it revealed would be a finding about
> `GeminiCompletionProvider`'s parser, not about the fixtures.

## `gemini-completed.json`

UNPROVEN: from docs, not captured. No Gemini API key was available in this environment (see the note above). Derived from Google's published Generative Language REST reference for GenerateContentResponse / Candidate / FinishReason (https://ai.google.dev/api/generate-content, "FinishReason" and "UsageMetadata" sections) and the text-generation quickstart (https://ai.google.dev/gemini-api/docs/text-generation), as of 2026-09-09. If a later real capture's shape differs — a field this fixture assumes is present is actually absent, or FinishReason's string literal differs — that is a finding about GeminiCompletionProvider's parser, which reads exactly this path.

## `gemini-error.json`

UNPROVEN: from docs, not captured. No Gemini API key was available in this environment (see the note above). Derived from Google's published API error model (https://ai.google.dev/api/generate-content, standard google.rpc.Status error envelope), as of 2026-09-09. GeminiCompletionProvider maps this by HTTP status (a non-2xx, non-429 status -> ProviderError), not by parsing this body — the exact error text is not load-bearing.

## `gemini-filtered.json`

UNPROVEN: from docs, not captured. No Gemini API key was available in this environment (see the note above). Derived from Google's published Generative Language REST reference (https://ai.google.dev/api/generate-content, "FinishReason.SAFETY": "The candidate content was flagged for safety reasons." and "Candidate.safetyRatings"), as of 2026-09-09. A safety-filtered candidate carries no `content.parts` (there is no text to surface) but the API still reports usageMetadata — the input was read and billed for even though nothing was returned, which is the premise Usage_is_reported_on_a_filtered_call exists to catch.

## `gemini-max-tokens.json`

UNPROVEN: from docs, not captured. No Gemini API key was available in this environment (see the note above). Derived from Google's published Generative Language REST reference (https://ai.google.dev/api/generate-content, "FinishReason.MAX_TOKENS": "The maximum number of tokens as specified in the request was reached."), as of 2026-09-09. A MAX_TOKENS candidate still carries whatever partial text was generated before the cap — that partial `parts[0].text` is what GeminiCompletionProvider must surface as AiCompletion.Text alongside AiOutcome.Truncated.

## `gemini-rate-limited.json`

UNPROVEN: from docs, not captured. No Gemini API key was available in this environment (see the note above). Derived from Google's published API error model (https://ai.google.dev/gemini-api/docs/troubleshooting, HTTP 429 / RESOURCE_EXHAUSTED — "You've exceeded the rate limit"), as of 2026-09-09. GeminiCompletionProvider maps this by HTTP status (429 -> ProviderLimit), not by parsing this body, so the exact error message text is not load-bearing — but the shape matches what the real endpoint returns on quota exhaustion.
