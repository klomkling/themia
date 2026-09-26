using System.Net;

namespace Themia.Payments.TwoCTwoP.Tests;

/// <summary>Replays scripted responses and records what was sent, including each attempt's headers.</summary>
/// <remarks>Copied unchanged from Themia.Payments.Beam.Tests (Task 13); Task 15 links one shared copy into
/// every payments test project.</remarks>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> responses = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    public StubHandler Enqueue(HttpStatusCode status, string body)
    {
        responses.Enqueue((status, body));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));

        var (status, body) = responses.Count > 0 ? responses.Dequeue() : (HttpStatusCode.OK, "{}");
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}
