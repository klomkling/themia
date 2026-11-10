using System.Text.Json;
using Themia.Exceptional.Masking;

namespace Themia.Exceptional;

/// <summary>Builds an <see cref="ExceptionEntry"/> from a captured <see cref="Exception"/>.</summary>
public static class ExceptionEntryFactory
{
    private const int MessageMax = 1000;
    private const int SourceMax = 500;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Creates an entry for <paramref name="exception"/> stamped with <paramref name="applicationName"/>.</summary>
    public static ExceptionEntry FromException(Exception exception, string applicationName, DateTime? utcNow = null)
        => FromException(exception, applicationName, utcNow, maskCredentialValues: true);

    internal static ExceptionEntry FromException(
        Exception exception, string applicationName, DateTime? utcNow, bool maskCredentialValues)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        var now = utcNow ?? DateTime.UtcNow;
        var type = exception.GetType().FullName ?? exception.GetType().Name;
        var mask = maskCredentialValues ? (Func<string?, string?>)CredentialShapeMasker.Mask : static v => v;
        var message = mask(exception.Message ?? string.Empty)!;

        return new ExceptionEntry
        {
            Guid = Guid.NewGuid(),
            ApplicationName = applicationName,
            MachineName = Environment.MachineName,
            Type = type,
            Source = exception.Source.Truncate(SourceMax),
            Message = message.Truncate(MessageMax)!,
            Detail = Serialize(exception, message, maskCredentialValues, mask),
            ErrorHash = ExceptionHash.Compute(type, exception.StackTrace ?? $"{exception.Source}|{message}|{exception.InnerException?.GetType().FullName}"),
            DuplicateCount = 1,
            CreationDate = now,
            LastLogDate = now,
        };
    }

    private static string Serialize(Exception exception, string message, bool maskCredentialValues, Func<string?, string?> mask)
    {
        var payload = new
        {
            Message = message,
            Type = exception.GetType().FullName,
            exception.Source,
            StackTrace = mask(exception.StackTrace),
            Inner = mask(exception.InnerException?.ToString()),
            Data = exception.Data.Count > 0 ? ToStringMap(exception.Data, maskCredentialValues, mask) : null,
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static Dictionary<string, string?> ToStringMap(
        System.Collections.IDictionary data, bool maskCredentialValues, Func<string?, string?> mask)
    {
        var map = new Dictionary<string, string?>(data.Count);
        foreach (System.Collections.DictionaryEntry e in data)
        {
            var key = e.Key.ToString() ?? string.Empty;
            var value = e.Value?.ToString();
            map[key] = maskCredentialValues && value is not null
                ? ExceptionalOptions.DefaultRedactor(key, value)
                : mask(value);
        }
        return map;
    }
}
