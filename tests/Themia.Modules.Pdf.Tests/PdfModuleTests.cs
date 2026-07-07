using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Xunit;

namespace Themia.Modules.Pdf.Tests;

public sealed class PdfModuleTests
{
    [Fact]
    public void Descriptor_reports_module_identity()
    {
        var module = new PdfModule(MigrationEngine.Postgres);

        Assert.Equal("Themia.Pdf", module.Descriptor.Name);
        Assert.Equal(new Version(0, 7, 0, 0), module.Descriptor.Version);
    }

    [Fact]
    public async Task InitializeAsync_throws_when_connection_string_missing()
    {
        var module = new PdfModule(MigrationEngine.Postgres);
        var provider = BuildProvider(configuration: new ConfigurationBuilder().Build());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await module.InitializeAsync(provider));
    }

    [Fact]
    public async Task InitializeAsync_honors_cancellation_before_touching_config()
    {
        var module = new PdfModule(MigrationEngine.Postgres);
        // No "Default" connection string: if the token were not honored first this would throw
        // InvalidOperationException instead, so the OperationCanceledException proves early exit.
        var provider = BuildProvider(configuration: new ConfigurationBuilder().Build());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await module.InitializeAsync(provider, cts.Token));
    }

    private static IServiceProvider BuildProvider(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        return services.BuildServiceProvider();
    }
}
