using System.Text;
using Themia.Storage;
using Themia.Storage.Local;
using Xunit;

namespace Themia.Storage.Tests;

public sealed class LocalStorageProviderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "themia-storage-tests", Guid.NewGuid().ToString("N"));

    private LocalStorageProvider NewProvider() =>
        new(new LocalStorageOptions { RootPath = root, SigningKey = "test-signing-key-please-change" });

    private static MemoryStream Bytes(string s) => new(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task Put_then_get_round_trips_content_and_type()
    {
        var provider = NewProvider();
        await provider.PutAsync("a/b.txt", Bytes("hello"), new StoragePutOptions("text/plain"));

        var read = await provider.GetAsync("a/b.txt");

        Assert.NotNull(read);
        Assert.Equal("text/plain", read!.ContentType);
        Assert.Equal(5, read.Length);
        using var reader = new StreamReader(read.Content);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Get_absent_key_returns_null()
    {
        Assert.Null(await NewProvider().GetAsync("missing.txt"));
    }

    [Fact]
    public async Task Exists_reflects_presence()
    {
        var provider = NewProvider();
        Assert.False(await provider.ExistsAsync("x.txt"));
        await provider.PutAsync("x.txt", Bytes("y"), new StoragePutOptions("text/plain"));
        Assert.True(await provider.ExistsAsync("x.txt"));
    }

    [Fact]
    public async Task Delete_is_idempotent()
    {
        var provider = NewProvider();
        await provider.PutAsync("x.txt", Bytes("y"), new StoragePutOptions("text/plain"));
        await provider.DeleteAsync("x.txt");
        await provider.DeleteAsync("x.txt"); // must not throw
        Assert.False(await provider.ExistsAsync("x.txt"));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("/abs.txt")]
    public async Task Traversal_keys_are_rejected(string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => NewProvider().PutAsync(key, Bytes("x"), new StoragePutOptions("text/plain")));
    }

    [Fact]
    public async Task Failed_overwrite_leaves_original_intact()
    {
        var provider = NewProvider();
        await provider.PutAsync("k", Bytes("v1"), new StoragePutOptions("text/plain"));

        // A stream that yields a few bytes then throws partway through the copy.
        var faulty = new ThrowingStream(Encoding.UTF8.GetBytes("garbage-that-will-not-be-fully-copied"), throwAfter: 4);
        await Assert.ThrowsAnyAsync<Exception>(
            () => provider.PutAsync("k", faulty, new StoragePutOptions("text/plain", Overwrite: true)));

        var read = await provider.GetAsync("k");
        Assert.NotNull(read);
        using var reader = new StreamReader(read!.Content);
        Assert.Equal("v1", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Key_named_like_a_sidecar_does_not_collide()
    {
        var provider = NewProvider();
        await provider.PutAsync("foo", Bytes("real"), new StoragePutOptions("text/plain"));
        await provider.PutAsync("foo.contenttype", Bytes("text/html"), new StoragePutOptions("text/html"));

        var read = await provider.GetAsync("foo");
        Assert.NotNull(read);
        Assert.Equal("text/plain", read!.ContentType);
        using var reader = new StreamReader(read.Content);
        Assert.Equal("real", await reader.ReadToEndAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    // A read-only stream that returns a few bytes then throws, to simulate a mid-copy failure.
    private sealed class ThrowingStream(byte[] data, int throwAfter) : Stream
    {
        private int position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (position >= throwAfter) throw new IOException("Simulated mid-copy failure.");
            var n = Math.Min(buffer.Length, Math.Min(throwAfter, data.Length) - position);
            data.AsSpan(position, n).CopyTo(buffer.Span);
            position += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
