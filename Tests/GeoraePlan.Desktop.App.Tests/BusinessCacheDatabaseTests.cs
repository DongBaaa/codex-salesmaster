using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class BusinessCacheDatabaseTests
{
    private static string NewRoot()
        => Path.Combine(Path.GetTempPath(), "trade-business-cache-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("USENET")]
    [InlineData("USENET_GROUP")]
    [InlineData("YEONSU")]
    [InlineData("uznet")]
    public void ParentAndBranchSelectSamePhysicalCache(string code)
    {
        var root = NewRoot();
        var selected = new BusinessCacheDatabase(root, code);
        Assert.Equal(new BusinessCacheDatabase(root, "USENET").DatabasePath, selected.DatabasePath);
        Assert.Equal("USENET_GROUP", selected.TenantCode);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("SHARED")]
    [InlineData("unknown")]
    [InlineData("../ITWORLD")]
    [InlineData("유즈넷/../../other")]
    [InlineData("ORG_BAD/../../other")]
    public void UnknownOrPathLikeCodeCannotDefaultToUsenet(string code)
        => Assert.Throws<ArgumentException>(() => new BusinessCacheDatabase(NewRoot(), code));

    [Fact]
    public void CustomTenantHasSeparateDeterministicPath()
    {
        var root = NewRoot();
        var custom = new BusinessCacheDatabase(root, "org_sample");
        Assert.Equal("ORG_SAMPLE", custom.TenantCode);
        Assert.NotEqual(new BusinessCacheDatabase(root, "USENET").DatabasePath, custom.DatabasePath);
        Assert.Throws<ArgumentException>(() => new BusinessCacheDatabase("relative", "USENET"));
    }

    [Fact]
    public async Task MissingOrUnmarkedFileCannotBeAdoptedOrOverwritten()
    {
        var root = NewRoot();
        var cache = new BusinessCacheDatabase(root, "ITWORLD");
        Directory.CreateDirectory(Path.GetDirectoryName(cache.DatabasePath)!);
        await Assert.ThrowsAsync<SqliteException>(() => cache.OpenAsync());
        Assert.False(File.Exists(cache.DatabasePath));
        await using (var legacy = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = cache.DatabasePath, Pooling = false }.ConnectionString).Options))
            await legacy.Database.EnsureCreatedAsync();
        var before = SHA256.HashData(await File.ReadAllBytesAsync(cache.DatabasePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.OpenAsync());
        await Assert.ThrowsAsync<IOException>(() => cache.CreateNewAsync());
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(cache.DatabasePath)));
    }

    [Theory]
    [InlineData("BusinessCache.Owner.v1", "ITWORLD")]
    [InlineData("BusinessCache.Format", "2")]
    public async Task WrongOwnerOrUnknownVersionIsRejectedWithoutRepair(string key, string value)
    {
        var cache = new BusinessCacheDatabase(NewRoot(), "USENET");
        await cache.CreateNewAsync();
        await using (var db = await cache.OpenAsync())
        {
            var marker = await db.Settings.SingleAsync(row => row.Key == key);
            marker.Value = value;
            await db.SaveChangesAsync();
        }
        var before = SHA256.HashData(await File.ReadAllBytesAsync(cache.DatabasePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.OpenAsync());
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(cache.DatabasePath)));
    }

    [Fact]
    public async Task CancelledCreationDoesNotTouchDisk()
    {
        var root = NewRoot();
        var cache = new BusinessCacheDatabase(root, "USENET");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.CreateNewAsync(new CancellationToken(true)));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    public async Task OrphanSidecarIsPreservedAndBlocksNewDatabase(string suffix)
    {
        var cache = new BusinessCacheDatabase(NewRoot(), "USENET");
        Directory.CreateDirectory(Path.GetDirectoryName(cache.DatabasePath)!);
        await File.WriteAllTextAsync(cache.DatabasePath + suffix, "preserve");
        await Assert.ThrowsAsync<IOException>(() => cache.CreateNewAsync());
        Assert.False(File.Exists(cache.DatabasePath));
        Assert.Equal("preserve", await File.ReadAllTextAsync(cache.DatabasePath + suffix));
    }

    [Fact]
    public async Task ConcurrentInitializersPublishOnlyOneOwner()
    {
        var cache = new BusinessCacheDatabase(NewRoot(), "USENET");
        async Task<Exception?> Attempt()
        {
            try { await cache.CreateNewAsync(); return null; }
            catch (Exception exception) { return exception; }
        }
        var attempts = await Task.WhenAll(Task.Run(Attempt), Task.Run(Attempt));
        Assert.Single(attempts, result => result is null);
        Assert.IsType<IOException>(Assert.Single(attempts, result => result is not null));
        await using var db = await cache.OpenAsync();
        Assert.Equal("USENET_GROUP", (await db.Settings.SingleAsync(row => row.Key == BusinessCacheDatabase.OwnerSettingKey)).Value);
    }

    [Fact]
    public async Task SameIdDifferentBusinessesKeepValuesAndPendingReceiptsAfterReopen()
    {
        var root = NewRoot();
        var usenet = new BusinessCacheDatabase(root, "USENET");
        var itworld = new BusinessCacheDatabase(root, "ITWORLD");
        await usenet.CreateNewAsync();
        await itworld.CreateNewAsync();
        var id = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        foreach (var cache in new[] { usenet, itworld })
        {
            await using var db = await cache.OpenAsync();
            db.Customers.Add(new LocalCustomer { Id = id, TenantCode = cache.TenantCode,
                OfficeCode = cache.BusinessDatabaseName, ResponsibleOfficeCode = cache.BusinessDatabaseName,
                NameOriginal = cache.BusinessDatabaseName, NameMatchKey = cache.BusinessDatabaseName,
                IsDirty = cache == usenet });
            if (cache == usenet)
                db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry { Id = receiptId, EntityId = id,
                    EntityName = "LocalCustomer", MutationId = "unsent-receipt", Status = "Prepared",
                    TenantCode = cache.TenantCode, OfficeCode = "USENET", BusinessDatabaseName = "USENET" });
            await db.SaveChangesAsync();
        }
        var usenetHash = SHA256.HashData(await File.ReadAllBytesAsync(usenet.DatabasePath));
        await using (var other = await itworld.OpenAsync())
        {
            (await other.Customers.SingleAsync()).NameOriginal = "ITWORLD updated";
            await other.SaveChangesAsync();
        }
        Assert.Equal(usenetHash, SHA256.HashData(await File.ReadAllBytesAsync(usenet.DatabasePath)));
        await using var first = await new BusinessCacheDatabase(root, "YEONSU").OpenAsync();
        Assert.Equal("USENET", (await first.Customers.SingleAsync()).NameOriginal);
        Assert.True((await first.Customers.SingleAsync()).IsDirty);
        Assert.Equal(receiptId, (await first.SyncOutboxEntries.SingleAsync()).Id);
        Assert.Equal("Prepared", (await first.SyncOutboxEntries.SingleAsync()).Status);
        await using var second = await itworld.OpenAsync();
        Assert.Equal(id, (await second.Customers.SingleAsync()).Id);
        Assert.Equal("ITWORLD updated", (await second.Customers.SingleAsync()).NameOriginal);
        Assert.Empty(await second.SyncOutboxEntries.ToListAsync());
    }
}
