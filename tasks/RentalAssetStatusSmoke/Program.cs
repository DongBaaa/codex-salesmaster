using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static void Main()
    {
        PrepareIsolatedLocalAppData();

        using var db = new LocalDbContext();
        LocalDbInitializer.InitializeAsync(db).GetAwaiter().GetResult();

        var session = BuildAdminSession();
        var officeAccess = new OfficeAccessService();
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, officeAccess, dispatcher, session);
        var rental = new RentalStateService(db, local);

        SeedItemCategory(db, "A3흑백복합기");
        var customerId = SeedCustomer(local, session, "ZZZ-비운용 상태 검증 거래처", "999-99-93001");
        var billingProfileId = SeedBillingProfile(db, customerId);
        var assetId = SeedAsset(db, customerId, billingProfileId);

        var firstSaveModel = db.RentalAssets.IgnoreQueryFilters().AsNoTracking().First(current => current.Id == assetId);
        firstSaveModel.AssetStatus = "회수";
        firstSaveModel.Notes = "비운용 전환 검증";

        var firstSave = rental.SaveAssetAsync(firstSaveModel, session).GetAwaiter().GetResult();
        Ensure(firstSave.Success, firstSave.Message);

        var persisted = db.RentalAssets.IgnoreQueryFilters().AsNoTracking().First(current => current.Id == assetId);
        AssertSnapshotPersisted(persisted, billingProfileId);

        var snapshotClearedAt = persisted.LastAssignmentClearedAtUtc;
        var secondSaveModel = db.RentalAssets.IgnoreQueryFilters().AsNoTracking().First(current => current.Id == assetId);
        secondSaveModel.Notes = "회수 상태 메모 수정";

        var secondSave = rental.SaveAssetAsync(secondSaveModel, session).GetAwaiter().GetResult();
        Ensure(secondSave.Success, secondSave.Message);

        var persistedAgain = db.RentalAssets.IgnoreQueryFilters().AsNoTracking().First(current => current.Id == assetId);
        AssertSnapshotPersisted(persistedAgain, billingProfileId);
        Ensure(persistedAgain.LastAssignmentClearedAtUtc == snapshotClearedAt, "비운용 상태 재저장 후 마지막 배정 정리 시각이 불필요하게 변경되었습니다.");

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            FirstSaveMessage = firstSave.Message,
            SecondSaveMessage = secondSave.Message,
            AssetId = assetId,
            Snapshot = new
            {
                persistedAgain.LastCustomerName,
                persistedAgain.LastInstallLocation,
                persistedAgain.LastBillingProfileId,
                persistedAgain.LastBillingProfileDisplay,
                persistedAgain.LastAssignmentClearedAtUtc
            }
        }, JsonOptions));
    }

    private static void AssertSnapshotPersisted(LocalRentalAsset asset, Guid billingProfileId)
    {
        Ensure(asset.CustomerId is null, "비운용 상태 저장 후 CustomerId 가 비워지지 않았습니다.");
        Ensure(string.IsNullOrWhiteSpace(asset.CustomerName), "비운용 상태 저장 후 거래처명이 비워지지 않았습니다.");
        Ensure(string.IsNullOrWhiteSpace(asset.CurrentCustomerName), "비운용 상태 저장 후 현재거래처명이 비워지지 않았습니다.");
        Ensure(string.IsNullOrWhiteSpace(asset.InstallLocation), "비운용 상태 저장 후 설치위치가 비워지지 않았습니다.");
        Ensure(string.IsNullOrWhiteSpace(asset.InstallSiteName), "비운용 상태 저장 후 설치처가 비워지지 않았습니다.");
        Ensure(asset.BillingProfileId is null, "비운용 상태 저장 후 BillingProfileId 가 해제되지 않았습니다.");
        Ensure(string.Equals(asset.BillingEligibilityStatus, "청구제외", StringComparison.Ordinal), $"비운용 상태 저장 후 청구상태가 청구제외가 아닙니다. 실제: {asset.BillingEligibilityStatus}");
        Ensure(string.Equals(asset.LastCustomerName, "ZZZ-비운용 상태 검증 거래처", StringComparison.Ordinal), $"마지막 거래처 이력이 잘못 저장되었습니다. 실제: {asset.LastCustomerName}");
        Ensure(string.Equals(asset.LastInstallLocation, "본관 3층", StringComparison.Ordinal), $"마지막 설치위치 이력이 잘못 저장되었습니다. 실제: {asset.LastInstallLocation}");
        Ensure(asset.LastBillingProfileId == billingProfileId, "마지막 청구프로필 이력이 잘못 저장되었습니다.");
        Ensure(!string.IsNullOrWhiteSpace(asset.LastBillingProfileDisplay), "마지막 청구프로필 표시명이 비어 있습니다.");
        Ensure(asset.LastAssignmentClearedAtUtc.HasValue, "마지막 배정 정리 시각이 저장되지 않았습니다.");
    }

    private static Guid SeedCustomer(LocalStateService local, SessionState session, string name, string businessNumber)
    {
        var customer = new LocalCustomer
        {
            Id = Guid.NewGuid(),
            NameOriginal = name,
            NameMatchKey = name.ToUpperInvariant(),
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            BusinessNumber = businessNumber,
            TradeType = CustomerTradeTypes.Sales,
            PriceGrade = "매출단가",
            Phone = "032-555-1212",
            Address = "테스트 주소"
        };

        var result = local.UpsertCustomerAsync(customer, session).GetAwaiter().GetResult();
        Ensure(result.Success, result.Message);
        return result.EntityId;
    }

    private static void SeedItemCategory(LocalDbContext db, string categoryName)
    {
        if (db.ItemCategoryOptions.IgnoreQueryFilters().Any(current => current.Name == categoryName))
            return;

        var now = DateTime.UtcNow;
        db.ItemCategoryOptions.Add(new LocalItemCategoryOption
        {
            Id = Guid.NewGuid(),
            Name = categoryName,
            SortOrder = 1,
            IsSystemDefault = false,
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Revision = 1,
            IsDirty = true,
            IsDeleted = false
        });
        db.SaveChanges();
    }

    private static Guid SeedBillingProfile(LocalDbContext db, Guid customerId)
    {
        var now = DateTime.UtcNow;
        var profileId = Guid.NewGuid();
        db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
        {
            Id = profileId,
            ProfileKey = $"USENET|{customerId:N}|PRIMARY",
            CustomerId = customerId,
            CustomerName = "ZZZ-비운용 상태 검증 거래처",
            BusinessNumber = "999-99-93001",
            ItemName = "IMC2010",
            BillingType = "묶음",
            InstallSiteName = "본관 3층",
            BillingAdvanceMode = "후불",
            ManagementCompanyCode = DomainConstants.OfficeUsenet,
            BillingMethod = "전자세금계산서",
            BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
            BillingDay = 25,
            BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay,
            BillingCycleMonths = 1,
            BillingAnchorMonth = 3,
            DocumentIssueMode = RentalBillingScheduleRules.DocumentIssueModeSameAsDueDate,
            MonthlyAmount = 150000m,
            SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            BillingTemplateJson = "[]",
            BillingRunsJson = "[]",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Revision = 1,
            IsDirty = true,
            IsDeleted = false
        });
        db.SaveChanges();
        return profileId;
    }

    private static Guid SeedAsset(LocalDbContext db, Guid customerId, Guid billingProfileId)
    {
        var now = DateTime.UtcNow;
        var assetId = Guid.NewGuid();
        db.RentalAssets.Add(new LocalRentalAsset
        {
            Id = assetId,
            AssetKey = "USENET|STATUS-SMOKE-001",
            CustomerId = customerId,
            BillingProfileId = billingProfileId,
            ManagementId = "STATUS-SMOKE-001",
            ManagementNumber = "STATUS-SMOKE-001",
            ManagementCompanyCode = DomainConstants.OfficeUsenet,
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            CurrentLocation = "본관 3층",
            CurrentCustomerName = "ZZZ-비운용 상태 검증 거래처",
            CustomerName = "ZZZ-비운용 상태 검증 거래처",
            InstallLocation = "본관 3층",
            InstallSiteName = "본관 3층",
            ItemCategoryName = "A3흑백복합기",
            Manufacturer = "리코",
            ItemName = "IMC2010",
            MachineNumber = "SMOKE-0001",
            PurchaseVendor = "테스트 매입처",
            BillingEligibilityStatus = "청구대상",
            AssetStatus = "임대진행중",
            MonthlyFee = 150000m,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Revision = 1,
            IsDirty = true,
            IsDeleted = false
        });
        db.SaveChanges();
        return assetId;
    }

    private static SessionState BuildAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "asset-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = DomainConstants.OfficeUsenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            Permissions = new List<string>()
        });
        return session;
    }

    private static void PrepareIsolatedLocalAppData()
    {
        var runtimeRoot = Path.Combine(AppContext.BaseDirectory, "runtime", "rental-asset-status-smoke");
        var localAppData = Path.Combine(runtimeRoot, "LocalAppData");
        var appRoot = Path.Combine(runtimeRoot, "거래플랜");

        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", appRoot, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_LEGACY_MERGE", "1", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", "1", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData, EnvironmentVariableTarget.Process);

        if (Directory.Exists(runtimeRoot))
            Directory.Delete(runtimeRoot, recursive: true);

        Directory.CreateDirectory(localAppData);
        Directory.CreateDirectory(appRoot);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
