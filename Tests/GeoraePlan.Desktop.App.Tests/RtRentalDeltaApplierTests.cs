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

    [Theory]
    [InlineData(1, 0, 0, 0, "saved_login_failed")]
    [InlineData(1, 1, 0, 0, "rental_asset_edit_permission_missing")]
    [InlineData(1, 1, 1, 0, "target_business_database_not_selectable")]
    public void CredentialSelectionFailure_DistinguishesTheActualGate(
        int candidateCount,
        int loginSucceededCount,
        int rentalAssetEditAllowedCount,
        int businessDatabaseSelectedCount,
        string expectedReason)
    {
        var message = RtRentalDeltaApplier.BuildCredentialSelectionFailureMessage(
            candidateCount,
            loginSucceededCount,
            rentalAssetEditAllowedCount,
            businessDatabaseSelectedCount);

        Assert.Contains($"reason={expectedReason}", message, StringComparison.Ordinal);
        Assert.Contains($"candidates={candidateCount}", message, StringComparison.Ordinal);
        Assert.Contains(
            $"login_succeeded={loginSucceededCount}",
            message,
            StringComparison.Ordinal);
        Assert.Contains(
            $"rental_asset_edit_allowed={rentalAssetEditAllowedCount}",
            message,
            StringComparison.Ordinal);
        Assert.Contains(
            $"business_database_selected={businessDatabaseSelectedCount}",
            message,
            StringComparison.Ordinal);
        Assert.Contains(
            "does not mean that the server has no administrator account",
            message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialSelectionFailure_RejectsImpossibleCounters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RtRentalDeltaApplier.BuildCredentialSelectionFailureMessage(
                candidateCount: 1,
                loginSucceededCount: 1,
                rentalAssetEditAllowedCount: 0,
                businessDatabaseSelectedCount: 1));
    }

    [Fact]
    public void BuildPlan_UsesOnlyUniqueProtectedIdentityMatch()
    {
        var asset = CreateItworldAsset();
        var source = CreateSource(asset) with
        {
            ItemName = "RT 최신 모델",
            InstallLocation = "RT 최신 설치위치"
        };

        var result = RtRentalDeltaPlanner.BuildPlan(
            [source],
            [asset],
            "ITWORLD",
            new string('1', 64),
            "rt-rental-itworld-test",
            DateTime.UtcNow);

        var entry = Assert.Single(result.Plan.Entries);
        Assert.Equal(asset.Id, entry.AssetId);
        Assert.Equal(asset.ManagementNumber, entry.ExpectedManagementNumber);
        Assert.Equal("RT 최신 모델", entry.Values.ItemName);
        Assert.Equal("RT 최신 설치위치", entry.Values.InstallLocation);
        Assert.Equal(1, result.Audit.MatchedUniqueKeyCount);
        Assert.Equal(1, result.Audit.PlannedChangeCount);
        Assert.Equal(0, result.Audit.CustomerMismatchExcludedCount);
    }

    [Theory]
    [InlineData("다른 거래처", "렌탈", 1, 0)]
    [InlineData("기존 거래처", "계약종료", 0, 1)]
    public void BuildPlan_ExcludesProtectedCustomerOrUnsupportedStatus(
        string sourceCustomer,
        string sourceStatus,
        int expectedCustomerMismatch,
        int expectedUnsupportedStatus)
    {
        var asset = CreateItworldAsset();
        var source = CreateSource(asset) with
        {
            CustomerName = sourceCustomer,
            Status = sourceStatus,
            ItemName = "변경되면 안 되는 모델"
        };

        var result = RtRentalDeltaPlanner.BuildPlan(
            [source],
            [asset],
            "ITWORLD",
            new string('2', 64),
            "rt-rental-itworld-test",
            DateTime.UtcNow);

        Assert.Empty(result.Plan.Entries);
        Assert.Equal(expectedCustomerMismatch, result.Audit.CustomerMismatchExcludedCount);
        Assert.Equal(expectedUnsupportedStatus, result.Audit.UnsupportedStatusExcludedCount);
    }

    [Fact]
    public void BuildPlan_PreservesProfileLinkedFeeButKeepsOtherSafeChanges()
    {
        var asset = CreateItworldAsset();
        asset.BillingProfileId = Guid.NewGuid();
        var source = CreateSource(asset) with
        {
            ItemName = "RT 최신 모델",
            MonthlyFeeText = "200,000"
        };

        var result = RtRentalDeltaPlanner.BuildPlan(
            [source],
            [asset],
            "ITWORLD",
            new string('3', 64),
            "rt-rental-itworld-test",
            DateTime.UtcNow);

        var entry = Assert.Single(result.Plan.Entries);
        Assert.Equal("RT 최신 모델", entry.Values.ItemName);
        Assert.Equal(asset.MonthlyFee, entry.Values.MonthlyFee);
        Assert.Equal(1, result.Audit.BillingProfileFeePreservedCount);
    }

    [Fact]
    public void BuildPlan_ExcludesDuplicateTargetManagementNumber()
    {
        var first = CreateItworldAsset();
        var second = CreateItworldAsset();
        second.ManagementNumber = first.ManagementNumber;
        var source = CreateSource(first) with { ItemName = "RT 최신 모델" };

        var result = RtRentalDeltaPlanner.BuildPlan(
            [source],
            [first, second],
            "ITWORLD",
            new string('4', 64),
            "rt-rental-itworld-test",
            DateTime.UtcNow);

        Assert.Empty(result.Plan.Entries);
        Assert.Equal(2, result.Audit.DuplicateTargetKeyCount);
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

    private static RentalAssetDto CreateItworldAsset()
    {
        var asset = CreateAsset();
        asset.TenantCode = "ITWORLD";
        asset.OfficeCode = "ITWORLD";
        asset.ResponsibleOfficeCode = "ITWORLD";
        asset.ManagementCompanyCode = "ITWORLD";
        return asset;
    }

    private static RtRentalSourceRow CreateSource(RentalAssetDto asset)
        => new(
            SourceLineNumber: 2,
            Status: "렌탈",
            ManagementNumber: asset.ManagementNumber,
            ItemCategoryName: asset.ItemCategoryName,
            ItemName: asset.ItemName,
            Manufacturer: asset.Manufacturer,
            MachineNumber: asset.MachineNumber,
            CustomerName: asset.CurrentCustomerName,
            InstallLocation: asset.InstallLocation,
            ManagementCompany: "아이티월드",
            MonthlyFeeText: asset.MonthlyFee.ToString("0"),
            ContractMonthsText: $"{asset.ContractMonths}개월",
            ContractStartDate: asset.ContractStartDate?.ToString("yyyy-MM-dd") ?? "-",
            RentalEndDate: asset.RentalEndDate?.ToString("yyyy-MM-dd") ?? "-",
            DisposalDate: asset.DisposalDate?.ToString("yyyy-MM-dd") ?? "-");
}
