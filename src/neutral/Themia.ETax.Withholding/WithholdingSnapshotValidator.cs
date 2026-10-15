namespace Themia.ETax.Withholding;

/// <summary>
/// Checks a <see cref="WithholdingCertificateSnapshot"/> before it is issued. Runs on NEW input only: stored snapshots are read
/// through <see cref="WithholdingSnapshotSerializer"/> and never re-validated.
/// </summary>
public static class WithholdingSnapshotValidator
{
    /// <summary>Returns every failed check; an empty list means the snapshot may be issued.</summary>
    /// <param name="snapshot">The snapshot to check.</param>
    /// <param name="catalogue">The income-type catalogue the rows were selected from.</param>
    /// <param name="options">Validation options; defaults apply when <see langword="null"/>.</param>
    public static IReadOnlyList<WithholdingValidationError> Validate(
        WithholdingCertificateSnapshot snapshot, IncomeTypeCatalogue catalogue, WithholdingValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalogue);
        var checksum = (options ?? new WithholdingValidationOptions()).ValidateTaxIdChecksum;
        var errors = new List<WithholdingValidationError>();

        ValidateHeader(snapshot, errors);
        ValidateParty(snapshot.Payer, "payer", isPayer: true, checksum, errors);
        ValidateParty(snapshot.Payee, "payee", isPayer: false, checksum, errors);
        ValidateRows(snapshot, catalogue, errors);
        ValidateTotals(snapshot, errors);
        ValidateCondition(snapshot, errors);
        ValidateFunds(snapshot.Funds, errors);
        ValidateReplacement(snapshot.Replacement, errors);
        ValidateCharacters(snapshot, errors);
        return errors;
    }

    private static void ValidateHeader(WithholdingCertificateSnapshot s, List<WithholdingValidationError> errors)
    {
        if (s.SchemaVersion != WithholdingCertificateSnapshot.CurrentSchemaVersion)
            Add(errors, WithholdingValidationCode.Malformed, "schema_version");
        if (string.IsNullOrWhiteSpace(s.DocumentNumber))
            Add(errors, WithholdingValidationCode.Required, "document_number");
        if (s.BookNumber is not null && string.IsNullOrWhiteSpace(s.BookNumber))
            Add(errors, WithholdingValidationCode.Malformed, "book_number");
        if (s.IssueDate == default)
            Add(errors, WithholdingValidationCode.Required, "issue_date");
        if (!Enum.IsDefined(s.PayeeKind))
            Add(errors, WithholdingValidationCode.Malformed, "payee_kind");
        if (!Enum.IsDefined(s.FilingForm))
            Add(errors, WithholdingValidationCode.Malformed, "filing_form");
        if (s.FilingSequence is < 1)
            Add(errors, WithholdingValidationCode.Malformed, "filing_sequence");
    }

    private static void ValidateParty(
        TaxParty? party, string path, bool isPayer, bool checksum, List<WithholdingValidationError> errors)
    {
        if (party is null)
        {
            Add(errors, WithholdingValidationCode.Required, path);
            return;
        }

        if (string.IsNullOrWhiteSpace(party.Name))
            Add(errors, WithholdingValidationCode.Required, $"{path}.name");
        if (string.IsNullOrWhiteSpace(party.Address))
            Add(errors, WithholdingValidationCode.Required, $"{path}.address");

        if (party.TaxId is null)
            Add(errors, WithholdingValidationCode.Required, $"{path}.tax_id");
        else if (!ThaiTaxId.IsWellFormed(party.TaxId))
            Add(errors, WithholdingValidationCode.Malformed, $"{path}.tax_id");
        else if (checksum && !ThaiTaxId.HasValidChecksum(party.TaxId))
            Add(errors, WithholdingValidationCode.InvalidChecksum, $"{path}.tax_id");

        if (party.Branch is null)
        {
            if (isPayer)
                Add(errors, WithholdingValidationCode.Required, $"{path}.branch");
        }
        else if (!ThaiBranchCode.IsWellFormed(party.Branch))
        {
            Add(errors, WithholdingValidationCode.Malformed, $"{path}.branch");
        }
    }

    private static void ValidateRows(WithholdingCertificateSnapshot s, IncomeTypeCatalogue catalogue, List<WithholdingValidationError> errors)
    {
        if (!string.Equals(s.IncomeCatalogueVersion, catalogue.Version, StringComparison.Ordinal))
            Add(errors, WithholdingValidationCode.CatalogueMismatch, "income_catalogue_version");

        if (s.Rows is not { Count: > 0 })
        {
            Add(errors, WithholdingValidationCode.Required, "rows");
            return;
        }

        var formNotAllowed = false;
        var kindNotAllowed = false;
        for (var i = 0; i < s.Rows.Count; i++)
        {
            var row = s.Rows[i];
            if (row is null)
            {
                Add(errors, WithholdingValidationCode.Required, $"rows[{i}]");
                continue;
            }

            ValidateRowCatalogue(row, i, catalogue, errors, s, ref formNotAllowed, ref kindNotAllowed);
            ValidateRowAmounts(row, i, s.IssueDate, errors);
        }

        if (formNotAllowed)
            Add(errors, WithholdingValidationCode.NotAllowed, "filing_form");
        if (kindNotAllowed)
            Add(errors, WithholdingValidationCode.NotAllowed, "payee_kind");
    }

    private static void ValidateRowCatalogue(
        WithholdingIncomeRow row, int i, IncomeTypeCatalogue catalogue, List<WithholdingValidationError> errors,
        WithholdingCertificateSnapshot s, ref bool formNotAllowed, ref bool kindNotAllowed)
    {
        if (!catalogue.TryGet(row.IncomeTypeCode, out var entry) || entry is null)
        {
            Add(errors, WithholdingValidationCode.CatalogueMismatch, $"rows[{i}].income_type_code");
            return;
        }

        if (!string.Equals(row.Section, entry.Section, StringComparison.Ordinal))
            Add(errors, WithholdingValidationCode.CatalogueMismatch, $"rows[{i}].section");
        if (!string.Equals(row.SubItem, entry.SubItem, StringComparison.Ordinal))
            Add(errors, WithholdingValidationCode.CatalogueMismatch, $"rows[{i}].sub_item");
        if (!string.Equals(row.LabelTh, entry.LabelTh, StringComparison.Ordinal))
            Add(errors, WithholdingValidationCode.CatalogueMismatch, $"rows[{i}].label_th");
        if (row.FormRow != entry.FormRow)
            Add(errors, WithholdingValidationCode.CatalogueMismatch, $"rows[{i}].form_row");

        formNotAllowed |= Enum.IsDefined(s.FilingForm) && !entry.AllowedFilingForms.Contains(s.FilingForm);
        kindNotAllowed |= Enum.IsDefined(s.PayeeKind) && !entry.AllowedPayeeKinds.Contains(s.PayeeKind);
    }

    private static void ValidateRowAmounts(WithholdingIncomeRow row, int i, DateOnly issueDate, List<WithholdingValidationError> errors)
    {
        if (row.DatePaid == default)
            Add(errors, WithholdingValidationCode.Required, $"rows[{i}].date_paid");
        else if (issueDate != default && row.DatePaid > issueDate)
            Add(errors, WithholdingValidationCode.Malformed, $"rows[{i}].date_paid");

        var amountsOk = true;
        if (row.AmountPaidSatang < 0)
        {
            Add(errors, WithholdingValidationCode.Malformed, $"rows[{i}].amount_paid_satang");
            amountsOk = false;
        }

        if (row.TaxWithheldSatang < 0)
        {
            Add(errors, WithholdingValidationCode.Malformed, $"rows[{i}].tax_withheld_satang");
            amountsOk = false;
        }

        var rateOk = row.RateBasisPoints is null or (>= 0 and <= WithholdingMath.MaxRateBasisPoints);
        if (!rateOk)
            Add(errors, WithholdingValidationCode.Malformed, $"rows[{i}].rate_basis_points");
        if (!amountsOk || !rateOk)
            return;

        if (row.TaxWithheldSatang > row.AmountPaidSatang)
            Add(errors, WithholdingValidationCode.WithheldExceedsPaid, $"rows[{i}].tax_withheld_satang");
        else if (row.RateBasisPoints is { } rate && row.TaxWithheldSatang != WithholdingMath.Withhold(row.AmountPaidSatang, rate))
            Add(errors, WithholdingValidationCode.AmountsInconsistent, $"rows[{i}].tax_withheld_satang");
    }

    private static void ValidateTotals(WithholdingCertificateSnapshot s, List<WithholdingValidationError> errors)
    {
        if (s.Totals is null)
        {
            Add(errors, WithholdingValidationCode.Required, "totals");
            return;
        }

        if (s.Totals.PaidSatang < 0 || s.Totals.WithheldSatang < 0)
        {
            Add(errors, WithholdingValidationCode.Malformed, "totals");
            return;
        }

        var rows = s.Rows?.Where(r => r is not null).ToArray() ?? [];
        if (rows.Length == 0 || rows.Any(r => r.AmountPaidSatang < 0 || r.TaxWithheldSatang < 0))
            return;

        try
        {
            var paid = rows.Aggregate(0L, (sum, r) => checked(sum + r.AmountPaidSatang));
            var withheld = rows.Aggregate(0L, (sum, r) => checked(sum + r.TaxWithheldSatang));
            if (paid != s.Totals.PaidSatang)
                Add(errors, WithholdingValidationCode.AmountsInconsistent, "totals.paid_satang");
            if (withheld != s.Totals.WithheldSatang)
                Add(errors, WithholdingValidationCode.AmountsInconsistent, "totals.withheld_satang");
        }
        catch (OverflowException)
        {
            // Amounts so large that the sums leave the range of a long: not a real certificate.
            Add(errors, WithholdingValidationCode.Malformed, "totals");
        }
    }

    private static void ValidateCondition(WithholdingCertificateSnapshot s, List<WithholdingValidationError> errors)
    {
        if (!Enum.IsDefined(s.Condition))
        {
            Add(errors, WithholdingValidationCode.Malformed, "condition");
            return;
        }

        var isOther = s.Condition == WithholdingCondition.Other;
        if (isOther && string.IsNullOrWhiteSpace(s.ConditionOther))
            Add(errors, WithholdingValidationCode.Required, "condition_other");
        else if (!isOther && s.ConditionOther is not null)
            Add(errors, WithholdingValidationCode.NotAllowed, "condition_other");
    }

    private static void ValidateFunds(WithholdingFunds? funds, List<WithholdingValidationError> errors)
    {
        if (funds is null)
            return;

        if (funds.SocialSecuritySatang is < 0)
            Add(errors, WithholdingValidationCode.Malformed, "funds.social_security_satang");
        if (funds.ProvidentFundSatang is < 0)
            Add(errors, WithholdingValidationCode.Malformed, "funds.provident_fund_satang");
        if (funds.GovernmentPensionSatang is < 0)
            Add(errors, WithholdingValidationCode.Malformed, "funds.government_pension_satang");
    }

    private static void ValidateReplacement(TaxDocumentReplacement? r, List<WithholdingValidationError> errors)
    {
        if (r is null)
            return;

        if (r.ReplacesDocumentId == Guid.Empty)
            Add(errors, WithholdingValidationCode.Required, "replacement.replaces_document_id");
        if (string.IsNullOrWhiteSpace(r.OriginalDocumentNumber))
            Add(errors, WithholdingValidationCode.Required, "replacement.original_document_number");
        if (r.OriginalIssueDate == default)
            Add(errors, WithholdingValidationCode.Required, "replacement.original_issue_date");
        if (string.IsNullOrWhiteSpace(r.Reason))
            Add(errors, WithholdingValidationCode.Required, "replacement.reason");
    }

    // The embedded fonts cover Thai, basic Latin and Latin Extended. Anything else would render in a host-dependent
    // fallback font and break byte-identical re-rendering, so it is refused here. The error carries the path only.
    private static void ValidateCharacters(WithholdingCertificateSnapshot s, List<WithholdingValidationError> errors)
    {
        CheckText(s.DocumentNumber, "document_number", errors);
        CheckText(s.BookNumber, "book_number", errors);
        CheckText(s.ConditionOther, "condition_other", errors);
        CheckParty(s.Payer, "payer", errors);
        CheckParty(s.Payee, "payee", errors);
        for (var i = 0; s.Rows is not null && i < s.Rows.Count; i++)
        {
            CheckText(s.Rows[i]?.Section, $"rows[{i}].section", errors);
            CheckText(s.Rows[i]?.SubItem, $"rows[{i}].sub_item", errors);
            CheckText(s.Rows[i]?.LabelTh, $"rows[{i}].label_th", errors);
            CheckText(s.Rows[i]?.Description, $"rows[{i}].description", errors);
        }

        if (s.Replacement is { } r)
        {
            CheckText(r.OriginalDocumentNumber, "replacement.original_document_number", errors);
            CheckText(r.Reason, "replacement.reason", errors);
        }
    }

    private static void CheckParty(TaxParty? party, string path, List<WithholdingValidationError> errors)
    {
        if (party is null)
            return;

        CheckText(party.Name, $"{path}.name", errors);
        CheckText(party.Address, $"{path}.address", errors);
    }

    private static void CheckText(string? text, string path, List<WithholdingValidationError> errors)
    {
        if (!TaxDocumentFontCoverage.Covers(text))
            Add(errors, WithholdingValidationCode.UnsupportedCharacter, path);
    }

    private static void Add(List<WithholdingValidationError> errors, WithholdingValidationCode code, string field) =>
        errors.Add(new WithholdingValidationError(code, field));
}
