using Microsoft.Extensions.DependencyInjection;
using Themia.Quartz;
using Xunit;

namespace Themia.Quartz.Tests;

/// <summary>
/// AddThemiaQuartz must compose across calls. Themia.Modules.Scheduling calls it to wire
/// VirtualPathRoot/Authorize, and the host app calls it to set appearance (HeadHtml, CustomStyleSheet,
/// ProductName…). Registering the options with TryAddSingleton made the FIRST call win outright and
/// silently discarded the other's settings — in either order — so a module consumer could not configure
/// the dashboard at all.
/// </summary>
public sealed class AddThemiaQuartzCompositionTests
{
    private static ThemiaQuartzOptions Resolve(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<ThemiaQuartzOptions>();

    [Fact]
    public void TwoCalls_ModuleThenApp_ApplyBothDelegates()
    {
        var services = new ServiceCollection();

        // The module wires routing + auth…
        services.AddThemiaQuartz(o => { o.VirtualPathRoot = "/admin/jobs"; o.Authorize = _ => Task.FromResult(true); });
        // …then the app sets appearance.
        services.AddThemiaQuartz(o => { o.HeadHtml = "<meta name=\"x\">"; o.ProductName = "Ezy"; });

        var options = Resolve(services);

        Assert.Equal("/admin/jobs", options.VirtualPathRoot);
        Assert.NotNull(options.Authorize);
        Assert.Equal("<meta name=\"x\">", options.HeadHtml);
        Assert.Equal("Ezy", options.ProductName);
    }

    [Fact]
    public void TwoCalls_AppThenModule_ApplyBothDelegates()
    {
        var services = new ServiceCollection();

        // Reverse order must be equally safe: previously this dropped the module's routing/auth wiring,
        // mounting the dashboard at the wrong path and leaving it deny-all.
        services.AddThemiaQuartz(o => { o.HeadHtml = "<meta name=\"x\">"; o.ProductName = "Ezy"; });
        services.AddThemiaQuartz(o => { o.VirtualPathRoot = "/admin/jobs"; o.Authorize = _ => Task.FromResult(true); });

        var options = Resolve(services);

        Assert.Equal("/admin/jobs", options.VirtualPathRoot);
        Assert.NotNull(options.Authorize);
        Assert.Equal("<meta name=\"x\">", options.HeadHtml);
        Assert.Equal("Ezy", options.ProductName);
    }

    [Fact]
    public void TwoCalls_RegisterOptionsExactlyOnce()
    {
        var services = new ServiceCollection();

        services.AddThemiaQuartz(o => o.ProductName = "first");
        services.AddThemiaQuartz(o => o.HeadHtml = "<x>");

        Assert.Single(services, d => d.ServiceType == typeof(ThemiaQuartzOptions));
    }

    [Fact]
    public void LastCallWins_ForTheSameProperty()
    {
        var services = new ServiceCollection();

        services.AddThemiaQuartz(o => o.ProductName = "first");
        services.AddThemiaQuartz(o => o.ProductName = "second");

        Assert.Equal("second", Resolve(services).ProductName);
    }

    [Fact]
    public void SingleCall_StillConfiguresOptions()
    {
        var services = new ServiceCollection();

        services.AddThemiaQuartz(o => { o.VirtualPathRoot = "/jobs"; o.ProductName = "Solo"; });

        var options = Resolve(services);

        Assert.Equal("/jobs", options.VirtualPathRoot);
        Assert.Equal("Solo", options.ProductName);
    }
}
