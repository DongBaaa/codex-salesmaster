using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Data;

// Explicit, immutable database selection. This does not authorize an account or
// migrate the legacy mixed cache. Callers must authorize the selected business.
internal sealed class BusinessCacheDatabase
{
    internal const string OwnerSettingKey = "BusinessCache.Owner.v1";
    internal const string FormatSettingKey = "BusinessCache.Format";
    private const string FormatVersion = "1";

    public string TenantCode { get; }
    public string BusinessDatabaseName { get; }
    public string DatabasePath { get; }

    public BusinessCacheDatabase(string cacheRoot, string businessCode)
    {
        if (!Path.IsPathFullyQualified(cacheRoot))
            throw new ArgumentException("업체 캐시에는 절대 경로가 필요합니다.", nameof(cacheRoot));

        // The general catalog intentionally accepts display labels and defaults.
        // Storage selection must not silently route an unknown value to USENET.
        var code = (businessCode ?? string.Empty).Trim().ToUpperInvariant();
        TenantCode = code switch
        {
            "USENET" or "USENET_GROUP" or "UZNET" or "YEONSU" => TenantScopeCatalog.UsenetGroup,
            "ITWORLD" => TenantScopeCatalog.Itworld,
            _ when TenantScopeCatalog.TryNormalizeCustomTenantCode(code, out var tenant) => tenant,
            _ => throw new ArgumentException("확인할 수 없는 업체 코드입니다.", nameof(businessCode))
        };
        BusinessDatabaseName = TenantScopeCatalog.GetDatabaseName(TenantCode);
        DatabasePath = Path.Combine(Path.GetFullPath(cacheRoot), BusinessDatabaseName, "거래플랜.db");
        ValidatePath();
    }

    public async Task CreateNewAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ValidatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        ValidatePath();
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(DatabasePath + suffix) || Directory.Exists(DatabasePath + suffix))
                throw new IOException("이전 SQLite 보조 파일이 있어 새 업체 캐시를 만들 수 없습니다.");

        // CreateNew arbitrates concurrent initializers and never adopts a legacy
        // file. A failed initialization remains unmarked and cannot be opened.
        using (new FileStream(DatabasePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        await using var db = CreateContext();
        await db.Database.OpenConnectionAsync(ct);
        ValidatePath();
        await db.Database.EnsureCreatedAsync(ct);
        db.Settings.AddRange(
            new LocalSetting { Key = OwnerSettingKey, Value = TenantCode },
            new LocalSetting { Key = FormatSettingKey, Value = FormatVersion });
        await db.SaveChangesAsync(ct);
    }

    public async Task<LocalDbContext> OpenAsync(CancellationToken ct = default)
    {
        ValidatePath();
        var db = CreateContext();
        try
        {
            // ReadWrite never creates a missing database. Keep this connection
            // open so the validated handle is also used for subsequent work.
            await db.Database.OpenConnectionAsync(ct);
            ValidatePath();
            var markers = await db.Settings.AsNoTracking()
                .Where(row => row.Key == OwnerSettingKey || row.Key == FormatSettingKey)
                .ToDictionaryAsync(row => row.Key, row => row.Value, ct);
            if (!markers.TryGetValue(OwnerSettingKey, out var owner) || owner != TenantCode ||
                !markers.TryGetValue(FormatSettingKey, out var format) || format != FormatVersion)
                throw new InvalidOperationException("업체 캐시의 소유 정보 또는 형식이 일치하지 않습니다. 원본은 변경하지 않았습니다.");
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private LocalDbContext CreateContext()
    {
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true
        };
        return new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection.ConnectionString).Options);
    }

    private void ValidatePath()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            AppPaths.EnsureNoExistingReparsePointInPathChain(DatabasePath + suffix, "BusinessCacheDatabase");
    }
}
