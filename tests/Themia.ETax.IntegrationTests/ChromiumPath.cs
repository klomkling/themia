using Themia.Pdf;

namespace Themia.ETax.IntegrationTests;

/// <summary>
/// Points the renderer at a Chromium the caller already installed, so test classes that run in parallel do
/// not each download one into the same <c>chrome-linux64.zip</c> (an <see cref="IOException"/> "being used by
/// another process"). The nightly workflow installs it once (<c>eng/fetch-chrome.sh</c>) and exports the path;
/// with the variable unset the renderer fetches its own, which is what a local run wants.
/// </summary>
internal static class ChromiumPath
{
    internal const string EnvironmentVariable = "PUPPETEER_EXECUTABLE_PATH";

    internal static void Apply(ThemiaPdfOptions options)
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(path))
            options.ExecutablePath = path;
    }
}
