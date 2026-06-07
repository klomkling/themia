# Themia.Quartz — Newtonsoft.Json Removal (System.Text.Json migration) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make `Themia.Quartz` completely free of `Newtonsoft.Json` (and `JsonSubTypes`, `Microsoft.AspNetCore.Mvc.NewtonsoftJson`), migrating the vendored dashboard's JSON layer to `System.Text.Json` (STJ) **without changing any wire format** the `.hbs` templates / `Content/Scripts/*.js` depend on.

**Architecture:** Test-first. The dashboard's JSON-heavy flows are currently untested, so Task 1 captures the exact CURRENT (Newtonsoft) wire format in regression tests that must stay green through the migration. Then replace `JsonSubTypes` runtime polymorphism with a custom STJ `JsonConverter<TypeHandlerBase>` (+ a `JsonConverter<Type>` for `EnumHandler.EnumType`), replace `JRaw`, do the mechanical `JsonConvert`→`JsonSerializer` swaps, drop `AddNewtonsoftJson()`, and remove the three packages.

**Tech stack:** .NET 8 + .NET 10; System.Text.Json (in-box); xUnit + `Microsoft.AspNetCore.TestHost`.

**Prerequisite:** PR #56 (Themia Scheduling) is merged to `main`. Branch this work off `main` as `feat/themia-quartz-stj`.

**Cross-cutting conventions:** nullable enabled; `TreatWarningsAsErrors`; full XML docs on authored types. The vendored dashboard's existing `NoWarn` (CS8xxx/CS1591/RS0016 for `Dashboard/**`) stays. After this migration the csproj's "accepted Newtonsoft trade-off" comment is removed. Conventional commits, no co-author trailers.

**Build/test:**
```bash
dotnet build Themia.sln -c Release                 # net8.0 + net10.0
dotnet test tests/Themia.Quartz.Tests -c Release   # unit + dashboard smoke + the new JSON regression tests
```

**Success criteria (all required):**
1. `grep -rn "Newtonsoft\|JsonSubTypes\|JsonConvert\|JObject\|JRaw\|AddNewtonsoftJson" src/neutral/Themia.Quartz --include="*.cs"` → **zero hits** (except a historical URL comment if any).
2. `Themia.Quartz.csproj` references **no** `Newtonsoft.Json`, `JsonSubTypes`, or `Microsoft.AspNetCore.Mvc.NewtonsoftJson`; those pins removed from `Directory.Packages.props` if unused elsewhere.
3. All Task-1 wire-format regression tests pass **unchanged** (byte-identical JSON), plus dashboard smoke + existing tests, on **net8.0 and net10.0**.
4. 0 warnings.

---

## File Structure
- Modify: `src/neutral/Themia.Quartz/Dashboard/TypeHandlerService.cs` (STJ + custom converter)
- Create: `src/neutral/Themia.Quartz/Dashboard/Json/TypeHandlerJsonConverter.cs`, `Json/SystemTypeJsonConverter.cs`
- Modify: `Dashboard/Controllers/JobDataMapController.cs` (JRaw → manual; `JsonException`)
- Modify: `Dashboard/Controllers/PageControllerBase.cs` (drop Newtonsoft branch)
- Modify: `Dashboard/Models/TriggerViewModel.cs`, `Dashboard/Helpers/HandlebarsHelpers.cs`, `Dashboard/Helpers/JsonErrorResponse.cs`
- Modify: `src/neutral/Themia.Quartz/ServiceCollectionExtensions.cs` (drop `AddNewtonsoftJson()`)
- Modify: `src/neutral/Themia.Quartz/Themia.Quartz.csproj`, `Directory.Packages.props`, `CHANGELOG.md`
- Create tests: `tests/Themia.Quartz.Tests/Json/TypeHandlerSerializationTests.cs`, `JobDataMapJsonTests.cs`, `MisfireInstructionsJsonTests.cs`, `HandlebarsJsonHelperTests.cs`, and an endpoint test in `DashboardSmokeTests.cs` (or a new `JobDataMapEndpointTests.cs`).

---

## Task 1: Capture the current (Newtonsoft) wire format in regression tests

**Goal:** lock the exact JSON the templates/JS consume, BEFORE changing serializers. These tests must pass against today's Newtonsoft code and remain green post-migration.

**First, read to learn the real shapes:**
- `Dashboard/TypeHandlerService.cs` — the `JsonSubtypesConverterBuilder.Of(typeof(TypeHandlerBase), nameof(TypeHandlerBase.TypeId))`, `Serialize`/`Deserialize`, `JsonSerializerSettings` (NullValueHandling).
- `Dashboard/TypeHandlers/*.cs` — `TypeHandlerBase` (`TypeId` => `GetType().FullName`?), and every concrete subtype incl. `EnumHandler` (the `System.Type EnumType` property), `DateTimeHandler`, `NumberHandler`, `StringHandler`, `BooleanHandler`, `FileHandler`.
- `Dashboard/Controllers/JobDataMapController.cs` — `$typeHandlerScripts` build (`JRaw`), `ChangeType`.
- `Dashboard/Models/TriggerViewModel.cs` — `CreateMisfireInstructionsJson()`.
- `Dashboard/Helpers/HandlebarsHelpers.cs` — the `{{json}}` helper(s).

- [ ] **Step 1.1: `TypeHandlerSerializationTests`** — for EACH concrete `TypeHandlerBase` subtype (including a configured `EnumHandler` with a real `EnumType`), assert `TypeHandlerService.Serialize(handler)` then `Deserialize(...)` round-trips to an equal handler, AND assert the decoded Base64 JSON contains the discriminator `"TypeId":"<FullName>"` and the expected PascalCase property names. Capture the exact JSON string for at least `StringHandler` and `EnumHandler` as literal expected values (this pins the wire format). These run against the current Newtonsoft impl and MUST pass now.
- [ ] **Step 1.2: `MisfireInstructionsJsonTests`** — assert `TriggerViewModel`'s misfire-instructions JSON has the exact keys (`"cron"`,`"simple"`,…) and int→string maps the Edit template + JS expect.
- [ ] **Step 1.3: `HandlebarsJsonHelperTests`** — render the `{{json}}` helper over a representative anonymous object (the `new { Value, StringValue, TypeHandler }` shape from the type-handler scripts) and assert the produced JSON (PascalCase, null handling).
- [ ] **Step 1.4: `JobDataMapEndpointTests`** (TestServer, authorized) — GET the `$typeHandlerScripts` JS (`TypeHandlers.js` action) and assert the output contains the expected `var $typeHandlerScripts = {...}` with each `TypeId` key and the raw JS function bodies intact; POST `ChangeType` for a String→Number conversion and assert the HTML fragment response. These pin the `JRaw` behavior.
- [ ] **Step 1.5:** Run all new tests against current code → **all green** (Newtonsoft). Commit: `test(quartz): pin dashboard JSON wire format before STJ migration`.

> These tests are the safety net for the whole migration. If any cannot be made to pass against the current impl, STOP and report — do not proceed blind.

---

## Task 2: Custom STJ converters for the type-handler polymorphism

- [ ] **Step 2.1: `SystemTypeJsonConverter : JsonConverter<Type>`** (`Dashboard/Json/SystemTypeJsonConverter.cs`) — serialize a `System.Type` as its assembly-qualified (or full) name string matching what Newtonsoft emitted for `EnumHandler.EnumType` (verify the exact string Newtonsoft produced via the Task-1 captured JSON), and resolve it back on read (`Type.GetType(name)` with the same qualification). Match the captured format EXACTLY.
- [ ] **Step 2.2: `TypeHandlerJsonConverter : JsonConverter<TypeHandlerBase>`** (`Dashboard/Json/TypeHandlerJsonConverter.cs`) — replicate JsonSubTypes' RUNTIME registration: hold a `IReadOnlyDictionary<string, Type> typeIdToType` (built from the registered handlers). On `Read`, parse with `JsonDocument`/`Utf8JsonReader`, read the `"TypeId"` discriminator, look up the concrete `Type`, and `JsonSerializer.Deserialize(concreteType, ...)`. On `Write`, write the object's properties plus inject `"TypeId"` = the handler's `TypeId`. Preserve PascalCase + NullValueHandling-equivalent (`DefaultIgnoreCondition` as needed to match the captured JSON). The converter is constructed with the registered type map (from `TypeHandlerService`).
- [ ] **Step 2.3: Migrate `TypeHandlerService`** to STJ: replace the `JsonSubtypesConverterBuilder`/`JsonSerializerSettings` with a `JsonSerializerOptions` that registers `TypeHandlerJsonConverter` (+ `SystemTypeJsonConverter`); `Serialize`/`Deserialize` use `JsonSerializer`. Keep the Base64 wrapping identical.
- [ ] **Step 2.4:** Run `TypeHandlerSerializationTests` → green with the SAME captured JSON (byte-identical discriminator + properties, incl. EnumHandler). If the wire format differs, fix the converters until it matches. Commit: `refactor(quartz): STJ polymorphic converter for type handlers (replaces JsonSubTypes)`.

---

## Task 3: Replace `JRaw` in `$typeHandlerScripts`

- [ ] **Step 3.1:** In `JobDataMapController`, the `$typeHandlerScripts` output embeds verbatim JS function literals (not valid JSON). Replace the `JRaw`+`JsonConvert.SerializeObject` approach with explicit string building (a `StringBuilder` producing `var $typeHandlerScripts = { "<TypeId>": <rawJsFunction>, ... };`), escaping the string keys but emitting the function bodies raw — matching the exact output captured in `JobDataMapEndpointTests`. Change the `catch (JsonSerializationException)` to `catch (JsonException)`.
- [ ] **Step 3.2:** Run `JobDataMapEndpointTests` → green (same JS output). Commit: `refactor(quartz): build $typeHandlerScripts JS without Newtonsoft JRaw`.

---

## Task 4: Mechanical STJ conversions (remaining files)

- [ ] **Step 4.1: `TriggerViewModel`** — `JsonConvert.SerializeObject(map, Formatting.None)` → `JsonSerializer.Serialize(map)`. Run `MisfireInstructionsJsonTests` → green.
- [ ] **Step 4.2: `HandlebarsHelpers`** — the `{{json}}` helper(s) → `JsonSerializer.Serialize(arg, options)` with a shared `JsonSerializerOptions` (PascalCase default). Run `HandlebarsJsonHelperTests` → green.
- [ ] **Step 4.3: `PageControllerBase`** — remove the Newtonsoft `JsonSerializerSettings`/`DefaultContractResolver` and the runtime executor-type check; use the STJ `JsonSerializerOptions` (the STJ branch already exists). `JsonErrorResponse` — convert its Newtonsoft usage to STJ. Replace any `[JsonProperty("x")]` → `[JsonPropertyName("x")]` and `[JsonIgnore]` (Newtonsoft) → `System.Text.Json.Serialization.JsonIgnore`.
- [ ] **Step 4.4:** Build + full `Themia.Quartz.Tests` → green both TFMs. Commit: `refactor(quartz): migrate remaining dashboard JSON to System.Text.Json`.

---

## Task 5: Drop `AddNewtonsoftJson()` + remove packages

- [ ] **Step 5.1:** In `ServiceCollectionExtensions.AddThemiaQuartz`, remove `.AddNewtonsoftJson()` (the controllers now serialize via STJ). Update the XML doc that mentions the Newtonsoft formatter.
- [ ] **Step 5.2:** Remove `<PackageReference>` to `Newtonsoft.Json`, `JsonSubTypes`, `Microsoft.AspNetCore.Mvc.NewtonsoftJson` from `Themia.Quartz.csproj`; remove the Newtonsoft "accepted trade-off" comment. Remove the corresponding `<PackageVersion>` pins from `Directory.Packages.props` **only if** no other project uses them (grep the solution first — `Microsoft.AspNetCore.Mvc.NewtonsoftJson` may be test-only; `Newtonsoft.Json` must be checked across all packages).
- [ ] **Step 5.3: Verify zero Newtonsoft:** `grep -rn "Newtonsoft\|JsonSubTypes\|JsonConvert\|JObject\|JRaw\|AddNewtonsoftJson" src/neutral/Themia.Quartz --include="*.cs"` → no hits. `dotnet list src/neutral/Themia.Quartz package --include-transitive | grep -i newtonsoft` → none.
- [ ] **Step 5.4:** `dotnet build Themia.sln -c Release --no-incremental` → 0 warnings; `dotnet test tests/Themia.Quartz.Tests -c Release` → all green both TFMs (incl. ALL Task-1 wire-format tests unchanged). Update `CHANGELOG.md` `[Unreleased]`: "`Themia.Quartz` is now `System.Text.Json`-only — Newtonsoft.Json/JsonSubTypes/Mvc.NewtonsoftJson removed; dashboard JSON migrated with wire-format-pinning tests." Update `VENDORING.md` (remove the Newtonsoft trade-off note). Commit: `refactor(quartz): remove Newtonsoft.Json — Themia.Quartz is now System.Text.Json-only`.

---

## Final verification
- [ ] Zero Newtonsoft/JsonSubTypes references (src + package graph).
- [ ] All Task-1 wire-format regression tests pass byte-identical → no template/JS-visible JSON change.
- [ ] 0 warnings, full suite green net8.0 + net10.0; dashboard smoke still renders.
- [ ] Final code review over the branch; then `MapThemiaQuartz` doc no longer mentions Newtonsoft.

---

## Self-Review
**Coverage:** every Newtonsoft usage from the feasibility map is addressed — TypeHandlerService/JsonSubTypes (Task 2), JRaw/$typeHandlerScripts (Task 3), TriggerViewModel + HandlebarsHelpers + PageControllerBase + JsonErrorResponse (Task 4), AddNewtonsoftJson + packages (Task 5), all guarded by the Task-1 wire-format tests written first.
**Risk:** the two genuine blockers (`EnumHandler.EnumType` `System.Type` round-trip; runtime-registered polymorphism) are isolated in Task 2's custom converters and validated against the Task-1 captured JSON. If a converter can't reproduce the exact captured wire format, that's the signal to stop and reassess rather than ship a shape change. The `JRaw`→string-builder change (Task 3) is the other architectural shift, validated by the endpoint test.
**Type consistency:** `TypeHandlerJsonConverter` consumes the same runtime type map `TypeHandlerService` already builds; `SystemTypeJsonConverter` matches the captured `EnumType` string format.
