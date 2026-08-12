using System.Data;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public enum DataIntegrityDirectActionKind
{
    None,
    OpenRentalBillingProfile,
    OpenRentalAsset,
    OpenInventoryItem,
    OpenCustomer,
    OpenInvoice,
    OpenPaymentForInvoice,
    OpenSyncDiagnostics,
    OpenEnvironmentSettings
}

public static class DataIntegrityIssueCodes
{
    public const string RentalBillingTemplateInvalid = "rental_billing_template_invalid";
    public const string RentalProfileTemplateEmpty = "rental_profile_template_empty";
    public const string RentalProfileMonthlyAmountMismatch = "rental_profile_monthly_amount_mismatch";
    public const string RentalTemplateItemWithoutAsset = "rental_template_item_without_asset";
    public const string RentalTemplateMissingAsset = "rental_template_missing_asset";
    public const string RentalAssetTemplateMonthlyMismatch = "rental_asset_template_monthly_mismatch";
    public const string RentalAssetProfileScopeMismatch = "rental_asset_profile_scope_mismatch";
    public const string RentalAssetMissingProfileTemplateReference = "rental_asset_missing_profile_template_reference";
    public const string RentalOperationalScopeMismatch = "rental_operational_scope_mismatch";
    public const string RentalCustomerNameMismatch = "rental_customer_name_mismatch";
    public const string RentalAssetInMultipleProfileTemplates = "rental_asset_in_multiple_profile_templates";
    public const string RentalProfileWithoutLinkedAssets = "rental_profile_without_linked_assets";
    public const string RentalProfileLinkedAssetsOutsideCurrentScope = "rental_profile_linked_assets_outside_current_scope";
    public const string RentalBillableAssetWithoutMonthlyFee = "rental_billable_asset_without_monthly_fee";
    public const string RentalAssetBillingEligibilityUnconfirmed = "rental_asset_billing_eligibility_unconfirmed";
    public const string RentalAssetMissingBillingProfile = "rental_asset_missing_billing_profile";
    public const string RentalAssignmentMissingReference = "rental_assignment_missing_reference";
    public const string RentalAssignmentHistoricalStaleReference = "rental_assignment_historical_stale_reference";
    public const string RentalAssetMultipleCurrentAssignments = "rental_asset_multiple_current_assignments";
    public const string RentalBillingRunSettlementMismatch = "rental_billing_run_settlement_mismatch";
    public const string RentalBillingRunMissingRunId = "rental_billing_run_missing_run_id";
    public const string RentalBillingRunKeyConflictingRunIds = "rental_billing_run_key_conflicting_run_ids";
    public const string RentalBillingRunsJsonMalformed = "rental_billing_runs_json_malformed";
    public const string RentalBillingProfileSummaryMismatch = "rental_billing_profile_summary_mismatch";
    public const string CustomerDuplicateCandidate = "customer_duplicate_candidate";
    public const string ItemDuplicateCandidate = "item_duplicate_candidate";
    public const string WarehouseDuplicateCandidate = "warehouse_duplicate_candidate";
    public const string CustomerContractMissingCustomerReference = "customer_contract_missing_customer_reference";
    public const string InvoiceAmountMismatch = "invoice_amount_mismatch";
    public const string InvoiceOverSettled = "invoice_over_settled";
    public const string InvoiceLineMissingInvoiceReference = "invoice_line_missing_invoice_reference";
    public const string PaymentMissingInvoiceReference = "payment_missing_invoice_reference";
    public const string InvoiceLinkedTransactionPaymentMismatch = "invoice_linked_transaction_payment_mismatch";
    public const string TransactionOperationalScopeMismatch = "transaction_operational_scope_mismatch";
    public const string RentalDeletedInvoiceActivePayment = "rental_deleted_invoice_active_payment";
    public const string RentalInvoiceDeletedPaymentDetachedTransaction = "rental_invoice_deleted_payment_detached_transaction";
    public const string TransactionAttachmentMissingTransactionReference = "transaction_attachment_missing_transaction_reference";
    public const string MissingAttachmentFiles = "missing_attachment_files";
    public const string RentalBillingLogMissingProfileReference = "rental_billing_log_missing_profile_reference";
    public const string InventoryTransferLineMissingTransferReference = "inventory_transfer_line_missing_transfer_reference";
    public const string InventoryDeletedItemStockResidue = "inventory_deleted_item_stock_residue";
    public const string InventoryStockSnapshotMismatch = "inventory_stock_snapshot_mismatch";
    public const string InventoryWarehouseReferenceMissing = "inventory_warehouse_reference_missing";
}

public sealed class DataIntegrityIssueDefinition
{
    public DataIntegrityIssueDefinition(string code, string title, string severity, string area, string description, string suggestedAction)
    {
        Code = code;
        Title = title;
        Severity = severity;
        Area = area;
        Description = description;
        SuggestedAction = suggestedAction;
    }

    public string Code { get; }
    public string Title { get; }
    public string Severity { get; }
    public string Area { get; }
    public string Description { get; }
    public string SuggestedAction { get; }
}

public sealed class DataIntegrityIssueSummary
{
    public string Code { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Severity { get; init; } = "Warning";
    public string Area { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SuggestedAction { get; init; } = string.Empty;
    public int Count { get; init; }
    public bool HasDirectAction { get; init; }

    public string CountText => $"{Count:N0}건";
    public string SeverityDisplay => DataIntegritySeverityFormatter.ToDisplayText(Severity);
}

public sealed class DataIntegrityIssueDetail
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Code { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Severity { get; init; } = "Warning";
    public string Area { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public Guid? EntityId { get; init; }
    public Guid? ProfileId { get; init; }
    public Guid? AssetId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string ItemName { get; init; } = string.Empty;
    public string AssetDisplayName { get; init; } = string.Empty;
    public string OfficeCode { get; init; } = string.Empty;
    public string CurrentValue { get; init; } = string.Empty;
    public string ExpectedValue { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string SuggestedAction { get; init; } = string.Empty;
    public DataIntegrityDirectActionKind DirectActionKind { get; init; }
    public IReadOnlyList<Guid> RelatedEntityIds { get; init; } = Array.Empty<Guid>();
    public string ReviewInfo { get; init; } = string.Empty;
    public DataIntegrityItemDuplicateComparison? ItemDuplicateComparison { get; init; }

    public bool HasDirectAction => DirectActionKind != DataIntegrityDirectActionKind.None;
    public bool CanMergeDuplicates => RelatedEntityIds.Count > 1 &&
                                      (string.Equals(Code, DataIntegrityIssueCodes.CustomerDuplicateCandidate, StringComparison.OrdinalIgnoreCase) ||
                                       (string.Equals(Code, DataIntegrityIssueCodes.ItemDuplicateCandidate, StringComparison.OrdinalIgnoreCase) &&
                                        ItemDuplicateComparison?.CanMerge == true));
    public bool CanReviewDuplicateCandidates =>
        string.Equals(Code, DataIntegrityIssueCodes.ItemDuplicateCandidate, StringComparison.OrdinalIgnoreCase)
            ? ItemDuplicateComparison?.Candidates.Count > 1
            : CanMergeDuplicates;
    public string SeverityDisplay => DataIntegritySeverityFormatter.ToDisplayText(Severity);
    public string RelatedEntityIdText => RelatedEntityIds.Count == 0
        ? string.Empty
        : string.Join(" / ", RelatedEntityIds.Select(id => id.ToString("N")));
    public string ReviewInfoDisplay
    {
        get
        {
            var review = (ReviewInfo ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(review))
                return review;

            var target = EntityId.HasValue
                ? $"{NormalizeDetailText(EntityType, "대상")} {EntityId.Value:N}"
                : NormalizeDetailText(EntityType, string.Empty);
            return string.IsNullOrWhiteSpace(target)
                ? SuggestedAction
                : $"{target} · {SuggestedAction}";
        }
    }
    public string MergeActionText => CanMergeDuplicates
        ? "중복 병합"
        : CanReviewDuplicateCandidates
            ? "후보 비교"
            : "병합 대상 아님";
    public string DuplicateReviewActionText =>
        string.Equals(Code, DataIntegrityIssueCodes.ItemDuplicateCandidate, StringComparison.OrdinalIgnoreCase) &&
        ItemDuplicateComparison?.Candidates.Count > 1
            ? "후보 비교"
            : MergeActionText;
    public string DirectActionText => DirectActionKind switch
    {
        DataIntegrityDirectActionKind.OpenRentalAsset => "자산 바로가기",
        DataIntegrityDirectActionKind.OpenRentalBillingProfile => "청구관리 바로가기",
        DataIntegrityDirectActionKind.OpenInventoryItem => "품목/재고 바로가기",
        DataIntegrityDirectActionKind.OpenCustomer => "거래처 바로가기",
        DataIntegrityDirectActionKind.OpenInvoice => "전표 바로가기",
        DataIntegrityDirectActionKind.OpenPaymentForInvoice => "수금/지급 바로가기",
        DataIntegrityDirectActionKind.OpenSyncDiagnostics => "동기화 진단 바로가기",
        DataIntegrityDirectActionKind.OpenEnvironmentSettings => "환경설정 바로가기",
        _ => "수동 확인"
    };

    private static string NormalizeDetailText(string? value, string fallback)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed;
    }
}

public sealed class DataIntegrityItemDuplicateComparison
{
    public IReadOnlyList<DataIntegrityItemDuplicateCandidate> Candidates { get; init; } = Array.Empty<DataIntegrityItemDuplicateCandidate>();
    public string SnapshotToken { get; init; } = string.Empty;
    public bool CanMerge { get; init; }
    public IReadOnlyList<string> BlockingConflictFields { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();
    public string BlockingReasonText => BlockingReasons.Count == 0 ? "병합 가능" : string.Join(" / ", BlockingReasons);
    public string SummaryText { get; init; } = string.Empty;
    public Guid? RecommendedCanonicalId { get; init; }
    public int TotalReferenceCount => Candidates.Sum(candidate => candidate.TotalReferenceCount);
    public decimal CurrentStockTotal => Candidates.Sum(candidate => candidate.CurrentStock);
    public decimal WarehouseStockTotal => Candidates.Sum(candidate => candidate.WarehouseStock);
}

public sealed class DataIntegrityItemDuplicateReviewPreparation
{
    public DataIntegrityItemDuplicateComparison Comparison { get; init; } = new();
    public IReadOnlyList<string> PermissionBlockingReasons { get; init; } = Array.Empty<string>();
    public bool CanMerge => Comparison.CanMerge && PermissionBlockingReasons.Count == 0;
    public string BlockingReasonText
    {
        get
        {
            var reasons = Comparison.BlockingReasons
                .Concat(PermissionBlockingReasons)
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return reasons.Count == 0 ? "병합 가능" : string.Join(" / ", reasons);
        }
    }
}

public sealed class DataIntegrityItemDuplicateCandidate
{
    public Guid ItemId { get; init; }
    public string ItemIdText => ItemId.ToString("N")[..8];
    public string TenantCode { get; init; } = string.Empty;
    public string OfficeCode { get; init; } = string.Empty;
    public long Revision { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public bool IsDirty { get; init; }
    public int UnresolvedOutboxCount { get; init; }
    public string NameOriginal { get; init; } = string.Empty;
    public string SpecificationOriginal { get; init; } = string.Empty;
    public string CategoryName { get; init; } = string.Empty;
    public string ItemKind { get; init; } = string.Empty;
    public string TrackingType { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public decimal BoxQuantity { get; init; }
    public string StorageLocation { get; init; } = string.Empty;
    public decimal CurrentStock { get; init; }
    public decimal WarehouseStock { get; init; }
    public decimal SafetyStock { get; init; }
    public decimal PurchasePrice { get; init; }
    public decimal SalePrice { get; init; }
    public decimal RetailPrice { get; init; }
    public decimal PriceGradeA { get; init; }
    public decimal PriceGradeB { get; init; }
    public decimal PriceGradeC { get; init; }
    public DateOnly? LastPurchaseDate { get; init; }
    public DateOnly? LastSaleDate { get; init; }
    public bool CatalogExtensionSyncPending { get; init; }
    public string SimpleMemo { get; init; } = string.Empty;
    public bool IsRental { get; init; }
    public bool IsSale { get; init; }
    public string SerialNumber { get; init; } = string.Empty;
    public string MaterialNumber { get; init; } = string.Empty;
    public string InstallLocation { get; init; } = string.Empty;
    public DateOnly? RentalStartDate { get; init; }
    public DateOnly? RentalEndDate { get; init; }
    public string Notes { get; init; } = string.Empty;
    public int InvoiceLineCount { get; init; }
    public int InvoiceLineSerialCount { get; init; }
    public int RentalAssetCount { get; init; }
    public int RentalBillingTemplateCount { get; init; }
    public int InventoryTransferLineCount { get; init; }
    public int InventoryMovementCount { get; init; }
    public int StockLayerCount { get; init; }
    public int SerialLedgerCount { get; init; }
    public int ItemWarehouseStockRowCount { get; init; }
    public int ItemPriceGradeCount { get; init; }
    public int TotalReferenceCount { get; init; }
    public string ReferenceIdentityFingerprint { get; init; } = string.Empty;
    public bool IsRecommended { get; init; }
    public string RecommendationText { get; init; } = string.Empty;
    public string NameAndSpecification => $"{NameOriginal} / {SpecificationOriginal}";
    public string ScopeText => $"{TenantCode} / {OfficeCode}";
    public string StockSummary => $"현재 {CurrentStock:N2} / 창고 {WarehouseStock:N2}";
    public string ReferenceSummary =>
        $"전표 {InvoiceLineCount:N0}, 시리얼 {InvoiceLineSerialCount:N0}, 자산 {RentalAssetCount:N0}, 청구템플릿 {RentalBillingTemplateCount:N0}, 이동 {InventoryTransferLineCount:N0}, 재고원장 {InventoryMovementCount:N0}, 재고층 {StockLayerCount:N0}, 시리얼원장 {SerialLedgerCount:N0}, 창고재고 {ItemWarehouseStockRowCount:N0}, 사용자등급가 {ItemPriceGradeCount:N0}";
    public string MasterDataSummary =>
        $"분류 {Display(CategoryName)} / 종류 {Display(ItemKind)} / 추적 {Display(TrackingType)} / 단위 {Display(Unit)} / 보관 {Display(StorageLocation)} / 매입 {PurchasePrice:N0} / 매출 {SalePrice:N0} / 소매 {RetailPrice:N0}";
    public string AssetDataSummary =>
        $"대여 {IsRental} / 판매 {IsSale} / 시리얼 {Display(SerialNumber)} / 자재 {Display(MaterialNumber)} / 설치 {Display(InstallLocation)} / 대여기간 {Display(RentalStartDate)}~{Display(RentalEndDate)} / 메모 {Display(Notes)}";
    public string SyncStateText => $"rev {Revision:N0} / {UpdatedAtUtc:yyyy-MM-dd HH:mm:ss}Z / dirty {(IsDirty ? "Y" : "N")} / outbox {UnresolvedOutboxCount:N0}";

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    private static string Display(DateOnly? value) => value?.ToString("yyyy-MM-dd") ?? "-";
}

public sealed class DataIntegrityIssueFilterOption
{
    public string Code { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    public override string ToString() => DisplayName;
}

internal static class DataIntegritySeverityFormatter
{
    public static string Normalize(string? severity)
    {
        var value = severity?.Trim();
        if (string.Equals(value, "Error", StringComparison.OrdinalIgnoreCase))
            return "Error";
        if (string.Equals(value, "Info", StringComparison.OrdinalIgnoreCase))
            return "Info";

        return "Warning";
    }

    public static string ToDisplayText(string? severity)
        => Normalize(severity) switch
        {
            "Error" => "오류",
            "Info" => "참고",
            _ => "주의"
        };

    public static int GetSortWeight(string? severity)
        => Normalize(severity) switch
        {
            "Error" => 3,
            "Info" => 1,
            _ => 2
        };

    public static bool IsActionRequired(string? severity)
        => Normalize(severity) != "Info";

    public static bool IsError(string? severity)
        => Normalize(severity) == "Error";

    public static bool IsWarning(string? severity)
        => Normalize(severity) == "Warning";

    public static bool IsInformational(string? severity)
        => Normalize(severity) == "Info";
}

public sealed class DataIntegrityScanResult
{
    public DataIntegrityScanResult(DateTime scannedAtLocal, IReadOnlyList<DataIntegrityIssueSummary> summaries, IReadOnlyList<DataIntegrityIssueDetail> issues)
    {
        ScannedAtLocal = scannedAtLocal;
        Summaries = summaries;
        Issues = issues;
        IssueSignature = string.Join("|", summaries.OrderBy(summary => summary.Code).Select(summary => $"{summary.Code}:{summary.Count}"));
    }

    public DateTime ScannedAtLocal { get; }
    public IReadOnlyList<DataIntegrityIssueSummary> Summaries { get; }
    public IReadOnlyList<DataIntegrityIssueDetail> Issues { get; }
    public int TotalIssueCount => Issues.Count;
    public int ErrorIssueCount => Issues.Count(issue => DataIntegritySeverityFormatter.IsError(issue.Severity));
    public int WarningIssueCount => Issues.Count(issue => DataIntegritySeverityFormatter.IsWarning(issue.Severity));
    public int ActionRequiredIssueCount => Issues.Count(issue => DataIntegritySeverityFormatter.IsActionRequired(issue.Severity));
    public int InformationalIssueCount => Issues.Count(issue => DataIntegritySeverityFormatter.IsInformational(issue.Severity));
    public int PassiveStartupNoticeIssueCount => Issues.Count(IntegrityIssueReviewPolicy.RequiresPassiveStartupNotice);
    public bool HasIssues => Issues.Count > 0;
    public bool HasActionRequiredIssues => ActionRequiredIssueCount > 0;
    public bool HasPassiveStartupNoticeIssues => Issues.Any(IntegrityIssueReviewPolicy.RequiresPassiveStartupNotice);
    public string PassiveStartupNoticeSignature => string.Join(
        "|",
        Issues
            .Where(IntegrityIssueReviewPolicy.RequiresPassiveStartupNotice)
            .GroupBy(issue => issue.Code, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{group.Key}:{group.Count()}"));
    public string IssueSignature { get; }
    public string ScannedAtText => ScannedAtLocal.ToString("yyyy-MM-dd HH:mm:ss");

    public string BuildPassiveStartupStatusMessage()
    {
        var counts = $"오류 {ErrorIssueCount:N0}건, 주의 {WarningIssueCount:N0}건, 참고 {InformationalIssueCount:N0}건";
        return ErrorIssueCount > 0
            ? $"운영 점검 알림: {counts}입니다. 오류가 있어 모든 업무가 안전하다고 볼 수 없습니다. 영향받을 수 있는 저장·청구 업무 전 환경설정 > 동기화 진단 > 무결성 리포트에서 상세 내용을 확인하세요."
            : $"운영 점검 알림: {counts}입니다. 업무는 계속할 수 있으며, 상세 내용은 환경설정 > 동기화 진단 > 무결성 리포트에서 확인하세요.";
    }
}

public sealed class DataIntegrityIssueService
{
    private const int LocalQueryContainsBatchSize = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly IReadOnlyDictionary<string, DataIntegrityIssueDefinition> Definitions = new Dictionary<string, DataIntegrityIssueDefinition>(StringComparer.OrdinalIgnoreCase)
    {
        [DataIntegrityIssueCodes.RentalBillingTemplateInvalid] = new(
            DataIntegrityIssueCodes.RentalBillingTemplateInvalid,
            "청구 품목 데이터 손상",
            "Error",
            "렌탈 청구",
            "청구 프로필의 품목 JSON을 해석할 수 없어 청구 금액 계산이 불안정합니다.",
            "청구관리에서 해당 프로필을 열고 품목을 다시 저장하세요."),
        [DataIntegrityIssueCodes.RentalProfileTemplateEmpty] = new(
            DataIntegrityIssueCodes.RentalProfileTemplateEmpty,
            "청구 품목 없음",
            "Warning",
            "렌탈 청구",
            "청구 프로필은 있으나 청구서 표시 품목이 비어 있습니다.",
            "청구관리에서 표시 품목과 연결 자산을 확인하세요."),
        [DataIntegrityIssueCodes.RentalProfileMonthlyAmountMismatch] = new(
            DataIntegrityIssueCodes.RentalProfileMonthlyAmountMismatch,
            "월 기준금액 불일치",
            "Warning",
            "렌탈 청구",
            "청구 프로필 월 기준금액과 품목별 수량×단가 합계가 다릅니다.",
            "청구관리에서 품목 단가/수량을 저장해 월 기준금액을 재계산하세요."),
        [DataIntegrityIssueCodes.RentalTemplateItemWithoutAsset] = new(
            DataIntegrityIssueCodes.RentalTemplateItemWithoutAsset,
            "품목-자산 연결 없음",
            "Warning",
            "렌탈 청구",
            "청구서 표시 품목에 연결된 렌탈 자산이 없습니다.",
            "청구관리에서 품목별 연결 자산을 지정하세요."),
        [DataIntegrityIssueCodes.RentalTemplateMissingAsset] = new(
            DataIntegrityIssueCodes.RentalTemplateMissingAsset,
            "삭제/누락 자산 참조",
            "Error",
            "렌탈 청구",
            "청구 품목이 존재하지 않거나 삭제된 자산 ID를 참조합니다.",
            "청구관리에서 해당 품목의 연결 자산을 다시 지정하세요."),
        [DataIntegrityIssueCodes.RentalAssetTemplateMonthlyMismatch] = new(
            DataIntegrityIssueCodes.RentalAssetTemplateMonthlyMismatch,
            "자산 월요금-품목 금액 불일치",
            "Warning",
            "렌탈 청구",
            "연결 자산의 월요금 합계와 청구 품목 금액이 다릅니다.",
            "자산 월요금 또는 청구 품목 단가를 하나의 기준으로 맞춘 뒤 저장하세요."),
        [DataIntegrityIssueCodes.RentalAssetProfileScopeMismatch] = new(
            DataIntegrityIssueCodes.RentalAssetProfileScopeMismatch,
            "자산-청구 프로필 범위 불일치",
            "Error",
            "렌탈 연결",
            "자산에 저장된 청구 프로필/지점/업체 범위가 청구 품목 참조와 다릅니다.",
            "청구관리 또는 자산 화면에서 연결 프로필을 다시 지정하세요."),
        [DataIntegrityIssueCodes.RentalAssetMissingProfileTemplateReference] = new(
            DataIntegrityIssueCodes.RentalAssetMissingProfileTemplateReference,
            "프로필 연결 자산이 표시품목에서 누락",
            "Error",
            "렌탈 연결",
            "자산은 청구 프로필에 연결되어 있으나 청구서 표시 품목의 IncludedAssetIds에는 없어 실제 전표/청구 대상에서 누락될 수 있습니다.",
            "렌탈 청구관리에서 거래처 임대 자산과 청구서 표시 품목을 다시 저장하거나 잘못 연결된 자산을 해제하세요."),
        [DataIntegrityIssueCodes.RentalOperationalScopeMismatch] = new(
            DataIntegrityIssueCodes.RentalOperationalScopeMismatch,
            "렌탈 scope 자체 불일치",
            "Error",
            "렌탈 범위",
            "청구 프로필 또는 자산의 tenant·owner·담당지점 값이 서로 맞지 않아 다른 점검 기준도 왜곡될 수 있습니다.",
            "청구관리 또는 자산 화면에서 담당지점을 다시 저장해 canonical scope로 맞추세요."),
        [DataIntegrityIssueCodes.RentalCustomerNameMismatch] = new(
            DataIntegrityIssueCodes.RentalCustomerNameMismatch,
            "거래처명 표시 불일치",
            "Warning",
            "렌탈 거래처",
            "렌탈 청구/자산에 저장된 거래처 표시명과 연결된 거래처 마스터명이 다릅니다.",
            "기관/지점이 맞는지 확인한 뒤 청구관리 또는 자산 화면에서 개별 저장으로 정리하세요."),
        [DataIntegrityIssueCodes.RentalAssetInMultipleProfileTemplates] = new(
            DataIntegrityIssueCodes.RentalAssetInMultipleProfileTemplates,
            "자산 중복 청구 연결",
            "Error",
            "렌탈 연결",
            "하나의 자산이 여러 청구 프로필 품목에 동시에 포함되어 중복 청구 위험이 있습니다.",
            "자산 또는 청구관리에서 실제 청구 대상 프로필 하나만 남기세요."),
        [DataIntegrityIssueCodes.RentalProfileWithoutLinkedAssets] = new(
            DataIntegrityIssueCodes.RentalProfileWithoutLinkedAssets,
            "프로필 연결 자산 없음",
            "Warning",
            "렌탈 연결",
            "청구 프로필에 연결된 자산이 없습니다.",
            "청구관리에서 자산을 연결하거나 더 이상 쓰지 않는 프로필이면 보류/삭제 검토하세요."),
        [DataIntegrityIssueCodes.RentalProfileLinkedAssetsOutsideCurrentScope] = new(
            DataIntegrityIssueCodes.RentalProfileLinkedAssetsOutsideCurrentScope,
            "현재 범위 밖 연결 자산 존재",
            "Warning",
            "렌탈 연결",
            "현재 업무 범위에는 보이지 않지만 다른 운영 범위의 활성 자산이 이 청구 프로필을 참조합니다.",
            "프로필을 미사용으로 판단하거나 삭제하지 말고, 권한이 있는 담당자가 해당 운영 범위의 연결 상태를 확인하세요."),
        [DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee] = new(
            DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee,
            "청구대상 자산 월요금 없음",
            "Warning",
            "렌탈 자산",
            "청구대상 자산인데 월요금이 0원입니다.",
            "자산 화면에서 월요금을 입력한 뒤 연결 청구 프로필에 반영하세요."),
        [DataIntegrityIssueCodes.RentalAssetBillingEligibilityUnconfirmed] = new(
            DataIntegrityIssueCodes.RentalAssetBillingEligibilityUnconfirmed,
            "청구상태 확인 필요",
            "Warning",
            "렌탈 자산",
            "운용 중인 자산의 청구대상 여부를 현재 저장값만으로 확정할 수 없습니다.",
            "자산 화면에서 실제 청구대상 또는 청구제외 상태를 명시적으로 선택하고 저장하세요."),
        [DataIntegrityIssueCodes.RentalAssetMissingBillingProfile] = new(
            DataIntegrityIssueCodes.RentalAssetMissingBillingProfile,
            "자산의 청구 프로필 누락",
            "Error",
            "렌탈 연결",
            "자산에 저장된 청구 프로필 ID가 현재 DB에 없습니다.",
            "자산 화면에서 청구 연결을 해제하거나 올바른 청구 프로필로 다시 연결하세요."),
        [DataIntegrityIssueCodes.RentalAssignmentMissingReference] = new(
            DataIntegrityIssueCodes.RentalAssignmentMissingReference,
            "임대이력 참조 누락",
            "Error",
            "렌탈 이력",
            "현재 렌탈 임대이력이 존재하지 않거나 삭제된 자산·거래처·청구 프로필을 참조합니다.",
            "현재 이력은 자산·거래처·청구 프로필을 재연결하세요. 과거 이력은 스냅샷 표시값 보존 대상으로 분류합니다."),
        [DataIntegrityIssueCodes.RentalAssignmentHistoricalStaleReference] = new(
            DataIntegrityIssueCodes.RentalAssignmentHistoricalStaleReference,
            "과거 임대이력 참조 보존",
            "Info",
            "렌탈 이력",
            "과거 렌탈 임대이력의 자산/거래처/청구 프로필 참조가 현재 마스터에서 사라졌지만 스냅샷 표시값은 남아 있습니다.",
            "현재 청구·설치 흐름에는 영향을 주지 않는 과거 이력입니다. 필요 시 백업 기준으로 과거 참조 정리 여부를 검토하세요."),
        [DataIntegrityIssueCodes.RentalAssetMultipleCurrentAssignments] = new(
            DataIntegrityIssueCodes.RentalAssetMultipleCurrentAssignments,
            "현재 임대이력 중복",
            "Error",
            "렌탈 이력",
            "하나의 렌탈 자산에 현재 임대중으로 표시된 이력이 여러 개 있습니다.",
            "임대이력에서 실제 현재 이력 1건만 남기고 나머지는 과거 이력으로 수정하세요."),
        [DataIntegrityIssueCodes.RentalBillingRunSettlementMismatch] = new(
            DataIntegrityIssueCodes.RentalBillingRunSettlementMismatch,
            "렌탈 청구 정산금액 불일치",
            "Error",
            "렌탈 청구",
            "렌탈 청구 run의 저장 정산금액과 실제 활성 수금/거래내역 합계가 다릅니다.",
            "청구관리에서 해당 청구월의 전표/수금/거래내역을 확인하고 정산 재계산이 필요한지 점검하세요."),
        [DataIntegrityIssueCodes.RentalBillingRunMissingRunId] = new(
            DataIntegrityIssueCodes.RentalBillingRunMissingRunId,
            "렌탈 청구 run ID 누락",
            "Info",
            "렌탈 청구",
            "렌탈 청구 프로필에 run ID가 비어 있는 과거 청구 JSON이 있습니다.",
            "청구관리에서 해당 프로필을 열고 전표/수금 근거를 확인한 뒤 필요 시 수동 정리하세요."),
        [DataIntegrityIssueCodes.RentalBillingRunKeyConflictingRunIds] = new(
            DataIntegrityIssueCodes.RentalBillingRunKeyConflictingRunIds,
            "렌탈 청구 RunKey-RunId 식별자 충돌",
            "Error",
            "렌탈 청구",
            "동일 normalized RunKey의 여러 non-empty RunId 또는 동일 RunId의 여러 normalized RunKey 때문에 재무 작업 대상을 안전하게 식별할 수 없습니다.",
            "청구관리에서 표시된 프로필과 RunKey/RunId의 전표·수금 근거를 확인해 수동으로 정리하세요."),
        [DataIntegrityIssueCodes.RentalBillingRunsJsonMalformed] = new(
            DataIntegrityIssueCodes.RentalBillingRunsJsonMalformed,
            "렌탈 청구 실행 이력 JSON 손상",
            "Error",
            "렌탈 청구",
            "BillingRunsJson이 올바른 JSON 배열이 아니어서 청구 실행 이력을 안전하게 읽을 수 없습니다.",
            "청구관리에서 프로필과 전표/수금 근거를 확인한 뒤 원문을 보존하며 수동으로 복구하세요."),
        [DataIntegrityIssueCodes.RentalBillingProfileSummaryMismatch] = new(
            DataIntegrityIssueCodes.RentalBillingProfileSummaryMismatch,
            "렌탈 청구 프로필 요약 불일치",
            "Error",
            "렌탈 청구",
            "렌탈 청구 프로필 요약 정산/미수금액이 대표 청구 run의 실제 입금 근거와 다릅니다.",
            "청구관리에서 대표 청구월의 전표/수금/거래내역을 확인하고 프로필 요약 재계산이 필요한지 점검하세요."),
        [DataIntegrityIssueCodes.CustomerDuplicateCandidate] = new(
            DataIntegrityIssueCodes.CustomerDuplicateCandidate,
            "거래처 중복 후보",
            "Warning",
            "거래처",
            "같은 테넌트/담당지점 안에 거래처명이 완전히 동일한 거래처가 여러 개 있습니다.",
            "목록을 확인한 뒤 실제 같은 거래처인 항목만 병합하거나 사용하지 않는 거래처를 정리하세요."),
        [DataIntegrityIssueCodes.ItemDuplicateCandidate] = new(
            DataIntegrityIssueCodes.ItemDuplicateCandidate,
            "품목 중복 후보",
            "Warning",
            "품목",
            "같은 테넌트/소속 안에 품명·규격이 같은 품목이 여러 개 있습니다.",
            "판매·구매·재고 참조를 확인한 뒤 실제 같은 품목만 병합하거나 사용하지 않는 품목을 정리하세요."),
        [DataIntegrityIssueCodes.WarehouseDuplicateCandidate] = new(
            DataIntegrityIssueCodes.WarehouseDuplicateCandidate,
            "창고 중복 후보",
            "Warning",
            "다중창고",
            "같은 담당지점 안에 창고 코드 또는 창고명이 중복된 후보가 있습니다.",
            "창고별 재고를 확인한 뒤 실제 같은 창고만 정리하세요."),
        [DataIntegrityIssueCodes.CustomerContractMissingCustomerReference] = new(
            DataIntegrityIssueCodes.CustomerContractMissingCustomerReference,
            "계약/첨부 거래처 참조 누락",
            "Error",
            "거래처",
            "거래처 계약/첨부 행이 현재 로컬 DB에 존재하지 않는 거래처 ID를 참조합니다.",
            "동기화 진단에서 서버 상태와 휴지통 영구삭제 이력을 확인한 뒤 계약/첨부 잔여 행을 정리하세요."),
        [DataIntegrityIssueCodes.InvoiceAmountMismatch] = new(
            DataIntegrityIssueCodes.InvoiceAmountMismatch,
            "전표 금액 계산 불일치",
            "Warning",
            "판매/구매/회계",
            "전표의 품목 합계, 공급가, 부가세, 합계금액이 현재 계산 기준과 다릅니다.",
            "전표를 열어 부가세 옵션과 품목 금액을 확인한 뒤 저장해 재계산하세요."),
        [DataIntegrityIssueCodes.InvoiceOverSettled] = new(
            DataIntegrityIssueCodes.InvoiceOverSettled,
            "수금/지급 초과",
            "Warning",
            "회계경리",
            "전표 합계금액보다 수금 또는 지급 합계가 큽니다.",
            "수금/지급 내역 중 중복 입력이나 잘못된 금액이 있는지 확인하세요."),
        [DataIntegrityIssueCodes.InvoiceLineMissingInvoiceReference] = new(
            DataIntegrityIssueCodes.InvoiceLineMissingInvoiceReference,
            "전표 세부내역 전표 참조 누락",
            "Error",
            "판매/구매/회계",
            "전표 세부내역 행이 현재 로컬 DB에 존재하지 않는 전표 ID를 참조합니다.",
            "동기화 진단에서 서버 상태와 휴지통 영구삭제 이력을 확인한 뒤 전표 세부내역 잔여 행을 정리하세요."),
        [DataIntegrityIssueCodes.PaymentMissingInvoiceReference] = new(
            DataIntegrityIssueCodes.PaymentMissingInvoiceReference,
            "수금/지급 전표 참조 누락",
            "Error",
            "회계경리",
            "수금/지급 행이 현재 로컬 DB에 존재하지 않는 전표 ID를 참조합니다.",
            "동기화 진단에서 서버 상태와 휴지통 영구삭제 이력을 확인한 뒤 결제 잔여 행을 정리하세요."),
        [DataIntegrityIssueCodes.InvoiceLinkedTransactionPaymentMismatch] = new(
            DataIntegrityIssueCodes.InvoiceLinkedTransactionPaymentMismatch,
            "전표 연결 수금/거래 불일치",
            "Error",
            "회계경리",
            "전표 연결 거래내역과 파생 수금/지급 행의 전표·금액 상태가 다릅니다.",
            "수금/지급 창에서 전표별 수금내역과 거래내역을 비교하고, 동기화 후에도 남으면 백업 기준으로 정리하세요."),
        [DataIntegrityIssueCodes.TransactionOperationalScopeMismatch] = new(
            DataIntegrityIssueCodes.TransactionOperationalScopeMismatch,
            "수금/지급 scope 저장값 불일치",
            "Error",
            "회계경리",
            "수금/지급 거래내역의 tenant·owner·담당지점 저장값이 거래처/연결 전표/렌탈 청구 범위와 맞지 않습니다.",
            "수금/지급 창에서 해당 거래를 다시 저장하거나 동기화 진단/운영점검 후 scope 정리 작업을 수행하세요."),
        [DataIntegrityIssueCodes.RentalDeletedInvoiceActivePayment] = new(
            DataIntegrityIssueCodes.RentalDeletedInvoiceActivePayment,
            "삭제 렌탈 전표 수금 잔여",
            "Error",
            "렌탈 청구",
            "삭제된 렌탈 청구 전표에 활성 수금/지급 행이 남아 있어 정산·미수금 계산을 왜곡할 수 있습니다.",
            "전표와 수금/지급 삭제 상태를 맞춘 뒤 렌탈 청구관리와 동기화 진단에서 정산 상태를 다시 확인하세요."),
        [DataIntegrityIssueCodes.RentalInvoiceDeletedPaymentDetachedTransaction] = new(
            DataIntegrityIssueCodes.RentalInvoiceDeletedPaymentDetachedTransaction,
            "렌탈 전표 복원 불완전",
            "Error",
            "렌탈 청구",
            "활성 렌탈 전표에 삭제 상태 수금/지급과 전표 링크가 끊긴 활성 거래내역이 함께 남아 있습니다.",
            "전표와 수금/지급을 다시 복원하거나 동기화 후 운영점검에서 연결 Payment·거래내역·렌탈 정산 상태를 확인하세요."),
        [DataIntegrityIssueCodes.TransactionAttachmentMissingTransactionReference] = new(
            DataIntegrityIssueCodes.TransactionAttachmentMissingTransactionReference,
            "거래첨부 거래내역 참조 누락",
            "Error",
            "회계경리",
            "거래첨부 행이 현재 로컬 DB에 존재하지 않는 거래내역 ID를 참조합니다.",
            "동기화 진단에서 서버 상태와 휴지통 영구삭제 이력을 확인한 뒤 거래첨부 잔여 행을 정리하세요."),
        [DataIntegrityIssueCodes.MissingAttachmentFiles] = new(
            DataIntegrityIssueCodes.MissingAttachmentFiles,
            "거래첨부 로컬 파일 누락",
            "Error",
            "회계경리",
            "거래첨부 행은 존재하지만 PC 로컬 저장 파일이 없거나 경로가 비어 있어 첨부 열기/동기화 재검증이 필요합니다.",
            "동기화 진단에서 첨부를 다시 내려받거나 원본 파일을 재첨부한 뒤 운영점검을 다시 실행하세요."),
        [DataIntegrityIssueCodes.RentalBillingLogMissingProfileReference] = new(
            DataIntegrityIssueCodes.RentalBillingLogMissingProfileReference,
            "청구로그 청구 프로필 참조 누락",
            "Error",
            "렌탈 청구",
            "렌탈 청구 로그가 현재 로컬 DB에 존재하지 않는 청구 프로필 ID를 참조합니다.",
            "청구관리와 동기화 진단에서 해당 청구월의 프로필/전표/입금 연결을 확인하세요."),
        [DataIntegrityIssueCodes.InventoryTransferLineMissingTransferReference] = new(
            DataIntegrityIssueCodes.InventoryTransferLineMissingTransferReference,
            "재고이동 세부내역 문서 참조 누락",
            "Error",
            "재고",
            "재고이동 세부내역 행이 현재 로컬 DB에 존재하지 않는 재고이동 문서 ID를 참조합니다.",
            "동기화 진단에서 서버 상태와 재고이동 영구삭제 이력을 확인한 뒤 세부내역 잔여 행을 정리하세요."),
        [DataIntegrityIssueCodes.InventoryDeletedItemStockResidue] = new(
            DataIntegrityIssueCodes.InventoryDeletedItemStockResidue,
            "삭제 품목 재고 잔여",
            "Error",
            "재고",
            "삭제된 품목에 현재재고 또는 창고별 재고 스냅샷이 남아 재고·전표 집계를 왜곡할 수 있습니다.",
            "삭제 품목의 현재고를 0으로 정리하고 창고별 재고 행을 제거한 뒤 전체 재동기화/무결성 점검을 다시 실행하세요."),
        [DataIntegrityIssueCodes.InventoryStockSnapshotMismatch] = new(
            DataIntegrityIssueCodes.InventoryStockSnapshotMismatch,
            "품목 재고 스냅샷 불일치",
            "Warning",
            "재고",
            "품목 현재재고와 창고별 재고 합계가 다릅니다.",
            "품목/재고 화면에서 창고별 재고와 수동 조정 이력을 확인한 뒤 재계산 또는 수동 조정하세요."),
        [DataIntegrityIssueCodes.InventoryWarehouseReferenceMissing] = new(
            DataIntegrityIssueCodes.InventoryWarehouseReferenceMissing,
            "삭제/누락 창고 참조",
            "Warning",
            "다중창고",
            "재고 스냅샷 또는 재고 이동 이력이 존재하지 않거나 비활성인 창고 코드를 참조합니다.",
            "창고를 복구하거나 해당 재고/이동 이력의 창고 코드를 올바른 창고로 수정하세요.")
    };

    private readonly LocalDbContext _db;
    private readonly SyncRequestDispatcher? _syncRequestDispatcher;
    private readonly SemaphoreSlim? _ownerScopeDataGate;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly LocalStateService? _localStateService;
    private readonly ErpApiClient? _erpApiClient;
    private readonly SyncService? _syncService;
    private DataIntegrityIssueService? _isolatedOperationOwner;

    public DataIntegrityIssueService(
        LocalDbContext db,
        SyncRequestDispatcher? syncRequestDispatcher = null,
        LocalStateService? localStateService = null,
        IServiceScopeFactory? scopeFactory = null,
        ErpApiClient? erpApiClient = null,
        SyncService? syncService = null)
    {
        _db = db;
        _syncRequestDispatcher = syncRequestDispatcher;
        _ownerScopeDataGate = localStateService?.OwnerScopeDataGate;
        _scopeFactory = scopeFactory;
        _localStateService = localStateService;
        _erpApiClient = erpApiClient;
        _syncService = syncService;
    }

    internal Func<CancellationToken, Task>? TestOnlyBeforeDuplicateMergeSaveAsync { get; set; }
    internal Action<DataIntegrityIssueService>? TestOnlyConfigureIsolatedChild { get; set; }
    internal Func<ItemDuplicateMergePreviewRequestDto, CancellationToken, Task<ItemDuplicateMergePreviewDto?>>? TestOnlyPreviewItemDuplicateMergeAsync { get; set; }
    internal Func<ItemDuplicateMergeRequestDto, CancellationToken, Task<ItemDuplicateMergeResultDto?>>? TestOnlyExecuteItemDuplicateMergeAsync { get; set; }
    internal Func<SessionState, CancellationToken, Task<int>>? TestOnlyCountDirtyAsync { get; set; }
    internal Func<CancellationToken, Task<bool>>? TestOnlyRefreshCurrentBusinessScopeAsync { get; set; }
#if DEBUG
    internal bool TestOnlyUseLegacyLocalItemDuplicateMerge { get; set; }
#endif

    public Task<DataIntegrityScanResult> ScanAsync(SessionState session, CancellationToken ct = default)
        => ExecuteCoordinatedDbContextOperationAsync(
            () => ExecuteUsingIsolatedScopeAsync(
                child => child.ScanCoreAsync(session, ct),
                () => ScanCoreAsync(session, ct)),
            ct);

    private async Task<DataIntegrityScanResult> ScanCoreAsync(SessionState session, CancellationToken ct)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var stepStopwatch = Stopwatch.StartNew();
        var activeProfiles = await SelectIntegrityRentalProfileProjection(ApplyOperationalAlertRentalProfileScopePrefilter(
                _db.RentalBillingProfiles
                    .AsNoTracking()
                    .Where(profile => !profile.IsDeleted && profile.IsActive),
                session))
            .ToListAsync(ct);
        var activeAssets = await SelectIntegrityRentalAssetProjection(ApplyOperationalAlertRentalAssetScopePrefilter(
                _db.RentalAssets
                    .AsNoTracking()
                    .Where(asset => !asset.IsDeleted),
                session))
            .ToListAsync(ct);
        var activeAssignmentHistories = await SelectIntegrityAssignmentHistoryProjection(ApplyOperationalAlertRentalAssignmentHistoryScopePrefilter(
                _db.RentalAssetAssignmentHistories
                    .AsNoTracking()
                    .Where(history => !history.IsDeleted),
                session))
            .ToListAsync(ct);
        var activeCustomers = await SelectIntegrityCustomerProjection(ApplyOperationalAlertCustomerScopePrefilter(
                _db.Customers
                    .AsNoTracking()
                    .Where(customer => !customer.IsDeleted),
                session))
            .ToListAsync(ct);
        var activeItems = await SelectIntegrityItemProjection(ApplyOperationalAlertItemScopePrefilter(
                _db.Items
                    .AsNoTracking()
                    .Where(item => !item.IsDeleted),
                session))
            .ToListAsync(ct);
        var sessionTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            session.TenantCode,
            session.OfficeCode);
        var activeTenantWarehouses = (await SelectIntegrityWarehouseProjection(
                _db.Warehouses
                    .AsNoTracking()
                    .Where(warehouse => !warehouse.IsDeleted && warehouse.IsActive))
            .ToListAsync(ct))
            .Where(warehouse => IsConsistentWarehouseReferenceInTenant(
                warehouse,
                sessionTenantCode))
            .ToList();
        var activeInvoices = await ApplyOperationalAlertInvoiceScopePrefilter(
                _db.Invoices
                    .AsNoTracking()
                    .Where(invoice => !invoice.IsDeleted && invoice.IsLatestVersion),
                session)
            .Select(invoice => new IntegrityInvoiceSnapshot
            {
                Id = invoice.Id,
                TenantCode = invoice.TenantCode,
                OfficeCode = invoice.OfficeCode,
                ResponsibleOfficeCode = invoice.ResponsibleOfficeCode,
                InvoiceNumber = invoice.InvoiceNumber,
                VoucherType = invoice.VoucherType,
                InvoiceDate = invoice.InvoiceDate,
                TotalAmount = invoice.TotalAmount,
                SupplyAmount = invoice.SupplyAmount,
                VatAmount = invoice.VatAmount,
                VatMode = invoice.VatMode
            })
            .ToListAsync(ct);
        LogIntegrityScanStep(
            "Integrity scan source load",
            stepStopwatch,
            $"profiles={activeProfiles.Count:N0}, assets={activeAssets.Count:N0}, histories={activeAssignmentHistories.Count:N0}, customers={activeCustomers.Count:N0}, items={activeItems.Count:N0}, invoices={activeInvoices.Count:N0}");

        stepStopwatch.Restart();
        var scopedProfiles = activeProfiles
            .Where(profile =>
            {
                var profileScope = ResolveProfileScope(profile);
                return IsInSessionScope(profileScope.TenantCode, profileScope.ResponsibleOfficeCode, session);
            })
            .ToList();
        var scopedAssets = activeAssets
            .Where(asset =>
            {
                var assetScope = ResolveAssetScope(asset);
                return IsInSessionScope(assetScope.TenantCode, assetScope.ResponsibleOfficeCode, session);
            })
            .ToList();
        var scopedAssetsByBillingProfileId = scopedAssets
            .Where(asset => asset.BillingProfileId.HasValue && asset.BillingProfileId.Value != Guid.Empty)
            .GroupBy(asset => asset.BillingProfileId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var allAssetsById = activeAssets.ToDictionary(asset => asset.Id);
        var parsedTemplateItemsByProfileId = scopedProfiles
            .ToDictionary(profile => profile.Id, ParseTemplateItems);
        var profilesRequiringGlobalLinkedAssetLookup = scopedProfiles
            .Where(profile =>
                !scopedAssetsByBillingProfileId.ContainsKey(profile.Id) &&
                parsedTemplateItemsByProfileId.TryGetValue(profile.Id, out var parsed) &&
                parsed.Success &&
                !parsed.Items
                    .SelectMany(item => item.IncludedAssetIds ?? [])
                    .Any(assetId => assetId != Guid.Empty))
            .Select(profile => profile.Id)
            .ToList();
        var profileIdsWithActiveAssetsOutsideCurrentScope = await LoadActiveRentalProfileIdsReferencedByAssetsAsync(
            profilesRequiringGlobalLinkedAssetLookup,
            ct);
        var templateReferencedAssetIdsOutsidePrefilter = parsedTemplateItemsByProfileId.Values
            .Where(parsed => parsed.Success)
            .SelectMany(parsed => parsed.Items)
            .SelectMany(item => item.IncludedAssetIds ?? [])
            .Where(id => id != Guid.Empty && !allAssetsById.ContainsKey(id))
            .Distinct()
            .ToList();
        var activeReferencedAssetIdsOutsidePrefilter = await LoadActiveRentalAssetIdsByIdsAsync(
            templateReferencedAssetIdsOutsidePrefilter,
            ct);
        var activeProfilesById = activeProfiles.ToDictionary(profile => profile.Id);
        var linkedCustomerIds = activeProfiles
            .Where(profile => profile.CustomerId.HasValue && profile.CustomerId.Value != Guid.Empty)
            .Select(profile => profile.CustomerId!.Value)
            .Concat(activeAssets
                .Where(asset => asset.CustomerId.HasValue && asset.CustomerId.Value != Guid.Empty)
                .Select(asset => asset.CustomerId!.Value))
            .Concat(activeAssignmentHistories
                .Where(history => history.CustomerId.HasValue && history.CustomerId.Value != Guid.Empty)
                .Select(history => history.CustomerId!.Value))
            .Distinct()
            .ToList();
        var activeCustomersById = await LoadCustomersByIdsAsync(linkedCustomerIds, ct);
        var details = new List<DataIntegrityIssueDetail>();
        var scopedAssignmentHistories = activeAssignmentHistories
            .Where(history =>
            {
                allAssetsById.TryGetValue(history.AssetId, out var historyAsset);
                LocalRentalBillingProfile? historyProfile = null;
                if (history.BillingProfileId.HasValue && history.BillingProfileId.Value != Guid.Empty)
                    activeProfilesById.TryGetValue(history.BillingProfileId.Value, out historyProfile);

                var historyScope = ResolveAssignmentHistoryScope(history, historyAsset, historyProfile);
                return IsInSessionScope(historyScope.TenantCode, historyScope.OfficeCode, session);
            })
            .ToList();
        var scopedCustomers = activeCustomers
            .Where(customer =>
            {
                if (session.HasGlobalDataScope)
                    return true;

                var customerScope = ResolveCustomerScope(customer);
                return IsInSessionScope(customerScope.TenantCode, customerScope.OfficeCode, session);
            })
            .ToList();
        var scopedItems = activeItems
            .Where(item =>
            {
                if (session.HasGlobalDataScope)
                    return true;

                var itemScope = ResolveItemScope(item);
                return IsInSessionScope(itemScope.TenantCode, itemScope.OfficeCode, session);
            })
            .ToList();
        var scopedWarehouses = activeTenantWarehouses
            .Where(warehouse => IsInSessionScope(null, ResolveWarehouseOfficeCode(warehouse), session))
            .ToList();
        var scopedInvoices = activeInvoices
            .Where(invoice =>
            {
                var invoiceScope = ResolveInvoiceScope(invoice);
                return IsInSessionScope(invoiceScope.TenantCode, invoiceScope.OfficeCode, session);
            })
            .ToList();
        LogIntegrityScanStep(
            "Integrity scan scope filter",
            stepStopwatch,
            $"profiles={scopedProfiles.Count:N0}, assets={scopedAssets.Count:N0}, histories={scopedAssignmentHistories.Count:N0}, customers={scopedCustomers.Count:N0}, items={scopedItems.Count:N0}, invoices={scopedInvoices.Count:N0}");

        stepStopwatch.Restart();
        var duplicateCustomerCandidateIds = BuildDuplicateCustomerCandidateIds(scopedCustomers);
        var scopedItemIds = scopedItems
            .Select(item => item.Id)
            .Distinct()
            .ToList();
        var duplicateItemCandidateIds = BuildDuplicateItemCandidateIds(scopedItems);
        var itemWarehouseStocks = await LoadItemWarehouseStocksForItemsAsync(scopedItemIds, ct);
        var inventoryMovements = await LoadInventoryMovementsForItemsAsync(scopedItemIds, ct);
        LogIntegrityScanStep(
            "Integrity scan inventory source load",
            stepStopwatch,
            $"stocks={itemWarehouseStocks.Count:N0}, movements={inventoryMovements.Count:N0}, scopedItems={scopedItemIds.Count:N0}");

        stepStopwatch.Restart();
        var customerDuplicateUsages = await LoadCustomerDuplicateUsagesAsync(duplicateCustomerCandidateIds, ct);
        var itemDuplicateUsages = await LoadItemDuplicateUsagesAsync(duplicateItemCandidateIds, ct);
        var itemDuplicateOutboxCounts = await LoadItemUnresolvedOutboxCountsAsync(duplicateItemCandidateIds, ct);
        LogIntegrityScanStep(
            "Integrity scan duplicate usage load",
            stepStopwatch,
            $"duplicateCustomers={duplicateCustomerCandidateIds.Count:N0}, duplicateItems={duplicateItemCandidateIds.Count:N0}");

        stepStopwatch.Restart();
        var scopedInvoiceIds = scopedInvoices
            .Select(invoice => invoice.Id)
            .Distinct()
            .ToList();
        var invoiceLineTotalsByInvoiceId = await LoadInvoiceLineTotalsForInvoicesAsync(scopedInvoiceIds, ct);
        var invoicePaymentTotalsByInvoiceId = await LoadInvoicePaymentTotalsForInvoicesAsync(scopedInvoiceIds, ct);
        LogIntegrityScanStep(
            "Integrity scan invoice aggregate load",
            stepStopwatch,
            $"lineTotals={invoiceLineTotalsByInvoiceId.Count:N0}, paymentTotals={invoicePaymentTotalsByInvoiceId.Count:N0}, scopedInvoices={scopedInvoiceIds.Count:N0}");

        stepStopwatch.Restart();
        AddMasterDataAndLedgerIssues(
            details,
            scopedCustomers,
            scopedItems,
            scopedWarehouses,
            activeTenantWarehouses,
            scopedInvoices,
            invoiceLineTotalsByInvoiceId,
            invoicePaymentTotalsByInvoiceId,
            customerDuplicateUsages,
            itemDuplicateUsages,
            itemDuplicateOutboxCounts,
            itemWarehouseStocks,
            inventoryMovements,
            session);
        LogIntegrityScanStep("Integrity scan master/ledger issues", stepStopwatch, $"issues={details.Count:N0}");

        stepStopwatch.Restart();
        var rentalBillingRunSettlementMismatchIssues = await LoadRentalBillingRunSettlementMismatchIssuesAsync(scopedProfiles, session, ct);
        details.AddRange(rentalBillingRunSettlementMismatchIssues);
        LogIntegrityScanStep(
            "Integrity scan rental billing run settlement mismatch",
            stepStopwatch,
            $"issues={rentalBillingRunSettlementMismatchIssues.Count:N0}");

        stepStopwatch.Restart();
        var rentalBillingRunMissingRunIdIssues = LoadRentalBillingRunMissingRunIdIssues(scopedProfiles);
        details.AddRange(rentalBillingRunMissingRunIdIssues);
        LogIntegrityScanStep(
            "Integrity scan rental billing run missing run id",
            stepStopwatch,
            $"issues={rentalBillingRunMissingRunIdIssues.Count:N0}");

        stepStopwatch.Restart();
        var malformedRentalBillingRunsJsonIssues = LoadMalformedRentalBillingRunsJsonIssues(scopedProfiles);
        details.AddRange(malformedRentalBillingRunsJsonIssues);
        LogIntegrityScanStep(
            "Integrity scan malformed rental billing runs json",
            stepStopwatch,
            $"issues={malformedRentalBillingRunsJsonIssues.Count:N0}");

        stepStopwatch.Restart();
        var rentalBillingRunKeyConflictIssues = LoadRentalBillingRunKeyConflictingRunIdIssues(scopedProfiles);
        details.AddRange(rentalBillingRunKeyConflictIssues);
        LogIntegrityScanStep(
            "Integrity scan rental billing run key conflicts",
            stepStopwatch,
            $"issues={rentalBillingRunKeyConflictIssues.Count:N0}");

        stepStopwatch.Restart();
        var rentalBillingProfileSummaryMismatchIssues = await LoadRentalBillingProfileSummaryMismatchIssuesAsync(scopedProfiles, session, ct);
        details.AddRange(rentalBillingProfileSummaryMismatchIssues);
        LogIntegrityScanStep(
            "Integrity scan rental billing profile summary mismatch",
            stepStopwatch,
            $"issues={rentalBillingProfileSummaryMismatchIssues.Count:N0}");

        stepStopwatch.Restart();
        var deletedItemStockResidueIssues = await LoadDeletedItemStockResidueIssuesAsync(session, ct);
        details.AddRange(deletedItemStockResidueIssues);
        LogIntegrityScanStep(
            "Integrity scan deleted item stock residues",
            stepStopwatch,
            $"issues={deletedItemStockResidueIssues.Count:N0}");

        stepStopwatch.Restart();
        var invoiceLineMissingInvoiceIssues = await LoadInvoiceLineMissingInvoiceReferenceIssuesAsync(session, ct);
        details.AddRange(invoiceLineMissingInvoiceIssues);
        LogIntegrityScanStep(
            "Integrity scan invoice line missing invoice references",
            stepStopwatch,
            $"issues={invoiceLineMissingInvoiceIssues.Count:N0}");

        stepStopwatch.Restart();
        var paymentMissingInvoiceIssues = await LoadPaymentMissingInvoiceReferenceIssuesAsync(session, ct);
        details.AddRange(paymentMissingInvoiceIssues);
        LogIntegrityScanStep(
            "Integrity scan payment missing invoice references",
            stepStopwatch,
            $"issues={paymentMissingInvoiceIssues.Count:N0}");

        stepStopwatch.Restart();
        var invoiceLinkedTransactionPaymentMismatchIssues = await LoadInvoiceLinkedTransactionPaymentMismatchIssuesAsync(session, ct);
        details.AddRange(invoiceLinkedTransactionPaymentMismatchIssues);
        LogIntegrityScanStep(
            "Integrity scan invoice linked transaction payment mismatch",
            stepStopwatch,
            $"issues={invoiceLinkedTransactionPaymentMismatchIssues.Count:N0}");

        stepStopwatch.Restart();
        var transactionOperationalScopeMismatchIssues = await LoadTransactionOperationalScopeMismatchIssuesAsync(session, ct);
        details.AddRange(transactionOperationalScopeMismatchIssues);
        LogIntegrityScanStep(
            "Integrity scan transaction operational scope mismatch",
            stepStopwatch,
            $"issues={transactionOperationalScopeMismatchIssues.Count:N0}");

        stepStopwatch.Restart();
        var rentalDeletedInvoiceActivePaymentIssues = await LoadRentalDeletedInvoiceActivePaymentIssuesAsync(session, ct);
        details.AddRange(rentalDeletedInvoiceActivePaymentIssues);
        LogIntegrityScanStep(
            "Integrity scan rental deleted invoice active payment",
            stepStopwatch,
            $"issues={rentalDeletedInvoiceActivePaymentIssues.Count:N0}");

        stepStopwatch.Restart();
        var rentalInvoiceDetachedPaymentIssues = await LoadRentalInvoiceDeletedPaymentDetachedTransactionIssuesAsync(session, ct);
        details.AddRange(rentalInvoiceDetachedPaymentIssues);
        LogIntegrityScanStep(
            "Integrity scan rental invoice deleted payment detached transaction",
            stepStopwatch,
            $"issues={rentalInvoiceDetachedPaymentIssues.Count:N0}");

        stepStopwatch.Restart();
        var hardMissingChildReferenceIssues = await LoadHardMissingChildReferenceIssuesAsync(session, ct);
        details.AddRange(hardMissingChildReferenceIssues);
        LogIntegrityScanStep(
            "Integrity scan hard-missing child references",
            stepStopwatch,
            $"issues={hardMissingChildReferenceIssues.Count:N0}");

        stepStopwatch.Restart();
        var missingAttachmentFileIssues = await LoadMissingAttachmentFileIssuesAsync(session, ct);
        details.AddRange(missingAttachmentFileIssues);
        LogIntegrityScanStep(
            "Integrity scan missing attachment files",
            stepStopwatch,
            $"issues={missingAttachmentFileIssues.Count:N0}");

        stepStopwatch.Restart();
        foreach (var history in scopedAssignmentHistories)
        {
            allAssetsById.TryGetValue(history.AssetId, out var historyAsset);
            LocalRentalBillingProfile? historyProfile = null;
            if (history.BillingProfileId.HasValue && history.BillingProfileId.Value != Guid.Empty)
                activeProfilesById.TryGetValue(history.BillingProfileId.Value, out historyProfile);

            var missingCriticalReferences = new List<string>();
            var staleHistoricalReferences = new List<string>();
            if (history.AssetId == Guid.Empty || historyAsset is null)
            {
                if (history.IsCurrent)
                    missingCriticalReferences.Add($"자산 {FormatNullableGuid(history.AssetId)}");
                else
                    staleHistoricalReferences.Add($"자산 {FormatNullableGuid(history.AssetId)}");
            }
            if (history.CustomerId.HasValue && history.CustomerId.Value != Guid.Empty && !activeCustomersById.ContainsKey(history.CustomerId.Value))
            {
                if (history.IsCurrent)
                    missingCriticalReferences.Add($"거래처 {history.CustomerId.Value:D}");
                else
                    staleHistoricalReferences.Add($"거래처 {history.CustomerId.Value:D}");
            }
            if (history.BillingProfileId.HasValue && history.BillingProfileId.Value != Guid.Empty && historyProfile is null)
            {
                if (history.IsCurrent)
                    missingCriticalReferences.Add($"청구 프로필 {history.BillingProfileId.Value:D}");
                else
                    staleHistoricalReferences.Add($"청구 프로필 {history.BillingProfileId.Value:D}");
            }

            if (missingCriticalReferences.Count > 0)
            {
                AddHistoryIssue(details, DataIntegrityIssueCodes.RentalAssignmentMissingReference, history, historyAsset, historyProfile,
                    currentValue: string.Join(" / ", missingCriticalReferences),
                    expectedValue: "현재 자산·거래처·청구 프로필 참조",
                    message: $"{BuildHistoryDisplay(history)} 현재 임대이력이 누락/삭제된 참조를 포함합니다.");
            }
            else if (staleHistoricalReferences.Count > 0)
            {
                AddHistoryIssue(details, DataIntegrityIssueCodes.RentalAssignmentHistoricalStaleReference, history, historyAsset, historyProfile,
                    currentValue: string.Join(" / ", staleHistoricalReferences),
                    expectedValue: "과거 이력 스냅샷 표시값 보존",
                    message: $"{BuildHistoryDisplay(history)} 과거 임대이력의 마스터 참조가 현재 DB에서 사라졌지만 표시값은 보존되어 있습니다.");
            }
        }

        foreach (var group in scopedAssignmentHistories
                     .Where(history => history.IsCurrent)
                     .GroupBy(history => history.AssetId)
                     .Where(group => group.Key != Guid.Empty))
        {
            var currentHistories = group
                .OrderByDescending(history => history.LinkedAtUtc)
                .ToList();
            if (currentHistories.Count <= 1)
                continue;

            allAssetsById.TryGetValue(group.Key, out var asset);
            var profile = currentHistories
                .Select(history => history.BillingProfileId)
                .Where(id => id.HasValue && id.Value != Guid.Empty)
                .Select(id => activeProfilesById.TryGetValue(id!.Value, out var found) ? found : null)
                .FirstOrDefault(found => found is not null);
            var currentDisplays = currentHistories
                .Take(5)
                .Select(BuildHistoryDisplay)
                .ToList();
            var representativeHistory = currentHistories[0];

            AddHistoryIssue(details, DataIntegrityIssueCodes.RentalAssetMultipleCurrentAssignments, representativeHistory, asset, profile,
                currentValue: $"{currentHistories.Count:N0}건 / {string.Join(" / ", currentDisplays)}",
                expectedValue: "현재 임대이력 1건",
                message: $"{FormatNullableGuid(group.Key)} 자산에 현재 임대이력이 {currentHistories.Count:N0}건 있습니다.");
        }

        var assetTemplateRefs = new Dictionary<Guid, List<AssetTemplateReference>>();

        foreach (var profile in scopedProfiles)
        {
            if (IsProfileScopeInconsistent(profile))
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalOperationalScopeMismatch, profile, null,
                    entityType: "청구 프로필",
                    entityId: profile.Id,
                    currentValue: BuildStoredProfileScopeDisplay(profile),
                    expectedValue: BuildProfileScopeDisplay(profile),
                    message: $"{BuildProfileDisplay(profile)} 프로필의 tenant/owner/담당지점 범위가 내부적으로 섞여 있습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
            }

            if (TryGetLinkedCustomerNameMismatch(
                    profile.CustomerId,
                    activeCustomersById,
                    new[] { profile.CustomerName },
                    out var profileMasterCustomerName,
                    out var profileStoredCustomerName))
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalCustomerNameMismatch, profile, null,
                    entityType: "청구 프로필",
                    entityId: profile.Id,
                    currentValue: profileStoredCustomerName,
                    expectedValue: profileMasterCustomerName,
                    message: $"{BuildProfileDisplay(profile)} 프로필의 거래처 표시명이 거래처 마스터명과 다릅니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
            }

            var parsed = parsedTemplateItemsByProfileId[profile.Id];
            if (!parsed.Success)
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalBillingTemplateInvalid, profile, null,
                    entityType: "청구 프로필",
                    entityId: profile.Id,
                    currentValue: "템플릿 해석 실패",
                    expectedValue: "정상 JSON",
                    message: $"{BuildProfileDisplay(profile)} 청구 품목 데이터를 해석할 수 없습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
                continue;
            }

            var templateItems = parsed.Items;
            var profileAssetIds = new HashSet<Guid>();
            var linkedAssets = scopedAssetsByBillingProfileId.GetValueOrDefault(profile.Id) ?? [];

            if (templateItems.Count == 0)
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalProfileTemplateEmpty, profile, null,
                    entityType: "청구 프로필",
                    entityId: profile.Id,
                    currentValue: "0개",
                    expectedValue: "1개 이상",
                    message: $"{BuildProfileDisplay(profile)} 청구서 표시 품목이 없습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
            }

            var templateMonthly = templateItems.Sum(ResolveTemplateMonthlyAmount);
            if (templateItems.Count > 0 && AmountDiffers(profile.MonthlyAmount, templateMonthly))
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalProfileMonthlyAmountMismatch, profile, null,
                    entityType: "청구 프로필",
                    entityId: profile.Id,
                    currentValue: FormatMoney(profile.MonthlyAmount),
                    expectedValue: FormatMoney(templateMonthly),
                    message: $"{BuildProfileDisplay(profile)} 월 기준금액이 품목 합계와 다릅니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
            }

            foreach (var item in templateItems)
            {
                var itemAssetIds = item.IncludedAssetIds?
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList() ?? new List<Guid>();

                if (itemAssetIds.Count == 0)
                {
                    AddIssue(details, DataIntegrityIssueCodes.RentalTemplateItemWithoutAsset, profile, null,
                        entityType: "청구 품목",
                        entityId: profile.Id,
                        itemName: item.DisplayItemName,
                        currentValue: "연결 자산 0개",
                        expectedValue: "연결 자산 1개 이상",
                        message: $"{BuildProfileDisplay(profile)} / {NormalizeDisplay(item.DisplayItemName, "품목")} 품목에 연결 자산이 없습니다.",
                        directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
                    continue;
                }

                var existingItemAssets = new List<LocalRentalAsset>();
                foreach (var assetId in itemAssetIds)
                {
                    profileAssetIds.Add(assetId);
                    if (!allAssetsById.TryGetValue(assetId, out var asset))
                    {
                        if (activeReferencedAssetIdsOutsidePrefilter.Contains(assetId))
                            continue;

                        AddIssue(details, DataIntegrityIssueCodes.RentalTemplateMissingAsset, profile, null,
                            entityType: "청구 품목",
                            entityId: profile.Id,
                            itemName: item.DisplayItemName,
                            currentValue: assetId.ToString("D"),
                            expectedValue: "활성 렌탈 자산",
                            message: $"{BuildProfileDisplay(profile)} / {NormalizeDisplay(item.DisplayItemName, "품목")} 품목이 누락 자산을 참조합니다.",
                            directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
                        continue;
                    }

                    existingItemAssets.Add(asset);
                    if (!assetTemplateRefs.TryGetValue(asset.Id, out var refs))
                    {
                        refs = new List<AssetTemplateReference>();
                        assetTemplateRefs[asset.Id] = refs;
                    }

                    refs.Add(new AssetTemplateReference(
                        profile.Id,
                        BuildProfileDisplay(profile),
                        item.DisplayItemName,
                        ResolveTemplateMonthlyAmount(item),
                        itemAssetIds.Count));

                    var hasConflictingProfile = asset.BillingProfileId.HasValue &&
                                                asset.BillingProfileId.Value != Guid.Empty &&
                                                asset.BillingProfileId.Value != profile.Id;
                    if (hasConflictingProfile)
                    {
                        AddIssue(details, DataIntegrityIssueCodes.RentalAssetProfileScopeMismatch, profile, asset,
                            entityType: "자산 연결",
                            entityId: asset.Id,
                            itemName: item.DisplayItemName,
                            currentValue: BuildAssetScopeDisplay(asset),
                            expectedValue: BuildProfileScopeDisplay(profile),
                            message: $"{BuildAssetDisplay(asset)} 자산의 청구 연결/범위가 {BuildProfileDisplay(profile)} 프로필과 다릅니다.",
                            directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
                    }
                }

                var assetMonthlySum = existingItemAssets.Sum(asset => Math.Max(0m, asset.MonthlyFee));
                var itemMonthly = ResolveTemplateMonthlyAmount(item);
                if (existingItemAssets.Count > 0 && assetMonthlySum > 0m && AmountDiffers(assetMonthlySum, itemMonthly))
                {
                    AddIssue(details, DataIntegrityIssueCodes.RentalAssetTemplateMonthlyMismatch, profile, existingItemAssets.FirstOrDefault(),
                        entityType: "청구 품목",
                        entityId: profile.Id,
                        itemName: item.DisplayItemName,
                        currentValue: FormatMoney(itemMonthly),
                        expectedValue: FormatMoney(assetMonthlySum),
                        message: $"{BuildProfileDisplay(profile)} / {NormalizeDisplay(item.DisplayItemName, "품목")} 품목 금액이 연결 자산 월요금 합계와 다릅니다.",
                        directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
                }
            }

            if (profileAssetIds.Count > 0)
            {
                foreach (var linkedAsset in linkedAssets.Where(asset => !profileAssetIds.Contains(asset.Id)))
                {
                    AddIssue(details, DataIntegrityIssueCodes.RentalAssetMissingProfileTemplateReference, profile, linkedAsset,
                        entityType: "자산-표시품목 연결",
                        entityId: linkedAsset.Id,
                        currentValue: $"BillingProfileId {profile.Id:D}",
                        expectedValue: "청구서 표시 품목 IncludedAssetIds",
                        message: $"{BuildAssetDisplay(linkedAsset)} 자산은 {BuildProfileDisplay(profile)} 프로필에 연결되어 있지만 청구서 표시 품목에는 포함되어 있지 않습니다.",
                        directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
                }
            }

            if (linkedAssets.Count == 0 && profileAssetIds.Count == 0)
            {
                if (profileIdsWithActiveAssetsOutsideCurrentScope.Contains(profile.Id))
                {
                    AddIssue(details, DataIntegrityIssueCodes.RentalProfileLinkedAssetsOutsideCurrentScope, profile, null,
                        entityType: "청구 프로필",
                        entityId: profile.Id,
                        currentValue: "현재 범위 연결 자산 0개 / 범위 밖 연결 존재",
                        expectedValue: "권한 범위별 연결 상태 확인",
                        message: $"{BuildProfileDisplay(profile)} 프로필은 현재 범위 밖의 활성 자산에서 참조되고 있습니다.",
                        directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile,
                        reviewInfo: "현재 세션 범위 연결 0개 / DB 전체 활성 자산 연결 존재 / 자산 식별정보 비표시");
                }
                else
                {
                    AddIssue(details, DataIntegrityIssueCodes.RentalProfileWithoutLinkedAssets, profile, null,
                        entityType: "청구 프로필",
                        entityId: profile.Id,
                        currentValue: "연결 자산 0개",
                        expectedValue: "연결 자산 1개 이상",
                        message: $"{BuildProfileDisplay(profile)} 프로필에 연결된 자산이 없습니다.",
                        directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
                }
            }
        }

        foreach (var group in assetTemplateRefs)
        {
            if (!allAssetsById.TryGetValue(group.Key, out var asset))
                continue;
            var assetScope = ResolveAssetScope(asset);
            if (!IsInSessionScope(assetScope.TenantCode, assetScope.ResponsibleOfficeCode, session))
                continue;

            var distinctProfilesById = new Dictionary<Guid, string>();
            foreach (var reference in group.Value)
                distinctProfilesById.TryAdd(reference.ProfileId, reference.ProfileDisplayName);
            if (distinctProfilesById.Count <= 1)
                continue;

            var distinctProfiles = distinctProfilesById.Values.ToList();
            AddIssue(details, DataIntegrityIssueCodes.RentalAssetInMultipleProfileTemplates, null, asset,
                entityType: "렌탈 자산",
                entityId: asset.Id,
                currentValue: string.Join(" / ", distinctProfiles.Take(3)),
                expectedValue: "청구 프로필 1개",
                message: $"{BuildAssetDisplay(asset)} 자산이 여러 청구 프로필 품목에 포함되어 있습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenRentalAsset);
        }

        foreach (var asset in scopedAssets)
        {
            if (IsAssetScopeInconsistent(asset))
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalOperationalScopeMismatch, null, asset,
                    entityType: "렌탈 자산",
                    entityId: asset.Id,
                    currentValue: BuildStoredAssetScopeDisplay(asset),
                    expectedValue: BuildAssetScopeDisplay(asset),
                    message: $"{BuildAssetDisplay(asset)} 자산의 tenant/owner/담당지점 범위가 내부적으로 섞여 있습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalAsset);
            }

            if (TryGetLinkedCustomerNameMismatch(
                    asset.CustomerId,
                    activeCustomersById,
                    new[] { asset.CustomerName, asset.CurrentCustomerName },
                    out var assetMasterCustomerName,
                    out var assetStoredCustomerName))
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalCustomerNameMismatch, null, asset,
                    entityType: "렌탈 자산",
                    entityId: asset.Id,
                    currentValue: assetStoredCustomerName,
                    expectedValue: assetMasterCustomerName,
                    message: $"{BuildAssetDisplay(asset)} 자산의 거래처 표시명이 거래처 마스터명과 다릅니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalAsset);
            }

            if (asset.BillingProfileId.HasValue && !activeProfilesById.ContainsKey(asset.BillingProfileId.Value))
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalAssetMissingBillingProfile, null, asset,
                    entityType: "렌탈 자산",
                    entityId: asset.Id,
                    currentValue: asset.BillingProfileId.Value.ToString("D"),
                    expectedValue: "활성 청구 프로필",
                    message: $"{BuildAssetDisplay(asset)} 자산에 저장된 청구 프로필을 찾을 수 없습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalAsset);
            }

            var hasValidLinkedProfile = asset.BillingProfileId.HasValue &&
                                        asset.BillingProfileId.Value != Guid.Empty &&
                                        activeProfilesById.ContainsKey(asset.BillingProfileId.Value);
            var billingEligibility = ClassifyOperatingAssetBillingEligibility(asset, hasValidLinkedProfile);
            var billingReviewInfo = BuildAssetBillingReviewInfo(
                asset,
                assetTemplateRefs.GetValueOrDefault(asset.Id) ?? []);
            if (billingEligibility == RentalAssetBillingEligibility.Billable && asset.MonthlyFee <= 0m)
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee, null, asset,
                    entityType: "렌탈 자산",
                    entityId: asset.Id,
                    currentValue: FormatMoney(asset.MonthlyFee),
                    expectedValue: "1원 이상",
                    message: $"{BuildAssetDisplay(asset)} 자산은 청구대상이나 월요금이 없습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalAsset,
                    reviewInfo: billingReviewInfo);
            }
            else if (billingEligibility == RentalAssetBillingEligibility.NeedsReview)
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalAssetBillingEligibilityUnconfirmed, null, asset,
                    entityType: "렌탈 자산",
                    entityId: asset.Id,
                    currentValue: NormalizeDisplay(asset.BillingEligibilityStatus, "공백"),
                    expectedValue: "청구대상 또는 청구제외",
                    message: $"{BuildAssetDisplay(asset)} 자산의 청구상태를 확인해야 합니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalAsset,
                    reviewInfo: billingReviewInfo);
            }
        }

        LogIntegrityScanStep("Integrity scan rental issues", stepStopwatch, $"issues={details.Count:N0}");

        stepStopwatch.Restart();
        details = details
            .Where(issue => IsIssueInSessionScope(issue, session))
            .ToList();
        LogIntegrityScanStep("Integrity scan final account scope filter", stepStopwatch, $"issues={details.Count:N0}");

        var summaries = details
            .GroupBy(issue => issue.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var definition = GetDefinition(group.Key);
                return new DataIntegrityIssueSummary
                {
                    Code = definition.Code,
                    Title = definition.Title,
                    Severity = definition.Severity,
                    Area = definition.Area,
                    Description = definition.Description,
                    SuggestedAction = definition.SuggestedAction,
                    Count = group.Count(),
                    HasDirectAction = group.Any(issue => issue.HasDirectAction)
                };
            })
            .OrderByDescending(summary => DataIntegritySeverityFormatter.GetSortWeight(summary.Severity))
            .ThenByDescending(summary => summary.Count)
            .ThenBy(summary => summary.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var result = new DataIntegrityScanResult(DateTime.Now, summaries, details);
        OperationTiming.LogIfSlow(
            "INTEGRITY",
            "Integrity scan total",
            totalStopwatch.Elapsed,
            $"issues={result.TotalIssueCount:N0}, types={result.Summaries.Count:N0}",
            infoThreshold: TimeSpan.FromMilliseconds(700),
            warningThreshold: TimeSpan.FromSeconds(3));
        return result;
    }

    private async Task<T> ExecuteCoordinatedDbContextOperationAsync<T>(
        Func<Task<T>> operation,
        CancellationToken ct)
    {
        var enteredOwnerScopeGate = false;
        try
        {
            if (_ownerScopeDataGate is not null)
            {
                await _ownerScopeDataGate.WaitAsync(ct);
                enteredOwnerScopeGate = true;
            }

            return await SyncService.ExecuteWithGlobalSyncOperationLockAsync(operation, ct);
        }
        finally
        {
            if (enteredOwnerScopeGate)
                _ownerScopeDataGate!.Release();
        }
    }

    private async Task<T> ExecuteUsingIsolatedScopeAsync<T>(
        Func<DataIntegrityIssueService, Task<T>> isolatedOperation,
        Func<Task<T>> fallbackOperation)
    {
        if (_scopeFactory is null || _isolatedOperationOwner is not null)
            return await fallbackOperation();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var child = scope.ServiceProvider.GetRequiredService<DataIntegrityIssueService>();
        if (ReferenceEquals(child, this))
            return await fallbackOperation();

        child._isolatedOperationOwner = this;
        try
        {
            TestOnlyConfigureIsolatedChild?.Invoke(child);
            return await isolatedOperation(child);
        }
        finally
        {
            child._isolatedOperationOwner = null;
        }
    }

    private async Task<OfficeMutationResult> ExecuteDuplicateMergeTransactionAsync(
        Func<Task<OfficeMutationResult>> operation,
        CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is not null)
            return OfficeMutationResult.Denied("다른 로컬 데이터 작업이 진행 중이어서 중복 병합을 시작할 수 없습니다. 잠시 후 다시 시도하세요.");

        if (_db.ChangeTracker.HasChanges())
            return OfficeMutationResult.Denied("아직 저장되지 않은 로컬 변경이 있어 중복 병합을 중단했습니다. 열려 있는 편집 화면을 저장하거나 취소한 뒤 다시 시도하세요.");

        // A tracking query can otherwise reuse stale Unchanged entities left by a previous screen.
        // There are no pending changes at this point, so detaching them is safe and forces a fresh read.
        _db.ChangeTracker.Clear();

        await using var transaction = await _db.BeginRuntimeMutationTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var result = await operation();
            if (!result.Success)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                _db.ChangeTracker.Clear();
                return result;
            }

            await transaction.CommitAsync(ct);
            _db.ChangeTracker.Clear();
            return result;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            finally
            {
                _db.ChangeTracker.Clear();
            }

            throw;
        }
    }

    private async Task<OfficeMutationResult> ExecuteDuplicateMutationWithIsolationAsync(
        Func<DataIntegrityIssueService, Task<OfficeMutationResult>> isolatedOperation,
        Func<Task<OfficeMutationResult>> fallbackOperation)
    {
        if (_db.ChangeTracker.HasChanges())
        {
            return OfficeMutationResult.Denied(
                "아직 저장되지 않은 로컬 변경이 있어 중복 병합을 중단했습니다. 열려 있는 편집 화면을 저장하거나 취소한 뒤 다시 시도하세요.");
        }

        var result = await ExecuteUsingIsolatedScopeAsync(isolatedOperation, fallbackOperation);
        if (!result.Success)
            return result;

        // A successful child-scope commit can make Unchanged entities cached by the long-lived UI
        // context stale. Detach only Unchanged entries so a later query reads the committed rows,
        // while any concurrently staged Added/Modified/Deleted work remains intact.
        foreach (var entry in _db.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Unchanged).ToList())
            entry.State = EntityState.Detached;

        return result;
    }

    public async Task<OfficeMutationResult> MergeDuplicateIssueAsync(
        DataIntegrityIssueDetail issue,
        SessionState session,
        CancellationToken ct = default)
    {
        var result = await ExecuteCoordinatedDbContextOperationAsync(
            () => ExecuteDuplicateMutationWithIsolationAsync(
                child => child.MergeDuplicateIssueCoreAsync(issue, session, ct),
                () => MergeDuplicateIssueCoreAsync(issue, session, ct)),
            ct);
        if (result.Success)
            _syncRequestDispatcher?.RequestFlushSync();
        return result;
    }

    private async Task<OfficeMutationResult> MergeDuplicateIssueCoreAsync(
        DataIntegrityIssueDetail issue,
        SessionState session,
        CancellationToken ct)
    {
        if (issue.RelatedEntityIds.Count <= 1)
            return OfficeMutationResult.Missing("병합할 중복 후보가 2건 이상 필요합니다. 운영점검을 새로고침한 뒤 다시 시도하세요.");

        if (string.Equals(issue.Code, DataIntegrityIssueCodes.CustomerDuplicateCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteDuplicateMergeTransactionAsync(
                () => MergeDuplicateCustomersAsync(issue.RelatedEntityIds, session, ct),
                ct);
        }

        if (string.Equals(issue.Code, DataIntegrityIssueCodes.ItemDuplicateCandidate, StringComparison.OrdinalIgnoreCase))
            return OfficeMutationResult.Denied("품목 중복 병합은 비교 화면에서 대표 품목과 최신 스냅샷을 명시해야 합니다.");

        return OfficeMutationResult.Denied("현재 항목은 자동 병합 대상이 아닙니다. 원본 화면에서 확인 후 수동 정리하세요.");
    }

    public async Task<OfficeMutationResult> MergeDuplicateItemIssueAsync(
        DataIntegrityIssueDetail issue,
        Guid canonicalItemId,
        string expectedSnapshotToken,
        SessionState session,
        CancellationToken ct = default)
    {
#if DEBUG
        if (TestOnlyUseLegacyLocalItemDuplicateMerge)
        {
            var legacyResult = await ExecuteCoordinatedDbContextOperationAsync(
                () => ExecuteDuplicateMutationWithIsolationAsync(
                    child => child.ExecuteDuplicateMergeTransactionAsync(
                            () => child.MergeDuplicateItemIssueLocallyForTestsAsync(
                                issue,
                                canonicalItemId,
                                expectedSnapshotToken,
                                session,
                                ct),
                            ct),
                    () => ExecuteDuplicateMergeTransactionAsync(
                            () => MergeDuplicateItemIssueLocallyForTestsAsync(
                                issue,
                                canonicalItemId,
                                expectedSnapshotToken,
                                session,
                                ct),
                            ct)),
                ct);
            if (legacyResult.Success)
                _syncRequestDispatcher?.RequestFlushSync();
            return legacyResult;
        }
#endif

        return await ExecuteCoordinatedDbContextOperationAsync(
            () => ExecuteServerAuthoritativeItemDuplicateMergeAndRefreshInsideLocksAsync(
                issue,
                canonicalItemId,
                expectedSnapshotToken,
                session,
                ct),
            ct);
    }

    private async Task<OfficeMutationResult> ExecuteServerAuthoritativeItemDuplicateMergeAndRefreshInsideLocksAsync(
        DataIntegrityIssueDetail issue,
        Guid canonicalItemId,
        string expectedSnapshotToken,
        SessionState session,
        CancellationToken ct)
    {
        await using var runtimeMutationLease =
            await LocalDbContext.AcquireRuntimeMutationGateAsync(ct);

        var outcome = await ExecuteServerAuthoritativeItemDuplicateMergeWithIsolationAsync(
            issue,
            canonicalItemId,
            expectedSnapshotToken,
            session,
            ct);
        if (outcome.CommitState == ItemDuplicateServerCommitState.NotStarted)
            return outcome.Result;

        bool refreshed;
        var localMergeStateVerificationFailed = false;
        var refreshToken = ct.IsCancellationRequested ? CancellationToken.None : ct;
        try
        {
            await using (LocalDbContext.EnterRuntimeMutationGateOwnerScope(runtimeMutationLease))
            {
                refreshed = TestOnlyRefreshCurrentBusinessScopeAsync is not null
                    ? await TestOnlyRefreshCurrentBusinessScopeAsync(refreshToken)
                    : _syncService is not null &&
                      await _syncService.RefreshCurrentBusinessScopeFromServerInsideGlobalOperationAsync(refreshToken);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("INTEGRITY", "서버 품목 중복 병합 후 로컬 캐시 새로고침 실패", ex);
            refreshed = false;
        }

        if (refreshed && outcome.CommitState == ItemDuplicateServerCommitState.Committed)
        {
            try
            {
                localMergeStateVerificationFailed =
                    !await HasExpectedAuthoritativeItemMergeStateAsync(
                        canonicalItemId,
                        outcome.TombstonedItemIds ?? [],
                        refreshToken);
                if (localMergeStateVerificationFailed)
                {
                    AppLogger.Warn(
                        "INTEGRITY",
                        "Server item duplicate merge refresh returned success without the complete clean canonical/tombstone state in the local cache.");
                    refreshed = false;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "INTEGRITY",
                    "서버 품목 중복 병합 후 로컬 대표 품목/tombstone 상태 검증 실패",
                    ex);
                localMergeStateVerificationFailed = true;
                refreshed = false;
            }
        }

        if (refreshed)
        {
            try
            {
                refreshed = await TryReloadOwnerContextAfterAuthoritativeRefreshAsync(refreshToken);
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "INTEGRITY",
                    "서버 품목 중복 병합 후 공유 화면 컨텍스트 다시 읽기 실패",
                    ex);
                refreshed = false;
            }
        }

        _db.AdvanceRuntimeMutationEpoch();
        if (refreshed)
        {
            _db.AcceptCurrentRuntimeMutationEpoch();
        }
        else if (_db.ChangeTracker.HasChanges())
        {
            AppLogger.Warn(
                "INTEGRITY",
                "Server item duplicate merge completed while the owner context gained pending local changes; the context remains stale and must be recreated.");
            refreshed = false;
        }

        if (!refreshed)
        {
            return outcome.CommitState == ItemDuplicateServerCommitState.Unknown
                ? OfficeMutationResult.Denied(
                    "서버 병합 요청의 완료 여부를 확인할 수 없고 이 PC 캐시 새로고침도 실패했습니다. 병합을 다시 실행하지 말고 먼저 동기화를 실행한 뒤 운영점검을 다시 확인하세요.")
                : OfficeMutationResult.Denied(
                    localMergeStateVerificationFailed
                        ? "서버 병합은 완료됐지만 이 PC 캐시에 대표 품목과 중복 tombstone이 모두 반영됐는지 확인할 수 없습니다. 병합을 다시 실행하지 말고 동기화를 다시 실행하세요."
                        : "서버 병합은 완료됐지만 이 PC 캐시 새로고침에 실패했습니다. 병합을 다시 실행하지 말고 동기화를 다시 실행하세요.");
        }

        if (outcome.CommitState == ItemDuplicateServerCommitState.Unknown)
        {
            return OfficeMutationResult.Denied(
                "서버 병합 요청의 응답은 확인하지 못했지만 이 PC 캐시는 서버 기준으로 새로고쳤습니다. 병합을 바로 다시 실행하지 말고 운영점검을 새로고쳐 후보가 남아 있는지 먼저 확인하세요.");
        }

        return outcome.Result;
    }

    private async Task<bool> TryReloadOwnerContextAfterAuthoritativeRefreshAsync(
        CancellationToken ct)
    {
        _db.ChangeTracker.DetectChanges();
        if (_db.ChangeTracker.HasChanges())
            return false;

        var trackedEntries = _db.ChangeTracker.Entries().ToList();
        foreach (var entry in trackedEntries)
        {
            _db.ChangeTracker.DetectChanges();
            if (_db.ChangeTracker.HasChanges())
                return false;
            if (entry.State == EntityState.Unchanged)
                await entry.ReloadAsync(ct);
        }

        _db.ChangeTracker.DetectChanges();
        return !_db.ChangeTracker.HasChanges();
    }

    private async Task<bool> HasExpectedAuthoritativeItemMergeStateAsync(
        Guid canonicalItemId,
        IReadOnlyCollection<Guid> tombstonedItemIds,
        CancellationToken ct)
    {
        var expectedTombstoneIds = tombstonedItemIds
            .Where(id => id != Guid.Empty && id != canonicalItemId)
            .Distinct()
            .ToList();
        if (canonicalItemId == Guid.Empty ||
            expectedTombstoneIds.Count == 0 ||
            expectedTombstoneIds.Count != tombstonedItemIds.Count)
        {
            return false;
        }

        var expectedItemIds = expectedTombstoneIds
            .Append(canonicalItemId)
            .ToList();
        var localStates = await _db.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => expectedItemIds.Contains(item.Id))
            .Select(item => new
            {
                item.Id,
                item.IsDeleted,
                item.IsDirty
            })
            .ToListAsync(ct);
        if (localStates.Count != expectedItemIds.Count)
            return false;

        var canonicalState = localStates.SingleOrDefault(item => item.Id == canonicalItemId);
        if (canonicalState is null || canonicalState.IsDeleted || canonicalState.IsDirty)
            return false;

        var localTombstoneIds = localStates
            .Where(item => item.IsDeleted && !item.IsDirty)
            .Select(item => item.Id)
            .OrderBy(id => id)
            .ToList();
        return localTombstoneIds.SequenceEqual(expectedTombstoneIds.OrderBy(id => id));
    }

    private enum ItemDuplicateServerCommitState
    {
        NotStarted,
        Committed,
        Unknown
    }

    private sealed record ItemDuplicateServerMergeOutcome(
        OfficeMutationResult Result,
        ItemDuplicateServerCommitState CommitState,
        IReadOnlyList<Guid>? TombstonedItemIds = null);

    private async Task<ItemDuplicateServerMergeOutcome> ExecuteServerAuthoritativeItemDuplicateMergeWithIsolationAsync(
        DataIntegrityIssueDetail issue,
        Guid canonicalItemId,
        string expectedSnapshotToken,
        SessionState session,
        CancellationToken ct)
    {
        if (_db.ChangeTracker.HasChanges())
        {
            return new ItemDuplicateServerMergeOutcome(
                OfficeMutationResult.Denied(
                    "아직 저장되지 않은 로컬 변경이 있어 품목 병합을 중단했습니다. 열려 있는 편집 화면을 저장하거나 취소한 뒤 다시 시도하세요."),
                ItemDuplicateServerCommitState.NotStarted);
        }

        return await ExecuteUsingIsolatedScopeAsync(
            child => child.ExecuteServerAuthoritativeItemDuplicateMergeCoreAsync(
                issue,
                canonicalItemId,
                expectedSnapshotToken,
                session,
                ct),
            () => ExecuteServerAuthoritativeItemDuplicateMergeCoreAsync(
                issue,
                canonicalItemId,
                expectedSnapshotToken,
                session,
                ct));
    }

    private async Task<ItemDuplicateServerMergeOutcome> ExecuteServerAuthoritativeItemDuplicateMergeCoreAsync(
        DataIntegrityIssueDetail issue,
        Guid canonicalItemId,
        string expectedSnapshotToken,
        SessionState session,
        CancellationToken ct)
    {
        ItemDuplicateServerMergeOutcome Block(OfficeMutationResult result)
            => new(result, ItemDuplicateServerCommitState.NotStarted);

        if (!string.Equals(issue.Code, DataIntegrityIssueCodes.ItemDuplicateCandidate, StringComparison.OrdinalIgnoreCase))
            return Block(OfficeMutationResult.Denied("품목 중복 경고만 이 병합 API를 사용할 수 있습니다."));
        if (!session.IsLoggedIn)
            return Block(OfficeMutationResult.Denied("로그인된 온라인 세션에서만 품목 중복 병합을 실행할 수 있습니다."));
        if (session.IsOfflineMode)
            return Block(OfficeMutationResult.Denied("오프라인 상태에서는 품목 중복 병합을 실행할 수 없습니다. 먼저 서버 동기화를 완료하세요."));
        if (_erpApiClient is null &&
            (TestOnlyPreviewItemDuplicateMergeAsync is null || TestOnlyExecuteItemDuplicateMergeAsync is null))
        {
            return Block(OfficeMutationResult.Denied("서버 품목 병합 서비스를 사용할 수 없습니다. 앱을 다시 시작한 뒤 시도하세요."));
        }
        if (_syncService is null && TestOnlyRefreshCurrentBusinessScopeAsync is null)
            return Block(OfficeMutationResult.Denied("서버 병합 후 캐시 새로고침 서비스를 사용할 수 없습니다. 앱을 다시 시작한 뒤 시도하세요."));
        if (_db.Database.CurrentTransaction is not null || _db.ChangeTracker.HasChanges())
        {
            return Block(OfficeMutationResult.Denied(
                "아직 저장되지 않은 로컬 변경이 있어 품목 병합을 중단했습니다. 열려 있는 편집 화면을 저장하거나 취소한 뒤 다시 시도하세요."));
        }

        var expectedIds = issue.RelatedEntityIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (expectedIds.Count <= 1)
            return Block(OfficeMutationResult.Missing("병합할 품목 후보가 2건 이상 필요합니다."));
        if (canonicalItemId == Guid.Empty || !expectedIds.Contains(canonicalItemId))
            return Block(OfficeMutationResult.Denied("선택한 대표 품목이 비교 대상 후보에 포함되어 있지 않습니다."));
        if (string.IsNullOrWhiteSpace(expectedSnapshotToken))
            return Block(OfficeMutationResult.Denied("품목 비교 스냅샷이 없어 병합을 중단했습니다. 다시 조회하세요."));

        var preparation = await PrepareItemDuplicateReviewCoreAsync(issue, session, ct);
        if (!preparation.CanMerge)
        {
            var reason = string.IsNullOrWhiteSpace(preparation.BlockingReasonText)
                ? "로컬 안전 조건 또는 권한을 충족하지 않았습니다."
                : preparation.BlockingReasonText;
            return Block(OfficeMutationResult.Denied($"품목 병합을 실행할 수 없습니다: {reason}"));
        }

        var latestLocalToken = preparation.Comparison.SnapshotToken?.Trim().ToLowerInvariant() ?? string.Empty;
        var requestedLocalToken = expectedSnapshotToken.Trim().ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(latestLocalToken),
                Encoding.UTF8.GetBytes(requestedLocalToken)))
        {
            return Block(OfficeMutationResult.Conflict(
                "품목 또는 참조 상태가 변경되어 로컬 비교 스냅샷이 만료되었습니다. 다시 조회하세요."));
        }

        var currentScopeDirtyCount = await CountCurrentScopeDirtyForItemMergeAsync(session, ct);
        if (!currentScopeDirtyCount.HasValue)
            return Block(OfficeMutationResult.Denied("로컬 미동기화 변경 여부를 확인할 수 없어 품목 병합을 중단했습니다."));

        if (currentScopeDirtyCount.Value > 0)
        {
            return Block(OfficeMutationResult.Denied(
                $"현재 업체 DB에 미동기화 변경 {currentScopeDirtyCount.Value:N0}건이 남아 있어 품목 병합을 중단했습니다. 먼저 동기화를 완료하세요."));
        }

        ItemDuplicateMergePreviewDto? preview;
        try
        {
            var previewRequest = new ItemDuplicateMergePreviewRequestDto
            {
                CandidateItemIds = expectedIds,
                CanonicalItemId = canonicalItemId
            };
            preview = TestOnlyPreviewItemDuplicateMergeAsync is not null
                ? await TestOnlyPreviewItemDuplicateMergeAsync(previewRequest, ct)
                : await _erpApiClient!.PreviewItemDuplicateMergeAsync(previewRequest, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return Block(CreateServerItemDuplicateMergeFailure(ex, "사전검증"));
        }

        if (preview is null)
            return Block(OfficeMutationResult.Denied("서버 품목 병합 사전검증 응답이 비어 있어 병합을 중단했습니다."));

        var previewCandidates = preview.Candidates ?? [];
        var previewIds = previewCandidates
            .Select(candidate => candidate.ItemId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (!previewIds.SequenceEqual(expectedIds) || previewCandidates.Count != expectedIds.Count)
        {
            return Block(OfficeMutationResult.Conflict(
                "서버의 중복 후보 구성이 이 PC의 비교 목록과 달라 병합을 중단했습니다. 동기화 후 다시 조회하세요."));
        }
        if (preview.CanonicalItemId != canonicalItemId)
            return Block(OfficeMutationResult.Conflict("서버가 확인한 대표 품목이 선택값과 달라 병합을 중단했습니다."));
        if (!preview.CanMerge)
        {
            var serverReasons = (preview.BlockingReasons ?? [])
                .Where(reason => !string.IsNullOrWhiteSpace(reason))
                .Select(reason => reason.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var reasonText = serverReasons.Count == 0
                ? "서버 안전 조건을 충족하지 않았습니다."
                : string.Join(" / ", serverReasons);
            return Block(OfficeMutationResult.Denied($"서버가 품목 병합을 차단했습니다: {reasonText}"));
        }
        if (string.IsNullOrWhiteSpace(preview.ServerSnapshotToken))
            return Block(OfficeMutationResult.Denied("서버 병합 스냅샷이 비어 있어 병합을 중단했습니다."));

        var dirtyCountAfterPreview = await CountCurrentScopeDirtyForItemMergeAsync(session, ct);
        if (!dirtyCountAfterPreview.HasValue)
            return Block(OfficeMutationResult.Denied("서버 사전검증 후 로컬 미동기화 변경 여부를 다시 확인할 수 없어 품목 병합을 중단했습니다."));
        if (dirtyCountAfterPreview.Value > 0)
        {
            return Block(OfficeMutationResult.Denied(
                $"서버 사전검증 중 미동기화 변경 {dirtyCountAfterPreview.Value:N0}건이 생겨 품목 병합을 중단했습니다. 먼저 동기화를 완료하세요."));
        }

        var activeLocalEditorReason = BuildActiveLocalItemEditorBlockingReason(expectedIds);
        if (activeLocalEditorReason is not null)
            return Block(OfficeMutationResult.Denied(activeLocalEditorReason));
        if (_db.ChangeTracker.HasChanges())
        {
            return Block(OfficeMutationResult.Denied(
                "서버 사전검증 중 저장되지 않은 로컬 변경이 생겨 품목 병합을 중단했습니다."));
        }

        ItemDuplicateMergeResultDto? result;
        var executeRequest = new ItemDuplicateMergeRequestDto
        {
            CandidateItemIds = expectedIds,
            CanonicalItemId = canonicalItemId,
            ExpectedServerSnapshotToken = preview.ServerSnapshotToken,
            MutationId = $"item-duplicate-merge:{Guid.NewGuid():N}"
        };
        try
        {
            result = TestOnlyExecuteItemDuplicateMergeAsync is not null
                ? await TestOnlyExecuteItemDuplicateMergeAsync(executeRequest, ct)
                : await _erpApiClient!.ExecuteItemDuplicateMergeAsync(executeRequest, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ItemDuplicateServerMergeOutcome(
                OfficeMutationResult.Denied(
                    "서버 품목 병합 실행 중 요청이 취소되어 완료 여부를 확인할 수 없습니다."),
                ItemDuplicateServerCommitState.Unknown);
        }
        catch (HttpRequestException ex) when (IsDefinitiveRejectedItemDuplicateMergeStatus(ex.StatusCode))
        {
            return Block(CreateServerItemDuplicateMergeFailure(ex, "실행"));
        }
        catch (HttpRequestException ex)
        {
            AppLogger.Warn(
                "INTEGRITY",
                $"서버 품목 중복 병합 응답 확인 실패. mutationId={executeRequest.MutationId}, status={(int?)ex.StatusCode}");
            return new ItemDuplicateServerMergeOutcome(
                OfficeMutationResult.Denied(
                    "서버 품목 병합 요청을 보냈지만 응답을 확인하지 못해 완료 여부를 확정할 수 없습니다."),
                ItemDuplicateServerCommitState.Unknown);
        }

        if (result is null)
        {
            return new ItemDuplicateServerMergeOutcome(
                OfficeMutationResult.Denied(
                    "서버 품목 병합 결과가 비어 있어 완료 여부를 확인할 수 없습니다."),
                ItemDuplicateServerCommitState.Unknown);
        }

        var expectedTombstonedIds = expectedIds.Where(id => id != canonicalItemId).OrderBy(id => id).ToList();
        var tombstonedItemIds = result.TombstonedItemIds ?? [];
        var actualTombstonedIds = tombstonedItemIds.Distinct().OrderBy(id => id).ToList();
        if (result.CanonicalItemId != canonicalItemId ||
            tombstonedItemIds.Count != actualTombstonedIds.Count ||
            !actualTombstonedIds.SequenceEqual(expectedTombstonedIds) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(result.ServerSnapshotToken?.Trim().ToLowerInvariant() ?? string.Empty),
                Encoding.UTF8.GetBytes(preview.ServerSnapshotToken.Trim().ToLowerInvariant())))
        {
            return new ItemDuplicateServerMergeOutcome(
                OfficeMutationResult.Denied(
                    "서버 병합의 2xx 응답 구조가 요청과 달라 실제 완료 여부를 확정할 수 없습니다."),
                ItemDuplicateServerCommitState.Unknown);
        }

        var message = result.IsReplay
            ? $"서버에서 이전에 완료된 동일 요청을 확인했습니다. 중복 {tombstonedItemIds.Count:N0}건의 병합 결과를 재생했으며," +
              " 재생 receipt에는 이동 건수가 보존되지 않으므로 서버 기준 새로고침 상태를 확인했습니다."
            : $"서버에서 대표 품목을 확정하고 중복 {tombstonedItemIds.Count:N0}건을 원자적으로 병합했습니다." +
              $" 전표 {result.MovedInvoiceLineCount:N0}건, 렌탈 자산 {result.MovedRentalAssetCount:N0}건," +
              $" 렌탈 청구 {result.UpdatedRentalBillingProfileCount:N0}건, 재고이동 {result.MovedInventoryTransferLineCount:N0}건을 함께 정리했습니다.";
        return new ItemDuplicateServerMergeOutcome(
            OfficeMutationResult.Ok(canonicalItemId, message),
            ItemDuplicateServerCommitState.Committed,
            actualTombstonedIds);
    }

    private async Task<int?> CountCurrentScopeDirtyForItemMergeAsync(
        SessionState session,
        CancellationToken ct)
    {
        if (TestOnlyCountDirtyAsync is not null)
            return await TestOnlyCountDirtyAsync(session, ct);
        if (_localStateService is not null)
            return await _localStateService.CountDirtyAsync(session, ct);
        return null;
    }

    private static bool IsDefinitiveRejectedItemDuplicateMergeStatus(System.Net.HttpStatusCode? statusCode)
        => statusCode is
            System.Net.HttpStatusCode.BadRequest or
            System.Net.HttpStatusCode.Forbidden or
            System.Net.HttpStatusCode.NotFound or
            System.Net.HttpStatusCode.Conflict or
            System.Net.HttpStatusCode.UpgradeRequired;

    private static OfficeMutationResult CreateServerItemDuplicateMergeFailure(
        HttpRequestException exception,
        string phase)
    {
        return exception.StatusCode switch
        {
            System.Net.HttpStatusCode.Conflict => OfficeMutationResult.Conflict(
                $"서버 품목 병합 {phase} 중 데이터가 변경되었습니다. 동기화 후 비교 화면을 다시 여세요."),
            System.Net.HttpStatusCode.Forbidden => OfficeMutationResult.Denied(
                $"서버 품목 병합 {phase} 권한 또는 데이터 범위가 부족합니다."),
            System.Net.HttpStatusCode.BadRequest => OfficeMutationResult.Denied(
                $"서버가 품목 병합 {phase} 요청을 안전 조건 불충족으로 거절했습니다. 비교 화면을 다시 확인하세요."),
            System.Net.HttpStatusCode.NotFound => OfficeMutationResult.Missing(
                $"서버 품목 병합 {phase} 대상을 찾을 수 없습니다. 동기화 후 다시 조회하세요."),
            System.Net.HttpStatusCode.UpgradeRequired => OfficeMutationResult.Denied(
                "필수 PC 업데이트가 필요해 서버 품목 병합을 중단했습니다."),
            _ => OfficeMutationResult.Denied(
                $"서버 품목 병합 {phase}에 실패했습니다. 네트워크와 동기화 상태를 확인한 뒤 다시 시도하세요.")
        };
    }

    public Task<DataIntegrityItemDuplicateReviewPreparation> PrepareItemDuplicateReviewAsync(
        DataIntegrityIssueDetail issue,
        SessionState session,
        CancellationToken ct = default)
        => ExecuteCoordinatedDbContextOperationAsync(
            () => ExecuteUsingIsolatedScopeAsync(
                child => child.PrepareItemDuplicateReviewCoreAsync(issue, session, ct),
                () => PrepareItemDuplicateReviewCoreAsync(issue, session, ct)),
            ct);

    private async Task<DataIntegrityItemDuplicateReviewPreparation> PrepareItemDuplicateReviewCoreAsync(
        DataIntegrityIssueDetail issue,
        SessionState session,
        CancellationToken ct)
    {
        var fallbackComparison = issue.ItemDuplicateComparison ?? new DataIntegrityItemDuplicateComparison();
        DataIntegrityItemDuplicateReviewPreparation Block(string reason, DataIntegrityItemDuplicateComparison? comparison = null)
            => new()
            {
                Comparison = comparison ?? fallbackComparison,
                PermissionBlockingReasons = [reason]
            };

        if (!string.Equals(issue.Code, DataIntegrityIssueCodes.ItemDuplicateCandidate, StringComparison.OrdinalIgnoreCase))
            return Block("품목 중복 경고만 후보 비교를 준비할 수 있습니다.");

        var expectedIds = issue.RelatedEntityIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (expectedIds.Count <= 1)
            return Block("비교할 활성 품목 후보가 2건 이상 필요합니다. 운영점검을 새로고침하세요.");

        var expectedItems = await _db.Items.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => expectedIds.Contains(item.Id) && !item.IsDeleted)
            .ToListAsync(ct);
        if (expectedItems.Count != expectedIds.Count || !BelongsToSingleExactItemDuplicateGroup(expectedItems))
            return Block("품목 후보가 삭제되었거나 이름·규격·범위가 변경되었습니다. 운영점검을 새로고침하세요.");

        var seed = expectedItems[0];
        var seedKey = BuildItemExactDuplicateKey(seed);
        var seedName = NormalizeExactDuplicateText(seed.NameOriginal);
        var seedSpecification = NormalizeExactDuplicateText(seed.SpecificationOriginal);
        var currentGroup = (await _db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => !item.IsDeleted &&
                               item.NameOriginal.Trim() == seedName &&
                               item.SpecificationOriginal.Trim() == seedSpecification)
                .ToListAsync(ct))
            .Where(item => string.Equals(BuildItemExactDuplicateKey(item), seedKey, StringComparison.Ordinal))
            .OrderBy(item => item.Id)
            .ToList();
        var currentIds = currentGroup.Select(item => item.Id).OrderBy(id => id).ToList();
        var usageById = await LoadItemDuplicateUsagesAsync(currentIds, ct);
        var outboxCountById = await LoadItemUnresolvedOutboxCountsAsync(currentIds, ct);
        var comparison = BuildItemDuplicateComparison(currentGroup, usageById, outboxCountById);
        if (!currentIds.SequenceEqual(expectedIds))
            return Block("중복 후보 구성이 변경되었습니다. 최신 후보를 확인하도록 운영점검을 새로고침하세요.", comparison);

        var activeLocalEditorReason = BuildActiveLocalItemEditorBlockingReason(currentIds);
        if (activeLocalEditorReason is not null)
            return Block(activeLocalEditorReason, comparison);

        var permissionReasons = new List<string>();
        var requiresOnlineServerMerge = true;
#if DEBUG
        requiresOnlineServerMerge = !TestOnlyUseLegacyLocalItemDuplicateMerge;
#endif
        if (requiresOnlineServerMerge && (!session.IsLoggedIn || session.IsOfflineMode))
        {
            permissionReasons.Add(
                "품목 중복 병합은 온라인 서버에서 원자적으로 처리됩니다. 먼저 온라인 로그인과 동기화를 완료하세요.");
        }
        else if (!CanEditItemsForIntegrity(session))
        {
            permissionReasons.Add("품목 편집 권한이 없어 병합할 수 없습니다. 후보는 읽기 전용으로 비교할 수 있습니다.");
        }
        else
        {
            var deniedItem = currentGroup.FirstOrDefault(item => !CanWriteItemScopeForIntegrity(session, item));
            if (deniedItem is not null)
            {
                permissionReasons.Add(
                    $"'{NormalizeDisplay(deniedItem.NameOriginal, deniedItem.Id.ToString("N"))}' 품목의 업체·사업장 범위에 쓰기 권한이 없어 병합할 수 없습니다.");
            }
            else
            {
                // The canonical item is not selected yet. Checking the full group is intentionally conservative:
                // it prevents the UI from offering a merge that could later need an unauthorized reference move.
                var sideEffectPermissionResult = await ValidateItemDuplicateMergeSideEffectPermissionsAsync(currentIds, session, ct);
                if (sideEffectPermissionResult is not null)
                    permissionReasons.Add(sideEffectPermissionResult.Message);
            }
        }

        return new DataIntegrityItemDuplicateReviewPreparation
        {
            Comparison = comparison,
            PermissionBlockingReasons = permissionReasons
        };
    }

    private async Task<OfficeMutationResult> MergeDuplicateItemIssueLocallyForTestsAsync(
        DataIntegrityIssueDetail issue,
        Guid canonicalItemId,
        string expectedSnapshotToken,
        SessionState session,
        CancellationToken ct)
    {
        if (!string.Equals(issue.Code, DataIntegrityIssueCodes.ItemDuplicateCandidate, StringComparison.OrdinalIgnoreCase))
            return OfficeMutationResult.Denied("품목 중복 경고만 이 병합 API를 사용할 수 있습니다.");
        if (!CanEditItemsForIntegrity(session))
            return OfficeMutationResult.Denied("권한이 없어 품목 중복 후보를 병합할 수 없습니다.");

        var expectedIds = issue.RelatedEntityIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (expectedIds.Count <= 1)
            return OfficeMutationResult.Missing("병합할 품목 후보가 부족합니다.");
        if (canonicalItemId == Guid.Empty || !expectedIds.Contains(canonicalItemId))
            return OfficeMutationResult.Denied("선택한 대표 품목이 비교 대상 후보에 포함되어 있지 않습니다.");
        if (string.IsNullOrWhiteSpace(expectedSnapshotToken))
            return OfficeMutationResult.Denied("품목 비교 스냅샷이 없어 병합을 중단했습니다. 다시 조회하세요.");

        var expectedItems = await _db.Items.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => expectedIds.Contains(item.Id) && !item.IsDeleted)
            .ToListAsync(ct);
        if (expectedItems.Count == 0)
            return OfficeMutationResult.Missing("활성 품목 후보를 찾을 수 없습니다. 다시 조회하세요.");
        if (!BelongsToSingleExactItemDuplicateGroup(expectedItems))
            return OfficeMutationResult.Denied("품명/규격/scope가 하나의 중복 그룹이 아니어서 병합할 수 없습니다.");

        var seed = expectedItems[0];
        var seedKey = BuildItemExactDuplicateKey(seed);
        var seedName = NormalizeExactDuplicateText(seed.NameOriginal);
        var seedSpecification = NormalizeExactDuplicateText(seed.SpecificationOriginal);
        var currentGroup = (await _db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => !item.IsDeleted &&
                               item.NameOriginal.Trim() == seedName &&
                               item.SpecificationOriginal.Trim() == seedSpecification)
                .ToListAsync(ct))
            .Where(item => string.Equals(BuildItemExactDuplicateKey(item), seedKey, StringComparison.Ordinal))
            .OrderBy(item => item.Id)
            .ToList();
        var currentIds = currentGroup.Select(item => item.Id).OrderBy(id => id).ToList();
        if (!currentIds.SequenceEqual(expectedIds))
            return OfficeMutationResult.Denied("중복 후보 구성이 변경되어 병합을 중단했습니다. 다시 조회하세요.");
        if (!currentIds.Contains(canonicalItemId))
            return OfficeMutationResult.Denied("선택한 대표 품목이 최신 후보 그룹에 포함되어 있지 않습니다.");

        var activeLocalEditorReason = BuildActiveLocalItemEditorBlockingReason(currentIds);
        if (activeLocalEditorReason is not null)
            return OfficeMutationResult.Denied(activeLocalEditorReason);

        var usageById = await LoadItemDuplicateUsagesAsync(currentIds, ct);
        var outboxCountById = await LoadItemUnresolvedOutboxCountsAsync(currentIds, ct);
        var comparison = BuildItemDuplicateComparison(currentGroup, usageById, outboxCountById);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(comparison.SnapshotToken),
                Encoding.UTF8.GetBytes(expectedSnapshotToken.Trim().ToLowerInvariant())))
        {
            return OfficeMutationResult.Denied("품목 또는 참조 상태가 변경되어 스냅샷이 만료되었습니다. 다시 조회하세요.");
        }

        if (!comparison.CanMerge)
            return OfficeMutationResult.Denied($"안전 조건을 충족하지 않아 병합할 수 없습니다: {comparison.BlockingReasonText}");

        return await MergeDuplicateItemsAsync(currentIds, canonicalItemId, session, ct);
    }

    private async Task<OfficeMutationResult> MergeDuplicateCustomersAsync(
        IReadOnlyCollection<Guid> relatedEntityIds,
        SessionState session,
        CancellationToken ct)
    {
        if (!CanEditCustomersForIntegrity(session))
            return OfficeMutationResult.Denied("권한이 없어 거래처 중복 후보를 병합할 수 없습니다.");

        var ids = relatedEntityIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count <= 1)
            return OfficeMutationResult.Missing("병합할 거래처 후보가 부족합니다.");

        var customers = await _db.Customers.IgnoreQueryFilters()
            .Where(customer => ids.Contains(customer.Id))
            .ToListAsync(ct);
        var activeCustomers = customers
            .Where(customer => !customer.IsDeleted)
            .ToList();
        if (activeCustomers.Count <= 1)
            return OfficeMutationResult.Missing("활성 거래처 중복 후보가 2건 이상 남아 있지 않습니다. 운영점검을 새로고침하세요.");

        if (!BelongsToSingleExactCustomerDuplicateGroup(activeCustomers))
            return OfficeMutationResult.Denied("거래처명이 완전히 동일한 후보만 병합할 수 있습니다. 사업자번호만 같거나 이름이 다른 거래처는 병합 대상이 아닙니다.");

        foreach (var customer in activeCustomers)
        {
            var customerScopeEvidence = ResolveStoredScopeEvidence(
                customer.TenantCode,
                customer.ResponsibleOfficeCode,
                customer.OfficeCode);
            if (!CanWriteStoredScopeEvidenceForIntegrity(session, customerScopeEvidence))
                return OfficeMutationResult.Denied($"권한이 없어 {NormalizeDisplay(customer.NameOriginal, customer.Id.ToString("N"))} 거래처를 병합할 수 없습니다.");
        }

        var usageById = await LoadCustomerDuplicateUsagesAsync(activeCustomers.Select(customer => customer.Id).ToList(), ct);
        var canonical = activeCustomers
            .OrderByDescending(customer => usageById.TryGetValue(customer.Id, out var usage) ? usage.TotalCount : 0)
            .ThenByDescending(CountFilledCustomerValues)
            .ThenBy(customer => customer.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(customer => customer.Id)
            .First();
        var duplicateIds = activeCustomers
            .Where(customer => customer.Id != canonical.Id)
            .Select(customer => customer.Id)
            .ToList();
        var sideEffectPermissionResult = await ValidateCustomerDuplicateMergeSideEffectPermissionsAsync(duplicateIds, session, ct);
        if (sideEffectPermissionResult is not null)
            return sideEffectPermissionResult;

        var duplicates = activeCustomers
            .Where(customer => duplicateIds.Contains(customer.Id))
            .ToList();
        var now = DateTime.UtcNow;

        foreach (var duplicate in duplicates)
        {
            MergeCustomerValues(canonical, duplicate);
            duplicate.IsDeleted = true;
            MarkDirty(duplicate, now);
        }

        MarkDirty(canonical, now);

        var invoices = await _db.Invoices.IgnoreQueryFilters()
            .Where(invoice => duplicateIds.Contains(invoice.CustomerId))
            .ToListAsync(ct);
        foreach (var invoice in invoices)
        {
            invoice.CustomerId = canonical.Id;
            MarkDirty(invoice, now);
        }

        var transactions = await _db.Transactions.IgnoreQueryFilters()
            .Where(transaction => duplicateIds.Contains(transaction.CustomerId))
            .ToListAsync(ct);
        foreach (var transaction in transactions)
        {
            transaction.CustomerId = canonical.Id;
            MarkDirty(transaction, now);
        }

        var contracts = await _db.CustomerContracts.IgnoreQueryFilters()
            .Where(contract => duplicateIds.Contains(contract.CustomerId))
            .ToListAsync(ct);
        foreach (var contract in contracts)
        {
            contract.CustomerId = canonical.Id;
            MarkDirty(contract, now);
        }

        var profiles = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .Where(profile => profile.CustomerId.HasValue && duplicateIds.Contains(profile.CustomerId.Value))
            .ToListAsync(ct);
        foreach (var profile in profiles)
        {
            var previousCustomerName = profile.CustomerName;
            profile.CustomerId = canonical.Id;
            if (ShouldReplaceDisplayName(previousCustomerName, duplicates.Select(duplicate => duplicate.NameOriginal)))
                profile.CustomerName = canonical.NameOriginal;
            MarkDirty(profile, now);
        }

        var assets = await _db.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.CustomerId.HasValue && duplicateIds.Contains(asset.CustomerId.Value))
            .ToListAsync(ct);
        foreach (var asset in assets)
        {
            var previousCustomerName = asset.CustomerName;
            var previousCurrentCustomerName = asset.CurrentCustomerName;
            asset.CustomerId = canonical.Id;
            if (ShouldReplaceDisplayName(previousCustomerName, duplicates.Select(duplicate => duplicate.NameOriginal)))
                asset.CustomerName = canonical.NameOriginal;
            if (ShouldReplaceDisplayName(previousCurrentCustomerName, duplicates.Select(duplicate => duplicate.NameOriginal)))
                asset.CurrentCustomerName = canonical.NameOriginal;
            MarkDirty(asset, now);
        }

        var histories = await _db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
            .Where(history => history.CustomerId.HasValue && duplicateIds.Contains(history.CustomerId.Value))
            .ToListAsync(ct);
        foreach (var history in histories)
        {
            var previousCustomerName = history.CustomerName;
            history.CustomerId = canonical.Id;
            if (ShouldReplaceDisplayName(previousCustomerName, duplicates.Select(duplicate => duplicate.NameOriginal)))
                history.CustomerName = canonical.NameOriginal;
            MarkDirty(history, now);
        }

        AddDuplicateMergeAudit(
            "Customer",
            canonical.Id,
            session,
            now,
            new
            {
                CanonicalId = canonical.Id,
                DuplicateIds = duplicateIds,
                CanonicalName = canonical.NameOriginal
            },
            new
            {
                MovedInvoices = invoices.Count,
                MovedTransactions = transactions.Count,
                MovedContracts = contracts.Count,
                MovedRentalProfiles = profiles.Count,
                MovedRentalAssets = assets.Count,
                MovedRentalHistories = histories.Count
            });

        if (TestOnlyBeforeDuplicateMergeSaveAsync is not null)
            await TestOnlyBeforeDuplicateMergeSaveAsync(ct);

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(
            canonical.Id,
            $"거래처 중복 후보 {activeCustomers.Count:N0}건 중 {duplicates.Count:N0}건을 '{NormalizeDisplay(canonical.NameOriginal, "대표 거래처")}'로 병합했습니다.");
    }

    private async Task<OfficeMutationResult?> ValidateCustomerDuplicateMergeSideEffectPermissionsAsync(
        IReadOnlyCollection<Guid> duplicateIds,
        SessionState session,
        CancellationToken ct)
    {
        if (duplicateIds.Count == 0)
            return null;

        var referencedCustomerMasterIds = await _db.Customers.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(customer =>
                duplicateIds.Contains(customer.Id) &&
                customer.CustomerMasterId.HasValue &&
                customer.CustomerMasterId.Value != Guid.Empty)
            .Select(customer => customer.CustomerMasterId!.Value)
            .Distinct()
            .ToListAsync(ct);
        if (referencedCustomerMasterIds.Count > 0)
        {
            var customerMasters = await _db.CustomerMasters.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(master => referencedCustomerMasterIds.Contains(master.Id))
                .ToListAsync(ct);
            if (customerMasters.Count != referencedCustomerMasterIds.Count ||
                customerMasters.Any(master => master.IsDeleted))
            {
                return OfficeMutationResult.Denied("삭제되었거나 찾을 수 없는 거래처 마스터가 연결된 거래처는 병합할 수 없습니다.");
            }

            foreach (var master in customerMasters)
            {
                var scopeEvidence = ResolveStoredScopeEvidence(
                    master.TenantCode,
                    responsibleOfficeCode: null,
                    ownerOfficeCode: master.OfficeCode);
                if (!CanWriteStoredScopeEvidenceForIntegrity(session, scopeEvidence))
                    return OfficeMutationResult.Denied("권한 범위 밖 거래처 마스터가 연결된 거래처는 병합할 수 없습니다.");
            }
        }

        var invoices = await _db.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice => duplicateIds.Contains(invoice.CustomerId))
            .ToListAsync(ct);
        if (invoices.Count > 0)
        {
            if (!HasIntegrityPermission(session, AppPermissionNames.InvoiceEdit))
                return OfficeMutationResult.Denied("거래처 병합으로 연결 전표를 함께 변경하려면 전표 편집 권한이 필요합니다.");

            foreach (var invoice in invoices)
            {
                var scopeEvidence = ResolveStoredScopeEvidence(
                    invoice.TenantCode,
                    invoice.ResponsibleOfficeCode,
                    invoice.OfficeCode);
                if (!CanWriteStoredScopeEvidenceForIntegrity(session, scopeEvidence))
                    return OfficeMutationResult.Denied("권한 범위 밖 전표가 연결된 거래처는 병합할 수 없습니다.");
            }
        }

        var transactions = await _db.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transaction => duplicateIds.Contains(transaction.CustomerId))
            .ToListAsync(ct);
        if (transactions.Count > 0)
        {
            if (!HasIntegrityPermission(session, AppPermissionNames.PaymentEdit))
                return OfficeMutationResult.Denied("거래처 병합으로 수금/지급 거래내역을 함께 변경하려면 수금/지급 편집 권한이 필요합니다.");

            foreach (var transaction in transactions)
            {
                var scopeEvidence = ResolveStoredScopeEvidence(
                    transaction.TenantCode,
                    transaction.ResponsibleOfficeCode,
                    transaction.OfficeCode);
                if (!CanWriteStoredScopeEvidenceForIntegrity(session, scopeEvidence))
                    return OfficeMutationResult.Denied("권한 범위 밖 수금/지급 거래내역이 연결된 거래처는 병합할 수 없습니다.");
            }
        }

        var profiles = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => profile.CustomerId.HasValue && duplicateIds.Contains(profile.CustomerId.Value))
            .ToListAsync(ct);
        if (profiles.Count > 0)
        {
            if (!HasIntegrityPermission(session, AppPermissionNames.RentalProfileEdit))
                return OfficeMutationResult.Denied("거래처 병합으로 렌탈 청구 프로필을 함께 변경하려면 렌탈 청구 편집 권한이 필요합니다.");

            foreach (var profile in profiles)
            {
                var scopeEvidence = ResolveStoredScopeEvidence(
                    profile.TenantCode,
                    profile.ResponsibleOfficeCode,
                    profile.OfficeCode,
                    profile.ManagementCompanyCode);
                if (!CanWriteStoredScopeEvidenceForIntegrity(session, scopeEvidence))
                    return OfficeMutationResult.Denied("권한 범위 밖 렌탈 청구 프로필이 연결된 거래처는 병합할 수 없습니다.");
            }
        }

        var assets = await _db.RentalAssets.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => asset.CustomerId.HasValue && duplicateIds.Contains(asset.CustomerId.Value))
            .ToListAsync(ct);
        if (assets.Count > 0)
        {
            if (!HasIntegrityPermission(session, AppPermissionNames.RentalAssetEdit))
                return OfficeMutationResult.Denied("거래처 병합으로 렌탈 자산을 함께 변경하려면 렌탈 자산 편집 권한이 필요합니다.");

            foreach (var asset in assets)
            {
                var scopeEvidence = ResolveStoredScopeEvidence(
                    asset.TenantCode,
                    asset.ResponsibleOfficeCode,
                    asset.OfficeCode,
                    asset.ManagementCompanyCode);
                if (!CanWriteStoredScopeEvidenceForIntegrity(session, scopeEvidence))
                    return OfficeMutationResult.Denied("권한 범위 밖 렌탈 자산이 연결된 거래처는 병합할 수 없습니다.");
            }
        }

        var histories = await _db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(history => history.CustomerId.HasValue && duplicateIds.Contains(history.CustomerId.Value))
            .ToListAsync(ct);
        if (histories.Count > 0)
        {
            if (!HasIntegrityPermission(session, AppPermissionNames.RentalAssetEdit))
                return OfficeMutationResult.Denied("거래처 병합으로 렌탈 설치 이력을 함께 변경하려면 렌탈 자산 편집 권한이 필요합니다.");

            foreach (var history in histories)
            {
                var scopeEvidence = ResolveStoredScopeEvidence(
                    history.TenantCode,
                    history.ResponsibleOfficeCode,
                    ownerOfficeCode: null);
                if (!CanWriteStoredScopeEvidenceForIntegrity(session, scopeEvidence))
                    return OfficeMutationResult.Denied("권한 범위 밖 렌탈 설치 이력이 연결된 거래처는 병합할 수 없습니다.");
            }
        }

        return null;
    }

    private async Task<OfficeMutationResult> MergeDuplicateItemsAsync(
        IReadOnlyCollection<Guid> relatedEntityIds,
        Guid canonicalItemId,
        SessionState session,
        CancellationToken ct)
    {
        if (!CanEditItemsForIntegrity(session))
            return OfficeMutationResult.Denied("권한이 없어 품목 중복 후보를 병합할 수 없습니다.");

        var ids = relatedEntityIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count <= 1)
            return OfficeMutationResult.Missing("병합할 품목 후보가 부족합니다.");

        var activeLocalEditorReason = BuildActiveLocalItemEditorBlockingReason(ids);
        if (activeLocalEditorReason is not null)
            return OfficeMutationResult.Denied(activeLocalEditorReason);

        var items = await _db.Items.IgnoreQueryFilters()
            .Where(item => ids.Contains(item.Id))
            .ToListAsync(ct);
        var activeItems = items
            .Where(item => !item.IsDeleted)
            .ToList();
        if (activeItems.Count <= 1)
            return OfficeMutationResult.Missing("활성 품목 중복 후보가 2건 이상 남아 있지 않습니다. 운영점검을 새로고침하세요.");

        if (!BelongsToSingleExactItemDuplicateGroup(activeItems))
            return OfficeMutationResult.Denied("품목명과 규격이 모두 완전히 동일한 후보만 병합할 수 있습니다. 품목명만 같거나 규격이 다른 품목은 병합 대상이 아닙니다.");

        foreach (var item in activeItems)
        {
            if (!CanWriteItemScopeForIntegrity(session, item))
                return OfficeMutationResult.Denied($"권한이 없어 {NormalizeDisplay(item.NameOriginal, item.Id.ToString("N"))} 품목을 병합할 수 없습니다.");
        }

        var canonical = activeItems.FirstOrDefault(item => item.Id == canonicalItemId);
        if (canonical is null)
            return OfficeMutationResult.Denied("선택한 대표 품목이 최신 후보 그룹에 포함되어 있지 않습니다.");
        var duplicateIds = activeItems
            .Where(item => item.Id != canonical.Id)
            .Select(item => item.Id)
            .ToList();
        var sideEffectPermissionResult = await ValidateItemDuplicateMergeSideEffectPermissionsAsync(duplicateIds, session, ct);
        if (sideEffectPermissionResult is not null)
            return sideEffectPermissionResult;

        var duplicates = activeItems
            .Where(item => duplicateIds.Contains(item.Id))
            .ToList();
        var now = DateTime.UtcNow;

        foreach (var duplicate in duplicates)
        {
            MergeItemValues(canonical, duplicate);
            duplicate.IsDeleted = true;
            MarkDirty(duplicate, now);
        }

        canonical.NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(canonical.NameOriginal);
        canonical.SpecificationMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(canonical.SpecificationOriginal);
        MarkDirty(canonical, now);

        var duplicateNames = duplicates.Select(duplicate => duplicate.NameOriginal).ToList();
        var duplicateSpecs = duplicates.Select(duplicate => duplicate.SpecificationOriginal).ToList();

        var invoiceLines = await _db.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.ItemId.HasValue && duplicateIds.Contains(line.ItemId.Value))
            .ToListAsync(ct);
        foreach (var line in invoiceLines)
        {
            line.ItemId = canonical.Id;
            if (ShouldReplaceDisplayName(line.ItemNameOriginal, duplicateNames))
                line.ItemNameOriginal = canonical.NameOriginal;
            if (ShouldReplaceDisplayName(line.SpecificationOriginal, duplicateSpecs))
                line.SpecificationOriginal = canonical.SpecificationOriginal;
        }

        var invoiceIdsToMark = invoiceLines
            .Select(line => line.InvoiceId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (invoiceIdsToMark.Count > 0)
        {
            var invoices = await _db.Invoices.IgnoreQueryFilters()
                .Where(invoice => invoiceIdsToMark.Contains(invoice.Id))
                .ToListAsync(ct);
            foreach (var invoice in invoices)
                MarkDirty(invoice, now);
        }

        var invoiceLineSerials = await _db.InvoiceLineSerials
            .Where(serial => serial.ItemId.HasValue && duplicateIds.Contains(serial.ItemId.Value))
            .ToListAsync(ct);
        foreach (var serial in invoiceLineSerials)
            serial.ItemId = canonical.Id;

        var rentalAssets = await _db.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.ItemId.HasValue && duplicateIds.Contains(asset.ItemId.Value))
            .ToListAsync(ct);
        foreach (var asset in rentalAssets)
        {
            asset.ItemId = canonical.Id;
            if (ShouldReplaceDisplayName(asset.ItemName, duplicateNames))
                asset.ItemName = canonical.NameOriginal;
            if (string.IsNullOrWhiteSpace(asset.ItemCategoryName) && !string.IsNullOrWhiteSpace(canonical.CategoryName))
                asset.ItemCategoryName = canonical.CategoryName;
            MarkDirty(asset, now);
        }

        var rentalAssetIds = rentalAssets
            .Select(asset => asset.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var histories = rentalAssetIds.Count == 0
            ? new List<LocalRentalAssetAssignmentHistory>()
            : await _db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                .Where(history => rentalAssetIds.Contains(history.AssetId))
                .ToListAsync(ct);
        foreach (var history in histories)
        {
            if (ShouldReplaceDisplayName(history.ItemName, duplicateNames))
            {
                history.ItemName = canonical.NameOriginal;
                MarkDirty(history, now);
            }
        }

        var rentalBillingTemplateProfileCount = await RemapRentalBillingProfileTemplateItemReferencesAsync(
            canonical,
            duplicates,
            now,
            ct);

        var serialLedgers = await _db.SerialLedgers
            .Where(ledger => ledger.ItemId.HasValue && duplicateIds.Contains(ledger.ItemId.Value))
            .ToListAsync(ct);
        foreach (var ledger in serialLedgers)
        {
            ledger.ItemId = canonical.Id;
            ledger.UpdatedAtUtc = now;
        }

        var inventoryTransferLines = await _db.InventoryTransferLines.IgnoreQueryFilters()
            .Where(line => line.ItemId.HasValue && duplicateIds.Contains(line.ItemId.Value))
            .ToListAsync(ct);
        foreach (var line in inventoryTransferLines)
        {
            line.ItemId = canonical.Id;
            if (ShouldReplaceDisplayName(line.ItemNameOriginal, duplicateNames))
                line.ItemNameOriginal = canonical.NameOriginal;
            if (ShouldReplaceDisplayName(line.SpecificationOriginal, duplicateSpecs))
                line.SpecificationOriginal = canonical.SpecificationOriginal;
        }

        var transferIdsToMark = inventoryTransferLines
            .Select(line => line.TransferId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (transferIdsToMark.Count > 0)
        {
            var transfers = await _db.InventoryTransfers.IgnoreQueryFilters()
                .Where(transfer => transferIdsToMark.Contains(transfer.Id))
                .ToListAsync(ct);
            foreach (var transfer in transfers)
                MarkDirty(transfer, now);
        }

        var inventoryMovements = await _db.InventoryMovements
            .Where(movement => movement.ItemId.HasValue && duplicateIds.Contains(movement.ItemId.Value))
            .ToListAsync(ct);
        foreach (var movement in inventoryMovements)
            movement.ItemId = canonical.Id;

        var stockLayers = await _db.StockLayers
            .Where(layer => layer.ItemId.HasValue && duplicateIds.Contains(layer.ItemId.Value))
            .ToListAsync(ct);
        foreach (var layer in stockLayers)
            layer.ItemId = canonical.Id;

        var canonicalStockTotal = await RemapItemWarehouseStocksAsync(canonical.Id, duplicateIds, now, ct);
        canonical.CurrentStock = canonicalStockTotal.HasValue
            ? canonicalStockTotal.Value
            : activeItems.Sum(item => item.CurrentStock);
        MarkDirty(canonical, now);

        AddDuplicateMergeAudit(
            "Item",
            canonical.Id,
            session,
            now,
            new
            {
                CanonicalId = canonical.Id,
                DuplicateIds = duplicateIds,
                CanonicalName = canonical.NameOriginal,
                CanonicalSpecification = canonical.SpecificationOriginal
            },
            new
            {
                MovedInvoiceLines = invoiceLines.Count,
                MovedInvoiceLineSerials = invoiceLineSerials.Count,
                MovedRentalAssets = rentalAssets.Count,
                MovedRentalBillingTemplateProfiles = rentalBillingTemplateProfileCount,
                MovedTransferLines = inventoryTransferLines.Count,
                MovedInventoryMovements = inventoryMovements.Count,
                MovedStockLayers = stockLayers.Count,
                MovedSerialLedgers = serialLedgers.Count,
                CanonicalStock = canonical.CurrentStock
            });

        if (TestOnlyBeforeDuplicateMergeSaveAsync is not null)
            await TestOnlyBeforeDuplicateMergeSaveAsync(ct);

        activeLocalEditorReason = BuildActiveLocalItemEditorBlockingReason(ids);
        if (activeLocalEditorReason is not null)
            return OfficeMutationResult.Denied(activeLocalEditorReason);

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(
            canonical.Id,
            $"품목 중복 후보 {activeItems.Count:N0}건 중 {duplicates.Count:N0}건을 '{NormalizeDisplay(canonical.NameOriginal, "대표 품목")}'로 병합했습니다.");
    }

    private static string? BuildActiveLocalItemEditorBlockingReason(IReadOnlyCollection<Guid> itemIds)
    {
        var activeSubjects = EntityEditSessionMonitor.FindActiveLocalSubjects("Item", itemIds);
        if (activeSubjects.Count == 0)
            return null;

        var displays = activeSubjects
            .Select(subject => string.IsNullOrWhiteSpace(subject.DisplayName) ? subject.EntityId : subject.DisplayName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(3)
            .ToList();
        return $"현재 이 PC에서 편집 중인 품목({string.Join(", ", displays)})이 포함되어 병합을 중단했습니다. 품목 편집을 마친 후 다시 비교하세요.";
    }

    private async Task<OfficeMutationResult?> ValidateItemDuplicateMergeSideEffectPermissionsAsync(
        IReadOnlyCollection<Guid> duplicateIds,
        SessionState session,
        CancellationToken ct)
    {
        if (duplicateIds.Count == 0)
            return null;

        var invoiceLines = await _db.InvoiceLines.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(line => line.ItemId.HasValue && duplicateIds.Contains(line.ItemId.Value))
            .ToListAsync(ct);
        var invoiceLineSerials = await _db.InvoiceLineSerials
            .AsNoTracking()
            .Where(serial => serial.ItemId.HasValue && duplicateIds.Contains(serial.ItemId.Value))
            .ToListAsync(ct);
        if (invoiceLines.Any(line => line.InvoiceId == Guid.Empty) ||
            invoiceLineSerials.Any(serial => serial.InvoiceId == Guid.Empty))
        {
            return OfficeMutationResult.Denied("참조 전표 ID가 비어 있는 전표 라인이 연결된 품목은 병합할 수 없습니다.");
        }

        var invoiceIds = invoiceLines
            .Select(line => line.InvoiceId)
            .Concat(invoiceLineSerials.Select(serial => serial.InvoiceId))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (invoiceIds.Count > 0)
        {
            if (!HasIntegrityPermission(session, AppPermissionNames.InvoiceEdit))
                return OfficeMutationResult.Denied("품목 병합으로 전표 라인을 함께 변경하려면 전표 편집 권한이 필요합니다.");

            var invoices = await _db.Invoices.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(invoice => invoiceIds.Contains(invoice.Id))
                .ToListAsync(ct);
            if (invoices.Count != invoiceIds.Count)
                return OfficeMutationResult.Denied("참조 전표를 찾을 수 없는 전표 라인이 연결된 품목은 병합할 수 없습니다.");

            foreach (var invoice in invoices)
            {
                var scopeEvidence = ResolveStoredScopeEvidence(
                    invoice.TenantCode,
                    invoice.ResponsibleOfficeCode,
                    invoice.OfficeCode);
                if (!CanWriteStoredScopeEvidenceForIntegrity(session, scopeEvidence))
                    return OfficeMutationResult.Denied("권한 범위 밖 전표 라인이 연결된 품목은 병합할 수 없습니다.");
            }
        }

        var rentalAssets = await _db.RentalAssets.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => asset.ItemId.HasValue && duplicateIds.Contains(asset.ItemId.Value))
            .ToListAsync(ct);
        if (rentalAssets.Count > 0)
        {
            if (!HasIntegrityPermission(session, AppPermissionNames.RentalAssetEdit))
                return OfficeMutationResult.Denied("품목 병합으로 렌탈 자산을 함께 변경하려면 렌탈 자산 편집 권한이 필요합니다.");

            foreach (var asset in rentalAssets)
            {
                var scopeEvidence = ResolveStoredScopeEvidence(
                    asset.TenantCode,
                    asset.ResponsibleOfficeCode,
                    asset.OfficeCode,
                    asset.ManagementCompanyCode);
                if (!CanWriteStoredScopeEvidenceForIntegrity(session, scopeEvidence))
                    return OfficeMutationResult.Denied("권한 범위 밖 렌탈 자산이 연결된 품목은 병합할 수 없습니다.");
            }

            var assetIds = rentalAssets
                .Select(asset => asset.Id)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            if (assetIds.Count > 0)
            {
                var histories = await _db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(history => assetIds.Contains(history.AssetId))
                    .ToListAsync(ct);
                foreach (var history in histories)
                {
                    var scopeEvidence = ResolveStoredScopeEvidence(
                        history.TenantCode,
                        history.ResponsibleOfficeCode,
                        ownerOfficeCode: null);
                    if (!CanWriteStoredScopeEvidenceForIntegrity(session, scopeEvidence))
                        return OfficeMutationResult.Denied("권한 범위 밖 렌탈 설치 이력이 연결된 품목은 병합할 수 없습니다.");
                }
            }
        }

        var rentalBillingProfiles = await LoadRentalBillingProfilesContainingItemIdsAsync(duplicateIds, ct);
        if (rentalBillingProfiles.Count > 0)
        {
            if (!HasIntegrityPermission(session, AppPermissionNames.RentalProfileEdit))
                return OfficeMutationResult.Denied("품목 병합으로 렌탈 청구 템플릿의 품목 참조를 함께 변경하려면 렌탈 청구 편집 권한이 필요합니다.");

            foreach (var profile in rentalBillingProfiles)
            {
                if (!CanSafelyRemapRentalBillingTemplateItemReferences(profile.BillingTemplateJson, duplicateIds))
                    return OfficeMutationResult.Denied("해석할 수 없거나 모호한 렌탈 청구 템플릿에 병합 대상 품목이 참조되어 있어 병합할 수 없습니다.");

                var scopeEvidence = ResolveStoredScopeEvidence(
                    profile.TenantCode,
                    profile.ResponsibleOfficeCode,
                    profile.OfficeCode,
                    profile.ManagementCompanyCode);
                if (!CanWriteStoredScopeEvidenceForIntegrity(session, scopeEvidence))
                    return OfficeMutationResult.Denied("권한 범위 밖의 렌탈 청구 템플릿이 연결된 품목은 병합할 수 없습니다.");
            }
        }

        var inventoryTransferLines = await _db.InventoryTransferLines.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(line => line.ItemId.HasValue && duplicateIds.Contains(line.ItemId.Value))
            .ToListAsync(ct);
        if (inventoryTransferLines.Any(line => line.TransferId == Guid.Empty))
            return OfficeMutationResult.Denied("참조 재고이동 문서 ID가 비어 있는 라인이 연결된 품목은 병합할 수 없습니다.");

        var transferIds = inventoryTransferLines
            .Select(line => line.TransferId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (transferIds.Count > 0)
        {
            if (!HasIntegrityPermission(session, AppPermissionNames.DeliveryEdit))
                return OfficeMutationResult.Denied("품목 병합으로 연결 재고이동 문서를 함께 변경하려면 재고이동 편집 권한이 필요합니다.");

            var transfers = await _db.InventoryTransfers.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(transfer => transferIds.Contains(transfer.Id))
                .ToListAsync(ct);
            if (transfers.Count != transferIds.Count)
                return OfficeMutationResult.Denied("참조 재고이동 문서를 찾을 수 없는 재고이동 라인이 연결된 품목은 병합할 수 없습니다.");

            foreach (var transfer in transfers)
            {
                if (!CanWriteInventoryTransferScopeForIntegrity(session, transfer))
                    return OfficeMutationResult.Denied("출발·도착 사업장 중 하나라도 쓰기 권한 범위 밖인 재고이동 문서가 연결된 품목은 병합할 수 없습니다.");
            }
        }

        var inventoryMovements = await _db.InventoryMovements
            .AsNoTracking()
            .Where(movement => movement.ItemId.HasValue && duplicateIds.Contains(movement.ItemId.Value))
            .ToListAsync(ct);
        foreach (var movement in inventoryMovements)
        {
            if (!CanWriteWarehouseScopeForIntegrity(session, movement.WarehouseCode))
                return OfficeMutationResult.Denied("권한 범위 밖 재고 원장이 연결된 품목은 병합할 수 없습니다.");
        }

        var stockLayers = await _db.StockLayers
            .AsNoTracking()
            .Where(layer => layer.ItemId.HasValue && duplicateIds.Contains(layer.ItemId.Value))
            .ToListAsync(ct);
        foreach (var layer in stockLayers)
        {
            if (!CanWriteWarehouseScopeForIntegrity(session, layer.WarehouseCode))
                return OfficeMutationResult.Denied("권한 범위 밖 재고 원가층이 연결된 품목은 병합할 수 없습니다.");
        }

        var serialLedgers = await _db.SerialLedgers
            .AsNoTracking()
            .Where(ledger => ledger.ItemId.HasValue && duplicateIds.Contains(ledger.ItemId.Value))
            .ToListAsync(ct);
        foreach (var ledger in serialLedgers)
        {
            if (!CanWriteWarehouseScopeForIntegrity(session, ledger.WarehouseCode))
                return OfficeMutationResult.Denied("권한 범위 밖 시리얼 원장이 연결된 품목은 병합할 수 없습니다.");
        }

        var stockRows = await _db.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock => duplicateIds.Contains(stock.ItemId))
            .ToListAsync(ct);
        foreach (var stock in stockRows)
        {
            if (!CanWriteWarehouseScopeForIntegrity(session, stock.WarehouseCode))
                return OfficeMutationResult.Denied("권한 범위 밖 창고 재고가 연결된 품목은 병합할 수 없습니다.");
        }

        return null;
    }

    private async Task<int> RemapRentalBillingProfileTemplateItemReferencesAsync(
        LocalItem canonical,
        IReadOnlyCollection<LocalItem> duplicates,
        DateTime now,
        CancellationToken ct)
    {
        var duplicateIds = duplicates
            .Select(item => item.Id)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        if (duplicateIds.Count == 0)
            return 0;

        var profiles = await LoadRentalBillingProfilesContainingItemIdsAsync(duplicateIds, ct);
        var changedCount = 0;

        foreach (var profile in profiles)
        {
            var remappedTemplateJson = RemapRentalBillingTemplateCatalogItemReference(
                profile.BillingTemplateJson,
                canonical.Id,
                duplicateIds,
                canonical.NameOriginal,
                canonical.SpecificationOriginal);
            if (string.Equals(profile.BillingTemplateJson, remappedTemplateJson, StringComparison.Ordinal))
                continue;

            profile.BillingTemplateJson = remappedTemplateJson!;
            MarkDirty(profile, now);
            changedCount++;
        }

        return changedCount;
    }

    private async Task<List<LocalRentalBillingProfile>> LoadRentalBillingProfilesContainingItemIdsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken ct)
    {
        var itemIdSet = itemIds
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        if (itemIdSet.Count == 0)
            return [];

        var profiles = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .Where(profile => !string.IsNullOrWhiteSpace(profile.BillingTemplateJson))
            .ToListAsync(ct);

        return profiles
            .Where(profile =>
                BillingTemplateContainsCatalogItemId(profile.BillingTemplateJson, itemIdSet) ||
                BillingTemplateContainsItemIdToken(profile.BillingTemplateJson, itemIdSet))
            .ToList();
    }

    private static bool CanSafelyRemapRentalBillingTemplateItemReferences(
        string? templateJson,
        IReadOnlyCollection<Guid> itemIds)
    {
        var itemIdSet = itemIds
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        if (itemIdSet.Count == 0 || !BillingTemplateContainsItemIdToken(templateJson, itemIdSet))
            return true;

        Guid probeCanonicalItemId;
        do
        {
            probeCanonicalItemId = Guid.NewGuid();
        }
        while (itemIdSet.Contains(probeCanonicalItemId));

        var remappedTemplateJson = RemapRentalBillingTemplateCatalogItemReference(
            templateJson,
            probeCanonicalItemId,
            itemIdSet,
            canonicalDisplayItemName: string.Empty,
            canonicalSpecification: string.Empty);
        return !string.Equals(templateJson, remappedTemplateJson, StringComparison.Ordinal) &&
               !BillingTemplateContainsItemIdToken(remappedTemplateJson, itemIdSet);
    }

    private static string? RemapRentalBillingTemplateCatalogItemReference(
        string? billingTemplateJson,
        Guid canonicalItemId,
        IReadOnlySet<Guid> duplicateItemIds,
        string canonicalDisplayItemName,
        string canonicalSpecification)
    {
        if (string.IsNullOrWhiteSpace(billingTemplateJson) ||
            canonicalItemId == Guid.Empty ||
            duplicateItemIds.Count == 0)
        {
            return billingTemplateJson;
        }

        try
        {
            if (JsonNode.Parse(billingTemplateJson) is not JsonArray templateItems)
                return billingTemplateJson;

            var changed = false;
            foreach (var item in templateItems.OfType<JsonObject>())
            {
                var catalogItemProperty = item.FirstOrDefault(property =>
                    string.Equals(
                        property.Key,
                        nameof(RentalBillingTemplateItemModel.CatalogItemId),
                        StringComparison.OrdinalIgnoreCase));
                if (catalogItemProperty.Key is null ||
                    catalogItemProperty.Value is not JsonValue catalogItemValue ||
                    !catalogItemValue.TryGetValue<string>(out var rawCatalogItemId) ||
                    !Guid.TryParse(rawCatalogItemId, out var catalogItemId) ||
                    !duplicateItemIds.Contains(catalogItemId))
                {
                    continue;
                }

                item[catalogItemProperty.Key] = canonicalItemId.ToString("D");
                SetExistingJsonPropertyValue(
                    item,
                    nameof(RentalBillingTemplateItemModel.DisplayItemName),
                    canonicalDisplayItemName);
                SetExistingJsonPropertyValue(
                    item,
                    nameof(RentalBillingTemplateItemModel.Specification),
                    canonicalSpecification);
                changed = true;
            }

            return changed ? templateItems.ToJsonString() : billingTemplateJson;
        }
        catch (JsonException)
        {
            return billingTemplateJson;
        }
        catch (InvalidOperationException)
        {
            return billingTemplateJson;
        }
    }

    private static void SetExistingJsonPropertyValue(JsonObject item, string propertyName, string value)
    {
        var property = item.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, propertyName, StringComparison.OrdinalIgnoreCase));
        if (property.Key is not null)
            item[property.Key] = value;
    }

    private static bool BillingTemplateContainsCatalogItemId(string? templateJson, IReadOnlySet<Guid> itemIds)
    {
        if (itemIds.Count == 0 || string.IsNullOrWhiteSpace(templateJson))
            return false;

        try
        {
            var items = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(templateJson, JsonOptions) ?? [];
            return items.Any(item => item is not null && item.CatalogItemId.HasValue && itemIds.Contains(item.CatalogItemId.Value));
        }
        catch
        {
            return false;
        }
    }

    private static bool BillingTemplateContainsItemIdToken(string? templateJson, IReadOnlySet<Guid> itemIds)
    {
        if (itemIds.Count == 0 || string.IsNullOrWhiteSpace(templateJson))
            return false;

        return JsonGuidTokenSafety.ContainsExactGuidToken(templateJson, itemIds);
    }

    private static void LogIntegrityScanStep(string operation, Stopwatch stopwatch, string? detail = null)
    {
        stopwatch.Stop();
        OperationTiming.LogIfSlow(
            "INTEGRITY",
            operation,
            stopwatch.Elapsed,
            detail,
            infoThreshold: TimeSpan.FromMilliseconds(300),
            warningThreshold: TimeSpan.FromSeconds(2));
    }

    private static IQueryable<LocalRentalBillingProfile> SelectIntegrityRentalProfileProjection(
        IQueryable<LocalRentalBillingProfile> query)
        => query.Select(profile => new LocalRentalBillingProfile
        {
            Id = profile.Id,
            TenantCode = profile.TenantCode,
            OfficeCode = profile.OfficeCode,
            CustomerId = profile.CustomerId,
            CustomerName = profile.CustomerName,
            ItemName = profile.ItemName,
            InstallSiteName = profile.InstallSiteName,
            ManagementCompanyCode = profile.ManagementCompanyCode,
            BillingMethod = profile.BillingMethod,
            BillingStatus = profile.BillingStatus,
            MonthlyAmount = profile.MonthlyAmount,
            SettlementStatus = profile.SettlementStatus,
            CompletionStatus = profile.CompletionStatus,
            SettledAmount = profile.SettledAmount,
            OutstandingAmount = profile.OutstandingAmount,
            ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
            BillingTemplateJson = profile.BillingTemplateJson,
            BillingRunsJson = profile.BillingRunsJson,
            IsActive = profile.IsActive
        });

    private static IQueryable<LocalRentalAsset> SelectIntegrityRentalAssetProjection(
        IQueryable<LocalRentalAsset> query)
        => query.Select(asset => new LocalRentalAsset
        {
            Id = asset.Id,
            TenantCode = asset.TenantCode,
            OfficeCode = asset.OfficeCode,
            CustomerId = asset.CustomerId,
            BillingProfileId = asset.BillingProfileId,
            ManagementId = asset.ManagementId,
            ManagementNumber = asset.ManagementNumber,
            ManagementCompanyCode = asset.ManagementCompanyCode,
            CurrentCustomerName = asset.CurrentCustomerName,
            BillingEligibilityStatus = asset.BillingEligibilityStatus,
            ItemName = asset.ItemName,
            CustomerName = asset.CustomerName,
            MonthlyFee = asset.MonthlyFee,
            ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
            AssetStatus = asset.AssetStatus
        });

    private static IQueryable<LocalRentalAssetAssignmentHistory> SelectIntegrityAssignmentHistoryProjection(
        IQueryable<LocalRentalAssetAssignmentHistory> query)
        => query.Select(history => new LocalRentalAssetAssignmentHistory
        {
            Id = history.Id,
            AssetId = history.AssetId,
            BillingProfileId = history.BillingProfileId,
            CustomerId = history.CustomerId,
            TenantCode = history.TenantCode,
            ResponsibleOfficeCode = history.ResponsibleOfficeCode,
            CustomerName = history.CustomerName,
            ItemName = history.ItemName,
            MachineNumber = history.MachineNumber,
            ManagementNumber = history.ManagementNumber,
            ContractStartDate = history.ContractStartDate,
            ContractEndDate = history.ContractEndDate,
            LinkedAtUtc = history.LinkedAtUtc,
            IsCurrent = history.IsCurrent
        });

    private static IQueryable<LocalCustomer> SelectIntegrityCustomerProjection(
        IQueryable<LocalCustomer> query)
        => query.Select(customer => new LocalCustomer
        {
            Id = customer.Id,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            NameOriginal = customer.NameOriginal,
            BusinessNumber = customer.BusinessNumber,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode
        });

    private static IQueryable<LocalItem> SelectIntegrityItemProjection(
        IQueryable<LocalItem> query)
        => query.Select(item => new LocalItem
        {
            Id = item.Id,
            Revision = item.Revision,
            UpdatedAtUtc = item.UpdatedAtUtc,
            IsDirty = item.IsDirty,
            TenantCode = item.TenantCode,
            OfficeCode = item.OfficeCode,
            NameOriginal = item.NameOriginal,
            SpecificationOriginal = item.SpecificationOriginal,
            CategoryName = item.CategoryName,
            ItemKind = item.ItemKind,
            TrackingType = item.TrackingType,
            Unit = item.Unit,
            BoxQuantity = item.BoxQuantity,
            StorageLocation = item.StorageLocation,
            CurrentStock = item.CurrentStock,
            SafetyStock = item.SafetyStock,
            PurchasePrice = item.PurchasePrice,
            SalePrice = item.SalePrice,
            RetailPrice = item.RetailPrice,
            PriceGradeA = item.PriceGradeA,
            PriceGradeB = item.PriceGradeB,
            PriceGradeC = item.PriceGradeC,
            LastPurchaseDate = item.LastPurchaseDate,
            LastSaleDate = item.LastSaleDate,
            CatalogExtensionSyncPending = item.CatalogExtensionSyncPending,
            SimpleMemo = item.SimpleMemo,
            IsRental = item.IsRental,
            IsSale = item.IsSale,
            SerialNumber = item.SerialNumber,
            MaterialNumber = item.MaterialNumber,
            InstallLocation = item.InstallLocation,
            RentalStartDate = item.RentalStartDate,
            RentalEndDate = item.RentalEndDate,
            Notes = item.Notes
        });

    private static IQueryable<LocalWarehouse> SelectIntegrityWarehouseProjection(
        IQueryable<LocalWarehouse> query)
        => query.Select(warehouse => new LocalWarehouse
        {
            Id = warehouse.Id,
            OfficeCode = warehouse.OfficeCode,
            Code = warehouse.Code,
            Name = warehouse.Name,
            IsActive = warehouse.IsActive
        });

    private async Task<Dictionary<Guid, LocalCustomer>> LoadCustomersByIdsAsync(
        IReadOnlyCollection<Guid> customerIds,
        CancellationToken ct)
    {
        var ids = customerIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var rows = new Dictionary<Guid, LocalCustomer>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds.ToList();
            var batchRows = await _db.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => scopedBatchIds.Contains(customer.Id) && !customer.IsDeleted)
                .Select(customer => new LocalCustomer
                {
                    Id = customer.Id,
                    NameOriginal = customer.NameOriginal
                })
                .ToListAsync(ct);

            foreach (var customer in batchRows)
                rows.TryAdd(customer.Id, customer);
        }

        return rows;
    }

    private async Task<HashSet<Guid>> LoadActiveRentalAssetIdsByIdsAsync(
        IReadOnlyCollection<Guid> assetIds,
        CancellationToken ct)
    {
        var ids = assetIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var rows = new HashSet<Guid>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds.ToList();
            var batchRows = await _db.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset => scopedBatchIds.Contains(asset.Id) && !asset.IsDeleted)
                .Select(asset => asset.Id)
                .ToListAsync(ct);
            rows.UnionWith(batchRows);
        }

        return rows;
    }

    private async Task<HashSet<Guid>> LoadActiveRentalProfileIdsReferencedByAssetsAsync(
        IReadOnlyCollection<Guid> profileIds,
        CancellationToken ct)
    {
        var ids = profileIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var rows = new HashSet<Guid>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds.ToList();
            var batchRows = await _db.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset =>
                    !asset.IsDeleted &&
                    asset.BillingProfileId.HasValue &&
                    scopedBatchIds.Contains(asset.BillingProfileId.Value))
                .Select(asset => asset.BillingProfileId!.Value)
                .Distinct()
                .ToListAsync(ct);
            rows.UnionWith(batchRows);
        }

        return rows;
    }

    private async Task<List<LocalItemWarehouseStock>> LoadItemWarehouseStocksForItemsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken ct)
    {
        var ids = itemIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var rows = new List<LocalItemWarehouseStock>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds.ToList();
            rows.AddRange(await _db.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock => scopedBatchIds.Contains(stock.ItemId))
                .Select(stock => new LocalItemWarehouseStock
                {
                    ItemId = stock.ItemId,
                    WarehouseCode = stock.WarehouseCode,
                    Quantity = stock.Quantity
                })
                .ToListAsync(ct));
        }

        return rows;
    }

    private async Task<List<DataIntegrityIssueDetail>> LoadDeletedItemStockResidueIssuesAsync(
        SessionState session,
        CancellationToken ct)
    {
        var deletedItemsWithCurrentStock = await SelectIntegrityItemProjection(ApplyOperationalAlertItemScopePrefilter(
                _db.Items
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(item => item.IsDeleted && item.CurrentStock != 0m),
                session))
            .ToListAsync(ct);

        var deletedItemIdsWithWarehouseRows = await (
                from stock in _db.ItemWarehouseStocks.AsNoTracking()
                join item in ApplyOperationalAlertItemScopePrefilter(
                        _db.Items
                            .IgnoreQueryFilters()
                            .AsNoTracking()
                            .Where(item => item.IsDeleted),
                        session)
                    on stock.ItemId equals item.Id
                select stock.ItemId)
            .Distinct()
            .ToListAsync(ct);

        var residueItemIds = deletedItemsWithCurrentStock
            .Select(item => item.Id)
            .Concat(deletedItemIdsWithWarehouseRows)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (residueItemIds.Count == 0)
            return [];

        var deletedItems = (await LoadDeletedIntegrityItemsByIdsAsync(residueItemIds, session, ct))
            .Where(item =>
            {
                var itemScope = ResolveItemScope(item);
                return IsInSessionScope(itemScope.TenantCode, itemScope.OfficeCode, session);
            })
            .OrderBy(item => item.NameOriginal)
            .ThenBy(item => item.SpecificationOriginal)
            .ThenBy(item => item.Id)
            .ToList();
        if (deletedItems.Count == 0)
            return [];

        var deletedItemIdSet = deletedItems.Select(item => item.Id).ToHashSet();
        var stockRowsByItem = (await LoadItemWarehouseStocksForItemsAsync(deletedItemIdSet, ct))
            .GroupBy(stock => stock.ItemId)
            .ToDictionary(group => group.Key, group => group.OrderBy(stock => stock.WarehouseCode).ToList());

        var issues = new List<DataIntegrityIssueDetail>();
        foreach (var item in deletedItems)
        {
            var itemScope = ResolveItemScope(item);
            var stocks = stockRowsByItem.TryGetValue(item.Id, out var rows) ? rows : [];
            var stockTotal = stocks.Sum(stock => stock.Quantity);
            if (item.CurrentStock == 0m && stocks.Count == 0)
                continue;

            var stockBreakdown = stocks.Count == 0
                ? "창고 행 없음"
                : string.Join(", ", stocks.Select(stock => $"{NormalizeDisplay(stock.WarehouseCode, "창고")}:{stock.Quantity:N2}"));
            AddGeneralIssue(
                issues,
                DataIntegrityIssueCodes.InventoryDeletedItemStockResidue,
                entityType: "삭제 품목",
                entityId: item.Id,
                itemName: item.NameOriginal,
                officeCode: itemScope.OfficeCode,
                currentValue: $"삭제 품목 현재고 {item.CurrentStock:N2} / 창고행 {stocks.Count:N0}건 / 창고합계 {stockTotal:N2}",
                expectedValue: "삭제 품목 현재고 0 / 창고별 재고 행 없음",
                message: $"{NormalizeDisplay(item.NameOriginal, "품목")} 삭제 품목에 재고 잔여가 남아 있습니다. {stockBreakdown}",
                directActionKind: DataIntegrityDirectActionKind.OpenSyncDiagnostics,
                reviewInfo: BuildItemScopeReviewInfo(item, itemScope));
        }

        return issues;
    }

    private async Task<List<DataIntegrityIssueDetail>> LoadMissingAttachmentFileIssuesAsync(SessionState session, CancellationToken ct)
    {
        var rows = await (
                from attachment in _db.TransactionAttachments.IgnoreQueryFilters().AsNoTracking()
                join transaction in _db.Transactions.IgnoreQueryFilters().AsNoTracking()
                    on attachment.TransactionId equals transaction.Id
                where !attachment.IsDeleted &&
                      !transaction.IsDeleted &&
                      attachment.FileSize > 0
                orderby attachment.UploadedAtUtc, attachment.Id
                select new
                {
                    attachment.Id,
                    attachment.TransactionId,
                    attachment.AttachmentType,
                    attachment.FileName,
                    attachment.StoredPath,
                    attachment.FileSize,
                    attachment.VerificationStatus,
                    TransactionTenantCode = transaction.TenantCode,
                    TransactionOfficeCode = transaction.ResponsibleOfficeCode,
                    transaction.TransactionDate,
                    transaction.TransactionKind
                })
            .ToListAsync(ct);

        var issues = new List<DataIntegrityIssueDetail>();
        foreach (var row in rows)
        {
            if (!IsInSessionScope(row.TransactionTenantCode, row.TransactionOfficeCode, session) ||
                HasReadableLocalAttachmentFile(row.StoredPath))
            {
                continue;
            }

            AddGeneralIssue(
                issues,
                DataIntegrityIssueCodes.MissingAttachmentFiles,
                entityType: "거래첨부",
                entityId: row.Id,
                officeCode: OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(row.TransactionOfficeCode, session.OfficeCode),
                currentValue: $"TransactionId {row.TransactionId:D} / 파일 {NormalizeDisplay(row.FileName, "파일명 없음")} / 크기 {row.FileSize:N0} bytes / 경로 {NormalizeDisplay(row.StoredPath, "경로 없음")}",
                expectedValue: "StoredPath 실제 파일 존재",
                message: $"{NormalizeDisplay(row.FileName, "거래첨부")} 행 {row.Id:N}의 로컬 저장 파일을 찾을 수 없습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenSyncDiagnostics,
                relatedEntityIds: [row.TransactionId],
                reviewInfo: $"거래일 {row.TransactionDate:yyyy-MM-dd} / 구분 {PaymentFlowConstants.GetTransactionKindDisplayName(row.TransactionKind)} / 첨부유형 {NormalizeDisplay(row.AttachmentType, "기타")} / 확인상태 {NormalizeDisplay(row.VerificationStatus, "미확인")}");
        }

        return issues;
    }

    private async Task<List<DataIntegrityIssueDetail>> LoadInvoiceLineMissingInvoiceReferenceIssuesAsync(SessionState session, CancellationToken ct)
    {
        if (!session.HasAdministrativePrivileges)
            return [];

        var lines = await (
                from line in _db.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                join invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking()
                    on line.InvoiceId equals invoice.Id into invoiceGroup
                from invoice in invoiceGroup.DefaultIfEmpty()
                where invoice == null
                orderby line.InvoiceId, line.OrderIndex, line.Id
                select new
                {
                    line.Id,
                    line.InvoiceId,
                    line.ItemId,
                    line.ItemNameOriginal,
                    line.LineAmount,
                    line.OrderIndex,
                    line.IsDeleted
                })
            .ToListAsync(ct);

        if (lines.Count == 0)
            return [];

        var lineIds = lines.Select(line => line.Id).Distinct().ToArray();
        var itemIds = lines
            .Where(line => line.ItemId.HasValue && line.ItemId.Value != Guid.Empty)
            .Select(line => line.ItemId!.Value)
            .Distinct()
            .ToArray();
        var scopeEvidenceByLineId = new Dictionary<Guid, List<(string Source, string OfficeCode)>>();

        void AddScopeEvidence(Guid lineId, string source, string? officeCode)
        {
            if (!OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out var normalizedOfficeCode))
                return;

            if (!scopeEvidenceByLineId.TryGetValue(lineId, out var evidence))
            {
                evidence = [];
                scopeEvidenceByLineId[lineId] = evidence;
            }

            if (evidence.Any(current =>
                    string.Equals(current.Source, source, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(current.OfficeCode, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            evidence.Add((source, normalizedOfficeCode));
        }

        void AddWarehouseScopeEvidence(Guid lineId, string source, string? warehouseCode)
        {
            if (TryResolveOfficeCodeFromWarehouseEvidence(warehouseCode, out var officeCode))
                AddScopeEvidence(lineId, source, officeCode);
        }

        var movementRows = await _db.InventoryMovements.IgnoreQueryFilters().AsNoTracking()
            .Where(movement => movement.InvoiceLineId.HasValue && lineIds.Contains(movement.InvoiceLineId.Value))
            .Select(movement => new
            {
                LineId = movement.InvoiceLineId!.Value,
                movement.WarehouseCode
            })
            .ToListAsync(ct);
        foreach (var movement in movementRows)
            AddWarehouseScopeEvidence(movement.LineId, "InventoryMovement", movement.WarehouseCode);

        var stockLayerRows = await _db.StockLayers.IgnoreQueryFilters().AsNoTracking()
            .Where(layer => layer.SourceInvoiceLineId.HasValue && lineIds.Contains(layer.SourceInvoiceLineId.Value))
            .Select(layer => new
            {
                LineId = layer.SourceInvoiceLineId!.Value,
                layer.WarehouseCode
            })
            .ToListAsync(ct);
        foreach (var layer in stockLayerRows)
            AddWarehouseScopeEvidence(layer.LineId, "StockLayer", layer.WarehouseCode);

        var costAllocationRows = await _db.CostAllocations.IgnoreQueryFilters().AsNoTracking()
            .Where(allocation =>
                lineIds.Contains(allocation.SalesInvoiceLineId) ||
                (allocation.PurchaseInvoiceLineId.HasValue && lineIds.Contains(allocation.PurchaseInvoiceLineId.Value)))
            .Select(allocation => new
            {
                allocation.SalesInvoiceLineId,
                allocation.PurchaseInvoiceLineId,
                allocation.WarehouseCode
            })
            .ToListAsync(ct);
        foreach (var allocation in costAllocationRows)
        {
            if (lineIds.Contains(allocation.SalesInvoiceLineId))
                AddWarehouseScopeEvidence(allocation.SalesInvoiceLineId, "CostAllocationSales", allocation.WarehouseCode);
            if (allocation.PurchaseInvoiceLineId.HasValue && lineIds.Contains(allocation.PurchaseInvoiceLineId.Value))
                AddWarehouseScopeEvidence(allocation.PurchaseInvoiceLineId.Value, "CostAllocationPurchase", allocation.WarehouseCode);
        }

        var serialRows = await (
                from serial in _db.InvoiceLineSerials.IgnoreQueryFilters().AsNoTracking()
                join ledger in _db.SerialLedgers.IgnoreQueryFilters().AsNoTracking()
                    on serial.SerialNumber equals ledger.SerialNumber into ledgerGroup
                from ledger in ledgerGroup.DefaultIfEmpty()
                where lineIds.Contains(serial.InvoiceLineId) && ledger != null
                select new
                {
                    serial.InvoiceLineId,
                    ledger!.WarehouseCode
                })
            .ToListAsync(ct);
        foreach (var serial in serialRows)
            AddWarehouseScopeEvidence(serial.InvoiceLineId, "SerialLedger", serial.WarehouseCode);

        var itemRows = await _db.Items.IgnoreQueryFilters().AsNoTracking()
            .Where(item => itemIds.Contains(item.Id))
            .Select(item => new
            {
                item.Id,
                item.OfficeCode,
                item.TenantCode
            })
            .ToListAsync(ct);
        var itemScopeEvidenceById = itemRows.ToDictionary(
            item => item.Id,
            item => ResolveStoredScopeEvidence(
                item.TenantCode,
                responsibleOfficeCode: null,
                ownerOfficeCode: item.OfficeCode));
        var itemScopeEvidenceByLineId = new Dictionary<Guid, IntegrityStoredScopeEvidence>();
        foreach (var line in lines.Where(line => line.ItemId.HasValue && itemScopeEvidenceById.ContainsKey(line.ItemId.Value)))
        {
            var itemScopeEvidence = itemScopeEvidenceById[line.ItemId!.Value];
            itemScopeEvidenceByLineId[line.Id] = itemScopeEvidence;
            if (itemScopeEvidence.HasOfficeEvidence && !itemScopeEvidence.IsTenantOnly)
            {
                AddScopeEvidence(line.Id, "ItemOffice", itemScopeEvidence.OfficeCode);
            }
        }

        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, DomainConstants.OfficeUsenet);
        var issues = new List<DataIntegrityIssueDetail>(lines.Count);
        foreach (var line in lines)
        {
            var issueOfficeCode = officeCode;
            var reviewInfo = $"InvoiceId {line.InvoiceId:D} / ItemId {(line.ItemId.HasValue ? line.ItemId.Value.ToString("D") : "없음")}";
            if (scopeEvidenceByLineId.TryGetValue(line.Id, out var scopeEvidence) && scopeEvidence.Count > 0)
            {
                var evidenceOfficeCodes = scopeEvidence
                    .Select(evidence => evidence.OfficeCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var combinedScopeEvidence = evidenceOfficeCodes
                    .Select(evidenceOfficeCode => ResolveStoredScopeEvidence(
                        TenantScopeCatalog.GetTenantCodeForOffice(evidenceOfficeCode),
                        responsibleOfficeCode: evidenceOfficeCode,
                        ownerOfficeCode: null))
                    .ToList();
                if (itemScopeEvidenceByLineId.TryGetValue(line.Id, out var linkedItemScopeEvidence))
                    combinedScopeEvidence.Add(linkedItemScopeEvidence);

                if (!CanReadCombinedStoredScopeEvidenceForIntegrity(session, combinedScopeEvidence))
                    continue;

                issueOfficeCode = evidenceOfficeCodes.Length == 1
                    ? evidenceOfficeCodes[0]
                    : OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, evidenceOfficeCodes[0]);
                reviewInfo += " / ScopeEvidence " + string.Join(", ", scopeEvidence
                    .Select(evidence => $"{evidence.Source}:{evidence.OfficeCode}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
                if (itemScopeEvidenceByLineId.TryGetValue(line.Id, out linkedItemScopeEvidence))
                {
                    reviewInfo += linkedItemScopeEvidence.HasEvidence
                        ? $" / ItemScopeTenant {linkedItemScopeEvidence.TenantCode} / ItemScopeOffice {NormalizeDisplay(linkedItemScopeEvidence.OfficeCode, "-")}"
                        : " / ItemScopeEvidence invalid";
                }
            }
            else if (itemScopeEvidenceByLineId.TryGetValue(line.Id, out var itemScopeEvidence))
            {
                if (!CanReadStoredScopeEvidenceForIntegrity(session, itemScopeEvidence))
                    continue;

                issueOfficeCode = ResolveIssueOfficeCodeForStoredScope(
                    itemScopeEvidence,
                    session,
                    officeCode);
                reviewInfo += itemScopeEvidence.HasEvidence
                    ? $" / ScopeEvidence ItemTenant:{itemScopeEvidence.TenantCode}"
                    : " / ScopeEvidence ItemScopeInvalid";
            }
            else
            {
                if (!session.HasGlobalDataScope)
                    continue;

                reviewInfo += " / ScopeEvidence 없음";
            }

            var deletionState = line.IsDeleted ? "삭제" : "활성";
            AddGeneralIssue(
                issues,
                DataIntegrityIssueCodes.InvoiceLineMissingInvoiceReference,
                entityType: "전표 세부내역",
                entityId: line.Id,
                itemName: line.ItemNameOriginal,
                officeCode: issueOfficeCode,
                currentValue: $"InvoiceId {line.InvoiceId:D} / 금액 {line.LineAmount:N0} / 삭제상태 {deletionState}",
                expectedValue: "참조 전표 행 존재",
                message: $"{NormalizeDisplay(line.ItemNameOriginal, "전표 세부내역")} 행 {line.Id:N}의 전표 참조가 현재 로컬 DB에 없습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenSyncDiagnostics,
                reviewInfo: reviewInfo);
        }

        return issues;
    }

    private async Task<List<DataIntegrityIssueDetail>> LoadPaymentMissingInvoiceReferenceIssuesAsync(SessionState session, CancellationToken ct)
    {
        if (!session.HasAdministrativePrivileges)
            return [];

        var payments = await (
                from payment in _db.Payments.IgnoreQueryFilters().AsNoTracking()
                join invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking()
                    on payment.InvoiceId equals invoice.Id into invoiceGroup
                from invoice in invoiceGroup.DefaultIfEmpty()
                join transaction in _db.Transactions.IgnoreQueryFilters().AsNoTracking()
                    on payment.Id equals transaction.Id into transactionGroup
                from transaction in transactionGroup.DefaultIfEmpty()
                where invoice == null
                orderby payment.PaymentDate, payment.Id
                select new
                {
                    PaymentId = payment.Id,
                    payment.InvoiceId,
                    payment.PaymentDate,
                    payment.Amount,
                    payment.IsDeleted,
                    TransactionId = transaction == null ? null : (Guid?)transaction.Id,
                    TransactionTenantCode = transaction == null ? null : transaction.TenantCode,
                    TransactionResponsibleOfficeCode = transaction == null ? null : transaction.ResponsibleOfficeCode,
                    TransactionOwnerOfficeCode = transaction == null ? null : transaction.OfficeCode,
                    TransactionCustomerId = transaction == null ? null : (Guid?)transaction.CustomerId,
                    TransactionDate = transaction == null ? null : (DateOnly?)transaction.TransactionDate,
                    TransactionKind = transaction == null ? null : transaction.TransactionKind
                })
            .ToListAsync(ct);

        if (payments.Count == 0)
            return [];

        var customerIds = payments
            .Where(payment => payment.TransactionCustomerId.HasValue && payment.TransactionCustomerId.Value != Guid.Empty)
            .Select(payment => payment.TransactionCustomerId!.Value)
            .Distinct()
            .ToArray();
        var customersById = customerIds.Length == 0
            ? new Dictionary<Guid, LocalCustomer>()
            : await _db.Customers.IgnoreQueryFilters().AsNoTracking()
                .Where(customer => customerIds.Contains(customer.Id))
                .ToDictionaryAsync(customer => customer.Id, ct);
        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, DomainConstants.OfficeUsenet);
        var issues = new List<DataIntegrityIssueDetail>(payments.Count);
        foreach (var payment in payments)
        {
            customersById.TryGetValue(payment.TransactionCustomerId ?? Guid.Empty, out var customer);
            var scopeEvidenceSources = new List<IntegrityStoredScopeEvidence>(capacity: 2);
            if (customer is not null)
            {
                scopeEvidenceSources.Add(ResolveStoredScopeEvidence(
                    customer.TenantCode,
                    customer.ResponsibleOfficeCode,
                    customer.OfficeCode));
            }

            if (payment.TransactionId.HasValue)
            {
                scopeEvidenceSources.Add(ResolveStoredScopeEvidence(
                    payment.TransactionTenantCode,
                    payment.TransactionResponsibleOfficeCode,
                    payment.TransactionOwnerOfficeCode));
            }

            if (!CanReadCombinedStoredScopeEvidenceForIntegrity(session, scopeEvidenceSources))
                continue;

            var validScopeEvidence = scopeEvidenceSources
                .Where(evidence => evidence.HasEvidence)
                .ToArray();
            var scopedTenants = validScopeEvidence
                .Select(evidence => evidence.TenantCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var scopedOffices = validScopeEvidence
                .Where(evidence => evidence.HasOfficeEvidence)
                .Select(evidence => evidence.OfficeCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var scopedTenantCode = scopedTenants.Length == 1 ? scopedTenants[0] : string.Empty;
            var scopedOfficeCode = scopedOffices.Length == 1 ? scopedOffices[0] : string.Empty;

            var deletionState = payment.IsDeleted ? "삭제" : "활성";
            var issueOfficeCode = scopedOffices.Length == 1
                ? scopedOffices[0]
                : OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, officeCode);
            var relatedIds = new[]
                {
                    payment.TransactionId ?? Guid.Empty,
                    payment.TransactionCustomerId ?? Guid.Empty
                }
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray();
            AddGeneralIssue(
                issues,
                DataIntegrityIssueCodes.PaymentMissingInvoiceReference,
                entityType: "수금/지급",
                entityId: payment.PaymentId,
                customerName: customer?.NameOriginal,
                officeCode: issueOfficeCode,
                currentValue: $"InvoiceId {payment.InvoiceId:D} / 금액 {payment.Amount:N0} / 삭제상태 {deletionState}",
                expectedValue: "참조 전표 행 존재",
                message: $"{payment.PaymentDate:yyyy-MM-dd} 수금/지급 {payment.PaymentId:N}의 전표 참조가 현재 로컬 DB에 없습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenSyncDiagnostics,
                relatedEntityIds: relatedIds,
                reviewInfo: string.Join(" / ", new[]
                {
                    payment.TransactionId.HasValue ? $"TransactionId {payment.TransactionId.Value:D}" : "TransactionId 없음",
                    payment.TransactionCustomerId.HasValue ? $"CustomerId {payment.TransactionCustomerId.Value:D}" : "CustomerId 없음",
                    $"ScopeTenant {NormalizeDisplay(scopedTenantCode, "-")}",
                    $"ScopeOffice {NormalizeDisplay(scopedOfficeCode, "-")}",
                    payment.TransactionDate.HasValue ? $"TransactionDate {payment.TransactionDate.Value:yyyy-MM-dd}" : "TransactionDate 없음",
                    string.IsNullOrWhiteSpace(payment.TransactionKind) ? "TransactionKind 없음" : $"TransactionKind {payment.TransactionKind}"
                }));
        }

        return issues;
    }

    private async Task<List<DataIntegrityIssueDetail>> LoadInvoiceLinkedTransactionPaymentMismatchIssuesAsync(SessionState session, CancellationToken ct)
    {
        var rows = await (
                from transaction in _db.Transactions.IgnoreQueryFilters().AsNoTracking()
                    .Where(transaction =>
                        !transaction.IsDeleted &&
                        transaction.LinkedInvoiceId.HasValue &&
                        transaction.LinkedInvoiceId.Value != Guid.Empty &&
                        transaction.SettlementAmount > 0m)
                join invoice in ApplyOperationalAlertInvoiceScopePrefilter(
                        _db.Invoices.IgnoreQueryFilters().AsNoTracking().Where(invoice => !invoice.IsDeleted),
                        session)
                    on transaction.LinkedInvoiceId!.Value equals invoice.Id
                join payment in _db.Payments.IgnoreQueryFilters().AsNoTracking()
                    on transaction.Id equals payment.Id into paymentGroup
                from payment in paymentGroup.DefaultIfEmpty()
                where payment == null ||
                      payment.IsDeleted ||
                      payment.InvoiceId != invoice.Id ||
                      payment.Amount - transaction.SettlementAmount >= 1m ||
                      transaction.SettlementAmount - payment.Amount >= 1m
                orderby transaction.TransactionDate, transaction.Id
                select new
                {
                    TransactionId = transaction.Id,
                    TransactionTenantCode = transaction.TenantCode,
                    TransactionOfficeCode = transaction.ResponsibleOfficeCode,
                    transaction.TransactionDate,
                    transaction.TransactionKind,
                    transaction.LinkedInvoiceNumber,
                    TransactionSettlementAmount = transaction.SettlementAmount,
                    InvoiceId = invoice.Id,
                    invoice.InvoiceNumber,
                    invoice.LocalTempNumber,
                    invoice.InvoiceDate,
                    invoice.TotalAmount,
                    invoice.ResponsibleOfficeCode,
                    PaymentId = payment == null ? null : (Guid?)payment.Id,
                    PaymentInvoiceId = payment == null ? null : (Guid?)payment.InvoiceId,
                    PaymentAmount = payment == null ? null : (decimal?)payment.Amount,
                    PaymentIsDeleted = payment == null ? null : (bool?)payment.IsDeleted
                })
            .ToListAsync(ct);

        var issues = new List<DataIntegrityIssueDetail>();
        foreach (var row in rows.Where(row => IsInSessionScope(row.TransactionTenantCode, row.TransactionOfficeCode, session)))
        {
            var invoiceNumber = NormalizeDisplay(
                !string.IsNullOrWhiteSpace(row.InvoiceNumber) ? row.InvoiceNumber : row.LocalTempNumber,
                row.InvoiceId.ToString("N"));
            var reason = BuildInvoiceLinkedTransactionPaymentMismatchReason(
                row.InvoiceId,
                row.TransactionSettlementAmount,
                row.PaymentId,
                row.PaymentInvoiceId,
                row.PaymentAmount,
                row.PaymentIsDeleted);
            var relatedIds = row.PaymentId.HasValue
                ? new[] { row.TransactionId, row.PaymentId.Value }
                : new[] { row.TransactionId };

            AddGeneralIssue(
                issues,
                DataIntegrityIssueCodes.InvoiceLinkedTransactionPaymentMismatch,
                entityType: "전표 연결 수금/거래",
                entityId: row.InvoiceId,
                officeCode: row.ResponsibleOfficeCode,
                currentValue: $"전표 {invoiceNumber} / 거래 정산 {row.TransactionSettlementAmount:N0} / 수금·지급 {FormatOptionalAmount(row.PaymentAmount)} / {reason}",
                expectedValue: "전표 연결 거래내역과 같은 ID의 활성 수금/지급 행 전표·금액 일치",
                message: $"{row.TransactionDate:yyyy-MM-dd} 전표 {invoiceNumber}의 거래내역과 수금/지급 행이 서로 다릅니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenPaymentForInvoice,
                relatedEntityIds: relatedIds,
                reviewInfo: string.Join(" / ", new[]
                {
                    $"InvoiceId {row.InvoiceId:D}",
                    $"TransactionId {row.TransactionId:D}",
                    row.PaymentId.HasValue ? $"PaymentId {row.PaymentId.Value:D}" : "PaymentId 없음",
                    row.PaymentInvoiceId.HasValue ? $"PaymentInvoiceId {row.PaymentInvoiceId.Value:D}" : "PaymentInvoiceId 없음",
                    $"TransactionSettlement {row.TransactionSettlementAmount:N0}",
                    row.PaymentAmount.HasValue ? $"PaymentAmount {row.PaymentAmount.Value:N0}" : "PaymentAmount 없음",
                    row.PaymentIsDeleted.HasValue ? $"PaymentDeleted {row.PaymentIsDeleted.Value}" : "PaymentDeleted -",
                    string.IsNullOrWhiteSpace(row.TransactionKind) ? "TransactionKind -" : $"TransactionKind {row.TransactionKind}",
                    string.IsNullOrWhiteSpace(row.LinkedInvoiceNumber) ? "LinkedInvoiceNumber -" : $"LinkedInvoiceNumber {row.LinkedInvoiceNumber}"
                }));
        }

        return issues;
    }

    private async Task<List<DataIntegrityIssueDetail>> LoadTransactionOperationalScopeMismatchIssuesAsync(SessionState session, CancellationToken ct)
    {
        var rows = await (
                from transaction in _db.Transactions.IgnoreQueryFilters().AsNoTracking()
                    .Where(transaction => !transaction.IsDeleted)
                join customer in _db.Customers.IgnoreQueryFilters().AsNoTracking().Where(customer => !customer.IsDeleted)
                    on transaction.CustomerId equals customer.Id
                join invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking().Where(invoice => !invoice.IsDeleted)
                    on transaction.LinkedInvoiceId equals (Guid?)invoice.Id into invoiceGroup
                from invoice in invoiceGroup.DefaultIfEmpty()
                join profile in _db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().Where(profile => !profile.IsDeleted)
                    on transaction.LinkedRentalBillingProfileId equals (Guid?)profile.Id into profileGroup
                from profile in profileGroup.DefaultIfEmpty()
                orderby transaction.TransactionDate, transaction.Id
                select new
                {
                    TransactionId = transaction.Id,
                    TransactionTenantCode = transaction.TenantCode,
                    TransactionOfficeCode = transaction.OfficeCode,
                    TransactionResponsibleOfficeCode = transaction.ResponsibleOfficeCode,
                    transaction.TransactionDate,
                    transaction.TransactionKind,
                    transaction.LinkedInvoiceId,
                    transaction.LinkedInvoiceNumber,
                    transaction.LinkedRentalBillingProfileId,
                    transaction.LinkedRentalBillingRunId,
                    transaction.SettlementAmount,
                    transaction.ReceiptTotal,
                    transaction.PaymentTotal,
                    transaction.Note,
                    CustomerId = customer.Id,
                    CustomerName = customer.NameOriginal,
                    CustomerTenantCode = customer.TenantCode,
                    CustomerOfficeCode = customer.OfficeCode,
                    CustomerResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                    InvoiceId = invoice == null ? null : (Guid?)invoice.Id,
                    InvoiceNumber = invoice == null ? null : invoice.InvoiceNumber,
                    InvoiceLocalTempNumber = invoice == null ? null : invoice.LocalTempNumber,
                    InvoiceOfficeCode = invoice == null ? null : invoice.OfficeCode,
                    InvoiceResponsibleOfficeCode = invoice == null ? null : invoice.ResponsibleOfficeCode,
                    ProfileId = profile == null ? null : (Guid?)profile.Id,
                    ProfileKey = profile == null ? null : profile.ProfileKey,
                    ProfileCustomerName = profile == null ? null : profile.CustomerName,
                    ProfileOfficeCode = profile == null ? null : profile.OfficeCode,
                    ProfileResponsibleOfficeCode = profile == null ? null : profile.ResponsibleOfficeCode,
                    ProfileManagementCompanyCode = profile == null ? null : profile.ManagementCompanyCode
                })
            .ToListAsync(ct);

        var issues = new List<DataIntegrityIssueDetail>();
        foreach (var row in rows)
        {
            var customerResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                row.CustomerResponsibleOfficeCode,
                DomainConstants.OfficeUsenet);
            var expectedResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                row.InvoiceResponsibleOfficeCode ??
                row.ProfileResponsibleOfficeCode ??
                row.ProfileManagementCompanyCode ??
                row.CustomerResponsibleOfficeCode,
                customerResponsibleOfficeCode);
            var expectedOwnerOfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(
                row.InvoiceOfficeCode ??
                row.ProfileOfficeCode ??
                row.CustomerOfficeCode,
                expectedResponsibleOfficeCode,
                row.CustomerOfficeCode);
            var expectedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                row.TransactionTenantCode,
                expectedOwnerOfficeCode,
                row.CustomerTenantCode,
                expectedResponsibleOfficeCode);

            var hasMismatch = RequiresExactTenantCode(row.TransactionTenantCode, expectedTenantCode) ||
                              RequiresExactOfficeScopeCode(row.TransactionOfficeCode, expectedOwnerOfficeCode) ||
                              RequiresExactOfficeScopeCode(row.TransactionResponsibleOfficeCode, expectedResponsibleOfficeCode);
            if (!hasMismatch)
                continue;

            if (!IsInSessionScope(row.TransactionTenantCode, row.TransactionResponsibleOfficeCode, session) &&
                !IsInSessionScope(expectedTenantCode, expectedResponsibleOfficeCode, session))
            {
                continue;
            }

            var invoiceNumber = NormalizeDisplay(
                !string.IsNullOrWhiteSpace(row.InvoiceNumber) ? row.InvoiceNumber : row.InvoiceLocalTempNumber,
                row.LinkedInvoiceId.HasValue && row.LinkedInvoiceId.Value != Guid.Empty
                    ? row.LinkedInvoiceId.Value.ToString("N")
                    : "전표 미연결");
            var profileDisplay = NormalizeDisplay(
                !string.IsNullOrWhiteSpace(row.ProfileKey) ? row.ProfileKey : row.ProfileCustomerName,
                row.LinkedRentalBillingProfileId.HasValue && row.LinkedRentalBillingProfileId.Value != Guid.Empty
                    ? row.LinkedRentalBillingProfileId.Value.ToString("N")
                    : "렌탈 미연결");
            var amount = row.SettlementAmount > 0m
                ? row.SettlementAmount
                : Math.Max(Math.Max(row.ReceiptTotal, row.PaymentTotal), 0m);
            var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                expectedResponsibleOfficeCode,
                row.TransactionResponsibleOfficeCode);
            var entityId = row.InvoiceId.HasValue && row.InvoiceId.Value != Guid.Empty
                ? row.InvoiceId
                : row.TransactionId;
            var directActionKind = row.InvoiceId.HasValue && row.InvoiceId.Value != Guid.Empty
                ? DataIntegrityDirectActionKind.OpenPaymentForInvoice
                : DataIntegrityDirectActionKind.OpenSyncDiagnostics;
            var relatedIds = new[]
                {
                    row.TransactionId,
                    row.CustomerId,
                    row.InvoiceId ?? Guid.Empty,
                    row.ProfileId ?? Guid.Empty
                }
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray();
            var storedScopeDisplay =
                $"저장 scope {NormalizeTenantForDisplay(row.TransactionTenantCode, row.TransactionOfficeCode, row.TransactionResponsibleOfficeCode)} / " +
                $"{NormalizeOfficeScopeForDisplay(row.TransactionOfficeCode, row.TransactionResponsibleOfficeCode)} / " +
                $"{NormalizeOfficeScopeForDisplay(row.TransactionResponsibleOfficeCode, customerResponsibleOfficeCode)} / " +
                $"거래 {row.TransactionDate:yyyy-MM-dd} / {PaymentFlowConstants.GetTransactionKindDisplayName(row.TransactionKind)} / 금액 {amount:N0}";
            var expectedScopeDisplay =
                $"기대 scope {expectedTenantCode} / {expectedOwnerOfficeCode} / {expectedResponsibleOfficeCode} / " +
                $"전표 {invoiceNumber} / 렌탈 {profileDisplay}";
            var issueMessage =
                $"{row.TransactionDate:yyyy-MM-dd} {NormalizeDisplay(row.CustomerName, row.CustomerId.ToString("N"))} " +
                "수금/지급 거래내역의 scope 저장값이 거래처/연결 전표/렌탈 청구 범위와 다릅니다.";

            AddGeneralIssue(
                issues,
                DataIntegrityIssueCodes.TransactionOperationalScopeMismatch,
                entityType: "수금/지급 거래내역",
                entityId: entityId,
                customerName: row.CustomerName,
                officeCode: officeCode,
                currentValue: storedScopeDisplay,
                expectedValue: expectedScopeDisplay,
                message: issueMessage,
                directActionKind: directActionKind,
                relatedEntityIds: relatedIds,
                reviewInfo: string.Join(" / ", new[]
                {
                    $"TransactionId {row.TransactionId:D}",
                    $"CustomerId {row.CustomerId:D}",
                    row.LinkedInvoiceId.HasValue ? $"LinkedInvoiceId {row.LinkedInvoiceId.Value:D}" : "LinkedInvoiceId 없음",
                    row.InvoiceId.HasValue ? $"ActiveInvoiceId {row.InvoiceId.Value:D}" : "ActiveInvoiceId 없음",
                    row.LinkedRentalBillingProfileId.HasValue ? $"LinkedRentalBillingProfileId {row.LinkedRentalBillingProfileId.Value:D}" : "LinkedRentalBillingProfileId 없음",
                    row.LinkedRentalBillingRunId.HasValue ? $"LinkedRentalBillingRunId {row.LinkedRentalBillingRunId.Value:D}" : "LinkedRentalBillingRunId 없음",
                    $"StoredTenant {NormalizeDisplay(row.TransactionTenantCode, "-")}",
                    $"StoredOwner {NormalizeDisplay(row.TransactionOfficeCode, "-")}",
                    $"StoredResponsible {NormalizeDisplay(row.TransactionResponsibleOfficeCode, "-")}",
                    $"ExpectedTenant {expectedTenantCode}",
                    $"ExpectedOwner {expectedOwnerOfficeCode}",
                    $"ExpectedResponsible {expectedResponsibleOfficeCode}",
                    string.IsNullOrWhiteSpace(row.LinkedInvoiceNumber) ? "LinkedInvoiceNumber -" : $"LinkedInvoiceNumber {row.LinkedInvoiceNumber}",
                    string.IsNullOrWhiteSpace(row.Note) ? "Note -" : $"Note {row.Note}"
                }));
        }

        return issues;
    }

    private async Task<List<DataIntegrityIssueDetail>> LoadRentalDeletedInvoiceActivePaymentIssuesAsync(SessionState session, CancellationToken ct)
    {
        var rows = await (
                from payment in _db.Payments.IgnoreQueryFilters().AsNoTracking().Where(payment => !payment.IsDeleted)
                join invoice in ApplyOperationalAlertInvoiceScopePrefilter(
                        _db.Invoices.IgnoreQueryFilters().AsNoTracking()
                            .Where(invoice =>
                                invoice.IsDeleted &&
                                invoice.LinkedRentalBillingProfileId.HasValue &&
                                invoice.LinkedRentalBillingProfileId.Value != Guid.Empty),
                        session)
                    on payment.InvoiceId equals invoice.Id
                join transaction in _db.Transactions.IgnoreQueryFilters().AsNoTracking()
                    on payment.Id equals transaction.Id into transactionGroup
                from transaction in transactionGroup.DefaultIfEmpty()
                orderby invoice.InvoiceDate, invoice.InvoiceNumber, payment.PaymentDate, payment.Id
                select new
                {
                    PaymentId = payment.Id,
                    payment.PaymentDate,
                    PaymentAmount = payment.Amount,
                    payment.Note,
                    InvoiceId = invoice.Id,
                    invoice.InvoiceNumber,
                    invoice.LocalTempNumber,
                    invoice.InvoiceDate,
                    invoice.TenantCode,
                    invoice.OfficeCode,
                    invoice.ResponsibleOfficeCode,
                    invoice.LinkedRentalBillingProfileId,
                    invoice.LinkedRentalBillingRunId,
                    TransactionId = transaction == null ? (Guid?)null : transaction.Id,
                    TransactionIsDeleted = transaction == null ? (bool?)null : transaction.IsDeleted,
                    TransactionLinkedInvoiceId = transaction == null ? (Guid?)null : transaction.LinkedInvoiceId,
                    TransactionSettlementAmount = transaction == null ? (decimal?)null : transaction.SettlementAmount,
                    TransactionTenantCode = transaction == null ? null : transaction.TenantCode,
                    TransactionOfficeCode = transaction == null ? null : transaction.OfficeCode,
                    TransactionResponsibleOfficeCode = transaction == null ? null : transaction.ResponsibleOfficeCode
                })
            .ToListAsync(ct);

        var issues = new List<DataIntegrityIssueDetail>();
        foreach (var row in rows)
        {
            var invoiceScope = ResolveEntityOfficeScope(row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode);
            var transactionScope = row.TransactionId.HasValue
                ? ResolveEntityOfficeScope(row.TransactionTenantCode, row.TransactionOfficeCode, row.TransactionResponsibleOfficeCode)
                : (TenantCode: string.Empty, OfficeCode: string.Empty);
            if (!IsInSessionScope(invoiceScope.TenantCode, invoiceScope.OfficeCode, session) &&
                (!row.TransactionId.HasValue || !IsInSessionScope(transactionScope.TenantCode, transactionScope.OfficeCode, session)))
            {
                continue;
            }

            var invoiceNumber = NormalizeDisplay(
                !string.IsNullOrWhiteSpace(row.InvoiceNumber) ? row.InvoiceNumber : row.LocalTempNumber,
                row.InvoiceId.ToString("N"));
            var transactionState = row.TransactionId.HasValue
                ? row.TransactionIsDeleted == true ? "삭제 거래내역" : "활성 거래내역"
                : "직접 수금/지급(거래내역 없음)";
            var transactionLinkText = row.TransactionLinkedInvoiceId.HasValue && row.TransactionLinkedInvoiceId.Value != Guid.Empty
                ? row.TransactionLinkedInvoiceId.Value.ToString("D")
                : "없음";

            AddGeneralIssue(
                issues,
                DataIntegrityIssueCodes.RentalDeletedInvoiceActivePayment,
                entityType: "렌탈 전표/수금",
                entityId: row.PaymentId,
                officeCode: invoiceScope.OfficeCode,
                currentValue: $"삭제 전표 {invoiceNumber} / 활성 수금 {row.PaymentAmount:N0} / 거래 {transactionState}",
                expectedValue: "삭제 전표에 연결된 수금/지급도 삭제",
                message: $"{row.InvoiceDate:yyyy-MM-dd} 삭제된 렌탈 전표 {invoiceNumber}에 활성 수금/지급 {row.PaymentAmount:N0}원이 남아 있습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenPaymentForInvoice,
                relatedEntityIds: new[] { row.InvoiceId, row.PaymentId, row.TransactionId ?? Guid.Empty },
                reviewInfo: string.Join(" / ", new[]
                {
                    $"PaymentId {row.PaymentId:D}",
                    $"InvoiceId {row.InvoiceId:D}",
                    row.TransactionId.HasValue ? $"TransactionId {row.TransactionId.Value:D}" : "TransactionId 없음",
                    row.LinkedRentalBillingProfileId.HasValue ? $"InvoiceProfile {row.LinkedRentalBillingProfileId.Value:D}" : "InvoiceProfile 없음",
                    row.LinkedRentalBillingRunId.HasValue ? $"InvoiceRun {row.LinkedRentalBillingRunId.Value:D}" : "InvoiceRun 없음",
                    $"InvoiceScopeTenant {invoiceScope.TenantCode}",
                    $"InvoiceScopeOffice {invoiceScope.OfficeCode}",
                    row.TransactionId.HasValue ? $"TransactionScopeTenant {transactionScope.TenantCode}" : "TransactionScopeTenant 없음",
                    row.TransactionId.HasValue ? $"TransactionScopeOffice {transactionScope.OfficeCode}" : "TransactionScopeOffice 없음",
                    $"TransactionLinkedInvoiceId {transactionLinkText}",
                    row.TransactionSettlementAmount.HasValue ? $"TransactionSettlementAmount {row.TransactionSettlementAmount.Value:N0}" : "TransactionSettlementAmount 없음",
                    string.IsNullOrWhiteSpace(row.Note) ? "Note -" : $"Note {row.Note}"
                }));
        }

        return issues;
    }

    private async Task<List<DataIntegrityIssueDetail>> LoadRentalInvoiceDeletedPaymentDetachedTransactionIssuesAsync(SessionState session, CancellationToken ct)
    {
        var rows = await (
                from payment in _db.Payments.IgnoreQueryFilters().AsNoTracking().Where(payment => payment.IsDeleted)
                join invoice in ApplyOperationalAlertInvoiceScopePrefilter(
                        _db.Invoices.IgnoreQueryFilters().AsNoTracking()
                            .Where(invoice =>
                                !invoice.IsDeleted &&
                                invoice.LinkedRentalBillingProfileId.HasValue &&
                                invoice.LinkedRentalBillingProfileId.Value != Guid.Empty),
                        session)
                    on payment.InvoiceId equals invoice.Id
                join transaction in _db.Transactions.IgnoreQueryFilters().AsNoTracking().Where(transaction => !transaction.IsDeleted)
                    on payment.Id equals transaction.Id
                where !transaction.LinkedInvoiceId.HasValue ||
                      transaction.LinkedInvoiceId.Value == Guid.Empty ||
                      transaction.LinkedInvoiceId.Value != invoice.Id ||
                      transaction.SettlementAmount != payment.Amount ||
                      transaction.LinkedRentalBillingProfileId != invoice.LinkedRentalBillingProfileId ||
                      transaction.LinkedRentalBillingRunId != invoice.LinkedRentalBillingRunId
                orderby invoice.InvoiceDate, invoice.InvoiceNumber, payment.PaymentDate, payment.Id
                select new
                {
                    PaymentId = payment.Id,
                    payment.PaymentDate,
                    PaymentAmount = payment.Amount,
                    payment.Note,
                    InvoiceId = invoice.Id,
                    invoice.InvoiceNumber,
                    invoice.LocalTempNumber,
                    invoice.InvoiceDate,
                    invoice.TotalAmount,
                    invoice.TenantCode,
                    invoice.ResponsibleOfficeCode,
                    invoice.LinkedRentalBillingProfileId,
                    invoice.LinkedRentalBillingRunId,
                    TransactionId = transaction.Id,
                    transaction.TransactionDate,
                    transaction.TransactionKind,
                    transaction.LinkedInvoiceId,
                    transaction.SettlementAmount,
                    TransactionTenantCode = transaction.TenantCode,
                    TransactionOfficeCode = transaction.ResponsibleOfficeCode,
                    TransactionRentalProfileId = transaction.LinkedRentalBillingProfileId,
                    TransactionRentalRunId = transaction.LinkedRentalBillingRunId
                })
            .ToListAsync(ct);

        var issues = new List<DataIntegrityIssueDetail>();
        foreach (var row in rows.Where(row => IsInSessionScope(row.TransactionTenantCode, row.TransactionOfficeCode, session)))
        {
            var invoiceNumber = NormalizeDisplay(
                !string.IsNullOrWhiteSpace(row.InvoiceNumber) ? row.InvoiceNumber : row.LocalTempNumber,
                row.InvoiceId.ToString("N"));
            var linkedInvoiceText = row.LinkedInvoiceId.HasValue && row.LinkedInvoiceId.Value != Guid.Empty
                ? row.LinkedInvoiceId.Value.ToString("D")
                : "없음";

            AddGeneralIssue(
                issues,
                DataIntegrityIssueCodes.RentalInvoiceDeletedPaymentDetachedTransaction,
                entityType: "렌탈 전표/수금",
                entityId: row.PaymentId,
                officeCode: row.ResponsibleOfficeCode,
                currentValue: $"전표 {invoiceNumber} / 삭제 수금 {row.PaymentAmount:N0} / 거래 전표링크 {linkedInvoiceText} / 거래 정산 {row.SettlementAmount:N0}",
                expectedValue: "수금/지급 활성 및 거래내역 전표 링크·정산금액 일치",
                message: $"{row.InvoiceDate:yyyy-MM-dd} 렌탈 전표 {invoiceNumber}에 삭제 상태 수금/지급과 전표 링크가 끊긴 활성 거래내역이 함께 남아 있습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenPaymentForInvoice,
                relatedEntityIds: new[] { row.InvoiceId, row.TransactionId },
                reviewInfo: string.Join(" / ", new[]
                {
                    $"PaymentId {row.PaymentId:D}",
                    $"InvoiceId {row.InvoiceId:D}",
                    $"TransactionId {row.TransactionId:D}",
                    row.LinkedRentalBillingProfileId.HasValue ? $"InvoiceProfile {row.LinkedRentalBillingProfileId.Value:D}" : "InvoiceProfile 없음",
                    row.TransactionRentalProfileId.HasValue ? $"TransactionProfile {row.TransactionRentalProfileId.Value:D}" : "TransactionProfile 없음"
                }));
        }

        return issues;
    }

    private async Task<List<DataIntegrityIssueDetail>> LoadRentalBillingRunSettlementMismatchIssuesAsync(
        IReadOnlyCollection<LocalRentalBillingProfile> profiles,
        SessionState session,
        CancellationToken ct)
    {
        var profileIds = profiles.Select(profile => profile.Id).Distinct().ToList();
        if (profileIds.Count == 0)
            return [];

        var transactions = await _db.Transactions.IgnoreQueryFilters().AsNoTracking()
            .Where(transaction =>
                !transaction.IsDeleted &&
                transaction.LinkedRentalBillingProfileId.HasValue &&
                profileIds.Contains(transaction.LinkedRentalBillingProfileId.Value))
            .Select(transaction => new
            {
                transaction.Id,
                ProfileId = transaction.LinkedRentalBillingProfileId!.Value,
                RunId = transaction.LinkedRentalBillingRunId,
                Amount = transaction.SettlementAmount
            })
            .ToListAsync(ct);

        var transactionKeys = transactions
            .Select(transaction => (PaymentId: transaction.Id, transaction.ProfileId))
            .ToHashSet();

        var directPayments = await (
                from payment in _db.Payments.IgnoreQueryFilters().AsNoTracking().Where(payment => !payment.IsDeleted)
                join invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking().Where(invoice =>
                        !invoice.IsDeleted &&
                        invoice.IsLatestVersion &&
                        invoice.LinkedRentalBillingProfileId.HasValue &&
                        profileIds.Contains(invoice.LinkedRentalBillingProfileId.Value))
                    on payment.InvoiceId equals invoice.Id
                select new
                {
                    payment.Id,
                    ProfileId = invoice.LinkedRentalBillingProfileId!.Value,
                    RunId = invoice.LinkedRentalBillingRunId,
                    Amount = payment.Amount
                })
            .ToListAsync(ct);

        var transactionSettledAmounts = transactions
            .GroupBy(transaction => (transaction.ProfileId, RunId: NormalizeRunId(transaction.RunId)))
            .ToDictionary(group => group.Key, group => group.Sum(transaction => transaction.Amount));
        var directPaymentSettledAmounts = directPayments
            .Where(payment => !transactionKeys.Contains((payment.Id, payment.ProfileId)))
            .GroupBy(payment => (payment.ProfileId, RunId: NormalizeRunId(payment.RunId)))
            .ToDictionary(group => group.Key, group => group.Sum(payment => payment.Amount));

        var issues = new List<DataIntegrityIssueDetail>();
        foreach (var profile in profiles)
        {
            var runs = ParseRentalBillingRuns(profile.BillingRunsJson);
            foreach (var run in runs)
            {
                if (run.RunId == Guid.Empty ||
                    RentalBillingRunIdentityPolicy.IsSuppressedByTombstone(runs, run))
                    continue;

                var runId = NormalizeRunId(run.RunId);
                var key = (profile.Id, RunId: runId);
                transactionSettledAmounts.TryGetValue(key, out var transactionAmount);
                directPaymentSettledAmounts.TryGetValue(key, out var directPaymentAmount);
                var actualAmount = transactionAmount + directPaymentAmount;
                if (!AmountDiffers(run.SettledAmount, actualAmount))
                    continue;

                var profileDisplay = BuildProfileDisplay(profile);
                AddGeneralIssue(
                    issues,
                    DataIntegrityIssueCodes.RentalBillingRunSettlementMismatch,
                    entityType: "렌탈 청구 run",
                    entityId: profile.Id,
                    customerName: profile.CustomerName,
                    officeCode: ResolveProfileOfficeCode(profile),
                    currentValue: $"Run {NormalizeDisplay(run.RunKey, runId.ToString("N"))} / 저장 정산 {run.SettledAmount:N0} / 실제 {actualAmount:N0} / 거래 {transactionAmount:N0} / 직접결제 {directPaymentAmount:N0}",
                    expectedValue: "저장 정산금액과 실제 활성 수금/거래내역 합계 일치",
                    message: $"{profileDisplay}의 {run.ScheduledDate:yyyy-MM-dd} 청구 run 정산금액이 실제 입금 근거와 다릅니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile,
                    relatedEntityIds: new[] { runId },
                    reviewInfo: string.Join(" / ", new[]
                    {
                        $"ProfileId {profile.Id:D}",
                        $"RunId {runId:D}",
                        $"Billed {run.BilledAmount:N0}",
                        $"Stored {run.SettledAmount:N0}",
                        $"Actual {actualAmount:N0}",
                        $"Transaction {transactionAmount:N0}",
                        $"DirectPayment {directPaymentAmount:N0}",
                        string.IsNullOrWhiteSpace(run.Status) ? "Status -" : $"Status {run.Status}",
                        string.IsNullOrWhiteSpace(run.SettlementStatus) ? "SettlementStatus -" : $"SettlementStatus {run.SettlementStatus}"
                    }));
            }
        }

        return issues;
    }

    private static List<DataIntegrityIssueDetail> LoadRentalBillingRunMissingRunIdIssues(
        IReadOnlyCollection<LocalRentalBillingProfile> profiles)
    {
        var issues = new List<DataIntegrityIssueDetail>();
        foreach (var profile in profiles)
        {
            var runs = ParseRentalBillingRuns(profile.BillingRunsJson);
            foreach (var run in runs)
            {
                if (run.RunId != Guid.Empty ||
                    RentalBillingRunIdentityPolicy.IsSuppressedByTombstone(runs, run))
                    continue;

                var profileDisplay = BuildProfileDisplay(profile);
                AddIssue(
                    issues,
                    DataIntegrityIssueCodes.RentalBillingRunMissingRunId,
                    profile,
                    asset: null,
                    entityType: "렌탈 청구 run",
                    entityId: profile.Id,
                    currentValue: $"RunKey {NormalizeDisplay(run.RunKey, "없음")} / 청구일 {run.ScheduledDate:yyyy-MM-dd} / 청구액 {run.BilledAmount:N0} / 정산 {run.SettledAmount:N0}",
                    expectedValue: "청구 run은 고유 RunId를 가져야 전표/수금/동기화 정산 비교 대상이 됩니다.",
                    message: $"{profileDisplay}의 청구 run에 RunId가 없어 자동 정산 비교 대상에서 제외됩니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile,
                    reviewInfo: string.Join(" / ", new[]
                    {
                        $"ProfileId {profile.Id:D}",
                        "RunId 없음",
                        $"RunKey {NormalizeDisplay(run.RunKey, "-")}",
                        $"Scheduled {run.ScheduledDate:yyyy-MM-dd}",
                        $"Period {run.PeriodStartDate:yyyy-MM-dd}~{run.PeriodEndDate:yyyy-MM-dd}",
                        $"Billed {run.BilledAmount:N0}",
                        $"Settled {run.SettledAmount:N0}",
                        string.IsNullOrWhiteSpace(run.Status) ? "Status -" : $"Status {run.Status}",
                        string.IsNullOrWhiteSpace(run.SettlementStatus) ? "SettlementStatus -" : $"SettlementStatus {run.SettlementStatus}"
                    }));
            }
        }

        return issues;
    }

    private static List<DataIntegrityIssueDetail> LoadRentalBillingRunKeyConflictingRunIdIssues(
        IReadOnlyCollection<LocalRentalBillingProfile> profiles)
    {
        var issues = new List<DataIntegrityIssueDetail>();
        foreach (var profile in profiles)
        {
            var diagnosticRuns = ParseRentalBillingRunsForIdentityDiagnosis(profile.BillingRunsJson);
            foreach (var group in diagnosticRuns
                         .Where(run =>
                             run.RunId != Guid.Empty &&
                             !string.IsNullOrWhiteSpace(SyncIdentityGenerator.NormalizeKey(run.RunKey)))
                         .GroupBy(
                             run => SyncIdentityGenerator.NormalizeKey(run.RunKey),
                             StringComparer.Ordinal)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var runIds = group
                    .Select(run => run.RunId)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();
                if (runIds.Length <= 1)
                    continue;

                var normalizedRunKey = group.Key;
                var runIdDisplay = string.Join(" / ", runIds.Select(id => id.ToString("D")));
                AddIssue(
                    issues,
                    DataIntegrityIssueCodes.RentalBillingRunKeyConflictingRunIds,
                    profile,
                    asset: null,
                    entityType: "렌탈 청구 run",
                    entityId: profile.Id,
                    currentValue: $"ProfileId {profile.Id:D} / normalizedRunKey {normalizedRunKey} / RunIds {runIdDisplay}",
                    expectedValue: "normalized RunKey 하나당 non-empty RunId 하나",
                    message: $"프로필 {profile.Id:D}의 RunKey {normalizedRunKey}에 서로 다른 RunId {runIdDisplay}가 연결되어 재무 작업이 차단됩니다. 자동 병합이나 ID 교체는 수행하지 않습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
            }

            foreach (var group in diagnosticRuns
                         .Where(run => run.RunId != Guid.Empty)
                         .GroupBy(run => run.RunId)
                         .OrderBy(group => group.Key))
            {
                var runKeys = group
                    .Select(run => SyncIdentityGenerator.NormalizeKey(run.RunKey))
                    .Where(runKey => !string.IsNullOrWhiteSpace(runKey))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(runKey => runKey, StringComparer.Ordinal)
                    .ToArray();
                if (runKeys.Length <= 1)
                    continue;

                var runId = group.Key;
                var runKeyDisplay = string.Join(" / ", runKeys);
                AddIssue(
                    issues,
                    DataIntegrityIssueCodes.RentalBillingRunKeyConflictingRunIds,
                    profile,
                    asset: null,
                    entityType: "렌탈 청구 run",
                    entityId: profile.Id,
                    currentValue: $"ProfileId {profile.Id:D} / RunId {runId:D} / normalizedRunKeys {runKeyDisplay}",
                    expectedValue: "normalized RunKey 하나당 non-empty RunId 하나",
                    message: $"프로필 {profile.Id:D}의 RunId {runId:D}에 서로 다른 normalized RunKey {runKeyDisplay}가 연결되어 재무 작업이 차단됩니다. 자동 병합이나 ID 교체는 수행하지 않습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
            }
        }

        return issues;
    }

    private static List<DataIntegrityIssueDetail> LoadMalformedRentalBillingRunsJsonIssues(
        IReadOnlyCollection<LocalRentalBillingProfile> profiles)
    {
        var issues = new List<DataIntegrityIssueDetail>();
        foreach (var profile in profiles)
        {
            if (TryParseRentalBillingRuns(profile.BillingRunsJson, out _) ||
                TryParseRentalBillingRunsForIdentityDiagnosis(profile.BillingRunsJson, out _))
                continue;

            AddIssue(
                issues,
                DataIntegrityIssueCodes.RentalBillingRunsJsonMalformed,
                profile,
                asset: null,
                entityType: "렌탈 청구 run JSON",
                entityId: profile.Id,
                currentValue: $"ProfileId {profile.Id:D} / BillingRunsJson 파싱 실패",
                expectedValue: "빈 값 또는 RentalBillingRunModel JSON 배열",
                message: $"프로필 {profile.Id:D}의 BillingRunsJson이 손상되어 재무 작업이 차단됩니다. 원문을 자동 변경하지 않습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile);
        }

        return issues;
    }

    private async Task<List<DataIntegrityIssueDetail>> LoadRentalBillingProfileSummaryMismatchIssuesAsync(
        IReadOnlyCollection<LocalRentalBillingProfile> profiles,
        SessionState session,
        CancellationToken ct)
    {
        var profileIds = profiles.Select(profile => profile.Id).Distinct().ToList();
        if (profileIds.Count == 0)
            return [];

        var transactions = await _db.Transactions.IgnoreQueryFilters().AsNoTracking()
            .Where(transaction =>
                !transaction.IsDeleted &&
                transaction.LinkedRentalBillingProfileId.HasValue &&
                profileIds.Contains(transaction.LinkedRentalBillingProfileId.Value))
            .Select(transaction => new
            {
                transaction.Id,
                ProfileId = transaction.LinkedRentalBillingProfileId!.Value,
                RunId = transaction.LinkedRentalBillingRunId,
                Amount = transaction.SettlementAmount
            })
            .ToListAsync(ct);

        var transactionKeys = transactions
            .Select(transaction => (PaymentId: transaction.Id, transaction.ProfileId))
            .ToHashSet();

        var directPayments = await (
                from payment in _db.Payments.IgnoreQueryFilters().AsNoTracking().Where(payment => !payment.IsDeleted)
                join invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking().Where(invoice =>
                        !invoice.IsDeleted &&
                        invoice.IsLatestVersion &&
                        invoice.LinkedRentalBillingProfileId.HasValue &&
                        profileIds.Contains(invoice.LinkedRentalBillingProfileId.Value))
                    on payment.InvoiceId equals invoice.Id
                select new
                {
                    payment.Id,
                    ProfileId = invoice.LinkedRentalBillingProfileId!.Value,
                    RunId = invoice.LinkedRentalBillingRunId,
                    Amount = payment.Amount
                })
            .ToListAsync(ct);

        var invoices = await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
            .Where(invoice =>
                !invoice.IsDeleted &&
                invoice.IsLatestVersion &&
                invoice.LinkedRentalBillingProfileId.HasValue &&
                profileIds.Contains(invoice.LinkedRentalBillingProfileId.Value))
            .Select(invoice => new
            {
                ProfileId = invoice.LinkedRentalBillingProfileId!.Value,
                RunId = invoice.LinkedRentalBillingRunId
            })
            .ToListAsync(ct);

        var transactionSettledAmounts = transactions
            .GroupBy(transaction => (transaction.ProfileId, RunId: NormalizeRunId(transaction.RunId)))
            .ToDictionary(group => group.Key, group => group.Sum(transaction => transaction.Amount));
        var directPaymentSettledAmounts = directPayments
            .Where(payment => !transactionKeys.Contains((payment.Id, payment.ProfileId)))
            .GroupBy(payment => (payment.ProfileId, RunId: NormalizeRunId(payment.RunId)))
            .ToDictionary(group => group.Key, group => group.Sum(payment => payment.Amount));
        var invoicedRunKeys = invoices
            .GroupBy(invoice => (invoice.ProfileId, RunId: NormalizeRunId(invoice.RunId)))
            .Select(group => group.Key)
            .ToHashSet();

        var issues = new List<DataIntegrityIssueDetail>();
        foreach (var profile in profiles)
        {
            var runs = ParseRentalBillingRuns(profile.BillingRunsJson);
            var activeRuns = runs
                .Where(run =>
                    run.RunId != Guid.Empty &&
                    !RentalBillingRunIdentityPolicy.IsSuppressedByTombstone(runs, run))
                .OrderByDescending(run => run.ScheduledDate)
                .ThenByDescending(run => run.PeriodEndDate)
                .ToList();
            if (activeRuns.Count == 0)
                continue;

            var activeRunIds = new HashSet<Guid>(
                transactionSettledAmounts
                    .Where(pair => pair.Key.ProfileId == profile.Id && pair.Value > 0m && pair.Key.RunId != Guid.Empty)
                    .Select(pair => pair.Key.RunId)
                    .Concat(directPaymentSettledAmounts
                        .Where(pair => pair.Key.ProfileId == profile.Id && pair.Value > 0m && pair.Key.RunId != Guid.Empty)
                        .Select(pair => pair.Key.RunId))
                    .Concat(invoicedRunKeys
                        .Where(key => key.ProfileId == profile.Id && key.RunId != Guid.Empty)
                        .Select(key => key.RunId)));

            var representativeRun = activeRuns.FirstOrDefault(run => activeRunIds.Contains(run.RunId)) ?? activeRuns.First();
            var runId = NormalizeRunId(representativeRun.RunId);
            var key = (profile.Id, RunId: runId);
            transactionSettledAmounts.TryGetValue(key, out var transactionAmount);
            directPaymentSettledAmounts.TryGetValue(key, out var directPaymentAmount);
            var expectedBilledAmount = Math.Max(0m, representativeRun.BilledAmount);
            var expectedSettledAmount = transactionAmount + directPaymentAmount;
            var expectedOutstandingAmount = Math.Max(0m, expectedBilledAmount - expectedSettledAmount);
            if (!AmountDiffers(profile.SettledAmount, expectedSettledAmount) &&
                !AmountDiffers(profile.OutstandingAmount, expectedOutstandingAmount))
            {
                continue;
            }

            var profileDisplay = BuildProfileDisplay(profile);
            AddGeneralIssue(
                issues,
                DataIntegrityIssueCodes.RentalBillingProfileSummaryMismatch,
                entityType: "렌탈 청구 프로필",
                entityId: profile.Id,
                customerName: profile.CustomerName,
                officeCode: ResolveProfileOfficeCode(profile),
                currentValue: $"프로필 저장 정산 {profile.SettledAmount:N0} / 저장 미수 {profile.OutstandingAmount:N0}",
                expectedValue: $"대표 run 실제 정산 {expectedSettledAmount:N0} / 실제 미수 {expectedOutstandingAmount:N0}",
                message: $"{profileDisplay}의 프로필 요약 정산/미수금액이 대표 청구 run 실제 입금 근거와 다릅니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenRentalBillingProfile,
                relatedEntityIds: new[] { runId },
                reviewInfo: string.Join(" / ", new[]
                {
                    $"ProfileId {profile.Id:D}",
                    $"RunId {runId:D}",
                    $"RunKey {NormalizeDisplay(representativeRun.RunKey, "-")}",
                    $"Billed {expectedBilledAmount:N0}",
                    $"ProfileSettled {profile.SettledAmount:N0}",
                    $"ExpectedSettled {expectedSettledAmount:N0}",
                    $"ProfileOutstanding {profile.OutstandingAmount:N0}",
                    $"ExpectedOutstanding {expectedOutstandingAmount:N0}",
                    $"Transaction {transactionAmount:N0}",
                    $"DirectPayment {directPaymentAmount:N0}",
                    string.IsNullOrWhiteSpace(profile.BillingStatus) ? "ProfileBillingStatus -" : $"ProfileBillingStatus {profile.BillingStatus}",
                    string.IsNullOrWhiteSpace(profile.SettlementStatus) ? "ProfileSettlementStatus -" : $"ProfileSettlementStatus {profile.SettlementStatus}",
                    string.IsNullOrWhiteSpace(profile.CompletionStatus) ? "ProfileCompletionStatus -" : $"ProfileCompletionStatus {profile.CompletionStatus}",
                    string.IsNullOrWhiteSpace(representativeRun.Status) ? "RunStatus -" : $"RunStatus {representativeRun.Status}",
                    string.IsNullOrWhiteSpace(representativeRun.SettlementStatus) ? "RunSettlementStatus -" : $"RunSettlementStatus {representativeRun.SettlementStatus}"
                }));
        }

        return issues;
    }

    private async Task<List<DataIntegrityIssueDetail>> LoadHardMissingChildReferenceIssuesAsync(SessionState session, CancellationToken ct)
    {
        var issues = new List<DataIntegrityIssueDetail>();
        var fallbackOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, DomainConstants.OfficeUsenet);

        if (session.HasAdministrativePrivileges)
        {
            var contracts = await (
                    from contract in _db.CustomerContracts.IgnoreQueryFilters().AsNoTracking()
                    join customer in _db.Customers.IgnoreQueryFilters().AsNoTracking()
                        on contract.CustomerId equals customer.Id into customerGroup
                    from customer in customerGroup.DefaultIfEmpty()
                    where customer == null
                    orderby contract.UploadedAtUtc, contract.Id
                    select contract)
                .ToListAsync(ct);

            foreach (var contract in contracts)
            {
                if (!session.HasGlobalDataScope)
                    continue;

                AddGeneralIssue(
                    issues,
                    DataIntegrityIssueCodes.CustomerContractMissingCustomerReference,
                    entityType: "거래처 계약/첨부",
                    entityId: contract.Id,
                    officeCode: fallbackOfficeCode,
                    currentValue: $"CustomerId {contract.CustomerId:D} / 파일 {NormalizeDisplay(contract.FileName, "파일명 없음")} / 삭제상태 {(contract.IsDeleted ? "삭제" : "활성")}",
                    expectedValue: "참조 거래처 행 존재",
                    message: $"{NormalizeDisplay(contract.FileName, "거래처 계약/첨부")} 행 {contract.Id:N}의 거래처 참조가 현재 로컬 DB에 없습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenSyncDiagnostics);
            }

            var transactionAttachments = await (
                    from attachment in _db.TransactionAttachments.IgnoreQueryFilters().AsNoTracking()
                    join transaction in _db.Transactions.IgnoreQueryFilters().AsNoTracking()
                        on attachment.TransactionId equals transaction.Id into transactionGroup
                    from transaction in transactionGroup.DefaultIfEmpty()
                    where transaction == null
                    orderby attachment.UploadedAtUtc, attachment.Id
                    select attachment)
                .ToListAsync(ct);

            foreach (var attachment in transactionAttachments)
            {
                if (!session.HasGlobalDataScope)
                    continue;

                AddGeneralIssue(
                    issues,
                    DataIntegrityIssueCodes.TransactionAttachmentMissingTransactionReference,
                    entityType: "거래첨부",
                    entityId: attachment.Id,
                    officeCode: fallbackOfficeCode,
                    currentValue: $"TransactionId {attachment.TransactionId:D} / 파일 {NormalizeDisplay(attachment.FileName, "파일명 없음")} / 삭제상태 {(attachment.IsDeleted ? "삭제" : "활성")}",
                    expectedValue: "참조 거래내역 행 존재",
                    message: $"{NormalizeDisplay(attachment.FileName, "거래첨부")} 행 {attachment.Id:N}의 거래내역 참조가 현재 로컬 DB에 없습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenSyncDiagnostics);
            }

            var transferLines = await (
                    from line in _db.InventoryTransferLines.IgnoreQueryFilters().AsNoTracking()
                    join transfer in _db.InventoryTransfers.IgnoreQueryFilters().AsNoTracking()
                        on line.TransferId equals transfer.Id into transferGroup
                    from transfer in transferGroup.DefaultIfEmpty()
                    where transfer == null
                    orderby line.TransferId, line.ItemNameOriginal, line.Id
                    select new
                    {
                        line.Id,
                        line.TransferId,
                        line.ItemId,
                        line.ItemNameOriginal,
                        line.Quantity,
                        line.IsDeleted
                    })
                .ToListAsync(ct);

            var transferLineItemIds = transferLines
                .Where(line => line.ItemId.HasValue && line.ItemId.Value != Guid.Empty)
                .Select(line => line.ItemId!.Value)
                .Distinct()
                .ToArray();
            var transferLineItemsById = await _db.Items.IgnoreQueryFilters().AsNoTracking()
                .Where(item => transferLineItemIds.Contains(item.Id))
                .Select(item => new
                {
                    item.Id,
                    item.TenantCode,
                    item.OfficeCode
                })
                .ToDictionaryAsync(item => item.Id, ct);
            foreach (var line in transferLines)
            {
                transferLineItemsById.TryGetValue(line.ItemId ?? Guid.Empty, out var itemScope);
                var scopeEvidence = itemScope is null
                    ? default
                    : ResolveStoredScopeEvidence(
                        itemScope.TenantCode,
                        responsibleOfficeCode: null,
                        ownerOfficeCode: itemScope.OfficeCode);
                if (!CanReadStoredScopeEvidenceForIntegrity(session, scopeEvidence))
                    continue;

                var issueOfficeCode = ResolveIssueOfficeCodeForStoredScope(
                    scopeEvidence,
                    session,
                    fallbackOfficeCode);
                var reviewInfo = string.Join(" / ", new[]
                {
                    line.ItemId.HasValue ? $"ItemId {line.ItemId.Value:D}" : "ItemId 없음",
                    scopeEvidence.HasEvidence ? $"ScopeTenant {scopeEvidence.TenantCode}" : "ScopeTenant -",
                    scopeEvidence.HasOfficeEvidence ? $"ScopeOffice {scopeEvidence.OfficeCode}" : "ScopeOffice -"
                });
                AddGeneralIssue(
                    issues,
                    DataIntegrityIssueCodes.InventoryTransferLineMissingTransferReference,
                    entityType: "재고이동 세부내역",
                    entityId: line.Id,
                    itemName: line.ItemNameOriginal,
                    officeCode: issueOfficeCode,
                    currentValue: $"TransferId {line.TransferId:D} / 요청수량 {line.Quantity:N2} / 삭제상태 {(line.IsDeleted ? "삭제" : "활성")}",
                    expectedValue: "참조 재고이동 문서 행 존재",
                    message: $"{NormalizeDisplay(line.ItemNameOriginal, "재고이동 세부내역")} 행 {line.Id:N}의 재고이동 문서 참조가 현재 로컬 DB에 없습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenSyncDiagnostics,
                    reviewInfo: reviewInfo);
            }
        }

        var rentalLogs = await (
                from log in _db.RentalBillingLogs.IgnoreQueryFilters().AsNoTracking()
                join profile in _db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                    on log.BillingProfileId equals profile.Id into profileGroup
                from profile in profileGroup.DefaultIfEmpty()
                where profile == null
                orderby log.BillingYearMonth, log.ScheduledDate, log.Id
                select log)
            .ToListAsync(ct);

        foreach (var log in rentalLogs)
        {
            var scopeEvidence = ResolveStoredScopeEvidence(
                log.TenantCode,
                log.ResponsibleOfficeCode,
                log.OfficeCode);
            if (!CanReadStoredScopeEvidenceForIntegrity(session, scopeEvidence))
                continue;

            var scopeTenantCode = scopeEvidence.HasEvidence ? scopeEvidence.TenantCode : string.Empty;
            var scopeOfficeCode = scopeEvidence.HasOfficeEvidence ? scopeEvidence.OfficeCode : string.Empty;
            var issueOfficeCode = ResolveIssueOfficeCodeForStoredScope(
                scopeEvidence,
                session,
                fallbackOfficeCode);

            var reviewInfo = string.Join(" / ", new[]
            {
                $"BillingProfileId {log.BillingProfileId:D}",
                $"TenantCode {NormalizeDisplay(log.TenantCode, "-")}",
                $"OfficeCode {NormalizeDisplay(log.OfficeCode, "-")}",
                $"ResponsibleOfficeCode {NormalizeDisplay(log.ResponsibleOfficeCode, "-")}",
                $"ScopeTenant {scopeTenantCode}",
                $"ScopeOffice {scopeOfficeCode}"
            });

            AddGeneralIssue(
                issues,
                DataIntegrityIssueCodes.RentalBillingLogMissingProfileReference,
                entityType: "렌탈 청구로그",
                entityId: log.Id,
                officeCode: issueOfficeCode,
                currentValue: $"BillingProfileId {log.BillingProfileId:D} / 청구월 {NormalizeDisplay(log.BillingYearMonth, "미지정")} / 삭제상태 {(log.IsDeleted ? "삭제" : "활성")}",
                expectedValue: "참조 청구 프로필 행 존재",
                message: $"{NormalizeDisplay(log.BillingYearMonth, "렌탈 청구월")} 청구로그 {log.Id:N}의 청구 프로필 참조가 현재 로컬 DB에 없습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenSyncDiagnostics,
                relatedEntityIds: [log.BillingProfileId],
                reviewInfo: reviewInfo);
        }

        return issues;
    }

    private static string ResolveRentalBillingLogScopeOfficeCode(LocalRentalBillingLog log)
    {
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(log.ResponsibleOfficeCode, out var responsibleOfficeCode))
            return responsibleOfficeCode;

        if (OfficeCodeCatalog.TryNormalizeOfficeCode(log.OfficeCode, out var ownerOfficeCode))
            return ownerOfficeCode;

        return TenantScopeCatalog.TryNormalizeTenantCode(log.TenantCode, out var tenantCode) &&
               string.Equals(tenantCode, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase)
            ? OfficeCodeCatalog.Itworld
            : OfficeCodeCatalog.Usenet;
    }

    private static (string TenantCode, string OfficeCode) ResolveAssignmentHistoryScope(
        LocalRentalAssetAssignmentHistory history,
        LocalRentalAsset? asset,
        LocalRentalBillingProfile? profile)
    {
        string scopeOfficeCode;
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(history.ResponsibleOfficeCode, out var responsibleOfficeCode))
        {
            scopeOfficeCode = responsibleOfficeCode;
        }
        else if (asset is not null)
        {
            scopeOfficeCode = ResolveAssetOfficeCode(asset);
        }
        else if (profile is not null)
        {
            scopeOfficeCode = ResolveProfileOfficeCode(profile);
        }
        else
        {
            scopeOfficeCode = TenantScopeCatalog.TryNormalizeTenantCode(history.TenantCode, out var tenantCode) &&
                              string.Equals(tenantCode, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase)
                ? OfficeCodeCatalog.Itworld
                : OfficeCodeCatalog.Usenet;
        }

        var scopeTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            history.TenantCode,
            scopeOfficeCode,
            history.TenantCode,
            scopeOfficeCode);
        return (scopeTenantCode, scopeOfficeCode);
    }

    private static (string TenantCode, string OfficeCode) ResolveInvoiceScope(IntegrityInvoiceSnapshot invoice)
    {
        string scopeOfficeCode;
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(invoice.ResponsibleOfficeCode, out var responsibleOfficeCode))
        {
            scopeOfficeCode = responsibleOfficeCode;
        }
        else if (OfficeCodeCatalog.TryNormalizeOfficeCode(invoice.OfficeCode, out var ownerOfficeCode))
        {
            scopeOfficeCode = ownerOfficeCode;
        }
        else
        {
            scopeOfficeCode = TenantScopeCatalog.TryNormalizeTenantCode(invoice.TenantCode, out var tenantCode) &&
                              string.Equals(tenantCode, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase)
                ? OfficeCodeCatalog.Itworld
                : OfficeCodeCatalog.Usenet;
        }

        var scopeTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            invoice.TenantCode,
            scopeOfficeCode,
            invoice.TenantCode,
            scopeOfficeCode);
        return (scopeTenantCode, scopeOfficeCode);
    }

    private static (string TenantCode, string OfficeCode) ResolveInvoiceScope(LocalInvoice invoice)
    {
        string scopeOfficeCode;
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(invoice.ResponsibleOfficeCode, out var responsibleOfficeCode))
        {
            scopeOfficeCode = responsibleOfficeCode;
        }
        else if (OfficeCodeCatalog.TryNormalizeOfficeCode(invoice.OfficeCode, out var ownerOfficeCode))
        {
            scopeOfficeCode = ownerOfficeCode;
        }
        else
        {
            scopeOfficeCode = TenantScopeCatalog.TryNormalizeTenantCode(invoice.TenantCode, out var tenantCode) &&
                              string.Equals(tenantCode, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase)
                ? OfficeCodeCatalog.Itworld
                : OfficeCodeCatalog.Usenet;
        }

        var scopeTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            invoice.TenantCode,
            scopeOfficeCode,
            invoice.TenantCode,
            scopeOfficeCode);
        return (scopeTenantCode, scopeOfficeCode);
    }

    private static (string TenantCode, string OfficeCode) ResolveEntityOfficeScope(
        string? tenantCode,
        string? ownerOfficeCode,
        string? responsibleOfficeCode)
    {
        string scopeOfficeCode;
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(responsibleOfficeCode, out var normalizedResponsibleOfficeCode))
        {
            scopeOfficeCode = normalizedResponsibleOfficeCode;
        }
        else if (OfficeCodeCatalog.TryNormalizeOfficeCode(ownerOfficeCode, out var normalizedOwnerOfficeCode))
        {
            scopeOfficeCode = normalizedOwnerOfficeCode;
        }
        else
        {
            scopeOfficeCode = TenantScopeCatalog.TryNormalizeTenantCode(tenantCode, out var normalizedTenantCode) &&
                              string.Equals(normalizedTenantCode, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase)
                ? OfficeCodeCatalog.Itworld
                : OfficeCodeCatalog.Usenet;
        }

        var scopeTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            tenantCode,
            scopeOfficeCode,
            tenantCode,
            scopeOfficeCode);
        return (scopeTenantCode, scopeOfficeCode);
    }

    private static (string TenantCode, string OfficeCode) ResolveTransactionScope(LocalTransaction transaction)
    {
        string scopeOfficeCode;
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(transaction.ResponsibleOfficeCode, out var responsibleOfficeCode))
        {
            scopeOfficeCode = responsibleOfficeCode;
        }
        else if (OfficeCodeCatalog.TryNormalizeOfficeCode(transaction.OfficeCode, out var ownerOfficeCode))
        {
            scopeOfficeCode = ownerOfficeCode;
        }
        else
        {
            scopeOfficeCode = TenantScopeCatalog.TryNormalizeTenantCode(transaction.TenantCode, out var tenantCode) &&
                              string.Equals(tenantCode, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase)
                ? OfficeCodeCatalog.Itworld
                : OfficeCodeCatalog.Usenet;
        }

        var scopeTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            transaction.TenantCode,
            scopeOfficeCode,
            transaction.TenantCode,
            scopeOfficeCode);
        return (scopeTenantCode, scopeOfficeCode);
    }

    private static (string TenantCode, string OfficeCode) ResolveItemScope(LocalItem item)
    {
        string scopeOfficeCode;
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(item.OfficeCode, out var itemOfficeCode))
        {
            scopeOfficeCode = itemOfficeCode;
        }
        else
        {
            scopeOfficeCode = TenantScopeCatalog.TryNormalizeTenantCode(item.TenantCode, out var tenantCode) &&
                              string.Equals(tenantCode, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase)
                ? OfficeCodeCatalog.Itworld
                : OfficeCodeCatalog.Usenet;
        }

        var scopeTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            item.TenantCode,
            scopeOfficeCode,
            item.TenantCode,
            scopeOfficeCode);
        return (scopeTenantCode, scopeOfficeCode);
    }

    private static string BuildItemScopeReviewInfo(LocalItem item, (string TenantCode, string OfficeCode) itemScope)
        => string.Join(" / ", new[]
        {
            $"TenantCode {NormalizeDisplay(item.TenantCode, "-")}",
            $"OfficeCode {NormalizeDisplay(item.OfficeCode, "-")}",
            $"ScopeTenant {itemScope.TenantCode}",
            $"ScopeOffice {itemScope.OfficeCode}"
        });

    private static string BuildItemScopeReviewInfo(Guid itemId, (string TenantCode, string OfficeCode) itemScope)
        => string.Join(" / ", new[]
        {
            $"ItemId {itemId:D}",
            $"ScopeTenant {itemScope.TenantCode}",
            $"ScopeOffice {itemScope.OfficeCode}"
        });

    private async Task<List<LocalItem>> LoadDeletedIntegrityItemsByIdsAsync(
        IReadOnlyCollection<Guid> itemIds,
        SessionState session,
        CancellationToken ct)
    {
        var ids = itemIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var rows = new List<LocalItem>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds.ToList();
            rows.AddRange(await SelectIntegrityItemProjection(ApplyOperationalAlertItemScopePrefilter(
                    _db.Items
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .Where(item => item.IsDeleted && scopedBatchIds.Contains(item.Id)),
                    session))
                .ToListAsync(ct));
        }

        return rows;
    }

    private async Task<List<LocalInventoryMovement>> LoadInventoryMovementsForItemsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken ct)
    {
        var ids = itemIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var rows = new List<LocalInventoryMovement>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds.ToList();
            rows.AddRange(await _db.InventoryMovements
                .AsNoTracking()
                .Where(movement =>
                    movement.IsActive &&
                    movement.ItemId.HasValue &&
                    scopedBatchIds.Contains(movement.ItemId.Value))
                .Select(movement => new LocalInventoryMovement
                {
                    ItemId = movement.ItemId,
                    WarehouseCode = movement.WarehouseCode,
                    IsActive = movement.IsActive
                })
                .ToListAsync(ct));
        }

        return rows;
    }

    private async Task<Dictionary<Guid, decimal>> LoadInvoiceLineTotalsForInvoicesAsync(
        IReadOnlyCollection<Guid> invoiceIds,
        CancellationToken ct)
    {
        var ids = invoiceIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var rows = new Dictionary<Guid, decimal>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds.ToList();
            var batchRows = await _db.InvoiceLines
                .AsNoTracking()
                .Where(line => scopedBatchIds.Contains(line.InvoiceId) && !line.IsDeleted)
                .GroupBy(line => line.InvoiceId)
                .Select(group => new
                {
                    InvoiceId = group.Key,
                    TotalAmount = group.Sum(line => (double)line.LineAmount)
                })
                .ToListAsync(ct);

            foreach (var row in batchRows)
                rows[row.InvoiceId] = (decimal)row.TotalAmount;
        }

        return rows;
    }

    private async Task<Dictionary<Guid, decimal>> LoadInvoicePaymentTotalsForInvoicesAsync(
        IReadOnlyCollection<Guid> invoiceIds,
        CancellationToken ct)
    {
        var ids = invoiceIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var rows = new Dictionary<Guid, decimal>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds.ToList();
            var batchRows = await _db.Payments
                .AsNoTracking()
                .Where(payment => scopedBatchIds.Contains(payment.InvoiceId) && !payment.IsDeleted)
                .GroupBy(payment => payment.InvoiceId)
                .Select(group => new
                {
                    InvoiceId = group.Key,
                    TotalAmount = group.Sum(payment => (double)payment.Amount)
                })
                .ToListAsync(ct);

            foreach (var row in batchRows)
                rows[row.InvoiceId] = (decimal)row.TotalAmount;
        }

        return rows;
    }

    private async Task<Dictionary<Guid, CustomerDuplicateUsage>> LoadCustomerDuplicateUsagesAsync(
        IReadOnlyCollection<Guid> customerIds,
        CancellationToken ct)
    {
        var ids = customerIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var usages = ids.ToDictionary(id => id, _ => new CustomerDuplicateUsage());
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds.ToList();

            foreach (var row in await _db.Invoices.IgnoreQueryFilters()
                         .AsNoTracking()
                         .Where(invoice => !invoice.IsDeleted && scopedBatchIds.Contains(invoice.CustomerId))
                         .GroupBy(invoice => invoice.CustomerId)
                         .Select(group => new ReferenceCountRow { EntityId = group.Key, Count = group.Count() })
                         .ToListAsync(ct))
            {
                if (usages.TryGetValue(row.EntityId, out var usage))
                    usage.InvoiceCount = row.Count;
            }

            foreach (var row in await _db.Transactions.IgnoreQueryFilters()
                         .AsNoTracking()
                         .Where(transaction => !transaction.IsDeleted && scopedBatchIds.Contains(transaction.CustomerId))
                         .GroupBy(transaction => transaction.CustomerId)
                         .Select(group => new ReferenceCountRow { EntityId = group.Key, Count = group.Count() })
                         .ToListAsync(ct))
            {
                if (usages.TryGetValue(row.EntityId, out var usage))
                    usage.TransactionCount = row.Count;
            }

            foreach (var row in await _db.RentalBillingProfiles.IgnoreQueryFilters()
                         .AsNoTracking()
                         .Where(profile => !profile.IsDeleted && profile.CustomerId.HasValue && scopedBatchIds.Contains(profile.CustomerId.Value))
                         .GroupBy(profile => profile.CustomerId!.Value)
                         .Select(group => new ReferenceCountRow { EntityId = group.Key, Count = group.Count() })
                         .ToListAsync(ct))
            {
                if (usages.TryGetValue(row.EntityId, out var usage))
                    usage.RentalBillingProfileCount = row.Count;
            }

            foreach (var row in await _db.RentalAssets.IgnoreQueryFilters()
                         .AsNoTracking()
                         .Where(asset => !asset.IsDeleted && asset.CustomerId.HasValue && scopedBatchIds.Contains(asset.CustomerId.Value))
                         .GroupBy(asset => asset.CustomerId!.Value)
                         .Select(group => new ReferenceCountRow { EntityId = group.Key, Count = group.Count() })
                         .ToListAsync(ct))
            {
                if (usages.TryGetValue(row.EntityId, out var usage))
                    usage.RentalAssetCount = row.Count;
            }

            foreach (var row in await _db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                         .AsNoTracking()
                         .Where(history => !history.IsDeleted && history.CustomerId.HasValue && scopedBatchIds.Contains(history.CustomerId.Value))
                         .GroupBy(history => history.CustomerId!.Value)
                         .Select(group => new ReferenceCountRow { EntityId = group.Key, Count = group.Count() })
                         .ToListAsync(ct))
            {
                if (usages.TryGetValue(row.EntityId, out var usage))
                    usage.RentalAssignmentHistoryCount = row.Count;
            }

            foreach (var row in await _db.CustomerContracts.IgnoreQueryFilters()
                         .AsNoTracking()
                         .Where(contract => !contract.IsDeleted && scopedBatchIds.Contains(contract.CustomerId))
                         .GroupBy(contract => contract.CustomerId)
                         .Select(group => new ReferenceCountRow { EntityId = group.Key, Count = group.Count() })
                         .ToListAsync(ct))
            {
                if (usages.TryGetValue(row.EntityId, out var usage))
                    usage.CustomerContractCount = row.Count;
            }
        }

        return usages;
    }

    private async Task<Dictionary<Guid, ItemDuplicateUsage>> LoadItemDuplicateUsagesAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken ct)
    {
        var ids = itemIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var usages = ids.ToDictionary(id => id, _ => new ItemDuplicateUsage());
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedBatchIds = batchIds.ToList();

            foreach (var row in await _db.InvoiceLines.IgnoreQueryFilters()
                         .AsNoTracking()
                         .Where(line => !line.IsDeleted && line.ItemId.HasValue && scopedBatchIds.Contains(line.ItemId.Value))
                         .GroupBy(line => line.ItemId!.Value)
                         .Select(group => new ReferenceCountRow { EntityId = group.Key, Count = group.Count() })
                         .ToListAsync(ct))
            {
                if (usages.TryGetValue(row.EntityId, out var usage))
                    usage.InvoiceLineCount = row.Count;
            }

            foreach (var row in await _db.InvoiceLineSerials
                         .AsNoTracking()
                         .Where(serial => serial.ItemId.HasValue && scopedBatchIds.Contains(serial.ItemId.Value))
                         .GroupBy(serial => serial.ItemId!.Value)
                         .Select(group => new ReferenceCountRow { EntityId = group.Key, Count = group.Count() })
                         .ToListAsync(ct))
            {
                if (usages.TryGetValue(row.EntityId, out var usage))
                    usage.InvoiceLineSerialCount = row.Count;
            }

            foreach (var row in await _db.RentalAssets.IgnoreQueryFilters()
                         .AsNoTracking()
                         .Where(asset => !asset.IsDeleted && asset.ItemId.HasValue && scopedBatchIds.Contains(asset.ItemId.Value))
                         .GroupBy(asset => asset.ItemId!.Value)
                         .Select(group => new ReferenceCountRow { EntityId = group.Key, Count = group.Count() })
                         .ToListAsync(ct))
            {
                if (usages.TryGetValue(row.EntityId, out var usage))
                    usage.RentalAssetCount = row.Count;
            }

            foreach (var row in await _db.InventoryTransferLines.IgnoreQueryFilters()
                         .AsNoTracking()
                         .Where(line => !line.IsDeleted && line.ItemId.HasValue && scopedBatchIds.Contains(line.ItemId.Value))
                         .GroupBy(line => line.ItemId!.Value)
                         .Select(group => new ReferenceCountRow { EntityId = group.Key, Count = group.Count() })
                         .ToListAsync(ct))
            {
                if (usages.TryGetValue(row.EntityId, out var usage))
                    usage.InventoryTransferLineCount = row.Count;
            }

            foreach (var row in await _db.InventoryMovements
                         .AsNoTracking()
                         .Where(movement => movement.IsActive && movement.ItemId.HasValue && scopedBatchIds.Contains(movement.ItemId.Value))
                         .GroupBy(movement => movement.ItemId!.Value)
                         .Select(group => new ReferenceCountRow { EntityId = group.Key, Count = group.Count() })
                         .ToListAsync(ct))
            {
                if (usages.TryGetValue(row.EntityId, out var usage))
                    usage.InventoryMovementCount = row.Count;
            }

            foreach (var row in await _db.StockLayers
                         .AsNoTracking()
                         .Where(layer => layer.ItemId.HasValue && scopedBatchIds.Contains(layer.ItemId.Value))
                         .GroupBy(layer => layer.ItemId!.Value)
                         .Select(group => new ReferenceCountRow { EntityId = group.Key, Count = group.Count() })
                         .ToListAsync(ct))
            {
                if (usages.TryGetValue(row.EntityId, out var usage))
                    usage.StockLayerCount = row.Count;
            }

            foreach (var row in await _db.SerialLedgers
                         .AsNoTracking()
                         .Where(ledger => ledger.ItemId.HasValue && scopedBatchIds.Contains(ledger.ItemId.Value))
                         .GroupBy(ledger => ledger.ItemId!.Value)
                         .Select(group => new ReferenceCountRow { EntityId = group.Key, Count = group.Count() })
                         .ToListAsync(ct))
            {
                if (usages.TryGetValue(row.EntityId, out var usage))
                    usage.SerialLedgerCount = row.Count;
            }

            foreach (var row in await _db.ItemPriceGrades.IgnoreQueryFilters()
                         .AsNoTracking()
                         .Where(grade => scopedBatchIds.Contains(grade.ItemId))
                         .GroupBy(grade => grade.ItemId)
                         .Select(group => new ReferenceCountRow { EntityId = group.Key, Count = group.Count() })
                         .ToListAsync(ct))
            {
                if (usages.TryGetValue(row.EntityId, out var usage))
                    usage.ItemPriceGradeCount = row.Count;
            }

            var stockRows = await _db.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock => scopedBatchIds.Contains(stock.ItemId))
                .Select(stock => new ItemStockRow
                {
                    EntityId = stock.ItemId,
                    Quantity = stock.Quantity
                })
                .ToListAsync(ct);

            foreach (var group in stockRows.GroupBy(row => row.EntityId))
            {
                if (!usages.TryGetValue(group.Key, out var usage))
                    continue;

                usage.ItemWarehouseStockRowCount = group.Count();
                usage.ItemWarehouseStockQuantity = group.Sum(row => row.Quantity);
            }
        }

        var rentalBillingTemplateCounts = await LoadRentalBillingTemplateItemReferenceCountsAsync(ids, ct);
        foreach (var (itemId, count) in rentalBillingTemplateCounts)
        {
            if (usages.TryGetValue(itemId, out var usage))
                usage.RentalBillingTemplateCount = count;
        }

        await LoadItemDuplicateReferenceIdentityTokensAsync(usages, ids, ct);

        return usages;
    }

    private async Task LoadItemDuplicateReferenceIdentityTokensAsync(
        IReadOnlyDictionary<Guid, ItemDuplicateUsage> usages,
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken ct)
    {
        void Add(Guid itemId, object token)
        {
            if (usages.TryGetValue(itemId, out var usage))
                usage.ReferenceIdentityTokens.Add(JsonSerializer.Serialize(token, JsonOptions));
        }

        foreach (var batchIds in itemIds.Where(id => id != Guid.Empty).Distinct().Chunk(LocalQueryContainsBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            var scopedIds = batchIds.ToList();

            foreach (var row in await _db.InvoiceLines.IgnoreQueryFilters()
                         .AsNoTracking()
                         .Where(line => !line.IsDeleted && line.ItemId.HasValue && scopedIds.Contains(line.ItemId.Value))
                         .Select(line => new { ItemId = line.ItemId!.Value, line.Id, line.InvoiceId })
                         .ToListAsync(ct))
            {
                Add(row.ItemId, new { Type = "InvoiceLine", row.Id, row.InvoiceId });
            }

            foreach (var row in await _db.InvoiceLineSerials
                         .AsNoTracking()
                         .Where(serial => serial.ItemId.HasValue && scopedIds.Contains(serial.ItemId.Value))
                         .Select(serial => new { ItemId = serial.ItemId!.Value, serial.Id, serial.InvoiceId, serial.InvoiceLineId })
                         .ToListAsync(ct))
            {
                Add(row.ItemId, new { Type = "InvoiceLineSerial", row.Id, row.InvoiceId, row.InvoiceLineId });
            }

            foreach (var row in await _db.RentalAssets.IgnoreQueryFilters()
                         .AsNoTracking()
                         .Where(asset => !asset.IsDeleted && asset.ItemId.HasValue && scopedIds.Contains(asset.ItemId.Value))
                         .Select(asset => new
                         {
                             ItemId = asset.ItemId!.Value,
                             asset.Id,
                             asset.Revision,
                             asset.UpdatedAtUtc,
                             asset.IsDirty,
                             asset.TenantCode,
                             asset.OfficeCode,
                             asset.ResponsibleOfficeCode
                         })
                         .ToListAsync(ct))
            {
                Add(row.ItemId, new
                {
                    Type = "RentalAsset",
                    row.Id,
                    row.Revision,
                    UpdatedAtUtcTicks = row.UpdatedAtUtc.ToUniversalTime().Ticks,
                    row.IsDirty,
                    row.TenantCode,
                    row.OfficeCode,
                    row.ResponsibleOfficeCode
                });
            }

            foreach (var row in await _db.InventoryTransferLines.IgnoreQueryFilters()
                         .AsNoTracking()
                         .Where(line => !line.IsDeleted && line.ItemId.HasValue && scopedIds.Contains(line.ItemId.Value))
                         .Select(line => new { ItemId = line.ItemId!.Value, line.Id, line.TransferId })
                         .ToListAsync(ct))
            {
                Add(row.ItemId, new { Type = "InventoryTransferLine", row.Id, row.TransferId });
            }

            foreach (var row in await _db.InventoryMovements
                         .AsNoTracking()
                         .Where(movement => movement.IsActive && movement.ItemId.HasValue && scopedIds.Contains(movement.ItemId.Value))
                         .Select(movement => new
                         {
                             ItemId = movement.ItemId!.Value,
                             movement.Id,
                             movement.InvoiceId,
                             movement.InvoiceLineId,
                             movement.WarehouseCode,
                             movement.IsActive
                         })
                         .ToListAsync(ct))
            {
                Add(row.ItemId, new
                {
                    Type = "InventoryMovement",
                    row.Id,
                    row.InvoiceId,
                    row.InvoiceLineId,
                    row.WarehouseCode,
                    row.IsActive
                });
            }

            foreach (var row in await _db.StockLayers
                         .AsNoTracking()
                         .Where(layer => layer.ItemId.HasValue && scopedIds.Contains(layer.ItemId.Value))
                         .Select(layer => new
                         {
                             ItemId = layer.ItemId!.Value,
                             layer.Id,
                             layer.SourceInvoiceId,
                             layer.SourceInvoiceLineId,
                             layer.WarehouseCode
                         })
                         .ToListAsync(ct))
            {
                Add(row.ItemId, new
                {
                    Type = "StockLayer",
                    row.Id,
                    row.SourceInvoiceId,
                    row.SourceInvoiceLineId,
                    row.WarehouseCode
                });
            }

            foreach (var row in await _db.SerialLedgers
                         .AsNoTracking()
                         .Where(ledger => ledger.ItemId.HasValue && scopedIds.Contains(ledger.ItemId.Value))
                         .Select(ledger => new { ItemId = ledger.ItemId!.Value, ledger.Id, ledger.WarehouseCode, ledger.UpdatedAtUtc })
                         .ToListAsync(ct))
            {
                Add(row.ItemId, new
                {
                    Type = "SerialLedger",
                    row.Id,
                    row.WarehouseCode,
                    UpdatedAtUtcTicks = row.UpdatedAtUtc.ToUniversalTime().Ticks
                });
            }

            foreach (var row in await _db.ItemPriceGrades.IgnoreQueryFilters()
                         .AsNoTracking()
                         .Where(grade => scopedIds.Contains(grade.ItemId))
                         .Select(grade => new
                         {
                             grade.ItemId,
                             grade.Id,
                             grade.Revision,
                             grade.UpdatedAtUtc,
                             grade.IsDirty,
                             grade.PriceGradeOptionId,
                             grade.IsActive,
                             grade.IsDeleted
                         })
                         .ToListAsync(ct))
            {
                Add(row.ItemId, new
                {
                    Type = "ItemPriceGrade",
                    row.Id,
                    row.Revision,
                    UpdatedAtUtcTicks = row.UpdatedAtUtc.ToUniversalTime().Ticks,
                    row.IsDirty,
                    row.PriceGradeOptionId,
                    row.IsActive,
                    row.IsDeleted
                });
            }

            foreach (var row in await _db.ItemWarehouseStocks
                         .AsNoTracking()
                         .Where(stock => scopedIds.Contains(stock.ItemId))
                         .Select(stock => new
                         {
                             stock.ItemId,
                             stock.WarehouseCode,
                             stock.Quantity,
                             stock.Revision,
                             stock.UpdatedAtUtc
                         })
                         .ToListAsync(ct))
            {
                Add(row.ItemId, new
                {
                    Type = "ItemWarehouseStock",
                    row.WarehouseCode,
                    row.Quantity,
                    row.Revision,
                    UpdatedAtUtcTicks = row.UpdatedAtUtc.ToUniversalTime().Ticks
                });
            }
        }

        var itemIdSet = itemIds.Where(id => id != Guid.Empty).ToHashSet();
        var profiles = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => !string.IsNullOrWhiteSpace(profile.BillingTemplateJson))
            .Select(profile => new
            {
                profile.Id,
                profile.Revision,
                profile.UpdatedAtUtc,
                profile.IsDirty,
                profile.IsDeleted,
                profile.BillingTemplateJson
            })
            .ToListAsync(ct);
        foreach (var profile in profiles)
        {
            List<RentalBillingTemplateItemModel>? templateItems;
            try
            {
                templateItems = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(
                    profile.BillingTemplateJson ?? "[]",
                    JsonOptions);
            }
            catch
            {
                continue;
            }

            if (templateItems is null)
                continue;

            for (var index = 0; index < templateItems.Count; index++)
            {
                var itemId = templateItems[index]?.CatalogItemId.GetValueOrDefault() ?? Guid.Empty;
                if (itemId == Guid.Empty || !itemIdSet.Contains(itemId))
                    continue;

                Add(itemId, new
                {
                    Type = "RentalBillingTemplate",
                    ProfileId = profile.Id,
                    TemplateIndex = index,
                    profile.Revision,
                    UpdatedAtUtcTicks = profile.UpdatedAtUtc.ToUniversalTime().Ticks,
                    profile.IsDirty,
                    profile.IsDeleted
                });
            }
        }
    }

    private async Task<Dictionary<Guid, int>> LoadItemUnresolvedOutboxCountsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken ct)
    {
        var ids = itemIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return [];

        var counts = new Dictionary<Guid, int>();
        foreach (var batchIds in ids.Chunk(LocalQueryContainsBatchSize))
        {
            var scopedIds = batchIds.ToList();
            var rows = await _db.SyncOutboxEntries
                .AsNoTracking()
                .Where(entry => scopedIds.Contains(entry.EntityId) && entry.Status != "Acknowledged")
                .Select(entry => new { entry.EntityId, entry.EntityName })
                .ToListAsync(ct);
            foreach (var group in rows
                         .Where(entry => IsItemOutboxEntityName(entry.EntityName))
                         .GroupBy(entry => entry.EntityId))
            {
                counts[group.Key] = counts.GetValueOrDefault(group.Key) + group.Count();
            }
        }

        return counts;
    }

    private static bool IsItemOutboxEntityName(string? entityName)
        => string.Equals(entityName, nameof(LocalItem), StringComparison.OrdinalIgnoreCase) ||
           string.Equals(entityName, "Item", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(entityName, nameof(ItemDto), StringComparison.OrdinalIgnoreCase);

    private static DataIntegrityItemDuplicateComparison BuildItemDuplicateComparison(
        IReadOnlyCollection<LocalItem> items,
        IReadOnlyDictionary<Guid, ItemDuplicateUsage> usageById,
        IReadOnlyDictionary<Guid, int> outboxCountById)
    {
        var activeItems = items
            .Where(item => !item.IsDeleted && item.Id != Guid.Empty)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .OrderBy(item => item.Id)
            .ToList();
        var recommended = activeItems
            .OrderByDescending(item => usageById.TryGetValue(item.Id, out var usage) ? usage.TotalCount : 0)
            .ThenByDescending(item => Math.Abs(item.CurrentStock))
            .ThenByDescending(CountFilledItemValues)
            .ThenBy(item => item.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Id)
            .FirstOrDefault();

        var candidates = activeItems.Select(item =>
        {
            var usage = usageById.TryGetValue(item.Id, out var foundUsage) ? foundUsage : ItemDuplicateUsage.Empty;
            var isRecommended = item.Id == recommended?.Id;
            return new DataIntegrityItemDuplicateCandidate
            {
                ItemId = item.Id,
                TenantCode = ResolveItemScope(item).TenantCode,
                OfficeCode = ResolveItemScope(item).OfficeCode,
                Revision = item.Revision,
                UpdatedAtUtc = item.UpdatedAtUtc,
                IsDirty = item.IsDirty,
                UnresolvedOutboxCount = outboxCountById.GetValueOrDefault(item.Id),
                NameOriginal = item.NameOriginal ?? string.Empty,
                SpecificationOriginal = item.SpecificationOriginal ?? string.Empty,
                CategoryName = item.CategoryName ?? string.Empty,
                ItemKind = item.ItemKind ?? string.Empty,
                TrackingType = item.TrackingType ?? string.Empty,
                Unit = item.Unit ?? string.Empty,
                BoxQuantity = item.BoxQuantity,
                StorageLocation = item.StorageLocation ?? string.Empty,
                CurrentStock = item.CurrentStock,
                WarehouseStock = usage.ItemWarehouseStockQuantity,
                SafetyStock = item.SafetyStock,
                PurchasePrice = item.PurchasePrice,
                SalePrice = item.SalePrice,
                RetailPrice = item.RetailPrice,
                PriceGradeA = item.PriceGradeA,
                PriceGradeB = item.PriceGradeB,
                PriceGradeC = item.PriceGradeC,
                LastPurchaseDate = item.LastPurchaseDate,
                LastSaleDate = item.LastSaleDate,
                CatalogExtensionSyncPending = item.CatalogExtensionSyncPending,
                SimpleMemo = item.SimpleMemo ?? string.Empty,
                IsRental = item.IsRental,
                IsSale = item.IsSale,
                SerialNumber = item.SerialNumber ?? string.Empty,
                MaterialNumber = item.MaterialNumber ?? string.Empty,
                InstallLocation = item.InstallLocation ?? string.Empty,
                RentalStartDate = item.RentalStartDate,
                RentalEndDate = item.RentalEndDate,
                Notes = item.Notes ?? string.Empty,
                InvoiceLineCount = usage.InvoiceLineCount,
                InvoiceLineSerialCount = usage.InvoiceLineSerialCount,
                RentalAssetCount = usage.RentalAssetCount,
                RentalBillingTemplateCount = usage.RentalBillingTemplateCount,
                InventoryTransferLineCount = usage.InventoryTransferLineCount,
                InventoryMovementCount = usage.InventoryMovementCount,
                StockLayerCount = usage.StockLayerCount,
                SerialLedgerCount = usage.SerialLedgerCount,
                ItemWarehouseStockRowCount = usage.ItemWarehouseStockRowCount,
                ItemPriceGradeCount = usage.ItemPriceGradeCount,
                TotalReferenceCount = usage.TotalCount,
                ReferenceIdentityFingerprint = usage.ReferenceIdentityFingerprint,
                IsRecommended = isRecommended,
                RecommendationText = isRecommended
                    ? "참조 수와 입력된 기준정보를 기준으로 한 권장 대표"
                    : "사용자가 대표로 선택 가능"
            };
        }).ToList();

        var conflictFields = new List<string>();
        var blockingReasons = new List<string>();
        void Block(string field, string reason)
        {
            if (!conflictFields.Contains(field, StringComparer.Ordinal))
                conflictFields.Add(field);
            if (!blockingReasons.Contains(reason, StringComparer.Ordinal))
                blockingReasons.Add(reason);
        }

        void BlockDifferentText(string field, string displayName, Func<DataIntegrityItemDuplicateCandidate, string> selector)
        {
            var values = candidates.Select(selector)
                .Select(value => (value ?? string.Empty).Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (values.Count > 1)
                Block(field, $"{displayName} 값이 서로 다름");
        }

        void BlockDifferentNumber(string field, string displayName, Func<DataIntegrityItemDuplicateCandidate, decimal> selector)
        {
            if (candidates.Select(selector).Where(value => value != 0m).Distinct().Skip(1).Any())
                Block(field, $"{displayName} 값이 서로 다름");
        }

        void BlockDifferentDate(string field, string displayName, Func<DataIntegrityItemDuplicateCandidate, DateOnly?> selector)
        {
            if (candidates.Select(selector).Where(value => value.HasValue).Distinct().Skip(1).Any())
                Block(field, $"{displayName} 값이 서로 다름");
        }

        if (candidates.Count <= 1)
            Block("Candidates", "활성 중복 후보가 2건 미만임");
        if (!BelongsToSingleExactItemDuplicateGroup(activeItems))
            Block("NameSpecificationScope", "품명/규격/scope가 하나의 중복 그룹이 아님");

        BlockDifferentText(nameof(DataIntegrityItemDuplicateCandidate.CategoryName), "분류", candidate => candidate.CategoryName);
        BlockDifferentText(nameof(DataIntegrityItemDuplicateCandidate.ItemKind), "품목 종류", candidate => candidate.ItemKind);
        BlockDifferentText(nameof(DataIntegrityItemDuplicateCandidate.TrackingType), "추적 방식", candidate => candidate.TrackingType);
        BlockDifferentText(nameof(DataIntegrityItemDuplicateCandidate.Unit), "단위", candidate => candidate.Unit);
        BlockDifferentText(nameof(DataIntegrityItemDuplicateCandidate.StorageLocation), "보관 위치", candidate => candidate.StorageLocation);
        BlockDifferentText(nameof(DataIntegrityItemDuplicateCandidate.SerialNumber), "시리얼 번호", candidate => candidate.SerialNumber);
        BlockDifferentText(nameof(DataIntegrityItemDuplicateCandidate.MaterialNumber), "자재 번호", candidate => candidate.MaterialNumber);
        BlockDifferentText(nameof(DataIntegrityItemDuplicateCandidate.InstallLocation), "설치 위치", candidate => candidate.InstallLocation);
        BlockDifferentText(nameof(DataIntegrityItemDuplicateCandidate.SimpleMemo), "간단 메모", candidate => candidate.SimpleMemo);
        BlockDifferentText(nameof(DataIntegrityItemDuplicateCandidate.Notes), "메모", candidate => candidate.Notes);
        BlockDifferentNumber(nameof(DataIntegrityItemDuplicateCandidate.BoxQuantity), "박스 수량", candidate => candidate.BoxQuantity);
        BlockDifferentNumber(nameof(DataIntegrityItemDuplicateCandidate.SafetyStock), "안전 재고", candidate => candidate.SafetyStock);
        BlockDifferentNumber(nameof(DataIntegrityItemDuplicateCandidate.PurchasePrice), "매입가", candidate => candidate.PurchasePrice);
        BlockDifferentNumber(nameof(DataIntegrityItemDuplicateCandidate.SalePrice), "매출가", candidate => candidate.SalePrice);
        BlockDifferentNumber(nameof(DataIntegrityItemDuplicateCandidate.RetailPrice), "소매가", candidate => candidate.RetailPrice);
        BlockDifferentNumber(nameof(DataIntegrityItemDuplicateCandidate.PriceGradeA), "A등급가", candidate => candidate.PriceGradeA);
        BlockDifferentNumber(nameof(DataIntegrityItemDuplicateCandidate.PriceGradeB), "B등급가", candidate => candidate.PriceGradeB);
        BlockDifferentNumber(nameof(DataIntegrityItemDuplicateCandidate.PriceGradeC), "C등급가", candidate => candidate.PriceGradeC);
        BlockDifferentDate(nameof(DataIntegrityItemDuplicateCandidate.LastPurchaseDate), "최근 매입일", candidate => candidate.LastPurchaseDate);
        BlockDifferentDate(nameof(DataIntegrityItemDuplicateCandidate.LastSaleDate), "최근 매출일", candidate => candidate.LastSaleDate);
        BlockDifferentDate(nameof(DataIntegrityItemDuplicateCandidate.RentalStartDate), "대여 시작일", candidate => candidate.RentalStartDate);
        BlockDifferentDate(nameof(DataIntegrityItemDuplicateCandidate.RentalEndDate), "대여 종료일", candidate => candidate.RentalEndDate);

        if (candidates.Select(candidate => candidate.IsRental).Distinct().Skip(1).Any())
            Block(nameof(DataIntegrityItemDuplicateCandidate.IsRental), "대여 여부가 서로 다름");
        if (candidates.Select(candidate => candidate.IsSale).Distinct().Skip(1).Any())
            Block(nameof(DataIntegrityItemDuplicateCandidate.IsSale), "판매 여부가 서로 다름");
        if (candidates.Any(candidate => candidate.CurrentStock != 0m))
            Block(nameof(DataIntegrityItemDuplicateCandidate.CurrentStock), "현재 재고가 0이 아닌 후보가 있음");
        if (candidates.Any(candidate => candidate.WarehouseStock != 0m))
            Block(nameof(DataIntegrityItemDuplicateCandidate.WarehouseStock), "창고별 재고가 0이 아닌 후보가 있음");
        if (candidates.Any(candidate => candidate.IsDirty))
            Block(nameof(DataIntegrityItemDuplicateCandidate.IsDirty), "동기화되지 않은 dirty 후보가 있음");
        if (candidates.Any(candidate => candidate.UnresolvedOutboxCount > 0))
            Block(nameof(DataIntegrityItemDuplicateCandidate.UnresolvedOutboxCount), "미해결 품목 outbox가 있음");
        if (candidates.Any(candidate => candidate.CatalogExtensionSyncPending))
            Block(nameof(DataIntegrityItemDuplicateCandidate.CatalogExtensionSyncPending), "품목 확장정보 동기화가 대기 중임");
        if (candidates.Any(candidate => candidate.ItemPriceGradeCount > 0))
            Block(nameof(DataIntegrityItemDuplicateCandidate.ItemPriceGradeCount), "사용자 등급가가 연결된 후보가 있어 원본 품목에서 수동 정리가 필요함");

        var snapshotToken = BuildItemDuplicateSnapshotToken(candidates);
        return new DataIntegrityItemDuplicateComparison
        {
            Candidates = candidates,
            SnapshotToken = snapshotToken,
            CanMerge = blockingReasons.Count == 0,
            BlockingConflictFields = conflictFields,
            BlockingReasons = blockingReasons,
            RecommendedCanonicalId = recommended?.Id,
            SummaryText = $"후보 {candidates.Count:N0}건 / 참조 {candidates.Sum(candidate => candidate.TotalReferenceCount):N0}건 / 현재재고 {candidates.Sum(candidate => candidate.CurrentStock):N2} / 창고재고 {candidates.Sum(candidate => candidate.WarehouseStock):N2}"
        };
    }

    private static string BuildItemDuplicateSnapshotToken(IReadOnlyCollection<DataIntegrityItemDuplicateCandidate> candidates)
    {
        var snapshot = candidates.OrderBy(candidate => candidate.ItemId).Select(candidate => new
        {
            candidate.ItemId,
            candidate.TenantCode,
            candidate.OfficeCode,
            candidate.Revision,
            UpdatedAtUtcTicks = candidate.UpdatedAtUtc.ToUniversalTime().Ticks,
            candidate.IsDirty,
            candidate.UnresolvedOutboxCount,
            candidate.NameOriginal,
            candidate.SpecificationOriginal,
            candidate.CategoryName,
            candidate.ItemKind,
            candidate.TrackingType,
            candidate.Unit,
            candidate.BoxQuantity,
            candidate.StorageLocation,
            candidate.CurrentStock,
            candidate.WarehouseStock,
            candidate.SafetyStock,
            candidate.PurchasePrice,
            candidate.SalePrice,
            candidate.RetailPrice,
            candidate.PriceGradeA,
            candidate.PriceGradeB,
            candidate.PriceGradeC,
            candidate.LastPurchaseDate,
            candidate.LastSaleDate,
            candidate.CatalogExtensionSyncPending,
            candidate.SimpleMemo,
            candidate.IsRental,
            candidate.IsSale,
            candidate.SerialNumber,
            candidate.MaterialNumber,
            candidate.InstallLocation,
            candidate.RentalStartDate,
            candidate.RentalEndDate,
            candidate.Notes,
            candidate.InvoiceLineCount,
            candidate.InvoiceLineSerialCount,
            candidate.RentalAssetCount,
            candidate.RentalBillingTemplateCount,
            candidate.InventoryTransferLineCount,
            candidate.InventoryMovementCount,
            candidate.StockLayerCount,
            candidate.SerialLedgerCount,
            candidate.ItemWarehouseStockRowCount,
            candidate.ItemPriceGradeCount,
            candidate.TotalReferenceCount,
            candidate.ReferenceIdentityFingerprint
        });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, JsonOptions)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task<Dictionary<Guid, int>> LoadRentalBillingTemplateItemReferenceCountsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken ct)
    {
        var itemIdSet = itemIds
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        if (itemIdSet.Count == 0)
            return [];

        var profiles = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => !string.IsNullOrWhiteSpace(profile.BillingTemplateJson))
            .Select(profile => profile.BillingTemplateJson)
            .ToListAsync(ct);

        var counts = new Dictionary<Guid, int>();
        foreach (var json in profiles)
        {
            List<RentalBillingTemplateItemModel>? templateItems;
            try
            {
                templateItems = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(json ?? "[]", JsonOptions);
            }
            catch
            {
                continue;
            }

            if (templateItems is null)
                continue;

            foreach (var item in templateItems)
            {
                if (item?.CatalogItemId is not { } catalogItemId || !itemIdSet.Contains(catalogItemId))
                    continue;

                counts[catalogItemId] = counts.GetValueOrDefault(catalogItemId) + 1;
            }
        }

        return counts;
    }

    public static DataIntegrityIssueDefinition GetDefinition(string code)
        => Definitions.TryGetValue(code, out var definition)
            ? definition
            : new DataIntegrityIssueDefinition(code, code, "Warning", "기타", "정의되지 않은 점검 항목입니다.", "상세 내용을 확인하세요.");

    private static void AddMasterDataAndLedgerIssues(
        ICollection<DataIntegrityIssueDetail> issues,
        IReadOnlyCollection<LocalCustomer> customers,
        IReadOnlyCollection<LocalItem> items,
        IReadOnlyCollection<LocalWarehouse> warehouses,
        IReadOnlyCollection<LocalWarehouse> activeTenantWarehouses,
        IReadOnlyCollection<IntegrityInvoiceSnapshot> invoices,
        IReadOnlyDictionary<Guid, decimal> invoiceLineTotalsByInvoiceId,
        IReadOnlyDictionary<Guid, decimal> invoicePaymentTotalsByInvoiceId,
        IReadOnlyDictionary<Guid, CustomerDuplicateUsage> customerDuplicateUsages,
        IReadOnlyDictionary<Guid, ItemDuplicateUsage> itemDuplicateUsages,
        IReadOnlyDictionary<Guid, int> itemDuplicateOutboxCounts,
        IReadOnlyCollection<LocalItemWarehouseStock> itemWarehouseStocks,
        IReadOnlyCollection<LocalInventoryMovement> inventoryMovements,
        SessionState session)
    {
        foreach (var group in customers
                     .Select(customer => new
                     {
                         Customer = customer,
                         Scope = ResolveCustomerScope(customer),
                         Key = BuildCustomerExactDuplicateKey(customer)
                     })
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                     .GroupBy(entry => $"{entry.Scope.TenantCode}|{entry.Scope.OfficeCode}|{entry.Key}", StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            var rows = group.Select(entry => entry.Customer).OrderBy(customer => customer.NameOriginal).ToList();
            var customerScope = ResolveCustomerScope(rows[0]);
            var relatedIds = rows.Select(row => row.Id).Distinct().ToList();
            AddGeneralIssue(issues, DataIntegrityIssueCodes.CustomerDuplicateCandidate,
                entityType: "거래처",
                entityId: rows[0].Id,
                customerName: rows[0].NameOriginal,
                officeCode: customerScope.OfficeCode,
                currentValue: BuildDuplicateDisplay(rows.Select(row => $"{row.NameOriginal}({row.Id:N})")),
                expectedValue: "거래처명이 완전히 같은 경우만 1건으로 정리",
                message: $"거래처명 '{rows[0].NameOriginal}' 완전 동일 중복 후보 {rows.Count:N0}건이 있습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenCustomer,
                relatedEntityIds: relatedIds,
                reviewInfo: string.Join(" / ", new[]
                {
                    BuildCustomerDuplicateReviewInfo(rows, customerDuplicateUsages),
                    BuildCustomerScopeReviewInfo(rows[0], customerScope)
                }));
        }

        foreach (var group in items
                     .Select(item => new
                     {
                         Item = item,
                         Scope = ResolveItemScope(item),
                         Key = BuildItemExactDuplicateKey(item)
                     })
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                     .GroupBy(entry => $"{entry.Scope.TenantCode}|{entry.Scope.OfficeCode}|{entry.Key}", StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            var rows = group.Select(entry => entry.Item).OrderBy(item => item.NameOriginal).ThenBy(item => item.SpecificationOriginal).ToList();
            var itemScope = ResolveItemScope(rows[0]);
            var relatedIds = rows.Select(row => row.Id).Distinct().ToList();
            var comparison = BuildItemDuplicateComparison(rows, itemDuplicateUsages, itemDuplicateOutboxCounts);
            AddGeneralIssue(issues, DataIntegrityIssueCodes.ItemDuplicateCandidate,
                entityType: "품목",
                entityId: rows[0].Id,
                itemName: rows[0].NameOriginal,
                officeCode: itemScope.OfficeCode,
                currentValue: BuildDuplicateDisplay(rows.Select(row => $"{row.NameOriginal} / {row.SpecificationOriginal}({row.Id:N})")),
                expectedValue: "품목명과 규격이 모두 완전히 같은 경우만 1건으로 정리",
                message: $"품목명 '{rows[0].NameOriginal}' / 규격 '{rows[0].SpecificationOriginal}' 완전 동일 중복 후보 {rows.Count:N0}건이 있습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenInventoryItem,
                relatedEntityIds: relatedIds,
                reviewInfo: BuildItemDuplicateReviewInfo(rows, itemDuplicateUsages),
                itemDuplicateComparison: comparison);
        }

        foreach (var group in warehouses
                     .Select(warehouse => new
                     {
                         Warehouse = warehouse,
                         OfficeCode = ResolveWarehouseOfficeCode(warehouse),
                         CodeKey = RentalCatalogValueNormalizer.NormalizeLooseKey(warehouse.Code),
                         NameKey = RentalCatalogValueNormalizer.NormalizeLooseKey(warehouse.Name)
                     })
                     .SelectMany(entry => new[]
                     {
                         new { entry.Warehouse, entry.OfficeCode, Key = $"CODE|{entry.CodeKey}" },
                         new { entry.Warehouse, entry.OfficeCode, Key = $"NAME|{entry.NameKey}" }
                     })
                     .Where(entry => !entry.Key.EndsWith("|", StringComparison.Ordinal))
                     .GroupBy(entry => $"{entry.OfficeCode}|{entry.Key}", StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Select(entry => entry.Warehouse.Id).Distinct().Count() > 1))
        {
            var rows = group.Select(entry => entry.Warehouse).GroupBy(warehouse => warehouse.Id).Select(grouping => grouping.First()).OrderBy(warehouse => warehouse.Name).ToList();
            var warehouseOfficeCode = group.Select(entry => entry.OfficeCode).FirstOrDefault() ?? ResolveWarehouseOfficeCode(rows[0]);
            var relatedIds = rows.Select(row => row.Id).Distinct().ToList();
            AddGeneralIssue(issues, DataIntegrityIssueCodes.WarehouseDuplicateCandidate,
                entityType: "창고",
                entityId: rows[0].Id,
                officeCode: warehouseOfficeCode,
                currentValue: BuildDuplicateDisplay(rows.Select(row => $"{row.Code} / {row.Name}({row.Id:N})")),
                expectedValue: "같은 창고이면 1건으로 정리",
                message: $"담당지점 {warehouseOfficeCode} 창고 중복 후보 {rows.Count:N0}건이 있습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenEnvironmentSettings,
                relatedEntityIds: relatedIds,
                reviewInfo: string.Join(" / ", new[]
                {
                    BuildWarehouseDuplicateReviewInfo(rows, itemWarehouseStocks, inventoryMovements, session),
                    BuildWarehouseScopeReviewInfo(rows[0], warehouseOfficeCode)
                }));
        }

        foreach (var invoice in invoices)
        {
            var invoiceScope = ResolveInvoiceScope(invoice);
            var invoiceScopeReviewInfo = string.Join(" / ", new[]
            {
                $"TenantCode {NormalizeDisplay(invoice.TenantCode, "-")}",
                $"OfficeCode {NormalizeDisplay(invoice.OfficeCode, "-")}",
                $"ResponsibleOfficeCode {NormalizeDisplay(invoice.ResponsibleOfficeCode, "-")}",
                $"ScopeTenant {invoiceScope.TenantCode}",
                $"ScopeOffice {invoiceScope.OfficeCode}"
            });
            invoiceLineTotalsByInvoiceId.TryGetValue(invoice.Id, out var lineTotal);
            var totals = InvoiceVatModes.CalculateTotals([lineTotal], invoice.VatMode);
            if (AmountDiffers(invoice.TotalAmount, totals.TotalAmount) ||
                AmountDiffers(invoice.SupplyAmount, totals.SupplyAmount) ||
                AmountDiffers(invoice.VatAmount, totals.VatAmount))
            {
                AddGeneralIssue(issues, DataIntegrityIssueCodes.InvoiceAmountMismatch,
                    entityType: "전표",
                    entityId: invoice.Id,
                    officeCode: invoiceScope.OfficeCode,
                    currentValue: $"공급 {invoice.SupplyAmount:N0} / 부가세 {invoice.VatAmount:N0} / 합계 {invoice.TotalAmount:N0}",
                    expectedValue: $"공급 {totals.SupplyAmount:N0} / 부가세 {totals.VatAmount:N0} / 합계 {totals.TotalAmount:N0}",
                    message: $"{invoice.InvoiceDate:yyyy-MM-dd} {FormatVoucherType(invoice.VoucherType)} 전표 {NormalizeDisplay(invoice.InvoiceNumber, invoice.Id.ToString("N"))} 금액 계산이 품목 합계와 다릅니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenInvoice,
                    reviewInfo: invoiceScopeReviewInfo);
            }

            invoicePaymentTotalsByInvoiceId.TryGetValue(invoice.Id, out var settlementTotal);
            if (settlementTotal - invoice.TotalAmount >= 1m)
            {
                AddGeneralIssue(issues, DataIntegrityIssueCodes.InvoiceOverSettled,
                    entityType: "전표",
                    entityId: invoice.Id,
                    officeCode: invoiceScope.OfficeCode,
                    currentValue: $"전표 {invoice.TotalAmount:N0} / 수금·지급 {settlementTotal:N0}",
                    expectedValue: "수금·지급 합계가 전표 합계 이하",
                    message: $"{invoice.InvoiceDate:yyyy-MM-dd} {FormatVoucherType(invoice.VoucherType)} 전표 {NormalizeDisplay(invoice.InvoiceNumber, invoice.Id.ToString("N"))}의 수금/지급 합계가 전표 금액보다 큽니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenPaymentForInvoice,
                    reviewInfo: invoiceScopeReviewInfo);
            }
        }

        var scopedItemIds = items.Select(item => item.Id).ToHashSet();
        var itemNameById = items.ToDictionary(item => item.Id, item => item.NameOriginal);
        var itemScopeById = items.ToDictionary(item => item.Id, ResolveItemScope);
        var stockByItem = itemWarehouseStocks
            .Where(stock => scopedItemIds.Contains(stock.ItemId))
            .GroupBy(stock => stock.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(stock => stock.Quantity));
        foreach (var item in items.Where(item => ItemOperationalPolicy.SupportsInventory(item.TrackingType)))
        {
            var itemScope = ResolveItemScope(item);
            stockByItem.TryGetValue(item.Id, out var stockTotal);
            if (!AmountDiffers(item.CurrentStock, stockTotal))
                continue;

            AddGeneralIssue(issues, DataIntegrityIssueCodes.InventoryStockSnapshotMismatch,
                entityType: "품목",
                entityId: item.Id,
                itemName: item.NameOriginal,
                officeCode: itemScope.OfficeCode,
                currentValue: $"품목 현재재고 {item.CurrentStock:N2}",
                expectedValue: $"창고별 합계 {stockTotal:N2}",
                message: $"{NormalizeDisplay(item.NameOriginal, "품목")} 품목의 현재재고와 창고별 재고 합계가 다릅니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenInventoryItem,
                reviewInfo: BuildItemScopeReviewInfo(item, itemScope));
        }

        var activeWarehouseCodes = activeTenantWarehouses
            .Select(warehouse => NormalizeWarehouseReferenceCodeForIntegrity(
                warehouse.Code,
                warehouse.OfficeCode))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stock in itemWarehouseStocks.Where(stock => scopedItemIds.Contains(stock.ItemId)))
        {
            var warehouseCode = NormalizeWarehouseReferenceCodeForIntegrity(
                stock.WarehouseCode,
                session.OfficeCode);
            if (activeWarehouseCodes.Contains(warehouseCode))
                continue;

            AddGeneralIssue(issues, DataIntegrityIssueCodes.InventoryWarehouseReferenceMissing,
                entityType: "재고",
                entityId: stock.ItemId,
                itemName: itemNameById.GetValueOrDefault(stock.ItemId) ?? string.Empty,
                officeCode: itemScopeById.TryGetValue(stock.ItemId, out var stockItemScope)
                    ? stockItemScope.OfficeCode
                    : ResolveOfficeCodeFromWarehouseCode(warehouseCode, session.OfficeCode),
                currentValue: warehouseCode,
                expectedValue: "활성 창고 코드",
                message: $"품목 재고 스냅샷이 존재하지 않거나 비활성인 창고 '{warehouseCode}'를 참조합니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenInventoryItem,
                reviewInfo: itemScopeById.TryGetValue(stock.ItemId, out stockItemScope)
                    ? $"Warehouse {warehouseCode} / {BuildItemScopeReviewInfo(stock.ItemId, stockItemScope)}"
                    : $"Warehouse {warehouseCode}");
        }

        foreach (var movement in inventoryMovements.Where(movement => movement.ItemId.HasValue && scopedItemIds.Contains(movement.ItemId.Value)))
        {
            var warehouseCode = NormalizeWarehouseReferenceCodeForIntegrity(
                movement.WarehouseCode,
                session.OfficeCode);
            if (activeWarehouseCodes.Contains(warehouseCode))
                continue;

            AddGeneralIssue(issues, DataIntegrityIssueCodes.InventoryWarehouseReferenceMissing,
                entityType: "재고 이동",
                entityId: movement.ItemId,
                itemName: movement.ItemId.HasValue ? itemNameById.GetValueOrDefault(movement.ItemId.Value) ?? string.Empty : string.Empty,
                officeCode: movement.ItemId.HasValue && itemScopeById.TryGetValue(movement.ItemId.Value, out var movementItemScope)
                    ? movementItemScope.OfficeCode
                    : ResolveOfficeCodeFromWarehouseCode(warehouseCode, session.OfficeCode),
                currentValue: warehouseCode,
                expectedValue: "활성 창고 코드",
                message: $"재고 이동 이력이 존재하지 않거나 비활성인 창고 '{warehouseCode}'를 참조합니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenInventoryItem,
                reviewInfo: movement.ItemId.HasValue && itemScopeById.TryGetValue(movement.ItemId.Value, out movementItemScope)
                    ? $"Warehouse {warehouseCode} / {BuildItemScopeReviewInfo(movement.ItemId.Value, movementItemScope)}"
                    : $"Warehouse {warehouseCode}");
        }
    }

    private static List<Guid> BuildDuplicateCustomerCandidateIds(IReadOnlyCollection<LocalCustomer> customers)
    {
        var ids = new HashSet<Guid>();
        foreach (var group in customers
                     .Select(customer => new
                     {
                         Customer = customer,
                         Scope = ResolveCustomerScope(customer),
                         Key = BuildCustomerExactDuplicateKey(customer)
                     })
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                     .GroupBy(entry => $"{entry.Scope.TenantCode}|{entry.Scope.OfficeCode}|{entry.Key}", StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            foreach (var entry in group)
                ids.Add(entry.Customer.Id);
        }

        return ids.ToList();
    }

    private static List<Guid> BuildDuplicateItemCandidateIds(IReadOnlyCollection<LocalItem> items)
    {
        var ids = new HashSet<Guid>();
        foreach (var group in items
                     .Select(item => new
                     {
                         Item = item,
                         Key = BuildItemExactDuplicateKey(item)
                     })
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                     .GroupBy(entry => entry.Key, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            foreach (var entry in group)
                ids.Add(entry.Item.Id);
        }

        return ids.ToList();
    }

    private static bool BelongsToSingleExactCustomerDuplicateGroup(IReadOnlyCollection<LocalCustomer> customers)
    {
        var groups = customers
            .Select(BuildCustomerExactDuplicateKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return groups.Count == 1;
    }

    private static bool BelongsToSingleExactItemDuplicateGroup(IReadOnlyCollection<LocalItem> items)
    {
        var groups = items
            .Select(BuildItemExactDuplicateKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return groups.Count == 1;
    }

    private static string BuildCustomerExactDuplicateKey(LocalCustomer customer)
    {
        var customerName = NormalizeExactDuplicateText(customer.NameOriginal);
        if (string.IsNullOrWhiteSpace(customerName))
            return string.Empty;

        var customerScope = ResolveCustomerScope(customer);
        return string.Join('|',
            customerScope.TenantCode,
            customerScope.OfficeCode,
            customerName);
    }

    private static string BuildItemExactDuplicateKey(LocalItem item)
    {
        var itemName = NormalizeExactDuplicateText(item.NameOriginal);
        if (string.IsNullOrWhiteSpace(itemName))
            return string.Empty;

        return string.Join('|',
            TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(item.TenantCode, item.OfficeCode),
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(item.OfficeCode, OfficeCodeCatalog.Shared),
            itemName,
            NormalizeExactDuplicateText(item.SpecificationOriginal));
    }

    private static string BuildCustomerDuplicateReviewInfo(
        IReadOnlyList<LocalCustomer> rows,
        IReadOnlyDictionary<Guid, CustomerDuplicateUsage> usageById)
    {
        var ranked = rows
            .Select(row => new
            {
                Customer = row,
                Usage = usageById.TryGetValue(row.Id, out var usage) ? usage : CustomerDuplicateUsage.Empty
            })
            .OrderByDescending(entry => entry.Usage.TotalCount)
            .ThenByDescending(entry => CountFilledCustomerValues(entry.Customer))
            .ThenBy(entry => entry.Customer.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var representative = ranked.First();
        var totalReferences = ranked.Sum(entry => entry.Usage.TotalCount);
        var candidates = string.Join(" / ", ranked.Take(6).Select(entry =>
            $"{NormalizeDisplay(entry.Customer.NameOriginal, "거래처")}({ShortId(entry.Customer.Id)}) 참조 {entry.Usage.TotalCount:N0}건"));

        return $"후보 {rows.Count:N0}건, 참조 합계 {totalReferences:N0}건. 대표 추천: {NormalizeDisplay(representative.Customer.NameOriginal, "거래처")}({ShortId(representative.Customer.Id)}). 후보별: {candidates}. 병합 시 전표·수금/지급·렌탈 청구/자산·계약서·임대이력 참조를 대표 거래처로 옮기고 나머지 거래처를 삭제 처리합니다.";
    }

    private static string BuildItemDuplicateReviewInfo(
        IReadOnlyList<LocalItem> rows,
        IReadOnlyDictionary<Guid, ItemDuplicateUsage> usageById)
    {
        var ranked = rows
            .Select(row => new
            {
                Item = row,
                Usage = usageById.TryGetValue(row.Id, out var usage) ? usage : ItemDuplicateUsage.Empty
            })
            .OrderByDescending(entry => entry.Usage.TotalCount)
            .ThenByDescending(entry => Math.Abs(entry.Item.CurrentStock))
            .ThenByDescending(entry => CountFilledItemValues(entry.Item))
            .ThenBy(entry => entry.Item.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var representative = ranked.First();
        var totalReferences = ranked.Sum(entry => entry.Usage.TotalCount);
        var stockQuantity = ranked.Sum(entry => entry.Usage.ItemWarehouseStockQuantity);
        var currentStock = rows.Sum(row => row.CurrentStock);
        var candidates = string.Join(" / ", ranked.Take(6).Select(entry =>
            $"{NormalizeDisplay(entry.Item.NameOriginal, "품목")}({ShortId(entry.Item.Id)}) 참조 {entry.Usage.TotalCount:N0}건, 재고 {entry.Item.CurrentStock:N2}"));

        return $"후보 {rows.Count:N0}건, 참조 합계 {totalReferences:N0}건, 품목 현재재고 합계 {currentStock:N2}, 창고별 재고 합계 {stockQuantity:N2}. 대표 추천: {NormalizeDisplay(representative.Item.NameOriginal, "품목")}({ShortId(representative.Item.Id)}). 후보별: {candidates}. 병합 시 전표 라인·렌탈 자산·배송/재고 이동·시리얼·창고별 재고를 대표 품목으로 옮기고 나머지 품목을 삭제 처리합니다.";
    }

    private static string BuildWarehouseDuplicateReviewInfo(
        IReadOnlyList<LocalWarehouse> rows,
        IReadOnlyCollection<LocalItemWarehouseStock> itemWarehouseStocks,
        IReadOnlyCollection<LocalInventoryMovement> inventoryMovements,
        SessionState session)
    {
        var warehouseCodes = rows
            .Select(row => OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(row.Code, row.OfficeCode, session.OfficeCode))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stockRows = itemWarehouseStocks
            .Where(stock => warehouseCodes.Contains(OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(stock.WarehouseCode, session.OfficeCode)))
            .ToList();
        var movementRows = inventoryMovements
            .Where(movement => warehouseCodes.Contains(OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(movement.WarehouseCode, session.OfficeCode)))
            .ToList();
        var candidates = string.Join(" / ", rows.Take(6).Select(row =>
            $"{NormalizeDisplay(row.Code, "코드없음")}·{NormalizeDisplay(row.Name, "창고")}({ShortId(row.Id)})"));

        return $"후보 {rows.Count:N0}건. 후보별: {candidates}. 연결 재고 행 {stockRows.Count:N0}건, 재고수량 합계 {stockRows.Sum(stock => stock.Quantity):N2}, 이동 이력 {movementRows.Count:N0}건. 창고는 재고/이동 경로 충돌 위험이 있어 자동 병합 대신 환경설정에서 코드·이름·창고별 재고를 확인한 뒤 같은 창고만 수동 정리하세요.";
    }

    private static void AddIssue(
        ICollection<DataIntegrityIssueDetail> issues,
        string code,
        LocalRentalBillingProfile? profile,
        LocalRentalAsset? asset,
        string entityType,
        Guid? entityId,
        string? itemName = null,
        string currentValue = "",
        string expectedValue = "",
        string message = "",
        DataIntegrityDirectActionKind directActionKind = DataIntegrityDirectActionKind.None,
        string reviewInfo = "")
    {
        var definition = GetDefinition(code);
        issues.Add(new DataIntegrityIssueDetail
        {
            Code = definition.Code,
            Title = definition.Title,
            Severity = definition.Severity,
            Area = definition.Area,
            EntityType = entityType,
            EntityId = entityId,
            ProfileId = profile?.Id,
            AssetId = asset?.Id,
            CustomerName = profile?.CustomerName ?? asset?.CurrentCustomerName ?? asset?.CustomerName ?? string.Empty,
            ItemName = NormalizeDisplay(itemName, profile?.ItemName ?? asset?.ItemName ?? string.Empty),
            AssetDisplayName = asset is null ? string.Empty : BuildAssetDisplay(asset),
            OfficeCode = profile is null ? (asset is null ? string.Empty : ResolveAssetOfficeCode(asset)) : ResolveProfileOfficeCode(profile),
            CurrentValue = currentValue,
            ExpectedValue = expectedValue,
            Message = message,
            SuggestedAction = definition.SuggestedAction,
            DirectActionKind = directActionKind,
            ReviewInfo = reviewInfo
        });
    }

    private static void AddHistoryIssue(
        ICollection<DataIntegrityIssueDetail> issues,
        string code,
        LocalRentalAssetAssignmentHistory history,
        LocalRentalAsset? asset,
        LocalRentalBillingProfile? profile,
        string currentValue,
        string expectedValue,
        string message)
    {
        var definition = GetDefinition(code);
        var directActionKind = asset is not null
            ? DataIntegrityDirectActionKind.OpenRentalAsset
            : profile is not null
                ? DataIntegrityDirectActionKind.OpenRentalBillingProfile
                : string.Equals(code, DataIntegrityIssueCodes.RentalAssignmentMissingReference, StringComparison.Ordinal)
                    ? DataIntegrityDirectActionKind.OpenSyncDiagnostics
                    : DataIntegrityDirectActionKind.None;
        var historyScope = ResolveAssignmentHistoryScope(history, asset, profile);

        issues.Add(new DataIntegrityIssueDetail
        {
            Code = definition.Code,
            Title = definition.Title,
            Severity = definition.Severity,
            Area = definition.Area,
            EntityType = "임대이력",
            EntityId = history.Id,
            ProfileId = profile?.Id ?? history.BillingProfileId,
            AssetId = asset?.Id ?? (history.AssetId == Guid.Empty ? null : history.AssetId),
            CustomerName = NormalizeDisplay(history.CustomerName, asset?.CurrentCustomerName ?? asset?.CustomerName ?? profile?.CustomerName ?? string.Empty),
            ItemName = NormalizeDisplay(history.ItemName, asset?.ItemName ?? profile?.ItemName ?? string.Empty),
            AssetDisplayName = asset is null ? BuildHistoryDisplay(history) : BuildAssetDisplay(asset),
            OfficeCode = historyScope.OfficeCode,
            CurrentValue = currentValue,
            ExpectedValue = expectedValue,
            Message = message,
            SuggestedAction = definition.SuggestedAction,
            DirectActionKind = directActionKind,
            ReviewInfo = string.Join(" / ", new[]
            {
                $"TenantCode {NormalizeDisplay(history.TenantCode, "-")}",
                $"ResponsibleOfficeCode {NormalizeDisplay(history.ResponsibleOfficeCode, "-")}",
                $"ScopeTenant {historyScope.TenantCode}",
                $"ScopeOffice {historyScope.OfficeCode}",
                history.BillingProfileId.HasValue ? $"BillingProfileId {history.BillingProfileId.Value:D}" : "BillingProfileId 없음",
                history.AssetId == Guid.Empty ? "AssetId 없음" : $"AssetId {history.AssetId:D}"
            })
        });
    }

    private static void AddGeneralIssue(
        ICollection<DataIntegrityIssueDetail> issues,
        string code,
        string entityType,
        Guid? entityId,
        string? customerName = null,
        string? itemName = null,
        string? assetDisplayName = null,
        string? officeCode = null,
        string currentValue = "",
        string expectedValue = "",
        string message = "",
        DataIntegrityDirectActionKind directActionKind = DataIntegrityDirectActionKind.None,
        IReadOnlyCollection<Guid>? relatedEntityIds = null,
        string reviewInfo = "",
        DataIntegrityItemDuplicateComparison? itemDuplicateComparison = null)
    {
        var definition = GetDefinition(code);
        issues.Add(new DataIntegrityIssueDetail
        {
            Code = definition.Code,
            Title = definition.Title,
            Severity = definition.Severity,
            Area = definition.Area,
            EntityType = entityType,
            EntityId = entityId,
            CustomerName = NormalizeDisplay(customerName, string.Empty),
            ItemName = NormalizeDisplay(itemName, string.Empty),
            AssetDisplayName = NormalizeDisplay(assetDisplayName, string.Empty),
            OfficeCode = NormalizeDisplay(officeCode, string.Empty),
            CurrentValue = currentValue,
            ExpectedValue = expectedValue,
            Message = message,
            SuggestedAction = definition.SuggestedAction,
            DirectActionKind = directActionKind,
            RelatedEntityIds = relatedEntityIds?.Where(id => id != Guid.Empty).Distinct().ToArray() ?? Array.Empty<Guid>(),
            ReviewInfo = reviewInfo,
            ItemDuplicateComparison = itemDuplicateComparison
        });
    }

    private async Task<decimal?> RemapItemWarehouseStocksAsync(
        Guid canonicalItemId,
        IReadOnlyCollection<Guid> duplicateItemIds,
        DateTime now,
        CancellationToken ct)
    {
        var duplicateIds = duplicateItemIds
            .Where(id => id != Guid.Empty && id != canonicalItemId)
            .Distinct()
            .ToList();
        if (duplicateIds.Count == 0)
            return null;

        var warehouseStocks = await _db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == canonicalItemId || duplicateIds.Contains(stock.ItemId))
            .ToListAsync(ct);
        if (warehouseStocks.Count == 0)
            return null;

        var canonicalStockLookup = warehouseStocks
            .Where(stock => stock.ItemId == canonicalItemId)
            .GroupBy(stock => BuildItemWarehouseStockKey(stock.ItemId, stock.WarehouseCode), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var stock in warehouseStocks.Where(stock => duplicateIds.Contains(stock.ItemId)).ToList())
        {
            var stockKey = BuildItemWarehouseStockKey(canonicalItemId, stock.WarehouseCode);
            if (canonicalStockLookup.TryGetValue(stockKey, out var canonicalStock))
            {
                canonicalStock.Quantity += stock.Quantity;
                canonicalStock.UpdatedAtUtc = now;
                _db.ItemWarehouseStocks.Remove(stock);
                continue;
            }

            var migratedStock = new LocalItemWarehouseStock
            {
                ItemId = canonicalItemId,
                WarehouseCode = stock.WarehouseCode,
                Quantity = stock.Quantity,
                UpdatedAtUtc = now
            };
            canonicalStockLookup[stockKey] = migratedStock;
            _db.ItemWarehouseStocks.Add(migratedStock);
            _db.ItemWarehouseStocks.Remove(stock);
        }

        return canonicalStockLookup.Values.Sum(stock => stock.Quantity);
    }

    private static string BuildItemWarehouseStockKey(Guid itemId, string? warehouseCode)
        => $"{itemId:D}|{(warehouseCode ?? string.Empty).Trim().ToUpperInvariant()}";

    private static bool ShouldReplaceDisplayName(string? currentValue, IEnumerable<string> duplicateValues)
    {
        var currentKey = RentalCatalogValueNormalizer.NormalizeLooseKey(currentValue);
        if (string.IsNullOrWhiteSpace(currentKey))
            return true;

        return duplicateValues
            .Select(RentalCatalogValueNormalizer.NormalizeLooseKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => string.Equals(value, currentKey, StringComparison.OrdinalIgnoreCase));
    }

    private static void MergeCustomerValues(LocalCustomer canonical, LocalCustomer duplicate)
    {
        FillIfBlank(value => canonical.BusinessNumber = value, canonical.BusinessNumber, duplicate.BusinessNumber);
        FillIfBlank(value => canonical.Department = value, canonical.Department, duplicate.Department);
        FillIfBlank(value => canonical.ContactPerson = value, canonical.ContactPerson, duplicate.ContactPerson);
        FillIfBlank(value => canonical.Address = value, canonical.Address, duplicate.Address);
        FillIfBlank(value => canonical.DetailAddress = value, canonical.DetailAddress, duplicate.DetailAddress);
        FillIfBlank(value => canonical.Phone = value, canonical.Phone, duplicate.Phone);
        FillIfBlank(value => canonical.MobilePhone = value, canonical.MobilePhone, duplicate.MobilePhone);
        FillIfBlank(value => canonical.FaxNumber = value, canonical.FaxNumber, duplicate.FaxNumber);
        FillIfBlank(value => canonical.Email = value, canonical.Email, duplicate.Email);
        FillIfBlank(value => canonical.HomePage = value, canonical.HomePage, duplicate.HomePage);
        FillIfBlank(value => canonical.Representative = value, canonical.Representative, duplicate.Representative);
        FillIfBlank(value => canonical.BusinessType = value, canonical.BusinessType, duplicate.BusinessType);
        FillIfBlank(value => canonical.BusinessItem = value, canonical.BusinessItem, duplicate.BusinessItem);
        FillIfBlank(value => canonical.Recipient = value, canonical.Recipient, duplicate.Recipient);
        FillIfBlank(value => canonical.PriceGrade = value, canonical.PriceGrade, duplicate.PriceGrade);
        FillIfBlank(value => canonical.Notes = value, canonical.Notes, duplicate.Notes);
        canonical.CategoryId ??= duplicate.CategoryId;
        canonical.CustomerMasterId ??= duplicate.CustomerMasterId;
    }

    private static void MergeItemValues(LocalItem canonical, LocalItem duplicate)
    {
        FillIfBlank(value => canonical.SpecificationOriginal = value, canonical.SpecificationOriginal, duplicate.SpecificationOriginal);
        FillIfBlank(value => canonical.CategoryName = value, canonical.CategoryName, duplicate.CategoryName);
        FillIfBlank(value => canonical.ItemKind = value, canonical.ItemKind, duplicate.ItemKind);
        FillIfBlank(value => canonical.TrackingType = value, canonical.TrackingType, duplicate.TrackingType);
        FillIfBlank(value => canonical.Unit = value, canonical.Unit, duplicate.Unit);
        FillIfBlank(value => canonical.StorageLocation = value, canonical.StorageLocation, duplicate.StorageLocation);
        FillIfBlank(value => canonical.SimpleMemo = value, canonical.SimpleMemo, duplicate.SimpleMemo);
        FillIfBlank(value => canonical.SerialNumber = value, canonical.SerialNumber, duplicate.SerialNumber);
        FillIfBlank(value => canonical.MaterialNumber = value, canonical.MaterialNumber, duplicate.MaterialNumber);
        FillIfBlank(value => canonical.InstallLocation = value, canonical.InstallLocation, duplicate.InstallLocation);
        FillIfBlank(value => canonical.Notes = value, canonical.Notes, duplicate.Notes);
        canonical.BoxQuantity = canonical.BoxQuantity == 0m ? duplicate.BoxQuantity : canonical.BoxQuantity;
        canonical.SafetyStock = canonical.SafetyStock == 0m ? duplicate.SafetyStock : canonical.SafetyStock;
        canonical.PurchasePrice = canonical.PurchasePrice == 0m ? duplicate.PurchasePrice : canonical.PurchasePrice;
        canonical.SalePrice = canonical.SalePrice == 0m ? duplicate.SalePrice : canonical.SalePrice;
        canonical.RetailPrice = canonical.RetailPrice == 0m ? duplicate.RetailPrice : canonical.RetailPrice;
        canonical.PriceGradeA = canonical.PriceGradeA == 0m ? duplicate.PriceGradeA : canonical.PriceGradeA;
        canonical.PriceGradeB = canonical.PriceGradeB == 0m ? duplicate.PriceGradeB : canonical.PriceGradeB;
        canonical.PriceGradeC = canonical.PriceGradeC == 0m ? duplicate.PriceGradeC : canonical.PriceGradeC;
        canonical.LastPurchaseDate ??= duplicate.LastPurchaseDate;
        canonical.LastSaleDate ??= duplicate.LastSaleDate;
        canonical.RentalStartDate ??= duplicate.RentalStartDate;
        canonical.RentalEndDate ??= duplicate.RentalEndDate;
        canonical.IsRental |= duplicate.IsRental;
        canonical.IsSale |= duplicate.IsSale;
    }

    private static void FillIfBlank(Action<string> setTarget, string? currentValue, string? sourceValue)
    {
        if (!string.IsNullOrWhiteSpace(currentValue) || string.IsNullOrWhiteSpace(sourceValue))
            return;

        setTarget(sourceValue.Trim());
    }

    private static void MarkDirty(ILocalSyncEntity entity, DateTime now)
    {
        entity.IsDirty = true;
        entity.UpdatedAtUtc = now;
    }

    private void AddDuplicateMergeAudit(
        string entityName,
        Guid canonicalId,
        SessionState session,
        DateTime now,
        object before,
        object after)
    {
        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = entityName,
            EntityId = canonicalId.ToString("D"),
            Action = "DataIntegrityDuplicateMerge",
            Username = session.User?.Username ?? string.Empty,
            Role = session.User?.Role ?? string.Empty,
            OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, DomainConstants.OfficeUsenet),
            BeforeJson = JsonSerializer.Serialize(before, JsonOptions),
            AfterJson = JsonSerializer.Serialize(after, JsonOptions),
            CreatedAtUtc = now
        });
    }

    private static bool CanEditCustomersForIntegrity(SessionState? session)
        => session is not null && (session.HasAdministrativePrivileges || session.HasPermission(AppPermissionNames.CustomerEdit));

    private static bool CanEditItemsForIntegrity(SessionState? session)
        => session is not null && (session.HasAdministrativePrivileges || session.HasPermission(AppPermissionNames.ItemEdit));

    private static bool HasIntegrityPermission(SessionState? session, string permissionName)
        => session is not null && (session.HasAdministrativePrivileges || session.HasPermission(permissionName));

    private static bool CanWriteInventoryTransferScopeForIntegrity(SessionState? session, LocalInventoryTransfer transfer)
        => CanWriteWarehouseScopeForIntegrity(session, transfer.FromWarehouseCode) &&
           CanWriteWarehouseScopeForIntegrity(session, transfer.ToWarehouseCode);

    private static bool CanWriteWarehouseScopeForIntegrity(SessionState? session, string? warehouseCode)
    {
        if (session is null || !session.IsLoggedIn)
            return false;

        if (session.HasGlobalDataScope)
            return true;

        if (!OfficeCodeCatalog.TryNormalizeWarehouseCode(warehouseCode, out var normalizedWarehouseCode))
            return false;

        var officeCode = normalizedWarehouseCode switch
        {
            OfficeCodeCatalog.ItworldMainWarehouse => OfficeCodeCatalog.Itworld,
            OfficeCodeCatalog.YeonsuMainWarehouse => OfficeCodeCatalog.Yeonsu,
            _ => OfficeCodeCatalog.Usenet
        };

        var scopeEvidence = ResolveStoredScopeEvidence(
            TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            responsibleOfficeCode: officeCode,
            ownerOfficeCode: null);
        return CanWriteStoredScopeEvidenceForIntegrity(session, scopeEvidence);
    }

    private readonly record struct IntegrityStoredScopeEvidence(
        string TenantCode,
        string OfficeCode,
        bool IsTenantOnly)
    {
        public bool HasEvidence => !string.IsNullOrWhiteSpace(TenantCode);
        public bool HasOfficeEvidence => !string.IsNullOrWhiteSpace(OfficeCode);
    }

    private static IntegrityStoredScopeEvidence ResolveStoredScopeEvidence(
        string? tenantCode,
        string? responsibleOfficeCode,
        string? ownerOfficeCode,
        string? additionalOfficeCode = null)
    {
        var hasTenant = TenantScopeCatalog.TryNormalizeTenantCode(tenantCode, out var normalizedTenant);
        if (!string.IsNullOrWhiteSpace(tenantCode) && !hasTenant)
            return default;

        static bool TryCollectOfficeEvidence(
            string? rawOfficeCode,
            bool allowShared,
            bool ignoreUnsupportedCode,
            ICollection<string> offices,
            out string normalizedOffice,
            out bool isShared)
        {
            if (string.IsNullOrWhiteSpace(rawOfficeCode))
            {
                normalizedOffice = string.Empty;
                isShared = false;
                return true;
            }

            if (OfficeCodeCatalog.TryNormalizeOfficeCode(rawOfficeCode, out normalizedOffice))
            {
                offices.Add(normalizedOffice);
                isShared = false;
                return true;
            }

            if (allowShared && OfficeCodeCatalog.IsSharedOfficeCode(rawOfficeCode))
            {
                normalizedOffice = string.Empty;
                isShared = true;
                return true;
            }

            normalizedOffice = string.Empty;
            isShared = false;
            return ignoreUnsupportedCode;
        }

        var officeEvidence = new List<string>(capacity: 3);
        if (!TryCollectOfficeEvidence(
                responsibleOfficeCode,
                allowShared: true,
                ignoreUnsupportedCode: false,
                officeEvidence,
                out var responsibleOffice,
                out var responsibleIsShared) ||
            !TryCollectOfficeEvidence(
                ownerOfficeCode,
                allowShared: true,
                ignoreUnsupportedCode: false,
                officeEvidence,
                out var ownerOffice,
                out var ownerIsShared) ||
            !TryCollectOfficeEvidence(
                additionalOfficeCode,
                allowShared: true,
                ignoreUnsupportedCode: true,
                officeEvidence,
                out var additionalOffice,
                out var additionalIsShared))
        {
            return default;
        }

        var evidenceTenants = officeEvidence
            .Select(TenantScopeCatalog.GetTenantCodeForOffice)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (evidenceTenants.Length > 1 ||
            (evidenceTenants.Length == 1 &&
             hasTenant &&
             !string.Equals(evidenceTenants[0], normalizedTenant, StringComparison.OrdinalIgnoreCase)))
        {
            return default;
        }

        var sharedScopeChosen = false;
        string chosenOffice;
        if (!string.IsNullOrWhiteSpace(responsibleOffice))
        {
            chosenOffice = responsibleOffice;
        }
        else if (responsibleIsShared)
        {
            chosenOffice = string.Empty;
            sharedScopeChosen = true;
        }
        else if (!string.IsNullOrWhiteSpace(ownerOffice))
        {
            chosenOffice = ownerOffice;
        }
        else if (ownerIsShared)
        {
            chosenOffice = string.Empty;
            sharedScopeChosen = true;
        }
        else if (!string.IsNullOrWhiteSpace(additionalOffice))
        {
            chosenOffice = additionalOffice;
        }
        else
        {
            chosenOffice = string.Empty;
            sharedScopeChosen = additionalIsShared;
        }

        if (!string.IsNullOrWhiteSpace(chosenOffice))
        {
            return new IntegrityStoredScopeEvidence(
                evidenceTenants[0],
                chosenOffice,
                IsTenantOnly: false);
        }

        var evidenceTenant = evidenceTenants.FirstOrDefault() ?? (hasTenant ? normalizedTenant : string.Empty);
        if (sharedScopeChosen)
        {
            return string.IsNullOrWhiteSpace(evidenceTenant)
                ? default
                : new IntegrityStoredScopeEvidence(
                    evidenceTenant,
                    OfficeCode: string.Empty,
                    IsTenantOnly: false);
        }

        if (!hasTenant)
            return default;

        var tenantOffices = TenantScopeCatalog.GetOfficeCodesForTenant(normalizedTenant);
        return new IntegrityStoredScopeEvidence(
            normalizedTenant,
            tenantOffices.Count == 1 ? tenantOffices[0] : string.Empty,
            IsTenantOnly: true);
    }

    private static bool CanReadStoredScopeEvidenceForIntegrity(
        SessionState session,
        IntegrityStoredScopeEvidence evidence)
    {
        if (session.HasGlobalDataScope)
            return true;

        if (!evidence.HasEvidence)
            return false;

        if (evidence.HasOfficeEvidence)
            return IsInSessionScope(evidence.TenantCode, evidence.OfficeCode, session);

        var sessionTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            session.TenantCode,
            session.OfficeCode);
        return string.Equals(evidence.TenantCode, sessionTenant, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(session.ScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanReadAllExplicitOfficeEvidenceForIntegrity(
        SessionState session,
        IReadOnlyCollection<string> officeCodes)
    {
        var normalizedOffices = officeCodes
            .Select(officeCode => OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out var normalizedOffice)
                ? normalizedOffice
                : string.Empty)
            .Where(officeCode => !string.IsNullOrWhiteSpace(officeCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedOffices.Length == 0)
            return session.HasGlobalDataScope;

        if (session.HasGlobalDataScope)
            return true;

        if (normalizedOffices.Length == 1)
            return IsInSessionScope(null, normalizedOffices[0], session);

        var evidenceTenants = normalizedOffices
            .Select(TenantScopeCatalog.GetTenantCodeForOffice)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (evidenceTenants.Length != 1 ||
            !string.Equals(session.ScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sessionTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            session.TenantCode,
            session.OfficeCode);
        if (!string.Equals(evidenceTenants[0], sessionTenant, StringComparison.OrdinalIgnoreCase))
            return false;

        var writableOffices = TenantScopeCatalog.ResolveScopedOfficeCodes(
            session.OfficeCode,
            sessionTenant,
            session.ScopeType,
            session.HasGlobalDataScope);
        return normalizedOffices.All(writableOffices.Contains);
    }

    private static bool CanReadCombinedStoredScopeEvidenceForIntegrity(
        SessionState session,
        IReadOnlyCollection<IntegrityStoredScopeEvidence> evidenceSources)
    {
        if (session.HasGlobalDataScope)
            return true;

        if (evidenceSources.Count == 0 || evidenceSources.Any(evidence => !evidence.HasEvidence))
            return false;

        var explicitOffices = evidenceSources
            .Where(evidence => evidence.HasOfficeEvidence)
            .Select(evidence => evidence.OfficeCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (explicitOffices.Length > 0 &&
            !CanReadAllExplicitOfficeEvidenceForIntegrity(session, explicitOffices))
        {
            return false;
        }

        return evidenceSources
            .Where(evidence => !evidence.HasOfficeEvidence)
            .All(evidence => CanReadStoredScopeEvidenceForIntegrity(session, evidence));
    }

    private static bool CanWriteStoredScopeEvidenceForIntegrity(
        SessionState? session,
        IntegrityStoredScopeEvidence evidence)
    {
        if (session is null || !session.IsLoggedIn)
            return false;

        if (session.HasGlobalDataScope)
            return true;

        if (!evidence.HasEvidence)
            return false;

        var sessionTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            session.TenantCode,
            session.OfficeCode);
        if (!string.Equals(evidence.TenantCode, sessionTenant, StringComparison.OrdinalIgnoreCase))
            return false;

        if (evidence.HasOfficeEvidence)
            return CanWriteOfficeScopeForIntegrity(session, evidence.OfficeCode);

        return string.Equals(session.ScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveIssueOfficeCodeForStoredScope(
        IntegrityStoredScopeEvidence evidence,
        SessionState session,
        string fallbackOfficeCode)
    {
        if (evidence.HasOfficeEvidence)
            return evidence.OfficeCode;

        if (evidence.HasEvidence &&
            OfficeCodeCatalog.TryNormalizeOfficeCode(session.OfficeCode, out var sessionOfficeCode) &&
            TenantScopeCatalog.TenantContainsOffice(evidence.TenantCode, sessionOfficeCode))
        {
            return sessionOfficeCode;
        }

        return fallbackOfficeCode;
    }

    private static bool CanWriteCustomerScopeForIntegrity(SessionState? session, string? officeCode, string? tenantCode = null)
    {
        if (session is null || !session.IsLoggedIn)
            return false;

        if (session.HasGlobalDataScope)
            return true;

        var normalizedOffice = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(officeCode, DomainConstants.OfficeUsenet);
        var targetTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, normalizedOffice);
        var sessionTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(session.TenantCode, session.OfficeCode);
        if (!string.Equals(targetTenant, sessionTenant, StringComparison.OrdinalIgnoreCase))
            return false;

        return CanWriteOfficeScopeForIntegrity(session, normalizedOffice);
    }

    private static bool CanWriteItemScopeForIntegrity(SessionState? session, LocalItem item)
        => CanWriteStoredScopeEvidenceForIntegrity(
            session,
            ResolveStoredScopeEvidence(
                item.TenantCode,
                responsibleOfficeCode: null,
                ownerOfficeCode: item.OfficeCode));

    private static bool CanWriteOfficeScopeForIntegrity(SessionState session, string? officeCode)
    {
        if (session.HasGlobalDataScope)
            return true;

        var normalizedOffice = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(officeCode, string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedOffice))
            return false;

        if (string.Equals(normalizedOffice, OfficeCodeCatalog.Shared, StringComparison.OrdinalIgnoreCase))
            return string.Equals(session.ScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase);

        var sessionTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(session.TenantCode, session.OfficeCode);
        var writableOffices = TenantScopeCatalog.ResolveScopedOfficeCodes(
            session.OfficeCode,
            sessionTenant,
            session.ScopeType,
            session.HasGlobalDataScope);
        return writableOffices.Contains(normalizedOffice);
    }

    private static (string TenantCode, string OfficeCode) ResolveCustomerScope(LocalCustomer customer)
    {
        string scopeOfficeCode;
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(customer.ResponsibleOfficeCode, out var responsibleOfficeCode))
        {
            scopeOfficeCode = responsibleOfficeCode;
        }
        else if (OfficeCodeCatalog.TryNormalizeOfficeCode(customer.OfficeCode, out var ownerOfficeCode))
        {
            scopeOfficeCode = ownerOfficeCode;
        }
        else
        {
            scopeOfficeCode = TenantScopeCatalog.TryNormalizeTenantCode(customer.TenantCode, out var tenantCode) &&
                              string.Equals(tenantCode, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase)
                ? OfficeCodeCatalog.Itworld
                : OfficeCodeCatalog.Usenet;
        }

        var scopeTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            customer.TenantCode,
            scopeOfficeCode,
            customer.TenantCode,
            scopeOfficeCode);
        return (scopeTenantCode, scopeOfficeCode);
    }

    private static string ResolveCustomerOfficeCode(LocalCustomer customer)
        => ResolveCustomerScope(customer).OfficeCode;

    private static string BuildCustomerScopeReviewInfo(LocalCustomer customer, (string TenantCode, string OfficeCode) customerScope)
        => string.Join(" / ", new[]
        {
            $"TenantCode {NormalizeDisplay(customer.TenantCode, "-")}",
            $"OfficeCode {NormalizeDisplay(customer.OfficeCode, "-")}",
            $"ResponsibleOfficeCode {NormalizeDisplay(customer.ResponsibleOfficeCode, "-")}",
            $"ScopeTenant {customerScope.TenantCode}",
            $"ScopeOffice {customerScope.OfficeCode}"
        });

    private static string ResolveOfficeCodeFromWarehouseCode(string? warehouseCode, string fallbackOfficeCode)
    {
        var normalized = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(warehouseCode, fallbackOfficeCode);
        return normalized switch
        {
            OfficeCodeCatalog.ItworldMainWarehouse => OfficeCodeCatalog.Itworld,
            OfficeCodeCatalog.YeonsuMainWarehouse => OfficeCodeCatalog.Yeonsu,
            _ => OfficeCodeCatalog.Usenet
        };
    }

    private static string NormalizeWarehouseReferenceCodeForIntegrity(
        string? warehouseCode,
        string? fallbackOfficeCode)
    {
        if (OfficeCodeCatalog.TryNormalizeWarehouseCode(
                warehouseCode,
                out var canonicalWarehouseCode))
        {
            return canonicalWarehouseCode;
        }

        var rawWarehouseCode = (warehouseCode ?? string.Empty)
            .Trim()
            .ToUpperInvariant();
        return string.IsNullOrWhiteSpace(rawWarehouseCode)
            ? OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
                null,
                fallbackOfficeCode)
            : rawWarehouseCode;
    }

    private static string ResolveWarehouseOfficeCode(LocalWarehouse warehouse)
    {
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(warehouse.OfficeCode, out var officeCode))
            return officeCode;

        if (TryResolveOfficeCodeFromWarehouseEvidence(warehouse.Code, out officeCode))
            return officeCode;

        return DomainConstants.OfficeUsenet;
    }

    private static bool IsConsistentWarehouseReferenceInTenant(
        LocalWarehouse warehouse,
        string tenantCode)
    {
        var hasStoredOffice = OfficeCodeCatalog.TryNormalizeOfficeCode(
            warehouse.OfficeCode,
            out var storedOfficeCode);
        var hasCodeOffice = TryResolveOfficeCodeFromWarehouseEvidence(
            warehouse.Code,
            out var codeOfficeCode);

        if (hasStoredOffice &&
            hasCodeOffice &&
            !string.Equals(
                storedOfficeCode,
                codeOfficeCode,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var resolvedOfficeCode = hasCodeOffice
            ? codeOfficeCode
            : hasStoredOffice
                ? storedOfficeCode
                : string.Empty;
        return !string.IsNullOrWhiteSpace(resolvedOfficeCode) &&
               TenantScopeCatalog.TenantContainsOffice(
                   tenantCode,
                   resolvedOfficeCode);
    }

    private static string BuildWarehouseScopeReviewInfo(LocalWarehouse warehouse, string scopeOfficeCode)
        => string.Join(" / ", new[]
        {
            $"OfficeCode {NormalizeDisplay(warehouse.OfficeCode, "-")}",
            $"WarehouseCode {NormalizeDisplay(warehouse.Code, "-")}",
            $"ScopeOffice {scopeOfficeCode}"
        });

    private static bool TryResolveOfficeCodeFromWarehouseEvidence(string? warehouseCode, out string officeCode)
    {
        var normalized = (warehouseCode ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            officeCode = string.Empty;
            return false;
        }

        if (string.Equals(normalized, OfficeCodeCatalog.ItworldMainWarehouse, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("ITWORLD", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("아이티월드", StringComparison.OrdinalIgnoreCase))
        {
            officeCode = OfficeCodeCatalog.Itworld;
            return true;
        }

        if (string.Equals(normalized, OfficeCodeCatalog.YeonsuMainWarehouse, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("YEONSU", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("연수", StringComparison.OrdinalIgnoreCase))
        {
            officeCode = OfficeCodeCatalog.Yeonsu;
            return true;
        }

        if (string.Equals(normalized, OfficeCodeCatalog.UsenetMainWarehouse, StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("USENET", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("UZNET", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("유즈넷", StringComparison.OrdinalIgnoreCase))
        {
            officeCode = OfficeCodeCatalog.Usenet;
            return true;
        }

        officeCode = string.Empty;
        return false;
    }

    private static string NormalizeExactDuplicateText(string? value)
        => (value ?? string.Empty).Trim();

    private static string BuildDuplicateDisplay(IEnumerable<string> values)
    {
        var rows = values
            .Select(value => NormalizeDisplay(value, string.Empty))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(6)
            .ToList();
        return string.Join(" / ", rows);
    }

    private static string ShortId(Guid id)
        => id.ToString("N")[..8];

    private static int CountFilledCustomerValues(LocalCustomer customer)
    {
        var values = new[]
        {
            customer.NameOriginal,
            customer.BusinessNumber,
            customer.Department,
            customer.ContactPerson,
            customer.Address,
            customer.DetailAddress,
            customer.Phone,
            customer.MobilePhone,
            customer.Email,
            customer.Representative,
            customer.BusinessType,
            customer.BusinessItem,
            customer.Notes
        };
        return values.Count(value => !string.IsNullOrWhiteSpace(value)) + (customer.CategoryId.HasValue ? 1 : 0);
    }

    private static int CountFilledItemValues(LocalItem item)
    {
        var values = new[]
        {
            item.NameOriginal,
            item.SpecificationOriginal,
            item.CategoryName,
            item.ItemKind,
            item.TrackingType,
            item.Unit,
            item.StorageLocation,
            item.SimpleMemo,
            item.SerialNumber,
            item.MaterialNumber,
            item.InstallLocation,
            item.Notes
        };
        return values.Count(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string FormatVoucherType(VoucherType voucherType)
        => voucherType switch
        {
            VoucherType.Sales => "판매",
            VoucherType.Purchase => "구매",
            VoucherType.Procurement => "발주",
            VoucherType.Expense => "경비",
            VoucherType.Collection => "수금",
            _ => voucherType.ToString()
        };

    private static ParsedTemplateItems ParseTemplateItems(LocalRentalBillingProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.BillingTemplateJson))
            return new ParsedTemplateItems(true, []);

        if (!RentalBillingTemplateAssetCoverageRules.TryGetExplicitIncludedAssetIds(
                profile.BillingTemplateJson,
                out _,
                out var hasDuplicateReferences) ||
            hasDuplicateReferences)
        {
            return new ParsedTemplateItems(false, []);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(profile.BillingTemplateJson, JsonOptions) ?? [];
            var normalized = parsed
                .Where(item => item is not null)
                .Select(item => new RentalBillingTemplateItemModel
                {
                    ItemId = item.ItemId == Guid.Empty ? Guid.NewGuid() : item.ItemId,
                    CatalogItemId = item.CatalogItemId.HasValue && item.CatalogItemId.Value != Guid.Empty ? item.CatalogItemId.Value : null,
                    DisplayItemName = NormalizeDisplay(item.DisplayItemName, profile.ItemName),
                    BillingLineMode = item.BillingLineMode ?? string.Empty,
                    RepresentativeAssetId = item.RepresentativeAssetId,
                    Quantity = item.Quantity <= 0m ? 1m : item.Quantity,
                    UnitPrice = Math.Max(0m, item.UnitPrice),
                    Amount = Math.Max(0m, item.Amount),
                    Note = item.Note ?? string.Empty,
                    IncludedAssetIds = item.IncludedAssetIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? []
                })
                .ToList();
            return new ParsedTemplateItems(true, normalized);
        }
        catch
        {
            return new ParsedTemplateItems(false, []);
        }
    }

    private static List<RentalBillingRunModel> ParseRentalBillingRuns(string? json)
        => TryParseRentalBillingRuns(json, out var runs) ? runs : [];

    private static bool TryParseRentalBillingRuns(
        string? json,
        out List<RentalBillingRunModel> runs)
    {
        runs = [];
        if (string.IsNullOrWhiteSpace(json))
            return true;

        if (!RentalBillingRunTombstonePolicy.Validate(json).IsValid)
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<List<RentalBillingRunModel>>(json, JsonOptions);
            if (parsed is null)
                return true;
            if (parsed.Any(run => run is null))
                return false;

            runs = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<RentalBillingRunModel> ParseRentalBillingRunsForIdentityDiagnosis(string? json)
        => TryParseRentalBillingRunsForIdentityDiagnosis(json, out var runs) ? runs : [];

    private static bool TryParseRentalBillingRunsForIdentityDiagnosis(
        string? json,
        out List<RentalBillingRunModel> runs)
    {
        if (TryParseRentalBillingRuns(json, out runs))
            return true;

        return RentalBillingRunDiagnosticParser.TryParseIdentityConflictPayload(
            json,
            JsonOptions,
            out runs);
    }

    private static Guid NormalizeRunId(Guid? runId)
        => runId.HasValue && runId.Value != Guid.Empty ? runId.Value : Guid.Empty;

    private static IQueryable<LocalInvoice> ApplyOperationalAlertInvoiceScopePrefilter(
        IQueryable<LocalInvoice> query,
        SessionState session)
    {
        var officeCodes = BuildOperationalAlertOfficeCodeQueryAliases(session);
        var sharedOfficeCodes = BuildSharedOfficeCodeQueryAliases();

        return query.Where(invoice =>
            officeCodes.Contains(invoice.ResponsibleOfficeCode) ||
            officeCodes.Contains(invoice.OfficeCode) ||
            invoice.ResponsibleOfficeCode == null ||
            invoice.ResponsibleOfficeCode == string.Empty ||
            sharedOfficeCodes.Contains(invoice.ResponsibleOfficeCode) ||
            invoice.OfficeCode == null ||
            invoice.OfficeCode == string.Empty ||
            sharedOfficeCodes.Contains(invoice.OfficeCode));
    }

    private static IQueryable<LocalRentalBillingProfile> ApplyOperationalAlertRentalProfileScopePrefilter(
        IQueryable<LocalRentalBillingProfile> query,
        SessionState session)
    {
        var officeCodes = BuildOperationalAlertOfficeCodeQueryAliases(session);
        var sharedOfficeCodes = BuildSharedOfficeCodeQueryAliases();

        return query.Where(profile =>
            officeCodes.Contains(profile.ResponsibleOfficeCode) ||
            officeCodes.Contains(profile.OfficeCode) ||
            officeCodes.Contains(profile.ManagementCompanyCode) ||
            profile.ResponsibleOfficeCode == null ||
            profile.ResponsibleOfficeCode == string.Empty ||
            sharedOfficeCodes.Contains(profile.ResponsibleOfficeCode) ||
            profile.OfficeCode == null ||
            profile.OfficeCode == string.Empty ||
            sharedOfficeCodes.Contains(profile.OfficeCode) ||
            profile.ManagementCompanyCode == null ||
            profile.ManagementCompanyCode == string.Empty ||
            sharedOfficeCodes.Contains(profile.ManagementCompanyCode));
    }

    private static IQueryable<LocalRentalAsset> ApplyOperationalAlertRentalAssetScopePrefilter(
        IQueryable<LocalRentalAsset> query,
        SessionState session)
    {
        var officeCodes = BuildOperationalAlertOfficeCodeQueryAliases(session);
        var sharedOfficeCodes = BuildSharedOfficeCodeQueryAliases();

        return query.Where(asset =>
            officeCodes.Contains(asset.ResponsibleOfficeCode) ||
            officeCodes.Contains(asset.OfficeCode) ||
            officeCodes.Contains(asset.ManagementCompanyCode) ||
            asset.ResponsibleOfficeCode == null ||
            asset.ResponsibleOfficeCode == string.Empty ||
            sharedOfficeCodes.Contains(asset.ResponsibleOfficeCode) ||
            asset.OfficeCode == null ||
            asset.OfficeCode == string.Empty ||
            sharedOfficeCodes.Contains(asset.OfficeCode) ||
            asset.ManagementCompanyCode == null ||
            asset.ManagementCompanyCode == string.Empty ||
            sharedOfficeCodes.Contains(asset.ManagementCompanyCode));
    }

    private static IQueryable<LocalRentalAssetAssignmentHistory> ApplyOperationalAlertRentalAssignmentHistoryScopePrefilter(
        IQueryable<LocalRentalAssetAssignmentHistory> query,
        SessionState session)
    {
        var officeCodes = BuildOperationalAlertOfficeCodeQueryAliases(session);
        var sharedOfficeCodes = BuildSharedOfficeCodeQueryAliases();

        return query.Where(history =>
            officeCodes.Contains(history.ResponsibleOfficeCode) ||
            history.ResponsibleOfficeCode == null ||
            history.ResponsibleOfficeCode == string.Empty ||
            sharedOfficeCodes.Contains(history.ResponsibleOfficeCode));
    }

    private static IQueryable<LocalCustomer> ApplyOperationalAlertCustomerScopePrefilter(
        IQueryable<LocalCustomer> query,
        SessionState session)
    {
        if (session.HasGlobalDataScope)
            return query;

        var officeCodes = BuildOperationalAlertOfficeCodeQueryAliases(session);
        var sharedOfficeCodes = BuildSharedOfficeCodeQueryAliases();

        return query.Where(customer =>
            officeCodes.Contains(customer.ResponsibleOfficeCode) ||
            officeCodes.Contains(customer.OfficeCode) ||
            customer.ResponsibleOfficeCode == null ||
            customer.ResponsibleOfficeCode == string.Empty ||
            sharedOfficeCodes.Contains(customer.ResponsibleOfficeCode) ||
            customer.OfficeCode == null ||
            customer.OfficeCode == string.Empty ||
            sharedOfficeCodes.Contains(customer.OfficeCode));
    }

    private static IQueryable<LocalItem> ApplyOperationalAlertItemScopePrefilter(
        IQueryable<LocalItem> query,
        SessionState session)
    {
        if (session.HasGlobalDataScope)
            return query;

        var officeCodes = BuildOperationalAlertOfficeCodeQueryAliases(session);
        var sharedOfficeCodes = BuildSharedOfficeCodeQueryAliases();

        return query.Where(item =>
            officeCodes.Contains(item.OfficeCode) ||
            item.OfficeCode == null ||
            item.OfficeCode == string.Empty ||
            sharedOfficeCodes.Contains(item.OfficeCode));
    }

    private static List<string> BuildOperationalAlertOfficeCodeQueryAliases(SessionState session)
    {
        var sessionTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(session.TenantCode, session.OfficeCode);
        return ResolveOperationalAlertOfficeCodes(session, sessionTenant)
            .SelectMany(BuildOfficeCodeQueryAliases)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> BuildSharedOfficeCodeQueryAliases()
        =>
        [
            OfficeCodeCatalog.Shared,
            "공용",
            "전체",
            "shared"
        ];

    private static IEnumerable<string> BuildOfficeCodeQueryAliases(string officeCode)
    {
        var normalized = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode);
        yield return normalized;

        if (string.Equals(normalized, OfficeCodeCatalog.Usenet, StringComparison.OrdinalIgnoreCase))
        {
            yield return "UZNET";
            yield return "유즈넷";
        }
        else if (string.Equals(normalized, OfficeCodeCatalog.Itworld, StringComparison.OrdinalIgnoreCase))
        {
            yield return "아이티월드";
        }
        else if (string.Equals(normalized, OfficeCodeCatalog.Yeonsu, StringComparison.OrdinalIgnoreCase))
        {
            yield return "연수구";
            yield return "연수구 사무실";
        }
    }

    private static bool IsInSessionScope(string? tenantCode, string? officeCode, SessionState session)
    {
        if (session.HasGlobalDataScope)
            return true;

        var normalizedOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, session.OfficeCode);
        var normalizedTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, normalizedOffice);
        var sessionTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(session.TenantCode, session.OfficeCode);
        if (!string.Equals(normalizedTenant, sessionTenant, StringComparison.OrdinalIgnoreCase))
            return false;

        var offices = TenantScopeCatalog.ResolveScopedOfficeCodes(
            session.OfficeCode,
            sessionTenant,
            session.ScopeType,
            session.HasGlobalDataScope);
        return offices.Contains(normalizedOffice);
    }

    private static bool IsIssueInSessionScope(DataIntegrityIssueDetail issue, SessionState session)
        => session.HasGlobalDataScope || IsInSessionScope(null, issue.OfficeCode, session);

    private static HashSet<string> ResolveOperationalAlertOfficeCodes(SessionState session, string? normalizedTenantCode = null)
    {
        var sessionOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, DomainConstants.OfficeUsenet);
        var sessionTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            normalizedTenantCode ?? session.TenantCode,
            sessionOffice);
        return TenantScopeCatalog.ResolveScopedOfficeCodes(
            sessionOffice,
            sessionTenant,
            session.ScopeType,
            session.HasGlobalDataScope);
    }

    private static RentalAssetBillingEligibility ClassifyOperatingAssetBillingEligibility(
        LocalRentalAsset asset,
        bool hasValidLinkedProfile)
    {
        var status = (asset.AssetStatus ?? string.Empty).Trim();
        if (RentalAssetStatusRules.IsNonOperating(status))
            return RentalAssetBillingEligibility.NotApplicable;

        var eligibility = (asset.BillingEligibilityStatus ?? string.Empty).Trim();
        if (string.Equals(eligibility, "청구제외", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(eligibility, "청구 제외", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(eligibility, "Excluded", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(eligibility, "NotBillable", StringComparison.OrdinalIgnoreCase))
        {
            return RentalAssetBillingEligibility.NotApplicable;
        }

        if (string.Equals(eligibility, "청구대상", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(eligibility, "청구 대상", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(eligibility, "Billable", StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(eligibility) && hasValidLinkedProfile))
        {
            return RentalAssetBillingEligibility.Billable;
        }

        return RentalAssetBillingEligibility.NeedsReview;
    }

    private static string BuildAssetBillingReviewInfo(
        LocalRentalAsset asset,
        IReadOnlyCollection<AssetTemplateReference> templateReferences)
    {
        var eligibility = NormalizeDisplay(asset.BillingEligibilityStatus, "공백");
        var profileId = asset.BillingProfileId.HasValue && asset.BillingProfileId.Value != Guid.Empty
            ? asset.BillingProfileId.Value.ToString("D")
            : "없음";
        var templateReview = templateReferences.Count == 0
            ? "템플릿 참조 없음"
            : $"템플릿 참조 {templateReferences.Count:N0}건: " + string.Join(" | ", templateReferences.Select(reference =>
                $"{NormalizeDisplay(reference.ProfileDisplayName, "프로필")} / {NormalizeDisplay(reference.ItemName, "품목")} / 월금액 {FormatMoney(reference.MonthlyAmount)} / 포함 자산 {reference.IncludedAssetCount:N0}개 / {(reference.IncludedAssetCount == 1 ? "개별금액 확인 가능" : "자동 배분 불가")}"));

        return $"청구상태 {eligibility} / BillingProfileId {profileId} / {templateReview}";
    }

    private static decimal ResolveTemplateMonthlyAmount(RentalBillingTemplateItemModel item)
    {
        var quantity = item.Quantity <= 0m ? 1m : item.Quantity;
        var unitPrice = Math.Max(0m, item.UnitPrice);
        var calculated = quantity * unitPrice;
        return calculated > 0m ? calculated : Math.Max(0m, item.Amount);
    }

    private static bool AmountDiffers(decimal left, decimal right)
        => Math.Abs(left - right) >= 1m;

    private static string FormatOptionalAmount(decimal? value)
        => value.HasValue ? value.Value.ToString("N0") : "행 없음";

    private static string BuildInvoiceLinkedTransactionPaymentMismatchReason(
        Guid invoiceId,
        decimal transactionSettlementAmount,
        Guid? paymentId,
        Guid? paymentInvoiceId,
        decimal? paymentAmount,
        bool? paymentIsDeleted)
    {
        if (!paymentId.HasValue)
            return "수금·지급 행 없음";
        if (paymentIsDeleted == true)
            return "수금·지급 삭제상태";
        if (paymentInvoiceId != invoiceId)
            return "수금·지급 전표 링크 불일치";
        if (!paymentAmount.HasValue || AmountDiffers(paymentAmount.Value, transactionSettlementAmount))
            return "수금·지급 금액 불일치";

        return "전표 연결 거래내역/수금·지급 불일치";
    }

    private static RentalOperationalScope ResolveProfileScope(LocalRentalBillingProfile profile)
        => RentalScopeNormalizer.ResolveScope(
            profile.TenantCode,
            profile.OfficeCode,
            profile.ManagementCompanyCode,
            profile.ResponsibleOfficeCode,
            OfficeCodeCatalog.Usenet);

    private static RentalOperationalScope ResolveAssetScope(LocalRentalAsset asset)
        => RentalScopeNormalizer.ResolveScope(
            asset.TenantCode,
            asset.OfficeCode,
            asset.ManagementCompanyCode,
            asset.ResponsibleOfficeCode,
            OfficeCodeCatalog.Usenet);

    private static string ResolveProfileOfficeCode(LocalRentalBillingProfile profile)
        => ResolveProfileScope(profile).ResponsibleOfficeCode;

    private static string ResolveAssetOfficeCode(LocalRentalAsset asset)
        => ResolveAssetScope(asset).ResponsibleOfficeCode;

    private static string BuildProfileDisplay(LocalRentalBillingProfile profile)
    {
        var customer = NormalizeDisplay(profile.CustomerName, "거래처 미지정");
        var site = NormalizeDisplay(profile.InstallSiteName, string.Empty);
        var item = NormalizeDisplay(profile.ItemName, string.Empty);
        return string.Join(" / ", new[] { customer, site, item }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildAssetDisplay(LocalRentalAsset asset)
    {
        var number = NormalizeDisplay(asset.ManagementNumber, asset.ManagementId);
        var customer = NormalizeDisplay(asset.CurrentCustomerName, asset.CustomerName);
        var item = NormalizeDisplay(asset.ItemName, "품목 미지정");
        return string.Join(" / ", new[] { number, customer, item }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string BuildHistoryDisplay(LocalRentalAssetAssignmentHistory history)
    {
        var number = NormalizeDisplay(history.ManagementNumber, history.MachineNumber);
        var customer = NormalizeDisplay(history.CustomerName, "거래처 미지정");
        var item = NormalizeDisplay(history.ItemName, "품목 미지정");
        var period = string.Join("~", new[]
        {
            history.ContractStartDate?.ToString("yyyy-MM-dd") ?? string.Empty,
            history.ContractEndDate?.ToString("yyyy-MM-dd") ?? string.Empty
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.Join(" / ", new[] { number, customer, item, period }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string FormatNullableGuid(Guid value)
        => value == Guid.Empty ? "미지정" : value.ToString("D");

    private static bool IsProfileScopeInconsistent(LocalRentalBillingProfile profile)
    {
        var canonicalScope = ResolveProfileScope(profile);
        return RequiresExactTenantCode(profile.TenantCode, canonicalScope.TenantCode) ||
               RequiresExactOfficeScopeCode(profile.OfficeCode, canonicalScope.OwnerOfficeCode) ||
               IsManagementCompanyScopeInconsistent(
                   profile.ManagementCompanyCode,
                   canonicalScope) ||
               RequiresExactOfficeCode(profile.ResponsibleOfficeCode, canonicalScope.ResponsibleOfficeCode);
    }

    private static bool IsAssetScopeInconsistent(LocalRentalAsset asset)
    {
        var canonicalScope = ResolveAssetScope(asset);
        return RequiresExactTenantCode(asset.TenantCode, canonicalScope.TenantCode) ||
               RequiresExactOfficeScopeCode(asset.OfficeCode, canonicalScope.OwnerOfficeCode) ||
               IsManagementCompanyScopeInconsistent(
                   asset.ManagementCompanyCode,
                   canonicalScope) ||
               RequiresExactOfficeCode(asset.ResponsibleOfficeCode, canonicalScope.ResponsibleOfficeCode);
    }

    private static bool IsManagementCompanyScopeInconsistent(
        string? managementCompanyCode,
        RentalOperationalScope canonicalScope)
    {
        if (!string.Equals(
                canonicalScope.OwnerOfficeCode,
                OfficeCodeCatalog.Shared,
                StringComparison.OrdinalIgnoreCase))
        {
            return RequiresExactOfficeCode(
                managementCompanyCode,
                canonicalScope.OwnerOfficeCode);
        }

        var responsibleOwnerOfficeCode =
            OfficeCodeCatalog.ResolveOwningOfficeCode(
                null,
                canonicalScope.ResponsibleOfficeCode,
                canonicalScope.ResponsibleOfficeCode);
        return RequiresExactOfficeScopeCode(
                   managementCompanyCode,
                   OfficeCodeCatalog.Shared) &&
               RequiresExactOfficeCode(
                   managementCompanyCode,
                   responsibleOwnerOfficeCode);
    }

    private static string BuildProfileScopeDisplay(LocalRentalBillingProfile profile)
    {
        var scope = ResolveProfileScope(profile);
        return $"{scope.TenantCode} / {scope.OwnerOfficeCode} / {scope.ResponsibleOfficeCode} / 프로필 {profile.Id:D}";
    }

    private static string BuildStoredProfileScopeDisplay(LocalRentalBillingProfile profile)
        => $"{NormalizeTenantForDisplay(profile.TenantCode, profile.OfficeCode, profile.ResponsibleOfficeCode)} / {NormalizeOfficeScopeForDisplay(profile.OfficeCode, profile.ManagementCompanyCode)} / {NormalizeOfficeForDisplay(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode)} / 프로필 {profile.Id:D}";

    private static string BuildAssetScopeDisplay(LocalRentalAsset asset)
    {
        var scope = ResolveAssetScope(asset);
        var billingProfileText = asset.BillingProfileId.HasValue ? asset.BillingProfileId.Value.ToString("D") : "미연결";
        return $"{scope.TenantCode} / {scope.OwnerOfficeCode} / {scope.ResponsibleOfficeCode} / 프로필 {billingProfileText}";
    }

    private static string BuildStoredAssetScopeDisplay(LocalRentalAsset asset)
    {
        var billingProfileText = asset.BillingProfileId.HasValue ? asset.BillingProfileId.Value.ToString("D") : "미연결";
        return $"{NormalizeTenantForDisplay(asset.TenantCode, asset.OfficeCode, asset.ResponsibleOfficeCode)} / {NormalizeOfficeScopeForDisplay(asset.OfficeCode, asset.ManagementCompanyCode)} / {NormalizeOfficeForDisplay(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode)} / 프로필 {billingProfileText}";
    }

    private static string NormalizeOfficeForDisplay(string? officeCode, string? fallbackOfficeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, fallbackOfficeCode);

    private static string NormalizeOfficeScopeForDisplay(string? officeCode, string? fallbackOfficeCode)
        => OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(officeCode, fallbackOfficeCode);

    private static string NormalizeTenantForDisplay(string? tenantCode, string? officeCode, string? responsibleOfficeCode)
        => TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode, tenantCode, responsibleOfficeCode);

    private static bool RequiresExactOfficeCode(string? currentOfficeCode, string expectedOfficeCode)
        => !OfficeCodeCatalog.TryNormalizeOfficeCode(currentOfficeCode, out var normalizedOfficeCode) ||
           !string.Equals(normalizedOfficeCode, expectedOfficeCode, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals((currentOfficeCode ?? string.Empty).Trim(), expectedOfficeCode, StringComparison.OrdinalIgnoreCase);

    private static bool RequiresExactOfficeScopeCode(string? currentOfficeCode, string expectedOfficeCode)
        => !OfficeCodeCatalog.TryNormalizeScope(currentOfficeCode, out var normalizedOfficeCode) ||
           !string.Equals(normalizedOfficeCode, expectedOfficeCode, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals((currentOfficeCode ?? string.Empty).Trim(), expectedOfficeCode, StringComparison.OrdinalIgnoreCase);

    private static bool RequiresExactTenantCode(string? currentTenantCode, string expectedTenantCode)
        => !TenantScopeCatalog.TryNormalizeTenantCode(currentTenantCode, out var normalizedTenantCode) ||
           !string.Equals(normalizedTenantCode, expectedTenantCode, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals((currentTenantCode ?? string.Empty).Trim(), expectedTenantCode, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetLinkedCustomerNameMismatch(
        Guid? customerId,
        IReadOnlyDictionary<Guid, LocalCustomer> customersById,
        IEnumerable<string?> storedCustomerNames,
        out string masterCustomerName,
        out string storedCustomerName)
    {
        masterCustomerName = string.Empty;
        storedCustomerName = string.Empty;
        if (!customerId.HasValue || customerId.Value == Guid.Empty)
            return false;

        if (!customersById.TryGetValue(customerId.Value, out var customer))
            return false;

        masterCustomerName = NormalizeDisplay(customer.NameOriginal, string.Empty);
        if (string.IsNullOrWhiteSpace(masterCustomerName))
            return false;

        var masterKey = RentalCatalogValueNormalizer.NormalizeLooseKey(masterCustomerName);
        if (string.IsNullOrWhiteSpace(masterKey))
            return false;

        var distinctDisplays = storedCustomerNames
            .Select(value => NormalizeDisplay(value, string.Empty))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (distinctDisplays.Count == 0)
        {
            storedCustomerName = "(비어 있음)";
            return true;
        }

        var mismatches = distinctDisplays
            .Where(value => !string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(value), masterKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (mismatches.Count == 0)
            return false;

        storedCustomerName = string.Join(" / ", mismatches);
        return true;
    }

    private static string NormalizeDisplay(string? value, string fallback)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? fallback : trimmed;
    }

    private static bool HasReadableLocalAttachmentFile(string? storedPath)
    {
        var trimmed = (storedPath ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(trimmed) && File.Exists(trimmed);
    }

    private static string FormatMoney(decimal value)
        => $"{value:N0}원";

    private enum RentalAssetBillingEligibility
    {
        NotApplicable,
        Billable,
        NeedsReview
    }

    private sealed record ParsedTemplateItems(bool Success, List<RentalBillingTemplateItemModel> Items);

    private sealed record AssetTemplateReference(
        Guid ProfileId,
        string ProfileDisplayName,
        string ItemName,
        decimal MonthlyAmount,
        int IncludedAssetCount);

    private sealed class CustomerDuplicateUsage
    {
        public static CustomerDuplicateUsage Empty { get; } = new();

        public int InvoiceCount { get; set; }
        public int TransactionCount { get; set; }
        public int RentalBillingProfileCount { get; set; }
        public int RentalAssetCount { get; set; }
        public int RentalAssignmentHistoryCount { get; set; }
        public int CustomerContractCount { get; set; }
        public int TotalCount => InvoiceCount +
                                 TransactionCount +
                                 RentalBillingProfileCount +
                                 RentalAssetCount +
                                 RentalAssignmentHistoryCount +
                                 CustomerContractCount;
    }

    private sealed class ItemDuplicateUsage
    {
        public static ItemDuplicateUsage Empty { get; } = new();

        public int InvoiceLineCount { get; set; }
        public int InvoiceLineSerialCount { get; set; }
        public int RentalAssetCount { get; set; }
        public int RentalBillingTemplateCount { get; set; }
        public int InventoryTransferLineCount { get; set; }
        public int InventoryMovementCount { get; set; }
        public int StockLayerCount { get; set; }
        public int SerialLedgerCount { get; set; }
        public int ItemWarehouseStockRowCount { get; set; }
        public int ItemPriceGradeCount { get; set; }
        public decimal ItemWarehouseStockQuantity { get; set; }
        public HashSet<string> ReferenceIdentityTokens { get; } = new(StringComparer.Ordinal);
        public string ReferenceIdentityFingerprint
        {
            get
            {
                var payload = string.Join("\n", ReferenceIdentityTokens.OrderBy(token => token, StringComparer.Ordinal));
                return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
            }
        }
        public int TotalCount => InvoiceLineCount +
                                 InvoiceLineSerialCount +
                                 RentalAssetCount +
                                 RentalBillingTemplateCount +
                                 InventoryTransferLineCount +
                                 InventoryMovementCount +
                                 StockLayerCount +
                                 SerialLedgerCount +
                                 ItemWarehouseStockRowCount +
                                 ItemPriceGradeCount;
    }

    private sealed class ReferenceCountRow
    {
        public Guid EntityId { get; init; }
        public int Count { get; init; }
    }

    private sealed class ItemStockRow
    {
        public Guid EntityId { get; init; }
        public decimal Quantity { get; init; }
    }

    private sealed class IntegrityInvoiceSnapshot
    {
        public Guid Id { get; init; }
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = string.Empty;
        public string ResponsibleOfficeCode { get; init; } = DomainConstants.OfficeUsenet;
        public string InvoiceNumber { get; init; } = string.Empty;
        public VoucherType VoucherType { get; init; }
        public DateOnly InvoiceDate { get; init; }
        public decimal TotalAmount { get; init; }
        public decimal SupplyAmount { get; init; }
        public decimal VatAmount { get; init; }
        public string VatMode { get; init; } = InvoiceVatModes.Included;
    }
}
