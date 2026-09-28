using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalNullableAmountContractTests
{
    [Fact]
    public async Task RentalAmounts_ExistingDatabaseAddsFlagsAndRetainsKnownAmounts()
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var profile=new LocalRentalBillingProfile {Id=Guid.NewGuid(),ProfileKey="rental-contract",MonthlyAmount=132000,DepositAmount=100000};
        var asset=new LocalRentalAsset {Id=Guid.NewGuid(),AssetKey="rental-contract",PurchasePrice=600000,SalePrice=1200000,MonthlyFee=132000};
        var log=new LocalRentalBillingLog {Id=Guid.NewGuid(),BillingProfileId=profile.Id,BilledAmount=132000};
        var history=new LocalRentalAssetAssignmentHistory {Id=Guid.NewGuid(),AssetId=asset.Id,MonthlyFee=132000};
        db.AddRange(profile,asset,log,history);await db.SaveChangesAsync();db.ChangeTracker.Clear();
        foreach(var sql in new[]{"ALTER TABLE RentalBillingProfiles DROP COLUMN AmountsHidden;","ALTER TABLE RentalAssets DROP COLUMN PurchaseAmountsHidden;","ALTER TABLE RentalAssets DROP COLUMN SalesAmountsHidden;","ALTER TABLE RentalBillingLogs DROP COLUMN AmountsHidden;","ALTER TABLE RentalAssetAssignmentHistories DROP COLUMN AmountsHidden;"})
            await db.Database.ExecuteSqlRawAsync(sql);
        var maintain=typeof(LocalDbInitializer).GetMethod("ApplySchemaMaintenanceAsync",BindingFlags.Static|BindingFlags.NonPublic)!;
        await (Task)maintain.Invoke(null,[db])!;await (Task)maintain.Invoke(null,[db])!;
        profile=await db.RentalBillingProfiles.SingleAsync();asset=await db.RentalAssets.SingleAsync();
        log=await db.RentalBillingLogs.SingleAsync();history=await db.RentalAssetAssignmentHistories.SingleAsync();
        Assert.False(profile.AmountsHidden);Assert.False(asset.PurchaseAmountsHidden);Assert.False(asset.SalesAmountsHidden);
        Assert.False(log.AmountsHidden);Assert.False(history.AmountsHidden);
        Assert.Equal(132000m,profile.MonthlyAmount);Assert.Equal(100000m,profile.DepositAmount);
        Assert.Equal(600000m,asset.PurchasePrice);Assert.Equal(1200000m,asset.SalePrice);Assert.Equal(132000m,asset.MonthlyFee);
        Assert.Equal(132000m,log.BilledAmount);Assert.Equal(132000m,history.MonthlyFee);
        profile.AmountsHidden=asset.PurchaseAmountsHidden=asset.SalesAmountsHidden=log.AmountsHidden=history.AmountsHidden=true;
        await db.SaveChangesAsync();db.ChangeTracker.Clear();
        Assert.Null(LocalMappings.ToDto(await db.RentalBillingProfiles.SingleAsync()).MonthlyAmount);
        Assert.Null(LocalMappings.ToDto(await db.RentalAssets.SingleAsync()).MonthlyFee);
        Assert.Null(LocalMappings.ToDto(await db.RentalBillingLogs.SingleAsync()).BilledAmount);
        Assert.Null(LocalMappings.ToDto(await db.RentalAssetAssignmentHistories.SingleAsync()).MonthlyFee);
    }

    public static IEnumerable<object[]> Contracts()
    {
        yield return [typeof(RentalBillingProfileDto),typeof(LocalRentalBillingProfile),new[]{"MonthlyAmount","DepositAmount","SettledAmount","OutstandingAmount"},"AmountsHidden"];
        yield return [typeof(RentalAssetDto),typeof(LocalRentalAsset),new[]{"PurchasePrice"},"PurchaseAmountsHidden"];
        yield return [typeof(RentalAssetDto),typeof(LocalRentalAsset),new[]{"SalePrice","MonthlyFee"},"SalesAmountsHidden"];
        yield return [typeof(RentalBillingLogDto),typeof(LocalRentalBillingLog),new[]{"BilledAmount"},"AmountsHidden"];
        yield return [typeof(RentalAssetAssignmentHistoryDto),typeof(LocalRentalAssetAssignmentHistory),new[]{"MonthlyFee"},"AmountsHidden"];
    }

    [Theory,MemberData(nameof(Contracts))]
    public void RentalAmounts_UnknownAndZeroSurviveJsonAndLocalRoundTrip(Type dtoType,Type localType,string[] fields,string flag)
    {
        var dto=Activator.CreateInstance(dtoType)!;
        var jsonOptions=new JsonSerializerOptions {DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull};
        foreach(var field in fields)
        {
            var property=dtoType.GetProperty(field)!;Assert.Equal(typeof(decimal?),property.PropertyType);
            Assert.Equal(0m,property.GetValue(dto));property.SetValue(dto,null);
        }
        var json=JsonSerializer.Serialize(dto,dtoType,jsonOptions);
        using(var document=JsonDocument.Parse(json))
            foreach(var field in fields) Assert.Equal(JsonValueKind.Null,document.RootElement.GetProperty(field).ValueKind);
        var restored=JsonSerializer.Deserialize(json,dtoType)!;
        var local=typeof(LocalMappings).GetMethod("ToLocal",[dtoType])!.Invoke(null,[restored])!;
        var hidden=localType.GetProperty(flag);Assert.NotNull(hidden);Assert.Equal(true,hidden.GetValue(local));
        var wire=typeof(LocalMappings).GetMethod("ToDto",[localType])!.Invoke(null,[local])!;
        foreach(var field in fields) Assert.Null(dtoType.GetProperty(field)!.GetValue(wire));
        foreach(var field in fields) dtoType.GetProperty(field)!.SetValue(dto,0m);
        var zeroLocal=typeof(LocalMappings).GetMethod("ToLocal",[dtoType])!.Invoke(null,[dto])!;
        Assert.Equal(false,hidden.GetValue(zeroLocal));
        var zeroWire=typeof(LocalMappings).GetMethod("ToDto",[localType])!.Invoke(null,[zeroLocal])!;
        foreach(var field in fields) Assert.Equal(0m,dtoType.GetProperty(field)!.GetValue(zeroWire));
    }
}
