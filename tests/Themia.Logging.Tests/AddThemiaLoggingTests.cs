using Microsoft.Extensions.Configuration;
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
        // Configure via a "Serilog" section with a console-only sink so the test stays
        // side-effect free — the default (parameterless) path enables the file sink and
        // would create/lock files under logs/ during the run.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Information",
                ["Serilog:WriteTo:0:Name"] = "Console",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddThemiaLogging(configuration);
        using var sp = services.BuildServiceProvider();

        var factory = sp.GetService<ILoggerFactory>();
        Assert.NotNull(factory);

        // A logger can be created without throwing.
        var logger = factory!.CreateLogger("smoke");
        Assert.NotNull(logger);
    }
}
