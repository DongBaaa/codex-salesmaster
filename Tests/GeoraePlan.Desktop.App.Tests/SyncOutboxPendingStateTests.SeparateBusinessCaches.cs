using System.IO;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData("customer")]
    [InlineData("asset")]
    [InlineData("profile")]
    public async Task SeparateBusinessCaches_ApplySameServerIdWithoutReplacingOtherBusiness(string kind)
    {
        var root = Path.Combine(Path.GetTempPath(), "trade-business-pull-tests", Guid.NewGuid().ToString("N"));
        var usenet = new BusinessCacheDatabase(root, "USENET");
        var itworld = new BusinessCacheDatabase(root, "ITWORLD");
        await usenet.CreateNewAsync();
        await itworld.CreateNewAsync();
        var sharedId = Guid.NewGuid();
        Task<string> ReadOwnerAsync(LocalDbContext db) => kind switch
        {
            "customer" => db.Customers.Where(row => row.Id == sharedId).Select(row => row.TenantCode).SingleAsync(),
            "asset" => db.RentalAssets.Where(row => row.Id == sharedId).Select(row => row.TenantCode).SingleAsync(),
            _ => db.RentalBillingProfiles.Where(row => row.Id == sharedId).Select(row => row.TenantCode).SingleAsync()
        };
        await using (var first = await usenet.OpenAsync())
        {
            AddBusinessIdentityFixture(first, kind, sharedId, false);
            first.Items.Add(new LocalItem { Id = Guid.NewGuid(), NameOriginal = "pending USENET item", IsDirty = true });
            first.SyncOutboxEntries.Add(new LocalSyncOutboxEntry { Id = Guid.NewGuid(), EntityId = sharedId,
                EntityName = "preserved-fixture", MutationId = "pending-USENET", Status = "Prepared",
                TenantCode = "USENET_GROUP", OfficeCode = "USENET", BusinessDatabaseName = "USENET" });
            first.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "5" });
            await first.SaveChangesAsync();
        }
        string original;
        await using (var first = await usenet.OpenAsync()) original = SnapshotBusinessIdentityTables(first);

        // Use the real sync application path; the fake HTTP client isn't called.
        await using (var second = await itworld.OpenAsync())
        {
            using var sync = CreateSyncService(second, CreateOnlineOfficeSession("ITWORLD", "ITWORLD"));
            await InvokeApplyPullAndUpdateRevisionAsync(sync, BusinessIdentityPull(kind, sharedId, "ITWORLD", "ITWORLD", false), 0);
        }
        await using (var first = await usenet.OpenAsync())
            Assert.Equal(original, SnapshotBusinessIdentityTables(first));
        string secondSnapshot;
        await using (var second = await itworld.OpenAsync())
        {
            secondSnapshot = SnapshotBusinessIdentityTables(second);
            Assert.Equal("ITWORLD", await ReadOwnerAsync(second));
            Assert.Equal("9", (await second.Settings.SingleAsync(row => row.Key == "LastSyncRevision")).Value);
        }
        await using (var first = await usenet.OpenAsync())
        {
            using var sync = CreateSyncService(first, CreateOnlineOfficeSession("USENET_GROUP", "USENET"));
            await InvokeApplyPullAndUpdateRevisionAsync(sync, BusinessIdentityPull(kind, sharedId, "USENET_GROUP", "USENET", false), 5);
        }
        await using (var second = await itworld.OpenAsync())
            Assert.Equal(secondSnapshot, SnapshotBusinessIdentityTables(second));
        await using (var first = await new BusinessCacheDatabase(root, "YEONSU").OpenAsync())
        {
            Assert.Equal("USENET_GROUP", await ReadOwnerAsync(first));
            Assert.True((await first.Items.SingleAsync()).IsDirty);
            Assert.Equal("Prepared", (await first.SyncOutboxEntries.SingleAsync()).Status);
            Assert.Equal(sharedId, (await first.SyncOutboxEntries.SingleAsync()).EntityId);
            Assert.Equal("9", (await first.Settings.SingleAsync(row => row.Key == "LastSyncRevision")).Value);
        }
    }
}
