using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.DependencyInjection;
using Themia.Framework.Data.Dapper.Mapping;
using Xunit;

namespace Themia.Framework.Data.Dapper.Tests.Mapping;

/// <summary>
/// One mapping-contribution mechanism for every module. Four modules had hand-rolled the same scan and
/// the copies drifted into three behaviours for one adopter mistake, so registering the peer after the
/// modules produced a hard failure from one module and silently unmapped tables from the others in the
/// same startup.
/// </summary>
public class DapperMappingRegistrationTests
{
    private sealed class Widget
    {
        public Guid Id { get; set; }
    }

    [Fact]
    public void ContributeDapperMappings_applies_to_the_registered_registry()
    {
        var services = new ServiceCollection();
        var registry = new EntityMappingRegistry();
        services.AddSingleton(registry);

        services.ContributeDapperMappings(Apply, "AddThemiaWidgets");

        Assert.Equal("widgets.widget", registry.For<Widget>().Table);
    }

    [Fact]
    public void ContributeDapperMappings_is_a_no_op_on_an_EF_peer()
    {
        // Neither a registry nor a Dapper connection context: a genuine EF Core adopter of a module that
        // supports both peers. Throwing here would break them.
        var services = new ServiceCollection();

        services.ContributeDapperMappings(Apply, "AddThemiaWidgets");
    }

    [Fact]
    public void ContributeDapperMappings_throws_when_a_dapper_peer_is_present_without_its_registry()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => (IDapperConnectionContext)null!);

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.ContributeDapperMappings(Apply, "AddThemiaWidgets"));

        Assert.Contains("AddThemiaWidgets", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireDapperMappings_throws_when_no_registry_is_registered()
    {
        // A Dapper-only package has no legitimate no-registry case, so it names the ordering outright
        // rather than mirroring the "genuine EF peer" silence.
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.RequireDapperMappings(Apply, "AddThemiaWidgetsDapper"));

        Assert.Contains("AddThemiaWidgetsDapper", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddThemiaDapperCore_called_twice_keeps_one_registry()
    {
        // A second registry would win resolution while the mappings modules contributed sat on the first,
        // so every module-mapped table would silently fall back to its convention name.
        var services = new ServiceCollection();
        services.AddThemiaDapperCore();
        services.RequireDapperMappings(Apply, "AddThemiaWidgetsDapper");
        services.AddThemiaDapperCore();

        var registries = services
            .Where(d => d.ServiceType == typeof(EntityMappingRegistry))
            .ToList();

        Assert.Single(registries);
        Assert.Equal("widgets.widget", services.FindEntityMappingRegistry()!.For<Widget>().Table);
    }

    private static void Apply(EntityMappingRegistry registry)
        => registry.Register<Widget>(EntityMapping.ForConvention<Widget>("widgets.widget", null));
}
