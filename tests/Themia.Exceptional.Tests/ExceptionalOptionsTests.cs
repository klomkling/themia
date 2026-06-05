using Themia.Exceptional;
using Xunit;

namespace Themia.Exceptional.Tests;

public class ExceptionalOptionsTests
{
    [Fact]
    public void Validate_Throws_WhenApplicationNameMissing()
    {
        var options = new ExceptionalOptions { ApplicationName = "" };

        var ex = Assert.Throws<InvalidOperationException>((Action)(() => options.Validate()));

        Assert.Contains("ApplicationName", ex.Message);
    }

    [Fact]
    public void Validate_Throws_WhenRollupPeriodNegative()
    {
        var options = new ExceptionalOptions
        {
            ApplicationName = "App",
            RollupPeriod = TimeSpan.FromSeconds(-1),
        };

        Assert.Throws<InvalidOperationException>((Action)(() => options.Validate()));
    }

    [Fact]
    public void Validate_Passes_WithDefaults()
    {
        var options = new ExceptionalOptions { ApplicationName = "App" };

        options.Validate(); // no throw

        Assert.Equal(TimeSpan.FromMinutes(10), options.RollupPeriod);
        Assert.False(options.CaptureRequestBody);
    }
}
