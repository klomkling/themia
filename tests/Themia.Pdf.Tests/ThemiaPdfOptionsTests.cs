using Themia.Pdf;
using Xunit;

namespace Themia.Pdf.Tests;

public sealed class ThemiaPdfOptionsTests
{
    [Fact]
    public void Defaults_AreFaithfulToEzy()
    {
        var o = new ThemiaPdfOptions();

        Assert.Null(o.ExecutablePath);
        Assert.False(o.DisableAutoDownload);
        Assert.True(o.Headless);
        Assert.Null(o.ConfigureHandlebars);
        Assert.Equal(
            new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" },
            o.LaunchArgs);
    }
}
