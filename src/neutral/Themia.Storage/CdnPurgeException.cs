namespace Themia.Storage;

/// <summary>
/// The object was deleted from storage but the CDN edge may still serve it. It means exactly that: a failed
/// delete throws whatever the inner provider throws. Recover by repeating the delete and purge; delete is
/// idempotent.
/// </summary>
public sealed class CdnPurgeException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="url">The public URL that could not be purged.</param>
    /// <param name="message">What happened, including that the object is already deleted.</param>
    /// <param name="httpStatus">The HTTP status the CDN answered with, or <see langword="null"/> when there was no usable answer.</param>
    /// <param name="innerException">The transport failure, when there was one.</param>
    public CdnPurgeException(Uri url, string message, int? httpStatus = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Url = url;
        HttpStatus = httpStatus;
    }

    /// <summary>The public URL that could not be purged.</summary>
    public Uri Url { get; }

    /// <summary>The HTTP status the CDN answered with; <see langword="null"/> for a transport failure or no answer.</summary>
    public int? HttpStatus { get; }
}
