using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace Themia.Conversations.AspNetCore.Internal;

/// <summary>A parsed multipart post: the text fields and the file parts, read only after the gate and the size cap.</summary>
internal sealed record MultipartPost(string? ClientMessageId, string? Body, IReadOnlyList<IFormFile> Files);

/// <summary>
/// Reads <c>multipart/form-data</c> under a hard byte cap that does not depend on the server: the stream is wrapped, so Kestrel, TestServer and any other host
/// reject an oversized or chunked body the same way, and the form limits are set explicitly rather than inherited.
/// </summary>
internal static class MultipartBody
{
    public const string ClientMessageIdField = "clientMessageId";
    public const string BodyField = "body";
    public const string FilesField = "files";
    private const int FieldCountLimit = 4;
    private const int HeaderCountLimit = 16;

    public static bool IsMultipart(HttpRequest request) =>
        MediaTypeHeaderValue.TryParse(request.ContentType, out var type) && type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase);

    public static async Task<(MultipartPost? Post, IResult? Failure)> ReadAsync(HttpContext context, long maxBytes, int maxFiles)
    {
        if (context.Request.ContentLength > maxBytes)
        {
            return (null, Problems.Of(StatusCodes.Status413PayloadTooLarge, Problems.PayloadTooLarge, "The request body is too large."));
        }

        context.Request.Body = new CappedStream(context.Request.Body, maxBytes);
        var features = context.Features;
        features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions
        {
            ValueCountLimit = FieldCountLimit + maxFiles,
            MultipartHeadersCountLimit = HeaderCountLimit,
            MultipartBodyLengthLimit = maxBytes,
            BufferBodyLengthLimit = maxBytes,
        }));

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (Exception ex) when (ex is BodyTooLargeException || ex.InnerException is BodyTooLargeException)
        {
            return (null, Problems.Of(StatusCodes.Status413PayloadTooLarge, Problems.PayloadTooLarge, "The request body is too large."));
        }
        catch (Exception ex) when (!context.RequestAborted.IsCancellationRequested && ex is InvalidDataException or InvalidOperationException or BadHttpRequestException or IOException)
        {
            return (null, Problems.Validation(Problems.InvalidBody, "The multipart body is not valid."));
        }

        if (form.Keys.Any(k => k is not (ClientMessageIdField or BodyField)) || form.Files.Any(f => f.Name != FilesField))
        {
            return (null, Problems.Validation(Problems.InvalidBody, "The multipart body has an unknown field."));
        }

        if (form.Files.Count > maxFiles)
        {
            return (null, Problems.Validation(Problems.TooManyFiles, $"At most {maxFiles} files are allowed."));
        }

        var post = new MultipartPost(Single(form, ClientMessageIdField), Single(form, BodyField), form.Files.ToList());
        return form[ClientMessageIdField].Count > 1 || form[BodyField].Count > 1
            ? (null, Problems.Validation(Problems.InvalidBody, "A text field was repeated."))
            : (post, null);
    }

    private static string? Single(IFormCollection form, string name) => form.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    internal sealed class BodyTooLargeException() : IOException("The request body is over the cap.");

    private sealed class CappedStream(Stream inner, long max) : Stream
    {
        private long total;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            total += read;
            return total > max ? throw new BodyTooLargeException() : read;
        }
    }
}
