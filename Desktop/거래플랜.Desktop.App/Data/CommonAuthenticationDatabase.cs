using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Infrastructure;

namespace 거래플랜.Desktop.App.Data;

// Explicit opt-in until the legacy cache has been migrated as a complete set.
// Never adopt an existing unmarked file or a business database as authentication storage.
internal sealed class CommonAuthenticationDatabase
{
    internal const string FormatSettingKey = "CommonAuthentication.Format";
    private const string FormatVersion = "1";
    public string DatabasePath { get; }

    public CommonAuthenticationDatabase(string cacheRoot)
    {
        if (!Path.IsPathFullyQualified(cacheRoot))
            throw new ArgumentException("공통 인증 저장소에는 절대 경로가 필요합니다.", nameof(cacheRoot));
        DatabasePath = Path.Combine(Path.GetFullPath(cacheRoot), "common", "authentication.db");
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
                throw new IOException("이전 SQLite 보조 파일이 있어 공통 인증 저장소를 만들 수 없습니다.");
        using (new FileStream(DatabasePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        await using var db = CreateContext();
        await db.Database.OpenConnectionAsync(ct);
        ValidatePath();
        await db.Database.EnsureCreatedAsync(ct);
        db.Settings.Add(new LocalSetting { Key = FormatSettingKey, Value = FormatVersion });
        await db.SaveChangesAsync(ct);
    }

    public async Task<LocalDbContext> OpenAsync(CancellationToken ct = default)
    {
        ValidatePath();
        var db = CreateContext();
        try
        {
            await db.Database.OpenConnectionAsync(ct);
            ValidatePath();
            var markers = await db.Settings.AsNoTracking()
                .Where(row => row.Key == FormatSettingKey || row.Key == BusinessCacheDatabase.OwnerSettingKey)
                .ToDictionaryAsync(row => row.Key, row => row.Value, ct);
            if (!markers.TryGetValue(FormatSettingKey, out var version) || version != FormatVersion ||
                markers.ContainsKey(BusinessCacheDatabase.OwnerSettingKey))
                throw new InvalidOperationException("공통 인증 저장소의 형식이 일치하지 않습니다. 원본은 변경하지 않았습니다.");
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
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWrite,
            Pooling = false, ForeignKeys = true, DefaultTimeout = 2
        };
        var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection.ConnectionString).Options);
        db.Database.SetCommandTimeout(2);
        return db;
    }

    private void ValidatePath()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            AppPaths.EnsureNoExistingReparsePointInPathChain(DatabasePath + suffix, "CommonAuthenticationDatabase");
    }
}
