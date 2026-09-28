using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class RentalNullableAmountMappingTests
{
    [Fact]
    public void RentalMapping_ProfileUnknownAmountsAndJsonPreserveStoredValues()
    {
        var entity=new RentalBillingProfile {MonthlyAmount=132000,DepositAmount=100000,SettledAmount=55000,OutstandingAmount=77000,BillingTemplateJson="[{\"Amount\":132000}]",BillingRunsJson="[{\"BilledAmount\":132000}]"};
        var dto=entity.ToDto();dto.MonthlyAmount=dto.DepositAmount=dto.SettledAmount=dto.OutstandingAmount=null;
        dto.BillingTemplateJson=dto.BillingRunsJson="[]";dto.Notes="new memo";
        entity.Apply(dto);
        Assert.Equal(132000m,entity.MonthlyAmount);Assert.Equal(100000m,entity.DepositAmount);
        Assert.Equal(55000m,entity.SettledAmount);Assert.Equal(77000m,entity.OutstandingAmount);
        Assert.Contains("132000",entity.BillingTemplateJson);Assert.Contains("132000",entity.BillingRunsJson);Assert.Equal("new memo",entity.Notes);
        dto.MonthlyAmount=dto.DepositAmount=dto.SettledAmount=dto.OutstandingAmount=0m;entity.Apply(dto);
        Assert.Equal(0m,entity.MonthlyAmount);Assert.Equal(0m,entity.DepositAmount);Assert.Equal("[]",entity.BillingRunsJson);
    }

    [Fact]
    public void RentalMapping_AssetUnknownPricesDepositAndOveragePreserveStoredValues()
    {
        var entity=new RentalAsset {PurchasePrice=600000,SalePrice=1200000,MonthlyFee=132000,DepositText="100,000원",BlackOverageUnitPrice=12,ColorOverageUnitPrice=120};
        var dto=entity.ToDto();dto.PurchasePrice=dto.SalePrice=dto.MonthlyFee=null;dto.DepositText=null;
        dto.BlackOverageUnitPrice=dto.ColorOverageUnitPrice=null;dto.Notes="new memo";entity.Apply(dto);
        Assert.Equal(600000m,entity.PurchasePrice);Assert.Equal(1200000m,entity.SalePrice);Assert.Equal(132000m,entity.MonthlyFee);
        Assert.Equal("100,000원",entity.DepositText);Assert.Equal(12m,entity.BlackOverageUnitPrice);Assert.Equal(120m,entity.ColorOverageUnitPrice);Assert.Equal("new memo",entity.Notes);
        dto.PurchasePrice=dto.SalePrice=dto.MonthlyFee=0;dto.DepositText="";entity.Apply(dto);
        Assert.Equal(0m,entity.MonthlyFee);Assert.Equal("",entity.DepositText);Assert.Null(entity.BlackOverageUnitPrice);
    }

    [Fact]
    public void RentalMapping_LogUnknownAmountPreservesStoredValue()
    {
        var entity=new RentalBillingLog {BilledAmount=132000};var dto=entity.ToDto();dto.BilledAmount=null;dto.Note="new memo";entity.Apply(dto);
        Assert.Equal(132000m,entity.BilledAmount);Assert.Equal("new memo",entity.Note);
        dto.BilledAmount=0;entity.Apply(dto);Assert.Equal(0m,entity.BilledAmount);
    }

    [Fact]
    public void RentalMapping_HistoryUnknownFeePreservesStoredValue()
    {
        var entity=new RentalAssetAssignmentHistory {MonthlyFee=132000};var dto=entity.ToDto();dto.MonthlyFee=null;dto.ChangeReason="new memo";entity.Apply(dto);
        Assert.Equal(132000m,entity.MonthlyFee);Assert.Equal("new memo",entity.ChangeReason);
        dto.MonthlyFee=0;entity.Apply(dto);Assert.Equal(0m,entity.MonthlyFee);
    }
}
