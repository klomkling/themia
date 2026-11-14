using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Themia.Totp;

using Xunit;

namespace Themia.SecondFactor.IntegrationTests;

// The recipe in the README ("Importing existing TOTP secrets"): an app that already holds TOTP secrets copies each into
// IMfaMethodStore.AddAsync with the scheme that protects it today, and a protector that still reads that scheme keeps
// the user signing in without re-enrolling.
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LegacyTotpImportTests : IAsyncLifetime
{
    private const string LegacyScheme = "legacy-v1";
    private const string LegacySecret = "JBSWY3DPEHPK3PXPJBSWY3DPEE";
    private const int TotpPeriodSeconds = 30;
    private static readonly MfaPrimary Password = new(MfaPrimaryKind.Password);

    private readonly PostgresFixture fixture;
    private ServiceHarness harness = null!;

    public LegacyTotpImportTests(PostgresFixture fixture) => this.fixture = fixture;

    public Task InitializeAsync()
    {
        harness = new ServiceHarness(fixture.ConnectionString, configureServices: services => services.AddSingleton<ISecondFactorSecretProtector, LegacyAwareProtector>());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await harness.DisposeAsync();

    // Stands in for the app's old at-rest format: the new scheme writes base64, the legacy one stored the secret reversed.
    private sealed class LegacyAwareProtector : ISecondFactorSecretProtector
    {
        public string Scheme => "new-v1";

        public string Protect(string secret) => Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));

        public string? Unprotect(string scheme, string protectedSecret) => scheme switch
        {
            "new-v1" => Encoding.UTF8.GetString(Convert.FromBase64String(protectedSecret)),
            LegacyScheme => new string(protectedSecret.Reverse().ToArray()),
            _ => null,
        };
    }

    private long CurrentStep() => harness.Clock.GetUtcNow().ToUnixTimeSeconds() / TotpPeriodSeconds;

    private async Task<(MfaSubject Subject, Guid MethodId)> ImportAsync(string scheme, long lastUsedStep = -1)
    {
        var subject = ServiceHarness.NewSubject();
        using var scope = harness.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMfaMethodStore>();
        var outcome = await store.AddAsync(new NewMfaMethod
        {
            TenantId = subject.TenantId,
            UserId = subject.UserId,
            Kind = MfaMethodKind.Totp,
            Label = "Authenticator",
            SecretProtected = new string(LegacySecret.Reverse().ToArray()),
            ProtectionScheme = scheme,
            LastUsedStep = lastUsedStep,
            EnrolledAt = harness.Clock.GetUtcNow(),
        });
        Assert.Equal(MfaEnrolOutcome.Added, outcome);
        var service = scope.ServiceProvider.GetRequiredService<ISecondFactorService>();
        return (subject, (await service.ListMethodsAsync(subject)).Single().Id);
    }

    private string CurrentCode()
    {
        using var scope = harness.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ITotpService>().GenerateCode(LegacySecret);
    }

    [Fact]
    public async Task VerifyTotp_ShouldSucceed_ForASecretImportedUnderTheLegacyScheme_WithoutReEnrolling()
    {
        var (subject, methodId) = await ImportAsync(LegacyScheme);
        using var scope = harness.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISecondFactorService>().VerifyTotpAsync(subject, Password, methodId, CurrentCode());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task VerifyTotp_ShouldRefuseTheCodeOfTheImportedLastUsedStep_AsReplayed()
    {
        var (subject, methodId) = await ImportAsync(LegacyScheme, lastUsedStep: CurrentStep());
        using var scope = harness.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISecondFactorService>().VerifyTotpAsync(subject, Password, methodId, CurrentCode());

        Assert.Equal(SecondFactorOutcome.Replayed, result.Outcome);
    }

    [Fact]
    public async Task VerifyTotp_ShouldFailClosed_ForASchemeNoProtectorReads()
    {
        var (subject, methodId) = await ImportAsync("retired-v0");
        using var scope = harness.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISecondFactorService>().VerifyTotpAsync(subject, Password, methodId, CurrentCode());

        Assert.Equal(SecondFactorOutcome.NotAvailable, result.Outcome);
    }
}
