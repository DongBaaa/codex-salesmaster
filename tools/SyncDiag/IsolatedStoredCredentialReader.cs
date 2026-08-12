using System.Globalization;
using Microsoft.Data.Sqlite;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Tools.SyncDiag;

internal static class IsolatedStoredCredentialReader
{
    private const string SavedLoginUsernameKey = "Login.SavedUsername";
    private const string SavedLoginPasswordKey =
        "Login.SavedPasswordProtected";
    private const string CredentialPrefix = "Sync.OfficeCredential.";
    private const string UsernameSuffix = "Username";
    private const string TenantSuffix = "TenantCode";
    private const string PasswordSuffix = "PasswordProtected";
    private const string SavedAtSuffix = "SavedAtUtc";
    internal const int MaximumCredentialSettingRows = 128;
    internal const int MaximumCredentialCount = 16;
    internal const int MaximumCredentialKeyChars = 256;
    internal const int MaximumCredentialValueChars = 32768;
    internal const int MaximumCredentialKeyBytes =
        MaximumCredentialKeyChars * 4;
    internal const int MaximumCredentialValueBytes =
        MaximumCredentialValueChars * 4;
    internal const int MaximumOfficeCodeChars = 64;
    internal const int MaximumTenantCodeChars = 64;
    internal const int MaximumUsernameChars = 256;
    internal const int MaximumProtectedPasswordChars = 24576;
    internal const int MaximumSavedAtUtcChars = 64;

    private static readonly HashSet<string> KnownSuffixes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            UsernameSuffix,
            TenantSuffix,
            PasswordSuffix,
            SavedAtSuffix
        };

    public static async Task<IReadOnlyList<IsolatedStoredCredential>> ReadAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
            throw new InvalidDataException("The isolated credential database does not exist.");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var queryOnly = connection.CreateCommand();
        queryOnly.CommandText = "PRAGMA query_only=ON;";
        await queryOnly.ExecuteNonQueryAsync(cancellationToken);

        var rows = new List<CredentialSettingRow>();
        await using (var command = connection.CreateCommand())
        {
            // Filter and bound inside SQLite so unrelated or hostile values are
            // never materialized by the managed provider.
            command.CommandText =
                """
                SELECT
                    typeof("Key"),
                    CASE
                        WHEN typeof("Key") = 'text'
                        THEN octet_length("Key")
                        ELSE 0
                    END,
                    CASE
                        WHEN typeof("Key") = 'text'
                            AND octet_length("Key") <= $maximumKeyBytes
                        THEN length("Key")
                        ELSE 0
                    END,
                    CASE
                        WHEN typeof("Key") = 'text'
                            AND octet_length("Key") <= $maximumKeyBytes
                        THEN instr("Key", char(0))
                        ELSE 0
                    END,
                    CASE
                        WHEN typeof("Key") = 'text'
                            AND octet_length("Key") <= $maximumKeyBytes
                            AND instr("Key", char(0)) = 0
                        THEN substr("Key", 1, $maximumKeyCharsPlusOne)
                        ELSE ''
                    END,
                    typeof("Value"),
                    CASE
                        WHEN typeof("Value") IN ('text', 'blob')
                        THEN octet_length("Value")
                        ELSE 0
                    END,
                    CASE
                        WHEN typeof("Value") = 'text'
                            AND octet_length("Value") <= $maximumValueBytes
                        THEN length("Value")
                        ELSE 0
                    END,
                    CASE
                        WHEN typeof("Value") = 'text'
                            AND octet_length("Value") <= $maximumValueBytes
                        THEN instr("Value", char(0))
                        ELSE 0
                    END,
                    CASE
                        WHEN typeof("Value") = 'text'
                            AND octet_length("Value") <= $maximumValueBytes
                            AND instr("Value", char(0)) = 0
                        THEN substr("Value", 1, $maximumValueCharsPlusOne)
                        ELSE ''
                    END
                FROM "Settings"
                WHERE
                    typeof("Key") = 'text'
                    AND CASE
                        WHEN octet_length("Key") > $maximumKeyBytes
                        THEN 1
                        WHEN instr("Key", char(0)) > 0
                        THEN 1
                        WHEN substr("Key", 1, $prefixChars)
                            COLLATE NOCASE = $credentialPrefix
                        THEN 1
                        ELSE 0
                    END = 1
                LIMIT $maximumRowsPlusOne;
                """;
            command.Parameters.AddWithValue(
                "$maximumKeyCharsPlusOne",
                MaximumCredentialKeyChars + 1);
            command.Parameters.AddWithValue(
                "$maximumValueCharsPlusOne",
                MaximumCredentialValueChars + 1);
            command.Parameters.AddWithValue(
                "$maximumKeyBytes",
                MaximumCredentialKeyBytes);
            command.Parameters.AddWithValue(
                "$maximumValueBytes",
                MaximumCredentialValueBytes);
            command.Parameters.AddWithValue(
                "$prefixChars",
                CredentialPrefix.Length);
            command.Parameters.AddWithValue(
                "$credentialPrefix",
                CredentialPrefix);
            command.Parameters.AddWithValue(
                "$maximumRowsPlusOne",
                MaximumCredentialSettingRows + 1);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count >= MaximumCredentialSettingRows)
                    throw InvalidCredentialData("row_limit_exceeded");
                if (!string.Equals(
                        reader.GetString(0),
                        "text",
                        StringComparison.Ordinal))
                {
                    throw InvalidCredentialData("key_type_invalid");
                }
                if (reader.GetInt64(1) > MaximumCredentialKeyBytes)
                    throw InvalidCredentialData("key_size_exceeded");
                if (reader.GetInt64(2) > MaximumCredentialKeyChars)
                    throw InvalidCredentialData("key_size_exceeded");
                if (reader.GetInt64(3) != 0)
                    throw InvalidCredentialData("key_nul_invalid");
                var key = reader.GetString(4);
                var valueType = reader.GetString(5);
                if (!string.Equals(
                        valueType,
                        "text",
                        StringComparison.Ordinal) &&
                    !string.Equals(
                        valueType,
                        "null",
                        StringComparison.Ordinal))
                {
                    throw InvalidCredentialData("value_type_invalid");
                }
                if (reader.GetInt64(6) > MaximumCredentialValueBytes)
                    throw InvalidCredentialData("value_size_exceeded");
                if (reader.GetInt64(7) > MaximumCredentialValueChars)
                    throw InvalidCredentialData("value_size_exceeded");
                if (reader.GetInt64(8) != 0)
                    throw InvalidCredentialData("value_nul_invalid");
                var value = string.Equals(
                    valueType,
                    "text",
                    StringComparison.Ordinal)
                    ? reader.GetString(9)
                    : string.Empty;
                rows.Add(new CredentialSettingRow(key, value));
            }
        }

        return ParseRows(rows);
    }

    public static async Task<IsolatedStoredCredential?> ReadSavedLoginAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
            throw new InvalidDataException(
                "The isolated credential database does not exist.");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var queryOnly = connection.CreateCommand())
        {
            queryOnly.CommandText = "PRAGMA query_only=ON;";
            await queryOnly.ExecuteNonQueryAsync(cancellationToken);
        }

        string? username = null;
        string? protectedPassword = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    "Key",
                    typeof("Value"),
                    CASE
                        WHEN typeof("Value") = 'text'
                        THEN octet_length("Value")
                        ELSE 0
                    END,
                    CASE
                        WHEN typeof("Value") = 'text'
                            AND octet_length("Value") <= $maximumValueBytes
                            AND instr("Value", char(0)) = 0
                        THEN "Value"
                        ELSE ''
                    END
                FROM "Settings"
                WHERE
                    "Key" = $usernameKey COLLATE BINARY
                    OR "Key" = $passwordKey COLLATE BINARY
                LIMIT 3;
                """;
            command.Parameters.AddWithValue(
                "$maximumValueBytes",
                MaximumCredentialValueBytes);
            command.Parameters.AddWithValue(
                "$usernameKey",
                SavedLoginUsernameKey);
            command.Parameters.AddWithValue(
                "$passwordKey",
                SavedLoginPasswordKey);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            var rowCount = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                rowCount++;
                if (rowCount > 2 ||
                    !string.Equals(
                        reader.GetString(1),
                        "text",
                        StringComparison.Ordinal) ||
                    reader.GetInt64(2) > MaximumCredentialValueBytes)
                {
                    throw InvalidCredentialData(
                        "saved_login_value_invalid");
                }

                var key = reader.GetString(0);
                var value = reader.GetString(3);
                if (string.Equals(
                        key,
                        SavedLoginUsernameKey,
                        StringComparison.Ordinal))
                {
                    if (username is not null)
                        throw InvalidCredentialData(
                            "saved_login_duplicate_field");
                    username = value;
                }
                else if (string.Equals(
                             key,
                             SavedLoginPasswordKey,
                             StringComparison.Ordinal))
                {
                    if (protectedPassword is not null)
                        throw InvalidCredentialData(
                            "saved_login_duplicate_field");
                    protectedPassword = value;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(username) &&
            string.IsNullOrEmpty(protectedPassword))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrEmpty(protectedPassword) ||
            username.Length > MaximumUsernameChars ||
            protectedPassword.Length > MaximumProtectedPasswordChars)
        {
            throw InvalidCredentialData("saved_login_required_field_missing");
        }

        ValidateProtectedPassword(protectedPassword);
        var savedAtUtc = File.GetLastWriteTimeUtc(fullPath);
        if (savedAtUtc.Kind != DateTimeKind.Utc)
            savedAtUtc = DateTime.SpecifyKind(savedAtUtc, DateTimeKind.Utc);
        return new IsolatedStoredCredential(
            "SOURCE_LOGIN",
            "SOURCE_LOGIN",
            username.Trim(),
            protectedPassword,
            savedAtUtc);
    }

    private static IReadOnlyList<IsolatedStoredCredential> ParseRows(
        IReadOnlyList<CredentialSettingRow> rows)
    {
        if (rows.Count == 0)
            return [];

        var buckets = new Dictionary<string, CredentialBucket>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var remainder = row.Key[CredentialPrefix.Length..];
            var separatorIndex = remainder.IndexOf('.');
            if (separatorIndex <= 0 ||
                separatorIndex == remainder.Length - 1 ||
                remainder.IndexOf('.', separatorIndex + 1) >= 0)
            {
                if (!string.IsNullOrEmpty(row.Value))
                    throw InvalidCredentialData("malformed_setting");

                continue;
            }

            var officeBucket = remainder[..separatorIndex].Trim();
            var suffix = remainder[(separatorIndex + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(officeBucket) ||
                string.IsNullOrWhiteSpace(suffix))
            {
                if (!string.IsNullOrEmpty(row.Value))
                    throw InvalidCredentialData("malformed_setting");

                continue;
            }
            if (officeBucket.Length > MaximumOfficeCodeChars)
                throw InvalidCredentialData("office_size_exceeded");

            if (!buckets.TryGetValue(officeBucket, out var bucket))
            {
                bucket = new CredentialBucket(officeBucket);
                buckets.Add(officeBucket, bucket);
            }

            bucket.Add(suffix, row.Value);
        }
        if (buckets.Count > MaximumCredentialCount)
            throw InvalidCredentialData("credential_limit_exceeded");

        var credentials = new List<IsolatedStoredCredential>();
        var canonicalOffices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bucket in buckets.Values)
        {
            if (bucket.IsCompletelyEmpty)
                continue;

            if (bucket.HasDuplicateSuffix)
                throw InvalidCredentialData("duplicate_field");

            if (bucket.HasUnknownNonEmptySuffix)
                throw InvalidCredentialData("unknown_field");

            if (!OfficeCodeCatalog.TryNormalizeOfficeCode(
                    bucket.OfficeBucket,
                    out var officeCode))
            {
                throw InvalidCredentialData("invalid_office");
            }

            if (!canonicalOffices.Add(officeCode))
            {
                throw InvalidCredentialData("duplicate_office");
            }

            var username = bucket.GetValue(UsernameSuffix);
            var protectedPassword = bucket.GetValue(PasswordSuffix);
            if (string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrEmpty(protectedPassword))
            {
                throw InvalidCredentialData("required_field_missing");
            }
            if (username.Length > MaximumUsernameChars)
                throw InvalidCredentialData("username_size_exceeded");
            if (protectedPassword.Length > MaximumProtectedPasswordChars)
                throw InvalidCredentialData("password_envelope_size_exceeded");

            var tenantCode = ResolveTenantCode(
                bucket.GetValue(TenantSuffix),
                officeCode);
            var savedAtValue = bucket.GetValue(SavedAtSuffix);
            if (savedAtValue.Length > MaximumSavedAtUtcChars)
                throw InvalidCredentialData("saved_timestamp_size_exceeded");
            var savedAtUtc = ResolveSavedAtUtc(savedAtValue);
            ValidateProtectedPassword(protectedPassword);

            credentials.Add(new IsolatedStoredCredential(
                officeCode,
                tenantCode,
                username.Trim(),
                protectedPassword,
                savedAtUtc));
        }

        return credentials
            .OrderByDescending(credential => credential.SavedAtUtc)
            .ThenBy(credential => credential.OfficeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveTenantCode(string? value, string officeCode)
    {
        var expectedTenant = TenantScopeCatalog.GetTenantCodeForOffice(officeCode);
        if (string.IsNullOrEmpty(value))
            return expectedTenant;
        if (value.Length > MaximumTenantCodeChars)
            throw InvalidCredentialData("tenant_size_exceeded");

        if (!TenantScopeCatalog.TryNormalizeTenantCode(value, out var tenantCode) ||
            !string.Equals(
                tenantCode,
                expectedTenant,
                StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidCredentialData("invalid_tenant");
        }

        return tenantCode;
    }

    private static DateTime ResolveSavedAtUtc(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            !DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var savedAt) ||
            savedAt.Offset != TimeSpan.Zero)
        {
            throw InvalidCredentialData("invalid_saved_timestamp");
        }

        return savedAt.UtcDateTime;
    }

    private static void ValidateProtectedPassword(string protectedText)
    {
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = Convert.FromBase64String(protectedText);
            if (protectedBytes.Length == 0 ||
                !string.Equals(
                    Convert.ToBase64String(protectedBytes),
                    protectedText,
                    StringComparison.Ordinal))
            {
                throw InvalidCredentialData("invalid_password_envelope");
            }
        }
        catch (FormatException)
        {
            throw InvalidCredentialData("invalid_password_envelope");
        }
        finally
        {
            if (protectedBytes is not null)
                Array.Clear(protectedBytes, 0, protectedBytes.Length);
        }
    }

    private static InvalidDataException InvalidCredentialData(string reasonCode)
        => new($"stored_credentials_rejected reason_code={reasonCode}");

    private sealed class CredentialBucket(string officeBucket)
    {
        private readonly Dictionary<string, string> _values =
            new(StringComparer.OrdinalIgnoreCase);

        public string OfficeBucket { get; } = officeBucket;
        public bool HasDuplicateSuffix { get; private set; }
        public bool HasUnknownNonEmptySuffix { get; private set; }
        public bool IsCompletelyEmpty { get; private set; } = true;

        public void Add(string suffix, string value)
        {
            if (!string.IsNullOrEmpty(value))
                IsCompletelyEmpty = false;

            if (!KnownSuffixes.Contains(suffix))
            {
                if (!string.IsNullOrEmpty(value))
                    HasUnknownNonEmptySuffix = true;
                return;
            }

            if (!_values.TryAdd(suffix, value))
                HasDuplicateSuffix = true;
        }

        public string GetValue(string suffix)
            => _values.TryGetValue(suffix, out var value)
                ? value
                : string.Empty;
    }

    private sealed record CredentialSettingRow(string Key, string Value);
}

internal sealed record IsolatedStoredCredential(
    string OfficeCode,
    string TenantCode,
    string Username,
    string PasswordProtected,
    DateTime SavedAtUtc);
