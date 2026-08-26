using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.Extensions.Options;
using Themia.WebAuthn;
using Xunit;

namespace Themia.WebAuthn.Tests;

/// <summary>
/// The challenge must be usable exactly once. The library verifies a response against the options it
/// was issued with but stores nothing, so an integration that keeps those options anywhere reusable
/// accepts the same signed response twice — and both sign-ins succeed, which is why nobody notices.
/// </summary>
public sealed class ChallengeGuardTests
{
    private const string Origin = "https://localhost";

    /// <summary>Atomic get-and-delete, as a real store must be.</summary>
    private sealed class RecordingChallengeStore : IWebAuthnChallengeStore
    {
        private readonly Dictionary<string, string> _open = [];

        public List<string> Consumed { get; } = [];

        public ValueTask StoreAsync(string challengeId, string optionsJson, TimeSpan ttl, CancellationToken ct = default)
        {
            _open[challengeId] = optionsJson;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> TryConsumeAsync(string challengeId, CancellationToken ct = default)
        {
            Consumed.Add(challengeId);
            if (!_open.Remove(challengeId, out var options))
            {
                return ValueTask.FromResult<string?>(null);
            }

            return ValueTask.FromResult<string?>(options);
        }
    }

    private static (WebAuthnService Service, RecordingChallengeStore Store) Build()
    {
        var options = new WebAuthnOptions
        {
            ServerDomain = "localhost",
            ServerName = "Themia",
            Origins = new HashSet<string>(StringComparer.Ordinal) { Origin },
        };

        var fido2 = new Fido2(new Fido2Configuration
        {
            ServerDomain = options.ServerDomain,
            ServerName = options.ServerName,
            Origins = options.Origins,
        });

        var store = new RecordingChallengeStore();
        return (new WebAuthnService(fido2, store, Options.Create(options)), store);
    }

    [Fact]
    public async Task Beginning_a_ceremony_stores_it_under_its_challenge()
    {
        var (service, store) = Build();

        var options = await service.BeginAuthenticationAsync([]);

        // Consuming it once returns the ceremony; the second time it is gone.
        var challenge = Base64Url.EncodeToString(options.Challenge);
        Assert.NotNull(await store.TryConsumeAsync(challenge));
        Assert.Null(await store.TryConsumeAsync(challenge));
    }

    [Fact]
    public async Task A_response_whose_challenge_was_never_issued_is_refused_before_any_verification()
    {
        var (service, store) = Build();

        var response = BuildAssertionResponse(RandomNumberGenerator.GetBytes(32), credentialId: [1, 2, 3]);

        var result = await service.CompleteAuthenticationAsync(
            response,
            new StoredWebAuthnCredential([1, 2, 3], [0], SignCount: 0, UserHandle: [9]));

        Assert.Equal(WebAuthnOutcome.ChallengeNotFound, result.Outcome);
        // The store was asked, and nothing beyond it ran: a forged challenge never reaches the crypto.
        Assert.Single(store.Consumed);
    }

    [Fact]
    public async Task A_second_submission_of_the_same_response_finds_no_open_ceremony()
    {
        var (service, _) = Build();

        var options = await service.BeginAuthenticationAsync([]);
        var response = BuildAssertionResponse(options.Challenge, credentialId: [1, 2, 3]);
        var stored = new StoredWebAuthnCredential([1, 2, 3], [0], SignCount: 0, UserHandle: [9]);

        // The first attempt fails verification (this response is not really signed) — but it must still
        // consume the ceremony, or a valid response could be replayed after a failed one.
        var first = await service.CompleteAuthenticationAsync(response, stored);
        var second = await service.CompleteAuthenticationAsync(response, stored);

        Assert.NotEqual(WebAuthnOutcome.ChallengeNotFound, first.Outcome);
        Assert.Equal(WebAuthnOutcome.ChallengeNotFound, second.Outcome);
    }

    private static AuthenticatorAssertionRawResponse BuildAssertionResponse(byte[] challenge, byte[] credentialId)
    {
        var clientData = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "webauthn.get",
            challenge = Base64Url.EncodeToString(challenge),
            origin = Origin,
        });

        return new AuthenticatorAssertionRawResponse
        {
            Id = Base64Url.EncodeToString(credentialId),
            RawId = credentialId,
            Type = PublicKeyCredentialType.PublicKey,
            Response = new AuthenticatorAssertionRawResponse.AssertionResponse
            {
                AuthenticatorData = new byte[37],
                ClientDataJson = clientData,
                Signature = [0],
                UserHandle = [9],
            },
        };
    }
}
