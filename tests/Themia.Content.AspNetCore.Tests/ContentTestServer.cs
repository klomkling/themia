using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Themia.Content.AspNetCore.Tests;

internal static class ContentTestServer
{
    /// <summary>A request carrying this header is authenticated as the header's value (the NameIdentifier claim).</summary>
    public const string UserHeader = "X-Test-User";

    public static async Task<HttpClient> StartAsync(IContentPageService service, Action<IEndpointRouteBuilder> map)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(service);
                });
                web.Configure(app =>
                {
                    app.Use((context, next) =>
                    {
                        if (context.Request.Headers.TryGetValue(UserHeader, out var user))
                        {
                            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                                [new Claim(ClaimTypes.NameIdentifier, user.ToString())], authenticationType: "Test"));
                        }

                        return next(context);
                    });
                    app.UseRouting();
                    app.UseEndpoints(map);
                });
            })
            .StartAsync();
        return host.GetTestClient();
    }
}
