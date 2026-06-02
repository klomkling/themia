using Themia.SourceGenerator.Generators;
using VerifyXunit;
using Xunit;

namespace Themia.SourceGenerator.Tests.Emission;

public class MarkerDiscoveryTests
{
    [Fact]
    public Task NonGenericScopedMarker()
    {
        var source = """
            using Themia.DependencyInjection;
            namespace Demo;
            public interface ICustomerRepository { }
            public class CustomerRepository : ICustomerRepository, IScopedService { }
        """;
        return TestHelpers.Verify<ServiceRegistrationGenerator>(source);
    }

    [Fact]
    public Task GenericScopedMarker_PinsServiceType()
    {
        var source = """
            using Themia.DependencyInjection;
            namespace Demo;
            public interface IFoo { }
            public interface IBar { }
            public class CustomerRepository : IFoo, IBar, IScopedService<IFoo> { }
        """;
        return TestHelpers.Verify<ServiceRegistrationGenerator>(source);
    }
}
