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
    public string ModelName { get; set; } = string.Empty;
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
    public string ModelName { get; set; } = string.Empty;
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

public sealed class RentalBillingViewRow
{
    public LocalRentalBillingProfile Source { get; init; } = new();
    public string ResponsibleOfficeName { get; init; } = string.Empty;
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
    public bool IsSelected { get; set; }
}

public sealed class RentalAssetViewRow
{
    public LocalRentalAsset Source { get; init; } = new();
    public string ResponsibleOfficeName { get; init; } = string.Empty;
    public int? DaysRemaining { get; init; }
    public bool IsSelected { get; set; }
}
