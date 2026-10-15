namespace Themia.ETax.Withholding;

/// <summary>
/// Which of the two copies the official certificate lists a rendering is. Chosen when rendering, never stored: the same stored certificate
/// prints as either copy.
/// </summary>
public enum WithholdingCopy
{
    /// <summary>ฉบับที่ 1: for the person withheld from, to attach to their tax return.</summary>
    Copy1 = 1,

    /// <summary>ฉบับที่ 2: for the person withheld from, to keep as evidence.</summary>
    Copy2 = 2,
}
