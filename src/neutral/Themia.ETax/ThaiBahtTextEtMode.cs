namespace Themia.ETax;

/// <summary>When <see cref="ThaiBahtText"/> reads a units digit of 1 as "เอ็ด" rather than "หนึ่ง". Both conventions are in use, so the adopter chooses.</summary>
public enum ThaiBahtTextEtMode
{
    /// <summary>"เอ็ด" for a 1 in the units place of any group that has something before it: หนึ่งร้อยเอ็ด, หนึ่งพันเอ็ด, หนึ่งล้านเอ็ด, หนึ่งพันเอ็ดล้าน. The default.</summary>
    Always = 0,

    /// <summary>"เอ็ด" only after a tens digit (สิบเอ็ด to เก้าสิบเอ็ด): หนึ่งร้อยหนึ่ง, หนึ่งพันหนึ่ง, หนึ่งล้านหนึ่ง, หนึ่งพันหนึ่งล้าน.</summary>
    TensOnly = 1,
}
