using System.Text.Json;

namespace Themia.Exceptional;

/// <summary>Builds an <see cref="ExceptionEntry"/> from a captured <see cref="Exception"/>.</summary>
public static class ExceptionEntryFactory
{
    private const int MessageMax = 1000;
    private const int SourceMax = 500;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Creates an entry for <paramref name="exception"/> stamped with <paramref name="applicationName"/>.</summary>
    public static ExceptionEntry FromException(Exception exception, string applicationName, DateTime? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        var now = utcNow ?? DateTime.UtcNow;
        var type = exception.GetType().FullName ?? exception.GetType().Name;

        return new ExceptionEntry
        {
            Guid = Guid.NewGuid(),
            ApplicationName = applicationName,
            MachineName = Environment.MachineName,
            Type = type,
            Source = exception.Source.Truncate(SourceMax),
            Message = (exception.Message ?? string.Empty).Truncate(MessageMax)!,
            Detail = Serialize(exception),
            ErrorHash = ExceptionHash.Compute(type, exception.StackTrace ?? exception.Message),
            DuplicateCount = 1,
            CreationDate = now,
            LastLogDate = now,
        };
    }

    private static string Serialize(Exception exception)
    {
        var payload = new
        {
            exception.Message,
            Type = exception.GetType().FullName,
            exception.Source,
            exception.StackTrace,
            Inner = exception.InnerException?.ToString(),
            Data = exception.Data.Count > 0 ? ToStringMap(exception.Data) : null,
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static Dictionary<string, string?> ToStringMap(System.Collections.IDictionary data)
    {
        var map = new Dictionary<string, string?>(data.Count);
        foreach (System.Collections.DictionaryEntry e in data)
            map[e.Key.ToString() ?? string.Empty] = e.Value?.ToString();
        return map;
    }
}
