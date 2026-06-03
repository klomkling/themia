using Themia.SourceGenerator.Generators;
using VerifyXunit;
using Xunit;

namespace Themia.SourceGenerator.Tests.Emission;

public class KeyedRegistrationTests
{
    [Fact]
    public Task ServiceKey_EmitsKeyedRegistration()
    {
        // ServiceKey set → keyed registration (services.AddKeyedScoped<TService, TImpl>("key")).
        var source = """
            using Themia.DependencyInjection;
            namespace Demo;
            public interface IPaymentGateway { }
            [Scoped(ServiceType = typeof(IPaymentGateway), ServiceKey = "primary")]
            public class PrimaryGateway : IPaymentGateway { }
        """;
        return TestHelpers.Verify<ServiceRegistrationGenerator>(source);
    }
}
