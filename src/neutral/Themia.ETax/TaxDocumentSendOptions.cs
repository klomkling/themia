namespace Themia.ETax;

/// <summary>Options for <see cref="TaxDocumentSender"/>.</summary>
public sealed class TaxDocumentSendOptions
{
    /// <summary>The Revenue Department's receiving address for route A. Required; never hardcoded.</summary>
    public string RevenueDepartmentAddress { get; set; } = string.Empty;

    /// <summary>How long a claim lasts. Default 2 minutes.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long one mailer call may take before it is cancelled and counted as a transient <c>send_timeout</c>. Must be shorter than
    /// <see cref="Lease"/>: the lease is what stops a cancel from slipping in while the email goes out, so a send that outlives it
    /// could deliver a document that has since been cancelled. Default 60 seconds.
    /// </summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Failed attempts after which a document with only transient errors becomes Failed. Default 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>The backoff after the first failed attempt; it doubles each time. Default 30 seconds.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest backoff. Default 1 hour.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How many documents one <see cref="TaxDocumentSender.RunOnceAsync"/> claims. Default 10.</summary>
    public int BatchSize { get; set; } = 10;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(RevenueDepartmentAddress))
            throw new InvalidOperationException($"{nameof(TaxDocumentSendOptions)}.{nameof(RevenueDepartmentAddress)} must be configured.");
        if (Lease <= TimeSpan.Zero)
            throw new InvalidOperationException($"{nameof(TaxDocumentSendOptions)}.{nameof(Lease)} must be positive.");
        if (SendTimeout <= TimeSpan.Zero || SendTimeout >= Lease)
            throw new InvalidOperationException($"{nameof(TaxDocumentSendOptions)}.{nameof(SendTimeout)} must be positive and shorter than {nameof(Lease)}.");
        if (MaxAttempts < 1)
            throw new InvalidOperationException($"{nameof(TaxDocumentSendOptions)}.{nameof(MaxAttempts)} must be at least 1.");
        if (InitialBackoff <= TimeSpan.Zero || MaxBackoff < InitialBackoff)
            throw new InvalidOperationException($"{nameof(TaxDocumentSendOptions)}: backoff must be positive and MaxBackoff at least InitialBackoff.");
        if (BatchSize < 1)
            throw new InvalidOperationException($"{nameof(TaxDocumentSendOptions)}.{nameof(BatchSize)} must be at least 1.");
    }
}
