using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Themia.AspNetCore;
using Themia.AspNetCore.Exceptions;
using Xunit;

namespace Themia.AspNetCore.Tests;

public sealed class UseThemiaProblemDetailsTests
{
    [Fact]
    public async Task Middleware_translates_exception_end_to_end()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .Configure(app =>
                {
                    app.UseThemiaProblemDetails();
                    app.Run(_ => throw new NotFoundException("missing"));
                }))
            .StartAsync();

        using var client = host.GetTestClient();
        using var response = await client.GetAsync("/anything");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }
}
