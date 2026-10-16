using System.Text.Json.Serialization;

namespace Themia.ETax.Withholding;

/// <summary>
/// A numbered line of the income table of the official certificate (หนังสือรับรองการหักภาษี ณ ที่จ่าย, form 50 ทวิ). Headings that carry no
/// amount (4(ข), (1), (2)) are not rows. Persisted by name, so renaming a member is a breaking change.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WithholdingFormRow>))]
public enum WithholdingFormRow
{
    /// <summary>1. เงินเดือน ค่าจ้าง เบี้ยเลี้ยง โบนัส ฯลฯ ตามมาตรา 40 (1).</summary>
    Salary40_1 = 1,

    /// <summary>2. ค่าธรรมเนียม ค่านายหน้า ฯลฯ ตามมาตรา 40 (2).</summary>
    Fees40_2 = 2,

    /// <summary>3. ค่าแห่งลิขสิทธิ์ ฯลฯ ตามมาตรา 40 (3).</summary>
    Royalty40_3 = 3,

    /// <summary>4. (ก) ดอกเบี้ย ฯลฯ ตามมาตรา 40 (4) (ก).</summary>
    Interest40_4a = 4,

    /// <summary>4. (ข) (1.1) dividend with tax credit, paid from profit taxed at 30%.</summary>
    DividendCredit30 = 5,

    /// <summary>4. (ข) (1.2) dividend with tax credit, paid from profit taxed at 25%.</summary>
    DividendCredit25 = 6,

    /// <summary>4. (ข) (1.3) dividend with tax credit, paid from profit taxed at 20%.</summary>
    DividendCredit20 = 7,

    /// <summary>4. (ข) (1.4) dividend with tax credit, other rate (to be specified).</summary>
    DividendCreditOtherRate = 8,

    /// <summary>4. (ข) (2.1) dividend without credit: profit of a business exempt from corporate income tax.</summary>
    DividendNoCreditExemptProfit = 9,

    /// <summary>4. (ข) (2.2) dividend without credit: dividend or profit share exempt from inclusion in corporate taxable income.</summary>
    DividendNoCreditExemptShare = 10,

    /// <summary>4. (ข) (2.3) dividend without credit: net profit after deducting net loss carried forward (up to 5 years).</summary>
    DividendNoCreditLossCarriedForward = 11,

    /// <summary>4. (ข) (2.4) dividend without credit: profit recognised under the equity method.</summary>
    DividendNoCreditEquityMethod = 12,

    /// <summary>4. (ข) (2.5) dividend without credit: other (to be specified).</summary>
    DividendNoCreditOther = 13,

    /// <summary>5. payments under a Revenue Department order issued under section 3 tredecim (promotion prizes, contests, performers, contract work, advertising, rent, transport, services, non-life premiums ...).</summary>
    Section3Tredecim = 14,

    /// <summary>6. อื่นๆ (ระบุ): other income (to be specified).</summary>
    Other = 15,
}
