using Themia.SourceGenerator.Generators;
using VerifyXunit;
using Xunit;

namespace Themia.SourceGenerator.Tests.Emission;

public class SelfRegistrationTests
{
    [Fact]
    public Task AllowSelfRegistration_RegistersConcreteTypeAsItself()
    {
        // No interface to resolve by convention, but AllowSelfRegistration = true opts the
        // type into registering as itself (services.AddScoped<Worker, Worker>()).
        var source = """
            using Themia.DependencyInjection;
            namespace Demo;
            [Scoped(AllowSelfRegistration = true)]
            public class StandaloneWorker { }
        """;
        return TestHelpers.Verify<ServiceRegistrationGenerator>(source);
    }
}
