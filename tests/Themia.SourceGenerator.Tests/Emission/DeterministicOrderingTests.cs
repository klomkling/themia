using Themia.SourceGenerator.Generators;
using VerifyXunit;
using Xunit;

namespace Themia.SourceGenerator.Tests.Emission;

public class DeterministicOrderingTests
{
    [Fact]
    public Task RegistrationsAreSortedAlphabetically()
    {
        var source = """
            using Themia.DependencyInjection;
            namespace Demo;
            public interface IZetaRepository { }
            [Scoped]
            public class ZetaRepository : IZetaRepository { }
            public interface IAlphaRepository { }
            [Scoped]
            public class AlphaRepository : IAlphaRepository { }
            public interface IMidRepository { }
            [Scoped]
            public class MidRepository : IMidRepository { }
        """;
        return TestHelpers.Verify<ServiceRegistrationGenerator>(source);
    }
}
