using Microsoft.Extensions.DependencyInjection;
using Themia.Modules.Storage;
using Themia.Modules.Storage.DependencyInjection;
using Themia.Storage;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Modules.Storage.Tests;

public sealed class StorageBuilderTests
{
    [Fact]
    public void UseLocal_registers_a_local_provider()
    {
        var services = new ServiceCollection();
        services.AddThemiaStorage().UseLocal(o => { o.RootPath = Path.GetTempPath(); o.SigningKey = "k-please-change-32-bytes-minimum"; });

        using var sp = services.BuildServiceProvider();
        Assert.IsType<LocalStorageProvider>(sp.GetRequiredService<IStorageProvider>());
        // ITenantStorage is registered (scoped); it also needs the data layer to resolve, exercised in
        // the integration conformance suite (Task 16). Here we only assert the backend selection.
        Assert.Contains(services, d => d.ServiceType == typeof(ITenantStorage));
    }

    [Fact]
    public void Registering_two_backends_throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddThemiaStorage();
        builder.UseLocal(o => { o.RootPath = Path.GetTempPath(); o.SigningKey = "k-please-change-32-bytes-minimum"; });

        Assert.Throws<InvalidOperationException>(() =>
            builder.UseS3(o => { o.BucketName = "b"; o.Region = "us-east-1"; }));
    }
}
