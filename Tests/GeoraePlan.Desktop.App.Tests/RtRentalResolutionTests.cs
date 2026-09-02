using System.Globalization;
using System.Text.Json;
using GeoraePlan.Tools.SyncDiag;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RtRentalResolutionTests
{
    [Theory]
    [InlineData("[연수구]자치행정과", "연수구청[자치행정과]")]
    [InlineData("[보건환경연구원]대기평가과", "인천보건환경연구원[대기평가과]")]
    [InlineData("[상수도사업소]중부수도사업소", "상수도사업본부[중부수도사업소]")]
    [InlineData("[연수구]보건소-감염병관리과", "연수구보건소[감염병관리과]")]
    [InlineData("[연수구]청학도서관", "연수구립도서관[청학도서관]")]
    [InlineData("명성다이케스팅", "명성다이캐스팅")]
    [InlineData("[미추홀구]용현1,4동행정복지센터", "미추홀구청[용현1.4동행정복지센터]")]
    public void CustomerNamesEquivalent_AcceptsKnownRtDisplayAliases(string rtName, string tradePlanName)
        => Assert.True(RtRentalResolutionPlanner.CustomerNamesEquivalent(rtName, tradePlanName));

    [Fact]
    public void CustomerNamesEquivalent_RejectsActualDepartmentChange()
        => Assert.False(RtRentalResolutionPlanner.CustomerNamesEquivalent(
            "[인천시청]미래기획과",
            "[인천시청]민생담당관실"));

    [Fact]
    public void BuildPlan_KeepsAliasButMovesActualCustomerAndCreatesMissingCustomer()
    {
        var aliasCustomer = Customer("연수구청[자치행정과]");
        var movedCustomer = Customer("[인천시청]미래기획과");
        var oldCustomer = Customer("[인천시청]민생담당관실");
        var aliasAsset = Asset("2601-001", aliasCustomer);
        var movedAsset = Asset("2601-002", oldCustomer);
        var source = new[]
        {
            Source("2601-001", "[연수구]자치행정과"),
            Source("2601-002", "[인천시청]미래기획과"),
            Source("2601-003", "[미추홀구]신규부서")
        };

        var build = RtRentalResolutionPlanner.BuildPlan(
            source,
            Snapshot([aliasCustomer, movedCustomer, oldCustomer], [aliasAsset, movedAsset]),
            "USENET",
            new string('A', 64),
            "rt-resolve-usenet-test",
            DateTime.UtcNow);

        Assert.Equal(1, build.Plan.Audit.CustomerAliasKeptCount);
        Assert.Equal(2, build.Plan.Audit.CustomerChangedCount);
        Assert.Equal(1, build.Plan.Audit.CustomerCreatedCount);
        Assert.Equal(aliasCustomer.Id, build.Plan.Assets.Single(entry => entry.Desired.ManagementNumber == "2601-001").Desired.CustomerId);
        Assert.Equal(movedCustomer.Id, build.Plan.Assets.Single(entry => entry.Desired.ManagementNumber == "2601-002").Desired.CustomerId);
        Assert.Contains(build.Plan.Customers, entry => entry.Desired.NameOriginal == "[미추홀구]신규부서");
    }

    [Fact]
    public void BuildPlan_ClearsNonOperatingAssignmentsAndDeactivatesEmptyProfile()
    {
        var customer = Customer("종료 고객");
        var asset = Asset("2401-001", customer);
        var profile = Profile(customer, [asset.Id], 165000);
        asset.BillingProfileId = profile.Id;
        var currentHistory = AssignmentHistory(asset, customer, profile);

        var build = RtRentalResolutionPlanner.BuildPlan(
            [Source("2401-001", "종료 고객") with { Status = "계약종료" }],
            Snapshot([customer], [asset], [profile], [currentHistory]),
            "USENET",
            new string('B', 64),
            "rt-resolve-usenet-test",
            DateTime.UtcNow);

        var desiredAsset = Assert.Single(build.Plan.Assets).Desired;
        Assert.Equal("창고", desiredAsset.AssetStatus);
        Assert.Null(desiredAsset.CustomerId);
        Assert.Null(desiredAsset.BillingProfileId);
        Assert.Equal("종료 고객", desiredAsset.LastCustomerName);
        Assert.Equal("청구제외", desiredAsset.BillingEligibilityStatus);
        var desiredProfile = Assert.Single(build.Plan.BillingProfiles).Desired;
        Assert.False(desiredProfile.IsActive);
        Assert.Equal(165000, desiredProfile.MonthlyAmount);
        Assert.Equal("[]", desiredProfile.BillingTemplateJson);
        var closedHistory = Assert.Single(build.Plan.AssignmentHistories).Desired;
        Assert.Equal(currentHistory.Id, closedHistory.Id);
        Assert.False(closedHistory.IsCurrent);
        Assert.NotNull(closedHistory.UnlinkedAtUtc);
        Assert.Equal(1, build.Plan.Audit.PlannedAssignmentHistoryUpdateCount);
    }

    [Fact]
    public void BuildPlan_TombstonesOnlyRtConfirmedCrossDatabaseCopyAndPreservesTradePlanOnlyAsset()
    {
        var customer = Customer("잘못된 DB 고객");
        var crossDatabaseCopy = Asset("2607-001", customer);
        var tradePlanOnlyAsset = Asset("LEGACY-001", customer);
        var profile = Profile(customer, [crossDatabaseCopy.Id], 165000);
        crossDatabaseCopy.BillingProfileId = profile.Id;
        var currentHistory = AssignmentHistory(crossDatabaseCopy, customer, profile);
        var source = Source("2607-001", "아이티월드 고객") with
        {
            ManagementCompany = "아이티월드"
        };

        var build = RtRentalResolutionPlanner.BuildPlan(
            [source],
            Snapshot([customer], [crossDatabaseCopy, tradePlanOnlyAsset], [profile], [currentHistory]),
            "USENET",
            new string('X', 64),
            "rt-cross-database-test",
            DateTime.UtcNow);

        var desiredAsset = Assert.Single(build.Plan.Assets).Desired;
        Assert.Equal(crossDatabaseCopy.Id, desiredAsset.Id);
        Assert.True(desiredAsset.IsDeleted);
        Assert.Null(desiredAsset.CustomerId);
        Assert.Null(desiredAsset.BillingProfileId);
        Assert.Equal(customer.NameOriginal, desiredAsset.LastCustomerName);
        Assert.DoesNotContain(build.Plan.Assets, entry => entry.EntityId == tradePlanOnlyAsset.Id);
        Assert.False(Assert.Single(build.Plan.BillingProfiles).Desired.IsActive);
        var closedHistory = Assert.Single(build.Plan.AssignmentHistories).Desired;
        Assert.False(closedHistory.IsCurrent);
        Assert.NotNull(closedHistory.UnlinkedAtUtc);
        Assert.Equal(1, build.Plan.Audit.CrossDatabaseTombstoneCount);
        Assert.Contains(build.Plan.Decisions, decision =>
            decision.ManagementNumber == "2607-001" &&
            decision.Decision == "잘못된 DB 복사본 비활성화");
    }

    [Fact]
    public void BuildPlan_ClosesOldAndCreatesCurrentHistoryWhenCustomerChanges()
    {
        var oldCustomer = Customer("[인천시청]민생담당관실");
        var newCustomer = Customer("[인천시청]미래기획과");
        var asset = Asset("2607-001", oldCustomer);
        var oldHistory = AssignmentHistory(asset, oldCustomer);

        var build = RtRentalResolutionPlanner.BuildPlan(
            [Source("2607-001", "[인천시청]미래기획과")],
            Snapshot([oldCustomer, newCustomer], [asset], histories: [oldHistory]),
            "USENET",
            new string('E', 64),
            "rt-resolve-usenet-test",
            DateTime.UtcNow);

        Assert.Equal(2, build.Plan.AssignmentHistories.Count);
        Assert.Contains(build.Plan.AssignmentHistories, entry =>
            entry.Operation == RtRentalResolutionPlanner.OperationUpdate &&
            entry.EntityId == oldHistory.Id &&
            !entry.Desired.IsCurrent);
        Assert.Contains(build.Plan.AssignmentHistories, entry =>
            entry.Operation == RtRentalResolutionPlanner.OperationCreate &&
            entry.Desired.CustomerId == newCustomer.Id &&
            entry.Desired.IsCurrent);
        Assert.Equal(1, build.Plan.Audit.PlannedAssignmentHistoryUpdateCount);
        Assert.Equal(1, build.Plan.Audit.PlannedAssignmentHistoryCreateCount);
    }

    [Fact]
    public void BuildProfileUnlinkPlan_PreservesAssignmentFieldsAndOnlyClearsProfileFirst()
    {
        var customer = Customer("선행 해제 고객");
        var asset = Asset("2508-010", customer);
        var profile = Profile(customer, [asset.Id], 165000);
        asset.BillingProfileId = profile.Id;

        var build = RtRentalResolutionPlanner.BuildProfileUnlinkPlan(
            [Source("2508-010", "선행 해제 고객") with { Status = "창고" }],
            Snapshot([customer], [asset], [profile]),
            "USENET",
            new string('F', 64),
            "rt-resolve-usenet-unlink-test",
            DateTime.UtcNow);

        var unlink = Assert.Single(build.Plan.Assets).Desired;
        Assert.Null(unlink.BillingProfileId);
        Assert.Equal(asset.CustomerId, unlink.CustomerId);
        Assert.Equal(asset.CurrentCustomerName, unlink.CurrentCustomerName);
        Assert.Equal(asset.InstallLocation, unlink.InstallLocation);
        Assert.Equal(asset.AssetStatus, unlink.AssetStatus);
        Assert.Single(build.Plan.BillingProfiles);
        Assert.Empty(build.Plan.AssignmentHistories);
    }

    [Fact]
    public void BuildProfileUnlinkPlan_ClearsDirectLinkToDeletedProfile()
    {
        var customer = Customer("고아 프로필 고객");
        var asset = Asset("2508-011", customer);
        var deletedProfile = Profile(customer, [asset.Id], 165000);
        deletedProfile.IsDeleted = true;
        asset.BillingProfileId = deletedProfile.Id;

        var build = RtRentalResolutionPlanner.BuildProfileUnlinkPlan(
            [Source("2508-011", "고아 프로필 고객")],
            Snapshot([customer], [asset], [deletedProfile]),
            "USENET",
            new string('G', 64),
            "rt-resolve-usenet-orphan-unlink-test",
            DateTime.UtcNow);

        var unlink = Assert.Single(build.Plan.Assets).Desired;
        Assert.Null(unlink.BillingProfileId);
        Assert.Equal(asset.CustomerId, unlink.CustomerId);
        Assert.Empty(build.Plan.BillingProfiles);
        Assert.Contains(build.Plan.Decisions, decision => decision.Decision == "고아 프로필 선행 해제");
    }

    [Fact]
    public void BuildPlan_TrimsIndividualProfileAndRecalculatesAmount()
    {
        var customer = Customer("상수도사업본부[중부수도사업소]");
        var activeAsset = Asset("2501-001", customer);
        var endedAsset = Asset("2501-002", customer);
        var profile = Profile(customer, [activeAsset.Id, endedAsset.Id], 330000, "개별", 165000);
        activeAsset.BillingProfileId = profile.Id;
        endedAsset.BillingProfileId = profile.Id;

        var build = RtRentalResolutionPlanner.BuildPlan(
            [
                Source("2501-001", "[상수도사업소]중부수도사업소"),
                Source("2501-002", "[상수도사업소]중부수도사업소") with { Status = "창고" }
            ],
            Snapshot([customer], [activeAsset, endedAsset], [profile]),
            "USENET",
            new string('C', 64),
            "rt-resolve-usenet-test",
            DateTime.UtcNow);

        var desiredProfile = Assert.Single(build.Plan.BillingProfiles).Desired;
        Assert.True(desiredProfile.IsActive);
        Assert.Equal(165000, desiredProfile.MonthlyAmount);
        using var document = JsonDocument.Parse(desiredProfile.BillingTemplateJson);
        var item = Assert.Single(document.RootElement.EnumerateArray().ToArray());
        Assert.Equal(1, item.GetProperty("Quantity").GetDecimal());
        Assert.Equal(activeAsset.Id, Assert.Single(item.GetProperty("IncludedAssetIds").EnumerateArray().ToArray()).GetGuid());
    }

    [Fact]
    public void BuildPlan_UsesExactRtFeeForUnlinkedAsset()
    {
        var customer = Customer("요금 고객");
        var asset = Asset("2603-006", customer);
        asset.MonthlyFee = 100000;

        var build = RtRentalResolutionPlanner.BuildPlan(
            [Source("2603-006", "요금 고객") with { MonthlyFeeText = "100,000" }],
            Snapshot([customer], [asset]),
            "USENET",
            new string('D', 64),
            "rt-resolve-usenet-test",
            DateTime.UtcNow);

        Assert.Equal(100000, Assert.Single(build.Plan.Assets).Desired.MonthlyFee);
        Assert.Equal(0, build.Plan.Audit.VatInclusiveFeeAdjustedCount);
    }

    [Fact]
    public void EntityBusinessEquals_AcceptsServerNormalizedUnlinkedActiveBillingStatus()
    {
        var customer = Customer("무료 장비 고객");
        var approved = Asset("1812-004", customer);
        approved.MonthlyFee = 0;
        approved.BillingProfileId = null;
        approved.BillingEligibilityStatus = "청구제외";
        approved.BillingExclusionReason = "청구 프로필 삭제로 청구목록 제외";
        var stored = RtRentalResolutionPlanner.Clone(approved);
        stored.BillingEligibilityStatus = "미확인";
        stored.BillingExclusionReason = string.Empty;

        Assert.True(RtRentalResolutionPlanner.EntityBusinessEquals(stored, approved));
    }

    [Fact]
    public void EntityBusinessEquals_StillRejectsMeaningfulRentalAssetDifference()
    {
        var customer = Customer("금액 확인 고객");
        var approved = Asset("2609-001", customer);
        approved.BillingProfileId = null;
        approved.BillingEligibilityStatus = "미확인";
        var stored = RtRentalResolutionPlanner.Clone(approved);
        stored.MonthlyFee += 1000;

        Assert.False(RtRentalResolutionPlanner.EntityBusinessEquals(stored, approved));
    }

    [Theory]
    [InlineData("무", RentalMeterPolicyModes.Unlimited, null)]
    [InlineData("무제한", RentalMeterPolicyModes.Unlimited, null)]
    [InlineData("10,000", RentalMeterPolicyModes.Numeric, 10000)]
    [InlineData("", RentalMeterPolicyModes.Unconfigured, null)]
    public void ParseRtIncludedPolicy_NormalizesActualRtValueShapes(
        string raw,
        string expectedMode,
        int? expectedPages)
    {
        var parsed = RtRentalResolutionPlanner.ParseRtIncludedPolicy(raw);

        Assert.Equal(expectedMode, parsed.Mode);
        Assert.Equal(expectedPages, parsed.Pages);
    }

    [Fact]
    public void BuildPlan_ImportsRtMeterPolicyWithoutEnablingBillingPrematurely()
    {
        var customer = Customer("검침 고객");
        var asset = Asset("2609-010", customer);
        var generatedAtUtc = new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc);
        var source = Source("2609-010", "검침 고객") with
        {
            BlackIncludedText = "10,000",
            ColorIncludedText = "무",
            BlackOverageText = "11",
            ColorOverageText = "",
            HasMeterPolicyColumns = true
        };

        var build = RtRentalResolutionPlanner.BuildPlan(
            [source],
            Snapshot([customer], [asset]),
            "USENET",
            new string('M', 64),
            "rt-resolve-meter-test",
            generatedAtUtc);

        var desired = Assert.Single(build.Plan.Assets).Desired;
        Assert.False(desired.MeterBillingEnabled);
        Assert.Equal(RentalMeterPolicyModes.Numeric, desired.BlackIncludedMode);
        Assert.Equal(10000, desired.BlackIncludedPages);
        Assert.Equal(11m, desired.BlackOverageUnitPrice);
        Assert.Equal(RentalMeterPolicyModes.Unlimited, desired.ColorIncludedMode);
        Assert.Null(desired.ColorIncludedPages);
        Assert.Null(desired.ColorOverageUnitPrice);
        Assert.Equal("rt.2884.kr", desired.MeterPolicySource);
        Assert.Equal(generatedAtUtc, desired.MeterPolicySourceUpdatedAtUtc);
    }

    [Fact]
    public void BuildPlan_DoesNotReplanWhenOnlyRtMeterPolicyObservationTimeChanged()
    {
        var customer = Customer("검침 고객");
        var asset = Asset("2609-010", customer);
        asset.ItemCategoryName = "복합기";
        asset.Manufacturer = "리코";
        asset.ContractMonths = 36;
        asset.ContractStartDate = new DateOnly(2026, 1, 1);
        asset.MonthlyFee = 150000;
        asset.BlackIncludedMode = RentalMeterPolicyModes.Numeric;
        asset.BlackIncludedPages = 10000;
        asset.BlackOverageUnitPrice = 11m;
        asset.ColorIncludedMode = RentalMeterPolicyModes.Unlimited;
        asset.ColorIncludedPages = null;
        asset.ColorOverageUnitPrice = null;
        asset.MeterPolicySource = "rt.2884.kr";
        asset.MeterPolicySourceUpdatedAtUtc = new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc);
        var source = Source("2609-010", "검침 고객") with
        {
            BlackIncludedText = "10,000",
            ColorIncludedText = "무",
            BlackOverageText = "11",
            ColorOverageText = "",
            HasMeterPolicyColumns = true
        };

        var build = RtRentalResolutionPlanner.BuildPlan(
            [source],
            Snapshot([customer], [asset]),
            "USENET",
            new string('N', 64),
            "rt-resolve-meter-idempotent-test",
            new DateTime(2026, 9, 2, 3, 0, 0, DateTimeKind.Utc));

        Assert.Empty(build.Plan.Assets);
        Assert.Equal(0, build.Plan.Audit.PlannedEntityCount);
    }

    [Fact]
    public void EntityBusinessEquals_IgnoresRtMeterPolicyTimestampPrecision()
    {
        var customer = Customer("검침 고객");
        var approved = Asset("2609-011", customer);
        approved.MeterPolicySourceUpdatedAtUtc = new DateTime(638924652000000009, DateTimeKind.Utc);
        var stored = RtRentalResolutionPlanner.Clone(approved);
        stored.MeterPolicySourceUpdatedAtUtc = new DateTime(638924652000000000, DateTimeKind.Utc);

        Assert.True(RtRentalResolutionPlanner.EntityBusinessEquals(stored, approved));
        Assert.Empty(RtRentalResolutionPlanner.EntityBusinessDifferenceProperties(stored, approved));
    }

    [Fact]
    public void EntityBusinessDifferenceProperties_ReportsOnlyMeaningfulFieldNames()
    {
        var customer = Customer("금액 확인 고객");
        var approved = Asset("2609-012", customer);
        var stored = RtRentalResolutionPlanner.Clone(approved);
        stored.MonthlyFee += 1000;
        stored.MeterPolicySourceUpdatedAtUtc = DateTime.UtcNow;

        Assert.Equal(
            [nameof(RentalAssetDto.MonthlyFee)],
            RtRentalResolutionPlanner.EntityBusinessDifferenceProperties(stored, approved));
    }

    [Fact]
    public void EntityBusinessEquals_AcceptsEquivalentDecimalScaleFromPostgreSql()
    {
        var customer = Customer("검침 고객");
        var approved = Asset("2609-013", customer);
        approved.BlackOverageUnitPrice = decimal.Parse("11", CultureInfo.InvariantCulture);
        var stored = RtRentalResolutionPlanner.Clone(approved);
        stored.BlackOverageUnitPrice = decimal.Parse("11.0000", CultureInfo.InvariantCulture);

        Assert.True(RtRentalResolutionPlanner.EntityBusinessEquals(stored, approved));
    }

    [Theory]
    [InlineData(false, "ITWORLD", "ITWORLD", "ITWORLD")]
    [InlineData(true, "USENET_GROUP", "USENET", "USENET")]
    public void BuildPlan_DoesNotPreserveInactiveOrWrongDatabaseBillingProfile(
        bool isActive,
        string tenantCode,
        string officeCode,
        string responsibleOfficeCode)
    {
        var customer = Customer("청구범위 고객");
        customer.TenantCode = TenantScopeCatalog.Itworld;
        customer.OfficeCode = OfficeCodeCatalog.Itworld;
        customer.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
        var asset = Asset("2609-014", customer);
        asset.TenantCode = TenantScopeCatalog.Itworld;
        asset.OfficeCode = OfficeCodeCatalog.Itworld;
        asset.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
        asset.ManagementCompanyCode = OfficeCodeCatalog.Itworld;
        asset.MonthlyFee = 150000;
        asset.ItemCategoryName = "복합기";
        asset.Manufacturer = "리코";
        asset.ContractMonths = 36;
        asset.ContractStartDate = new DateOnly(2026, 1, 1);
        var profile = Profile(customer, [asset.Id], 150000);
        profile.IsActive = isActive;
        profile.TenantCode = tenantCode;
        profile.OfficeCode = officeCode;
        profile.ResponsibleOfficeCode = responsibleOfficeCode;
        asset.BillingProfileId = profile.Id;

        var source = Source("2609-014", "청구범위 고객") with { ManagementCompany = "아이티월드" };
        var build = RtRentalResolutionPlanner.BuildPlan(
            [source],
            Snapshot([customer], [asset], [profile]),
            "ITWORLD",
            new string('P', 64),
            "rt-resolve-profile-scope-test",
            new DateTime(2026, 9, 2, 3, 0, 0, DateTimeKind.Utc));

        Assert.Null(Assert.Single(build.Plan.Assets).Desired.BillingProfileId);
    }

    private static SyncPullResponse Snapshot(
        IReadOnlyCollection<CustomerDto> customers,
        IReadOnlyCollection<RentalAssetDto> assets,
        IReadOnlyCollection<RentalBillingProfileDto>? profiles = null,
        IReadOnlyCollection<RentalAssetAssignmentHistoryDto>? histories = null)
        => new()
        {
            CurrentServerRevision = 1234,
            Customers = customers.ToList(),
            RentalAssets = assets.ToList(),
            RentalBillingProfiles = profiles?.ToList() ?? [],
            RentalAssetAssignmentHistories = histories?.ToList() ?? []
        };

    private static CustomerDto Customer(string name)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = "USENET_GROUP",
            OfficeCode = "USENET",
            ResponsibleOfficeCode = "USENET",
            NameOriginal = name,
            NameMatchKey = name,
            CreatedAtUtc = DateTime.UtcNow.AddYears(-1),
            UpdatedAtUtc = DateTime.UtcNow.AddDays(-1),
            Revision = Random.Shared.Next(1, 1000)
        };

    private static RentalAssetDto Asset(string managementNumber, CustomerDto customer)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = "USENET_GROUP",
            OfficeCode = "USENET",
            ResponsibleOfficeCode = "USENET",
            ManagementCompanyCode = "USENET",
            ManagementNumber = managementNumber,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            CurrentCustomerName = customer.NameOriginal,
            InstallSiteName = "설치처",
            InstallLocation = "설치처",
            CurrentLocation = "설치처",
            ItemName = "IMC2010",
            MachineNumber = $"SERIAL-{managementNumber}",
            AssetStatus = "임대진행중",
            MonthlyFee = 165000,
            BillingEligibilityStatus = "미확인",
            CreatedAtUtc = DateTime.UtcNow.AddYears(-1),
            UpdatedAtUtc = DateTime.UtcNow.AddDays(-1),
            Revision = Random.Shared.Next(1, 1000)
        };

    private static RentalBillingProfileDto Profile(
        CustomerDto customer,
        IReadOnlyCollection<Guid> assetIds,
        decimal amount,
        string mode = "묶음",
        decimal? unitPrice = null)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = "USENET_GROUP",
            OfficeCode = "USENET",
            ResponsibleOfficeCode = "USENET",
            ProfileKey = $"USENET|{customer.Id:N}",
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            MonthlyAmount = amount,
            BillingTemplateJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    ItemId = Guid.NewGuid(),
                    DisplayItemName = "임대료",
                    BillingLineMode = mode,
                    Quantity = assetIds.Count,
                    UnitPrice = unitPrice ?? amount,
                    Amount = amount,
                    IncludedAssetIds = assetIds
                }
            }),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow.AddYears(-1),
            UpdatedAtUtc = DateTime.UtcNow.AddDays(-1),
            Revision = Random.Shared.Next(1, 1000)
        };

    private static RentalAssetAssignmentHistoryDto AssignmentHistory(
        RentalAssetDto asset,
        CustomerDto customer,
        RentalBillingProfileDto? profile = null)
        => new()
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            BillingProfileId = profile?.Id,
            CustomerId = customer.Id,
            TenantCode = asset.TenantCode,
            OfficeCode = asset.OfficeCode,
            ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
            CustomerName = customer.NameOriginal,
            InstallLocation = asset.InstallLocation,
            BillingProfileDisplay = profile?.ProfileKey ?? string.Empty,
            ItemName = asset.ItemName,
            MachineNumber = asset.MachineNumber,
            ManagementNumber = asset.ManagementNumber,
            MonthlyFee = asset.MonthlyFee,
            IsCurrent = true,
            LinkedAtUtc = DateTime.UtcNow.AddMonths(-6),
            CreatedAtUtc = DateTime.UtcNow.AddMonths(-6),
            UpdatedAtUtc = DateTime.UtcNow.AddDays(-1),
            Revision = Random.Shared.Next(1, 1000)
        };

    private static RtRentalSourceRow Source(string managementNumber, string customerName)
        => new(
            SourceLineNumber: 2,
            Status: "렌탈",
            ManagementNumber: managementNumber,
            ItemCategoryName: "복합기",
            ItemName: "IMC2010",
            Manufacturer: "리코",
            MachineNumber: $"SERIAL-{managementNumber}",
            CustomerName: customerName,
            InstallLocation: "설치처",
            ManagementCompany: "유즈넷",
            MonthlyFeeText: "150,000",
            ContractMonthsText: "36개월",
            ContractStartDate: "2026-01-01",
            RentalEndDate: "-",
            DisposalDate: "-");
}
