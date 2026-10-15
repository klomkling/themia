using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Themia.ETax.Withholding.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void AddThemiaETaxWithholding_ShouldResolveTheService_FromAScope_WhenTheAppsStoreAndNumbersAreScoped_WithScopeValidationOn()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IWithholdingCertificateStore>(_ => new InMemoryWithholdingCertificateStore());
        services.AddScoped<IWithholdingNumberSource, Flow.CountingNumbers>();
        services.AddThemiaETaxWithholding(Samples.Catalogue());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<WithholdingCertificateService>());
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<WithholdingCertificateService>());
    }

    [Fact]
    public async Task AddThemiaETaxWithholding_ShouldApplyTheValidationOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWithholdingCertificateStore>(new InMemoryWithholdingCertificateStore());
        services.AddSingleton<IWithholdingNumberSource, Flow.CountingNumbers>();
        services.AddThemiaETaxWithholding(Samples.Catalogue(), o => o.ValidateTaxIdChecksum = false);
        using var provider = services.BuildServiceProvider();
        var badChecksum = Samples.Valid() with { Payee = Samples.Valid().Payee with { TaxId = "1101700230700" } };

        await using var tx = new InMemoryWithholdingCertificateStore.FakeTransaction();
        var result = await provider.GetRequiredService<WithholdingCertificateService>().IssueAsync(tx, Flow.Request("pay_1", badChecksum));

        Assert.Equal(WithholdingIssueOutcome.Issued, result.Outcome);
    }

    [Fact]
    public void AddThemiaETaxWithholding_ShouldRejectNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddThemiaETaxWithholding(Samples.Catalogue()));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddThemiaETaxWithholding(null!));
    }
}
