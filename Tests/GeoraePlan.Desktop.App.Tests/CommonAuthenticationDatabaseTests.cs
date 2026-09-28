using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class CommonAuthenticationDatabaseTests
{
    private static string Root() => Path.Combine(Path.GetTempPath(), "common-auth-store-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MissingStoreIsNotCreatedAndExistingStoreIsNeverOverwritten()
    {
        var store = new CommonAuthenticationDatabase(Root());
        await Assert.ThrowsAnyAsync<Exception>(() => store.OpenAsync());
        Assert.False(File.Exists(store.DatabasePath));
        await store.CreateNewAsync();
        var before = SHA256.HashData(await File.ReadAllBytesAsync(store.DatabasePath));
        await Assert.ThrowsAsync<IOException>(() => store.CreateNewAsync());
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(store.DatabasePath)));
    }

    [Theory]
    [InlineData("unmarked")]
    [InlineData("business")]
    [InlineData("unknown-format")]
    public async Task InvalidStoreIsRejectedWithoutRepair(string kind)
    {
        var store = new CommonAuthenticationDatabase(Root());
        await store.CreateNewAsync();
        await using (var db = await store.OpenAsync())
        {
            var marker = await db.Settings.SingleAsync(row => row.Key == CommonAuthenticationDatabase.FormatSettingKey);
            if (kind == "unmarked") db.Settings.Remove(marker);
            else if (kind == "unknown-format") marker.Value = "2";
            else db.Settings.Add(new LocalSetting { Key = BusinessCacheDatabase.OwnerSettingKey, Value = "USENET_GROUP" });
            await db.SaveChangesAsync();
        }
        var before = SHA256.HashData(await File.ReadAllBytesAsync(store.DatabasePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.OpenAsync());
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(store.DatabasePath)));
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    public async Task StaleSidecarIsPreservedAndBlocksCreation(string suffix)
    {
        var store = new CommonAuthenticationDatabase(Root());
        Directory.CreateDirectory(Path.GetDirectoryName(store.DatabasePath)!);
        await File.WriteAllTextAsync(store.DatabasePath + suffix, "preserve");
        await Assert.ThrowsAsync<IOException>(() => store.CreateNewAsync());
        Assert.False(File.Exists(store.DatabasePath));
        Assert.Equal("preserve", await File.ReadAllTextAsync(store.DatabasePath + suffix));
    }
}
