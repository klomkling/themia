using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Themia.ETax;

/// <summary>
/// The one persisted format of a <see cref="TaxDocumentSnapshot"/>: versioned JSON text plus a SHA-256 of its
/// exact UTF-8 bytes.
/// </summary>
/// <remarks>
/// Store the text in a column that preserves bytes (PostgreSQL <c>text</c>, SQL Server <c>nvarchar(max)</c>,
/// MySQL <c>longtext</c>), never <c>jsonb</c> or MySQL <c>JSON</c>, which reorder keys and drop whitespace and so
/// break the hash. The output is pure ASCII (non-ASCII is <c>\u</c>-escaped) so a column encoding cannot change it.
/// </remarks>
public static class TaxDocumentSnapshotSerializer
{
    private const string SchemaVersionProperty = "schema_version";

    /// <summary>Writes the snapshot in the deterministic persisted format.</summary>
    public static string Serialize(TaxDocumentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, TaxDocumentSnapshotJsonContext.Default.TaxDocumentSnapshot);
    }

    /// <summary>The lowercase hex SHA-256 of the UTF-8 bytes of <paramref name="snapshotJson"/>, exactly as stored.</summary>
    public static string ComputeHash(string snapshotJson)
    {
        ArgumentNullException.ThrowIfNull(snapshotJson);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson))).ToLowerInvariant();
    }

    /// <summary>Verifies the stored text against its recorded hash, then reads it.</summary>
    /// <exception cref="TaxDocumentSnapshotException">The hash does not match, the schema version is unsupported, or the text is malformed.</exception>
    public static TaxDocumentSnapshot Deserialize(string snapshotJson, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(snapshotJson);
        ArgumentNullException.ThrowIfNull(expectedSha256);

        if (!string.Equals(ComputeHash(snapshotJson), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new TaxDocumentSnapshotException(TaxDocumentSnapshotFailure.HashMismatch);

        try
        {
            if (ReadSchemaVersion(snapshotJson) != TaxDocumentSnapshot.CurrentSchemaVersion)
                throw new TaxDocumentSnapshotException(TaxDocumentSnapshotFailure.UnsupportedSchemaVersion);

            return JsonSerializer.Deserialize(snapshotJson, TaxDocumentSnapshotJsonContext.Default.TaxDocumentSnapshot)
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
