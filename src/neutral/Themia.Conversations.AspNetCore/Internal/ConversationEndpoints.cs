using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Themia.Conversations.AspNetCore.Internal;

internal static class ConversationEndpoints
{
    private const int DefaultListLimit = 50;
    private const int MaxClientMessageIdLength = 64;

    public static void Map(RouteGroupBuilder group, bool guestExchange)
    {
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var headers = invocation.HttpContext.Response.Headers;
            headers.CacheControl = "no-store";
            headers["Referrer-Policy"] = "no-referrer";
            headers.XContentTypeOptions = "nosniff";
            return await next(invocation);
        });

        if (guestExchange)
        {
            group.MapPost("/guest/exchange", (Func<HttpContext, Task<IResult>>)ExchangeAsync);
        }

        group.MapGet("/conversations/{id:guid}/messages", ListAsync);
        group.MapPost("/conversations/{id:guid}/messages", PostAsync);
        group.MapPost("/conversations/{id:guid}/read", ReadAsync);
    }

    private static async Task<IResult> ExchangeAsync(HttpContext context)
    {
        if (RequestGate.RequireCsrfHeader(context) is { } csrf)
        {
            return csrf;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<ConversationHttpOptions>>().Value;
        var (body, failure) = await JsonBody.ReadAsync<ExchangeRequest>(context, options.MaxJsonBodyBytes);
        if (failure is not null)
        {
            return failure;
        }

        if (!GuestCookie.IsBoundedTenant(body!.TenantId) || string.IsNullOrWhiteSpace(body.Token))
        {
            return Problems.Validation(Problems.InvalidBody, "tenantId and token are required.");
        }

        var result = await context.RequestServices.GetRequiredService<IGuestAccess>().ExchangeAsync(body.TenantId!, body.Token!, context.RequestAborted);
        if (!result.Succeeded || result.ConversationId is not { } conversationId || result.SessionKey is null || result.ExpiresAt is not { } expiresAt)
        {
            return Problems.Missing();
        }

        GuestCookie.Write(context, conversationId, body.TenantId!, result.SessionKey.Reveal(), expiresAt);
        return Envelope(new { conversationId, expiresAt });
    }

    private static async Task<IResult> ListAsync(HttpContext context, Guid id)
    {
        var (caller, failure) = await RequestGate.ResolveAsync(context, id);
        if (failure is not null)
        {
            return failure;
        }

        var conversation = context.RequestServices.GetRequiredService<IOptions<ConversationOptions>>().Value;
        if (!TryReadLong(context, "afterSeq", 0, out var afterSeq, min: 0))
        {
            return Problems.Validation(Problems.InvalidAfterSeq, "afterSeq must be a number of 0 or more.");
        }

        if (!TryReadLong(context, "limit", Math.Min(DefaultListLimit, conversation.MaxListLimit), out var limit, min: 1) || limit > conversation.MaxListLimit)
        {
            return Problems.Validation(Problems.InvalidLimit, $"limit must be between 1 and {conversation.MaxListLimit}.");
        }

        var page = await context.RequestServices.GetRequiredService<IConversationService>()
            .ListAsync(caller!.Actor, caller.TenantId, id, afterSeq, (int)limit, context.RequestAborted);
        if (page.Outcome != ListOutcome.Listed)
        {
            return Problems.Missing();
        }

        var messages = page.Messages.Select(m => new
        {
            id = m.Id,
            seq = m.Seq,
            participantId = m.ParticipantId,
            kind = m.Kind.ToString(),
            body = m.Body,
            createdAt = m.CreatedAt,
            isRedacted = m.IsRedacted,
            senderLabel = m.SenderLabel,
            attachments = m.Attachments.Select(a => new { id = a.Id, fileName = a.FileName, contentType = a.ContentType, size = a.Size }),
        });
        return Envelope(messages, new { count = page.Messages.Count });
    }

    private static async Task<IResult> PostAsync(HttpContext context, Guid id)
    {
        if (RequestGate.RequireCsrfHeader(context) is { } csrf)
        {
            return csrf;
        }

        var (caller, failure) = await RequestGate.ResolveAsync(context, id);
        if (failure is not null)
        {
            return failure;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<ConversationHttpOptions>>().Value;
        var (body, bodyFailure) = await JsonBody.ReadAsync<PostRequest>(context, options.MaxJsonBodyBytes);
        if (bodyFailure is not null)
        {
            return bodyFailure;
        }

        if (!IsClientMessageId(body!.ClientMessageId))
        {
            return Problems.Validation(Problems.InvalidClientMessageId, $"clientMessageId must be 1 to {MaxClientMessageIdLength} characters without surrounding whitespace.");
        }

        var result = await context.RequestServices.GetRequiredService<IConversationService>()
            .PostAsync(caller!.Actor, caller.TenantId, id, new PostMessage(body.ClientMessageId!, body.Body ?? string.Empty), context.RequestAborted);
        return PostOutcomeResult(result);
    }

    private static async Task<IResult> ReadAsync(HttpContext context, Guid id)
    {
        if (RequestGate.RequireCsrfHeader(context) is { } csrf)
        {
            return csrf;
        }

        var (caller, failure) = await RequestGate.ResolveAsync(context, id);
        if (failure is not null)
        {
            return failure;
        }

        var options = context.RequestServices.GetRequiredService<IOptions<ConversationHttpOptions>>().Value;
        var (body, bodyFailure) = await JsonBody.ReadAsync<ReadRequest>(context, options.MaxJsonBodyBytes);
        if (bodyFailure is not null)
        {
            return bodyFailure;
        }

        if (body!.Seq is not { } seq || seq < 0)
        {
            return Problems.Validation(Problems.InvalidSeq, "seq must be a number of 0 or more.");
        }

        var result = await context.RequestServices.GetRequiredService<IConversationService>()
            .MarkReadAsync(caller!.Actor, caller.TenantId, id, seq, context.RequestAborted);
        return result.Outcome == MarkReadOutcome.Marked ? Envelope(new { lastReadSeq = result.LastReadSeq }) : Problems.Missing();
    }

    internal static IResult PostOutcomeResult(PostResult result) => result.Outcome switch
    {
        PostOutcome.Posted => Results.Json(new { data = new { messageId = result.MessageId, seq = result.Seq }, meta = (object?)null }, statusCode: StatusCodes.Status201Created),
        PostOutcome.Duplicate => Envelope(new { messageId = result.MessageId, seq = result.Seq }),
        PostOutcome.Closed => Problems.Of(StatusCodes.Status409Conflict, Problems.Closed, "The conversation is closed."),
        PostOutcome.InvalidMessage => Problems.Validation(Problems.InvalidMessage, "The message is not valid."),
        PostOutcome.AttachmentRejected => Problems.Validation(Problems.AttachmentRejected, "An attachment was rejected."),
        PostOutcome.QuotaExceeded => Problems.Of(StatusCodes.Status413PayloadTooLarge, Problems.QuotaExceeded, "The conversation's attachment quota is used up."),
        _ => Problems.Missing(),
    };

    private static IResult Envelope(object data, object? meta = null) => Results.Json(new { data, meta });

    private static bool IsClientMessageId(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxClientMessageIdLength && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static bool TryReadLong(HttpContext context, string name, long fallback, out long value, long min)
    {
        value = fallback;
        if (!context.Request.Query.TryGetValue(name, out var raw) || raw.Count == 0)
        {
            return true;
        }

        return raw.Count == 1 && long.TryParse(raw[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value) && value >= min;
    }

    private sealed record ExchangeRequest(string? TenantId, string? Token);

    private sealed record PostRequest(string? ClientMessageId, string? Body);

    private sealed record ReadRequest(long? Seq);
}
