using System.Text.Json;

namespace Themia.ETax.Withholding;

/// <summary>
/// The one persisted format of a <see cref="WithholdingCertificateSnapshot"/>: versioned JSON text plus a SHA-256 of its exact UTF-8
/// bytes. Same storage rules as <see cref="TaxDocumentSnapshotSerializer"/>: keep the text in a byte-preserving column, never
/// <c>jsonb</c> or MySQL <c>JSON</c>.
/// </summary>
public static class WithholdingSnapshotSerializer
{
    private const string SchemaVersionProperty = "schema_version";

    /// <summary>Writes the snapshot in the deterministic persisted format (pure ASCII; non-ASCII is <c>\u</c>-escaped).</summary>
    public static string Serialize(WithholdingCertificateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, WithholdingSnapshotJsonContext.Default.WithholdingCertificateSnapshot);
    }

    /// <summary>The lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="snapshotJson"/>, exactly as stored.</summary>
    public static string ComputeHash(string snapshotJson) => TaxDocumentSnapshotSerializer.ComputeHash(snapshotJson);

    /// <summary>Verifies the stored text against its recorded hash, then reads it.</summary>
    /// <exception cref="TaxDocumentSnapshotException">The hash does not match, the schema version is unsupported, or the text is malformed.</exception>
    public static WithholdingCertificateSnapshot Deserialize(string snapshotJson, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(snapshotJson);
        ArgumentNullException.ThrowIfNull(expectedSha256);

        if (!string.Equals(ComputeHash(snapshotJson), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new TaxDocumentSnapshotException(TaxDocumentSnapshotFailure.HashMismatch);

        try
        {
            if (ReadSchemaVersion(snapshotJson) != WithholdingCertificateSnapshot.CurrentSchemaVersion)
                throw new TaxDocumentSnapshotException(TaxDocumentSnapshotFailure.UnsupportedSchemaVersion);

            return JsonSerializer.Deserialize(snapshotJson, WithholdingSnapshotJsonContext.Default.WithholdingCertificateSnapshot)
                ?? throw new TaxDocumentSnapshotException(TaxDocumentSnapshotFailure.Malformed);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // The inner exception can quote the offending text, so it is kept off the message.
            throw new TaxDocumentSnapshotException(TaxDocumentSnapshotFailure.Malformed, ex);
        }
    }

    private static int ReadSchemaVersion(string snapshotJson)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(SchemaVersionProperty, out var version)
            || !version.TryGetInt32(out var value))
        {
            throw new TaxDocumentSnapshotException(TaxDocumentSnapshotFailure.Malformed);
        }

        return value;
    }
}
