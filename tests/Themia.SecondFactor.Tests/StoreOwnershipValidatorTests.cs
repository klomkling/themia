using Microsoft.Extensions.DependencyInjection;
using Themia.SecondFactor.DependencyInjection;
using Themia.Totp;
using Themia.WebAuthn;
using Xunit;

namespace Themia.SecondFactor.Tests;

public sealed class StoreOwnershipValidatorTests
{
    private static readonly byte[] Key = new byte[32];

    private static ServiceCollection Registered(Action<ServiceCollection>? before = null)
    {
        var services = new ServiceCollection();
        before?.Invoke(services);
        services.AddThemiaSecondFactor(Key, options => options.Issuer = "Test", webAuthn =>
        {
            webAuthn.ServerDomain = "localhost";
            webAuthn.ServerName = "Test";
            webAuthn.Origins.Add("https://localhost");
        });
        return services;
    }

    [Fact]
    public void Validate_ShouldSucceed_WhenThisLayerOwnsBothStores()
    {
        var result = new StoreOwnershipValidator(Registered()).Validate(null, new SecondFactorOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_ShouldFailNamingTypesOnly_WhenTheAppRegisteredItsOwnTotpStore()
    {
        var services = Registered(s => s.AddScoped<ITotpReplayStore, OtherReplayStore>());

        var result = new StoreOwnershipValidator(services).Validate(null, new SecondFactorOptions());

        Assert.True(result.Failed);
        Assert.Contains(nameof(OtherReplayStore), result.FailureMessage, StringComparison.Ordinal);
        Assert.Contains(nameof(MfaTotpReplayStore), result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ShouldFail_WhenTheAppRegisteredItsOwnChallengeStoreAfterwards()
    {
        var services = Registered();
        services.AddScoped<IWebAuthnChallengeStore, OtherChallengeStore>();

        var result = new StoreOwnershipValidator(services).Validate(null, new SecondFactorOptions());

        Assert.True(result.Failed);
        Assert.Contains(nameof(OtherChallengeStore), result.FailureMessage, StringComparison.Ordinal);
    }

    private sealed class OtherReplayStore : ITotpReplayStore
    {
        public ValueTask<bool> TryAdvanceAsync(string secretId, long matchedStep, CancellationToken ct = default) => ValueTask.FromResult(true);
    }

    private sealed class OtherChallengeStore : IWebAuthnChallengeStore
    {
        public ValueTask StoreAsync(string challengeId, string optionsJson, TimeSpan ttl, CancellationToken ct = default) => ValueTask.CompletedTask;

        public ValueTask<string?> TryConsumeAsync(string challengeId, CancellationToken ct = default) => ValueTask.FromResult<string?>(null);
    }
}
