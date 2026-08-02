# Themia.Messaging HMAC Transport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `Themia.Messaging` a signed HTTP transport — `themia-hmac-v1` signing and verification, an outbox dispatcher that delivers over HTTP, and a receiving filter with a loop guard — pinned to golden vectors supplied by the two live consumers.

**Architecture:** Three new packages. `Themia.Messaging.Hmac` holds the scheme with no HTTP or ASP.NET dependency, so both ends share one canonicalizer and it is testable in isolation. `Themia.Messaging.Http` implements `IOutboxDispatcher<ClaimedMessageRow>` over `IHttpClientFactory`. `Themia.Messaging.AspNetCore` verifies inbound requests and runs the loop guard.

**Tech Stack:** .NET 10, `System.Security.Cryptography` (in-box), `IHttpClientFactory`, ASP.NET Core endpoint filters, xUnit.

**Spec of record:** `docs/superpowers/specs/2026-07-31-themia-messaging-hmac-transport-design.md`. Read it before starting; it carries the reasoning for decisions this plan only states.

## Global Constraints

- All three new packages target **`net10.0`** only.
- `TreatWarningsAsErrors=true`, `GenerateDocumentationFile=true` — every public member needs an XML doc comment or the build fails.
- `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` per package. Undeclared public members are **RS0016**; a removed member whose entry remains is **RS0017**. Build with `--no-incremental` and fix both.
- Central package management: a new `PackageReference` needs a matching `PackageVersion` in `Directory.Packages.props` or restore fails with **NU1010**.
- `System.Text.Json` only, never `Newtonsoft.Json`. `ILogger<T>` only, never `Console.WriteLine`.
- **The canonical string is fixed and not configurable.** `{timestamp}\n{METHOD}\n{pathAndQuery}\n{body}`, LF separators, ISO-8601 `"O"` timestamp, `HMACSHA256` over UTF-8 bytes keyed with UTF-8 bytes of the secret, lowercase hex output.
- **The rejection statuses are normative**, not implementation choices: 408 stale timestamp, 401 missing/unparseable timestamp or signature mismatch or unknown key-id, 400 unrecognised scheme header, 413 oversize body.
- **Never log a secret, a signature, or a request body.**
- Commit subjects `<type>: <subject>`, imperative. **Never** add `Co-authored-by:` or "Generated with" trailers.
- Run from `Packages/themia/`. Branch off `main`; do not commit `CLAUDE.md`.

---

## File Structure

**`src/neutral/Themia.Messaging.Hmac/`** — the scheme, no HTTP, no ASP.NET
- `ThemiaHmacV1.cs` — canonical-string construction and signature computation
- `HmacHeaderNames.cs` — resolves the five header names from a prefix
- `HmacVerificationResult.cs` — the verdict enum plus the matched key id
- `HmacVerifier.cs` — timestamp window, key selection, fixed-time comparison
- `MessagingPeer.cs`, `MessagingPeerRegistry.cs`, `HmacOptions.cs` — peers, keys, routes
- `DependencyInjection/HmacServiceCollectionExtensions.cs` — `AddThemiaMessagingHmac`
- `PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`

**`src/neutral/Themia.Messaging.Http/`**
- `HttpMessageDispatcher.cs` — `IOutboxDispatcher<ClaimedMessageRow>`
- `HttpStatusClassifier.cs` — status → `DispatchOutcome`
- `DependencyInjection/HttpServiceCollectionExtensions.cs` — `AddThemiaMessagingHttp`
- `PublicAPI.*.txt`

**`src/neutral/Themia.Messaging.AspNetCore/`**
- `HmacVerificationFilter.cs` — the endpoint filter
- `LoopGuard.cs` — origin comparison
- `DependencyInjection/AspNetCoreServiceCollectionExtensions.cs`
- `PublicAPI.*.txt`

**`tests/Themia.Messaging.Hmac.Tests/`** — including `Vectors/golden-vectors.json`
**`tests/Themia.Messaging.Http.Tests/`**
**`tests/Themia.Messaging.AspNetCore.Tests/`**

---

### Task 1: The scheme — canonicalizer and signer

**Files:**
- Create: `src/neutral/Themia.Messaging.Hmac/Themia.Messaging.Hmac.csproj`
- Create: `src/neutral/Themia.Messaging.Hmac/ThemiaHmacV1.cs`
- Create: `src/neutral/Themia.Messaging.Hmac/PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Messaging.Hmac.Tests/Themia.Messaging.Hmac.Tests.csproj`
- Test: `tests/Themia.Messaging.Hmac.Tests/Vectors/golden-vectors.json`
- Test: `tests/Themia.Messaging.Hmac.Tests/GoldenVectorTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `public static class ThemiaHmacV1` with `public const string SchemeName = "themia-hmac-v1";`, `public static string Canonicalize(string timestamp, string method, string pathAndQuery, string body)`, `public static string Sign(string canonical, string secret)`, and `public static string FormatTimestamp(DateTimeOffset value)`.

- [ ] **Step 1: Create the project**

Create `src/neutral/Themia.Messaging.Hmac/Themia.Messaging.Hmac.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>Themia.Messaging.Hmac</RootNamespace>
    <PackageId>Themia.Messaging.Hmac</PackageId>
    <Description>Themia messaging HMAC scheme — the themia-hmac-v1 canonicalizer, signer and verifier, plus per-peer key registration. No HTTP or ASP.NET dependency; shared by the sending dispatcher and the receiving filter so both ends compute identical bytes.</Description>
    <PackageTags>themia;messaging;hmac;security;integration</PackageTags>
  </PropertyGroup>
  <ItemGroup>
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
  <ItemGroup>
    <InternalsVisibleTo Include="Themia.Messaging.Hmac.Tests" />
  </ItemGroup>
</Project>
```

```bash
: > src/neutral/Themia.Messaging.Hmac/PublicAPI.Shipped.txt
printf '#nullable enable\n' > src/neutral/Themia.Messaging.Hmac/PublicAPI.Unshipped.txt
dotnet sln Themia.sln add src/neutral/Themia.Messaging.Hmac/Themia.Messaging.Hmac.csproj --solution-folder neutral
```

- [ ] **Step 2: Commit the golden vectors**

Create `tests/Themia.Messaging.Hmac.Tests/Vectors/golden-vectors.json`. These are the exact values propertiezy supplied from their committed fixture, which backs live tests on both sides. **Do not recompute or "correct" any signature** — if one does not match your implementation, your implementation is wrong.

```json
{
  "$comment": "themia-hmac-v1 conformance vectors. Vectors 1-4 are byte-identical to the fixture at propertiezy/docs/contracts/hmac-golden-vector.json and ezy-assets/docs/contracts/hmac-golden-vector.json (verified identical, no drift, coord #0050). Vector 5 is CANDIDATE, generated here and NOT yet confirmed by either peer.",
  "vectors": [
    {
      "name": "put-upsert",
      "status": "confirmed",
      "secret": "test-shared-secret",
      "timestamp": "2026-07-14T09:30:00.0000000Z",
      "method": "PUT",
      "pathAndQuery": "/api/v1/ingest/listings",
      "body": "{\"schemaVersion\":1}",
      "signature": "30ea976ab1615b314c661e623aa145693dc5307aee5e3d46def8195636718176"
    },
    {
      "name": "delete-unpublish-empty-body-with-query",
      "status": "confirmed",
      "$comment": "Empty body is the empty string and its separator newline is RETAINED. Three query params pin as-sent ordering.",
      "secret": "test-shared-secret",
      "timestamp": "2026-07-14T09:30:00.0000000Z",
      "method": "DELETE",
      "pathAndQuery": "/api/v1/ingest/listings?source=EzyAssets&tenantId=1&propertyId=1001",
      "body": "",
      "signature": "d5a3a42c544f6b764ca8c5ee43185f9ec91b7fffbc116eccb875aa17912b3129"
    },
    {
      "name": "entitlement-put",
      "status": "confirmed",
      "secret": "test-shared-secret",
      "timestamp": "2026-07-14T09:30:00.0000000Z",
      "method": "PUT",
      "pathAndQuery": "/api/v1/ingest/entitlements",
      "body": "{\"schemaVersion\":1,\"source\":\"EzyAssets\",\"tenantId\":1,\"activeListingLimit\":20,\"version\":3}",
      "signature": "68252b8a366ff3e86071842be102d3a910c4b56c8d3385fb92ddd008e7270c58"
    },
    {
      "name": "lead-post",
      "status": "confirmed",
      "secret": "test-lead-secret-please-change-32chars!",
      "timestamp": "2026-07-21T09:30:00.0000000Z",
      "method": "POST",
      "pathAndQuery": "/api/v1/leads",
      "body": "{\"schemaVersion\":1,\"leadId\":\"11111111-1111-1111-1111-111111111111\",\"sourcePropertyId\":42,\"agentUserId\":101,\"propertiezyListingId\":7,\"name\":\"Somchai\",\"phone\":\"+66811112222\",\"email\":null,\"message\":null,\"submittedAt\":\"2026-07-21T09:30:00+00:00\"}",
      "signature": "b93c889f649354d0305c04ee7611ddf084051d02d1f0f388ba2cd8c80340118b"
    }
  ]
}
```

- [ ] **Step 3: Write the failing golden-vector test**

Create the test project by copying `tests/Themia.Messaging.Tests/Themia.Messaging.Tests.csproj`, changing the `ProjectReference` to `../../src/neutral/Themia.Messaging.Hmac/Themia.Messaging.Hmac.csproj`, and adding the fixture as content so it is present at runtime:

```xml
  <ItemGroup>
    <None Include="Vectors/golden-vectors.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

Create `tests/Themia.Messaging.Hmac.Tests/GoldenVectorTests.cs`:

```csharp
using System.Text.Json;

using Xunit;

namespace Themia.Messaging.Hmac.Tests;

// These vectors are the interop contract with the live ezy-assets <-> propertiezy link. A failure here
// means Themia would 401 in production, not that the test needs adjusting.
public class GoldenVectorTests
{
    public sealed record Vector(
        string Name, string Status, string Secret, string Timestamp,
        string Method, string PathAndQuery, string Body, string Signature);

    public static TheoryData<Vector> Confirmed()
    {
        var data = new TheoryData<Vector>();
        foreach (var v in Load().Where(v => v.Status == "confirmed"))
        {
            data.Add(v);
        }

        return data;
    }

    private static IReadOnlyList<Vector> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Vectors", "golden-vectors.json");
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("vectors").EnumerateArray()
            .Select(e => new Vector(
                e.GetProperty("name").GetString()!,
                e.GetProperty("status").GetString()!,
                e.GetProperty("secret").GetString()!,
                e.GetProperty("timestamp").GetString()!,
                e.GetProperty("method").GetString()!,
                e.GetProperty("pathAndQuery").GetString()!,
                e.GetProperty("body").GetString()!,
                e.GetProperty("signature").GetString()!))
            .ToList();
    }

    [Theory]
    [MemberData(nameof(Confirmed))]
    public void Sign_ShouldProduceTheExpectedSignature(Vector v)
    {
        var canonical = ThemiaHmacV1.Canonicalize(v.Timestamp, v.Method, v.PathAndQuery, v.Body);

        Assert.Equal(v.Signature, ThemiaHmacV1.Sign(canonical, v.Secret));
    }

    // The fixture must be reachable at runtime; an empty load would make every Theory above vacuous.
    [Fact]
    public void Vectors_ShouldLoad_AndContainTheFourConfirmedCases()
        => Assert.Equal(4, Load().Count(v => v.Status == "confirmed"));

    [Fact]
    public void Canonicalize_ShouldRetainTheSeparatorNewline_ForAnEmptyBody()
    {
        var canonical = ThemiaHmacV1.Canonicalize("2026-07-14T09:30:00.0000000Z", "DELETE", "/x", string.Empty);

        Assert.EndsWith("\n", canonical, StringComparison.Ordinal);
        Assert.Equal(3, canonical.Count(c => c == '\n'));
    }

    [Fact]
    public void Canonicalize_ShouldUpperCaseTheMethod()
        => Assert.Contains("\nPUT\n", ThemiaHmacV1.Canonicalize("t", "put", "/x", "b"), StringComparison.Ordinal);

    [Fact]
    public void Canonicalize_ShouldNeverUseCarriageReturns()
        => Assert.DoesNotContain('\r', ThemiaHmacV1.Canonicalize("t", "PUT", "/x", "b"));

    [Fact]
    public void Sign_ShouldReturnLowercaseHex()
    {
        var signature = ThemiaHmacV1.Sign("canonical", "secret");

        Assert.Equal(64, signature.Length);
        Assert.Equal(signature.ToLowerInvariant(), signature);
    }

    // Round-trip format, not a fixed instant: the vectors already pin the exact shape.
    [Fact]
    public void FormatTimestamp_ShouldRoundTripToTheVectorShape()
    {
        var formatted = ThemiaHmacV1.FormatTimestamp(
            new DateTimeOffset(2026, 7, 14, 9, 30, 0, TimeSpan.Zero));

        Assert.Equal("2026-07-14T09:30:00.0000000Z", formatted);
    }
}
```

- [ ] **Step 4: Run the test to verify it fails**

Run: `dotnet test tests/Themia.Messaging.Hmac.Tests/Themia.Messaging.Hmac.Tests.csproj`
Expected: FAIL — `ThemiaHmacV1` does not exist.

- [ ] **Step 5: Implement the scheme**

Create `src/neutral/Themia.Messaging.Hmac/ThemiaHmacV1.cs`:

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Themia.Messaging.Hmac;

/// <summary>
/// The <c>themia-hmac-v1</c> signing scheme. Deliberately fixed and not configurable: canonicalization is
/// where signature-bypass bugs live, and an adopter-swappable canonical string would let two services
/// running the same framework fail to talk to each other.
/// </summary>
public static class ThemiaHmacV1
{
    /// <summary>The scheme identifier carried in the scheme header.</summary>
    public const string SchemeName = "themia-hmac-v1";

    /// <summary>The timestamp format: ISO-8601 round-trip, seven fractional digits, trailing <c>Z</c>.</summary>
    private const string TimestampFormat = "O";

    /// <summary>
    /// Builds the canonical string that is signed: <c>{timestamp}\n{METHOD}\n{pathAndQuery}\n{body}</c>.
    /// </summary>
    /// <remarks>
    /// An empty body contributes the empty string and its separator newline is RETAINED — the segment is
    /// never omitted. Both ends CONSTRUCT this string rather than parsing it, so a newline inside the body
    /// cannot shift a field boundary.
    /// </remarks>
    /// <param name="timestamp">The timestamp string, byte-identical to the one sent in the header.</param>
    /// <param name="method">The HTTP method; upper-cased here.</param>
    /// <param name="pathAndQuery">The path and query exactly as sent — not re-encoded, decoded or reordered.</param>
    /// <param name="body">The raw body string; empty for a bodyless request.</param>
    /// <returns>The canonical string to sign or verify.</returns>
    public static string Canonicalize(string timestamp, string method, string pathAndQuery, string body)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        ArgumentNullException.ThrowIfNull(body);

        return string.Join('\n', timestamp, method.ToUpperInvariant(), pathAndQuery, body);
    }

    /// <summary>Computes the lowercase-hex HMAC-SHA256 of <paramref name="canonical"/>.</summary>
    /// <remarks>The secret is used as RAW UTF-8 string bytes — never hex- or base64-decoded.</remarks>
    /// <param name="canonical">The canonical string from <see cref="Canonicalize"/>.</param>
    /// <param name="secret">The shared secret, used as UTF-8 bytes.</param>
    /// <returns>The signature as lowercase hexadecimal.</returns>
    public static string Sign(string canonical, string secret)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        ArgumentException.ThrowIfNullOrEmpty(secret);

        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Formats an instant in the scheme's timestamp format.</summary>
    /// <param name="value">The instant to format; converted to UTC.</param>
    /// <returns>The timestamp string for the header and the canonical string.</returns>
    public static string FormatTimestamp(DateTimeOffset value)
        => value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    /// <summary>Parses a timestamp in the scheme's format.</summary>
    /// <param name="value">The header value.</param>
    /// <param name="result">The parsed instant when parsing succeeds.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a well-formed timestamp.</returns>
    public static bool TryParseTimestamp(string? value, out DateTimeOffset result)
        => DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/Themia.Messaging.Hmac.Tests/Themia.Messaging.Hmac.Tests.csproj`
Expected: PASS, including all four golden vectors.

**If a golden vector fails, STOP.** Do not adjust the vector. Compare your canonical string byte-for-byte against the spec's table and report what differs.

- [ ] **Step 7: Declare the public API and commit**

```bash
dotnet build src/neutral/Themia.Messaging.Hmac/Themia.Messaging.Hmac.csproj --no-incremental
```
Append every symbol named by an `RS0016` error to `PublicAPI.Unshipped.txt`; re-run until it succeeds.

```bash
git add src/neutral/Themia.Messaging.Hmac tests/Themia.Messaging.Hmac.Tests Themia.sln
git commit -m "feat(messaging): add themia-hmac-v1 scheme pinned to the golden vectors"
```

---

### Task 2: Peers, keys, headers and the verifier

**Files:**
- Create: `src/neutral/Themia.Messaging.Hmac/HmacHeaderNames.cs`, `MessagingPeer.cs`, `HmacOptions.cs`, `HmacVerificationResult.cs`, `HmacVerifier.cs`
- Create: `src/neutral/Themia.Messaging.Hmac/DependencyInjection/HmacServiceCollectionExtensions.cs`
- Modify: `src/neutral/Themia.Messaging.Hmac/PublicAPI.Unshipped.txt`
- Test: `tests/Themia.Messaging.Hmac.Tests/HmacVerifierTests.cs`

**Interfaces:**
- Consumes: `ThemiaHmacV1` from Task 1.
- Produces: `HmacHeaderNames` (record with `Timestamp`, `Signature`, `KeyId`, `Scheme`, `Origin`, built from a prefix); `MessagingPeer` (`Name`, `HeaderPrefix`, `BaseAddress`, `ClockSkewTolerance`, `MaxBodyBytes`, `OutboundKeyId`, `OutboundSecret`, `InboundKeys` as `IReadOnlyDictionary<string,string>`, `Routes` as `IReadOnlyDictionary<string,string>`, `HeaderNames`); `HmacOptions` with `AddPeer(string, Action<MessagingPeerBuilder>)` and `TryGetPeer`; `HmacVerdict` enum (`Verified`, `StaleTimestamp`, `MalformedTimestamp`, `UnknownKeyId`, `SignatureMismatch`, `UnknownScheme`, `UnknownPeer`); `HmacVerificationResult` (`Verdict`, `MatchedKeyId`, `Skew`); `IHmacVerifier.Verify(MessagingPeer peer, IReadOnlyDictionary<string,string?> headers, string method, string pathAndQuery, string body, DateTimeOffset now)`.

- [ ] **Step 1: Write the failing verifier tests**

Create `tests/Themia.Messaging.Hmac.Tests/HmacVerifierTests.cs`:

```csharp
using Xunit;

namespace Themia.Messaging.Hmac.Tests;

public class HmacVerifierTests
{
    private const string Secret = "test-shared-secret";
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 9, 30, 0, TimeSpan.Zero);

    private static MessagingPeer Peer(
        string prefix = "X-Themia-",
        int toleranceSeconds = 300,
        params (string KeyId, string Secret)[] inbound)
    {
        var options = new HmacOptions();
        options.AddPeer("peer", p =>
        {
            p.HeaderPrefix = prefix;
            p.ClockSkewTolerance = TimeSpan.FromSeconds(toleranceSeconds);
            p.SignWith("out-1", Secret);
            foreach (var (keyId, secret) in inbound.DefaultIfEmpty(("in-1", Secret)))
            {
                p.Accept(keyId, secret);
            }
        });

        Assert.True(options.TryGetPeer("peer", out var peer));
        return peer!;
    }

    private static Dictionary<string, string?> Headers(
        MessagingPeer peer, DateTimeOffset stamp, string body, string secret,
        string? keyId = "in-1", string? scheme = ThemiaHmacV1.SchemeName, string? origin = null,
        string method = "PUT", string path = "/api/v1/ingest/listings")
    {
        var timestamp = ThemiaHmacV1.FormatTimestamp(stamp);
        var signature = ThemiaHmacV1.Sign(ThemiaHmacV1.Canonicalize(timestamp, method, path, body), secret);
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [peer.HeaderNames.Timestamp] = timestamp,
            [peer.HeaderNames.Signature] = signature,
        };
        if (keyId is not null) headers[peer.HeaderNames.KeyId] = keyId;
        if (scheme is not null) headers[peer.HeaderNames.Scheme] = scheme;
        if (origin is not null) headers[peer.HeaderNames.Origin] = origin;
        return headers;
    }

    private static HmacVerificationResult Verify(
        MessagingPeer peer, Dictionary<string, string?> headers, string body = "{}",
        string method = "PUT", string path = "/api/v1/ingest/listings", DateTimeOffset? now = null)
        => new HmacVerifier().Verify(peer, headers, method, path, body, now ?? Now);

    [Fact]
    public void Verify_ShouldSucceed_ForAWellFormedRequest()
    {
        var peer = Peer();

        Assert.Equal(HmacVerdict.Verified, Verify(peer, Headers(peer, Now, "{}", Secret)).Verdict);
    }

    // The live link sends ONLY timestamp and signature — no key-id, no scheme, no origin. Requiring any of
    // them would reject the entire existing integration on its first request.
    [Fact]
    public void Verify_ShouldSucceed_WithOnlyTimestampAndSignature()
    {
        var peer = Peer();
        var headers = Headers(peer, Now, "{}", Secret, keyId: null, scheme: null, origin: null);

        Assert.Equal(HmacVerdict.Verified, Verify(peer, headers).Verdict);
    }

    [Fact]
    public void Verify_ShouldTryEveryInboundKey_WhenKeyIdIsAbsent()
    {
        var peer = Peer(inbound: [("in-1", "wrong-secret"), ("in-2", Secret)]);
        var headers = Headers(peer, Now, "{}", Secret, keyId: null);

        var result = Verify(peer, headers);

        Assert.Equal(HmacVerdict.Verified, result.Verdict);
        Assert.Equal("in-2", result.MatchedKeyId);
    }

    // Absence must mean v1 specifically, never "newest" — otherwise adding v2 later silently
    // reinterprets every legacy request that predates the header.
    [Fact]
    public void Verify_ShouldAssumeV1_WhenSchemeHeaderIsAbsent()
    {
        var peer = Peer();

        Assert.Equal(HmacVerdict.Verified, Verify(peer, Headers(peer, Now, "{}", Secret, scheme: null)).Verdict);
    }

    [Fact]
    public void Verify_ShouldReturnUnknownScheme_WhenSchemeHeaderIsUnrecognised()
    {
        var peer = Peer();

        Assert.Equal(
            HmacVerdict.UnknownScheme,
            Verify(peer, Headers(peer, Now, "{}", Secret, scheme: "themia-hmac-v2")).Verdict);
    }

    // A clock problem is infrastructure and self-heals; it must be distinguishable from a bad signature so
    // the sender can retry rather than dead-letter. This verdict maps to 408, not 401.
    [Theory]
    [InlineData(-600)]
    [InlineData(600)]
    public void Verify_ShouldReturnStaleTimestamp_WhenOutsideTheWindow(int offsetSeconds)
    {
        var peer = Peer();
        var headers = Headers(peer, Now.AddSeconds(offsetSeconds), "{}", Secret);

        Assert.Equal(HmacVerdict.StaleTimestamp, Verify(peer, headers).Verdict);
    }

    [Theory]
    [InlineData(-299)]
    [InlineData(299)]
    public void Verify_ShouldSucceed_JustInsideTheWindow(int offsetSeconds)
    {
        var peer = Peer();
        var headers = Headers(peer, Now.AddSeconds(offsetSeconds), "{}", Secret);

        Assert.Equal(HmacVerdict.Verified, Verify(peer, headers).Verdict);
    }

    // Malformed input never becomes valid by retrying, so it is NOT stale — it maps to 401.
    [Fact]
    public void Verify_ShouldReturnMalformedTimestamp_WhenUnparseable()
    {
        var peer = Peer();
        var headers = Headers(peer, Now, "{}", Secret);
        headers[peer.HeaderNames.Timestamp] = "not-a-timestamp";

        Assert.Equal(HmacVerdict.MalformedTimestamp, Verify(peer, headers).Verdict);
    }

    [Fact]
    public void Verify_ShouldReturnMalformedTimestamp_WhenHeaderIsMissing()
    {
        var peer = Peer();
        var headers = Headers(peer, Now, "{}", Secret);
        headers.Remove(peer.HeaderNames.Timestamp);

        Assert.Equal(HmacVerdict.MalformedTimestamp, Verify(peer, headers).Verdict);
    }

    [Fact]
    public void Verify_ShouldReturnUnknownKeyId_WhenKeyIdIsPresentButUnconfigured()
    {
        var peer = Peer();

        Assert.Equal(
            HmacVerdict.UnknownKeyId,
            Verify(peer, Headers(peer, Now, "{}", Secret, keyId: "nope")).Verdict);
    }

    [Fact]
    public void Verify_ShouldReturnSignatureMismatch_WhenTheBodyIsTampered()
    {
        var peer = Peer();
        var headers = Headers(peer, Now, "{}", Secret);

        Assert.Equal(HmacVerdict.SignatureMismatch, Verify(peer, headers, body: "{\"evil\":1}").Verdict);
    }

    [Fact]
    public void Verify_ShouldReturnSignatureMismatch_WhenThePathIsTampered()
    {
        var peer = Peer();
        var headers = Headers(peer, Now, "{}", Secret);

        Assert.Equal(HmacVerdict.SignatureMismatch, Verify(peer, headers, path: "/api/v1/ingest/other").Verdict);
    }

    // Query order is signed as-sent, so reordering must break the signature.
    [Fact]
    public void Verify_ShouldReturnSignatureMismatch_WhenTheQueryIsReordered()
    {
        var peer = Peer();
        var headers = Headers(peer, Now, "", Secret, method: "DELETE", path: "/x?a=1&b=2");

        var result = Verify(peer, headers, body: "", method: "DELETE", path: "/x?b=2&a=1");

        Assert.Equal(HmacVerdict.SignatureMismatch, result.Verdict);
    }

    [Fact]
    public void Verify_ShouldReturnSignatureMismatch_WhenTheSecretIsWrong()
    {
        var peer = Peer();

        Assert.Equal(
            HmacVerdict.SignatureMismatch,
            Verify(peer, Headers(peer, Now, "{}", "different-secret")).Verdict);
    }

    [Fact]
    public void Verify_ShouldHonourACustomHeaderPrefix()
    {
        var peer = Peer(prefix: "X-Propertiezy-");

        Assert.Equal("X-Propertiezy-Signature", peer.HeaderNames.Signature);
        Assert.Equal(HmacVerdict.Verified, Verify(peer, Headers(peer, Now, "{}", Secret)).Verdict);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Themia.Messaging.Hmac.Tests/Themia.Messaging.Hmac.Tests.csproj --filter HmacVerifierTests`
Expected: FAIL — none of `MessagingPeer`, `HmacOptions`, `HmacVerifier` exist.

- [ ] **Step 3: Implement headers, peers and options**

`HmacHeaderNames.cs` — a record computing the five names from a prefix:

```csharp
namespace Themia.Messaging.Hmac;

/// <summary>The five wire header names, derived from a per-peer prefix.</summary>
/// <remarks>
/// Header names are NOT part of the canonical string, so a mismatch can only cause a failure to verify,
/// never a bypass — which is why the prefix is safe to make configurable where canonicalization is not.
/// It exists because the live ezy-assets/propertiezy link sends <c>X-Propertiezy-*</c>: with a different
/// prefix a receiver looks for a header that is not there and rejects a perfectly valid signature.
/// </remarks>
/// <param name="Prefix">The header prefix, e.g. <c>X-Themia-</c>.</param>
public sealed record HmacHeaderNames(string Prefix)
{
    /// <summary>The default prefix used when a peer does not override it.</summary>
    public const string DefaultPrefix = "X-Themia-";

    /// <summary>Header carrying the signed timestamp. Required.</summary>
    public string Timestamp { get; } = Prefix + "Timestamp";

    /// <summary>Header carrying the lowercase-hex signature. Required.</summary>
    public string Signature { get; } = Prefix + "Signature";

    /// <summary>Header selecting which inbound key verifies. Optional.</summary>
    public string KeyId { get; } = Prefix + "Key-Id";

    /// <summary>Header naming the signing scheme. Optional; absence means <c>themia-hmac-v1</c>.</summary>
    public string Scheme { get; } = Prefix + "Scheme";

    /// <summary>Header naming the originating system, for the loop guard. Optional.</summary>
    public string Origin { get; } = Prefix + "Origin";
}
```

Write `MessagingPeer`, `MessagingPeerBuilder` and `HmacOptions` to satisfy the test's usage: `AddPeer(name, configure)`, builder members `HeaderPrefix`, `BaseAddress`, `ClockSkewTolerance` (default 5 minutes), `MaxBodyBytes` (default 4 MB), `SignWith(keyId, secret)`, `Accept(keyId, secret)`, `Route(type, path)`, and `HmacOptions.TryGetPeer(name, out peer)`. Validate on build: name non-blank, outbound key set, at least one inbound key, tolerance positive.

- [ ] **Step 4: Implement the verifier**

Create `HmacVerificationResult.cs` and `HmacVerifier.cs`. The verdict order is fixed and load-bearing — scheme, then timestamp, then key, then signature:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Themia.Messaging.Hmac;

/// <summary>Verifies an inbound request against a peer's configured keys.</summary>
public sealed class HmacVerifier : IHmacVerifier
{
    /// <inheritdoc />
    public HmacVerificationResult Verify(
        MessagingPeer peer,
        IReadOnlyDictionary<string, string?> headers,
        string method,
        string pathAndQuery,
        string body,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(headers);

        var names = peer.HeaderNames;

        // Absence means v1 specifically, never "the newest scheme" — otherwise a future v2 would silently
        // reinterpret legacy traffic that predates the header.
        if (headers.TryGetValue(names.Scheme, out var scheme)
            && !string.IsNullOrEmpty(scheme)
            && !string.Equals(scheme, ThemiaHmacV1.SchemeName, StringComparison.Ordinal))
        {
            return HmacVerificationResult.UnknownScheme();
        }

        headers.TryGetValue(names.Timestamp, out var timestampHeader);
        if (!ThemiaHmacV1.TryParseTimestamp(timestampHeader, out var sentAt))
        {
            // Malformed, not stale: it will never become valid by retrying.
            return HmacVerificationResult.MalformedTimestamp();
        }

        var skew = now - sentAt;
        if (skew.Duration() > peer.ClockSkewTolerance)
        {
            return HmacVerificationResult.StaleTimestamp(skew);
        }

        var candidates = ResolveCandidateKeys(peer, headers, names);
        if (candidates.Count == 0)
        {
            return HmacVerificationResult.UnknownKeyId();
        }

        headers.TryGetValue(names.Signature, out var presented);
        if (string.IsNullOrEmpty(presented))
        {
            return HmacVerificationResult.SignatureMismatch();
        }

        var canonical = ThemiaHmacV1.Canonicalize(timestampHeader!, method, pathAndQuery, body);
        foreach (var (keyId, secret) in candidates)
        {
            var expected = ThemiaHmacV1.Sign(canonical, secret);
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(presented)))
            {
                return HmacVerificationResult.Verified(keyId);
            }
        }

        return HmacVerificationResult.SignatureMismatch();
    }

    // No key-id header means try every configured inbound key. The live link sends no key-id at all, so
    // requiring one would reject the entire existing integration.
    private static IReadOnlyList<KeyValuePair<string, string>> ResolveCandidateKeys(
        MessagingPeer peer, IReadOnlyDictionary<string, string?> headers, HmacHeaderNames names)
    {
        if (headers.TryGetValue(names.KeyId, out var keyId) && !string.IsNullOrEmpty(keyId))
        {
            return peer.InboundKeys.TryGetValue(keyId, out var secret)
                ? [new KeyValuePair<string, string>(keyId, secret)]
                : [];
        }

        return peer.InboundKeys.ToArray();
    }
}
```

Add `IHmacVerifier` with the same signature, and `AddThemiaMessagingHmac(this IServiceCollection, Action<HmacOptions>)` registering the validated `HmacOptions` and `IHmacVerifier` as singletons.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/Themia.Messaging.Hmac.Tests/Themia.Messaging.Hmac.Tests.csproj`
Expected: PASS — Task 1's vectors plus all verifier cases.

- [ ] **Step 6: Declare the public API and commit**

```bash
dotnet build src/neutral/Themia.Messaging.Hmac/Themia.Messaging.Hmac.csproj --no-incremental
git add src/neutral/Themia.Messaging.Hmac tests/Themia.Messaging.Hmac.Tests
git commit -m "feat(messaging): add peer registry, key rotation and the HMAC verifier"
```

---

### Task 3: The candidate fifth vector

**Files:**
- Modify: `tests/Themia.Messaging.Hmac.Tests/Vectors/golden-vectors.json`
- Test: `tests/Themia.Messaging.Hmac.Tests/CandidateVectorTests.cs`

**Interfaces:**
- Consumes: `ThemiaHmacV1` from Task 1.
- Produces: a fifth vector entry with `"status": "candidate"`.

- [ ] **Step 1: Generate the signature**

The body must contain **Thai script and a newline**, at propertiezy's suggestion — Thai visitor names are the real traffic on the lead channel, and `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` puts them on the wire as raw UTF-8 rather than `\uXXXX` escapes. That encoding path is currently unpinned on both consumer sides.

Write a throwaway xUnit fact that prints the signature for this input, run it, and copy the value:

- secret: `test-shared-secret`
- timestamp: `2026-07-21T09:30:00.0000000Z`
- method: `POST`
- pathAndQuery: `/api/v1/leads`
- body: `{"name":"สมชาย ใจดี","message":"บรรทัดแรก\nบรรทัดที่สอง"}`

Note the body's `\n` is a **literal newline character inside the JSON string value**, not an escape sequence in the file. In the fixture JSON it must be written as `\n` inside the quoted string so it deserializes to one newline character. Verify after loading that the body contains exactly one `\n`.

- [ ] **Step 2: Add it to the fixture**

Append to the `vectors` array, marked `"status": "candidate"` with a comment stating it is unconfirmed:

```json
    {
      "name": "lead-post-thai-multiline-body",
      "status": "candidate",
      "$comment": "CANDIDATE — generated by Themia, NOT yet confirmed by propertiezy or ezy-assets. Pins UTF-8 encoding of the canonical string and a literal newline inside the body. Promote to confirmed only when both peers verify it against their implementations (coord #0050).",
      "secret": "test-shared-secret",
      "timestamp": "2026-07-21T09:30:00.0000000Z",
      "method": "POST",
      "pathAndQuery": "/api/v1/leads",
      "body": "{\"name\":\"สมชาย ใจดี\",\"message\":\"บรรทัดแรก\\nบรรทัดที่สอง\"}",
      "signature": "<paste from Step 1>"
    }
```

- [ ] **Step 3: Test it, separately from the confirmed set**

Create `CandidateVectorTests.cs` asserting the candidate signs to its recorded signature, and that the body round-trips with a literal newline and non-ASCII characters intact. Keep it in its own class so a candidate failure is never mistaken for a conformance failure.

`GoldenVectorTests.Confirmed()` filters on `status == "confirmed"`, so the candidate must not leak into the conformance theory — the existing count assertion (`Assert.Equal(4, ...)`) guards this and must still pass.

- [ ] **Step 4: Run, then commit**

```bash
dotnet test tests/Themia.Messaging.Hmac.Tests/Themia.Messaging.Hmac.Tests.csproj
git add tests/Themia.Messaging.Hmac.Tests
git commit -m "test(messaging): add candidate Thai multiline HMAC vector for peer confirmation"
```

---

### Task 4: HTTP dispatcher

**Files:**
- Create: `src/neutral/Themia.Messaging.Http/Themia.Messaging.Http.csproj`, `HttpStatusClassifier.cs`, `HttpMessageDispatcher.cs`, `DependencyInjection/HttpServiceCollectionExtensions.cs`, `PublicAPI.*.txt`
- Test: `tests/Themia.Messaging.Http.Tests/` — project, `HttpStatusClassifierTests.cs`, `HttpMessageDispatcherTests.cs`

**Interfaces:**
- Consumes: `IOutboxDispatcher<ClaimedMessageRow>`, `DispatchResult` (`Delivered()`, `Transient(string)`, `Transient(string, Exception)`, `Permanent(string)`, `Permanent(string, Exception)`), `ClaimedMessageRow(Id, MessageId, TenantId, Type, Payload, Destination, Origin, EntityKey, Version, Headers, Attempts)`, `HmacOptions`, `MessagingPeer`, `ThemiaHmacV1`.
- Produces: `HttpMessageDispatcher : IOutboxDispatcher<ClaimedMessageRow>`; `HttpStatusClassifier.Classify(int status) -> DispatchOutcome`; `AddThemiaMessagingHttp(this IServiceCollection)`.

- [ ] **Step 1: Write the failing classifier test**

The classification table is normative — 408 must be transient or a clock-skew rejection dead-letters the queue.

```csharp
using Themia.Messaging.Outbox;

using Xunit;

namespace Themia.Messaging.Http.Tests;

public class HttpStatusClassifierTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(204)]
    public void Classify_ShouldBeDelivered_For2xx(int status)
        => Assert.Equal(DispatchOutcome.Delivered, HttpStatusClassifier.Classify(status));

    // 408 is the scheme's stale-timestamp status. Classifying it permanent would dead-letter every
    // message a clock-drifted sender produces — the exact failure themia-hmac-v1 exists to prevent.
    [Theory]
    [InlineData(408)]
    [InlineData(425)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void Classify_ShouldBeTransient_ForRetryableStatuses(int status)
        => Assert.Equal(DispatchOutcome.Transient, HttpStatusClassifier.Classify(status));

    // Retrying an identical signature fails identically, so auth failures must surface at once.
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(413)]
    [InlineData(422)]
    public void Classify_ShouldBePermanent_ForClientErrors(int status)
        => Assert.Equal(DispatchOutcome.Permanent, HttpStatusClassifier.Classify(status));

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    public void Classify_ShouldBePermanent_ForRedirects(int status)
        => Assert.Equal(DispatchOutcome.Permanent, HttpStatusClassifier.Classify(status));
}
```

- [ ] **Step 2: Run it, confirm it fails, then implement**

Create the project mirroring `Themia.Messaging.Hmac.csproj`, adding `ProjectReference`s to `Themia.Messaging` and `Themia.Messaging.Hmac` plus a `PackageReference` to `Microsoft.Extensions.Http` (check `Directory.Packages.props` for an existing `PackageVersion`; add one if missing).

```csharp
namespace Themia.Messaging.Http;

/// <summary>Maps an HTTP status onto a delivery outcome.</summary>
public static class HttpStatusClassifier
{
    /// <summary>Classifies a response status.</summary>
    /// <remarks>
    /// 408 is transient because it is the scheme's stale-timestamp status: a clock problem is
    /// infrastructure, self-heals when the clock corrects, and must retry. 401 is permanent because
    /// retrying an identical signature fails identically.
    /// </remarks>
    /// <param name="status">The HTTP status code.</param>
    /// <returns>The outcome the drainer should record.</returns>
    public static DispatchOutcome Classify(int status) => status switch
    {
        >= 200 and < 300 => DispatchOutcome.Delivered,
        408 or 425 or 429 => DispatchOutcome.Transient,
        >= 500 => DispatchOutcome.Transient,
        _ => DispatchOutcome.Permanent,
    };
}
```

- [ ] **Step 3: Write the failing dispatcher tests**

Cover, using a stubbed `HttpMessageHandler` that captures the outgoing request:
1. Signs with the peer's **outbound** key and sends timestamp, signature, key-id, scheme and origin headers under the peer's prefix.
2. Sends `Payload` **verbatim** — assert the request body is byte-identical to the row's payload, since re-serializing would invalidate the signature.
3. Resolves the URL from the peer's `BaseAddress` plus the `Type`'s configured route.
4. An unroutable `Type` returns `Permanent` **without** sending a request.
5. An unknown `Destination` returns `Permanent` without sending.
6. A 2xx returns `Delivered`; a 408 returns `Transient`; a 401 returns `Permanent`.
7. An `HttpRequestException` returns `Transient` carrying the exception.
8. A `TaskCanceledException` from a timeout returns `Transient`; an `OperationCanceledException` on the caller's token **propagates** rather than being swallowed.
9. Envelope `Headers` JSON is merged onto the request without overwriting any signature header.
10. Neither the secret nor the signature appears in any log message.

- [ ] **Step 4: Implement the dispatcher**

Resolve the peer from `row.Destination` and the path from its routes. Build the request, set `Content` from `row.Payload` with the peer's configured media type (default `application/json`), compute `pathAndQuery` from the **request URI's** `PathAndQuery` so the signed value is exactly what is sent, sign, attach headers, send, classify.

Let `OperationCanceledException` propagate when the caller's token is cancelled; treat a timeout as `Transient`.

- [ ] **Step 5: Run, declare API, commit**

```bash
dotnet test tests/Themia.Messaging.Http.Tests/Themia.Messaging.Http.Tests.csproj
dotnet build src/neutral/Themia.Messaging.Http/Themia.Messaging.Http.csproj --no-incremental
git add src/neutral/Themia.Messaging.Http tests/Themia.Messaging.Http.Tests Themia.sln
git commit -m "feat(messaging): add HTTP dispatcher signing with themia-hmac-v1"
```

---

### Task 5: ASP.NET verification filter and loop guard

**Files:**
- Create: `src/neutral/Themia.Messaging.AspNetCore/Themia.Messaging.AspNetCore.csproj`, `HmacVerificationFilter.cs`, `LoopGuard.cs`, `DependencyInjection/AspNetCoreServiceCollectionExtensions.cs`, `PublicAPI.*.txt`
- Test: `tests/Themia.Messaging.AspNetCore.Tests/` — project plus `HmacVerificationFilterTests.cs`

**Interfaces:**
- Consumes: `IHmacVerifier`, `HmacOptions`, `MessagingPeer`, `HmacVerdict`, `ThemiaHmacV1`.
- Produces: `HmacVerificationFilter : IEndpointFilter`; `AddThemiaMessagingVerification(this IServiceCollection, Action<VerificationOptions>?)`; `RequireThemiaHmac(this RouteHandlerBuilder)`.

- [ ] **Step 1: Write the failing filter tests**

Use `WebApplicationFactory` or a `DefaultHttpContext`-driven filter invocation. Assert the **status mapping**, which is normative:

| Case | Expected |
|---|---|
| Valid request | endpoint runs, 2xx |
| Body over `MaxBodyBytes` | **413**, endpoint not run, body never hashed |
| Scheme header present and unrecognised | **400** |
| Scheme header absent | endpoint runs |
| Timestamp missing or unparseable | **401** |
| Timestamp outside window | **408** |
| Key-id present but unknown | **401** |
| Signature mismatch | **401** |
| Only timestamp + signature sent | endpoint runs |
| `Origin` equals this service's origin | **200**, endpoint **not** run |
| `Origin` differs | endpoint runs |
| `Origin` absent | endpoint runs, no loop guard |

Two further assertions:
- A 408 response logs the observed skew and the configured tolerance, so an operator can separate a clock problem from an attack in one line.
- No log message contains the secret or the signature.

- [ ] **Step 2: Implement the filter**

Order is fixed: size → buffer → scheme → timestamp → key → signature → loop guard. Read the body with `EnableBuffering` and rewind so the endpoint can read it again.

The loop guard runs **last, after verification**, because `Origin` is attacker-controlled until the signature is checked — trusting it earlier would let anyone short-circuit an ingest endpoint by claiming to be its owner. On a match, return **200** and do not invoke the endpoint: the message has come home, and a non-2xx would make the sender retry something it can never deliver.

- [ ] **Step 3: Warn at startup when the loop guard cannot run**

`AddThemiaMessagingVerification` logs a warning naming any peer marked `BiDirectional` whose requests carry no `Origin` header, since loop protection on that channel is **absent**, not merely degraded. Add a test asserting the warning fires.

- [ ] **Step 4: Run, declare API, commit**

```bash
dotnet test tests/Themia.Messaging.AspNetCore.Tests/Themia.Messaging.AspNetCore.Tests.csproj
dotnet build Themia.sln --no-incremental
git add src/neutral/Themia.Messaging.AspNetCore tests/Themia.Messaging.AspNetCore.Tests Themia.sln
git commit -m "feat(messaging): add HMAC verification filter and loop guard"
```

---

### Task 6: End-to-end round trip

**Files:**
- Test: `tests/Themia.Messaging.AspNetCore.Tests/RoundTripTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-5.

- [ ] **Step 1: Write the round-trip tests**

Host the verification filter in a `WebApplicationFactory`, point a real `HttpMessageDispatcher` at it through the factory's `HttpClient`, and assert:

1. A dispatched row is **verified and delivered** — the signature the dispatcher produced is the one the filter accepts. This is the only test that proves both halves agree; every other test exercises one side against its own expectations.
2. A row whose peer prefix is `X-Propertiezy-` still round-trips, proving the legacy prefix path works end to end.
3. A dispatcher signing with the wrong secret is rejected **401** and classified `Permanent`.
4. A dispatcher whose clock is 10 minutes fast is rejected **408** and classified `Transient` — the full clock-skew path, from bad clock to retryable outcome.
5. A message whose `Origin` matches the receiver's own origin is answered **200** without the endpoint running, and the dispatcher records `Delivered`.

- [ ] **Step 2: Run the full suite and commit**

```bash
dotnet build Themia.sln --no-incremental
dotnet test Themia.sln
git add tests/Themia.Messaging.AspNetCore.Tests
git commit -m "test(messaging): add HMAC transport end-to-end round trip"
```

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| `themia-hmac-v1` canonical string and signature | 1 |
| Golden vectors as a committed, runtime-read fixture | 1 |
| Normative rejection statuses | 2 (verdicts), 4 (classifier), 5 (status mapping) |
| Header names, configurable prefix | 2 |
| Only two headers mandatory; `Key-Id`/`Scheme`/`Origin` optional | 2, 5 |
| Absent `Scheme` means v1, never "newest" | 2 |
| Per-peer, per-direction keys; inbound key set for rotation | 2 |
| Fifth candidate vector, Thai script | 3 |
| HTTP dispatcher, verbatim payload, no retry layer | 4 |
| `Retry-After` explicitly not honoured | 4 (absent by construction) |
| Verify filter ordering, body size limit, `EnableBuffering` | 5 |
| Loop guard last, 200 on match | 5 |
| Loop guard unavailable on legacy channels; startup warning | 5 |
| Both halves agree end to end | 6 |

**Deviation from the spec, recorded deliberately:** the spec describes `MaxBodyBytes` rejection as step 1 "before reading or hashing anything". In ASP.NET the check is `Request.ContentLength` where present, falling back to the `bufferLimit` passed to `EnableBuffering` for chunked requests without a declared length — a chunked body cannot be sized before reading. Task 5 must implement both, and the test for the 413 path should cover a declared `Content-Length` case; the chunked case is bounded by `bufferLimit` throwing rather than by an explicit check.

**Type consistency:** `DispatchOutcome` members are `Delivered`/`Transient`/`Permanent`, matching the merged enum. `ClaimedMessageRow`'s positional order in Task 4 matches the merged record, including `Headers` between `Version` and `Attempts`. `HmacVerdict` members are identical across Tasks 2, 4 and 5.

**Known gap requiring judgment during execution:** Task 3 Step 1 requires generating a signature and pasting it, which cannot be pre-computed here. The instructions say explicitly to derive it from the implementation rather than invent it, and to verify the body's newline survives round-tripping.
