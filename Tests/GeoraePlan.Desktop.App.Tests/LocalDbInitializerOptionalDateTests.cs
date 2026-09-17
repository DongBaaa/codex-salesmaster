using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using 거래플랜.Desktop.App.Data;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalDbInitializerOptionalDateTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2024-05-06T07:08:09Z")]
    [InlineData("1970-01-01T00:00:00Z")]
    public async Task LegacyNormalization_PreservesOptionalEventMeaningAndSyncMetadata(string? value)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var asset = new LocalRentalAsset { Id = Guid.NewGuid(), IsDirty = false, Revision = 77 };
        var invoice = new LocalInvoice { Id = Guid.NewGuid(), IsDirty = false, Revision = 78 };
        db.RentalAssets.Add(asset);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        foreach (var (table, column) in new[] { ("RentalAssets", "LastAssignmentClearedAtUtc"), ("Invoices", "PurchaseReceivedAtUtc") })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE {table} SET {column}=$value";
            command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
        await db.Database.ExecuteSqlRawAsync("UPDATE RentalAssets SET UpdatedAtUtc=''");
        var normalize = typeof(LocalDbInitializer).GetMethod("NormalizeLegacyDateTimeColumnsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        for (var pass = 0; pass < 2; pass++)
        {
            await (Task)normalize.Invoke(null, new object[] { db })!;
            db.ChangeTracker.Clear();
            var savedAsset = await db.RentalAssets.SingleAsync();
            var savedInvoice = await db.Invoices.SingleAsync();
            var expected = string.IsNullOrWhiteSpace(value) ? (DateTime?)null : DateTime.Parse(value).ToUniversalTime();
            Assert.Equal(expected, savedAsset.LastAssignmentClearedAtUtc?.ToUniversalTime());
            Assert.Equal(expected, savedInvoice.PurchaseReceivedAtUtc?.ToUniversalTime());
            Assert.Equal(DateTime.UnixEpoch, savedAsset.UpdatedAtUtc.ToUniversalTime());
            Assert.False(savedAsset.IsDirty);
            Assert.False(savedInvoice.IsDirty);
            Assert.Equal(77, savedAsset.Revision);
            Assert.Equal(78, savedInvoice.Revision);
        }
    }
}
