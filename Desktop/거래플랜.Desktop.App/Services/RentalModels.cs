using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

using System.Text.Json.Serialization;

namespace 거래플랜.Desktop.App.Services;

public sealed class RentalBillingFilter
{
    public string SearchText { get; set; } = string.Empty;
    public string OfficeCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool DueOnly { get; set; }
    public bool PastDueOnly { get; set; }
    public bool ExpandCustomerSummaryRows { get; set; }
    public bool IncludeHistoryRows { get; set; } = true;
    public DateOnly ReferenceDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
}

public sealed class RentalAssetFilter
{
    public string SearchText { get; set; } = string.Empty;
    public List<string> ItemCategoryNames { get; set; } = new();
    public List<string> OfficeCodes { get; set; } = new();
    public List<string> AssetStatuses { get; set; } = new();
    public Guid? PinnedAssetId { get; set; }
    public int MaxResults { get; set; }
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

public sealed class RentalAssetAssignmentHistoryViewItem
{
    public Guid HistoryId { get; init; }
    public Guid AssetId { get; init; }
    public bool IsCurrent { get; init; }
    public bool IsLinkedAtEstimated { get; init; }
    public string StatusDisplay => IsCurrent ? "현재 임대" : "종료";
    public DateTime LinkedAtLocal { get; init; }
    public DateTime? UnlinkedAtLocal { get; init; }
    public string LinkedAtDisplay => IsLinkedAtEstimated ? "시작일 미상" : LinkedAtLocal.ToString("yyyy-MM-dd");
    public string UnlinkedAtDisplay => IsCurrent ? "현재" : UnlinkedAtLocal?.ToString("yyyy-MM-dd") ?? "-";
    public string PeriodDisplay => $"{LinkedAtDisplay} ~ {UnlinkedAtDisplay}";
    public string CustomerName { get; init; } = string.Empty;
    public string InstallLocation { get; init; } = string.Empty;
    public string BillingProfileDisplay { get; init; } = string.Empty;
    public string ResponsibleOfficeName { get; init; } = string.Empty;
    public string ItemName { get; init; } = string.Empty;
    public string MachineNumber { get; init; } = string.Empty;
    public string ManagementNumber { get; init; } = string.Empty;
    public decimal MonthlyFee { get; init; }
    public string ChangeReason { get; init; } = string.Empty;
}

public sealed class RentalAssetAssignmentHistoryEditRequest
{
    public Guid HistoryId { get; set; }
    public Guid AssetId { get; set; }
    public bool IsNew => HistoryId == Guid.Empty;
    public bool IsCurrent { get; set; }
    public DateTime LinkedAtLocal { get; set; } = DateTime.Today;
    public DateTime? UnlinkedAtLocal { get; set; } = DateTime.Today;
    public string CustomerName { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string BillingProfileDisplay { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string MachineNumber { get; set; } = string.Empty;
    public string ManagementNumber { get; set; } = string.Empty;
    public decimal MonthlyFee { get; set; }
    public string ChangeReason { get; set; } = string.Empty;
}

public sealed class RentalEquipmentReplacementRequest
{
    public Guid OriginalAssetId { get; set; }
    public long OriginalAssetRevision { get; set; }
    public Guid ReplacementAssetId { get; set; }
    public long ReplacementAssetRevision { get; set; }
    public DateOnly ReplacementDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string OriginalAssetNextStatus { get; set; } = "창고";
    public string ChangeReason { get; set; } = "렌탈 장비 교체";
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
    public string Specification { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public string MaterialNumber { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RepresentativeAssetId { get; set; }
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
    public long Revision { get; set; }
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

public sealed class RentalBillingHistoryRow
{
    public Guid BillingProfileId { get; init; }
    public Guid BillingRunId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string PeriodLabel { get; init; } = string.Empty;
    public DateOnly ScheduledDate { get; init; }
    public decimal BilledAmount { get; init; }
    public decimal SettledAmount { get; init; }
    public decimal OutstandingAmount { get; init; }
    public DateOnly? SettledDate { get; init; }
    public string BillingStatus { get; init; } = string.Empty;
    public string SettlementStatus { get; init; } = string.Empty;
    public bool HasInvoice { get; init; }
    public Guid? InvoiceId { get; init; }
    public long? InvoiceRevision { get; init; }
    public bool IsPastUnresolved { get; init; }
    public bool CanRegisterSettlement => BillingRunId != Guid.Empty && OutstandingAmount > 0m;
    public bool HasSettlement => SettledAmount > 0m;
    public bool CanDelete => BillingRunId != Guid.Empty && (HasInvoice || HasSettlement);
    public string ActionLabel => CanRegisterSettlement
        ? SettledAmount > 0m ? "추가 입금" : "입금 등록"
        : "완료";
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
    public Dictionary<Guid, long> GroupedProfileRevisions { get; init; } = new();
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
    public List<RentalBillingHistoryRow> BillingHistoryRows { get; init; } = new();
    public int PastUnresolvedCount { get; init; }
    public decimal PastUnresolvedAmount { get; init; }
    public DateOnly? OldestPastUnresolvedScheduledDate { get; init; }
    public string OldestPastUnresolvedPeriodLabel { get; init; } = string.Empty;
    public bool HasPastUnresolved => PastUnresolvedCount > 0 || PastUnresolvedAmount > 0m;
    public string PastUnresolvedSummary => HasPastUnresolved
        ? $"이전 청구월 미처리 {PastUnresolvedCount:N0}건 / 미수 {PastUnresolvedAmount:N0}원"
        : "이전 청구월 미처리 내역 없음";
    public bool HasDataIssue { get; init; }
    public string DataIssueSummary { get; init; } = string.Empty;
    public bool IsAggregateRow => GroupedSourceCount > 1;
    public bool HasUnlinkedBillingAssets => GroupedUnlinkedAssetCount > 0;
    public bool RequiresBillingProfileCreation => GroupedPersistedProfileCount == 0 && GroupedUnlinkedAssetCount > 0;
    public string BillingSetupStatus
    {
        get
        {
            if (RequiresBillingProfileCreation)
                return GroupedUnlinkedAssetCount > 1 ? $"생성필요 {GroupedUnlinkedAssetCount:N0}대" : "생성필요";
            if (HasUnlinkedBillingAssets)
                return $"미연결 {GroupedUnlinkedAssetCount:N0}대";
            return "설정완료";
        }
    }
    public string BillingSetupHelpText
        => RequiresBillingProfileCreation
            ? GroupedUnlinkedAssetCount > 1
                ? $"같은 거래처의 미연결 장비 {GroupedUnlinkedAssetCount:N0}대를 묶어 표시했습니다. '개별 청구건 보기'로 전환해 장비별 내용을 확인한 뒤 저장하면 청구 프로필이 생성됩니다."
                : "청구 프로필이 없는 렌탈 장비입니다. 내용을 확인한 뒤 저장하면 청구 프로필이 생성됩니다."
            : HasUnlinkedBillingAssets
                ? $"청구 프로필에 연결되지 않은 장비 {GroupedUnlinkedAssetCount:N0}대가 함께 있습니다. 필요하면 '개별 청구건 보기'에서 정리하세요."
                : "청구 설정이 완료된 건입니다.";
    public string SettlementStatusDisplay
    {
        get
        {
            if (RequiresBillingProfileCreation)
                return "청구 전";
            if (string.Equals(SettlementStatus, "생성필요", StringComparison.OrdinalIgnoreCase))
                return "청구 전";
            return string.IsNullOrWhiteSpace(SettlementStatus) ? PaymentFlowConstants.SettlementStatusUnpaid : SettlementStatus;
        }
    }
    public bool IsSelected { get; set; }
}

public sealed class RentalAssetViewRow
{
    public LocalRentalAsset Source { get; init; } = new();
    public bool HasFullDetail { get; init; } = true;
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
    public static string Normalize(string? assetStatus)
        => RentalAssetStatusNormalizer.Normalize(assetStatus);

    public static bool IsWarehouse(string? assetStatus)
        => RentalAssetStatusNormalizer.IsWarehouse(assetStatus);

    public static bool IsNonOperating(string? assetStatus)
        => RentalAssetStatusNormalizer.IsNonOperating(assetStatus);

    public static string BuildAutoExclusionReason(string? assetStatus)
    {
        var normalizedStatus = Normalize(assetStatus);
        return $"자산상태: {(string.IsNullOrWhiteSpace(normalizedStatus) ? "미확인" : normalizedStatus)}";
    }

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
