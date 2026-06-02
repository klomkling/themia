using Themia.DependencyInjection;
using Xunit;

namespace Themia.DependencyInjection.Tests;

public class MarkerInterfaceTests
{
    [Fact]
    public void IScopedService_Generic_ExtendsScopedService()
    {
        Assert.True(typeof(IScopedService<object>).IsAssignableTo(typeof(IScopedService)));
    }

    [Fact]
    public void ISingletonService_Generic_ExtendsSingletonService()
    {
        Assert.True(typeof(ISingletonService<object>).IsAssignableTo(typeof(ISingletonService)));
    }

    [Fact]
    public void ITransientService_Generic_ExtendsTransientService()
    {
        Assert.True(typeof(ITransientService<object>).IsAssignableTo(typeof(ITransientService)));
    }

    [Fact]
    public void MarkerInterfacesAreInThemiaDependencyInjectionNamespace()
    {
        Assert.Equal("Themia.DependencyInjection", typeof(IScopedService).Namespace);
        Assert.Equal("Themia.DependencyInjection", typeof(ISingletonService).Namespace);
        Assert.Equal("Themia.DependencyInjection", typeof(ITransientService).Namespace);
    }
}
