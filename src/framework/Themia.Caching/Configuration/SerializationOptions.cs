namespace Themia.Caching;

/// <summary>
/// Configuration options for serialization.
/// </summary>
public sealed class SerializationOptions
{
    /// <summary>
    /// Gets or sets the serialization provider type (MessagePack or Json).
    /// </summary>
    public string Provider { get; set; } = "MessagePack";
}

/// <summary>
/// Configuration options for JSON serialization.
/// </summary>
public sealed class JsonSerializationOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether to write indented JSON.
    /// </summary>
    public bool WriteIndented { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether property names are case-insensitive.
    /// </summary>
    public bool PropertyNameCaseInsensitive { get; set; } = true;
}
