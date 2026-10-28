using Amazon.Runtime;
using Themia.Storage.S3;
using Xunit;

namespace Themia.Storage.Tests;

/// <summary>
/// Cloudflare R2 rejects the SDK's default upload framing: the checksum trailer, and aws-chunked streaming
/// signatures ("STREAMING-AWS4-HMAC-SHA256-PAYLOAD not implemented", coord #0178). For a custom endpoint the PUT
/// must go out as one plain body with a known Content-Length; real AWS keeps the SDK defaults. Observed on the
/// request the SDK actually sends, through a stub HttpClient.
/// </summary>
public sealed class S3CustomEndpointUploadTests
{
    private sealed record Sent(IReadOnlyDictionary<string, string> Headers, byte[] Body);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            if (request.Content is not null)
            {
                foreach (var h in request.Content.Headers)
                {
                    headers[h.Key.ToLowerInvariant()] = string.Join(",", h.Value);
                }
            }

            Requests.Add(new Sent(headers, body));
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc\"");
            return response;
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(handler);

        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    }

    private static async Task<Sent> PutAsync(string? serviceUrl, Stream content)
    {
        var handler = new CapturingHandler();
        using var provider = new S3StorageProvider(
            S3StorageProvider.BuildClient(
                new S3StorageOptions
                {
                    BucketName = "private-bucket",
                    ServiceUrl = serviceUrl is null ? null : new Uri(serviceUrl),
                    Region = "us-east-1",
                    AccessKey = "test-access-key",
                    SecretKey = "test-secret-key",
                },
                new StubFactory(handler)),
            "private-bucket");

        await provider.PutAsync("a.txt", content, new StoragePutOptions { ContentType = "text/plain" });

        return Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("https://account.r2.cloudflarestorage.com")]
    [InlineData("http://localhost:3900")]
    public async Task A_custom_endpoint_sends_one_plain_body_with_no_trailer_or_chunk_framing(string serviceUrl)
    {
        var payload = "hello"u8.ToArray();

        var sent = await PutAsync(serviceUrl, new MemoryStream(payload));

        Assert.Equal(payload, sent.Body);
        Assert.Equal("5", sent.Headers["content-length"]);
        Assert.DoesNotContain("x-amz-trailer", sent.Headers.Keys);
        Assert.DoesNotContain("content-encoding", sent.Headers.Keys);
        Assert.DoesNotContain("x-amz-decoded-content-length", sent.Headers.Keys);
        Assert.False(sent.Headers["x-amz-content-sha256"].StartsWith("STREAMING", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_custom_https_endpoint_does_not_sign_the_payload()
    {
        var sent = await PutAsync("https://account.r2.cloudflarestorage.com", new MemoryStream("hello"u8.ToArray()));

        Assert.Equal("UNSIGNED-PAYLOAD", sent.Headers["x-amz-content-sha256"]);
    }

    [Fact]
    public async Task A_stream_that_cannot_seek_is_buffered_so_content_length_is_known()
    {
        var payload = "hello"u8.ToArray();

        var sent = await PutAsync("https://account.r2.cloudflarestorage.com", new NonSeekableStream(payload));

        Assert.Equal(payload, sent.Body);
        Assert.Equal("5", sent.Headers["content-length"]);
    }

    [Fact]
    public async Task Aws_itself_keeps_the_sdk_default_framing()
    {
        var sent = await PutAsync(null, new MemoryStream("hello"u8.ToArray()));

        Assert.StartsWith("STREAMING", sent.Headers["x-amz-content-sha256"], StringComparison.Ordinal);
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
