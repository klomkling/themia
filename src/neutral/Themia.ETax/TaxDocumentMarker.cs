namespace Themia.ETax;

/// <summary>The mark a rendered document carries. ORIGINAL is printed only on the send path; every other render is a copy.</summary>
public enum TaxDocumentMarker
{
    /// <summary>ต้นฉบับ: the one e-Tax original, rendered only while the document is waiting to be sent.</summary>
    Original = 0,

    /// <summary>สำเนา: any later render, such as a download.</summary>
    Copy = 1,

    /// <summary>A copy of a cancelled document, with a CANCELLED (ยกเลิก) watermark.</summary>
    CancelledCopy = 2,
}
