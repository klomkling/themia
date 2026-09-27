using System.Net;

namespace Themia.Payments.Beam.Tests;

/// <summary>Replays scripted responses and records what was sent, including each attempt's headers.</summary>
/// <remarks>Public, not internal: Task 15's <c>PaymentGatewayContract</c> exposes it through public abstract
/// members so every adapter's contract subclass can override them.</remarks>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body, Func<Exception>? Throw)> responses = new();

    public List<HttpRequestMessage> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    public StubHandler Enqueue(HttpStatusCode status, string body)
    {
        responses.Enqueue((status, body, null));
        return this;
    }

    /// <summary>Makes the next attempt throw what <paramref name="exception"/> returns instead of responding.</summary>
    public StubHandler EnqueueThrow(Func<Exception> exception)
    {
        responses.Enqueue((default, "", exception));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));

        var (status, body, exception) = responses.Count > 0 ? responses.Dequeue() : (HttpStatusCode.OK, "{}", null);
        if (exception is not null)
        {
            throw exception();
        }

        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}
