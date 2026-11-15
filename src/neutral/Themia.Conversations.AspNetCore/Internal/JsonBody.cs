using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Themia.Conversations.AspNetCore.Internal;

/// <summary>Reads a capped JSON body after the gate has passed. Handlers take the raw request, not a bound parameter, because binding would read the body first.</summary>
internal static class JsonBody
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public static async Task<(T? Value, IResult? Failure)> ReadAsync<T>(HttpContext context, int maxBytes)
        where T : class
    {
        if (!context.Request.HasJsonContentType())
        {
            return (null, Problems.Of(StatusCodes.Status415UnsupportedMediaType, Problems.UnsupportedMediaType, "The body must be application/json."));
        }

        if (context.Request.ContentLength > maxBytes)
        {
            return (null, TooLarge());
        }

        var buffer = new byte[(int)Math.Min(maxBytes + 1L, (context.Request.ContentLength ?? maxBytes) + 1L)];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(total), context.RequestAborted);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > maxBytes)
        {
            return (null, TooLarge());
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(buffer.AsSpan(0, total), Options);
            return value is null ? (null, Invalid()) : (value, null);
        }
        catch (JsonException)
        {
            return (null, Invalid());
        }
    }

    private static IResult TooLarge() => Problems.Of(StatusCodes.Status413PayloadTooLarge, Problems.PayloadTooLarge, "The request body is too large.");

    private static IResult Invalid() => Problems.Validation(Problems.InvalidBody, "The body is not valid for this endpoint.");
}
