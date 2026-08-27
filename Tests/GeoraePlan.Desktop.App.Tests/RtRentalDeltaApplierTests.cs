using GeoraePlan.Tools.SyncDiag;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RtRentalDeltaApplierTests
{
    [Fact]
    public void PrepareMutations_ChangesOnlyApprovedScalarFields()
    {
        var asset = CreateAsset();
        var plan = CreatePlan(asset);
        plan.Entries[0].Values.ItemName = "변경 모델";
        plan.Entries[0].Values.InstallLocation = "변경 설치위치";
        plan.Entries[0].Values.ContractMonths = 48;

        var prepared = RtRentalDeltaApplier.PrepareMutations(
            plan,
            new string('A', 64),
            [asset]);

        var mutation = Assert.Single(prepared.Mutations);
        Assert.Equal("변경 모델", mutation.ItemName);
        Assert.Equal("변경 설치위치", mutation.InstallLocation);
        Assert.Equal(48, mutation.ContractMonths);
        Assert.Equal("USENET_GROUP", mutation.TenantCode);
        Assert.Equal("USENET", mutation.OfficeCode);
        Assert.Equal("YEONSU", mutation.ResponsibleOfficeCode);
        Assert.Equal(asset.CustomerId, mutation.CustomerId);
        Assert.Equal(asset.BillingProfileId, mutation.BillingProfileId);
        Assert.Equal(asset.ManagementNumber, mutation.ManagementNumber);
        Assert.Equal(asset.AssetStatus, mutation.AssetStatus);
        Assert.Equal(asset.Notes, mutation.Notes);
        Assert.Equal(asset.Revision, mutation.ExpectedRevision);
        Assert.StartsWith("rt-rental-", mutation.MutationId, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareMutations_RejectsFeeChangeForProfileLinkedAsset()
    {
        var asset = CreateAsset();
        var plan = CreatePlan(asset);
        plan.Entries[0].Values.MonthlyFee = asset.MonthlyFee + 1;

        var exception = Assert.Throws<InvalidDataException>(() =>
            RtRentalDeltaApplier.PrepareMutations(
                plan,
                new string('B', 64),
                [asset]));

        Assert.Contains("billing profile", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrepareMutations_RejectsCrossOfficeItworldAsset()
    {
        var asset = CreateAsset();
        asset.TenantCode = "ITWORLD";
        asset.OfficeCode = "ITWORLD";
        asset.ManagementCompanyCode = "ITWORLD";
        asset.ResponsibleOfficeCode = "USENET";
        var plan = CreatePlan(asset);
        plan.BusinessDatabaseName = "ITWORLD";
        plan.Entries[0].Values.ItemName = "변경 모델";

        var exception = Assert.Throws<InvalidDataException>(() =>
            RtRentalDeltaApplier.PrepareMutations(
                plan,
                new string('C', 64),
                [asset]));

        Assert.Contains("Cross-office ITWORLD", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepareMutations_RejectsRevisionDrift()
    {
        var asset = CreateAsset();
        var plan = CreatePlan(asset);
        plan.Entries[0].Values.ItemName = "변경 모델";
        asset.Revision++;

        Assert.Throws<InvalidDataException>(() =>
            RtRentalDeltaApplier.PrepareMutations(
                plan,
                new string('D', 64),
                [asset]));
    }

    private static RtRentalDeltaPlan CreatePlan(RentalAssetDto asset)
        => new()
        {
            SchemaVersion = 1,
            PlanId = "rt-rental-test",
            BusinessDatabaseName = "USENET",
            SourceSha256 = new string('E', 64),
            GeneratedAtUtc = DateTime.UtcNow,
            Entries =
            [
                new RtRentalDeltaPlanEntry
                {
                    AssetId = asset.Id,
                    ExpectedRevision = asset.Revision,
                    ExpectedUpdatedAtUtc = asset.UpdatedAtUtc,
                    ExpectedTenantCode = asset.TenantCode,
                    ExpectedOfficeCode = asset.OfficeCode,
                    ExpectedManagementCompanyCode = asset.ManagementCompanyCode,
                    ExpectedResponsibleOfficeCode = asset.ResponsibleOfficeCode,
                    ExpectedManagementNumber = asset.ManagementNumber,
                    ExpectedAssetStatus = asset.AssetStatus,
                    Values = new RtRentalScalarValues
                    {
                        CurrentLocation = asset.CurrentLocation,
                        ItemCategoryName = asset.ItemCategoryName,
                        Manufacturer = asset.Manufacturer,
                        ItemName = asset.ItemName,
                        MachineNumber = asset.MachineNumber,
                        PurchaseVendor = asset.PurchaseVendor,
                        PurchaseDate = asset.PurchaseDate,
                        DisposalDate = asset.DisposalDate,
                        PurchasePrice = asset.PurchasePrice,
                        SalePrice = asset.SalePrice,
                        InstallLocation = asset.InstallLocation,
                        DepositText = asset.DepositText,
                        MonthlyFee = asset.MonthlyFee,
                        ContractMonths = asset.ContractMonths,
                        ContractDate = asset.ContractDate,
                        InstallDate = asset.InstallDate,
                        ContractStartDate = asset.ContractStartDate,
                        RentalEndDate = asset.RentalEndDate,
                        FreeSupplyItems = asset.FreeSupplyItems,
                        PaidSupplyItems = asset.PaidSupplyItems
                    }
                }
            ]
        };

    private static RentalAssetDto CreateAsset()
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = "USENET_GROUP",
            OfficeCode = "USENET",
            ResponsibleOfficeCode = "YEONSU",
            ManagementCompanyCode = "USENET",
            ManagementId = "123",
            ManagementNumber = "2401-001",
            CustomerId = Guid.NewGuid(),
            CustomerName = "기존 거래처",
            CurrentCustomerName = "기존 거래처",
            InstallSiteName = "기존 거래처",
            BillingProfileId = Guid.NewGuid(),
            ItemName = "기존 모델",
            ItemCategoryName = "복합기",
            Manufacturer = "제조사",
            MachineNumber = "SERIAL-1",
            CurrentLocation = "렌탈",
            InstallLocation = "기존 설치위치",
            AssetStatus = "임대진행중",
            Notes = "보존 메모",
            MonthlyFee = 100000,
            ContractMonths = 36,
            CreatedAtUtc = DateTime.UtcNow.AddYears(-1),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            Revision = 1234
        };
}
