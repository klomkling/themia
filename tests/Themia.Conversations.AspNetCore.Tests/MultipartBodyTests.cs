using Microsoft.AspNetCore.Http;
using Themia.Conversations.AspNetCore.Internal;

namespace Themia.Conversations.AspNetCore.Tests;

public sealed class MultipartBodyTests
{
    private sealed class FailingStream(CancellationTokenSource? abortOnRead = null) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw Gone();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw Gone();
        private IOException Gone()
        {
            abortOnRead?.Cancel();
            return new IOException("The client went away.");
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static DefaultHttpContext ContextWith(CancellationTokenSource? aborted = null) => new()
    {
        RequestAborted = aborted?.Token ?? CancellationToken.None,
        Request = { ContentType = "multipart/form-data; boundary=x", Body = new FailingStream(aborted) },
    };

    [Fact]
    public async Task ReadAsync_ShouldAnswerAValidationFailure_WhenTheBodyIsBrokenAndTheClientIsStillThere()
    {
        var (post, failure) = await MultipartBody.ReadAsync(ContextWith(), maxBytes: 1024, maxFiles: 1);

        Assert.Null(post);
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task ReadAsync_ShouldLetTheExceptionThrough_WhenTheClientAborted()
    {
        using var aborted = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<Exception>(() => MultipartBody.ReadAsync(ContextWith(aborted), maxBytes: 1024, maxFiles: 1));
    }
}
