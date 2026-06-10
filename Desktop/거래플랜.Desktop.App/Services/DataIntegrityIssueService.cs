using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
    public const string RentalOperationalScopeMismatch = "rental_operational_scope_mismatch";
    public const string RentalCustomerNameMismatch = "rental_customer_name_mismatch";
    public const string RentalAssetInMultipleProfileTemplates = "rental_asset_in_multiple_profile_templates";
    public const string RentalProfileWithoutLinkedAssets = "rental_profile_without_linked_assets";
    public const string RentalBillableAssetWithoutMonthlyFee = "rental_billable_asset_without_monthly_fee";
    public const string RentalAssetMissingBillingProfile = "rental_asset_missing_billing_profile";
    public const string RentalAssignmentMissingReference = "rental_assignment_missing_reference";
    public const string RentalAssetMultipleCurrentAssignments = "rental_asset_multiple_current_assignments";
    public const string CustomerDuplicateCandidate = "customer_duplicate_candidate";
    public const string ItemDuplicateCandidate = "item_duplicate_candidate";
    public const string WarehouseDuplicateCandidate = "warehouse_duplicate_candidate";
    public const string InvoiceAmountMismatch = "invoice_amount_mismatch";
    public const string InvoiceOverSettled = "invoice_over_settled";
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
    public string SeverityDisplay => string.Equals(Severity, "Error", StringComparison.OrdinalIgnoreCase) ? "오류" : "주의";
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

    public bool HasDirectAction => DirectActionKind != DataIntegrityDirectActionKind.None;
    public string SeverityDisplay => string.Equals(Severity, "Error", StringComparison.OrdinalIgnoreCase) ? "오류" : "주의";
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
}

public sealed class DataIntegrityIssueFilterOption
{
    public string Code { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    public override string ToString() => DisplayName;
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
    public bool HasIssues => Issues.Count > 0;
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
        [DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee] = new(
            DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee,
            "청구대상 자산 월요금 없음",
            "Warning",
            "렌탈 자산",
            "청구대상 자산인데 월요금이 0원입니다.",
            "자산 화면에서 월요금을 입력한 뒤 연결 청구 프로필에 반영하세요."),
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
            "렌탈 임대이력이 존재하지 않거나 삭제된 자산/거래처/청구 프로필을 참조합니다.",
            "임대이력 상세를 확인한 뒤 자산·거래처·청구 프로필을 재연결하거나 과거 이력으로 보존 처리하세요."),
        [DataIntegrityIssueCodes.RentalAssetMultipleCurrentAssignments] = new(
            DataIntegrityIssueCodes.RentalAssetMultipleCurrentAssignments,
            "현재 임대이력 중복",
            "Error",
            "렌탈 이력",
            "하나의 렌탈 자산에 현재 임대중으로 표시된 이력이 여러 개 있습니다.",
            "임대이력에서 실제 현재 이력 1건만 남기고 나머지는 과거 이력으로 수정하세요."),
        [DataIntegrityIssueCodes.CustomerDuplicateCandidate] = new(
            DataIntegrityIssueCodes.CustomerDuplicateCandidate,
            "거래처 중복 후보",
            "Warning",
            "거래처",
            "같은 테넌트/담당지점 안에 이름 또는 사업자번호가 같은 거래처가 여러 개 있습니다.",
            "목록을 확인한 뒤 실제 같은 거래처인 항목만 수동 병합 또는 정리하세요."),
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

    public DataIntegrityIssueService(LocalDbContext db)
    {
        _db = db;
    }

    public async Task<DataIntegrityScanResult> ScanAsync(SessionState session, CancellationToken ct = default)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var stepStopwatch = Stopwatch.StartNew();
        var activeProfiles = await _db.RentalBillingProfiles
            .AsNoTracking()
            .Where(profile => !profile.IsDeleted && profile.IsActive)
            .ToListAsync(ct);
        var activeAssets = await _db.RentalAssets
            .AsNoTracking()
            .Where(asset => !asset.IsDeleted)
            .ToListAsync(ct);
        var activeAssignmentHistories = await _db.RentalAssetAssignmentHistories
            .AsNoTracking()
            .Where(history => !history.IsDeleted)
            .ToListAsync(ct);
        var activeCustomers = await ApplyOperationalAlertCustomerScopePrefilter(
                _db.Customers
                    .AsNoTracking()
                    .Where(customer => !customer.IsDeleted),
                session)
            .ToListAsync(ct);
        var activeItems = await ApplyOperationalAlertItemScopePrefilter(
                _db.Items
                    .AsNoTracking()
                    .Where(item => !item.IsDeleted),
                session)
            .ToListAsync(ct);
        var activeWarehouses = await ApplyOperationalAlertWarehouseScopePrefilter(
                _db.Warehouses
                    .AsNoTracking()
                    .Where(warehouse => !warehouse.IsDeleted && warehouse.IsActive),
                session)
            .ToListAsync(ct);
        var activeInvoices = await ApplyOperationalAlertInvoiceScopePrefilter(
                _db.Invoices
                    .AsNoTracking()
                    .AsSplitQuery()
                    .Where(invoice => !invoice.IsDeleted && invoice.IsLatestVersion),
                session)
            .Include(invoice => invoice.Lines.Where(line => !line.IsDeleted))
            .Include(invoice => invoice.Payments.Where(payment => !payment.IsDeleted))
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
        var activeCustomersById = linkedCustomerIds.Count == 0
            ? new Dictionary<Guid, LocalCustomer>()
            : await _db.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer => linkedCustomerIds.Contains(customer.Id) && !customer.IsDeleted)
                .ToDictionaryAsync(customer => customer.Id, ct);
        var details = new List<DataIntegrityIssueDetail>();
        var scopedAssignmentHistories = activeAssignmentHistories
            .Where(history => IsInSessionScope(history.TenantCode, history.ResponsibleOfficeCode, session))
            .ToList();
        var scopedCustomers = activeCustomers
            .Where(customer => IsInSessionScope(customer.TenantCode, ResolveCustomerOfficeCode(customer), session))
            .ToList();
        var scopedItems = activeItems
            .Where(item => IsInSessionScope(item.TenantCode, item.OfficeCode, session))
            .ToList();
        var scopedWarehouses = activeWarehouses
            .Where(warehouse => IsInSessionScope(null, warehouse.OfficeCode, session))
            .ToList();
        var scopedInvoices = activeInvoices
            .Where(invoice => IsInSessionScope(invoice.TenantCode, invoice.ResponsibleOfficeCode, session))
            .ToList();
        LogIntegrityScanStep(
            "Integrity scan scope filter",
            stepStopwatch,
            $"profiles={scopedProfiles.Count:N0}, assets={scopedAssets.Count:N0}, histories={scopedAssignmentHistories.Count:N0}, customers={scopedCustomers.Count:N0}, items={scopedItems.Count:N0}, invoices={scopedInvoices.Count:N0}");

        stepStopwatch.Restart();
        var scopedItemIds = scopedItems
            .Select(item => item.Id)
            .Distinct()
            .ToList();
        var itemWarehouseStocks = await LoadItemWarehouseStocksForItemsAsync(scopedItemIds, ct);
        var inventoryMovements = await LoadInventoryMovementsForItemsAsync(scopedItemIds, ct);
        LogIntegrityScanStep(
            "Integrity scan inventory source load",
            stepStopwatch,
            $"stocks={itemWarehouseStocks.Count:N0}, movements={inventoryMovements.Count:N0}, scopedItems={scopedItemIds.Count:N0}");

        stepStopwatch.Restart();
        AddMasterDataAndLedgerIssues(
            details,
            scopedCustomers,
            scopedItems,
            scopedWarehouses,
            scopedInvoices,
            itemWarehouseStocks,
            inventoryMovements,
            session);
        LogIntegrityScanStep("Integrity scan master/ledger issues", stepStopwatch, $"issues={details.Count:N0}");

        stepStopwatch.Restart();
        foreach (var history in scopedAssignmentHistories)
        {
            allAssetsById.TryGetValue(history.AssetId, out var historyAsset);
            LocalRentalBillingProfile? historyProfile = null;
            if (history.BillingProfileId.HasValue && history.BillingProfileId.Value != Guid.Empty)
                activeProfilesById.TryGetValue(history.BillingProfileId.Value, out historyProfile);

            var missingReferences = new List<string>();
            if (history.AssetId == Guid.Empty || historyAsset is null)
                missingReferences.Add($"자산 {FormatNullableGuid(history.AssetId)}");
            if (history.CustomerId.HasValue && history.CustomerId.Value != Guid.Empty && !activeCustomersById.ContainsKey(history.CustomerId.Value))
                missingReferences.Add($"거래처 {history.CustomerId.Value:D}");
            if (history.BillingProfileId.HasValue && history.BillingProfileId.Value != Guid.Empty && historyProfile is null)
                missingReferences.Add($"청구 프로필 {history.BillingProfileId.Value:D}");

            if (missingReferences.Count > 0)
            {
                AddHistoryIssue(details, DataIntegrityIssueCodes.RentalAssignmentMissingReference, history, historyAsset, historyProfile,
                    currentValue: string.Join(" / ", missingReferences),
                    expectedValue: "활성 자산·거래처·청구 프로필 참조",
                    message: $"{BuildHistoryDisplay(history)} 임대이력이 누락/삭제된 참조를 포함합니다.");
            }
        }

        foreach (var group in scopedAssignmentHistories
                     .Where(history => history.IsCurrent)
                     .GroupBy(history => history.AssetId)
                     .Where(group => group.Key != Guid.Empty && group.Count() > 1))
        {
            allAssetsById.TryGetValue(group.Key, out var asset);
            var profile = group
                .Select(history => history.BillingProfileId)
                .Where(id => id.HasValue && id.Value != Guid.Empty)
                .Select(id => activeProfilesById.TryGetValue(id!.Value, out var found) ? found : null)
                .FirstOrDefault(found => found is not null);
            var currentDisplays = group
                .OrderByDescending(history => history.LinkedAtUtc)
                .Take(5)
                .Select(BuildHistoryDisplay)
                .ToList();
            var representativeHistory = group.OrderByDescending(history => history.LinkedAtUtc).First();

            AddHistoryIssue(details, DataIntegrityIssueCodes.RentalAssetMultipleCurrentAssignments, representativeHistory, asset, profile,
                currentValue: $"{group.Count():N0}건 / {string.Join(" / ", currentDisplays)}",
                expectedValue: "현재 임대이력 1건",
                message: $"{FormatNullableGuid(group.Key)} 자산에 현재 임대이력이 {group.Count():N0}건 있습니다.");
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

            var parsed = ParseTemplateItems(profile);
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

                    refs.Add(new AssetTemplateReference(profile.Id, BuildProfileDisplay(profile), item.DisplayItemName));

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

            if (linkedAssets.Count == 0 && profileAssetIds.Count == 0)
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

        foreach (var group in assetTemplateRefs.Where(pair => pair.Value.Select(reference => reference.ProfileId).Distinct().Count() > 1))
        {
            if (!allAssetsById.TryGetValue(group.Key, out var asset))
                continue;
            var assetScope = ResolveAssetScope(asset);
            if (!IsInSessionScope(assetScope.TenantCode, assetScope.ResponsibleOfficeCode, session))
                continue;

            var distinctProfiles = group.Value
                .GroupBy(reference => reference.ProfileId)
                .Select(grouping => grouping.First().ProfileDisplayName)
                .ToList();
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

            if (IsBillableOperatingAsset(asset) && asset.MonthlyFee <= 0m)
            {
                AddIssue(details, DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee, null, asset,
                    entityType: "렌탈 자산",
                    entityId: asset.Id,
                    currentValue: FormatMoney(asset.MonthlyFee),
                    expectedValue: "1원 이상",
                    message: $"{BuildAssetDisplay(asset)} 자산은 청구대상이나 월요금이 없습니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenRentalAsset);
            }
        }

        LogIntegrityScanStep("Integrity scan rental issues", stepStopwatch, $"issues={details.Count:N0}");

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
            .OrderByDescending(summary => string.Equals(summary.Severity, "Error", StringComparison.OrdinalIgnoreCase))
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
                .ToListAsync(ct));
        }

        return rows;
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
        IReadOnlyCollection<LocalInvoice> invoices,
        IReadOnlyCollection<LocalItemWarehouseStock> itemWarehouseStocks,
        IReadOnlyCollection<LocalInventoryMovement> inventoryMovements,
        SessionState session)
    {
        foreach (var group in customers
                     .Select(customer => new
                     {
                         Customer = customer,
                         Key = RentalCatalogValueNormalizer.NormalizeLooseKey(customer.NameOriginal),
                         OfficeCode = ResolveCustomerOfficeCode(customer),
                         TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(customer.TenantCode, ResolveCustomerOfficeCode(customer))
                     })
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                     .GroupBy(entry => $"{entry.TenantCode}|{entry.OfficeCode}|NAME|{entry.Key}", StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var rows = group.Select(entry => entry.Customer).OrderBy(customer => customer.NameOriginal).ToList();
            AddGeneralIssue(issues, DataIntegrityIssueCodes.CustomerDuplicateCandidate,
                entityType: "거래처",
                entityId: rows[0].Id,
                customerName: rows[0].NameOriginal,
                officeCode: ResolveCustomerOfficeCode(rows[0]),
                currentValue: BuildDuplicateDisplay(rows.Select(row => $"{row.NameOriginal}({row.Id:N})")),
                expectedValue: "같은 거래처이면 1건으로 정리",
                message: $"거래처명 '{rows[0].NameOriginal}' 기준 중복 후보 {rows.Count:N0}건이 있습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenCustomer);
        }

        foreach (var group in customers
                     .Select(customer => new
                     {
                         Customer = customer,
                         Key = NormalizeBusinessNumber(customer.BusinessNumber),
                         OfficeCode = ResolveCustomerOfficeCode(customer),
                         TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(customer.TenantCode, ResolveCustomerOfficeCode(customer))
                     })
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                     .GroupBy(entry => $"{entry.TenantCode}|{entry.OfficeCode}|BIZ|{entry.Key}", StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var rows = group.Select(entry => entry.Customer).OrderBy(customer => customer.NameOriginal).ToList();
            AddGeneralIssue(issues, DataIntegrityIssueCodes.CustomerDuplicateCandidate,
                entityType: "거래처",
                entityId: rows[0].Id,
                customerName: rows[0].NameOriginal,
                officeCode: ResolveCustomerOfficeCode(rows[0]),
                currentValue: BuildDuplicateDisplay(rows.Select(row => $"{row.NameOriginal} / {row.BusinessNumber}({row.Id:N})")),
                expectedValue: "같은 사업자이면 1건으로 정리",
                message: $"사업자번호 '{rows[0].BusinessNumber}' 기준 거래처 중복 후보 {rows.Count:N0}건이 있습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenCustomer);
        }

        foreach (var group in items
                     .Select(item => new
                     {
                         Item = item,
                         NameKey = RentalCatalogValueNormalizer.NormalizeLooseKey(item.NameOriginal),
                         SpecKey = RentalCatalogValueNormalizer.NormalizeLooseKey(item.SpecificationOriginal),
                         OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(item.OfficeCode, OfficeCodeCatalog.Shared),
                         TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(item.TenantCode, item.OfficeCode)
                     })
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.NameKey))
                     .GroupBy(entry => $"{entry.TenantCode}|{entry.OfficeCode}|{entry.NameKey}|{entry.SpecKey}", StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var rows = group.Select(entry => entry.Item).OrderBy(item => item.NameOriginal).ThenBy(item => item.SpecificationOriginal).ToList();
            AddGeneralIssue(issues, DataIntegrityIssueCodes.ItemDuplicateCandidate,
                entityType: "품목",
                entityId: rows[0].Id,
                itemName: rows[0].NameOriginal,
                officeCode: OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(rows[0].OfficeCode, OfficeCodeCatalog.Shared),
                currentValue: BuildDuplicateDisplay(rows.Select(row => $"{row.NameOriginal} / {row.SpecificationOriginal}({row.Id:N})")),
                expectedValue: "같은 품목이면 1건으로 정리",
                message: $"품목 '{rows[0].NameOriginal}' / 규격 '{rows[0].SpecificationOriginal}' 중복 후보 {rows.Count:N0}건이 있습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenInventoryItem);
        }

        foreach (var group in warehouses
                     .Select(warehouse => new
                     {
                         Warehouse = warehouse,
                         OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(warehouse.OfficeCode, session.OfficeCode),
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
            AddGeneralIssue(issues, DataIntegrityIssueCodes.WarehouseDuplicateCandidate,
                entityType: "창고",
                entityId: rows[0].Id,
                officeCode: rows[0].OfficeCode,
                currentValue: BuildDuplicateDisplay(rows.Select(row => $"{row.Code} / {row.Name}({row.Id:N})")),
                expectedValue: "같은 창고이면 1건으로 정리",
                message: $"담당지점 {rows[0].OfficeCode} 창고 중복 후보 {rows.Count:N0}건이 있습니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenEnvironmentSettings);
        }

        foreach (var invoice in invoices)
        {
            var activeLines = invoice.Lines.Where(line => !line.IsDeleted).ToList();
            var totals = InvoiceVatModes.CalculateTotals(activeLines.Select(line => line.LineAmount), invoice.VatMode);
            if (AmountDiffers(invoice.TotalAmount, totals.TotalAmount) ||
                AmountDiffers(invoice.SupplyAmount, totals.SupplyAmount) ||
                AmountDiffers(invoice.VatAmount, totals.VatAmount))
            {
                AddGeneralIssue(issues, DataIntegrityIssueCodes.InvoiceAmountMismatch,
                    entityType: "전표",
                    entityId: invoice.Id,
                    officeCode: invoice.ResponsibleOfficeCode,
                    currentValue: $"공급 {invoice.SupplyAmount:N0} / 부가세 {invoice.VatAmount:N0} / 합계 {invoice.TotalAmount:N0}",
                    expectedValue: $"공급 {totals.SupplyAmount:N0} / 부가세 {totals.VatAmount:N0} / 합계 {totals.TotalAmount:N0}",
                    message: $"{invoice.InvoiceDate:yyyy-MM-dd} {FormatVoucherType(invoice.VoucherType)} 전표 {NormalizeDisplay(invoice.InvoiceNumber, invoice.Id.ToString("N"))} 금액 계산이 품목 합계와 다릅니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenInvoice);
            }

            var settlementTotal = invoice.Payments.Where(payment => !payment.IsDeleted).Sum(payment => payment.Amount);
            if (settlementTotal - invoice.TotalAmount >= 1m)
            {
                AddGeneralIssue(issues, DataIntegrityIssueCodes.InvoiceOverSettled,
                    entityType: "전표",
                    entityId: invoice.Id,
                    officeCode: invoice.ResponsibleOfficeCode,
                    currentValue: $"전표 {invoice.TotalAmount:N0} / 수금·지급 {settlementTotal:N0}",
                    expectedValue: "수금·지급 합계가 전표 합계 이하",
                    message: $"{invoice.InvoiceDate:yyyy-MM-dd} {FormatVoucherType(invoice.VoucherType)} 전표 {NormalizeDisplay(invoice.InvoiceNumber, invoice.Id.ToString("N"))}의 수금/지급 합계가 전표 금액보다 큽니다.",
                    directActionKind: DataIntegrityDirectActionKind.OpenPaymentForInvoice);
            }
        }

        var scopedItemIds = items.Select(item => item.Id).ToHashSet();
        var itemNameById = items.ToDictionary(item => item.Id, item => item.NameOriginal);
        var stockByItem = itemWarehouseStocks
            .Where(stock => scopedItemIds.Contains(stock.ItemId))
            .GroupBy(stock => stock.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(stock => stock.Quantity));
        foreach (var item in items.Where(item => ItemOperationalPolicy.SupportsInventory(item.TrackingType)))
        {
            stockByItem.TryGetValue(item.Id, out var stockTotal);
            if (!AmountDiffers(item.CurrentStock, stockTotal))
                continue;

            AddGeneralIssue(issues, DataIntegrityIssueCodes.InventoryStockSnapshotMismatch,
                entityType: "품목",
                entityId: item.Id,
                itemName: item.NameOriginal,
                officeCode: item.OfficeCode,
                currentValue: $"품목 현재재고 {item.CurrentStock:N2}",
                expectedValue: $"창고별 합계 {stockTotal:N2}",
                message: $"{NormalizeDisplay(item.NameOriginal, "품목")} 품목의 현재재고와 창고별 재고 합계가 다릅니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenInventoryItem);
        }

        var activeWarehouseCodes = warehouses
            .Select(warehouse => OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(warehouse.Code, warehouse.OfficeCode))
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stock in itemWarehouseStocks.Where(stock => scopedItemIds.Contains(stock.ItemId)))
        {
            var warehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(stock.WarehouseCode, session.OfficeCode);
            if (activeWarehouseCodes.Contains(warehouseCode))
                continue;

            AddGeneralIssue(issues, DataIntegrityIssueCodes.InventoryWarehouseReferenceMissing,
                entityType: "재고",
                entityId: stock.ItemId,
                itemName: itemNameById.GetValueOrDefault(stock.ItemId) ?? string.Empty,
                officeCode: ResolveOfficeCodeFromWarehouseCode(warehouseCode, session.OfficeCode),
                currentValue: warehouseCode,
                expectedValue: "활성 창고 코드",
                message: $"품목 재고 스냅샷이 존재하지 않거나 비활성인 창고 '{warehouseCode}'를 참조합니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenInventoryItem);
        }

        foreach (var movement in inventoryMovements.Where(movement => movement.ItemId.HasValue && scopedItemIds.Contains(movement.ItemId.Value)))
        {
            var warehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(movement.WarehouseCode, session.OfficeCode);
            if (activeWarehouseCodes.Contains(warehouseCode))
                continue;

            AddGeneralIssue(issues, DataIntegrityIssueCodes.InventoryWarehouseReferenceMissing,
                entityType: "재고 이동",
                entityId: movement.ItemId,
                itemName: movement.ItemId.HasValue ? itemNameById.GetValueOrDefault(movement.ItemId.Value) ?? string.Empty : string.Empty,
                officeCode: ResolveOfficeCodeFromWarehouseCode(warehouseCode, session.OfficeCode),
                currentValue: warehouseCode,
                expectedValue: "활성 창고 코드",
                message: $"재고 이동 이력이 존재하지 않거나 비활성인 창고 '{warehouseCode}'를 참조합니다.",
                directActionKind: DataIntegrityDirectActionKind.OpenInventoryItem);
        }
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
        DataIntegrityDirectActionKind directActionKind = DataIntegrityDirectActionKind.None)
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
            DirectActionKind = directActionKind
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
                : DataIntegrityDirectActionKind.None;

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
            OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(history.ResponsibleOfficeCode, asset is null ? profile?.ResponsibleOfficeCode : ResolveAssetOfficeCode(asset)),
            CurrentValue = currentValue,
            ExpectedValue = expectedValue,
            Message = message,
            SuggestedAction = definition.SuggestedAction,
            DirectActionKind = directActionKind
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
        DataIntegrityDirectActionKind directActionKind = DataIntegrityDirectActionKind.None)
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
            DirectActionKind = directActionKind
        });
    }

    private static string ResolveCustomerOfficeCode(LocalCustomer customer)
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            string.IsNullOrWhiteSpace(customer.ResponsibleOfficeCode) ? customer.OfficeCode : customer.ResponsibleOfficeCode,
            DomainConstants.OfficeUsenet);

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

    private static string NormalizeBusinessNumber(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsDigit).ToArray());

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

        try
        {
            var parsed = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(profile.BillingTemplateJson, JsonOptions) ?? [];
            var normalized = parsed
                .Where(item => item is not null)
                .Select(item => new RentalBillingTemplateItemModel
                {
                    ItemId = item.ItemId == Guid.Empty ? Guid.NewGuid() : item.ItemId,
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

    private static IQueryable<LocalInvoice> ApplyOperationalAlertInvoiceScopePrefilter(
        IQueryable<LocalInvoice> query,
        SessionState session)
    {
        var officeCodes = BuildOperationalAlertOfficeCodeQueryAliases(session);
        var sharedOfficeCodes = BuildSharedOfficeCodeQueryAliases();

        return query.Where(invoice =>
            officeCodes.Contains(invoice.ResponsibleOfficeCode) ||
            invoice.ResponsibleOfficeCode == null ||
            invoice.ResponsibleOfficeCode == string.Empty ||
            sharedOfficeCodes.Contains(invoice.ResponsibleOfficeCode));
    }

    private static IQueryable<LocalCustomer> ApplyOperationalAlertCustomerScopePrefilter(
        IQueryable<LocalCustomer> query,
        SessionState session)
    {
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
        var officeCodes = BuildOperationalAlertOfficeCodeQueryAliases(session);
        var sharedOfficeCodes = BuildSharedOfficeCodeQueryAliases();

        return query.Where(item =>
            officeCodes.Contains(item.OfficeCode) ||
            item.OfficeCode == null ||
            item.OfficeCode == string.Empty ||
            sharedOfficeCodes.Contains(item.OfficeCode));
    }

    private static IQueryable<LocalWarehouse> ApplyOperationalAlertWarehouseScopePrefilter(
        IQueryable<LocalWarehouse> query,
        SessionState session)
    {
        var officeCodes = BuildOperationalAlertOfficeCodeQueryAliases(session);
        var sharedOfficeCodes = BuildSharedOfficeCodeQueryAliases();

        return query.Where(warehouse =>
            officeCodes.Contains(warehouse.OfficeCode) ||
            warehouse.OfficeCode == null ||
            warehouse.OfficeCode == string.Empty ||
            sharedOfficeCodes.Contains(warehouse.OfficeCode));
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
        var normalizedOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, session.OfficeCode);
        var normalizedTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, normalizedOffice);
        var sessionTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(session.TenantCode, session.OfficeCode);
        if (!string.Equals(normalizedTenant, sessionTenant, StringComparison.OrdinalIgnoreCase))
            return false;

        var offices = ResolveOperationalAlertOfficeCodes(session, sessionTenant);
        return offices.Contains(normalizedOffice);
    }

    private static HashSet<string> ResolveOperationalAlertOfficeCodes(SessionState session, string? normalizedTenantCode = null)
    {
        var sessionOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, DomainConstants.OfficeUsenet);
        var sessionTenant = TenantScopeCatalog.NormalizeTenantCodeOrDefault(normalizedTenantCode, session.TenantCode);

        if (string.Equals(sessionTenant, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sessionOffice, OfficeCodeCatalog.Itworld, StringComparison.OrdinalIgnoreCase))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                OfficeCodeCatalog.Itworld
            };
        }

        if (string.Equals(sessionOffice, OfficeCodeCatalog.Yeonsu, StringComparison.OrdinalIgnoreCase))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                OfficeCodeCatalog.Yeonsu
            };
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            OfficeCodeCatalog.Usenet,
            OfficeCodeCatalog.Yeonsu
        };
    }

    private static bool IsBillableOperatingAsset(LocalRentalAsset asset)
    {
        var status = (asset.AssetStatus ?? string.Empty).Trim();
        if (RentalAssetStatusRules.IsNonOperating(status))
            return false;

        var eligibility = (asset.BillingEligibilityStatus ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(eligibility)
               || string.Equals(eligibility, "청구대상", StringComparison.OrdinalIgnoreCase)
               || string.Equals(eligibility, "청구 대상", StringComparison.OrdinalIgnoreCase);
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
               RequiresExactOfficeCode(profile.OfficeCode, canonicalScope.OwnerOfficeCode) ||
               RequiresExactOfficeCode(profile.ManagementCompanyCode, canonicalScope.OwnerOfficeCode) ||
               RequiresExactOfficeCode(profile.ResponsibleOfficeCode, canonicalScope.ResponsibleOfficeCode);
    }

    private static bool IsAssetScopeInconsistent(LocalRentalAsset asset)
    {
        var canonicalScope = ResolveAssetScope(asset);
        return RequiresExactTenantCode(asset.TenantCode, canonicalScope.TenantCode) ||
               RequiresExactOfficeCode(asset.OfficeCode, canonicalScope.OwnerOfficeCode) ||
               RequiresExactOfficeCode(asset.ManagementCompanyCode, canonicalScope.OwnerOfficeCode) ||
               RequiresExactOfficeCode(asset.ResponsibleOfficeCode, canonicalScope.ResponsibleOfficeCode);
    }

    private static string BuildProfileScopeDisplay(LocalRentalBillingProfile profile)
    {
        var scope = ResolveProfileScope(profile);
        return $"{scope.TenantCode} / {scope.OwnerOfficeCode} / {scope.ResponsibleOfficeCode} / 프로필 {profile.Id:D}";
    }

    private static string BuildStoredProfileScopeDisplay(LocalRentalBillingProfile profile)
        => $"{NormalizeTenantForDisplay(profile.TenantCode, profile.OfficeCode, profile.ResponsibleOfficeCode)} / {NormalizeOfficeForDisplay(profile.OfficeCode, profile.ManagementCompanyCode)} / {NormalizeOfficeForDisplay(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode)} / 프로필 {profile.Id:D}";

    private static string BuildAssetScopeDisplay(LocalRentalAsset asset)
    {
        var scope = ResolveAssetScope(asset);
        var billingProfileText = asset.BillingProfileId.HasValue ? asset.BillingProfileId.Value.ToString("D") : "미연결";
        return $"{scope.TenantCode} / {scope.OwnerOfficeCode} / {scope.ResponsibleOfficeCode} / 프로필 {billingProfileText}";
    }

    private static string BuildStoredAssetScopeDisplay(LocalRentalAsset asset)
    {
        var billingProfileText = asset.BillingProfileId.HasValue ? asset.BillingProfileId.Value.ToString("D") : "미연결";
        return $"{NormalizeTenantForDisplay(asset.TenantCode, asset.OfficeCode, asset.ResponsibleOfficeCode)} / {NormalizeOfficeForDisplay(asset.OfficeCode, asset.ManagementCompanyCode)} / {NormalizeOfficeForDisplay(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode)} / 프로필 {billingProfileText}";
    }

    private static string NormalizeOfficeForDisplay(string? officeCode, string? fallbackOfficeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, fallbackOfficeCode);

    private static string NormalizeTenantForDisplay(string? tenantCode, string? officeCode, string? responsibleOfficeCode)
        => TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode, tenantCode, responsibleOfficeCode);

    private static bool RequiresExactOfficeCode(string? currentOfficeCode, string expectedOfficeCode)
        => !OfficeCodeCatalog.TryNormalizeOfficeCode(currentOfficeCode, out var normalizedOfficeCode) ||
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

    private static string FormatMoney(decimal value)
        => $"{value:N0}원";

    private sealed record ParsedTemplateItems(bool Success, List<RentalBillingTemplateItemModel> Items);

    private sealed record AssetTemplateReference(Guid ProfileId, string ProfileDisplayName, string ItemName);
}
