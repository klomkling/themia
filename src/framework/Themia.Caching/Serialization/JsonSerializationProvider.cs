using System.Text.Json;
using System.Text.Json.Serialization;

namespace Themia.Caching;

/// <summary>
/// System.Text.Json-based serialization provider with configurable options.
/// Thread-safe as options are immutable after construction.
/// </summary>
public sealed class JsonSerializationProvider : ISerializationProvider
{
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonSerializationProvider"/> class.
    /// </summary>
    /// <param name="options">JSON serialization options. If null, default options are used.</param>
    public JsonSerializationProvider(JsonSerializationOptions? options = null)
    {
        _options = options is not null ? CreateOptions(options) : CreateDefaultOptions();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonSerializationProvider"/> class.
    /// </summary>
    /// <param name="options">System.Text.Json serializer options.</param>
    public JsonSerializationProvider(JsonSerializerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public byte[] Serialize<T>(T value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        try
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, _options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to serialize type {typeof(T).FullName} via System.Text.Json.", ex);
        }
    }

    /// <inheritdoc />
    public T? Deserialize<T>(byte[] data)
    {
        if (data is null || data.Length == 0)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(data, _options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize JSON payload to {typeof(T).FullName}.", ex);
        }
    }

    private static JsonSerializerOptions CreateDefaultOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    private static JsonSerializerOptions CreateOptions(JsonSerializationOptions options)
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = options.WriteIndented,
            PropertyNameCaseInsensitive = options.PropertyNameCaseInsensitive,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }
}
