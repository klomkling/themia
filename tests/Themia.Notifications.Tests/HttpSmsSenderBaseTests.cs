using System.Net;
using Themia.Notifications;
using Themia.Notifications.Providers;
using Xunit;

namespace Themia.Notifications.Tests;

public sealed class HttpSmsSenderBaseTests
{
    private sealed class StubHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
    }

    private sealed class FakeSmsSender(HttpClient http) : HttpSmsSenderBase(http)
    {
        protected override HttpRequestMessage BuildRequest(NotificationMessage m)
            => new(HttpMethod.Post, "https://sms.test/send") { Content = new StringContent($"{m.Recipient}:{m.Body}") };
        protected override NotificationResult Interpret(HttpStatusCode status, string responseBody)
            => status == HttpStatusCode.OK ? NotificationResult.Success(responseBody) : NotificationResult.Failure(responseBody);
    }

    [Fact]
    public async Task Send_PostsAndInterpretsSuccess()
    {
        var sut = new FakeSmsSender(new HttpClient(new StubHandler(HttpStatusCode.OK, "msg-123")));
        var r = await sut.SendAsync(new NotificationMessage { Channel = NotificationChannel.Sms, Recipient = "+100", Body = "hi" });
        Assert.True(r.Succeeded);
        Assert.Equal("msg-123", r.ProviderMessageId);
    }

    [Fact]
    public async Task Send_InterpretsFailureFromStatus()
    {
        var sut = new FakeSmsSender(new HttpClient(new StubHandler(HttpStatusCode.BadGateway, "down")));
        var r = await sut.SendAsync(new NotificationMessage { Channel = NotificationChannel.Sms, Recipient = "+100", Body = "hi" });
        Assert.False(r.Succeeded);
        Assert.Equal("down", r.Error);
    }
}
