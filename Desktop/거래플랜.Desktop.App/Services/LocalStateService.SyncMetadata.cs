using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    private const int SyncMetadataDbTimeoutSeconds = 2;
    private const int SyncMetadataBusyRetryCount = 2;
    private static readonly TimeSpan SyncMetadataGateTimeout =
        TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _syncMetadataSettingGate = new(1, 1);

    internal async Task SetSyncMetadataSettingIndependentAsync(
        string key,
        string value,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        if (!await _syncMetadataSettingGate.WaitAsync(
                SyncMetadataGateTimeout,
                ct))
        {
            throw new TimeoutException(
                "동기화 메타데이터 저장 대기 시간을 초과했습니다.");
        }

        try
        {
            var connectionStringBuilder =
                GetSyncMetadataConnectionStringBuilder();
            if (RequiresOwningSqliteConnection(connectionStringBuilder))
            {
                ThrowIfOwningConnectionTransactionActive();
                await ExecutePrivateMemoryUpsertAsync(key, value, ct);
            }
            else
            {
                await ExecuteIndependentContextUpsertAsync(
                    connectionStringBuilder,
                    key,
                    value,
                    ct);
            }

            ReconcileTrackedSyncMetadataSetting(key, value);
        }
        finally
        {
            _syncMetadataSettingGate.Release();
        }
    }

    internal async Task DeleteSyncMetadataSettingIndependentAsync(
        string key,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (!await _syncMetadataSettingGate.WaitAsync(
                SyncMetadataGateTimeout,
                ct))
        {
            throw new TimeoutException(
                "동기화 메타데이터 삭제 대기 시간을 초과했습니다.");
        }

        try
        {
            var connectionStringBuilder =
                GetSyncMetadataConnectionStringBuilder();
            if (RequiresOwningSqliteConnection(connectionStringBuilder))
            {
                ThrowIfOwningConnectionTransactionActive();
                await ExecutePrivateMemoryDeleteAsync(key, ct);
            }
            else
            {
                await ExecuteIndependentContextDeleteAsync(
                    connectionStringBuilder,
                    key,
                    ct);
            }

            ReconcileTrackedSyncMetadataDeletion(key);
        }
        finally
        {
            _syncMetadataSettingGate.Release();
        }
    }

    private SqliteConnectionStringBuilder
        GetSyncMetadataConnectionStringBuilder()
    {
        var connectionString = _db.Database.GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = _db.Database.GetDbConnection().ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "동기화 메타데이터를 변경할 로컬 데이터베이스 연결 정보가 없습니다.");
        }

        return new SqliteConnectionStringBuilder(connectionString);
    }

    private void ThrowIfOwningConnectionTransactionActive()
    {
        if (_db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "사설 메모리 SQLite의 활성 트랜잭션 중에는 동기화 메타데이터를 변경할 수 없습니다.");
        }
    }

    private static bool RequiresOwningSqliteConnection(
        SqliteConnectionStringBuilder connectionString)
        => string.Equals(
                connectionString.DataSource,
                ":memory:",
                StringComparison.OrdinalIgnoreCase) ||
           (connectionString.Mode == SqliteOpenMode.Memory &&
            connectionString.Cache != SqliteCacheMode.Shared);

    private async Task ExecutePrivateMemoryUpsertAsync(
        string key,
        string value,
        CancellationToken ct)
    {
        if (_db.Database.GetDbConnection() is not SqliteConnection connection ||
            connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "사설 메모리 SQLite 데이터베이스의 열린 소유 연결이 없습니다.");
        }

        await ExecuteWithBusyRetryAsync(
            async () =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO "Settings" ("Key", "Value")
                    VALUES ($key, $value)
                    ON CONFLICT("Key") DO UPDATE SET "Value" = excluded."Value";
                    """;
                command.CommandTimeout = SyncMetadataDbTimeoutSeconds;
                command.Parameters.Add("$key", SqliteType.Text).Value = key;
                command.Parameters.Add("$value", SqliteType.Text).Value = value;
                await _db.ExecuteRuntimeMutationCommandAsync(
                    () => command.ExecuteNonQueryAsync(ct),
                    ct);
            },
            ct);
    }

    private static async Task ExecuteIndependentContextUpsertAsync(
        SqliteConnectionStringBuilder connectionString,
        string key,
        string value,
        CancellationToken ct)
    {
        connectionString.DefaultTimeout = SyncMetadataDbTimeoutSeconds;
        connectionString.Pooling = false;
        await ExecuteWithBusyRetryAsync(
            async () =>
            {
                var options = new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(connectionString.ConnectionString)
                    .Options;
                await using var metadataDb = new LocalDbContext(options);
                metadataDb.Database.SetCommandTimeout(
                    SyncMetadataDbTimeoutSeconds);
                await metadataDb.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "Settings" ("Key", "Value")
                    VALUES ({key}, {value})
                    ON CONFLICT("Key") DO UPDATE SET "Value" = excluded."Value";
                    """, ct);
            },
            ct);
    }

    private async Task ExecutePrivateMemoryDeleteAsync(
        string key,
        CancellationToken ct)
    {
        if (_db.Database.GetDbConnection() is not SqliteConnection connection ||
            connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "사설 메모리 SQLite 데이터베이스의 열린 소유 연결이 없습니다.");
        }

        await ExecuteWithBusyRetryAsync(
            async () =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM "Settings"
                    WHERE "Key" = $key;
                    """;
                command.CommandTimeout = SyncMetadataDbTimeoutSeconds;
                command.Parameters.Add("$key", SqliteType.Text).Value = key;
                await _db.ExecuteRuntimeMutationCommandAsync(
                    () => command.ExecuteNonQueryAsync(ct),
                    ct);
            },
            ct);
    }

    private static async Task ExecuteIndependentContextDeleteAsync(
        SqliteConnectionStringBuilder connectionString,
        string key,
        CancellationToken ct)
    {
        connectionString.DefaultTimeout = SyncMetadataDbTimeoutSeconds;
        connectionString.Pooling = false;
        await ExecuteWithBusyRetryAsync(
            async () =>
            {
                var options = new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(connectionString.ConnectionString)
                    .Options;
                await using var metadataDb = new LocalDbContext(options);
                metadataDb.Database.SetCommandTimeout(
                    SyncMetadataDbTimeoutSeconds);
                await metadataDb.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM "Settings"
                    WHERE "Key" = {key};
                    """, ct);
            },
            ct);
    }

    private static async Task ExecuteWithBusyRetryAsync(
        Func<Task> operation,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await operation();
                return;
            }
            catch (SqliteException ex)
                when (IsSyncMetadataBusy(ex) &&
                      attempt < SyncMetadataBusyRetryCount)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(40 * (attempt + 1)),
                    ct);
            }
        }
    }

    private void ReconcileTrackedSyncMetadataSetting(
        string key,
        string value)
    {
        var trackedEntry = _db.ChangeTracker
            .Entries<LocalSetting>()
            .FirstOrDefault(entry =>
                string.Equals(entry.Entity.Key, key, StringComparison.Ordinal));
        if (trackedEntry is null)
            return;

        // The metadata UPSERT is authoritative for this key. Marking the one
        // matching entry unchanged prevents a later main-context SaveChanges
        // from inserting a duplicate, deleting the restored row, or reviving
        // the pre-UPSERT value. No unrelated tracked entry is touched.
        trackedEntry.State = EntityState.Unchanged;
        trackedEntry.Property(setting => setting.Value).CurrentValue = value;
        trackedEntry.Property(setting => setting.Value).OriginalValue = value;
        trackedEntry.Property(setting => setting.Value).IsModified = false;
    }

    private void ReconcileTrackedSyncMetadataDeletion(string key)
    {
        var trackedEntry = _db.ChangeTracker
            .Entries<LocalSetting>()
            .FirstOrDefault(entry =>
                string.Equals(entry.Entity.Key, key, StringComparison.Ordinal));
        if (trackedEntry is null)
            return;

        // The independent DELETE is authoritative for this key. Detaching the
        // matching entry prevents a later SaveChanges from inserting, updating,
        // or deleting it again. No unrelated tracked entry is touched.
        trackedEntry.State = EntityState.Detached;
    }

    private static bool IsSyncMetadataBusy(SqliteException exception)
        => exception.SqliteErrorCode is 5 or 6;
}
