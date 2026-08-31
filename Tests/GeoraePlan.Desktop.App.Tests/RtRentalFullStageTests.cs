using GeoraePlan.Tools.SyncDiag;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RtRentalFullStageTests
{
    [Fact]
    public void BuildPlan_MapsContractEndAndPreservesProfileLinkedFeeAndCustomer()
    {
        var asset = CreateItworldAsset();
        asset.BillingProfileId = Guid.NewGuid();
        var profile = new RentalBillingProfileDto
        {
            Id = asset.BillingProfileId.Value,
            ProfileKey = "ITWORLD|TEST",
            MonthlyAmount = 165000,
            TenantCode = "ITWORLD",
            OfficeCode = "ITWORLD",
            ResponsibleOfficeCode = "ITWORLD",
            CustomerId = asset.CustomerId,
            BillingTemplateJson = $"[{{\"IncludedAssetIds\":[\"{asset.Id}\"]}}]"
        };
        var source = CreateSource(asset) with
        {
            Status = "계약종료",
            CustomerName = "[기존 거래처]산업환경과",
            MonthlyFeeText = "90,000",
            RentalEndDate = "2026-08-31"
        };

        var build = RtRentalFullStagePlanner.BuildPlan(
            [source],
            new SyncPullResponse
            {
                RentalAssets = [asset],
                RentalBillingProfiles = [profile],
                Customers =
                [
                    new CustomerDto
                    {
                        Id = asset.CustomerId!.Value,
                        NameOriginal = asset.CurrentCustomerName,
                        ResponsibleOfficeCode = "ITWORLD"
                    }
                ]
            },
            "ITWORLD",
            new string('A', 64),
            "rt-full-itworld-test",
            DateTime.UtcNow);

        var entry = Assert.Single(build.Plan.Entries);
        Assert.Equal(RentalAssetStatusNormalizer.Warehouse, entry.Values.AssetStatus);
        Assert.Equal("창고", entry.Values.CurrentLocation);
        Assert.Equal("청구제외", entry.Values.BillingEligibilityStatus);
        Assert.Equal("자산상태: 창고", entry.Values.BillingExclusionReason);
        Assert.Equal(asset.MonthlyFee, entry.Values.MonthlyFee);
        Assert.Equal(new DateOnly(2026, 8, 31), entry.Values.RentalEndDate);
        Assert.Equal(asset.CustomerId, entry.ExpectedCustomerId);
        Assert.Single(build.BillingFeeCandidates);
        Assert.Single(build.CustomerCandidates);
        Assert.Equal(1, build.Audit.StatusChangeCount);
    }

    [Fact]
    public void BuildPlan_CreatesOnlyMissingAssetWithoutCustomer()
    {
        var blankCustomer = CreateSource(CreateItworldAsset()) with
        {
            ManagementNumber = "2608-099",
            Status = "창고",
            CustomerName = "-",
            MonthlyFeeText = "-"
        };
        var linkedCustomer = blankCustomer with
        {
            ManagementNumber = "2608-100",
            Status = "렌탈",
            CustomerName = "신규 거래처",
            MonthlyFeeText = "100,000"
        };

        var build = RtRentalFullStagePlanner.BuildPlan(
            [blankCustomer, linkedCustomer],
            new SyncPullResponse(),
            "ITWORLD",
            new string('B', 64),
            "rt-full-itworld-test",
            DateTime.UtcNow);

        var entry = Assert.Single(build.Plan.Entries);
        Assert.Equal(RtRentalFullStagePlanner.OperationCreate, entry.Operation);
        Assert.Equal("2608-099", entry.ExpectedManagementNumber);
        Assert.Null(entry.ExpectedCustomerId);
        Assert.Null(entry.ExpectedBillingProfileId);
        Assert.Equal(1, build.Audit.SafeCreateCount);
        Assert.Equal(1, build.Audit.CustomerApprovalHeldCreateCount);
        Assert.Single(build.CustomerCandidates);
    }

    [Fact]
    public void PrepareFullStageMutations_ChangesStatusButKeepsCustomerProfileAndScope()
    {
        var asset = CreateItworldAsset();
        var plan = CreateUpdatePlan(asset);
        plan.Entries[0].Values.AssetStatus = RentalAssetStatusNormalizer.Warehouse;
        plan.Entries[0].Values.CurrentLocation = "창고";
        plan.Entries[0].Values.BillingEligibilityStatus = "청구제외";
        plan.Entries[0].Values.BillingExclusionReason = "자산상태: 창고";

        var prepared = RtRentalDeltaApplier.PrepareFullStageMutations(
            plan,
            new string('C', 64),
            [asset]);

        var mutation = Assert.Single(prepared.Mutations);
        Assert.Equal(RentalAssetStatusNormalizer.Warehouse, mutation.AssetStatus);
        Assert.Equal(asset.CustomerId, mutation.CustomerId);
        Assert.Equal(asset.BillingProfileId, mutation.BillingProfileId);
        Assert.Equal("ITWORLD", mutation.TenantCode);
        Assert.Equal("ITWORLD", mutation.ResponsibleOfficeCode);
        Assert.Equal(asset.Revision, mutation.ExpectedRevision);
    }

    [Fact]
    public void PrepareFullStageMutations_RejectsCustomerDrift()
    {
        var asset = CreateItworldAsset();
        var plan = CreateUpdatePlan(asset);
        asset.CustomerId = Guid.NewGuid();

        Assert.Throws<InvalidDataException>(() =>
            RtRentalDeltaApplier.PrepareFullStageMutations(
                plan,
                new string('D', 64),
                [asset]));
    }

    [Theory]
    [InlineData("렌탈", "임대진행중")]
    [InlineData("계약종료", "창고")]
    [InlineData("창고", "창고")]
    [InlineData("판매", "판매")]
    [InlineData("폐기", "폐기")]
    public void StatusMapping_CoversEveryRtStatus(string source, string expected)
    {
        Assert.True(RtRentalFullStagePlanner.TryMapSourceStatus(source, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildPlan_TreatsExemptMonthlyFeeAsZero()
    {
        var asset = CreateItworldAsset();
        asset.BillingProfileId = null;
        var source = CreateSource(asset) with { MonthlyFeeText = "면제" };

        var build = RtRentalFullStagePlanner.BuildPlan(
            [source],
            new SyncPullResponse { RentalAssets = [asset] },
            "ITWORLD",
            new string('F', 64),
            "rt-full-itworld-test",
            DateTime.UtcNow);

        var entry = Assert.Single(build.Plan.Entries);
        Assert.Equal(0m, entry.Values.MonthlyFee);
        Assert.Equal(0, build.Audit.InvalidSourceCount);
    }

    [Fact]
    public void BuildPlan_HoldsChangedAssetWhenReferencedBillingProfileIsMissing()
    {
        var asset = CreateItworldAsset();
        var source = CreateSource(asset) with { Status = "계약종료" };

        var build = RtRentalFullStagePlanner.BuildPlan(
            [source],
            new SyncPullResponse { RentalAssets = [asset] },
            "ITWORLD",
            new string('G', 64),
            "rt-full-itworld-test",
            DateTime.UtcNow);

        Assert.Empty(build.Plan.Entries);
        var candidate = Assert.Single(build.BillingProfileReferenceCandidates);
        Assert.Equal(asset.BillingProfileId, candidate.MissingBillingProfileId);
        Assert.Equal(RentalAssetStatusNormalizer.Warehouse, candidate.RequestedAssetStatus);
        Assert.Equal(1, build.Audit.MissingBillingProfileReferenceHeldCount);
    }

    [Fact]
    public void BuildPlan_HoldsChangedAssetWhenReferencedBillingProfileIsDeleted()
    {
        var asset = CreateItworldAsset();
        var source = CreateSource(asset) with { Status = "계약종료" };
        var deletedProfile = new RentalBillingProfileDto
        {
            Id = asset.BillingProfileId!.Value,
            IsDeleted = true,
            ProfileKey = "ITWORLD|DELETED"
        };

        var build = RtRentalFullStagePlanner.BuildPlan(
            [source],
            new SyncPullResponse
            {
                RentalAssets = [asset],
                RentalBillingProfiles = [deletedProfile]
            },
            "ITWORLD",
            new string('H', 64),
            "rt-full-itworld-test",
            DateTime.UtcNow);

        Assert.Empty(build.Plan.Entries);
        Assert.Single(build.BillingProfileReferenceCandidates);
        Assert.Equal(1, build.Audit.MissingBillingProfileReferenceHeldCount);
    }

    [Fact]
    public void BuildPlan_HoldsChangedAssetWhenBillingProfileResponsibleOfficeDiffers()
    {
        var asset = CreateItworldAsset();
        asset.ResponsibleOfficeCode = "USENET";
        var source = CreateSource(asset) with { Status = "계약종료" };
        var mismatchedProfile = new RentalBillingProfileDto
        {
            Id = asset.BillingProfileId!.Value,
            TenantCode = "ITWORLD",
            OfficeCode = "ITWORLD",
            ResponsibleOfficeCode = "USENET",
            CustomerId = asset.CustomerId,
            BillingTemplateJson = $"[{{\"IncludedAssetIds\":[\"{asset.Id}\"]}}]"
        };

        var build = RtRentalFullStagePlanner.BuildPlan(
            [source],
            new SyncPullResponse
            {
                RentalAssets = [asset],
                RentalBillingProfiles = [mismatchedProfile]
            },
            "ITWORLD",
            new string('I', 64),
            "rt-full-itworld-test",
            DateTime.UtcNow);

        Assert.Empty(build.Plan.Entries);
        Assert.Single(build.BillingProfileReferenceCandidates);
        Assert.Equal(1, build.Audit.MissingBillingProfileReferenceHeldCount);
    }

    [Fact]
    public void BuildPlan_HoldsUnlinkedAssetWhenProfileTemplateWouldAutoLinkIt()
    {
        var asset = CreateItworldAsset();
        asset.BillingProfileId = null;
        var source = CreateSource(asset) with { Status = "판매" };
        var profile = new RentalBillingProfileDto
        {
            Id = Guid.NewGuid(),
            TenantCode = asset.TenantCode,
            OfficeCode = asset.OfficeCode,
            ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
            CustomerId = asset.CustomerId,
            BillingTemplateJson = $"[{{\"IncludedAssetIds\":[\"{asset.Id}\"]}}]"
        };

        var build = RtRentalFullStagePlanner.BuildPlan(
            [source],
            new SyncPullResponse
            {
                RentalAssets = [asset],
                RentalBillingProfiles = [profile]
            },
            "ITWORLD",
            new string('J', 64),
            "rt-full-itworld-test",
            DateTime.UtcNow);

        Assert.Empty(build.Plan.Entries);
        var candidate = Assert.Single(build.BillingProfileReferenceCandidates);
        Assert.Equal(profile.Id, candidate.MissingBillingProfileId);
        Assert.Contains("자동 연결", candidate.Reason, StringComparison.Ordinal);
        Assert.Equal(1, build.Audit.MissingBillingProfileReferenceHeldCount);
    }

    [Fact]
    public void PrepareFullStageMutations_AllowsVerifiedNoOpPlan()
    {
        var plan = new RtRentalFullStagePlan
        {
            SchemaVersion = 2,
            PlanId = "rt-full-itworld-noop",
            BusinessDatabaseName = "ITWORLD",
            SourceSha256 = new string('A', 64),
            GeneratedAtUtc = DateTime.UtcNow,
            Entries = []
        };

        var prepared = RtRentalDeltaApplier.PrepareFullStageMutations(
            plan,
            new string('B', 64),
            []);

        Assert.Empty(prepared.Mutations);
        Assert.Equal(0, prepared.SkippedNoChangeCount);
    }

    [Fact]
    public void ProtectedValuesAfterServer_AllowsAuthoritativeDisplayNameForUnchangedCustomerId()
    {
        var asset = CreateItworldAsset();
        var plan = CreateUpdatePlan(asset);
        asset.CustomerName = "고객 마스터 정식명";
        asset.CurrentCustomerName = "고객 마스터 정식명";

        var matches = RtRentalDeltaApplier.FullStageProtectedValuesMatchAfterServer(
            plan.Entries[0],
            asset,
            new Dictionary<Guid, string>
            {
                [asset.CustomerId!.Value] = "고객 마스터 정식명"
            });

        Assert.True(matches);
    }

    [Fact]
    public void ProtectedValuesAfterServer_RejectsUnapprovedDisplayNameOrCustomerLinkChange()
    {
        var asset = CreateItworldAsset();
        var plan = CreateUpdatePlan(asset);
        var originalCustomerId = asset.CustomerId!.Value;
        asset.CustomerName = "승인되지 않은 이름";
        asset.CurrentCustomerName = "승인되지 않은 이름";

        Assert.False(RtRentalDeltaApplier.FullStageProtectedValuesMatchAfterServer(
            plan.Entries[0],
            asset,
            new Dictionary<Guid, string> { [originalCustomerId] = "고객 마스터 정식명" }));

        asset.CustomerId = Guid.NewGuid();
        asset.CustomerName = "고객 마스터 정식명";
        asset.CurrentCustomerName = "고객 마스터 정식명";

        Assert.False(RtRentalDeltaApplier.FullStageProtectedValuesMatchAfterServer(
            plan.Entries[0],
            asset,
            new Dictionary<Guid, string> { [originalCustomerId] = "고객 마스터 정식명" }));
    }

    private static RtRentalFullStagePlan CreateUpdatePlan(RentalAssetDto asset)
        => new()
        {
            SchemaVersion = 2,
            PlanId = "rt-full-itworld-test",
            BusinessDatabaseName = "ITWORLD",
            SourceSha256 = new string('E', 64),
            GeneratedAtUtc = DateTime.UtcNow,
            Entries =
            [
                new RtRentalFullStagePlanEntry
                {
                    Operation = RtRentalFullStagePlanner.OperationUpdate,
                    AssetId = asset.Id,
                    ExpectedRevision = asset.Revision,
                    ExpectedUpdatedAtUtc = asset.UpdatedAtUtc,
                    ExpectedTenantCode = asset.TenantCode,
                    ExpectedOfficeCode = asset.OfficeCode,
                    ExpectedResponsibleOfficeCode = asset.ResponsibleOfficeCode,
                    ExpectedManagementCompanyCode = asset.ManagementCompanyCode,
                    ExpectedManagementNumber = asset.ManagementNumber,
                    ExpectedAssetStatus = asset.AssetStatus,
                    ExpectedCustomerId = asset.CustomerId,
                    ExpectedCustomerName = asset.CustomerName,
                    ExpectedCurrentCustomerName = asset.CurrentCustomerName,
                    ExpectedBillingProfileId = asset.BillingProfileId,
                    Values = new RtRentalFullStageValues
                    {
                        CurrentLocation = asset.CurrentLocation,
                        ItemCategoryName = asset.ItemCategoryName,
                        Manufacturer = asset.Manufacturer,
                        ItemName = asset.ItemName,
                        MachineNumber = asset.MachineNumber,
                        DisposalDate = asset.DisposalDate,
                        InstallLocation = asset.InstallLocation,
                        MonthlyFee = asset.MonthlyFee,
                        ContractMonths = asset.ContractMonths,
                        ContractStartDate = asset.ContractStartDate,
                        RentalEndDate = asset.RentalEndDate,
                        AssetStatus = asset.AssetStatus,
                        BillingEligibilityStatus = asset.BillingEligibilityStatus,
                        BillingExclusionReason = asset.BillingExclusionReason
                    }
                }
            ]
        };

    private static RentalAssetDto CreateItworldAsset()
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = "ITWORLD",
            OfficeCode = "ITWORLD",
            ResponsibleOfficeCode = "ITWORLD",
            ManagementCompanyCode = "ITWORLD",
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
            CurrentLocation = "기존 설치위치",
            InstallLocation = "기존 설치위치",
            AssetStatus = RentalAssetStatusNormalizer.Active,
            BillingEligibilityStatus = "청구대상",
            MonthlyFee = 100000,
            ContractMonths = 36,
            CreatedAtUtc = DateTime.UtcNow.AddYears(-1),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            Revision = 1234
        };

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
