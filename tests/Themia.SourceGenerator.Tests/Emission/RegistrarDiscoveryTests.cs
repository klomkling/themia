using Themia.SourceGenerator.Generators;
using VerifyXunit;
using Xunit;

namespace Themia.SourceGenerator.Tests.Emission;

public class RegistrarDiscoveryTests
{
    [Fact]
    public Task SingleRegistrar_EmitsConstructAndRegisterCall()
    {
        var source = """
            using Themia.DependencyInjection;
            using Microsoft.Extensions.DependencyInjection;
            namespace Demo;
            public class MyRegistrar : IThemiaServiceRegistrar
            {
                public void Register(IServiceCollection services)
                {
                    services.AddSingleton<object>();
                }
            }
        """;
        return TestHelpers.Verify<ServiceRegistrationGenerator>(source);
    }
}
