using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GeoraePlan.Tools.SyncDiag;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class IsolatedStoredCredentialReaderTests
{
    [Fact]
    public void SyncDiag_ExposesOnlyProtectedCredentialEnvelopeCommand()
    {
        var repositoryRoot = FindRepositoryRoot();
        var programSource = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "tools",
                "SyncDiag",
                "Program.cs"));
        var readerSource = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "tools",
                "SyncDiag",
                "IsolatedStoredCredentialReader.cs"));

        Assert.Contains(
            "\"stored-credential-envelopes\"",
            programSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"stored-" + "credentials\"",
            programSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "schemaVersion = 1",
            programSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "protection = \"DPAPI-CurrentUser\"",
            programSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "credential.PasswordProtected",
            programSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "credential.Password,",
            programSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ProtectedData.Unprotect",
            readerSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncDiag_StoredCredentialEnvelopeWritesExactlyOneCiphertextJsonLine()
    {
        const string secret = "command-fixture-secret";
        var outputRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"stored-credential-command-{Guid.NewGuid():N}");
        var appRoot = Path.Combine(outputRoot, "AppData");
        var dataRoot = Path.Combine(appRoot, "data");
        var databasePath = Path.Combine(dataRoot, "거래플랜.db");
        Directory.CreateDirectory(dataRoot);
        await CreateSettingsDatabaseAsync(
            databasePath,
            ("Sync.OfficeCredential.USENET.Username", "fixture-user"),
            ("Sync.OfficeCredential.USENET.TenantCode", "USENET_GROUP"),
            ("Sync.OfficeCredential.USENET.PasswordProtected", Protect(secret)),
            ("Sync.OfficeCredential.USENET.SavedAtUtc", "2026-07-29T00:00:00.0000000Z"));
        File.WriteAllText(
            Path.Combine(appRoot, ".georaeplan-isolated-seed-root"),
            appRoot);
        await using var preparationLease = File.Open(
            Path.Combine(outputRoot, ".georaeplan-prepare.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read);

        try
        {
            var repositoryRoot = FindRepositoryRoot();
            var toolPath = Path.Combine(
                repositoryRoot,
                "tools",
                "SyncDiag",
                "bin",
                "Debug",
                "net8.0-windows",
                "SyncDiag.dll");
            Assert.True(File.Exists(toolPath), $"SyncDiag was not built: {toolPath}");
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = File.Exists(@"D:\.dotnet-sdk\dotnet.exe")
                    ? @"D:\.dotnet-sdk\dotnet.exe"
                    : "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(toolPath);
            startInfo.ArgumentList.Add("stored-credential-envelopes");
            startInfo.Environment["GEORAEPLAN_TEST_MODE"] = "1";
            startInfo.Environment["GEORAEPLAN_TEST_SEED_MODE"] = "1";
            startInfo.Environment["GEORAEPLAN_APP_ROOT"] = appRoot;
            startInfo.Environment["GEORAEPLAN_TEST_SEED_ROOT"] = appRoot;

            using var process = System.Diagnostics.Process.Start(startInfo);
            Assert.NotNull(process);
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(30));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.Equal(0, process.ExitCode);
            Assert.Empty(stderr);
            var jsonLine = stdout.TrimEnd('\r', '\n');
            Assert.DoesNotContain('\r', jsonLine);
            Assert.DoesNotContain('\n', jsonLine);
            using var document = JsonDocument.Parse(jsonLine);
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(
                "DPAPI-CurrentUser",
                root.GetProperty("protection").GetString());
            var credential = Assert.Single(
                root.GetProperty("credentials").EnumerateArray());
            Assert.False(credential.TryGetProperty("Password", out _));
            var passwordProtected =
                credential.GetProperty("PasswordProtected").GetString();
            Assert.NotNull(passwordProtected);
            Assert.NotEqual(secret, passwordProtected);
            Assert.Equal(secret, Unprotect(passwordProtected));
            Assert.DoesNotContain(secret, stdout, StringComparison.Ordinal);
        }
        finally
        {
            preparationLease.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_ReturnsProtectedEnvelopesWithoutDecryptingPasswords()
    {
        const string firstPassword = "first-fixture-password";
        const string secondPassword = "second-fixture-password";
        await using var fixture = await CredentialDatabaseFixture.CreateAsync(
            ("Sync.OfficeCredential.USENET.Username", "usenet-user"),
            ("Sync.OfficeCredential.USENET.TenantCode", "USENET_GROUP"),
            ("Sync.OfficeCredential.USENET.PasswordProtected", Protect(firstPassword)),
            ("Sync.OfficeCredential.USENET.SavedAtUtc", "2026-07-24T01:02:03.0000000Z"),
            ("Sync.OfficeCredential.ITWORLD.username", "itworld-user"),
            ("Sync.OfficeCredential.ITWORLD.TenantCode", "ITWORLD"),
            ("Sync.OfficeCredential.ITWORLD.PasswordProtected", Protect(secondPassword)),
            ("Sync.OfficeCredential.ITWORLD.SavedAtUtc", "2026-07-24T02:03:04.0000000Z"),
            ("Sync.OfficeCredential.INVALID.Username", string.Empty),
            ("Sync.OfficeCredential.INVALID.UnknownField", string.Empty));

        var credentials =
            await IsolatedStoredCredentialReader.ReadAsync(fixture.DatabasePath);

        Assert.Collection(
            credentials,
            credential =>
            {
                Assert.Equal("ITWORLD", credential.OfficeCode);
                Assert.Equal("ITWORLD", credential.TenantCode);
                Assert.Equal("itworld-user", credential.Username);
                var passwordProtected = GetPasswordProtected(credential);
                Assert.NotEqual(secondPassword, passwordProtected);
                Assert.Equal(
                    secondPassword,
                    Unprotect(passwordProtected));
                Assert.Equal(
                    DateTime.Parse(
                        "2026-07-24T02:03:04.0000000Z",
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind),
                    credential.SavedAtUtc);
            },
            credential =>
            {
                Assert.Equal("USENET", credential.OfficeCode);
                Assert.Equal("USENET_GROUP", credential.TenantCode);
                Assert.Equal("usenet-user", credential.Username);
                var passwordProtected = GetPasswordProtected(credential);
                Assert.NotEqual(firstPassword, passwordProtected);
                Assert.Equal(
                    firstPassword,
                    Unprotect(passwordProtected));
                Assert.Equal(
                    DateTime.Parse(
                        "2026-07-24T01:02:03.0000000Z",
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind),
                    credential.SavedAtUtc);
            });
    }

    [Theory]
    [InlineData("Username")]
    [InlineData("PasswordProtected")]
    [InlineData("SavedAtUtc")]
    public async Task ReadAsync_RejectsNonEmptyBucketWithMissingRequiredField(
        string omittedSuffix)
    {
        var rows = new List<(string Key, string Value)>
        {
            ("Sync.OfficeCredential.USENET.Username", "fixture-user"),
            ("Sync.OfficeCredential.USENET.PasswordProtected", Protect("missing-field-secret")),
            ("Sync.OfficeCredential.USENET.TenantCode", "USENET_GROUP"),
            ("Sync.OfficeCredential.USENET.SavedAtUtc", "2026-07-29T00:00:00.0000000Z")
        };
        rows.RemoveAll(row => row.Key.EndsWith(
            "." + omittedSuffix,
            StringComparison.Ordinal));
        await using var fixture =
            await CredentialDatabaseFixture.CreateAsync(rows.ToArray());

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => IsolatedStoredCredentialReader.ReadAsync(fixture.DatabasePath));

        Assert.DoesNotContain("fixture-user", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("missing-field-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_RejectsInvalidOfficeBucket()
    {
        await using var fixture = await CredentialDatabaseFixture.CreateAsync(
            ("Sync.OfficeCredential.NOT_AN_OFFICE.Username", "fixture-user"),
            ("Sync.OfficeCredential.NOT_AN_OFFICE.PasswordProtected", Protect("office-secret")));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => IsolatedStoredCredentialReader.ReadAsync(fixture.DatabasePath));

        Assert.DoesNotContain("fixture-user", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("office-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_RejectsMalformedPrefixedRow()
    {
        await using var fixture = await CredentialDatabaseFixture.CreateAsync(
            ("Sync.OfficeCredential.USENET", "malformed-setting-secret"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => IsolatedStoredCredentialReader.ReadAsync(fixture.DatabasePath));

        Assert.DoesNotContain(
            "malformed-setting-secret",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_RejectsUnknownNonEmptySuffix()
    {
        await using var fixture = await CredentialDatabaseFixture.CreateAsync(
            ("Sync.OfficeCredential.USENET.Username", "fixture-user"),
            ("Sync.OfficeCredential.USENET.PasswordProtected", Protect("unknown-secret")),
            ("Sync.OfficeCredential.USENET.FutureField", "unexpected-sensitive-value"));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => IsolatedStoredCredentialReader.ReadAsync(fixture.DatabasePath));

        Assert.DoesNotContain(
            "unexpected-sensitive-value",
            error.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("unknown-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_RejectsCaseInsensitiveDuplicateSuffix()
    {
        await using var fixture = await CredentialDatabaseFixture.CreateAsync(
            ("Sync.OfficeCredential.USENET.Username", "first-user"),
            ("Sync.OfficeCredential.USENET.username", "second-user"),
            ("Sync.OfficeCredential.USENET.PasswordProtected", Protect("duplicate-secret")));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => IsolatedStoredCredentialReader.ReadAsync(fixture.DatabasePath));

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("first-user", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("second-user", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("duplicate-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_DoesNotAttemptDpapiUnprotect()
    {
        var invalidDpapiPayload = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes("not-a-dpapi-payload")));
        await using var fixture = await CredentialDatabaseFixture.CreateAsync(
            ("Sync.OfficeCredential.USENET.Username", "fixture-user"),
            ("Sync.OfficeCredential.USENET.PasswordProtected", invalidDpapiPayload),
            ("Sync.OfficeCredential.USENET.SavedAtUtc", "2026-07-29T00:00:00.0000000Z"));

        var credential = Assert.Single(
            await IsolatedStoredCredentialReader.ReadAsync(fixture.DatabasePath));

        Assert.Equal("fixture-user", credential.Username);
        Assert.Equal(
            invalidDpapiPayload,
            GetPasswordProtected(credential));
        Assert.DoesNotContain(
            credential.GetType().GetProperties(),
            property => string.Equals(
                property.Name,
                "Password",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2026-07-29T00:00:00")]
    [InlineData("2026-07-29T09:00:00.0000000+09:00")]
    [InlineData("2026-07-29T00:00:00.0000000")]
    [InlineData("2026-07-29 00:00:00Z")]
    public async Task ReadAsync_RejectsSavedAtUtcWithoutExactUtcRoundTripFormat(
        string savedAtUtc)
    {
        await using var fixture = await CredentialDatabaseFixture.CreateAsync(
            ("Sync.OfficeCredential.USENET.Username", "fixture-user"),
            ("Sync.OfficeCredential.USENET.PasswordProtected", Protect("timestamp-secret")),
            ("Sync.OfficeCredential.USENET.SavedAtUtc", savedAtUtc));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => IsolatedStoredCredentialReader.ReadAsync(
                fixture.DatabasePath));

        Assert.DoesNotContain(
            savedAtUtc,
            error.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "timestamp-secret",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_RejectsOversizedCredentialRowSetWithBoundedReason()
    {
        var rows = Enumerable.Range(0, 129)
            .Select(index => (
                $"Sync.OfficeCredential.OFFICE{index:D3}.Username",
                string.Empty))
            .ToArray();
        await using var fixture =
            await CredentialDatabaseFixture.CreateAsync(rows);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => IsolatedStoredCredentialReader.ReadAsync(
                fixture.DatabasePath));

        Assert.Equal(
            "stored_credentials_rejected reason_code=row_limit_exceeded",
            error.Message);
    }

    [Fact]
    public async Task ReadAsync_RejectsTooManyCredentialBucketsBeforeFieldUse()
    {
        var rows = Enumerable.Range(0, 17)
            .Select(index => (
                $"Sync.OfficeCredential.OFFICE{index:D2}.Username",
                "bounded-value"))
            .ToArray();
        await using var fixture =
            await CredentialDatabaseFixture.CreateAsync(rows);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => IsolatedStoredCredentialReader.ReadAsync(
                fixture.DatabasePath));

        Assert.Equal(
            "stored_credentials_rejected reason_code=credential_limit_exceeded",
            error.Message);
        Assert.DoesNotContain(
            "bounded-value",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("value")]
    public async Task ReadAsync_RejectsOversizedCredentialFieldsWithoutEcho(
        string oversizedPart)
    {
        const string secretMarker = "oversized-credential-secret";
        var oversized = secretMarker + new string('X', 40000);
        var key = oversizedPart == "key"
            ? "Sync.OfficeCredential." + oversized + ".Username"
            : "Sync.OfficeCredential.USENET.Username";
        var value = oversizedPart == "value"
            ? oversized
            : "fixture-user";
        await using var fixture =
            await CredentialDatabaseFixture.CreateAsync((key, value));

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => IsolatedStoredCredentialReader.ReadAsync(
                fixture.DatabasePath));

        Assert.Matches(
            "^stored_credentials_rejected reason_code=(key|value)_size_exceeded$",
            error.Message);
        Assert.DoesNotContain(
            secretMarker,
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("large-text")]
    public async Task ReadAsync_RejectsUnboundedCredentialValueBeforeMaterialization(
        string valueKind)
    {
        await using var fixture =
            await CredentialDatabaseFixture.CreateAsync();
        await ExecuteFixtureSqlAsync(
            fixture.DatabasePath,
            valueKind == "blob"
                ? """
                  INSERT INTO "Settings" ("Key", "Value")
                  VALUES (
                    'Sync.OfficeCredential.USENET.PasswordProtected',
                    zeroblob(16777216));
                  """
                : """
                  INSERT INTO "Settings" ("Key", "Value")
                  VALUES (
                    'Sync.OfficeCredential.USENET.Username',
                    replace(hex(zeroblob(2000000)), '00', 'X'));
                  """);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => IsolatedStoredCredentialReader.ReadAsync(
                fixture.DatabasePath));

        Assert.Matches(
            "^stored_credentials_rejected reason_code=(value_type_invalid|value_size_exceeded)$",
            error.Message);
        Assert.DoesNotContain(
            "XXXX",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_RejectsSqlGeneratedHugeCredentialKeyBeforeMaterialization()
    {
        await using var fixture =
            await CredentialDatabaseFixture.CreateAsync();
        await ExecuteFixtureSqlAsync(
            fixture.DatabasePath,
            """
            INSERT INTO "Settings" ("Key", "Value")
            VALUES (
                'Sync.OfficeCredential.' ||
                    replace(hex(zeroblob(2000000)), '00', 'K') ||
                    '.Username',
                'bounded-value');
            """);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => IsolatedStoredCredentialReader.ReadAsync(
                fixture.DatabasePath));

        Assert.Equal(
            "stored_credentials_rejected reason_code=key_size_exceeded",
            error.Message);
        Assert.DoesNotContain(
            "KKKK",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("value")]
    public async Task ReadAsync_RejectsEmbeddedNulBeforeMaterialization(
        string nulLocation)
    {
        await using var fixture =
            await CredentialDatabaseFixture.CreateAsync();
        await ExecuteFixtureSqlAsync(
            fixture.DatabasePath,
            nulLocation == "key"
                ? """
                  INSERT INTO "Settings" ("Key", "Value")
                  VALUES (
                    'Sync.OfficeCredential.USENET.' || char(0) || 'Username',
                    'bounded-value');
                  """
                : """
                  INSERT INTO "Settings" ("Key", "Value")
                  VALUES (
                    'Sync.OfficeCredential.USENET.Username',
                    'bounded' || char(0) || 'value');
                  """);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => IsolatedStoredCredentialReader.ReadAsync(
                fixture.DatabasePath));

        Assert.Contains(
            nulLocation == "key"
                ? "reason_code=key_nul_invalid"
                : "reason_code=value_nul_invalid",
            error.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "bounded-value",
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_IgnoresBlobKeyEvenWhenBytesContainCredentialPrefix()
    {
        await using var fixture =
            await CredentialDatabaseFixture.CreateAsync(
                ("Sync.OfficeCredential.USENET.Username", "fixture-user"),
                ("Sync.OfficeCredential.USENET.TenantCode", "USENET_GROUP"),
                ("Sync.OfficeCredential.USENET.PasswordProtected", Protect("bounded-secret")),
                ("Sync.OfficeCredential.USENET.SavedAtUtc", "2026-07-29T00:00:00.0000000Z"));
        await ExecuteFixtureSqlAsync(
            fixture.DatabasePath,
            """
            INSERT INTO "Settings" ("Key", "Value")
            VALUES (
                CAST(
                    'Sync.OfficeCredential.ITWORLD.Username'
                    AS BLOB),
                zeroblob(16777216));
            """);

        var credentials =
            await IsolatedStoredCredentialReader.ReadAsync(
                fixture.DatabasePath);

        var credential = Assert.Single(credentials);
        Assert.Equal("USENET", credential.OfficeCode);
        Assert.Equal("fixture-user", credential.Username);
    }

    [Fact]
    public async Task ReadAsync_IgnoresManyLargeNonCredentialRowsInSql()
    {
        await using var fixture =
            await CredentialDatabaseFixture.CreateAsync(
                ("Sync.OfficeCredential.USENET.Username", "fixture-user"),
                ("Sync.OfficeCredential.USENET.TenantCode", "USENET_GROUP"),
                ("Sync.OfficeCredential.USENET.PasswordProtected", Protect("bounded-secret")),
                ("Sync.OfficeCredential.USENET.SavedAtUtc", "2026-07-29T00:00:00.0000000Z"));
        await ExecuteFixtureSqlAsync(
            fixture.DatabasePath,
            """
            WITH RECURSIVE rows(value) AS (
                SELECT 1
                UNION ALL
                SELECT value + 1 FROM rows WHERE value < 1000
            )
            INSERT INTO "Settings" ("Key", "Value")
            SELECT
                'Unrelated.Setting.' || printf('%04d', value),
                zeroblob(65536)
            FROM rows;
            """);

        var credentials =
            await IsolatedStoredCredentialReader.ReadAsync(
                fixture.DatabasePath);

        var credential = Assert.Single(credentials);
        Assert.Equal("fixture-user", credential.Username);
    }

    [Fact]
    public async Task ReadAsync_RejectsInvalidTenantAndTimestampInsteadOfDefaulting()
    {
        await using var invalidTenant = await CredentialDatabaseFixture.CreateAsync(
            ("Sync.OfficeCredential.ITWORLD.Username", "fixture-user"),
            ("Sync.OfficeCredential.ITWORLD.PasswordProtected", Protect("tenant-secret")),
            ("Sync.OfficeCredential.ITWORLD.TenantCode", "USENET_GROUP"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => IsolatedStoredCredentialReader.ReadAsync(invalidTenant.DatabasePath));

        await using var invalidTimestamp = await CredentialDatabaseFixture.CreateAsync(
            ("Sync.OfficeCredential.USENET.Username", "fixture-user"),
            ("Sync.OfficeCredential.USENET.PasswordProtected", Protect("timestamp-secret")),
            ("Sync.OfficeCredential.USENET.SavedAtUtc", "not-a-timestamp"));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => IsolatedStoredCredentialReader.ReadAsync(invalidTimestamp.DatabasePath));
    }

    private static string Protect(string password)
    {
        var plainBytes = Encoding.UTF8.GetBytes(password);
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = ProtectedData.Protect(
                plainBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
            if (protectedBytes is not null)
                CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    private static string Unprotect(string passwordProtected)
    {
        var protectedBytes = Convert.FromBase64String(passwordProtected);
        byte[]? plainBytes = null;
        try
        {
            plainBytes = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plainBytes is not null)
                CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private static string GetPasswordProtected(object credential)
        => Assert.IsType<string>(
            credential.GetType()
                .GetProperty("PasswordProtected")
                ?.GetValue(credential));

    private static async Task CreateSettingsDatabaseAsync(
        string databasePath,
        params (string Key, string Value)[] settings)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE "Settings" (
                    "Key" TEXT NOT NULL CONSTRAINT "PK_Settings" PRIMARY KEY,
                    "Value" TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync();
        }
        foreach (var (key, value) in settings)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """INSERT INTO "Settings" ("Key", "Value") VALUES ($key, $value);""";
            insert.Parameters.AddWithValue("$key", key);
            insert.Parameters.AddWithValue("$value", value);
            await insert.ExecuteNonQueryAsync();
        }
    }

    private static async Task ExecuteFixtureSqlAsync(
        string databasePath,
        string sql)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class CredentialDatabaseFixture : IAsyncDisposable
    {
        private CredentialDatabaseFixture(string root, string databasePath)
        {
            Root = root;
            DatabasePath = databasePath;
        }

        private string Root { get; }
        public string DatabasePath { get; }

        public static async Task<CredentialDatabaseFixture> CreateAsync(
            params (string Key, string Value)[] settings)
        {
            var root = Path.Combine(
                TestProcessIsolation.TempRoot,
                $"stored-credential-reader-{Guid.NewGuid():N}");
            Assert.Equal(
                "D:\\",
                Path.GetPathRoot(Path.GetFullPath(root)),
                ignoreCase: true);
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "credential-fixture.db");

            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false
                }.ToString());
            await connection.OpenAsync();
            await using (var create = connection.CreateCommand())
            {
                create.CommandText =
                    """
                    CREATE TABLE "Settings" (
                        "Key" TEXT NOT NULL CONSTRAINT "PK_Settings" PRIMARY KEY,
                        "Value" TEXT NOT NULL
                    );
                    """;
                await create.ExecuteNonQueryAsync();
            }

            foreach (var (key, value) in settings)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText =
                    """INSERT INTO "Settings" ("Key", "Value") VALUES ($key, $value);""";
                insert.Parameters.AddWithValue("$key", key);
                insert.Parameters.AddWithValue("$value", value);
                await insert.ExecuteNonQueryAsync();
            }

            return new CredentialDatabaseFixture(root, databasePath);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private static string FindRepositoryRoot(
        [System.Runtime.CompilerServices.CallerFilePath]
        string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(sourceFilePath)
            ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            "Repository root was not found.");
    }
}
