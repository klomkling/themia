using System.Text;
using Microsoft.AspNetCore.Http;

namespace Themia.Exceptional.Middleware;

/// <summary>
/// Optionally buffers a size-capped copy of the request body into <see cref="HttpContext.Items"/> for later
/// attachment to a stored exception. Off by default; never captures Authorization/Cookie (those live in headers,
/// not the body), though the body itself may contain secrets — see <see cref="ExceptionalOptions.CaptureRequestBody"/>.
/// The body stream is rewound so downstream handlers read it normally.
/// </summary>
public sealed class RequestBodyLoggingMiddleware
{
    /// <summary>Key under which the captured body string is stored in <see cref="HttpContext.Items"/>.</summary>
    public const string BodyItemKey = "Themia.Exceptional.RequestBody";

    private readonly RequestDelegate next;
    private readonly ExceptionalOptions options;

    /// <summary>Creates the middleware.</summary>
    public RequestBodyLoggingMiddleware(RequestDelegate next, ExceptionalOptions options)
    {
        this.next = next;
        this.options = options;
    }

    /// <summary>Buffers the body when capture is enabled, then invokes the next middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!options.CaptureRequestBody || context.Request.ContentLength is null or 0)
        {
            await next(context);
            return;
        }

        context.Request.EnableBuffering();
        var limit = options.MaxBodyBytes;
        var size = context.Request.ContentLength is long len && len < limit ? (int)len : limit;
        var buffer = new byte[size];
        var read = await ReadUpTo(context.Request.Body, buffer);
        context.Request.Body.Position = 0;
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        if (context.Request.ContentLength is long total && total > read)
            text += "…[truncated]";
        context.Items[BodyItemKey] = text;

        await next(context);
    }

    private static async Task<int> ReadUpTo(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total));
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
