using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Themia.Conversations.AspNetCore.Internal;

/// <summary>The error responses of the endpoints: RFC 7807 bodies with a stable <c>code</c>, built by hand so two refusals of one kind are byte-identical.</summary>
internal static class Problems
{
    public const string CsrfRequired = "csrf_header_required";
    public const string Unauthenticated = "unauthenticated";
    public const string NotFound = "not_found";
    public const string Closed = "conversation_closed";
    public const string InvalidMessage = "invalid_message";
    public const string AttachmentRejected = "attachment_rejected";
    public const string QuotaExceeded = "quota_exceeded";
    public const string RateLimited = "rate_limited";
    public const string PayloadTooLarge = "payload_too_large";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string InvalidBody = "invalid_body";
    public const string InvalidClientMessageId = "invalid_client_message_id";
    public const string InvalidAfterSeq = "invalid_after_seq";
    public const string InvalidLimit = "invalid_limit";
    public const string InvalidSeq = "invalid_seq";
    public const string TooManyFiles = "too_many_files";

    private const string ProblemContentType = "application/problem+json";

    public static IResult Of(int status, string code, string title)
    {
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        return Results.Json(problem, statusCode: status, contentType: ProblemContentType);
    }

    public static IResult Missing() => Of(StatusCodes.Status404NotFound, NotFound, "Not found.");

    public static IResult Validation(string code, string title) => Of(StatusCodes.Status422UnprocessableEntity, code, title);
}
