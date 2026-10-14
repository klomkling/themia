namespace Themia.ETax;

/// <summary>One recorded ORIGINAL render of a document.</summary>
/// <param name="PdfSha256">Hash of the normalised PDF.</param>
/// <param name="RendererVersion">The <see cref="TaxDocumentRender.RendererVersion"/> that produced it.</param>
public readonly record struct TaxDocumentRenderEvidence(string PdfSha256, string RendererVersion);

/// <summary>What a new ORIGINAL render means against the recorded history.</summary>
public enum TaxDocumentEvidenceOutcome
{
    /// <summary>No ORIGINAL has been rendered before.</summary>
    FirstRender = 0,

    /// <summary>The same renderer version produced the same bytes: safe to resend.</summary>
    Identical = 1,

    /// <summary>No earlier render used this renderer version: record the new evidence (and warn), then resend.</summary>
    NewRendererVersion = 2,
}

/// <summary>Thrown when the same renderer version produced different bytes for the same document. Carries versions only, never document content.</summary>
public sealed class TaxDocumentRenderMismatchException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="rendererVersion">The renderer version whose output changed.</param>
    public TaxDocumentRenderMismatchException(string rendererVersion)
        : base($"Renderer version '{rendererVersion}' produced different bytes for the same document than it recorded earlier.")
    {
        RendererVersion = rendererVersion;
    }

    /// <summary>The renderer version whose output changed.</summary>
    public string RendererVersion { get; }
}

/// <summary>The resend rule for ORIGINAL renders: same renderer version and a different hash fails loud, with no exceptions.</summary>
public static class TaxDocumentEvidence
{
    /// <summary>Compares a fresh ORIGINAL render with the recorded history.</summary>
    /// <param name="history">Every earlier ORIGINAL render of this document.</param>
    /// <param name="render">The render just produced.</param>
    /// <exception cref="TaxDocumentRenderMismatchException">A row with the same renderer version has a different hash.</exception>
    public static TaxDocumentEvidenceOutcome Check(IReadOnlyList<TaxDocumentRenderEvidence> history, TaxDocumentRender render)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(render);

        if (history.Count == 0)
            return TaxDocumentEvidenceOutcome.FirstRender;

        var sameVersion = history.Where(r => r.RendererVersion == render.RendererVersion).ToList();
        if (sameVersion.Count == 0)
            return TaxDocumentEvidenceOutcome.NewRendererVersion;

        if (sameVersion.Any(r => !string.Equals(r.PdfSha256, render.PdfSha256, StringComparison.OrdinalIgnoreCase)))
            throw new TaxDocumentRenderMismatchException(render.RendererVersion);

        return TaxDocumentEvidenceOutcome.Identical;
    }
}
