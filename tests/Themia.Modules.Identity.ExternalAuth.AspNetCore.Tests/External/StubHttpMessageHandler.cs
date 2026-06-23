using System.Net;

namespace Themia.Modules.Identity.ExternalAuth.AspNetCore.Tests.External;

/// <summary>A test <see cref="HttpMessageHandler"/> that returns a response built by a per-request
/// factory, recording the requests it saw so tests can assert on the outbound exchange.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> responder;

    public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) =>
        this.responder = responder;

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Captured request bodies, in order. The body stream is consumed when received, so it is
    /// buffered here for assertions.</summary>
    public List<string> RequestBodies { get; } = [];

    public static StubHttpMessageHandler Json(HttpStatusCode status, string json) =>
        new(_ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        }));

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content is not null)
        {
            RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        }

        return await responder(request);
    }
}
