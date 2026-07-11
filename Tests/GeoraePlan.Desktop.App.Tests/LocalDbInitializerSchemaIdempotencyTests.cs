using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using 거래플랜.Desktop.App.Data;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalDbInitializerSchemaIdempotencyTests
{
    [Fact]
    public async Task TryAddColumnAsync_AddsMissingColumn_WhenColumnDoesNotExist()
    {
        var tempRoot = CreateTempRoot("add-missing-column");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            var interceptor = new SqlCommandCaptureInterceptor();
            await using var db = CreateDbContext(dbPath, interceptor);
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"SchemaProbe\" (\"Id\" INTEGER NOT NULL);");
            interceptor.Clear();

            await InvokeTryAddColumnAsync(db, "SchemaProbe", "ProbeValue", "TEXT NOT NULL DEFAULT ''");

            var columns = await GetTableColumnsAsync(db, "SchemaProbe");
            Assert.Contains("ProbeValue", columns);
            Assert.Contains(interceptor.NonQueryCommands, sql => ContainsAddColumnCommand(sql, "SchemaProbe", "ProbeValue"));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task TryAddColumnAsync_SkipsExistingColumn_CaseInsensitively()
    {
        var tempRoot = CreateTempRoot("skip-existing-column-case");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            var interceptor = new SqlCommandCaptureInterceptor();
            await using var db = CreateDbContext(dbPath, interceptor);
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"SchemaProbe\" (\"Id\" INTEGER NOT NULL, \"TenantCode\" TEXT NOT NULL DEFAULT '');");
            interceptor.Clear();

            await InvokeTryAddColumnAsync(db, "SchemaProbe", "tenantcode", "TEXT NOT NULL DEFAULT ''");

            var columns = await GetTableColumnsAsync(db, "SchemaProbe");
            Assert.Single(columns, column => string.Equals(column, "TenantCode", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(interceptor.NonQueryCommands, sql => ContainsAddColumnCommand(sql, "SchemaProbe", "tenantcode"));
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task TryAddColumnAsync_IgnoresUnsafeIdentifiers()
    {
        var tempRoot = CreateTempRoot("ignore-unsafe-identifier");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            var interceptor = new SqlCommandCaptureInterceptor();
            await using var db = CreateDbContext(dbPath, interceptor);
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"SchemaProbe\" (\"Id\" INTEGER NOT NULL);");
            interceptor.Clear();

            await InvokeTryAddColumnAsync(db, "SchemaProbe", "Bad-Column", "TEXT NOT NULL DEFAULT ''");

            var columns = await GetTableColumnsAsync(db, "SchemaProbe");
            Assert.DoesNotContain(columns, column => string.Equals(column, "Bad-Column", StringComparison.Ordinal));
            Assert.Empty(interceptor.NonQueryCommands);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task InitializeAsync_SecondRun_DoesNotAttemptDuplicateAddColumn()
    {
        var tempRoot = CreateTempRoot("initialize-twice");
        var dbPath = Path.Combine(tempRoot, "local.db");

        try
        {
            await using (var firstDb = CreateDbContext(dbPath))
            {
                await firstDb.Database.EnsureDeletedAsync();
                await LocalDbInitializer.InitializeAsync(firstDb);
            }

            var interceptor = new SqlCommandCaptureInterceptor();
            await using var secondDb = CreateDbContext(dbPath, interceptor);

            await LocalDbInitializer.InitializeAsync(secondDb);

            Assert.True(await secondDb.Settings.AsNoTracking().AnyAsync(setting => setting.Key == "Theme"));
            Assert.DoesNotContain(interceptor.NonQueryCommands, IsAlterTableAddColumnCommand);
        }
        finally
        {
            CleanupTempRoot(tempRoot);
        }
    }

    private static LocalDbContext CreateDbContext(string dbPath, DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={dbPath}");

        if (interceptor is not null)
            builder.AddInterceptors(interceptor);

        return new LocalDbContext(builder.Options);
    }

    private static async Task InvokeTryAddColumnAsync(LocalDbContext db, string tableName, string columnName, string definition)
    {
        var method = typeof(LocalDbInitializer).GetMethod(
            "TryAddColumnAsync",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var task = method!.Invoke(null, new object?[] { db, tableName, columnName, definition }) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static async Task<IReadOnlyList<string>> GetTableColumnsAsync(LocalDbContext db, string tableName)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;

        if (shouldClose)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
            await using var reader = await command.ExecuteReaderAsync();
            var columns = new List<string>();
            while (await reader.ReadAsync())
            {
                columns.Add(reader["name"]?.ToString() ?? string.Empty);
            }

            return columns;
        }
        finally
        {
            if (shouldClose)
                await connection.CloseAsync();
        }
    }

    private static bool ContainsAddColumnCommand(string sql, string tableName, string columnName)
        => sql.Contains("ALTER TABLE", StringComparison.OrdinalIgnoreCase)
           && sql.Contains("ADD COLUMN", StringComparison.OrdinalIgnoreCase)
           && sql.Contains($"\"{tableName}\"", StringComparison.OrdinalIgnoreCase)
           && sql.Contains($"\"{columnName}\"", StringComparison.OrdinalIgnoreCase);

    private static bool IsAlterTableAddColumnCommand(string sql)
        => sql.Contains("ALTER TABLE", StringComparison.OrdinalIgnoreCase)
           && sql.Contains("ADD COLUMN", StringComparison.OrdinalIgnoreCase);

    private static string CreateTempRoot(string scenario)
    {
        var tempRoot = Path.Combine(
            FindRepositoryRoot(),
            "temp",
            "localdb-schema-tests",
            $"{scenario}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static void CleanupTempRoot(string tempRoot)
    {
        SqliteConnection.ClearAllPools();

        if (!Directory.Exists(tempRoot))
            return;

        try
        {
            Directory.Delete(tempRoot, recursive: true);
        }
        catch
        {
            // Ignore temp cleanup failures during tests.
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜 저장소 루트를 찾지 못했습니다.");
    }

    private sealed class SqlCommandCaptureInterceptor : DbCommandInterceptor
    {
        public List<string> NonQueryCommands { get; } = [];

        public void Clear() => NonQueryCommands.Clear();

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            NonQueryCommands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            NonQueryCommands.Add(command.CommandText);
            return new ValueTask<InterceptionResult<int>>(result);
        }
    }
}
