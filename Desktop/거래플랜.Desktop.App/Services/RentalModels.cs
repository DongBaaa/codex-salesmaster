using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

public sealed class RentalBillingFilter
{
    public string SearchText { get; set; } = string.Empty;
    public string OfficeCode { get; set; } = string.Empty;
    public string AssignedUsername { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool DueOnly { get; set; }
    public DateOnly ReferenceDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
}

public sealed class RentalAssetFilter
{
    public string SearchText { get; set; } = string.Empty;
    public string ItemCategoryName { get; set; } = string.Empty;
    public string OfficeCode { get; set; } = string.Empty;
    public string AssignedUsername { get; set; } = string.Empty;
    public string AssetStatus { get; set; } = string.Empty;
    public DateOnly ReferenceDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
}

public sealed class RentalAlertItem
{
    public Guid BillingProfileId { get; set; }
    public string ResponsibleOfficeName { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string RealCustomerName { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string AssignedUsername { get; set; } = string.Empty;
    public decimal MonthlyAmount { get; set; }
    public DateOnly NextBillingDate { get; set; }
    public int DaysRemaining { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Summary => string.IsNullOrWhiteSpace(RealCustomerName)
        ? CustomerName
        : $"{CustomerName} / {RealCustomerName}";
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

public sealed class RentalDashboardSummary
{
    public int DueTodayCount { get; set; }
    public int UpcomingCount { get; set; }
    public int OverdueCount { get; set; }
    public int ActiveAssetCount { get; set; }
    public int ExpiringContractCount { get; set; }
    public int UnassignedCount { get; set; }
    public string AlertPopupMessage { get; set; } = string.Empty;
    public IReadOnlyList<RentalAlertItem> AlertItems { get; set; } = Array.Empty<RentalAlertItem>();
    public IReadOnlyList<RentalExpiringAssetItem> ExpiringAssets { get; set; } = Array.Empty<RentalExpiringAssetItem>();
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
    public List<string> AddedCategoryNames { get; } = new();
    public List<string> AddedItemNames { get; } = new();
    public List<string> AmbiguousItemNames { get; } = new();
}

public sealed class RentalBillingTemplateItemModel
{
    public Guid ItemId { get; set; } = Guid.NewGuid();
    public string DisplayItemName { get; set; } = string.Empty;
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

public sealed class RentalBillingViewRow
{
    public LocalRentalBillingProfile Source { get; init; } = new();
    public string ResponsibleOfficeName { get; init; } = string.Empty;
    public string AssignedUsernameDisplay { get; init; } = string.Empty;
    public DateOnly? NextBillingDate { get; init; }
    public int? DaysRemaining { get; init; }
    public string DisplayStatus { get; init; } = string.Empty;
    public string SettlementStatus { get; init; } = string.Empty;
    public string CompletionStatus { get; init; } = string.Empty;
    public decimal SettledAmount { get; init; }
    public decimal OutstandingAmount { get; init; }
    public bool RequiresFollowUp { get; init; }
    public string FollowUpNote { get; init; } = string.Empty;
    public DateOnly? LastSettledDate { get; init; }
    public int AssetCount { get; init; }
    public int TemplateItemCount { get; init; }
    public int IncludedAssetCount { get; init; }
    public string BillingType { get; init; } = string.Empty;
    public string BillToCustomerName { get; init; } = string.Empty;
    public string InstallSiteName { get; init; } = string.Empty;
    public string BillingAdvanceMode { get; init; } = string.Empty;
    public Guid? CurrentBillingRunId { get; init; }
    public string CurrentBillingPeriodLabel { get; init; } = string.Empty;
    public string CurrentBillingRunStatus { get; init; } = string.Empty;
    public decimal CurrentBilledAmount { get; init; }
    public bool HasDataIssue { get; init; }
    public string DataIssueSummary { get; init; } = string.Empty;
    public bool IsSelected { get; set; }
}

public sealed class RentalAssetViewRow
{
    public LocalRentalAsset Source { get; init; } = new();
    public string ResponsibleOfficeName { get; init; } = string.Empty;
    public int? DaysRemaining { get; init; }
    public string CurrentCustomerName { get; init; } = string.Empty;
    public string BillToCustomerName { get; init; } = string.Empty;
    public string InstallSiteName { get; init; } = string.Empty;
    public string BillingEligibilityStatus { get; init; } = string.Empty;
    public bool HasDataIssue { get; init; }
    public bool IsSelected { get; set; }
}
