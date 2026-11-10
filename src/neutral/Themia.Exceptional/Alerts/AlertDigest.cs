namespace Themia.Exceptional.Alerts;

/// <summary>One rendered digest message and how many of the offered groups fit into it.</summary>
public sealed class AlertDigest
{
    /// <summary>Creates a digest.</summary>
    /// <param name="text">Plain-text message body.</param>
    /// <param name="includedCount">How many leading candidates are in <paramref name="text"/>.</param>
    public AlertDigest(string text, int includedCount)
    {
        Text = text;
        IncludedCount = includedCount;
    }

    /// <summary>Plain-text message body.</summary>
    public string Text { get; }

    /// <summary>How many leading candidates are in <see cref="Text"/>.</summary>
    public int IncludedCount { get; }
}
