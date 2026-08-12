using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncDiagReadOnlyIntegrityReportTests
{
    [Fact]
    public async Task Command_ReportsDuplicateFixtureWithoutChangingDatabaseOrCreatingSidecars()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GeoraePlan.SyncDiag.ReadOnlyIntegrity.Tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "integrity-fixture.db");
        var childAppRoot = Path.Combine(root, "child-app-root");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(childAppRoot);

        try
        {
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false
                }.ToString())
                .Options;
            await using (var db = new LocalDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Customers.AddRange(
                    CreateCustomer(
                        "11111111-1111-1111-1111-111111111111",
                        "READ ONLY DUPLICATE"),
                    CreateCustomer(
                        "22222222-2222-2222-2222-222222222222",
                        "READ ONLY DUPLICATE"));
                await db.SaveChangesAsync();
                await db.Database.CloseConnectionAsync();
            }

            SqliteConnection.ClearAllPools();
            Assert.Empty(SnapshotSidecars(databasePath));
            var hashBefore = ComputeSha256(databasePath);
            var lengthBefore = new FileInfo(databasePath).Length;
            var lastWriteBefore = File.GetLastWriteTimeUtc(databasePath);

            var repositoryRoot = FindRepositoryRoot();
            var result = await RunProcessAsync(
                CreateStartInfo(
                    repositoryRoot,
                    databasePath,
                    childAppRoot),
                TimeSpan.FromSeconds(30));

            Assert.Equal(0, result.ExitCode);
            Assert.True(
                string.IsNullOrWhiteSpace(result.StandardError),
                result.StandardError);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var report = document.RootElement;
            Assert.Equal(
                "read_only",
                report.GetProperty("inspectionMode").GetString());
            Assert.Equal(
                "immutable_sidecar_free_database",
                report.GetProperty("inspectionSource").GetString());
            Assert.Equal(
                TenantScopeCatalog.UsenetGroup,
                report.GetProperty("tenantCode").GetString());
            Assert.Equal(
                OfficeCodeCatalog.Usenet,
                report.GetProperty("officeCode").GetString());
            Assert.Equal(1, report.GetProperty("TotalIssueCount").GetInt32());
            var summary = Assert.Single(
                report.GetProperty("summaries").EnumerateArray());
            Assert.Equal(
                "customer_duplicate_candidate",
                summary.GetProperty("Code").GetString());
            Assert.Equal(1, summary.GetProperty("Count").GetInt32());
            Assert.Equal(JsonValueKind.Null, report.GetProperty("details").ValueKind);

            Assert.Equal(hashBefore, ComputeSha256(databasePath));
            Assert.Equal(lengthBefore, new FileInfo(databasePath).Length);
            Assert.Equal(lastWriteBefore, File.GetLastWriteTimeUtc(databasePath));
            Assert.Empty(SnapshotSidecars(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Command_IncludeDetails_ReportsItemComparisonSafetyWithoutChangingDatabase()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "GeoraePlan.SyncDiag.ReadOnlyIntegrity.Tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "item-comparison-fixture.db");
        var childAppRoot = Path.Combine(root, "child-app-root");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(childAppRoot);

        try
        {
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false
                }.ToString())
                .Options;
            await using (var db = new LocalDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.Items.AddRange(
                    CreateItem(
                        "33333333-3333-3333-3333-333333333333",
                        "READ ONLY ITEM",
                        "MODEL-A",
                        "SERIAL-A"),
                    CreateItem(
                        "44444444-4444-4444-4444-444444444444",
                        "READ ONLY ITEM",
                        "MODEL-A",
                        "SERIAL-B"));
                await db.SaveChangesAsync();
                await db.Database.CloseConnectionAsync();
            }

            SqliteConnection.ClearAllPools();
            Assert.Empty(SnapshotSidecars(databasePath));
            var hashBefore = ComputeSha256(databasePath);
            var lengthBefore = new FileInfo(databasePath).Length;
            var lastWriteBefore = File.GetLastWriteTimeUtc(databasePath);

            var result = await RunProcessAsync(
                CreateStartInfo(
                    FindRepositoryRoot(),
                    databasePath,
                    childAppRoot,
                    includeDetails: true),
                TimeSpan.FromSeconds(30));

            Assert.Equal(0, result.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(result.StandardError), result.StandardError);
            using var document = JsonDocument.Parse(result.StandardOutput);
            var itemIssue = Assert.Single(
                document.RootElement.GetProperty("details").EnumerateArray(),
                detail => string.Equals(
                    detail.GetProperty("Code").GetString(),
                    "item_duplicate_candidate",
                    StringComparison.Ordinal));
            var comparison = itemIssue.GetProperty("itemDuplicateComparison");
            Assert.Equal(2, comparison.GetProperty("candidateCount").GetInt32());
            Assert.False(comparison.GetProperty("CanMerge").GetBoolean());
            Assert.Contains(
                comparison.GetProperty("BlockingConflictFields").EnumerateArray(),
                value => string.Equals(value.GetString(), "SerialNumber", StringComparison.Ordinal));
            Assert.Equal(0, comparison.GetProperty("TotalReferenceCount").GetInt32());
            Assert.Equal(0, comparison.GetProperty("referenceBreakdown").GetProperty("rentalAssets").GetInt32());
            Assert.Equal(0, comparison.GetProperty("referenceBreakdown").GetProperty("itemPriceGrades").GetInt32());

            Assert.Equal(hashBefore, ComputeSha256(databasePath));
            Assert.Equal(lengthBefore, new FileInfo(databasePath).Length);
            Assert.Equal(lastWriteBefore, File.GetLastWriteTimeUtc(databasePath));
            Assert.Empty(SnapshotSidecars(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static LocalCustomer CreateCustomer(string id, string name)
        => new()
        {
            Id = Guid.Parse(id),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = name,
            NameMatchKey = name,
            IsDirty = false
        };

    private static LocalItem CreateItem(
        string id,
        string name,
        string specification,
        string serialNumber)
        => new()
        {
            Id = Guid.Parse(id),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = name,
            NameMatchKey = name,
            SpecificationOriginal = specification,
            SpecificationMatchKey = specification,
            SerialNumber = serialNumber,
            IsDirty = false
        };

    private static ProcessStartInfo CreateStartInfo(
        string repositoryRoot,
        string databasePath,
        string childAppRoot,
        bool includeDetails = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetPath(),
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(ResolveSyncDiagToolPath(repositoryRoot));
        startInfo.ArgumentList.Add("read-only-integrity-report");
        startInfo.ArgumentList.Add(databasePath);
        startInfo.ArgumentList.Add(TenantScopeCatalog.UsenetGroup);
        startInfo.ArgumentList.Add(OfficeCodeCatalog.Usenet);
        if (includeDetails)
            startInfo.ArgumentList.Add("--include-details");
        startInfo.Environment["GEORAEPLAN_TEST_MODE"] = "1";
        startInfo.Environment["GEORAEPLAN_APP_ROOT"] = childAppRoot;
        return startInfo;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The SyncDiag child process did not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await Task.WhenAll(
                    process.WaitForExitAsync(),
                    stdoutTask,
                    stderrTask)
                .WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }

            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string ResolveDotnetPath()
    {
        const string isolatedSdk = @"D:\.dotnet-sdk\dotnet.exe";
        if (File.Exists(isolatedSdk))
            return isolatedSdk;

        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrWhiteSpace(configured) && File.Exists(configured)
            ? configured
            : "dotnet";
    }

    private static string ResolveSyncDiagToolPath(string repositoryRoot)
    {
        var assemblyDirectory = new DirectoryInfo(
            Path.GetDirectoryName(typeof(SyncDiagReadOnlyIntegrityReportTests).Assembly.Location)
            ?? throw new InvalidOperationException(
                "The test assembly directory was not found."));
        var configuration = assemblyDirectory.Parent?.Name
            ?? throw new InvalidOperationException(
                "The test build configuration was not found.");
        var toolPath = Path.Combine(
            repositoryRoot,
            "tools",
            "SyncDiag",
            "bin",
            configuration,
            assemblyDirectory.Name,
            "SyncDiag.dll");
        Assert.True(File.Exists(toolPath), $"SyncDiag was not built: {toolPath}");
        return toolPath;
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string[] SnapshotSidecars(string databasePath)
        => new[] { "-wal", "-shm", "-journal" }
            .Select(suffix => databasePath + suffix)
            .Where(File.Exists)
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException(
                "The test source directory was not found."));
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "tools", "SyncDiag")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Desktop")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "The repository root could not be found.");
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
