using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Themia.Storage.Local;
using Themia.Storage.S3;

namespace Themia.Storage.Configuration;

/// <summary>
/// Reads the <c>Storage</c> section. Private files are always Local: no key can move them, and a value that tries
/// stops the host. Every failure names the key, never a secret's value.
/// </summary>
internal static class StorageSettingsReader
{
    /// <summary>The release after which the deprecated keys stop being read.</summary>
    internal const string LegacyKeysRemovedIn = "0.48.0";

    private const string Local = "Local";
    private const string PrivateProvider = "Storage:Private:Provider";
    private const string PublicProvider = "Storage:Public:Provider";
    private const string LegacyProvider = "Storage:Provider";
    private const string LegacyBucketName = "Storage:S3:BucketName";
    private const string LegacyUploadsDirectory = "Storage:Local:UploadsDirectory";
    private const string LegacyDownloadBaseUrl = "Storage:DownloadBaseUrl";
    private const string PresignedBaseUrlKey = "Storage:Local:PresignedBaseUrl";

    private static readonly string[] S3Keys =
        ["PublicBucketName", "PublicBaseUrl", "ServiceUrl", "Region", "ForcePathStyle", "AccessKey", "SecretKey"];

    private static readonly string[] LocalKeys =
        ["RootPath", "SigningKey", "PresignedBaseUrl", "PublicRootPath", "PublicBaseUrl", "UploadsDirectory"];

    /// <summary>Reads and validates the section.</summary>
    /// <exception cref="InvalidOperationException">The configuration is not usable; the message names the key.</exception>
    public static StorageSettings Read(IConfiguration configuration, IHostEnvironment environment)
    {
        var warnings = new List<string>();

        RefuseUnknownKeys(configuration, "Storage:S3", S3Keys.Append("BucketName"));
        RefuseUnknownKeys(configuration, "Storage:Local", LocalKeys);
        RefuseUnknownKeys(configuration, "Storage:Public", ["Provider"]);
        RefuseUnknownKeys(configuration, "Storage:Private", ["Provider"]);

        if (Value(configuration, LegacyBucketName) is not null)
        {
            throw Fail($"{LegacyBucketName} was renamed to Storage:S3:PublicBucketName; there is no private bucket, private files stay on Local.");
        }

        var privateProvider = Value(configuration, PrivateProvider);
        if (privateProvider is not null && !privateProvider.Equals(Local, StringComparison.OrdinalIgnoreCase))
        {
            throw Fail($"{PrivateProvider} is not supported: private files stay on Local (RoPA), there is no other value.");
        }

        var publicSlot = ReadPublicSlot(configuration, warnings);
        var presigned = ReadPresignedBaseUrl(configuration, warnings);
        var local = ReadLocal(configuration, environment, publicSlot, warnings);

        return new StorageSettings
        {
            Public = publicSlot,
            Local = local,
            S3 = publicSlot == PublicSlot.S3 ? ReadS3(configuration) : null,
            PresignedBaseUrl = presigned,
            Warnings = warnings,
        };
    }

    private static PublicSlot ReadPublicSlot(IConfiguration configuration, List<string> warnings)
    {
        var configured = Value(configuration, PublicProvider);
        var legacy = Value(configuration, LegacyProvider);

        if (legacy is not null)
        {
            if (configured is not null)
            {
                throw Fail($"{LegacyProvider} and {PublicProvider} are both set; keep {PublicProvider} only.");
            }

            warnings.Add($"{LegacyProvider} is deprecated; use {PublicProvider} (Split becomes S3, Local becomes Local).");
            if (legacy.Equals("Split", StringComparison.OrdinalIgnoreCase))
            {
                return PublicSlot.S3;
            }

            return legacy.Equals(Local, StringComparison.OrdinalIgnoreCase)
                ? PublicSlot.Local
                : throw Fail($"{LegacyProvider} is not supported: it would put private files off Local. Use Local or Split.");
        }

        if (configured is null || configured.Equals(Local, StringComparison.OrdinalIgnoreCase))
        {
            return PublicSlot.Local;
        }

        return configured.Equals("S3", StringComparison.OrdinalIgnoreCase) || configured.Equals("R2", StringComparison.OrdinalIgnoreCase)
            ? PublicSlot.S3
            : throw Fail($"{PublicProvider} is not supported. Expected Local, S3 or R2.");
    }

    private static string ReadPresignedBaseUrl(IConfiguration configuration, List<string> warnings)
    {
        var canonical = Value(configuration, PresignedBaseUrlKey);
        var legacy = Value(configuration, LegacyDownloadBaseUrl);
        if (legacy is null)
        {
            return canonical ?? string.Empty;
        }

        warnings.Add($"{LegacyDownloadBaseUrl} is deprecated; use {PresignedBaseUrlKey}.");
        return canonical is null || canonical == legacy
            ? legacy
            : throw Fail($"{LegacyDownloadBaseUrl} and {PresignedBaseUrlKey} are both set with different values; keep {PresignedBaseUrlKey} only.");
    }

    private static LocalStorageOptions ReadLocal(
        IConfiguration configuration, IHostEnvironment environment, PublicSlot publicSlot, List<string> warnings)
    {
        var rootPath = Value(configuration, "Storage:Local:RootPath");
        var uploads = Value(configuration, LegacyUploadsDirectory);
        if (uploads is not null)
        {
            warnings.Add($"{LegacyUploadsDirectory} is deprecated; use Storage:Local:RootPath (a relative path is under the content root).");
            rootPath = rootPath is null
                ? uploads
                : throw Fail($"{LegacyUploadsDirectory} and Storage:Local:RootPath are both set; keep Storage:Local:RootPath only.");
        }

        if (rootPath is null)
        {
            throw Fail("Storage:Local:RootPath is required (private files live there; a relative path is under the content root). " +
                "It has no default: a default that differs from an earlier deployment would orphan its files.");
        }

        var root = Resolve(environment, rootPath);
        var publicRoot = Value(configuration, "Storage:Local:PublicRootPath");
        var publicBaseUrl = Value(configuration, "Storage:Local:PublicBaseUrl");

        if (publicSlot == PublicSlot.S3)
        {
            if (publicRoot is not null || publicBaseUrl is not null)
            {
                throw Fail("Storage:Local:PublicRootPath / PublicBaseUrl are set but the public slot is S3/R2; they would be dead configuration.");
            }
        }
        else if (publicBaseUrl is not null)
        {
            publicRoot = publicRoot is null ? Path.Combine(root, "public") : Resolve(environment, publicRoot);
        }

        if (Any(configuration, "Storage:S3") && publicSlot == PublicSlot.Local)
        {
            throw Fail($"Storage:S3:* is set but the public slot is Local; set {PublicProvider}=S3 or remove the S3 keys.");
        }

        var options = new LocalStorageOptions
        {
            RootPath = root,
            SigningKey = Value(configuration, "Storage:Local:SigningKey") ?? string.Empty,
            PublicRootPath = publicRoot ?? string.Empty,
            PublicBaseUrl = publicBaseUrl ?? string.Empty,
        };

        try
        {
            options.Validate();
        }
        catch (ArgumentException ex)
        {
            throw Fail($"Storage:Local: {ex.Message}");
        }

        return options;
    }

    private static S3StorageOptions ReadS3(IConfiguration configuration)
    {
        var options = new S3StorageOptions
        {
            PublicOnly = true,
            PublicBucketName = Required(configuration, "Storage:S3:PublicBucketName"),
            PublicBaseUrl = Required(configuration, "Storage:S3:PublicBaseUrl"),
            AccessKey = Required(configuration, "Storage:S3:AccessKey"),
            SecretKey = Required(configuration, "Storage:S3:SecretKey"),
            Region = Value(configuration, "Storage:S3:Region"),
            ServiceUrl = OptionalUrl(configuration, "Storage:S3:ServiceUrl"),
            ForcePathStyle = ForcePathStyle(configuration),
        };

        try
        {
            options.Validate();
        }
        catch (ArgumentException ex)
        {
            throw Fail($"Storage:S3: {ex.Message}");
        }

        return options;
    }

    // Both keys are required because a blank one makes the SDK fall back to the ambient AWS credential chain.
    private static string Required(IConfiguration configuration, string key) =>
        Value(configuration, key)
        ?? throw Fail($"{key} is required when {PublicProvider} is S3/R2 (as an environment variable: {key.Replace(":", "__")}).");

    private static Uri? OptionalUrl(IConfiguration configuration, string key)
    {
        var value = Value(configuration, key);
        if (value is null)
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw Fail($"{key} must be an absolute http(s) URL.");
    }

    // R2 and MinIO need path-style addressing, so it is on unless the key says otherwise.
    private static bool ForcePathStyle(IConfiguration configuration)
    {
        var value = Value(configuration, "Storage:S3:ForcePathStyle");
        return value is null || (bool.TryParse(value, out var parsed)
            ? parsed
            : throw Fail("Storage:S3:ForcePathStyle must be true or false."));
    }

    private static string Resolve(IHostEnvironment environment, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(environment.ContentRootPath, path));

    // A blank value (an empty environment variable) counts as not set.
    private static string? Value(IConfiguration configuration, string key)
    {
        var value = configuration[key]?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool Any(IConfiguration configuration, string section) =>
        configuration.GetSection(section).GetChildren().Any(c => !string.IsNullOrWhiteSpace(c.Value));

    // A typo such as Storage:Public:Provder would otherwise be ignored and the files would land on the wrong disk.
    private static void RefuseUnknownKeys(IConfiguration configuration, string section, IEnumerable<string> known)
    {
        var allowed = known.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var child in configuration.GetSection(section).GetChildren())
        {
            if (!allowed.Contains(child.Key))
            {
                throw Fail($"{section}:{child.Key} is not a storage key. Known keys: {string.Join(", ", allowed.Order())}.");
            }
        }
    }

    private static InvalidOperationException Fail(string message) => new(message);
}
