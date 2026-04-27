using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed class RentalBillingFilter
{
    public string SearchText { get; set; } = string.Empty;
    public string OfficeCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool DueOnly { get; set; }
    public DateOnly ReferenceDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
}

public sealed class RentalAssetFilter
{
    public string SearchText { get; set; } = string.Empty;
    public List<string> ItemCategoryNames { get; set; } = new();
    public List<string> OfficeCodes { get; set; } = new();
    public List<string> AssetStatuses { get; set; } = new();
}

public sealed class RentalAlertItem
{
    public Guid BillingProfileId { get; set; }
    public string ResponsibleOfficeName { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public decimal MonthlyAmount { get; set; }
    public DateOnly NextBillingDate { get; set; }
    public DateOnly? DocumentIssueDate { get; set; }
    public DateOnly AlertDate { get; set; }
    public string AlertReason { get; set; } = string.Empty;
    public int DaysRemaining { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Summary => CustomerName;
}

public sealed class RentalExpiringAssetItem
{
    public Guid AssetId { get; set; }
    public string ManagementNumber { get; set; } = string.Empty;
    public string ResponsibleOfficeName { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public DateOnly? RentalEndDate { get; set; }
    public int DaysRemaining { get; set; }
}

public sealed class RentalLinkReviewItem
{
    public string QueueType { get; set; } = string.Empty;
    public string ResponsibleOfficeName { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string ReviewNote { get; set; } = string.Empty;
    public int CandidateCount { get; set; }
}

public sealed class RentalCustomerLinkCleanupRow
{
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string ResponsibleOfficeName { get; set; } = string.Empty;
    public string CurrentCustomerName { get; set; } = string.Empty;
    public string MasterCustomerName { get; set; } = string.Empty;
    public string BusinessNumber { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string LinkedProfileDisplay { get; set; } = string.Empty;
    public string IssueSummary { get; set; } = string.Empty;
    public string SuggestedAction { get; set; } = string.Empty;
    public bool CanAutoNormalize { get; set; }
}

public sealed class RentalCustomerLinkCleanupResult
{
    public int ScannedProfileCount { get; set; }
    public int ScannedAssetCount { get; set; }
    public int ReviewItemCount { get; set; }
    public int UpdatedProfileCount { get; set; }
    public int UpdatedAssetCount { get; set; }
    public int LinkedCustomerCount { get; set; }
}

public sealed class RentalDashboardSummary
{
    public int DueTodayCount { get; set; }
    public int UpcomingCount { get; set; }
    public int OverdueCount { get; set; }
    public int ActiveAssetCount { get; set; }
    public int ExpiringContractCount { get; set; }
    public int UnassignedCount { get; set; }
    public int BillingCustomerUnlinkedCount { get; set; }
    public int AssetCustomerUnlinkedCount { get; set; }
    public int AssetBillingUnlinkedCount { get; set; }
    public int AssetlessBillingProfileCount { get; set; }
    public string AlertPopupMessage { get; set; } = string.Empty;
    public IReadOnlyList<RentalAlertItem> AlertItems { get; set; } = Array.Empty<RentalAlertItem>();
    public IReadOnlyList<RentalExpiringAssetItem> ExpiringAssets { get; set; } = Array.Empty<RentalExpiringAssetItem>();
    public IReadOnlyList<RentalLinkReviewItem> UnresolvedLinkItems { get; set; } = Array.Empty<RentalLinkReviewItem>();
}

public sealed class RentalImportResult
{
    public string SourcePath { get; init; } = string.Empty;
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }
    public int ErrorCount { get; set; }
    public List<string> Messages { get; } = new();

    public string Summary
        => $"신규 {CreatedCount:N0}, 수정 {UpdatedCount:N0}, 건너뜀 {SkippedCount:N0}, 오류 {ErrorCount:N0}";
}

public sealed class RentalCatalogRepairResult
{
    public int ScannedAssetCount { get; set; }
    public int UpdatedAssetCount { get; set; }
    public int AddedCategoryCount => AddedCategoryNames.Count;
    public int AddedItemCount => AddedItemNames.Count;
    public int AmbiguousItemNameCount => AmbiguousItemNames.Count;
    public int MissingCategoryCount => MissingCategoryNames.Count;
    public List<string> AddedCategoryNames { get; } = new();
    public List<string> AddedItemNames { get; } = new();
    public List<string> AmbiguousItemNames { get; } = new();
    public List<string> MissingCategoryNames { get; } = new();
}

public sealed class RentalWorkbookAuditEntry
{
    public int RowNumber { get; set; }
    public string Action { get; set; } = string.Empty;
    public string MatchedBy { get; set; } = string.Empty;
    public Guid? ExistingAssetId { get; set; }
    public string ExistingManagementNumber { get; set; } = string.Empty;
    public string WorkbookManagementNumber { get; set; } = string.Empty;
    public string WorkbookManagementId { get; set; } = string.Empty;
    public string WorkbookOfficeCode { get; set; } = string.Empty;
    public string WorkbookCustomerName { get; set; } = string.Empty;
    public string WorkbookItemName { get; set; } = string.Empty;
    public string WorkbookMachineNumber { get; set; } = string.Empty;
    public string WorkbookInstallLocation { get; set; } = string.Empty;
    public List<string> Differences { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class RentalWorkbookMissingAssetEntry
{
    public Guid AssetId { get; set; }
    public string ManagementNumber { get; set; } = string.Empty;
    public string ManagementId { get; set; } = string.Empty;
    public string OfficeCode { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string MachineNumber { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
}

public sealed class RentalWorkbookAuditResult
{
    public string WorkbookPath { get; set; } = string.Empty;
    public string SheetName { get; set; } = string.Empty;
    public DateTime GeneratedAtUtc { get; set; }
    public int ProcessedRowCount { get; set; }
    public int ExactMatchCount { get; set; }
    public int UpdateSafeCount { get; set; }
    public int CreateNewCount { get; set; }
    public int AmbiguousCount { get; set; }
    public int MissingInWorkbookCount { get; set; }
    public int UnresolvedCustomerCount { get; set; }
    public List<RentalWorkbookAuditEntry> Entries { get; set; } = new();
    public List<RentalWorkbookMissingAssetEntry> MissingInWorkbookAssets { get; set; } = new();
}

public sealed class RentalWorkbookScopeIssue
{
    public string OfficeCode { get; set; } = string.Empty;
    public string OfficeDisplayName { get; set; } = string.Empty;
    public string TenantDisplayName { get; set; } = string.Empty;
    public int RowCount { get; set; }
    public bool WritableInCurrentSession { get; set; }
    public bool HasStoredCredential { get; set; }
    public string ResolutionHint { get; set; } = string.Empty;
}

public sealed class RentalWorkbookRebuildResult
{
    public string WorkbookPath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public DateTime ProcessedAtUtc { get; set; }
    public bool IsBlocked { get; set; }
    public string BlockReason { get; set; } = string.Empty;
    public int UpdatedCount { get; set; }
    public int CreatedCount { get; set; }
    public int AmbiguousCount { get; set; }
    public int MissingInWorkbookCount { get; set; }
    public int LinkedBillingProfileCount { get; set; }
    public int AutoCreatedCategoryCount { get; set; }
    public int AutoCreatedItemCount { get; set; }
    public List<RentalWorkbookScopeIssue> ScopeIssues { get; set; } = new();
    public List<RentalWorkbookAuditEntry> UpdatedEntries { get; set; } = new();
    public List<RentalWorkbookAuditEntry> AmbiguousEntries { get; set; } = new();
    public List<RentalWorkbookMissingAssetEntry> MissingInWorkbookAssets { get; set; } = new();
}

public sealed class RentalBillingTemplateItemModel
{
    public Guid ItemId { get; set; } = Guid.NewGuid();
    public string DisplayItemName { get; set; } = string.Empty;
    public string BillingLineMode { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1m;
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public string Note { get; set; } = string.Empty;
    public List<Guid> IncludedAssetIds { get; set; } = new();
}

public sealed class RentalBillingRunModel
{
    public Guid RunId { get; set; } = Guid.NewGuid();
    public string RunKey { get; set; } = string.Empty;
    public DateOnly ScheduledDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly PeriodStartDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly PeriodEndDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public int CycleMonths { get; set; } = 1;
    public string PeriodLabel { get; set; } = string.Empty;
    public string Status { get; set; } = "예정";
    public decimal BilledAmount { get; set; }
    public decimal SettledAmount { get; set; }
    public string SettlementStatus { get; set; } = PaymentFlowConstants.SettlementStatusUnpaid;
    public DateOnly? SettledDate { get; set; }
    public string Note { get; set; } = string.Empty;
    public List<RentalBillingTemplateItemModel> Items { get; set; } = new();
}

public sealed class RentalBillingEditorDraftModel
{
    public Guid EditId { get; set; }
    public Guid? CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string BusinessNumber { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string InstallSiteName { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string BillingType { get; set; } = "묶음";
    public string BillingAdvanceMode { get; set; } = "후불";
    public string OfficeCode { get; set; } = string.Empty;
    public string BillingMethod { get; set; } = string.Empty;
    public string BillingStatus { get; set; } = "예정";
    public string SettlementStatus { get; set; } = PaymentFlowConstants.SettlementStatusUnpaid;
    public string CompletionStatus { get; set; } = PaymentFlowConstants.CompletionPending;
    public string Email { get; set; } = string.Empty;
    public int BillingDay { get; set; } = 25;
    public string BillingDayMode { get; set; } = RentalBillingScheduleRules.BillingDayModeFixedDay;
    public int BillingCycleMonths { get; set; } = 1;
    public int BillingAnchorMonth { get; set; } = 3;
    public string DocumentIssueMode { get; set; } = RentalBillingScheduleRules.DocumentIssueModeSameAsDueDate;
    public int DocumentLeadDays { get; set; }
    public decimal MonthlyAmount { get; set; }
    public decimal DepositAmount { get; set; }
    public decimal SettledAmount { get; set; }
    public decimal OutstandingAmount { get; set; }
    public bool RequiresFollowUp { get; set; }
    public string SubmissionDocuments { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool LinkAssetsLater { get; set; }
    public DateTime? BillingAnchorDate { get; set; }
    public DateTime? BillingStartDate { get; set; }
    public DateTime? ContractDate { get; set; }
    public DateTime? ContractStartDate { get; set; }
    public DateTime? ContractEndDate { get; set; }
    public DateTime? LastBilledDate { get; set; }
    public DateTime? LastSettledDate { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid? SelectedTemplateItemId { get; set; }
    public List<RentalBillingTemplateItemModel> TemplateItems { get; set; } = new();
    public List<RentalBillingAssetLinkEdit> AssetLinkEdits { get; set; } = new();
}

public sealed class RentalBillingAssetLinkEdit
{
    public Guid AssetId { get; set; }
    public Guid? CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string InstallSiteName { get; set; } = string.Empty;
    public decimal? MonthlyFee { get; set; }
    public DateOnly? ContractStartDate { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public sealed class RentalAssetLinkCandidate
{
    public LocalRentalAsset Source { get; init; } = new();
    public string CustomerDisplayName { get; set; } = string.Empty;
    public string ResponsibleOfficeName { get; init; } = string.Empty;
    public string ManagementCompanyName { get; init; } = string.Empty;
    public string AssetScopeDisplay { get; init; } = string.Empty;
    public bool IsOutsideCurrentOffice { get; init; }
    public Guid? BillingProfileId { get; init; }
    public string CurrentBillingProfileDisplay { get; init; } = string.Empty;
}

public sealed class RentalCustomerOnboardingDraftModel
{
    public int CurrentStepIndex { get; set; }
    public Guid? CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string BusinessNumber { get; set; } = string.Empty;
    public string Representative { get; set; } = string.Empty;
    public string ContactPerson { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string OfficeCode { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string InstallSiteName { get; set; } = string.Empty;
    public string BillingType { get; set; } = "묶음";
    public string BillingAdvanceMode { get; set; } = "후불";
    public int BillingDay { get; set; } = 25;
    public string BillingDayMode { get; set; } = RentalBillingScheduleRules.BillingDayModeFixedDay;
    public int BillingCycleMonths { get; set; } = 1;
    public int BillingAnchorMonth { get; set; } = 3;
    public string DocumentIssueMode { get; set; } = RentalBillingScheduleRules.DocumentIssueModeSameAsDueDate;
    public int DocumentLeadDays { get; set; }
    public DateTime? BillingStartDate { get; set; }
    public decimal MonthlyAmount { get; set; }
    public string BillingMethod { get; set; } = string.Empty;
    public string SubmissionDocuments { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public bool LinkAssetsLater { get; set; }
    public Guid? SelectedTemplateItemId { get; set; }
    public List<RentalBillingTemplateItemModel> TemplateItems { get; set; } = new();
}

public sealed class RentalBillingViewRow
{
    public Guid SelectionId { get; init; }
    public bool HasPersistedProfile { get; init; } = true;
    public LocalRentalBillingProfile Source { get; init; } = new();
    public int GroupedSourceCount { get; init; } = 1;
    public int GroupedPersistedProfileCount { get; init; }
    public int GroupedUnlinkedAssetCount { get; init; }
    public List<Guid> GroupedSelectionIds { get; init; } = new();
    public List<Guid> GroupedPersistedProfileIds { get; init; } = new();
    public string AggregateSummary { get; init; } = string.Empty;
    public string CustomerDisplayName { get; set; } = string.Empty;
    public string BillingCycleDisplay { get; init; } = string.Empty;
    public string ResponsibleOfficeName { get; init; } = string.Empty;
    public DateOnly? NextBillingDate { get; init; }
    public int? DaysRemaining { get; init; }
    public string DisplayStatus { get; init; } = string.Empty;
    public string SettlementStatus { get; init; } = string.Empty;
    public string CompletionStatus { get; init; } = string.Empty;
    public decimal SettledAmount { get; init; }
    public decimal OutstandingAmount { get; init; }
    public bool RequiresFollowUp { get; init; }
    public DateOnly? LastSettledDate { get; init; }
    public int AssetCount { get; init; }
    public int TemplateItemCount { get; init; }
    public int IncludedAssetCount { get; init; }
    public string BillingType { get; init; } = string.Empty;
    public string InstallSiteName { get; init; } = string.Empty;
    public string InstallLocationDisplay { get; init; } = string.Empty;
    public string BillingAdvanceMode { get; init; } = string.Empty;
    public string BillingDayMode { get; init; } = RentalBillingScheduleRules.BillingDayModeFixedDay;
    public int BillingAnchorMonth { get; init; }
    public string DocumentIssueMode { get; init; } = RentalBillingScheduleRules.DocumentIssueModeSameAsDueDate;
    public int DocumentLeadDays { get; init; }
    public DateOnly? DocumentIssueDate { get; init; }
    public DateOnly? AlertDate { get; init; }
    public string AlertReason { get; init; } = string.Empty;
    public Guid? CurrentBillingRunId { get; init; }
    public string CurrentBillingPeriodLabel { get; init; } = string.Empty;
    public string CurrentBillingRunStatus { get; init; } = string.Empty;
    public decimal CurrentBilledAmount { get; init; }
    public bool HasDataIssue { get; init; }
    public string DataIssueSummary { get; init; } = string.Empty;
    public bool IsAggregateRow => GroupedSourceCount > 1;
    public bool IsSelected { get; set; }
}

public sealed class RentalAssetViewRow
{
    public LocalRentalAsset Source { get; init; } = new();
    public string ResponsibleOfficeName { get; init; } = string.Empty;
    public int? DaysRemaining { get; init; }
    public string CurrentCustomerName { get; init; } = string.Empty;
    public string InstallLocationDisplay { get; init; } = string.Empty;
    public string BillingEligibilityStatus { get; init; } = string.Empty;
    public bool HasDataIssue { get; init; }
    public bool IsSelected { get; set; }
}

public static class RentalAssetStatusRules
{
    public static bool IsNonOperating(string? assetStatus)
        => string.Equals(assetStatus, "대기", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(assetStatus, "회수", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(assetStatus, "폐기", StringComparison.OrdinalIgnoreCase);

    public static string BuildAutoExclusionReason(string? assetStatus)
        => $"자산상태: {(string.IsNullOrWhiteSpace(assetStatus) ? "미확인" : assetStatus.Trim())}";

    public static bool IsAutoGeneratedExclusionReason(string? reason)
        => !string.IsNullOrWhiteSpace(reason) &&
           reason.Trim().StartsWith("자산상태:", StringComparison.OrdinalIgnoreCase);
}

public static class RentalAssetCategoryRules
{
    public static bool IsA3ColorMultiFunctionAsset(LocalRentalAsset? asset)
    {
        if (asset is null)
            return false;

        return IsA3ColorMultiFunctionAsset(asset.ItemCategoryName, asset.ItemName);
    }

    public static bool IsA3ColorMultiFunctionAsset(string? itemCategoryName, string? itemName)
    {
        var normalizedCategory = RentalCatalogValueNormalizer.NormalizeLooseKey(itemCategoryName);
        if (normalizedCategory is "a3컬러복합기" or "a3칼라복합기" or "a3컬러mfp")
            return true;

        var normalizedName = RentalCatalogValueNormalizer.NormalizeLooseKey(itemName);
        if (string.IsNullOrWhiteSpace(normalizedName) || !normalizedName.Contains("a3", StringComparison.OrdinalIgnoreCase))
            return false;

        return normalizedName.Contains("컬러복합기", StringComparison.OrdinalIgnoreCase) ||
               normalizedName.Contains("칼라복합기", StringComparison.OrdinalIgnoreCase) ||
               normalizedName.Contains("컬러mfp", StringComparison.OrdinalIgnoreCase) ||
               normalizedName.Contains("컬러복사기", StringComparison.OrdinalIgnoreCase);
    }
}

public static class RentalContractDateRules
{
    public static RentalContractDateResolution Resolve(
        DateOnly? preferredCustomerContractDate,
        DateOnly? assetContractDate,
        DateOnly? assetContractStartDate,
        DateOnly? assetInstallDate)
    {
        var contractDate = preferredCustomerContractDate
                           ?? assetContractDate
                           ?? assetContractStartDate
                           ?? assetInstallDate;
        var contractStartDate = assetContractStartDate
                                ?? assetInstallDate
                                ?? contractDate;

        return new RentalContractDateResolution(contractDate, contractStartDate);
    }
}

public readonly record struct RentalContractDateResolution(
    DateOnly? ContractDate,
    DateOnly? ContractStartDate);
