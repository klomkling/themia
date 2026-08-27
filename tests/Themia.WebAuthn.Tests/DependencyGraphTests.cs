using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Xunit;

namespace Themia.WebAuthn.Tests;

/// <summary>
/// Proves Fido2NetLib 4.0.1 actually WORKS under this repository's pinned dependency set, rather than
/// merely restoring against it.
/// <para>
/// Fido2 4.0.1 was built against <c>Microsoft.IdentityModel.JsonWebTokens 8.2.0</c> and
/// <c>Microsoft.Extensions.Http 9.0.0</c>. This repository pins 8.19.1 and 10.0.9, and central package
/// management with transitive pinning raises Fido2's transitive references to those. Restore succeeds
/// silently — which is exactly what coord #0085 showed proves nothing: a package resolved to a version
/// it was not built against fails at runtime, on a green build.
/// </para>
/// <para>
/// So this drives a full assertion ceremony with a real ES256 key pair and a real signature, through
/// the library's own verification path.
/// </para>
/// </summary>
public sealed class DependencyGraphTests
{
    private const string Origin = "https://localhost";
    private const string RpId = "localhost";

    private static Fido2 CreateFido2() => new(new Fido2Configuration
    {
        ServerDomain = RpId,
        ServerName = "Themia",
        Origins = new HashSet<string>(StringComparer.Ordinal) { Origin },
    });

    [Fact]
    public void Registration_options_are_produced_under_the_pinned_graph()
    {
        var fido2 = CreateFido2();

        var options = fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = "user-1"u8.ToArray(), Name = "someone", DisplayName = "Someone" },
            ExcludeCredentials = [],
        });

        Assert.NotNull(options.Challenge);
        Assert.NotEmpty(options.Challenge);
        Assert.Equal(RpId, options.Rp.Id);
    }

    [Fact]
    public async Task A_real_ES256_signature_verifies_under_the_pinned_graph()
    {
        // The decisive check: this exercises COSE key parsing and ECDSA verification inside Fido2,
        // the paths a mis-resolved dependency would break.
        var fido2 = CreateFido2();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var credentialId = RandomNumberGenerator.GetBytes(32);
        var userHandle = "user-1"u8.ToArray();

        var assertionOptions = fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [new PublicKeyCredentialDescriptor(credentialId)],
            UserVerification = UserVerificationRequirement.Discouraged,
        });

        var clientData = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "webauthn.get",
            challenge = Base64Url.EncodeToString(assertionOptions.Challenge),
            origin = Origin,
        });

        var authenticatorData = BuildAuthenticatorData(signCount: 1);
        var signature = SignAssertion(key, authenticatorData, clientData);

        var result = await fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = new AuthenticatorAssertionRawResponse
            {
                Id = Base64Url.EncodeToString(credentialId),
                RawId = credentialId,
                Type = PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAssertionRawResponse.AssertionResponse
                {
                    AuthenticatorData = authenticatorData,
                    ClientDataJson = clientData,
                    Signature = signature,
                    UserHandle = userHandle,
                },
            },
            OriginalOptions = assertionOptions,
            StoredPublicKey = CoseEs256(key),
            StoredSignatureCounter = 0,
            IsUserHandleOwnerOfCredentialIdCallback = (_, _) => Task.FromResult(true),
        });

        Assert.Equal(1u, result.SignCount);
    }

    /// <summary>rpIdHash ‖ flags ‖ signCount — the minimum an assertion carries (WebAuthn §6.1).</summary>
    private static byte[] BuildAuthenticatorData(uint signCount)
    {
        var data = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(RpId)).CopyTo(data, 0);
        data[32] = 0x01; // UP: user present
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(33), signCount);
        return data;
    }

    private static byte[] SignAssertion(ECDsa key, byte[] authenticatorData, byte[] clientData)
    {
        byte[] payload = [.. authenticatorData, .. SHA256.HashData(clientData)];
        return key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// <summary>The public key as a COSE_Key, which is the shape a relying party stores.</summary>
    private static byte[] CoseEs256(ECDsa key)
    {
        var p = key.ExportParameters(includePrivateParameters: false);

        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(5);
        writer.WriteInt32(1); writer.WriteInt32(2);     // kty: EC2
        writer.WriteInt32(3); writer.WriteInt32(-7);    // alg: ES256
        writer.WriteInt32(-1); writer.WriteInt32(1);    // crv: P-256
        writer.WriteInt32(-2); writer.WriteByteString(p.Q.X!);
        writer.WriteInt32(-3); writer.WriteByteString(p.Q.Y!);
        writer.WriteEndMap();
        return writer.Encode();
    }
}
