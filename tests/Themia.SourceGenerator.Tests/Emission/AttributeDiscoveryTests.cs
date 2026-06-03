using Themia.SourceGenerator.Generators;
using VerifyXunit;
using Xunit;

namespace Themia.SourceGenerator.Tests.Emission;

public class AttributeDiscoveryTests
{
    [Fact]
    public Task SingleScopedAttributeOnConventionalType()
    {
        var source = """
            using Themia.DependencyInjection;
            namespace Demo;
            public interface ICustomerRepository { }
            [Scoped]
            public class CustomerRepository : ICustomerRepository { }
        """;
        return TestHelpers.Verify<ServiceRegistrationGenerator>(source);
    }
}
