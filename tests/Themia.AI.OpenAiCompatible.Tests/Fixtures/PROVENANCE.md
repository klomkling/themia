# Themia.AI.OpenAiCompatible test fixtures

Every file here is the *exact* body a stub handler feeds `OpenAiCompatibleCompletionProvider`, so
it is strict JSON with no comment header. Provenance lives here instead, one section per fixture —
the provider parses with `System.Text.Json` defaults, which reject comments, exactly as the real
endpoint's bytes would be parsed.

## `openai-completed.json`

CAPTURED. `POST http://localhost:11434/v1/chat/completions`, model `qwen2.5-coder:32b`, captured
2026-09-09 against a local Ollama instance (Ollama serves the OpenAI chat-completions shape at this
path). Request body: `{"model":"qwen2.5-coder:32b","messages":[{"role":"user","content":"Say hello
in one short sentence."}]}`. This is the exact response body, byte-for-byte (only re-indented for
readability; no field was added, removed or renamed).

## `openai-max-tokens.json`

CAPTURED. `POST http://localhost:11434/v1/chat/completions`, model `qwen2.5-coder:32b`, captured
2026-09-09 against the same local Ollama instance, with `"max_tokens": 3` added to the request to
force a length stop. Request body: `{"model":"qwen2.5-coder:32b","messages":[{"role":"user",
"content":"Write a long paragraph about the ocean."}],"max_tokens":3}`. Confirms Ollama's
OpenAI-compatible endpoint reports `finish_reason: "length"` (not e.g. `"max_tokens"`) and still
returns the partial `message.content` generated before the cap, exactly as OpenAI's own endpoint
documents. Exact response body, re-indented only.

## `openai-filtered.json`

UNPROVEN: from docs, not captured. Ollama has no content-filtering layer of its own, so no local
endpoint could produce a `content_filter` finish reason to capture. A fixture that cannot be captured
is derived from the vendor's published reference and marked as such rather than blocking the task —
that decision is recorded in `.superpowers/sdd/2026-09-08-themia-ai/progress.md`, not in the plan
document, so an earlier draft's "R2 in the plan" citation pointed at nothing findable. Derived from OpenAI's published Chat
Completion object reference (https://developers.openai.com/api/docs/api-reference/chat/object,
read 2026-09-09), which lists `finish_reason: "content_filter"` as "Output omitted due to content
filtering" and documents `message.content` as nullable. `usage` is still populated on this fixture
on purpose: a filtered call still consumed and is billed for its input tokens, which is exactly the
case `Usage_is_reported_on_a_filtered_call` exists to catch — mirroring Task 5's
`gemini-filtered.json`.

## `openai-rate-limited.json`

UNPROVEN: from docs, not captured. Forcing a real 429 would require exhausting a paid provider's
quota; Ollama has no rate limiting to trigger. Derived from OpenAI's standard error envelope
(`{"error": {"message", "type", "param", "code"}}`), documented across the OpenAI API reference and
error-codes guide (https://developers.openai.com/api/docs/guides/error-codes, read 2026-09-09,
which names `rate_limit_error` / `rate_limit_exceeded` for 429 responses).
`OpenAiCompatibleCompletionProvider` maps this by HTTP status alone (429 -> `ProviderLimit`), not by
parsing this body, so the exact error text is not load-bearing — only the 429 status code the test
supplies alongside this fixture is.

## `openai-error.json`

UNPROVEN: from docs, not captured. No local endpoint reachable in this environment fails with a 5xx
on demand. Derived from the same OpenAI error envelope
(https://developers.openai.com/api/docs/guides/error-codes, read 2026-09-09, `server_error` /
`service_unavailable_error` for 500/503 responses). `OpenAiCompatibleCompletionProvider` maps any
non-2xx, non-429 status to `ProviderError` by HTTP status alone, not by parsing this body, so the
exact error text is not load-bearing.
