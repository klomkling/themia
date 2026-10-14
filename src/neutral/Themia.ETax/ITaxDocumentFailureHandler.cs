namespace Themia.ETax;

/// <summary>A document that automation gave up on. Ids and codes only: no names, tax ids or addresses.</summary>
/// <param name="DocumentId">The document id.</param>
/// <param name="Kind">The kind.</param>
/// <param name="ErrorCode">The code of the error that ended it.</param>
/// <param name="Attempts">Attempts made.</param>
public sealed record TaxDocumentFailure(Guid DocumentId, TaxDocumentKind Kind, string ErrorCode, int Attempts);

/// <summary>Alerting hook, called once when a document enters <see cref="TaxDocumentState.Failed"/>.</summary>
public interface ITaxDocumentFailureHandler
{
    /// <summary>Raises the alert. An exception thrown here is logged (type only) and does not undo the failure.</summary>
    /// <param name="failure">What failed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task OnFailedAsync(TaxDocumentFailure failure, CancellationToken ct = default);
}
