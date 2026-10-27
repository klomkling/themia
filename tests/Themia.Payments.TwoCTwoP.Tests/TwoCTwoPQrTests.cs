using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Themia.Payments.TwoCTwoP.Internal;

using Xunit;

namespace Themia.Payments.TwoCTwoP.Tests;

/// <summary>PromptPay-only charges go through 2C2P's Do Payment API and show the QR in the page. The response
/// shapes are the samples in 2C2P's "Do Payment" and "Payment Flow Codes" documentation (respCode 1005,
/// channelCode THQR / agentCode PPQR); none of it is verified against the sandbox.</summary>
public class TwoCTwoPQrTests
{
    private const string Secret = "test-secret";
    private const string HostedUrl = "https://sandbox-pgw-ui.2c2p.com/payment/4.3/#/token/abc";
    private const string PngUrl = "https://api.2c2p.com/pgw/qr/e5d8d1ed-2684-48b2-a7b6-d36fbc4fa5c2.png";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    [Fact]
    public async Task A_promptpay_only_charge_does_payment_and_shows_the_qr_in_the_page()
    {
        var handler = new RoutingHandler().Token().DoPayment(QrResponse()).Png(PngUrl, Png);
        var before = DateTimeOffset.UtcNow;

        var creation = await BuildGateway(handler).CreateChargeAsync(PromptPayOnly());

        var qr = Assert.IsType<NextAction.ShowQr>(creation.Action);
        Assert.Equal(Png, qr.ImagePng);
        Assert.Null(qr.RawPayload);
        Assert.InRange(qr.Expiry!.Value, before.AddMinutes(5), DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(PaymentStatus.Pending, creation.Status);
        Assert.Equal("order-1", creation.ChargeId);
    }

    [Fact]
    public async Task Do_payment_is_asked_for_the_promptpay_channel_with_the_payment_token()
    {
        var handler = new RoutingHandler().Token().DoPayment(QrResponse()).Png(PngUrl, Png);

        await BuildGateway(handler).CreateChargeAsync(PromptPayOnly());

        var doPayment = handler.Calls.Single(c => c.Path == "/payment/4.3/payment");
        using var body = JsonDocument.Parse(doPayment.Body);
        Assert.Equal("tok-abc", body.RootElement.GetProperty("paymentToken").GetString());
        var code = body.RootElement.GetProperty("payment").GetProperty("code");
        Assert.Equal("THQR", code.GetProperty("channelCode").GetString());
        Assert.Equal("PPQR", code.GetProperty("agentCode").GetString());
    }

    [Fact]
    public async Task A_promptpay_only_charge_in_hosted_page_mode_redirects_and_never_calls_do_payment()
    {
        var handler = new RoutingHandler().Token();

        var creation = await BuildGateway(handler).CreateChargeAsync(PromptPayOnly() with { PresentationMode = PresentationMode.HostedPage });

        Assert.Equal(new Uri(HostedUrl), Assert.IsType<NextAction.Redirect>(creation.Action).Url);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task A_charge_that_allows_cards_keeps_the_hosted_redirect_and_never_calls_do_payment()
    {
        var handler = new RoutingHandler().Token();

        var creation = await BuildGateway(handler).CreateChargeAsync(new CreateChargeRequest
        {
            Amount = Money.Thb(100000),
            ReferenceId = "order-1",
            AllowedMethods = [PaymentMethod.Card, PaymentMethod.QrPromptPay],
        });

        Assert.Equal(new Uri(HostedUrl), Assert.IsType<NextAction.Redirect>(creation.Action).Url);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Without_an_expiry_timer_the_requests_own_expiry_is_reported()
    {
        var handler = new RoutingHandler().Token().Png(PngUrl, Png)
            .DoPayment("""{"channelCode":"THQR","type":"URL","data":"https://api.2c2p.com/pgw/qr/x.png","respCode":"1005","respDesc":"Pending for user scan QR."}""")
            .Png("https://api.2c2p.com/pgw/qr/x.png", Png);
        var expiresAt = new DateTimeOffset(2099, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var creation = await BuildGateway(handler).CreateChargeAsync(PromptPayOnly(expiresAt));

        Assert.Equal(expiresAt, Assert.IsType<NextAction.ShowQr>(creation.Action).Expiry);
    }

    [Fact]
    public async Task A_do_payment_response_signed_with_the_merchant_secret_is_accepted()
    {
        var signed = JsonSerializer.Serialize(new { payload = JwtHs256.Encode(QrClaims(), Secret) });
        var handler = new RoutingHandler().Token().DoPayment(signed).Png(PngUrl, Png);

        var creation = await BuildGateway(handler).CreateChargeAsync(PromptPayOnly());

        Assert.IsType<NextAction.ShowQr>(creation.Action);
    }

    [Fact]
    public async Task A_do_payment_response_signed_with_another_secret_falls_back_to_the_redirect()
    {
        var forged = JsonSerializer.Serialize(new { payload = JwtHs256.Encode(QrClaims(), "someone-elses-secret") });
        var handler = new RoutingHandler().Token().DoPayment(forged).Png(PngUrl, Png);

        var creation = await BuildGateway(handler).CreateChargeAsync(PromptPayOnly());

        Assert.Equal(new Uri(HostedUrl), Assert.IsType<NextAction.Redirect>(creation.Action).Url);
        Assert.DoesNotContain(handler.Calls, c => c.Path.EndsWith(".png", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"channelCode":"THQR","type":"QR","data":"00020101021230...","respCode":"1005","respDesc":"x","expiryTimer":300000}""")]
    [InlineData("""{"channelCode":"THQR","type":"URL","data":"https://api.2c2p.com/pgw/qr/a.png","respCode":"1000","respDesc":"Redirect"}""")]
    [InlineData("""{"channelCode":"THQR","type":"URL","data":"https://evil.example/a.png","respCode":"1005","respDesc":"x"}""")]
    [InlineData("""{"channelCode":"THQR","type":"URL","data":"https://api.2c2p.com.evil.example/a.png","respCode":"1005","respDesc":"x"}""")]
    [InlineData("""{"channelCode":"THQR","type":"URL","data":"https://evil2c2p.com/a.png","respCode":"1005","respDesc":"x"}""")]
    [InlineData("""{"channelCode":"THQR","type":"URL","data":"http://api.2c2p.com/pgw/qr/a.png","respCode":"1005","respDesc":"x"}""")]
    [InlineData("""{"channelCode":"THQR","type":"URL","data":"not a url","respCode":"1005","respDesc":"x"}""")]
    [InlineData("""{"channelCode":"THQR","type":"URL","respCode":"1005","respDesc":"x"}""")]
    [InlineData("""{"respCode":"4001","respDesc":"Payment token invalid"}""")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    public async Task A_do_payment_answer_that_is_not_a_trusted_qr_url_falls_back_to_the_redirect(string doPaymentBody)
    {
        var handler = new RoutingHandler().Token().DoPayment(doPaymentBody).Png("https://api.2c2p.com/pgw/qr/a.png", Png);

        var creation = await BuildGateway(handler).CreateChargeAsync(PromptPayOnly());

        Assert.Equal(new Uri(HostedUrl), Assert.IsType<NextAction.Redirect>(creation.Action).Url);
        Assert.DoesNotContain(handler.Calls, c => c.Path.EndsWith(".png", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_do_payment_server_error_falls_back_to_the_redirect_without_retrying()
    {
        var handler = new RoutingHandler().Token().DoPayment("boom", HttpStatusCode.InternalServerError);

        var creation = await BuildGateway(handler).CreateChargeAsync(PromptPayOnly());

        Assert.IsType<NextAction.Redirect>(creation.Action);
        Assert.Single(handler.Calls, c => c.Path == "/payment/4.3/payment");
    }

    [Fact]
    public async Task A_do_payment_transport_failure_falls_back_to_the_redirect()
    {
        var handler = new RoutingHandler().Token().DoPaymentThrows(() => new HttpRequestException("down"));

        var creation = await BuildGateway(handler).CreateChargeAsync(PromptPayOnly());

        Assert.IsType<NextAction.Redirect>(creation.Action);
    }

    [Fact]
    public async Task A_qr_image_that_is_not_a_png_falls_back_to_the_redirect()
    {
        var handler = new RoutingHandler().Token().DoPayment(QrResponse()).Png(PngUrl, Encoding.UTF8.GetBytes("<html>nope</html>"));

        var creation = await BuildGateway(handler).CreateChargeAsync(PromptPayOnly());

        Assert.IsType<NextAction.Redirect>(creation.Action);
    }

    [Fact]
    public async Task A_qr_image_over_the_size_cap_falls_back_to_the_redirect()
    {
        var huge = new byte[300 * 1024];
        Png.CopyTo(huge, 0);
        var handler = new RoutingHandler().Token().DoPayment(QrResponse()).Png(PngUrl, huge);

        var creation = await BuildGateway(handler).CreateChargeAsync(PromptPayOnly());

        Assert.IsType<NextAction.Redirect>(creation.Action);
    }

    [Fact]
    public async Task A_qr_image_that_cannot_be_downloaded_falls_back_to_the_redirect()
    {
        var handler = new RoutingHandler().Token().DoPayment(QrResponse()).PngStatus(PngUrl, HttpStatusCode.NotFound);

        var creation = await BuildGateway(handler).CreateChargeAsync(PromptPayOnly());

        Assert.IsType<NextAction.Redirect>(creation.Action);
    }

    [Fact]
    public async Task A_qr_image_download_that_ends_on_another_host_falls_back_to_the_redirect()
    {
        var handler = new RoutingHandler().Token().DoPayment(QrResponse())
            .Png(PngUrl, Png, finalUri: new Uri("https://evil.example/q.png"));

        var creation = await BuildGateway(handler).CreateChargeAsync(PromptPayOnly());

        Assert.IsType<NextAction.Redirect>(creation.Action);
    }

    [Fact]
    public async Task A_qr_image_cut_off_mid_body_falls_back_to_the_redirect()
    {
        var handler = new RoutingHandler().Token().DoPayment(QrResponse())
            .PngBody(PngUrl, new FaultyStream(Png, hang: false));

        var creation = await BuildGateway(handler).CreateChargeAsync(PromptPayOnly());

        Assert.IsType<NextAction.Redirect>(creation.Action);
    }

    [Fact]
    public async Task A_qr_image_body_that_never_finishes_falls_back_to_the_redirect_at_the_client_timeout()
    {
        var handler = new RoutingHandler().Token().DoPayment(QrResponse())
            .PngBody(PngUrl, new FaultyStream(Png, hang: true));

        var creation = await BuildGateway(handler, TimeSpan.FromMilliseconds(200)).CreateChargeAsync(PromptPayOnly());

        Assert.IsType<NextAction.Redirect>(creation.Action);
    }

    [Fact]
    public async Task The_reported_expiry_is_the_earlier_of_the_countdown_and_the_requests_own_expiry()
    {
        var handler = new RoutingHandler().Token().DoPayment(QrResponse()).Png(PngUrl, Png);   // countdown: 5 minutes
        var ownDeadline = DateTimeOffset.UtcNow.AddMinutes(1);

        var creation = await BuildGateway(handler).CreateChargeAsync(PromptPayOnly(ownDeadline));

        Assert.Equal(ownDeadline, Assert.IsType<NextAction.ShowQr>(creation.Action).Expiry);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_swallowed_into_the_fallback()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RoutingHandler().Token().DoPaymentThrows(() =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BuildGateway(handler).CreateChargeAsync(PromptPayOnly(), cts.Token));
    }

    private static CreateChargeRequest PromptPayOnly(DateTimeOffset? expiresAt = null) => new()
    {
        Amount = Money.Thb(100000),
        ReferenceId = "order-1",
        AllowedMethods = [PaymentMethod.QrPromptPay],
        ExpiresAt = expiresAt,
    };

    private static string QrResponse() => JsonSerializer.Serialize(QrClaims());

    private static Dictionary<string, object?> QrClaims() => new()
    {
        ["channelCode"] = "THQR",
        ["type"] = "URL",
        ["data"] = PngUrl,
        ["respCode"] = "1005",
        ["respDesc"] = "Pending for user scan QR.",
        ["expiryTimer"] = 300000,
    };

    private static TwoCTwoPPaymentGateway BuildGateway(RoutingHandler handler, TimeSpan? timeout = null)
    {
        var client = new HttpClient(handler) { BaseAddress = TwoCTwoPOptions.BaseAddressFor(TwoCTwoPEnvironment.Sandbox) };
        if (timeout is { } limit)
        {
            client.Timeout = limit;
        }

        var options = new TwoCTwoPOptions { MerchantId = "JT01", SecretKey = Secret };
        var gate = new PaymentMethodGate(Options.Create(new ThemiaPaymentsOptions()));
        return new TwoCTwoPPaymentGateway(new SingleClientFactory(client), Options.Create(options), gate);
    }

    private sealed record Call(string Host, string Path, string Body);

    /// <summary>Answers by request path: the paymentToken call, Do Payment, and any QR image URL.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> routes = new(StringComparer.Ordinal);

        public List<Call> Calls { get; } = [];

        public RoutingHandler Token() => Route("sandbox-pgw.2c2p.com/payment/4.3/paymentToken", () => Text(
            JsonSerializer.Serialize(new
            {
                payload = JwtHs256.Encode(new Dictionary<string, object?>
                {
                    ["webPaymentUrl"] = HostedUrl,
                    ["paymentToken"] = "tok-abc",
                    ["respCode"] = "0000",
                    ["respDesc"] = "Success",
                }, Secret),
            }), HttpStatusCode.OK));

        public RoutingHandler DoPayment(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            Route("sandbox-pgw.2c2p.com/payment/4.3/payment", () => Text(body, status));

        public RoutingHandler DoPaymentThrows(Func<Exception> exception) =>
            Route("sandbox-pgw.2c2p.com/payment/4.3/payment", () => throw exception());

        public RoutingHandler Png(string url, byte[] bytes, Uri? finalUri = null) =>
            Route(KeyOf(new Uri(url)), () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
                RequestMessage = finalUri is null ? null : new HttpRequestMessage(HttpMethod.Get, finalUri),
            });

        public RoutingHandler PngBody(string url, Stream body) =>
            Route(KeyOf(new Uri(url)), () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });

        public RoutingHandler PngStatus(string url, HttpStatusCode status) =>
            Route(KeyOf(new Uri(url)), () => new HttpResponseMessage(status));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Call(uri.Host, uri.AbsolutePath, body));
            return routes.TryGetValue(KeyOf(uri), out var respond)
                ? respond()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private RoutingHandler Route(string key, Func<HttpResponseMessage> respond)
        {
            routes[key] = respond;
            return this;
        }

        private static string KeyOf(Uri uri) => uri.Host + uri.AbsolutePath;

        private static HttpResponseMessage Text(string body, HttpStatusCode status) =>
            new(status) { Content = new StringContent(body) };
    }

    /// <summary>Delivers the PNG signature, then either fails like a dropped connection or never returns.</summary>
    private sealed class FaultyStream(byte[] head, bool hang) : Stream
    {
        private bool sentHead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!sentHead)
            {
                sentHead = true;
                head.AsMemory(0, 8).CopyTo(buffer);
                return 8;
            }

            if (hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            throw new IOException("The response ended prematurely.");
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
