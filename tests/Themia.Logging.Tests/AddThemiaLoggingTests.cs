using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Themia.Logging;
using Xunit;

namespace Themia.Logging.Tests;

public class AddThemiaLoggingTests
{
    [Fact]
    public void AddThemiaLogging_RegistersLoggerFactory()
    {
        var services = new ServiceCollection();
        services.AddThemiaLogging();
        using var sp = services.BuildServiceProvider();
        var factory = sp.GetService<ILoggerFactory>();
        Assert.NotNull(factory);
        // A logger can be created without throwing.
        var logger = factory!.CreateLogger("smoke");
        Assert.NotNull(logger);
    }
}
