using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Themia.Pdf;
using Xunit;

namespace Themia.Pdf.Tests;

public sealed class ThemiaPdfServiceCollectionExtensionsTests
{
    private static ServiceProvider Build(Action<ThemiaPdfOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaPdf(configure);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddThemiaPdf_RegistersBothRenderers()
    {
        using var sp = Build();

        Assert.NotNull(sp.GetService<IHtmlTemplateRenderer>());
        Assert.NotNull(sp.GetService<IPdfRenderer>());
    }

    [Fact]
    public void AddThemiaPdf_RenderersAreSingletons()
    {
        using var sp = Build();

        Assert.Same(sp.GetService<IPdfRenderer>(), sp.GetService<IPdfRenderer>());
        Assert.Same(sp.GetService<IHtmlTemplateRenderer>(), sp.GetService<IHtmlTemplateRenderer>());
    }

    [Fact]
    public void AddThemiaPdf_AppliesConfigure()
    {
        using var sp = Build(o => o.ExecutablePath = "/usr/bin/chromium");

        var options = sp.GetRequiredService<ThemiaPdfOptions>();
        Assert.Equal("/usr/bin/chromium", options.ExecutablePath);
    }

    [Fact]
    public void AddThemiaPdf_IsIdempotent()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaPdf();
        services.AddThemiaPdf();
        using var sp = services.BuildServiceProvider();

        Assert.Single(sp.GetServices<IPdfRenderer>());
    }
}
