namespace Themia.ETax;

/// <summary>
/// Checks a <see cref="TaxDocumentSnapshot"/> before it is issued. Runs on NEW input only: stored snapshots
/// are read through <see cref="TaxDocumentSnapshotSerializer"/> and never re-validated.
/// </summary>
public static class TaxDocumentSnapshotValidator
{
    /// <summary>Returns every failed check; an empty list means the snapshot may be issued.</summary>
    /// <param name="snapshot">The snapshot to check.</param>
    /// <param name="options">Validation options; defaults apply when <see langword="null"/>.</param>
    public static IReadOnlyList<TaxDocumentValidationError> Validate(
        TaxDocumentSnapshot snapshot, TaxDocumentValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var checksum = (options ?? new TaxDocumentValidationOptions()).ValidateTaxIdChecksum;
        var errors = new List<TaxDocumentValidationError>();

        ValidateHeader(snapshot, errors);
        ValidateParty(snapshot.Seller, "seller", isSeller: true, checksum, errors);
        ValidateParty(snapshot.Buyer, "buyer", isSeller: false, checksum, errors);
        ValidateLines(snapshot, errors);
        ValidateTotals(snapshot, errors);
        ValidateNote(snapshot, errors);
        ValidateReplacement(snapshot.Replacement, errors);
        ValidatePayment(snapshot, errors);
        ValidateLogo(snapshot, errors);
        ValidateCharacters(snapshot, errors);
        return errors;
    }

    private static void ValidateHeader(TaxDocumentSnapshot s, List<TaxDocumentValidationError> errors)
    {
        if (s.SchemaVersion != TaxDocumentSnapshot.CurrentSchemaVersion)
            Add(errors, TaxDocumentValidationCode.Malformed, "schema_version");
        if (!Enum.IsDefined(s.Kind))
            Add(errors, TaxDocumentValidationCode.Malformed, "kind");
        if (string.IsNullOrWhiteSpace(s.DocumentNumber))
            Add(errors, TaxDocumentValidationCode.Required, "document_number");
        if (s.IssueDate == default)
            Add(errors, TaxDocumentValidationCode.Required, "issue_date");
    }

    private static void ValidateParty(
        TaxParty? party, string path, bool isSeller, bool checksum, List<TaxDocumentValidationError> errors)
    {
        if (party is null)
        {
            Add(errors, TaxDocumentValidationCode.Required, path);
            return;
        }

        if (string.IsNullOrWhiteSpace(party.Name))
            Add(errors, TaxDocumentValidationCode.Required, $"{path}.name");

        if (party.TaxId is null)
        {
            if (isSeller)
                Add(errors, TaxDocumentValidationCode.Required, $"{path}.tax_id");
        }
        else if (!ThaiTaxId.IsWellFormed(party.TaxId))
        {
            Add(errors, TaxDocumentValidationCode.Malformed, $"{path}.tax_id");
        }
        else if (checksum && !ThaiTaxId.HasValidChecksum(party.TaxId))
        {
            Add(errors, TaxDocumentValidationCode.InvalidChecksum, $"{path}.tax_id");
        }

        if (party.Branch is null)
        {
            if (isSeller)
                Add(errors, TaxDocumentValidationCode.Required, $"{path}.branch");
        }
        else if (!ThaiBranchCode.IsWellFormed(party.Branch))
        {
            Add(errors, TaxDocumentValidationCode.Malformed, $"{path}.branch");
        }

        if (isSeller && string.IsNullOrWhiteSpace(party.Address))
            Add(errors, TaxDocumentValidationCode.Required, $"{path}.address");
    }

    private static void ValidateLines(TaxDocumentSnapshot s, List<TaxDocumentValidationError> errors)
    {
        if (s.Lines is not { Count: > 0 })
        {
            Add(errors, TaxDocumentValidationCode.Required, "lines");
            return;
        }

        for (var i = 0; i < s.Lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(s.Lines[i].Description))
                Add(errors, TaxDocumentValidationCode.Required, $"lines[{i}].description");
            if (s.Lines[i].AmountExVatSatang < 0)
                Add(errors, TaxDocumentValidationCode.Malformed, $"lines[{i}].amount_ex_vat_satang");
            ValidateQuantityAndPrice(s.Lines[i], i, errors);
            ValidateNoteValues(s, s.Lines[i], i, errors);
        }
    }

    private static void ValidateQuantityAndPrice(TaxDocumentLine line, int index, List<TaxDocumentValidationError> errors)
    {
        if (line.Quantity is null != line.UnitPriceSatang is null)
        {
            Add(errors, TaxDocumentValidationCode.Required, line.Quantity is null ? $"lines[{index}].quantity" : $"lines[{index}].unit_price_satang");
            return;
        }

        if (line.Quantity is <= 0)
            Add(errors, TaxDocumentValidationCode.Malformed, $"lines[{index}].quantity");
        if (line.UnitPriceSatang is < 0)
            Add(errors, TaxDocumentValidationCode.Malformed, $"lines[{index}].unit_price_satang");
    }

    // The original and correct value of a line mean something only against a referenced document, so a receipt may not carry them.
    private static void ValidateNoteValues(TaxDocumentSnapshot s, TaxDocumentLine line, int index, List<TaxDocumentValidationError> errors)
    {
        if (line.OriginalAmountExVatSatang is null && line.CorrectAmountExVatSatang is null)
            return;

        if (s.Kind is not (TaxDocumentKind.CreditNote or TaxDocumentKind.DebitNote))
        {
            Add(errors, TaxDocumentValidationCode.NotAllowed, $"lines[{index}].original_amount_ex_vat_satang");
            return;
        }

        if (line.OriginalAmountExVatSatang is null || line.CorrectAmountExVatSatang is null)
        {
            Add(errors, TaxDocumentValidationCode.Required,
                line.OriginalAmountExVatSatang is null ? $"lines[{index}].original_amount_ex_vat_satang" : $"lines[{index}].correct_amount_ex_vat_satang");
            return;
        }

        if (line.OriginalAmountExVatSatang < 0)
            Add(errors, TaxDocumentValidationCode.Malformed, $"lines[{index}].original_amount_ex_vat_satang");
        if (line.CorrectAmountExVatSatang < 0)
            Add(errors, TaxDocumentValidationCode.Malformed, $"lines[{index}].correct_amount_ex_vat_satang");
    }

    private static void ValidateLogo(TaxDocumentSnapshot s, List<TaxDocumentValidationError> errors)
    {
        if (s.LogoSha256 is { } sha && !(sha.Length == 64 && sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            Add(errors, TaxDocumentValidationCode.Malformed, "logo_sha256");
    }

    private static void ValidatePayment(TaxDocumentSnapshot s, List<TaxDocumentValidationError> errors)
    {
        if (s.Payment is null)
            return;

        if (s.Kind != TaxDocumentKind.TaxInvoiceReceipt)
            Add(errors, TaxDocumentValidationCode.NotAllowed, "payment");
        else if (!Enum.IsDefined(s.Payment.Method))
            Add(errors, TaxDocumentValidationCode.Malformed, "payment.method");
    }

    private static void ValidateTotals(TaxDocumentSnapshot s, List<TaxDocumentValidationError> errors)
    {
        var t = s.Totals;
        if (t is null)
        {
            Add(errors, TaxDocumentValidationCode.Required, "totals");
            return;
        }

        var rateOk = t.VatRateBasisPoints is >= 0 and <= VatMath.MaxRateBasisPoints;
        if (!rateOk)
            Add(errors, TaxDocumentValidationCode.Malformed, "totals.vat_rate_basis_points");
        var amountsOk = t.ExVatSatang >= 0 && t.VatSatang >= 0 && t.InclVatSatang >= 0 && t.WithheldSatang >= 0;
        if (!amountsOk)
            Add(errors, TaxDocumentValidationCode.Malformed, "totals");
        if (!rateOk || !amountsOk)
            return;

        try
        {
            ValidateTotalsArithmetic(s, t, errors);
        }
        catch (OverflowException)
        {
            // Amounts so large that the sums or the VAT derivation leave the range of a long: not a real document.
            Add(errors, TaxDocumentValidationCode.Malformed, "totals");
        }
    }

    private static void ValidateTotalsArithmetic(TaxDocumentSnapshot s, TaxDocumentTotals t, List<TaxDocumentValidationError> errors)
    {
        if (s.Lines is { Count: > 0 } && s.Lines.All(l => l.AmountExVatSatang >= 0)
            && s.Lines.Aggregate(0L, (sum, l) => checked(sum + l.AmountExVatSatang)) != t.ExVatSatang)
        {
            Add(errors, TaxDocumentValidationCode.TotalsInconsistent, "totals.ex_vat_satang");
        }

        if (checked(t.ExVatSatang + t.VatSatang) != t.InclVatSatang)
            Add(errors, TaxDocumentValidationCode.TotalsInconsistent, "totals.incl_vat_satang");
        else if (!MatchesVatMath(t))
            Add(errors, TaxDocumentValidationCode.TotalsInconsistent, "totals.vat_satang");

        if (t.WithheldSatang > t.InclVatSatang)
            Add(errors, TaxDocumentValidationCode.WithheldExceedsTotal, "totals.withheld_satang");
        if (t.NetReceivedSatang != t.InclVatSatang - t.WithheldSatang)
            Add(errors, TaxDocumentValidationCode.TotalsInconsistent, "totals.net_received_satang");
    }

    // The app may price VAT-exclusive (add VAT to the sum) or VAT-inclusive (split the total); either must reproduce the totals.
    private static bool MatchesVatMath(TaxDocumentTotals t)
    {
        var added = VatMath.AddVat(t.ExVatSatang, t.VatRateBasisPoints);
        var split = VatMath.Split(t.InclVatSatang, t.VatRateBasisPoints);
        return (added.ExVatSatang == t.ExVatSatang && added.VatSatang == t.VatSatang)
            || (split.ExVatSatang == t.ExVatSatang && split.VatSatang == t.VatSatang);
    }

    private static void ValidateNote(TaxDocumentSnapshot s, List<TaxDocumentValidationError> errors)
    {
        var isNoteKind = s.Kind is TaxDocumentKind.CreditNote or TaxDocumentKind.DebitNote;
        if (s.Note is null)
        {
            if (isNoteKind)
                Add(errors, TaxDocumentValidationCode.Required, "note");
            return;
        }

        if (!isNoteKind)
        {
            Add(errors, TaxDocumentValidationCode.NotAllowed, "note");
            return;
        }

        ValidateNoteReference(s, s.Note, errors);
        if (!Enum.IsDefined(s.Note.Mode))
            Add(errors, TaxDocumentValidationCode.Malformed, "note.mode");
        else if (s.Note.Mode == TaxNoteMode.FullCancellation)
            ValidateFullCancellation(s, errors);
        else
            ValidateAdjustment(s, errors);
    }

    private static void ValidateNoteReference(TaxDocumentSnapshot s, TaxNoteReference n, List<TaxDocumentValidationError> errors)
    {
        if (n.OriginalDocumentId == Guid.Empty)
            Add(errors, TaxDocumentValidationCode.Required, "note.original_document_id");
        if (string.IsNullOrWhiteSpace(n.OriginalDocumentNumber))
            Add(errors, TaxDocumentValidationCode.Required, "note.original_document_number");
        if (n.OriginalIssueDate == default)
            Add(errors, TaxDocumentValidationCode.Required, "note.original_issue_date");
        if (string.IsNullOrWhiteSpace(n.Reason))
            Add(errors, TaxDocumentValidationCode.Required, "note.reason");
        ValidateReasonKind(s, n, errors);
    }

    private static void ValidateReasonKind(TaxDocumentSnapshot s, TaxNoteReference n, List<TaxDocumentValidationError> errors)
    {
        if (n.ReasonKind is not { } kind)
        {
            if (s.Kind == TaxDocumentKind.CreditNote)
                Add(errors, TaxDocumentValidationCode.Required, "note.reason_kind");
            return;
        }

        if (s.Kind != TaxDocumentKind.CreditNote)
            Add(errors, TaxDocumentValidationCode.NotAllowed, "note.reason_kind");
        else if (!Enum.IsDefined(kind))
            Add(errors, TaxDocumentValidationCode.Malformed, "note.reason_kind");
        // A code with an uncovered character is already reported on this field as UnsupportedCharacter; it is not reported twice.
        else if (n.ReasonCode is not null && TaxDocumentFontCoverage.Covers(n.ReasonCode)
            && !string.Equals(n.ReasonCode, TaxNoteReasons.CodeOf(kind), StringComparison.Ordinal))
            Add(errors, TaxDocumentValidationCode.Malformed, "note.reason_code");
    }

    private static void ValidateFullCancellation(TaxDocumentSnapshot s, List<TaxDocumentValidationError> errors)
    {
        var n = s.Note!;
        if (s.Kind == TaxDocumentKind.DebitNote)
            Add(errors, TaxDocumentValidationCode.NotAllowed, "note.mode");
        if (n.OriginalValueSatang is not null || n.CorrectedValueSatang is not null)
            Add(errors, TaxDocumentValidationCode.NotAllowed, "note.original_value_satang");

        if (n.RefundedNetSatang is null)
            Add(errors, TaxDocumentValidationCode.Required, "note.refunded_net_satang");
        else if (s.Totals is not null && n.RefundedNetSatang != s.Totals.NetReceivedSatang)
            Add(errors, TaxDocumentValidationCode.TotalsInconsistent, "note.refunded_net_satang");
    }

    private static void ValidateAdjustment(TaxDocumentSnapshot s, List<TaxDocumentValidationError> errors)
    {
        var n = s.Note!;
        if (n.RefundedNetSatang is not null)
            Add(errors, TaxDocumentValidationCode.NotAllowed, "note.refunded_net_satang");
        if (n.OriginalValueSatang is null)
            Add(errors, TaxDocumentValidationCode.Required, "note.original_value_satang");
        if (n.CorrectedValueSatang is null)
            Add(errors, TaxDocumentValidationCode.Required, "note.corrected_value_satang");
        if (n.OriginalValueSatang is not { } original || n.CorrectedValueSatang is not { } corrected)
            return;

        // Negative values are rejected before any subtraction, so the difference of two non-negative longs cannot overflow.
        var lowers = s.Kind == TaxDocumentKind.CreditNote;
        var valid = original >= 0 && corrected >= 0
            && (lowers ? corrected < original : corrected > original)
            && s.Totals is not null && Math.Abs(original - corrected) == s.Totals.InclVatSatang;
        if (!valid)
            Add(errors, TaxDocumentValidationCode.TotalsInconsistent, "note.corrected_value_satang");
    }

    private static void ValidateReplacement(TaxDocumentReplacement? r, List<TaxDocumentValidationError> errors)
    {
        if (r is null)
            return;

        if (r.ReplacesDocumentId == Guid.Empty)
            Add(errors, TaxDocumentValidationCode.Required, "replacement.replaces_document_id");
        if (string.IsNullOrWhiteSpace(r.OriginalDocumentNumber))
            Add(errors, TaxDocumentValidationCode.Required, "replacement.original_document_number");
        if (r.OriginalIssueDate == default)
            Add(errors, TaxDocumentValidationCode.Required, "replacement.original_issue_date");
        if (string.IsNullOrWhiteSpace(r.Reason))
            Add(errors, TaxDocumentValidationCode.Required, "replacement.reason");
    }

    // The embedded fonts cover Thai, basic Latin and Latin Extended. Anything else would render in a host-dependent
    // fallback font and break byte-identical re-rendering, so it is refused here. The error carries the path only.
    private static void ValidateCharacters(TaxDocumentSnapshot s, List<TaxDocumentValidationError> errors)
    {
        CheckText(s.DocumentNumber, "document_number", errors);
        CheckParty(s.Seller, "seller", errors);
        CheckParty(s.Buyer, "buyer", errors);
        for (var i = 0; s.Lines is not null && i < s.Lines.Count; i++)
        {
            CheckText(s.Lines[i]?.Description, $"lines[{i}].description", errors);
            CheckText(s.Lines[i]?.Unit, $"lines[{i}].unit", errors);
        }

        if (s.Note is { } n)
        {
            CheckText(n.OriginalDocumentNumber, "note.original_document_number", errors);
            CheckText(n.ReasonCode, "note.reason_code", errors);
            CheckText(n.Reason, "note.reason", errors);
        }

        if (s.Replacement is { } r)
        {
            CheckText(r.OriginalDocumentNumber, "replacement.original_document_number", errors);
            CheckText(r.Reason, "replacement.reason", errors);
        }
    }

    private static void CheckParty(TaxParty? party, string path, List<TaxDocumentValidationError> errors)
    {
        if (party is null)
            return;

        CheckText(party.Name, $"{path}.name", errors);
        CheckText(party.Address, $"{path}.address", errors);
    }

    private static void CheckText(string? text, string path, List<TaxDocumentValidationError> errors)
    {
        if (!TaxDocumentFontCoverage.Covers(text))
            Add(errors, TaxDocumentValidationCode.UnsupportedCharacter, path);
    }

    private static void Add(List<TaxDocumentValidationError> errors, TaxDocumentValidationCode code, string field) =>
        errors.Add(new TaxDocumentValidationError(code, field));
}
