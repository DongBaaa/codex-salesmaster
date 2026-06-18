using System.Text.Json;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize(Policy = 거래플랜.Server.Api.Security.PermissionNames.SettingsEdit)]
[Route("integrity")]
public sealed class IntegrityController : ControllerBase
{
    private static readonly JsonSerializerOptions RentalTemplateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly HashSet<string> AllowedEvidenceAttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic", ".heif"
    };
    private static readonly HashSet<string> AllowedEvidenceAttachmentContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/bmp",
        "image/gif",
        "image/webp",
        "image/tiff",
        "image/heic",
        "image/heif"
    };

    private readonly AppDbContext _dbContext;
    private readonly OfficeScopeService _officeScopeService;
    private readonly ICentralFileStorage _fileStorage;

    public IntegrityController(
        AppDbContext dbContext,
        OfficeScopeService officeScopeService,
        ICentralFileStorage fileStorage)
    {
        _dbContext = dbContext;
        _officeScopeService = officeScopeService;
        _fileStorage = fileStorage;
    }

    [HttpGet("report")]
    public async Task<ActionResult<IntegrityReportDto>> GetReport(CancellationToken cancellationToken)
    {
        var issues = new List<IntegrityIssueDto>();

        var duplicateProfileKeyCount = await _officeScopeService.ApplyRentalBillingProfileScope(
                _dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
            .Where(profile => !profile.IsDeleted && !string.IsNullOrWhiteSpace(profile.ProfileKey))
            .GroupBy(profile => profile.ProfileKey)
            .Where(group => group.Count() > 1)
            .Select(group => group.Count())
            .SumAsync(cancellationToken);
        AddIssue(issues, "duplicate_rental_profile_keys", duplicateProfileKeyCount, "Error", "중복된 렌탈 청구 프로필 키가 존재합니다.");

        var duplicateAssetKeyCount = await _officeScopeService.ApplyRentalAssetScope(
                _dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
            .Where(asset => !asset.IsDeleted && !string.IsNullOrWhiteSpace(asset.AssetKey))
            .GroupBy(asset => asset.AssetKey)
            .Where(group => group.Count() > 1)
            .Select(group => group.Count())
            .SumAsync(cancellationToken);
        AddIssue(issues, "duplicate_rental_asset_keys", duplicateAssetKeyCount, "Error", "중복된 렌탈 자산 키가 존재합니다.");

        var scopedCustomers = await _officeScopeService.ApplyCustomerScope(_dbContext.Customers.IgnoreQueryFilters().AsNoTracking())
            .Where(customer => !customer.IsDeleted && !string.IsNullOrWhiteSpace(customer.NameMatchKey))
            .ToListAsync(cancellationToken);
        var duplicateCustomerMatchKeyCount = CountDuplicateRows(scopedCustomers, BuildScopedCustomerMatchKey);
        AddIssue(issues, "duplicate_customer_match_keys", duplicateCustomerMatchKeyCount, "Warning", "중복된 거래처 매칭키가 존재합니다.");

        var scopedItems = await _officeScopeService.ApplyItemScope(_dbContext.Items.IgnoreQueryFilters().AsNoTracking())
            .Where(item => !item.IsDeleted)
            .ToListAsync(cancellationToken);

        var duplicateItemNameMatchKeyCount = CountDuplicateRows(scopedItems, BuildScopedItemNameMatchKey);
        AddIssue(
            issues,
            "duplicate_item_name_match_keys",
            duplicateItemNameMatchKeyCount,
            "Info",
            "동일 품명 매칭키를 공유하는 품목이 있습니다. 규격/분류가 다르면 정상일 수 있습니다.");

        var duplicateItemMatchKeyCount = CountDuplicateRows(
            scopedItems.Where(IsPotentiallyAmbiguousItemDuplicate),
            BuildScopedItemDescriptorConflictKey);
        AddIssue(
            issues,
            "duplicate_item_match_keys",
            duplicateItemMatchKeyCount,
            "Warning",
            "동일한 품명/규격/분류/구분/재고방식 조합이 중복됩니다.");

        var ambiguousSharedItemScopeCount = (await LoadSharedItemScopeConflictSnapshotsAsync(scopedItems, cancellationToken))
            .Count;
        AddIssue(
            issues,
            "ambiguous_shared_item_tenant_scope",
            ambiguousSharedItemScopeCount,
            "Warning",
            "공용(ALL) 품목 중 사용 이력이 서로 다른 업체로 섞여 tenant 자동 보정이 보류된 항목이 있습니다.");

        var deletedItemStockResidueCount = (await LoadDeletedItemStockResidueSnapshotsAsync(cancellationToken)).Count;
        AddIssue(
            issues,
            "deleted_item_stock_residue",
            deletedItemStockResidueCount,
            "Error",
            "삭제된 품목에 현재재고 또는 창고 재고 행이 남아 있습니다.");

        var crossTenantInventoryTransferCount = (await _officeScopeService.ApplyInventoryTransferScope(
                _dbContext.InventoryTransfers.IgnoreQueryFilters().AsNoTracking())
                .Where(transfer => !transfer.IsDeleted)
                .Select(transfer => new InventoryTransferRouteSnapshot(
                    transfer.TenantCode,
                    transfer.SourceOfficeCode,
                    transfer.TargetOfficeCode,
                    transfer.FromWarehouseCode,
                    transfer.ToWarehouseCode))
                .ToListAsync(cancellationToken))
            .Count(transfer => IsCrossTenantInventoryTransfer(transfer));
        AddIssue(
            issues,
            "cross_tenant_inventory_transfers",
            crossTenantInventoryTransferCount,
            "Error",
            "업체 간 직접 재고이동 문서가 존재합니다.");

        var inventoryTransferLineMissingTransferRowCount = _officeScopeService.HasGlobalDataScope
            ? await _dbContext.InventoryTransferLines
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(line => !_dbContext.InventoryTransfers.IgnoreQueryFilters().Any(transfer => transfer.Id == line.TransferId), cancellationToken)
            : 0;
        AddIssue(issues, "inventory_transfer_line_missing_transfer_rows", inventoryTransferLineMissingTransferRowCount, "Error", "부모 재고이동 문서가 없는 재고이동 세부내역이 존재합니다.");

        var orphanWarehouseStockCount = await _officeScopeService.ApplyWarehouseScope(
                _dbContext.ItemWarehouseStocks.IgnoreQueryFilters().AsNoTracking())
            .CountAsync(stock => !_dbContext.Items.IgnoreQueryFilters().Any(item => !item.IsDeleted && item.Id == stock.ItemId), cancellationToken);
        AddIssue(issues, "orphan_item_warehouse_stock_refs", orphanWarehouseStockCount, "Error", "품목이 없는 창고 재고 행이 존재합니다.");

        var warehouseStocks = await _officeScopeService.ApplyItemWarehouseStockScope(
                _dbContext.ItemWarehouseStocks.IgnoreQueryFilters().AsNoTracking())
            .Select(stock => new
            {
                stock.ItemId,
                stock.Quantity
            })
            .ToListAsync(cancellationToken);
        var warehouseTotalMap = warehouseStocks
            .GroupBy(row => row.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Quantity));
        var stockMismatches = scopedItems.Count(row =>
        {
            if (!ItemOperationalPolicy.SupportsInventory(row.TrackingType))
                return false;

            var warehouseSum = warehouseTotalMap.TryGetValue(row.Id, out var quantity) ? quantity : 0m;
            return row.CurrentStock != warehouseSum;
        });
        AddIssue(issues, "item_stock_snapshot_mismatch", stockMismatches, "Warning", "품목 현재재고와 창고 합계가 일치하지 않는 항목이 있습니다.");

        var orphanInvoiceCustomerCount = await _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
            .Where(invoice => !invoice.IsDeleted)
            .CountAsync(invoice => !_dbContext.Customers.IgnoreQueryFilters().Any(customer => !customer.IsDeleted && customer.Id == invoice.CustomerId), cancellationToken);
        AddIssue(issues, "orphan_invoice_customer_refs", orphanInvoiceCustomerCount, "Error", "거래처가 없는 전표 참조가 존재합니다.");

        var activeInvoiceLinesDeletedInvoiceCount = await (
                from line in _dbContext.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                join invoice in _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
                    on line.InvoiceId equals invoice.Id
                where !line.IsDeleted && invoice.IsDeleted
                select line.Id)
            .CountAsync(cancellationToken);
        AddIssue(issues, "active_invoice_lines_deleted_invoice", activeInvoiceLinesDeletedInvoiceCount, "Error", "삭제된 전표에 활성 세부내역 행이 남아 있습니다.");

        var activeInvoiceDeletedLineOnlyCount = await _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
            .Where(invoice => !invoice.IsDeleted && invoice.TotalAmount != 0m)
            .CountAsync(
                invoice => _dbContext.InvoiceLines.IgnoreQueryFilters().Any(line => line.InvoiceId == invoice.Id && line.IsDeleted) &&
                           !_dbContext.InvoiceLines.IgnoreQueryFilters().Any(line => line.InvoiceId == invoice.Id && !line.IsDeleted),
                cancellationToken);
        AddIssue(issues, "active_invoice_deleted_line_only", activeInvoiceDeletedLineOnlyCount, "Warning", "활성 전표에 활성 세부내역이 없고 삭제된 세부내역만 남아 있습니다.");

        var invoiceTotalActiveLineMismatchCount = (await LoadInvoiceTotalActiveLineMismatchRowsAsync(cancellationToken)).Count;
        AddIssue(issues, "invoice_total_active_line_mismatch", invoiceTotalActiveLineMismatchCount, "Error", "활성 전표 금액 합계와 활성 세부내역 기준 계산값이 다릅니다.");

        var invoiceLineMissingInvoiceRowCount = _officeScopeService.HasGlobalDataScope
            ? await _dbContext.InvoiceLines
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(line => !_dbContext.Invoices.IgnoreQueryFilters().Any(invoice => invoice.Id == line.InvoiceId), cancellationToken)
            : 0;
        AddIssue(issues, "invoice_line_missing_invoice_rows", invoiceLineMissingInvoiceRowCount, "Error", "부모 전표 행이 없는 전표 세부내역이 존재합니다.");

        var orphanTransactionCustomerCount = await _officeScopeService.ApplyTransactionScope(_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
            .Where(transaction => !transaction.IsDeleted)
            .CountAsync(transaction => !_dbContext.Customers.IgnoreQueryFilters().Any(customer => !customer.IsDeleted && customer.Id == transaction.CustomerId), cancellationToken);
        AddIssue(issues, "orphan_transaction_customer_refs", orphanTransactionCustomerCount, "Error", "거래처가 없는 수금/지불 참조가 존재합니다.");

        var orphanRentalProfileCustomerCount = await _officeScopeService.ApplyRentalBillingProfileScope(_dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
            .Where(profile => !profile.IsDeleted && profile.CustomerId.HasValue)
            .CountAsync(profile => !_dbContext.Customers.IgnoreQueryFilters().Any(customer => !customer.IsDeleted && customer.Id == profile.CustomerId), cancellationToken);
        AddIssue(issues, "orphan_rental_profile_customer_refs", orphanRentalProfileCustomerCount, "Error", "거래처가 없는 렌탈 청구 프로필 참조가 존재합니다.");

        var rentalProfileCustomerScopeMismatchCount = await (
                from profile in _officeScopeService.ApplyRentalBillingProfileScope(_dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
                    .Where(profile => !profile.IsDeleted && profile.CustomerId.HasValue)
                join customer in _dbContext.Customers.IgnoreQueryFilters().AsNoTracking().Where(customer => !customer.IsDeleted)
                    on profile.CustomerId!.Value equals customer.Id
                where profile.TenantCode != customer.TenantCode ||
                      profile.ResponsibleOfficeCode != customer.ResponsibleOfficeCode
                select profile.Id)
            .CountAsync(cancellationToken);
        AddIssue(issues, "rental_profile_customer_scope_mismatch", rentalProfileCustomerScopeMismatchCount, "Error", "렌탈 청구 프로필이 다른 업체/담당지점 거래처를 참조합니다.");

        var rentalProfileCustomerUnlinkedCount = await _officeScopeService.ApplyRentalBillingProfileScope(_dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
            .Where(profile => !profile.IsDeleted && !profile.CustomerId.HasValue && !string.IsNullOrWhiteSpace(profile.CustomerName))
            .CountAsync(cancellationToken);
        AddIssue(issues, "rental_profile_customer_unlinked", rentalProfileCustomerUnlinkedCount, "Warning", "거래처 ID 없이 거래처명만 저장된 렌탈 청구 프로필이 있습니다.");

        var rentalTemplateScanRows = await LoadRentalTemplateScanRowsAsync(cancellationToken);
        var rentalProfileMonthlyAmountMismatchCount = rentalTemplateScanRows.Count(row =>
            row.TemplateParseSucceeded &&
            row.TemplateItems.Count > 0 &&
            AmountDiffers(row.Profile.MonthlyAmount, row.TemplateMonthlyAmount));
        AddIssue(issues, "rental_profile_monthly_amount_mismatch", rentalProfileMonthlyAmountMismatchCount, "Warning", "렌탈 청구 프로필 월 기준금액과 청구 품목 합계가 다릅니다.");

        var rentalProfileAssetMonthlyAmountMismatchCount = rentalTemplateScanRows.Count(ShouldWarnRentalProfileAssetMonthlyMismatch);
        AddIssue(issues, "rental_profile_asset_monthly_amount_mismatch", rentalProfileAssetMonthlyAmountMismatchCount, "Warning", "렌탈 청구 프로필 월 기준금액과 연결 자산 월요금 합계가 다릅니다.");

        var rentalAssetTemplateMonthlyMismatchCount = CountRentalAssetTemplateMonthlyMismatches(rentalTemplateScanRows);
        AddIssue(issues, "rental_asset_template_monthly_mismatch", rentalAssetTemplateMonthlyMismatchCount, "Warning", "렌탈 자산 월요금 합계와 청구 품목 금액이 다릅니다.");

        var orphanRentalAssetCustomerCount = await _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
            .Where(asset => !asset.IsDeleted && asset.CustomerId.HasValue)
            .CountAsync(asset => !_dbContext.Customers.IgnoreQueryFilters().Any(customer => !customer.IsDeleted && customer.Id == asset.CustomerId), cancellationToken);
        AddIssue(issues, "orphan_rental_asset_customer_refs", orphanRentalAssetCustomerCount, "Error", "거래처가 없는 렌탈 자산 참조가 존재합니다.");

        var rentalAssetCustomerScopeMismatchCount = await (
                from asset in _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
                    .Where(asset => !asset.IsDeleted && asset.CustomerId.HasValue)
                join customer in _dbContext.Customers.IgnoreQueryFilters().AsNoTracking().Where(customer => !customer.IsDeleted)
                    on asset.CustomerId!.Value equals customer.Id
                where asset.TenantCode != customer.TenantCode ||
                      asset.ResponsibleOfficeCode != customer.ResponsibleOfficeCode
                select asset.Id)
            .CountAsync(cancellationToken);
        AddIssue(issues, "rental_asset_customer_scope_mismatch", rentalAssetCustomerScopeMismatchCount, "Error", "렌탈 자산이 다른 업체/담당지점 거래처를 참조합니다.");

        var orphanRentalAssetProfileCount = await _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
            .Where(asset => !asset.IsDeleted && asset.BillingProfileId.HasValue)
            .CountAsync(asset => !_dbContext.RentalBillingProfiles.IgnoreQueryFilters().Any(profile => !profile.IsDeleted && profile.Id == asset.BillingProfileId), cancellationToken);
        AddIssue(issues, "orphan_rental_asset_profile_refs", orphanRentalAssetProfileCount, "Error", "렌탈 청구 프로필이 없는 자산 연결이 존재합니다.");

        var rentalAssetProfileScopeMismatchCount = await (
                from asset in _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
                    .Where(asset => !asset.IsDeleted && asset.BillingProfileId.HasValue)
                join profile in _dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().Where(profile => !profile.IsDeleted)
                    on asset.BillingProfileId!.Value equals profile.Id
                where asset.TenantCode != profile.TenantCode ||
                      asset.ResponsibleOfficeCode != profile.ResponsibleOfficeCode
                select asset.Id)
            .CountAsync(cancellationToken);
        AddIssue(issues, "rental_asset_profile_scope_mismatch", rentalAssetProfileScopeMismatchCount, "Error", "렌탈 자산이 다른 업체/담당지점 청구 프로필을 참조합니다.");

        var orphanRentalAssetItemCount = await _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
            .Where(asset => !asset.IsDeleted && asset.ItemId.HasValue)
            .CountAsync(asset => !_dbContext.Items.IgnoreQueryFilters().Any(item => !item.IsDeleted && item.Id == asset.ItemId), cancellationToken);
        AddIssue(issues, "orphan_rental_asset_item_refs", orphanRentalAssetItemCount, "Error", "품목이 없는 렌탈 자산 연결이 존재합니다.");

        var scopedRentalAssignmentHistories = _officeScopeService.ApplyRentalAssignmentHistoryScope(
            _dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking());
        var rentalAssignmentMissingReferenceCount = await scopedRentalAssignmentHistories
            .Where(history => !history.IsDeleted)
            .CountAsync(history =>
                    history.AssetId == Guid.Empty ||
                    !_dbContext.RentalAssets.IgnoreQueryFilters().Any(asset => !asset.IsDeleted && asset.Id == history.AssetId) ||
                    (history.IsCurrent &&
                     ((history.CustomerId.HasValue && history.CustomerId.Value != Guid.Empty &&
                       !_dbContext.Customers.IgnoreQueryFilters().Any(customer => !customer.IsDeleted && customer.Id == history.CustomerId.Value)) ||
                      (history.BillingProfileId.HasValue && history.BillingProfileId.Value != Guid.Empty &&
                       !_dbContext.RentalBillingProfiles.IgnoreQueryFilters().Any(profile => !profile.IsDeleted && profile.Id == history.BillingProfileId.Value)))),
                cancellationToken);
        AddIssue(issues, "rental_assignment_missing_reference_rows", rentalAssignmentMissingReferenceCount, "Error", "렌탈 임대이력이 존재하지 않거나 삭제된 자산/거래처/청구 프로필을 참조합니다.");

        var rentalAssignmentScopeMismatchCount = await (
                from history in scopedRentalAssignmentHistories.Where(history => !history.IsDeleted && history.IsCurrent && history.AssetId != Guid.Empty)
                join asset in _dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking().Where(asset => !asset.IsDeleted)
                    on history.AssetId equals asset.Id
                join customer in _dbContext.Customers.IgnoreQueryFilters().AsNoTracking().Where(customer => !customer.IsDeleted)
                    on history.CustomerId equals (Guid?)customer.Id into customerGroup
                from customer in customerGroup.DefaultIfEmpty()
                join profile in _dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().Where(profile => !profile.IsDeleted)
                    on history.BillingProfileId equals (Guid?)profile.Id into profileGroup
                from profile in profileGroup.DefaultIfEmpty()
                where history.TenantCode != asset.TenantCode ||
                      history.ResponsibleOfficeCode != asset.ResponsibleOfficeCode ||
                      (customer != null &&
                       (history.TenantCode != customer.TenantCode ||
                        history.ResponsibleOfficeCode != customer.ResponsibleOfficeCode)) ||
                      (profile != null &&
                       (history.TenantCode != profile.TenantCode ||
                        history.ResponsibleOfficeCode != profile.ResponsibleOfficeCode))
                select history.Id)
            .CountAsync(cancellationToken);
        AddIssue(issues, "rental_assignment_current_scope_mismatch", rentalAssignmentScopeMismatchCount, "Error", "현재 렌탈 설치이력이 다른 업체/담당지점 자산/거래처/청구 프로필을 참조합니다.");

        var rentalAssignmentHistoricalStaleReferenceCount = await scopedRentalAssignmentHistories
            .Where(history => !history.IsDeleted && !history.IsCurrent && history.AssetId != Guid.Empty)
            .Where(history => _dbContext.RentalAssets.IgnoreQueryFilters().Any(asset => !asset.IsDeleted && asset.Id == history.AssetId))
            .CountAsync(history =>
                    (history.CustomerId.HasValue && history.CustomerId.Value != Guid.Empty &&
                     !_dbContext.Customers.IgnoreQueryFilters().Any(customer => !customer.IsDeleted && customer.Id == history.CustomerId.Value)) ||
                    (history.BillingProfileId.HasValue && history.BillingProfileId.Value != Guid.Empty &&
                     !_dbContext.RentalBillingProfiles.IgnoreQueryFilters().Any(profile => !profile.IsDeleted && profile.Id == history.BillingProfileId.Value)),
                cancellationToken);
        AddIssue(issues, "rental_assignment_historical_stale_reference_rows", rentalAssignmentHistoricalStaleReferenceCount, "Info", "과거 렌탈 임대이력의 거래처/청구 프로필 참조가 현재 마스터에서 사라졌지만 스냅샷 표시값은 남아 있습니다.");

        var rentalAssetMultipleCurrentAssignmentCount = await scopedRentalAssignmentHistories
            .Where(history => !history.IsDeleted && history.IsCurrent && history.AssetId != Guid.Empty)
            .GroupBy(history => history.AssetId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Count())
            .SumAsync(cancellationToken);
        AddIssue(issues, "rental_asset_multiple_current_assignments", rentalAssetMultipleCurrentAssignmentCount, "Error", "하나의 렌탈 자산에 현재 임대중으로 표시된 이력이 여러 개 있습니다.");

        var orphanTransactionInvoiceCount = await _officeScopeService.ApplyTransactionScope(_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
            .Where(transaction => !transaction.IsDeleted && transaction.LinkedInvoiceId.HasValue)
            .CountAsync(transaction => !_dbContext.Invoices.IgnoreQueryFilters().Any(invoice => !invoice.IsDeleted && invoice.Id == transaction.LinkedInvoiceId), cancellationToken);
        AddIssue(issues, "orphan_transaction_invoice_refs", orphanTransactionInvoiceCount, "Error", "전표가 없는 거래/수금 참조가 존재합니다.");

        var orphanPaymentInvoiceCount = _officeScopeService.HasGlobalDataScope
            ? await _dbContext.Payments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(payment => !payment.IsDeleted)
                .CountAsync(payment => !_dbContext.Invoices.IgnoreQueryFilters().Any(invoice => !invoice.IsDeleted && invoice.Id == payment.InvoiceId), cancellationToken)
            : await (
                    from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking().Where(payment => !payment.IsDeleted)
                    join invoice in _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
                            .Where(invoice => invoice.IsDeleted)
                        on payment.InvoiceId equals invoice.Id
                    select payment.Id)
                .CountAsync(cancellationToken);
        AddIssue(issues, "orphan_payment_invoice_refs", orphanPaymentInvoiceCount, "Error", "전표가 없는 수금/지급 참조가 존재합니다.");

        var deletedPaymentMissingInvoiceRowCount = _officeScopeService.HasGlobalDataScope
            ? await _dbContext.Payments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(payment => payment.IsDeleted)
                .CountAsync(payment => !_dbContext.Invoices.IgnoreQueryFilters().Any(invoice => invoice.Id == payment.InvoiceId), cancellationToken)
            : 0;
        AddIssue(issues, "deleted_payment_missing_invoice_rows", deletedPaymentMissingInvoiceRowCount, "Error", "영구 삭제된 전표의 삭제 결제 잔여 행이 존재합니다.");

        var invoiceLinkedTransactionPaymentMismatchCount = (await LoadInvoiceLinkedTransactionPaymentMismatchRowsAsync(cancellationToken)).Count;
        AddIssue(issues, "invoice_linked_transaction_payment_mismatch", invoiceLinkedTransactionPaymentMismatchCount, "Error", "전표 연결 거래내역과 파생 수금/지급 행의 전표·금액 상태가 다릅니다.");

        var rentalInvoiceDeletedPaymentDetachedTransactionCount = await (
                from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking().Where(payment => payment.IsDeleted)
                join invoice in _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
                        .Where(invoice =>
                            !invoice.IsDeleted &&
                            invoice.LinkedRentalBillingProfileId.HasValue &&
                            invoice.LinkedRentalBillingProfileId.Value != Guid.Empty)
                    on payment.InvoiceId equals invoice.Id
                join transaction in _officeScopeService.ApplyTransactionScope(_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
                        .Where(transaction => !transaction.IsDeleted)
                    on payment.Id equals transaction.Id
                where !transaction.LinkedInvoiceId.HasValue ||
                      transaction.LinkedInvoiceId.Value == Guid.Empty ||
                      transaction.LinkedInvoiceId.Value != invoice.Id ||
                      transaction.SettlementAmount != payment.Amount ||
                      transaction.LinkedRentalBillingProfileId != invoice.LinkedRentalBillingProfileId ||
                      transaction.LinkedRentalBillingRunId != invoice.LinkedRentalBillingRunId
                select payment.Id)
            .CountAsync(cancellationToken);
        AddIssue(issues, "rental_invoice_deleted_payment_detached_transaction", rentalInvoiceDeletedPaymentDetachedTransactionCount, "Error", "활성 렌탈 전표에 삭제 상태 수금/지급과 전표 링크가 끊긴 활성 거래내역이 함께 남아 있습니다.");

        var rentalBillingRunSettlementMismatchCount = (await LoadRentalBillingRunSettlementMismatchRowsAsync(cancellationToken)).Count;
        AddIssue(issues, "rental_billing_run_settlement_mismatch", rentalBillingRunSettlementMismatchCount, "Error", "렌탈 청구 run의 저장 정산금액과 실제 활성 수금/거래내역 합계가 다릅니다.");

        var rentalBillingRunMissingRunIdCount = (await LoadRentalBillingRunMissingRunIdRowsAsync(cancellationToken)).Count;
        AddIssue(issues, "rental_billing_run_missing_run_id", rentalBillingRunMissingRunIdCount, "Info", "렌탈 청구 프로필에 run ID가 비어 있는 과거 청구 JSON이 있습니다.");

        var rentalBillingProfileSummaryMismatchCount = (await LoadRentalBillingProfileSummaryMismatchRowsAsync(cancellationToken)).Count;
        AddIssue(issues, "rental_billing_profile_summary_mismatch", rentalBillingProfileSummaryMismatchCount, "Error", "렌탈 청구 프로필 요약 정산/미수금액이 대표 청구 run의 실제 입금 근거와 다릅니다.");

        var orphanTransactionAttachmentCount = _officeScopeService.HasGlobalDataScope
            ? await _dbContext.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(attachment => !attachment.IsDeleted)
                .CountAsync(attachment => !_dbContext.Transactions.IgnoreQueryFilters().Any(transaction => !transaction.IsDeleted && transaction.Id == attachment.TransactionId), cancellationToken)
            : await (
                    from attachment in _dbContext.TransactionAttachments.IgnoreQueryFilters().AsNoTracking().Where(attachment => !attachment.IsDeleted)
                    join transaction in _officeScopeService.ApplyTransactionScope(_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
                            .Where(transaction => transaction.IsDeleted)
                        on attachment.TransactionId equals transaction.Id
                    select attachment.Id)
                .CountAsync(cancellationToken);
        AddIssue(issues, "orphan_attachment_transaction_refs", orphanTransactionAttachmentCount, "Error", "거래내역이 없는 증빙 첨부가 존재합니다.");

        var deletedTransactionAttachmentMissingTransactionRowCount = _officeScopeService.HasGlobalDataScope
            ? await _dbContext.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(attachment => attachment.IsDeleted)
                .CountAsync(attachment => !_dbContext.Transactions.IgnoreQueryFilters().Any(transaction => transaction.Id == attachment.TransactionId), cancellationToken)
            : 0;
        AddIssue(issues, "deleted_transaction_attachment_missing_transaction_rows", deletedTransactionAttachmentMissingTransactionRowCount, "Error", "영구 삭제된 거래내역의 삭제 첨부 잔여 행이 존재합니다.");

        var orphanPaymentAttachmentCount = _officeScopeService.HasGlobalDataScope
            ? await _dbContext.PaymentAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(attachment => !attachment.IsDeleted)
                .CountAsync(attachment => !_dbContext.Payments.IgnoreQueryFilters().Any(payment => !payment.IsDeleted && payment.Id == attachment.PaymentId), cancellationToken)
            : await (
                    from attachment in _dbContext.PaymentAttachments.IgnoreQueryFilters().AsNoTracking().Where(attachment => !attachment.IsDeleted)
                    join payment in _officeScopeService.ApplyPaymentScope(_dbContext.Payments.IgnoreQueryFilters().AsNoTracking())
                            .Where(payment => payment.IsDeleted)
                        on attachment.PaymentId equals payment.Id
                    select attachment.Id)
                .CountAsync(cancellationToken);
        AddIssue(issues, "orphan_payment_attachment_refs", orphanPaymentAttachmentCount, "Error", "결제내역이 없는 결제 첨부가 존재합니다.");

        var deletedPaymentAttachmentMissingPaymentRowCount = _officeScopeService.HasGlobalDataScope
            ? await _dbContext.PaymentAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(attachment => attachment.IsDeleted)
                .CountAsync(attachment => !_dbContext.Payments.IgnoreQueryFilters().Any(payment => payment.Id == attachment.PaymentId), cancellationToken)
            : 0;
        AddIssue(issues, "deleted_payment_attachment_missing_payment_rows", deletedPaymentAttachmentMissingPaymentRowCount, "Error", "영구 삭제된 결제의 삭제 첨부 잔여 행이 존재합니다.");

        var unsupportedAttachmentFileTypeCount = (await LoadUnsupportedAttachmentFileTypeDetailsAsync(cancellationToken)).Count;
        AddIssue(issues, "unsupported_attachment_file_type", unsupportedAttachmentFileTypeCount, "Warning", "PDF/이미지 정책과 맞지 않는 거래/결제 증빙 첨부가 있습니다.");

        var attachmentContentSignatureMismatchCount = (await LoadAttachmentContentSignatureMismatchDetailsAsync(cancellationToken)).Count;
        AddIssue(issues, "attachment_content_signature_mismatch", attachmentContentSignatureMismatchCount, "Warning", "파일명/MIME과 실제 저장 파일 내용이 일치하지 않는 거래/결제 증빙 첨부가 있습니다.");

        var customerContractMissingCustomerRowCount = _officeScopeService.HasGlobalDataScope
            ? await _dbContext.CustomerContracts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(contract => !_dbContext.Customers.IgnoreQueryFilters().Any(customer => customer.Id == contract.CustomerId), cancellationToken)
            : 0;
        AddIssue(issues, "customer_contract_missing_customer_rows", customerContractMissingCustomerRowCount, "Error", "부모 거래처 행이 없는 계약/첨부가 존재합니다.");

        var rentalBillingLogMissingProfileRowCount = await _officeScopeService.ApplyRentalBillingLogScope(
                _dbContext.RentalBillingLogs.IgnoreQueryFilters().AsNoTracking())
            .CountAsync(log => !_dbContext.RentalBillingProfiles.IgnoreQueryFilters().Any(profile => profile.Id == log.BillingProfileId), cancellationToken);
        AddIssue(issues, "rental_billing_log_missing_profile_rows", rentalBillingLogMissingProfileRowCount, "Error", "부모 청구 프로필 행이 없는 렌탈 청구 로그가 존재합니다.");

        var fileStorageIssueCandidates = await LoadFileStorageIssueCandidatesAsync(cancellationToken);
        AddIssue(
            issues,
            "file_content_unavailable",
            fileStorageIssueCandidates.Count(IsFileContentUnavailable),
            "Error",
            "파일 크기는 있으나 저장소 경로와 DB 파일 본문이 모두 비어 있는 첨부/계약서가 있습니다.");
        AddIssue(
            issues,
            "file_content_db_residue",
            fileStorageIssueCandidates.Count(HasDbFileContentResidue),
            "Warning",
            "파일 본문이 DB에 남아 저장소 이동이 완료되지 않은 첨부/계약서가 있습니다.");
        AddIssue(
            issues,
            "file_storage_missing",
            fileStorageIssueCandidates.Count(IsStoredFileMissing),
            "Error",
            "저장소 경로가 있으나 실제 저장 파일을 읽을 수 없는 첨부/계약서가 있습니다.");
        AddIssue(
            issues,
            "file_storage_size_mismatch",
            fileStorageIssueCandidates.Count(IsStoredFileSizeMismatch),
            "Error",
            "저장소 실제 파일 크기와 DB 파일 크기가 다른 첨부/계약서가 있습니다.");
        AddIssue(
            issues,
            "file_storage_hash_mismatch",
            fileStorageIssueCandidates.Count(IsStoredFileHashMismatch),
            "Error",
            "저장소 실제 파일 SHA256과 DB 파일 해시가 다른 첨부/계약서가 있습니다.");

        var report = new IntegrityReportDto
        {
            GeneratedAtUtc = DateTime.UtcNow,
            TenantCode = _officeScopeService.CurrentTenantCode,
            OfficeCode = _officeScopeService.CurrentOfficeCode,
            IssueCount = issues.Count,
            Issues = issues
        };

        return Ok(report);
    }

    [HttpGet("report/details")]
    public async Task<ActionResult<IntegrityIssueDetailResultDto>> GetReportDetails([FromQuery] string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
            return BadRequest("무결성 코드가 필요합니다.");

        if (!TryResolveIssueDefinition(code, out var definition))
            return BadRequest($"지원하지 않는 무결성 코드입니다: {code}");

        var rows = await LoadIssueDetailRowsAsync(definition.Code, cancellationToken);
        var result = new IntegrityIssueDetailResultDto
        {
            GeneratedAtUtc = DateTime.UtcNow,
            TenantCode = _officeScopeService.CurrentTenantCode,
            OfficeCode = _officeScopeService.CurrentOfficeCode,
            Code = definition.Code,
            Severity = definition.Severity,
            Message = definition.Message,
            DetailCount = rows.Count,
            Rows = rows
        };

        return Ok(result);
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadIssueDetailRowsAsync(string code, CancellationToken cancellationToken)
    {
        return code switch
        {
            "duplicate_rental_profile_keys" => await LoadDuplicateRentalProfileKeyDetailsAsync(cancellationToken),
            "duplicate_rental_asset_keys" => await LoadDuplicateRentalAssetKeyDetailsAsync(cancellationToken),
            "duplicate_customer_match_keys" => await LoadDuplicateCustomerMatchKeyDetailsAsync(cancellationToken),
            "duplicate_item_name_match_keys" => await LoadDuplicateItemNameMatchKeyDetailsAsync(cancellationToken),
            "duplicate_item_match_keys" => await LoadDuplicateItemMatchKeyDetailsAsync(cancellationToken),
            "ambiguous_shared_item_tenant_scope" => await LoadSharedItemScopeConflictDetailsAsync(cancellationToken),
            "deleted_item_stock_residue" => await LoadDeletedItemStockResidueDetailsAsync(cancellationToken),
            "cross_tenant_inventory_transfers" => await LoadCrossTenantInventoryTransferDetailsAsync(cancellationToken),
            "inventory_transfer_line_missing_transfer_rows" => await LoadInventoryTransferLineMissingTransferRowDetailsAsync(cancellationToken),
            "orphan_item_warehouse_stock_refs" => await LoadOrphanItemWarehouseStockDetailsAsync(cancellationToken),
            "item_stock_snapshot_mismatch" => await LoadItemStockSnapshotMismatchDetailsAsync(cancellationToken),
            "orphan_invoice_customer_refs" => await LoadOrphanInvoiceCustomerDetailsAsync(cancellationToken),
            "active_invoice_lines_deleted_invoice" => await LoadActiveInvoiceLinesDeletedInvoiceDetailsAsync(cancellationToken),
            "active_invoice_deleted_line_only" => await LoadActiveInvoiceDeletedLineOnlyDetailsAsync(cancellationToken),
            "invoice_total_active_line_mismatch" => await LoadInvoiceTotalActiveLineMismatchDetailsAsync(cancellationToken),
            "invoice_line_missing_invoice_rows" => await LoadInvoiceLineMissingInvoiceRowDetailsAsync(cancellationToken),
            "orphan_transaction_customer_refs" => await LoadOrphanTransactionCustomerDetailsAsync(cancellationToken),
            "orphan_rental_profile_customer_refs" => await LoadOrphanRentalProfileCustomerDetailsAsync(cancellationToken),
            "rental_profile_customer_scope_mismatch" => await LoadRentalProfileCustomerScopeMismatchDetailsAsync(cancellationToken),
            "rental_profile_customer_unlinked" => await LoadRentalProfileCustomerUnlinkedDetailsAsync(cancellationToken),
            "rental_profile_monthly_amount_mismatch" => await LoadRentalProfileMonthlyAmountMismatchDetailsAsync(cancellationToken),
            "rental_profile_asset_monthly_amount_mismatch" => await LoadRentalProfileAssetMonthlyAmountMismatchDetailsAsync(cancellationToken),
            "rental_asset_template_monthly_mismatch" => await LoadRentalAssetTemplateMonthlyMismatchDetailsAsync(cancellationToken),
            "orphan_rental_asset_customer_refs" => await LoadOrphanRentalAssetCustomerDetailsAsync(cancellationToken),
            "rental_asset_customer_scope_mismatch" => await LoadRentalAssetCustomerScopeMismatchDetailsAsync(cancellationToken),
            "orphan_rental_asset_profile_refs" => await LoadOrphanRentalAssetProfileDetailsAsync(cancellationToken),
            "rental_asset_profile_scope_mismatch" => await LoadRentalAssetProfileScopeMismatchDetailsAsync(cancellationToken),
            "orphan_rental_asset_item_refs" => await LoadOrphanRentalAssetItemDetailsAsync(cancellationToken),
            "rental_assignment_missing_reference_rows" => await LoadRentalAssignmentMissingReferenceDetailsAsync(cancellationToken),
            "rental_assignment_current_scope_mismatch" => await LoadRentalAssignmentCurrentScopeMismatchDetailsAsync(cancellationToken),
            "rental_assignment_historical_stale_reference_rows" => await LoadRentalAssignmentHistoricalStaleReferenceDetailsAsync(cancellationToken),
            "rental_asset_multiple_current_assignments" => await LoadRentalAssetMultipleCurrentAssignmentDetailsAsync(cancellationToken),
            "orphan_transaction_invoice_refs" => await LoadOrphanTransactionInvoiceDetailsAsync(cancellationToken),
            "orphan_payment_invoice_refs" => await LoadOrphanPaymentInvoiceDetailsAsync(cancellationToken),
            "deleted_payment_missing_invoice_rows" => await LoadDeletedPaymentMissingInvoiceRowDetailsAsync(cancellationToken),
            "invoice_linked_transaction_payment_mismatch" => await LoadInvoiceLinkedTransactionPaymentMismatchDetailsAsync(cancellationToken),
            "rental_invoice_deleted_payment_detached_transaction" => await LoadRentalInvoiceDeletedPaymentDetachedTransactionDetailsAsync(cancellationToken),
            "rental_billing_run_settlement_mismatch" => await LoadRentalBillingRunSettlementMismatchDetailsAsync(cancellationToken),
            "rental_billing_run_missing_run_id" => await LoadRentalBillingRunMissingRunIdDetailsAsync(cancellationToken),
            "rental_billing_profile_summary_mismatch" => await LoadRentalBillingProfileSummaryMismatchDetailsAsync(cancellationToken),
            "orphan_attachment_transaction_refs" => await LoadOrphanTransactionAttachmentDetailsAsync(cancellationToken),
            "deleted_transaction_attachment_missing_transaction_rows" => await LoadDeletedTransactionAttachmentMissingTransactionRowDetailsAsync(cancellationToken),
            "orphan_payment_attachment_refs" => await LoadOrphanPaymentAttachmentDetailsAsync(cancellationToken),
            "deleted_payment_attachment_missing_payment_rows" => await LoadDeletedPaymentAttachmentMissingPaymentRowDetailsAsync(cancellationToken),
            "unsupported_attachment_file_type" => await LoadUnsupportedAttachmentFileTypeDetailsAsync(cancellationToken),
            "attachment_content_signature_mismatch" => await LoadAttachmentContentSignatureMismatchDetailsAsync(cancellationToken),
            "customer_contract_missing_customer_rows" => await LoadCustomerContractMissingCustomerRowDetailsAsync(cancellationToken),
            "rental_billing_log_missing_profile_rows" => await LoadRentalBillingLogMissingProfileRowDetailsAsync(cancellationToken),
            "file_content_unavailable" => await LoadFileContentUnavailableDetailsAsync(cancellationToken),
            "file_content_db_residue" => await LoadFileContentDbResidueDetailsAsync(cancellationToken),
            "file_storage_missing" => await LoadStoredFileMissingDetailsAsync(cancellationToken),
            "file_storage_size_mismatch" => await LoadStoredFileSizeMismatchDetailsAsync(cancellationToken),
            "file_storage_hash_mismatch" => await LoadStoredFileHashMismatchDetailsAsync(cancellationToken),
            _ => []
        };
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadDuplicateRentalProfileKeyDetailsAsync(CancellationToken cancellationToken)
    {
        var scopedProfiles = _officeScopeService.ApplyRentalBillingProfileScope(
            _dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking());

        var duplicateKeys = await scopedProfiles
            .Where(profile => !profile.IsDeleted && !string.IsNullOrWhiteSpace(profile.ProfileKey))
            .GroupBy(profile => profile.ProfileKey)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToListAsync(cancellationToken);

        if (duplicateKeys.Count == 0)
            return [];

        var profiles = await scopedProfiles
            .Where(profile => !profile.IsDeleted && duplicateKeys.Contains(profile.ProfileKey))
            .OrderBy(profile => profile.ProfileKey)
            .ThenBy(profile => profile.CustomerName)
            .ThenBy(profile => profile.Id)
            .ToListAsync(cancellationToken);

        return profiles
            .Select(profile => CreateDetailRow(
                entityType: "렌탈청구프로필",
                entityIdText: FormatGuid(profile.Id),
                primaryText: profile.ProfileKey,
                secondaryText: CombineParts(profile.CustomerName, profile.ItemName),
                referenceText: profile.CustomerId.HasValue ? $"거래처 {FormatGuid(profile.CustomerId.Value)}" : "거래처 미연결",
                scopeText: FormatScope(profile.TenantCode, profile.OfficeCode, profile.ResponsibleOfficeCode),
                detailText: CombineParts(
                    string.IsNullOrWhiteSpace(profile.ManagementCompanyCode) ? null : $"관리업체 {profile.ManagementCompanyCode}",
                    string.IsNullOrWhiteSpace(profile.InstallSiteName) ? null : $"설치처 {profile.InstallSiteName}",
                    string.IsNullOrWhiteSpace(profile.BillingStatus) ? null : $"청구상태 {profile.BillingStatus}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadDuplicateRentalAssetKeyDetailsAsync(CancellationToken cancellationToken)
    {
        var scopedAssets = _officeScopeService.ApplyRentalAssetScope(
            _dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking());

        var duplicateKeys = await scopedAssets
            .Where(asset => !asset.IsDeleted && !string.IsNullOrWhiteSpace(asset.AssetKey))
            .GroupBy(asset => asset.AssetKey)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToListAsync(cancellationToken);

        if (duplicateKeys.Count == 0)
            return [];

        var assets = await scopedAssets
            .Where(asset => !asset.IsDeleted && duplicateKeys.Contains(asset.AssetKey))
            .OrderBy(asset => asset.AssetKey)
            .ThenBy(asset => asset.ManagementNumber)
            .ThenBy(asset => asset.Id)
            .ToListAsync(cancellationToken);

        return assets
            .Select(asset => CreateDetailRow(
                entityType: "렌탈자산",
                entityIdText: FormatGuid(asset.Id),
                primaryText: FirstNonEmpty(asset.ManagementNumber, asset.AssetKey, asset.ManagementId),
                secondaryText: CombineParts(asset.CustomerName, asset.ItemName),
                referenceText: asset.BillingProfileId.HasValue ? $"청구프로필 {FormatGuid(asset.BillingProfileId.Value)}" : "청구프로필 미연결",
                scopeText: FormatScope(asset.TenantCode, asset.OfficeCode, asset.ResponsibleOfficeCode),
                detailText: CombineParts(
                    string.IsNullOrWhiteSpace(asset.AssetKey) ? null : $"자산키 {asset.AssetKey}",
                    string.IsNullOrWhiteSpace(asset.ManagementId) ? null : $"관리ID {asset.ManagementId}",
                    string.IsNullOrWhiteSpace(asset.ManagementCompanyCode) ? null : $"관리업체 {asset.ManagementCompanyCode}",
                    string.IsNullOrWhiteSpace(asset.InstallLocation) ? null : $"설치위치 {asset.InstallLocation}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadDuplicateCustomerMatchKeyDetailsAsync(CancellationToken cancellationToken)
    {
        var scopedCustomers = await _officeScopeService.ApplyCustomerScope(_dbContext.Customers.IgnoreQueryFilters().AsNoTracking())
            .Where(customer => !customer.IsDeleted && !string.IsNullOrWhiteSpace(customer.NameMatchKey))
            .ToListAsync(cancellationToken);

        var duplicateKeys = scopedCustomers
            .GroupBy(BuildScopedCustomerMatchKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (duplicateKeys.Count == 0)
            return [];

        var customers = scopedCustomers
            .Where(customer => duplicateKeys.Contains(BuildScopedCustomerMatchKey(customer)))
            .OrderBy(customer => customer.NameMatchKey)
            .ThenBy(customer => customer.NameOriginal)
            .ThenBy(customer => customer.Id)
            .ToList();

        return customers
            .Select(customer => CreateDetailRow(
                entityType: "거래처",
                entityIdText: FormatGuid(customer.Id),
                primaryText: customer.NameMatchKey,
                secondaryText: CombineParts(customer.NameOriginal, customer.BusinessNumber),
                referenceText: customer.CustomerMasterId.HasValue ? $"거래처기준 {FormatGuid(customer.CustomerMasterId.Value)}" : "거래처기준 미연결",
                scopeText: FormatScope(customer.TenantCode, customer.OfficeCode, customer.ResponsibleOfficeCode),
                detailText: CombineParts(customer.TradeType, customer.Representative, customer.ContactPerson)))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadDuplicateItemNameMatchKeyDetailsAsync(CancellationToken cancellationToken)
    {
        var scopedItems = await _officeScopeService.ApplyItemScope(_dbContext.Items.IgnoreQueryFilters().AsNoTracking())
            .Where(item => !item.IsDeleted)
            .ToListAsync(cancellationToken);

        var keyedItems = scopedItems
            .Select(item => new
            {
                Item = item,
                DuplicateKey = BuildScopedItemNameMatchKey(item)
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.DuplicateKey))
            .ToList();

        var duplicateKeys = keyedItems
            .GroupBy(row => row.DuplicateKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return keyedItems
            .Where(row => duplicateKeys.Contains(row.DuplicateKey))
            .OrderBy(row => row.DuplicateKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Item.NameOriginal, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Item.SpecificationOriginal, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Item.Id)
            .Select(row => CreateDetailRow(
                entityType: "품목",
                entityIdText: FormatGuid(row.Item.Id),
                primaryText: row.Item.NameOriginal,
                secondaryText: CombineParts(row.Item.SpecificationOriginal, row.Item.CategoryName),
                referenceText: string.IsNullOrWhiteSpace(row.Item.NameMatchKey) ? "품명키 없음" : $"품명키 {row.Item.NameMatchKey}",
                scopeText: FormatScope(row.Item.TenantCode, row.Item.OfficeCode),
                detailText: CombineParts(
                    $"스코프키 {row.DuplicateKey}",
                    string.IsNullOrWhiteSpace(row.Item.ItemKind) ? null : $"구분 {row.Item.ItemKind}",
                    string.IsNullOrWhiteSpace(row.Item.TrackingType) ? null : $"재고방식 {row.Item.TrackingType}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadDuplicateItemMatchKeyDetailsAsync(CancellationToken cancellationToken)
    {
        var scopedItems = await _officeScopeService.ApplyItemScope(_dbContext.Items.IgnoreQueryFilters().AsNoTracking())
            .Where(item => !item.IsDeleted)
            .ToListAsync(cancellationToken);

        var keyedItems = scopedItems
            .Where(IsPotentiallyAmbiguousItemDuplicate)
            .Select(item => new
            {
                Item = item,
                DuplicateKey = BuildScopedItemDescriptorConflictKey(item)
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.DuplicateKey))
            .ToList();

        var duplicateKeys = keyedItems
            .GroupBy(row => row.DuplicateKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return keyedItems
            .Where(row => duplicateKeys.Contains(row.DuplicateKey))
            .OrderBy(row => row.DuplicateKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Item.NameOriginal, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Item.SpecificationOriginal, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Item.Id)
            .Select(row => CreateDetailRow(
                entityType: "품목",
                entityIdText: FormatGuid(row.Item.Id),
                primaryText: row.Item.NameOriginal,
                secondaryText: CombineParts(row.Item.SpecificationOriginal, row.Item.CategoryName),
                referenceText: CombineParts(
                    string.IsNullOrWhiteSpace(row.Item.NameMatchKey) ? null : $"품명키 {row.Item.NameMatchKey}",
                    string.IsNullOrWhiteSpace(row.Item.SpecificationMatchKey) ? null : $"규격키 {row.Item.SpecificationMatchKey}"),
                scopeText: FormatScope(row.Item.TenantCode, row.Item.OfficeCode),
                detailText: CombineParts(
                    $"중복조합 {row.DuplicateKey}",
                    string.IsNullOrWhiteSpace(row.Item.ItemKind) ? null : $"구분 {row.Item.ItemKind}",
                    string.IsNullOrWhiteSpace(row.Item.TrackingType) ? null : $"재고방식 {row.Item.TrackingType}",
                    row.Item.IsRental ? "렌탈 품목" : null)))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadCrossTenantInventoryTransferDetailsAsync(CancellationToken cancellationToken)
    {
        var transfers = await _officeScopeService.ApplyInventoryTransferScope(_dbContext.InventoryTransfers.IgnoreQueryFilters().AsNoTracking())
            .Where(transfer => !transfer.IsDeleted)
            .Select(transfer => new InventoryTransferDetailSnapshot(
                transfer.Id,
                transfer.TenantCode,
                transfer.SourceOfficeCode,
                transfer.TargetOfficeCode,
                transfer.FromWarehouseCode,
                transfer.ToWarehouseCode,
                transfer.TransferNumber,
                transfer.TransferDate,
                transfer.TransferStatus,
                transfer.Memo))
            .ToListAsync(cancellationToken);

        return transfers
            .Select(transfer => new
            {
                Transfer = transfer,
                Route = DescribeInventoryTransfer(new InventoryTransferRouteSnapshot(
                    transfer.TenantCode,
                    transfer.SourceOfficeCode,
                    transfer.TargetOfficeCode,
                    transfer.FromWarehouseCode,
                    transfer.ToWarehouseCode))
            })
            .Where(row => !string.Equals(row.Route.SourceTenantCode, row.Route.TargetTenantCode, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => row.Transfer.TransferDate)
            .ThenBy(row => row.Transfer.TransferNumber, StringComparer.OrdinalIgnoreCase)
            .Select(row => CreateDetailRow(
                entityType: "재고이동",
                entityIdText: FormatGuid(row.Transfer.Id),
                primaryText: FirstNonEmpty(row.Transfer.TransferNumber, FormatGuid(row.Transfer.Id)),
                secondaryText: $"{NormalizeCellText(row.Route.SourceOfficeCode)} → {NormalizeCellText(row.Route.TargetOfficeCode)}",
                referenceText: $"{NormalizeCellText(row.Route.SourceTenantCode)} → {NormalizeCellText(row.Route.TargetTenantCode)}",
                scopeText: FormatScope(row.Transfer.TenantCode, row.Transfer.SourceOfficeCode, row.Transfer.TargetOfficeCode),
                detailText: CombineParts(
                    $"이동일 {FormatDate(row.Transfer.TransferDate)}",
                    string.IsNullOrWhiteSpace(row.Transfer.TransferStatus) ? null : $"상태 {row.Transfer.TransferStatus}",
                    $"창고 {NormalizeCellText(row.Transfer.FromWarehouseCode)} → {NormalizeCellText(row.Transfer.ToWarehouseCode)}",
                    string.IsNullOrWhiteSpace(row.Transfer.Memo) ? null : $"메모 {row.Transfer.Memo}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadInventoryTransferLineMissingTransferRowDetailsAsync(CancellationToken cancellationToken)
    {
        if (!_officeScopeService.HasGlobalDataScope)
            return [];

        var lines = await (
                from line in _dbContext.InventoryTransferLines.IgnoreQueryFilters().AsNoTracking()
                join transfer in _dbContext.InventoryTransfers.IgnoreQueryFilters().AsNoTracking()
                    on line.TransferId equals transfer.Id into transferGroup
                from transfer in transferGroup.DefaultIfEmpty()
                where transfer == null
                orderby line.TransferId, line.ItemNameOriginal, line.Id
                select line)
            .ToListAsync(cancellationToken);

        return lines
            .Select(line => CreateDetailRow(
                entityType: "재고이동 세부내역",
                entityIdText: FormatGuid(line.Id),
                primaryText: FirstNonEmpty(line.ItemNameOriginal, FormatGuid(line.Id)),
                secondaryText: CombineParts(line.SpecificationOriginal, line.Unit),
                referenceText: $"누락 재고이동 {FormatGuid(line.TransferId)}",
                scopeText: "공통",
                detailText: CombineParts(
                    line.IsDeleted ? "삭제상태 삭제" : "삭제상태 활성",
                    $"요청수량 {FormatNumber(line.Quantity)}",
                    line.ReceivedQuantity.HasValue ? $"수령수량 {FormatNumber(line.ReceivedQuantity.Value)}" : null,
                    line.QuantityDifference.HasValue ? $"차이 {FormatNumber(line.QuantityDifference.Value)}" : null,
                    string.IsNullOrWhiteSpace(line.Remark) ? null : $"비고 {line.Remark}",
                    string.IsNullOrWhiteSpace(line.ReceiptRemark) ? null : $"수령비고 {line.ReceiptRemark}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadOrphanItemWarehouseStockDetailsAsync(CancellationToken cancellationToken)
    {
        var orphanStocks = await (
                from stock in _officeScopeService.ApplyWarehouseScope(
                    _dbContext.ItemWarehouseStocks.IgnoreQueryFilters().AsNoTracking())
                join item in _dbContext.Items.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    on stock.ItemId equals item.Id into itemGroup
                from item in itemGroup.DefaultIfEmpty()
                where item == null
                orderby stock.WarehouseCode, stock.ItemId
                select stock)
            .ToListAsync(cancellationToken);

        return orphanStocks
            .Select(stock => CreateDetailRow(
                entityType: "창고재고",
                entityIdText: $"{FormatGuid(stock.ItemId)}@{NormalizeCellText(stock.WarehouseCode)}",
                primaryText: stock.WarehouseCode,
                secondaryText: $"수량 {FormatNumber(stock.Quantity)}",
                referenceText: $"누락 품목 {FormatGuid(stock.ItemId)}",
                scopeText: $"창고 {NormalizeCellText(stock.WarehouseCode)}",
                detailText: $"최종갱신 {FormatUtcDateTime(stock.UpdatedAtUtc)}"))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadDeletedItemStockResidueDetailsAsync(CancellationToken cancellationToken)
    {
        var residues = await LoadDeletedItemStockResidueSnapshotsAsync(cancellationToken);
        return residues
            .Select(row => CreateDetailRow(
                entityType: "삭제 품목",
                entityIdText: FormatGuid(row.Item.Id),
                primaryText: row.Item.NameOriginal,
                secondaryText: CombineParts(row.Item.SpecificationOriginal, row.Item.CategoryName),
                referenceText: string.IsNullOrWhiteSpace(row.Item.NameMatchKey) ? FormatGuid(row.Item.Id) : $"품명키 {row.Item.NameMatchKey}",
                scopeText: FormatScope(row.Item.TenantCode, row.Item.OfficeCode),
                detailText: $"삭제 품목 현재재고 {FormatNumber(row.Item.CurrentStock)} / 창고행 {row.WarehouseRowCount:N0}건 / 창고합계 {FormatNumber(row.WarehouseSum)} / {row.WarehouseBreakdown}"))
            .ToList();
    }

    private async Task<List<DeletedItemStockResidueSnapshot>> LoadDeletedItemStockResidueSnapshotsAsync(CancellationToken cancellationToken)
    {
        var deletedItems = await _officeScopeService.ApplyItemScope(_dbContext.Items.IgnoreQueryFilters().AsNoTracking())
            .Where(item => item.IsDeleted)
            .Select(item => new DeletedItemSnapshot(
                item.Id,
                item.TenantCode,
                item.OfficeCode,
                item.NameOriginal,
                item.NameMatchKey,
                item.SpecificationOriginal,
                item.CategoryName,
                item.CurrentStock))
            .ToListAsync(cancellationToken);
        if (deletedItems.Count == 0)
            return [];

        var deletedItemIds = deletedItems.Select(item => item.Id).ToHashSet();
        var warehouseStocks = new List<ItemWarehouseStockSnapshot>();
        foreach (var batchIds in deletedItemIds.Chunk(500))
        {
            var scopedBatchIds = batchIds.ToList();
            warehouseStocks.AddRange(await _officeScopeService.ApplyWarehouseScope(
                    _dbContext.ItemWarehouseStocks.IgnoreQueryFilters().AsNoTracking())
                .Where(stock => scopedBatchIds.Contains(stock.ItemId))
                .Select(stock => new ItemWarehouseStockSnapshot(stock.ItemId, stock.WarehouseCode, stock.Quantity))
                .ToListAsync(cancellationToken));
        }

        var warehouseStocksByItem = warehouseStocks
            .GroupBy(stock => stock.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(stock => stock.WarehouseCode, StringComparer.OrdinalIgnoreCase).ToList());

        return deletedItems
            .Select(item =>
            {
                var itemStocks = warehouseStocksByItem.TryGetValue(item.Id, out var rows) ? rows : [];
                var warehouseSum = itemStocks.Sum(stock => stock.Quantity);
                var warehouseBreakdown = itemStocks.Count == 0
                    ? "창고 행 없음"
                    : string.Join(", ", itemStocks.Select(stock => $"{NormalizeCellText(stock.WarehouseCode)}:{FormatNumber(stock.Quantity)}"));
                return new DeletedItemStockResidueSnapshot(item, itemStocks.Count, warehouseSum, warehouseBreakdown);
            })
            .Where(row => row.Item.CurrentStock != 0m || row.WarehouseRowCount > 0)
            .OrderBy(row => row.Item.NameOriginal, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Item.SpecificationOriginal, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Item.Id)
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadItemStockSnapshotMismatchDetailsAsync(CancellationToken cancellationToken)
    {
        var scopedItems = await _officeScopeService.ApplyItemScope(_dbContext.Items.IgnoreQueryFilters().AsNoTracking())
            .Where(item => !item.IsDeleted)
            .ToListAsync(cancellationToken);

        var warehouseStocks = await _officeScopeService.ApplyItemWarehouseStockScope(
                _dbContext.ItemWarehouseStocks.IgnoreQueryFilters().AsNoTracking())
            .Select(stock => new ItemWarehouseStockSnapshot(stock.ItemId, stock.WarehouseCode, stock.Quantity))
            .ToListAsync(cancellationToken);

        var warehouseStocksByItem = warehouseStocks
            .GroupBy(stock => stock.ItemId)
            .ToDictionary(group => group.Key, group => group.OrderBy(stock => stock.WarehouseCode, StringComparer.OrdinalIgnoreCase).ToList());

        return scopedItems
            .Where(item => ItemOperationalPolicy.SupportsInventory(item.TrackingType))
            .Select(item =>
            {
                var itemStocks = warehouseStocksByItem.TryGetValue(item.Id, out var rows) ? rows : [];
                var warehouseSum = itemStocks.Sum(row => row.Quantity);
                var difference = item.CurrentStock - warehouseSum;
                return new
                {
                    Item = item,
                    WarehouseSum = warehouseSum,
                    Difference = difference,
                    WarehouseBreakdown = itemStocks.Count == 0
                        ? "창고 행 없음"
                        : string.Join(", ", itemStocks.Select(stock => $"{NormalizeCellText(stock.WarehouseCode)}:{FormatNumber(stock.Quantity)}"))
                };
            })
            .Where(row => row.Item.CurrentStock != row.WarehouseSum)
            .OrderBy(row => row.Item.NameOriginal, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Item.SpecificationOriginal, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Item.Id)
            .Select(row => CreateDetailRow(
                entityType: "품목",
                entityIdText: FormatGuid(row.Item.Id),
                primaryText: row.Item.NameOriginal,
                secondaryText: CombineParts(row.Item.SpecificationOriginal, row.Item.CategoryName),
                referenceText: string.IsNullOrWhiteSpace(row.Item.NameMatchKey) ? FormatGuid(row.Item.Id) : $"품명키 {row.Item.NameMatchKey}",
                scopeText: FormatScope(row.Item.TenantCode, row.Item.OfficeCode),
                detailText: $"현재재고 {FormatNumber(row.Item.CurrentStock)} / 창고합계 {FormatNumber(row.WarehouseSum)} / 차이 {FormatNumber(row.Difference)} / {row.WarehouseBreakdown}"))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadNegativeCurrentStockDetailsAsync(CancellationToken cancellationToken)
    {
        var scopedItems = await _officeScopeService.ApplyItemScope(_dbContext.Items.IgnoreQueryFilters().AsNoTracking())
            .Where(item => !item.IsDeleted)
            .ToListAsync(cancellationToken);

        var negativeItems = scopedItems
            .Where(item => ItemOperationalPolicy.SupportsInventory(item.TrackingType) && item.CurrentStock < 0m)
            .OrderBy(item => item.NameOriginal, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SpecificationOriginal, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .ToList();

        if (negativeItems.Count == 0)
            return [];

        var itemIds = negativeItems.Select(item => item.Id).ToHashSet();
        var warehouseStocks = await _officeScopeService.ApplyItemWarehouseStockScope(_dbContext.ItemWarehouseStocks.IgnoreQueryFilters().AsNoTracking())
            .Where(stock => itemIds.Contains(stock.ItemId))
            .ToListAsync(cancellationToken);
        var warehouseStocksByItem = warehouseStocks
            .GroupBy(stock => stock.ItemId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var recentInvoiceRows = await (
                from line in _dbContext.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                join invoice in _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
                    on line.InvoiceId equals invoice.Id
                join customer in _dbContext.Customers.IgnoreQueryFilters().AsNoTracking()
                    on invoice.CustomerId equals customer.Id into customerJoin
                from customer in customerJoin.DefaultIfEmpty()
                where line.ItemId.HasValue &&
                      itemIds.Contains(line.ItemId.Value) &&
                      !line.IsDeleted &&
                      !invoice.IsDeleted
                select new NegativeStockInvoiceEvidence(
                    line.ItemId!.Value,
                    invoice.InvoiceNumber,
                    invoice.InvoiceDate,
                    invoice.VoucherType,
                    invoice.SourceWarehouseCode,
                    invoice.PurchaseReceivingWarehouseCode,
                    customer == null ? string.Empty : customer.NameOriginal,
                    line.Quantity,
                    line.LineAmount))
            .ToListAsync(cancellationToken);
        var recentInvoicesByItem = recentInvoiceRows
            .GroupBy(row => row.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(row => row.InvoiceDate)
                    .ThenByDescending(row => row.InvoiceNumber, StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .Select(FormatNegativeStockInvoiceEvidence)
                    .ToList());

        var recentLedgerRows = await _dbContext.InventoryLedgerEntries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entry => itemIds.Contains(entry.ItemId))
            .OrderByDescending(entry => entry.OccurredDate)
            .ThenByDescending(entry => entry.CreatedAtUtc)
            .Take(Math.Max(negativeItems.Count * 6, 20))
            .ToListAsync(cancellationToken);
        var recentLedgersByItem = recentLedgerRows
            .GroupBy(entry => entry.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(entry => entry.OccurredDate)
                    .ThenByDescending(entry => entry.CreatedAtUtc)
                    .Take(3)
                    .Select(entry => CombineParts(
                        FormatDate(entry.OccurredDate),
                        string.IsNullOrWhiteSpace(entry.SourceType) ? null : entry.SourceType,
                        $"수량 {FormatSignedNumber(entry.QuantityDelta)}",
                        string.IsNullOrWhiteSpace(entry.WarehouseCode) ? null : $"창고 {entry.WarehouseCode}"))
                    .ToList());

        return negativeItems
            .Select(item =>
            {
                List<ItemWarehouseStock> itemStocks = warehouseStocksByItem.GetValueOrDefault(item.Id) ?? [];
                var warehouseBreakdown = itemStocks.Count == 0
                    ? "창고재고 행 없음"
                    : "창고재고 " + string.Join(", ", itemStocks
                        .OrderBy(stock => stock.WarehouseCode, StringComparer.OrdinalIgnoreCase)
                        .Select(stock => $"{NormalizeCellText(stock.WarehouseCode)}:{FormatNumber(stock.Quantity)}"));
                List<string> recentInvoices = recentInvoicesByItem.GetValueOrDefault(item.Id) ?? [];
                List<string> recentLedgers = recentLedgersByItem.GetValueOrDefault(item.Id) ?? [];

                return CreateDetailRow(
                entityType: "품목",
                entityIdText: FormatGuid(item.Id),
                primaryText: item.NameOriginal,
                secondaryText: CombineParts(item.SpecificationOriginal, item.CategoryName),
                    referenceText: CombineParts(
                        string.IsNullOrWhiteSpace(item.NameMatchKey) ? FormatGuid(item.Id) : $"품명키 {item.NameMatchKey}",
                        recentInvoices.Count == 0 ? "최근 전표 없음" : $"최근 전표 {string.Join("; ", recentInvoices)}"),
                scopeText: FormatScope(item.TenantCode, item.OfficeCode),
                detailText: CombineParts(
                    $"현재재고 {FormatNumber(item.CurrentStock)}",
                        warehouseBreakdown,
                    string.IsNullOrWhiteSpace(item.TrackingType) ? null : $"재고방식 {item.TrackingType}",
                        item.SafetyStock > 0m ? $"안전재고 {FormatNumber(item.SafetyStock)}" : null,
                        recentLedgers.Count == 0 ? "재고이력 없음" : $"재고이력 {string.Join("; ", recentLedgers)}",
                        "조치: 판매/구매 전표와 재고이동·재고초기화 이력을 확인하세요"));
            })
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadOrphanInvoiceCustomerDetailsAsync(CancellationToken cancellationToken)
    {
        var invoices = await (
                from invoice in _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
                    .Where(current => !current.IsDeleted)
                join customer in _dbContext.Customers.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    on invoice.CustomerId equals customer.Id into customerGroup
                from customer in customerGroup.DefaultIfEmpty()
                where customer == null
                orderby invoice.InvoiceDate, invoice.InvoiceNumber
                select invoice)
            .ToListAsync(cancellationToken);

        return invoices
            .Select(invoice => CreateDetailRow(
                entityType: "전표",
                entityIdText: FormatGuid(invoice.Id),
                primaryText: FirstNonEmpty(invoice.InvoiceNumber, invoice.LocalTempNumber, FormatGuid(invoice.Id)),
                secondaryText: CombineParts(invoice.LocalTempNumber, FormatDate(invoice.InvoiceDate)),
                referenceText: $"누락 거래처 {FormatGuid(invoice.CustomerId)}",
                scopeText: FormatScope(invoice.TenantCode, invoice.OfficeCode, invoice.ResponsibleOfficeCode),
                detailText: CombineParts($"전표유형 {invoice.VoucherType}", $"합계 {FormatMoney(invoice.TotalAmount)}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadActiveInvoiceLinesDeletedInvoiceDetailsAsync(CancellationToken cancellationToken)
    {
        var lines = await (
                from line in _dbContext.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                join invoice in _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
                    on line.InvoiceId equals invoice.Id
                where !line.IsDeleted && invoice.IsDeleted
                orderby invoice.InvoiceDate, invoice.InvoiceNumber, line.ItemNameOriginal
                select new
                {
                    Line = line,
                    Invoice = invoice
                })
            .ToListAsync(cancellationToken);

        return lines
            .Select(row => CreateDetailRow(
                entityType: "전표세부내역",
                entityIdText: FormatGuid(row.Line.Id),
                primaryText: FirstNonEmpty(row.Line.ItemNameOriginal, FormatGuid(row.Line.Id)),
                secondaryText: CombineParts(row.Line.SpecificationOriginal, $"수량 {FormatNumber(row.Line.Quantity)}", $"금액 {FormatMoney(row.Line.LineAmount)}"),
                referenceText: $"삭제 전표 {FirstNonEmpty(row.Invoice.InvoiceNumber, row.Invoice.LocalTempNumber, FormatGuid(row.Invoice.Id))}",
                scopeText: FormatScope(row.Invoice.TenantCode, row.Invoice.OfficeCode, row.Invoice.ResponsibleOfficeCode),
                detailText: CombineParts(
                    $"전표일 {FormatDate(row.Invoice.InvoiceDate)}",
                    $"전표유형 {row.Invoice.VoucherType}",
                    $"전표ID {FormatGuid(row.Invoice.Id)}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadActiveInvoiceDeletedLineOnlyDetailsAsync(CancellationToken cancellationToken)
    {
        var invoices = await (
                from invoice in _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
                    .Where(invoice => !invoice.IsDeleted && invoice.TotalAmount != 0m)
                where _dbContext.InvoiceLines.IgnoreQueryFilters().Any(line => line.InvoiceId == invoice.Id && line.IsDeleted) &&
                      !_dbContext.InvoiceLines.IgnoreQueryFilters().Any(line => line.InvoiceId == invoice.Id && !line.IsDeleted)
                orderby invoice.InvoiceDate, invoice.InvoiceNumber, invoice.Id
                select invoice)
            .ToListAsync(cancellationToken);

        var invoiceIds = invoices.Select(invoice => invoice.Id).ToHashSet();
        var deletedLineStats = new Dictionary<Guid, (int Count, decimal Amount)>();
        if (invoiceIds.Count > 0)
        {
            var deletedLines = await _dbContext.InvoiceLines
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(line => invoiceIds.Contains(line.InvoiceId) && line.IsDeleted)
                .Select(line => new
                {
                    line.InvoiceId,
                    line.LineAmount
                })
                .ToListAsync(cancellationToken);
            deletedLineStats = deletedLines
                .GroupBy(line => line.InvoiceId)
                .ToDictionary(
                    group => group.Key,
                    group => (Count: group.Count(), Amount: group.Sum(line => line.LineAmount)));
        }

        return invoices
            .Select(invoice =>
            {
                var stats = deletedLineStats.TryGetValue(invoice.Id, out var value)
                    ? value
                    : (Count: 0, Amount: 0m);
                return CreateDetailRow(
                    entityType: "전표",
                    entityIdText: FormatGuid(invoice.Id),
                    primaryText: FirstNonEmpty(invoice.InvoiceNumber, invoice.LocalTempNumber, FormatGuid(invoice.Id)),
                    secondaryText: CombineParts(FormatDate(invoice.InvoiceDate), $"전표유형 {invoice.VoucherType}"),
                    referenceText: $"거래처 {FormatGuid(invoice.CustomerId)}",
                    scopeText: FormatScope(invoice.TenantCode, invoice.OfficeCode, invoice.ResponsibleOfficeCode),
                    detailText: CombineParts(
                        $"총액 {FormatMoney(invoice.TotalAmount)}",
                        $"삭제 세부내역 {FormatNumber(stats.Count)}",
                        $"삭제 세부금액 {FormatMoney(stats.Amount)}",
                        "활성 세부내역 0"));
            })
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadInvoiceTotalActiveLineMismatchDetailsAsync(CancellationToken cancellationToken)
    {
        var rows = await LoadInvoiceTotalActiveLineMismatchRowsAsync(cancellationToken);
        return rows
            .Select(row => CreateDetailRow(
                entityType: "전표",
                entityIdText: FormatGuid(row.InvoiceId),
                primaryText: FirstNonEmpty(row.InvoiceNumber, row.LocalTempNumber, FormatGuid(row.InvoiceId)),
                secondaryText: CombineParts(FormatDate(row.InvoiceDate), $"전표유형 {row.VoucherType}", $"VAT {row.VatMode}"),
                referenceText: $"거래처 {FormatGuid(row.CustomerId)}",
                scopeText: FormatScope(row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode),
                detailText: CombineParts(
                    $"전표 총액 {FormatMoney(row.TotalAmount)}",
                    $"활성 라인 합계 {FormatMoney(row.ExpectedTotalAmount)}",
                    $"차이 {FormatMoney(row.TotalAmount - row.ExpectedTotalAmount)}",
                    $"활성 라인 수 {FormatNumber(row.ActiveLineCount)}",
                    $"공급가 {FormatMoney(row.SupplyAmount)} / 기대 {FormatMoney(row.ExpectedSupplyAmount)}",
                    $"부가세 {FormatMoney(row.VatAmount)} / 기대 {FormatMoney(row.ExpectedVatAmount)}")))
            .ToList();
    }

    private async Task<List<InvoiceTotalActiveLineMismatchRow>> LoadInvoiceTotalActiveLineMismatchRowsAsync(CancellationToken cancellationToken)
    {
        var scopedActiveInvoices = _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
            .Where(invoice => !invoice.IsDeleted);
        var invoices = await scopedActiveInvoices
            .Select(invoice => new
            {
                invoice.Id,
                invoice.CustomerId,
                invoice.TenantCode,
                invoice.OfficeCode,
                invoice.ResponsibleOfficeCode,
                invoice.InvoiceNumber,
                invoice.LocalTempNumber,
                invoice.InvoiceDate,
                invoice.VoucherType,
                invoice.VatMode,
                invoice.SupplyAmount,
                invoice.VatAmount,
                invoice.TotalAmount
            })
            .ToListAsync(cancellationToken);
        if (invoices.Count == 0)
            return [];

        var scopedActiveInvoiceIds = scopedActiveInvoices.Select(invoice => invoice.Id);
        var activeLines = await _dbContext.InvoiceLines
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(line => !line.IsDeleted && scopedActiveInvoiceIds.Contains(line.InvoiceId))
            .Select(line => new
            {
                line.InvoiceId,
                line.LineAmount
            })
            .ToListAsync(cancellationToken);
        var lineTotals = activeLines
            .GroupBy(line => line.InvoiceId)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    Count = group.Count(),
                    Total = group.Sum(line => line.LineAmount)
                });

        return invoices
            .Select(invoice =>
            {
                var lineStats = lineTotals.TryGetValue(invoice.Id, out var value)
                    ? value
                    : new { Count = 0, Total = 0m };
                var expected = InvoiceVatModes.CalculateTotals(new[] { lineStats.Total }, invoice.VatMode);
                if (!AmountDiffers(invoice.TotalAmount, expected.TotalAmount) &&
                    !AmountDiffers(invoice.SupplyAmount, expected.SupplyAmount) &&
                    !AmountDiffers(invoice.VatAmount, expected.VatAmount))
                {
                    return null;
                }

                return new InvoiceTotalActiveLineMismatchRow(
                    invoice.Id,
                    invoice.CustomerId,
                    invoice.TenantCode,
                    invoice.OfficeCode,
                    invoice.ResponsibleOfficeCode,
                    invoice.InvoiceNumber,
                    invoice.LocalTempNumber,
                    invoice.InvoiceDate,
                    invoice.VoucherType,
                    invoice.VatMode,
                    invoice.SupplyAmount,
                    invoice.VatAmount,
                    invoice.TotalAmount,
                    lineStats.Count,
                    lineStats.Total,
                    expected.SupplyAmount,
                    expected.VatAmount,
                    expected.TotalAmount);
            })
            .OfType<InvoiceTotalActiveLineMismatchRow>()
            .OrderByDescending(row => row.InvoiceDate)
            .ThenBy(row => row.InvoiceNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.InvoiceId)
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadInvoiceLineMissingInvoiceRowDetailsAsync(CancellationToken cancellationToken)
    {
        if (!_officeScopeService.HasGlobalDataScope)
            return [];

        var lines = await (
                from line in _dbContext.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
                    on line.InvoiceId equals invoice.Id into invoiceGroup
                from invoice in invoiceGroup.DefaultIfEmpty()
                where invoice == null
                orderby line.InvoiceId, line.OrderIndex, line.Id
                select line)
            .ToListAsync(cancellationToken);

        return lines
            .Select(line => CreateDetailRow(
                entityType: "전표세부내역",
                entityIdText: FormatGuid(line.Id),
                primaryText: FirstNonEmpty(line.ItemNameOriginal, FormatGuid(line.Id)),
                secondaryText: CombineParts(line.SpecificationOriginal, line.Unit, line.SerialNumber),
                referenceText: $"누락 전표 행 {FormatGuid(line.InvoiceId)}",
                scopeText: "공통",
                detailText: CombineParts(
                    line.IsDeleted ? "삭제상태 삭제" : "삭제상태 활성",
                    $"수량 {FormatNumber(line.Quantity)}",
                    $"단가 {FormatMoney(line.UnitPrice)}",
                    $"금액 {FormatMoney(line.LineAmount)}",
                    line.OrderIndex > 0 ? $"순번 {line.OrderIndex:N0}" : null,
                    string.IsNullOrWhiteSpace(line.Remark) ? null : $"비고 {line.Remark}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadOrphanTransactionCustomerDetailsAsync(CancellationToken cancellationToken)
    {
        var transactions = await (
                from transaction in _officeScopeService.ApplyTransactionScope(_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
                    .Where(current => !current.IsDeleted)
                join customer in _dbContext.Customers.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    on transaction.CustomerId equals customer.Id into customerGroup
                from customer in customerGroup.DefaultIfEmpty()
                where customer == null
                orderby transaction.TransactionDate, transaction.Id
                select transaction)
            .ToListAsync(cancellationToken);

        return transactions
            .Select(transaction => CreateDetailRow(
                entityType: "거래내역",
                entityIdText: FormatGuid(transaction.Id),
                primaryText: CombineParts(transaction.TransactionKind, FormatDate(transaction.TransactionDate)),
                secondaryText: FirstNonEmpty(transaction.LinkedInvoiceNumber, transaction.Note, transaction.Memo),
                referenceText: $"누락 거래처 {FormatGuid(transaction.CustomerId)}",
                scopeText: FormatScope(transaction.TenantCode, transaction.OfficeCode, transaction.ResponsibleOfficeCode),
                detailText: CombineParts(
                    $"정산 {FormatMoney(transaction.SettlementAmount)}",
                    $"수금합계 {FormatMoney(transaction.ReceiptTotal)}",
                    $"지급합계 {FormatMoney(transaction.PaymentTotal)}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadOrphanRentalProfileCustomerDetailsAsync(CancellationToken cancellationToken)
    {
        var profiles = await (
                from profile in _officeScopeService.ApplyRentalBillingProfileScope(_dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
                    .Where(current => !current.IsDeleted && current.CustomerId.HasValue)
                join customer in _dbContext.Customers.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    on profile.CustomerId!.Value equals customer.Id into customerGroup
                from customer in customerGroup.DefaultIfEmpty()
                where customer == null
                orderby profile.ProfileKey, profile.Id
                select profile)
            .ToListAsync(cancellationToken);

        return profiles
            .Select(profile => CreateDetailRow(
                entityType: "렌탈청구프로필",
                entityIdText: FormatGuid(profile.Id),
                primaryText: FirstNonEmpty(profile.ProfileKey, FormatGuid(profile.Id)),
                secondaryText: CombineParts(profile.CustomerName, profile.ItemName),
                referenceText: profile.CustomerId.HasValue ? $"누락 거래처 {FormatGuid(profile.CustomerId.Value)}" : "거래처 미연결",
                scopeText: FormatScope(profile.TenantCode, profile.OfficeCode, profile.ResponsibleOfficeCode),
                detailText: CombineParts(
                    string.IsNullOrWhiteSpace(profile.ManagementCompanyCode) ? null : $"관리업체 {profile.ManagementCompanyCode}",
                    string.IsNullOrWhiteSpace(profile.InstallSiteName) ? null : $"설치처 {profile.InstallSiteName}",
                    string.IsNullOrWhiteSpace(profile.BillingStatus) ? null : $"청구상태 {profile.BillingStatus}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalProfileCustomerScopeMismatchDetailsAsync(CancellationToken cancellationToken)
    {
        var rows = await (
                from profile in _officeScopeService.ApplyRentalBillingProfileScope(_dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
                    .Where(current => !current.IsDeleted && current.CustomerId.HasValue)
                join customer in _dbContext.Customers.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    on profile.CustomerId!.Value equals customer.Id
                where profile.TenantCode != customer.TenantCode ||
                      profile.ResponsibleOfficeCode != customer.ResponsibleOfficeCode
                orderby profile.ProfileKey, profile.Id
                select new { Profile = profile, Customer = customer })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => CreateDetailRow(
                entityType: "렌탈청구프로필",
                entityIdText: FormatGuid(row.Profile.Id),
                primaryText: FirstNonEmpty(row.Profile.ProfileKey, FormatGuid(row.Profile.Id)),
                secondaryText: CombineParts(row.Profile.CustomerName, row.Profile.ItemName),
                referenceText: CombineParts(
                    $"거래처 범위 불일치 {FormatGuid(row.Customer.Id)}",
                    $"거래처 범위 {FormatScope(row.Customer.TenantCode, row.Customer.OfficeCode, row.Customer.ResponsibleOfficeCode)}"),
                scopeText: FormatScope(row.Profile.TenantCode, row.Profile.OfficeCode, row.Profile.ResponsibleOfficeCode),
                detailText: CombineParts(
                    string.IsNullOrWhiteSpace(row.Customer.NameOriginal) ? null : $"거래처 {row.Customer.NameOriginal}",
                    string.IsNullOrWhiteSpace(row.Profile.ManagementCompanyCode) ? null : $"관리업체 {row.Profile.ManagementCompanyCode}",
                    string.IsNullOrWhiteSpace(row.Profile.InstallSiteName) ? null : $"설치처 {row.Profile.InstallSiteName}",
                    "조치: 렌탈 청구 프로필과 같은 업체/담당지점의 거래처로 다시 연결하세요")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalProfileCustomerUnlinkedDetailsAsync(CancellationToken cancellationToken)
    {
        var profiles = await _officeScopeService.ApplyRentalBillingProfileScope(_dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
            .Where(profile => !profile.IsDeleted && !profile.CustomerId.HasValue && !string.IsNullOrWhiteSpace(profile.CustomerName))
            .OrderBy(profile => profile.CustomerName)
            .ThenBy(profile => profile.ProfileKey)
            .ThenBy(profile => profile.Id)
            .ToListAsync(cancellationToken);

        if (profiles.Count == 0)
            return [];

        var profileIds = profiles.Select(profile => profile.Id).ToHashSet();
        var linkedAssets = await _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
            .Where(asset => !asset.IsDeleted && asset.BillingProfileId.HasValue && profileIds.Contains(asset.BillingProfileId.Value))
            .ToListAsync(cancellationToken);
        var linkedAssetsByProfile = linkedAssets
            .GroupBy(asset => asset.BillingProfileId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var customerCandidateKeys = profiles
            .Select(profile => profile.CustomerName)
            .Concat(linkedAssets.Select(asset => asset.CustomerName))
            .Concat(linkedAssets.Select(asset => asset.CurrentCustomerName))
            .Concat(linkedAssets.Select(asset => asset.LastCustomerName))
            .Select(MatchKeyNormalizer.Normalize)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var scopedCustomers = await _officeScopeService.ApplyCustomerScope(_dbContext.Customers.IgnoreQueryFilters().AsNoTracking())
            .Where(customer => !customer.IsDeleted)
            .ToListAsync(cancellationToken);
        var customerCandidatesByKey = scopedCustomers
            .Where(customer => customerCandidateKeys.Contains(customer.NameMatchKey))
            .GroupBy(customer => customer.NameMatchKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        return profiles
            .Select(profile =>
            {
                List<RentalAsset> assets = linkedAssetsByProfile.GetValueOrDefault(profile.Id) ?? [];
                var candidateKey = MatchKeyNormalizer.Normalize(profile.CustomerName);
                List<Customer> candidates = string.IsNullOrWhiteSpace(candidateKey)
                    ? []
                    : customerCandidatesByKey.GetValueOrDefault(candidateKey) ?? [];
                List<Customer> similarCandidates = candidates.Count == 0
                    ? FindSimilarRentalCustomers(profile, assets, scopedCustomers)
                    : [];
                var assetSummary = assets.Count == 0
                    ? "연결 장비 없음"
                    : "연결 장비 " + string.Join("; ", assets
                        .OrderBy(asset => FirstNonEmpty(asset.ManagementNumber, asset.AssetKey, asset.ManagementId, FormatGuid(asset.Id)), StringComparer.OrdinalIgnoreCase)
                        .Take(4)
                        .Select(asset => CombineParts(
                            FirstNonEmpty(asset.ManagementNumber, asset.AssetKey, asset.ManagementId, FormatGuid(asset.Id)),
                            string.IsNullOrWhiteSpace(asset.AssetKey) ? null : $"자산키 {asset.AssetKey}",
                            asset.CustomerId.HasValue ? $"거래처ID {FormatGuid(asset.CustomerId.Value)}" : "거래처ID 없음",
                            FirstNonEmpty(asset.CurrentCustomerName, asset.CustomerName, asset.LastCustomerName),
                            string.IsNullOrWhiteSpace(asset.InstallLocation) ? asset.InstallSiteName : asset.InstallLocation,
                            $"월요금 {FormatMoney(asset.MonthlyFee)}")));
                var candidateSummary = candidates.Count == 0
                    ? similarCandidates.Count == 0
                        ? "일치/유사 거래처 없음"
                        : "유사 거래처 " + string.Join(", ", similarCandidates
                            .OrderBy(customer => customer.ResponsibleOfficeCode, StringComparer.OrdinalIgnoreCase)
                            .ThenBy(customer => customer.NameOriginal, StringComparer.OrdinalIgnoreCase)
                            .Take(4)
                            .Select(customer => CombineParts(customer.NameOriginal, customer.ResponsibleOfficeCode, FormatGuid(customer.Id))))
                    : "일치 거래처 " + string.Join(", ", candidates
                        .OrderBy(customer => customer.ResponsibleOfficeCode, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(customer => customer.NameOriginal, StringComparer.OrdinalIgnoreCase)
                        .Take(4)
                        .Select(customer => CombineParts(customer.NameOriginal, customer.ResponsibleOfficeCode, FormatGuid(customer.Id))));

                return CreateDetailRow(
                entityType: "렌탈청구프로필",
                entityIdText: FormatGuid(profile.Id),
                primaryText: FirstNonEmpty(profile.CustomerName, profile.ProfileKey, FormatGuid(profile.Id)),
                secondaryText: CombineParts(profile.ItemName, profile.InstallSiteName),
                    referenceText: CombineParts("거래처 ID 미연결", candidateSummary),
                scopeText: FormatScope(profile.TenantCode, profile.OfficeCode, profile.ResponsibleOfficeCode),
                detailText: CombineParts(
                    $"월기준금액 {FormatMoney(profile.MonthlyAmount)}",
                    string.IsNullOrWhiteSpace(profile.BillingType) ? null : $"라인유형 {profile.BillingType}",
                        string.IsNullOrWhiteSpace(profile.BillingStatus) ? null : $"청구상태 {profile.BillingStatus}",
                        assetSummary,
                        "조치: 거래처가 등록되어 있으면 렌탈 프로필/장비의 거래처를 지정하고, 없으면 거래처 등록 후 연결하세요"));
            })
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalProfileMonthlyAmountMismatchDetailsAsync(CancellationToken cancellationToken)
    {
        var rows = await LoadRentalTemplateScanRowsAsync(cancellationToken);

        return rows
            .Where(row => row.TemplateParseSucceeded && row.TemplateItems.Count > 0 && AmountDiffers(row.Profile.MonthlyAmount, row.TemplateMonthlyAmount))
            .OrderBy(row => row.Profile.CustomerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Profile.ProfileKey, StringComparer.OrdinalIgnoreCase)
            .Select(row => CreateDetailRow(
                entityType: "렌탈청구프로필",
                entityIdText: FormatGuid(row.Profile.Id),
                primaryText: FirstNonEmpty(row.Profile.CustomerName, row.Profile.ProfileKey, FormatGuid(row.Profile.Id)),
                secondaryText: CombineParts(row.Profile.ItemName, row.Profile.InstallSiteName),
                referenceText: $"청구 품목 {row.TemplateItems.Count:N0}개",
                scopeText: FormatScope(row.Profile.TenantCode, row.Profile.OfficeCode, row.Profile.ResponsibleOfficeCode),
                detailText: CombineParts(
                    $"월기준금액 {FormatMoney(row.Profile.MonthlyAmount)}",
                    $"품목합계 {FormatMoney(row.TemplateMonthlyAmount)}",
                    $"차이 {FormatMoney(row.Profile.MonthlyAmount - row.TemplateMonthlyAmount)}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalProfileAssetMonthlyAmountMismatchDetailsAsync(CancellationToken cancellationToken)
    {
        var rows = await LoadRentalTemplateScanRowsAsync(cancellationToken);

        return rows
            .Where(ShouldWarnRentalProfileAssetMonthlyMismatch)
            .OrderBy(row => row.Profile.CustomerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Profile.ProfileKey, StringComparer.OrdinalIgnoreCase)
            .Select(row => CreateDetailRow(
                entityType: "렌탈청구프로필",
                entityIdText: FormatGuid(row.Profile.Id),
                primaryText: FirstNonEmpty(row.Profile.CustomerName, row.Profile.ProfileKey, FormatGuid(row.Profile.Id)),
                secondaryText: CombineParts(row.Profile.ItemName, row.Profile.InstallSiteName),
                referenceText: $"연결 자산 {row.LinkedAssets.Count:N0}대",
                scopeText: FormatScope(row.Profile.TenantCode, row.Profile.OfficeCode, row.Profile.ResponsibleOfficeCode),
                detailText: CombineParts(
                    $"월기준금액 {FormatMoney(row.Profile.MonthlyAmount)}",
                    $"자산월요금합계 {FormatMoney(row.LinkedAssetMonthlyAmount)}",
                    $"차이 {FormatMoney(row.Profile.MonthlyAmount - row.LinkedAssetMonthlyAmount)}",
                    BuildAssetMonthlyBreakdown(row.LinkedAssets))))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalAssetTemplateMonthlyMismatchDetailsAsync(CancellationToken cancellationToken)
    {
        var rows = await LoadRentalTemplateScanRowsAsync(cancellationToken);

        return rows
            .SelectMany(CreateRentalAssetTemplateMonthlyMismatchRows)
            .OrderBy(row => row.Profile.CustomerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Profile.ProfileKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.TemplateItem.DisplayItemName, StringComparer.OrdinalIgnoreCase)
            .Select(row => CreateDetailRow(
                entityType: "렌탈청구품목",
                entityIdText: FormatGuid(row.Profile.Id),
                primaryText: FirstNonEmpty(row.Profile.CustomerName, row.Profile.ProfileKey, FormatGuid(row.Profile.Id)),
                secondaryText: FirstNonEmpty(row.TemplateItem.DisplayItemName, row.Profile.ItemName, "청구 품목"),
                referenceText: $"연결 자산 {row.LinkedAssets.Count:N0}대",
                scopeText: FormatScope(row.Profile.TenantCode, row.Profile.OfficeCode, row.Profile.ResponsibleOfficeCode),
                detailText: CombineParts(
                    $"품목금액 {FormatMoney(row.TemplateMonthlyAmount)}",
                    $"자산월요금합계 {FormatMoney(row.AssetMonthlyAmount)}",
                    $"차이 {FormatMoney(row.TemplateMonthlyAmount - row.AssetMonthlyAmount)}",
                    BuildAssetMonthlyBreakdown(row.LinkedAssets))))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadOrphanRentalAssetCustomerDetailsAsync(CancellationToken cancellationToken)
    {
        var assets = await (
                from asset in _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
                    .Where(current => !current.IsDeleted && current.CustomerId.HasValue)
                join customer in _dbContext.Customers.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    on asset.CustomerId!.Value equals customer.Id into customerGroup
                from customer in customerGroup.DefaultIfEmpty()
                where customer == null
                orderby asset.ManagementNumber, asset.AssetKey, asset.Id
                select asset)
            .ToListAsync(cancellationToken);

        return assets
            .Select(asset => CreateDetailRow(
                entityType: "렌탈자산",
                entityIdText: FormatGuid(asset.Id),
                primaryText: FirstNonEmpty(asset.ManagementNumber, asset.AssetKey, asset.ManagementId),
                secondaryText: CombineParts(asset.CustomerName, asset.ItemName),
                referenceText: asset.CustomerId.HasValue ? $"누락 거래처 {FormatGuid(asset.CustomerId.Value)}" : "거래처 미연결",
                scopeText: FormatScope(asset.TenantCode, asset.OfficeCode, asset.ResponsibleOfficeCode),
                detailText: CombineParts(
                    string.IsNullOrWhiteSpace(asset.AssetKey) ? null : $"자산키 {asset.AssetKey}",
                    asset.BillingProfileId.HasValue ? $"청구프로필 {FormatGuid(asset.BillingProfileId.Value)}" : null,
                    string.IsNullOrWhiteSpace(FirstNonEmpty(asset.InstallLocation, asset.CurrentLocation, asset.InstallSiteName))
                        ? null
                        : $"설치위치 {FirstNonEmpty(asset.InstallLocation, asset.CurrentLocation, asset.InstallSiteName)}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalAssetCustomerScopeMismatchDetailsAsync(CancellationToken cancellationToken)
    {
        var rows = await (
                from asset in _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
                    .Where(current => !current.IsDeleted && current.CustomerId.HasValue)
                join customer in _dbContext.Customers.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    on asset.CustomerId!.Value equals customer.Id
                where asset.TenantCode != customer.TenantCode ||
                      asset.ResponsibleOfficeCode != customer.ResponsibleOfficeCode
                orderby asset.ManagementNumber, asset.AssetKey, asset.Id
                select new { Asset = asset, Customer = customer })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => CreateDetailRow(
                entityType: "렌탈자산",
                entityIdText: FormatGuid(row.Asset.Id),
                primaryText: FirstNonEmpty(row.Asset.ManagementNumber, row.Asset.AssetKey, row.Asset.ManagementId),
                secondaryText: CombineParts(row.Asset.CustomerName, row.Asset.ItemName),
                referenceText: CombineParts(
                    $"거래처 범위 불일치 {FormatGuid(row.Customer.Id)}",
                    $"거래처 범위 {FormatScope(row.Customer.TenantCode, row.Customer.OfficeCode, row.Customer.ResponsibleOfficeCode)}"),
                scopeText: FormatScope(row.Asset.TenantCode, row.Asset.OfficeCode, row.Asset.ResponsibleOfficeCode),
                detailText: CombineParts(
                    string.IsNullOrWhiteSpace(row.Asset.AssetKey) ? null : $"자산키 {row.Asset.AssetKey}",
                    string.IsNullOrWhiteSpace(row.Customer.NameOriginal) ? null : $"거래처 {row.Customer.NameOriginal}",
                    string.IsNullOrWhiteSpace(FirstNonEmpty(row.Asset.InstallLocation, row.Asset.CurrentLocation, row.Asset.InstallSiteName))
                        ? null
                        : $"설치위치 {FirstNonEmpty(row.Asset.InstallLocation, row.Asset.CurrentLocation, row.Asset.InstallSiteName)}",
                    "조치: 렌탈 자산과 같은 업체/담당지점의 거래처로 다시 연결하세요")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadOrphanRentalAssetProfileDetailsAsync(CancellationToken cancellationToken)
    {
        var assets = await (
                from asset in _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
                    .Where(current => !current.IsDeleted && current.BillingProfileId.HasValue)
                join profile in _dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    on asset.BillingProfileId!.Value equals profile.Id into profileGroup
                from profile in profileGroup.DefaultIfEmpty()
                where profile == null
                orderby asset.ManagementNumber, asset.AssetKey, asset.Id
                select asset)
            .ToListAsync(cancellationToken);

        return assets
            .Select(asset => CreateDetailRow(
                entityType: "렌탈자산",
                entityIdText: FormatGuid(asset.Id),
                primaryText: FirstNonEmpty(asset.ManagementNumber, asset.AssetKey, asset.ManagementId),
                secondaryText: CombineParts(asset.CustomerName, asset.ItemName),
                referenceText: asset.BillingProfileId.HasValue ? $"누락 청구프로필 {FormatGuid(asset.BillingProfileId.Value)}" : "청구프로필 미연결",
                scopeText: FormatScope(asset.TenantCode, asset.OfficeCode, asset.ResponsibleOfficeCode),
                detailText: CombineParts(
                    string.IsNullOrWhiteSpace(asset.AssetKey) ? null : $"자산키 {asset.AssetKey}",
                    string.IsNullOrWhiteSpace(asset.ManagementCompanyCode) ? null : $"관리업체 {asset.ManagementCompanyCode}",
                    string.IsNullOrWhiteSpace(FirstNonEmpty(asset.InstallLocation, asset.CurrentLocation, asset.InstallSiteName))
                        ? null
                        : $"설치위치 {FirstNonEmpty(asset.InstallLocation, asset.CurrentLocation, asset.InstallSiteName)}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalAssetProfileScopeMismatchDetailsAsync(CancellationToken cancellationToken)
    {
        var rows = await (
                from asset in _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
                    .Where(current => !current.IsDeleted && current.BillingProfileId.HasValue)
                join profile in _dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    on asset.BillingProfileId!.Value equals profile.Id
                where asset.TenantCode != profile.TenantCode ||
                      asset.ResponsibleOfficeCode != profile.ResponsibleOfficeCode
                orderby asset.ManagementNumber, asset.AssetKey, asset.Id
                select new { Asset = asset, Profile = profile })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => CreateDetailRow(
                entityType: "렌탈자산",
                entityIdText: FormatGuid(row.Asset.Id),
                primaryText: FirstNonEmpty(row.Asset.ManagementNumber, row.Asset.AssetKey, row.Asset.ManagementId),
                secondaryText: CombineParts(row.Asset.CustomerName, row.Asset.ItemName),
                referenceText: CombineParts(
                    $"청구프로필 범위 불일치 {FormatGuid(row.Profile.Id)}",
                    $"청구프로필 범위 {FormatScope(row.Profile.TenantCode, row.Profile.OfficeCode, row.Profile.ResponsibleOfficeCode)}"),
                scopeText: FormatScope(row.Asset.TenantCode, row.Asset.OfficeCode, row.Asset.ResponsibleOfficeCode),
                detailText: CombineParts(
                    string.IsNullOrWhiteSpace(row.Asset.AssetKey) ? null : $"자산키 {row.Asset.AssetKey}",
                    string.IsNullOrWhiteSpace(row.Profile.ProfileKey) ? null : $"청구프로필키 {row.Profile.ProfileKey}",
                    string.IsNullOrWhiteSpace(FirstNonEmpty(row.Asset.InstallLocation, row.Asset.CurrentLocation, row.Asset.InstallSiteName))
                        ? null
                        : $"설치위치 {FirstNonEmpty(row.Asset.InstallLocation, row.Asset.CurrentLocation, row.Asset.InstallSiteName)}",
                    "조치: 렌탈 자산과 같은 업체/담당지점의 청구 프로필로 다시 연결하세요")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadOrphanRentalAssetItemDetailsAsync(CancellationToken cancellationToken)
    {
        var assets = await (
                from asset in _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
                    .Where(current => !current.IsDeleted && current.ItemId.HasValue)
                join item in _dbContext.Items.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    on asset.ItemId!.Value equals item.Id into itemGroup
                from item in itemGroup.DefaultIfEmpty()
                where item == null
                orderby asset.ManagementNumber, asset.AssetKey, asset.Id
                select asset)
            .ToListAsync(cancellationToken);

        return assets
            .Select(asset => CreateDetailRow(
                entityType: "렌탈자산",
                entityIdText: FormatGuid(asset.Id),
                primaryText: FirstNonEmpty(asset.ManagementNumber, asset.AssetKey, asset.ManagementId),
                secondaryText: CombineParts(asset.CustomerName, asset.ItemName),
                referenceText: asset.ItemId.HasValue ? $"누락 품목 {FormatGuid(asset.ItemId.Value)}" : "품목 미연결",
                scopeText: FormatScope(asset.TenantCode, asset.OfficeCode, asset.ResponsibleOfficeCode),
                detailText: CombineParts(
                    string.IsNullOrWhiteSpace(asset.AssetKey) ? null : $"자산키 {asset.AssetKey}",
                    asset.BillingProfileId.HasValue ? $"청구프로필 {FormatGuid(asset.BillingProfileId.Value)}" : null,
                    string.IsNullOrWhiteSpace(FirstNonEmpty(asset.InstallLocation, asset.CurrentLocation, asset.InstallSiteName))
                        ? null
                        : $"설치위치 {FirstNonEmpty(asset.InstallLocation, asset.CurrentLocation, asset.InstallSiteName)}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalAssignmentMissingReferenceDetailsAsync(CancellationToken cancellationToken)
    {
        var histories = await _officeScopeService.ApplyRentalAssignmentHistoryScope(
                _dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking())
            .Where(history => !history.IsDeleted)
            .Where(history =>
                history.AssetId == Guid.Empty ||
                !_dbContext.RentalAssets.IgnoreQueryFilters().Any(asset => !asset.IsDeleted && asset.Id == history.AssetId) ||
                (history.IsCurrent &&
                 ((history.CustomerId.HasValue && history.CustomerId.Value != Guid.Empty &&
                   !_dbContext.Customers.IgnoreQueryFilters().Any(customer => !customer.IsDeleted && customer.Id == history.CustomerId.Value)) ||
                  (history.BillingProfileId.HasValue && history.BillingProfileId.Value != Guid.Empty &&
                   !_dbContext.RentalBillingProfiles.IgnoreQueryFilters().Any(profile => !profile.IsDeleted && profile.Id == history.BillingProfileId.Value)))))
            .OrderBy(history => history.ResponsibleOfficeCode)
            .ThenBy(history => history.ManagementNumber)
            .ThenBy(history => history.LinkedAtUtc)
            .ThenBy(history => history.Id)
            .ToListAsync(cancellationToken);

        var activeAssetIds = await LoadActiveRentalAssetIdsAsync(histories.Select(history => history.AssetId), cancellationToken);
        var activeCustomerIds = await LoadActiveCustomerIdsAsync(histories.Select(history => history.CustomerId), cancellationToken);
        var activeProfileIds = await LoadActiveRentalBillingProfileIdsAsync(histories.Select(history => history.BillingProfileId), cancellationToken);

        return histories
            .Select(history => CreateDetailRow(
                entityType: "렌탈 임대이력",
                entityIdText: FormatGuid(history.Id),
                primaryText: BuildRentalAssignmentHistoryDisplay(history),
                secondaryText: CombineParts(history.CustomerName, history.ItemName, history.ManagementNumber),
                referenceText: BuildRentalAssignmentMissingReferenceText(history, activeAssetIds, activeCustomerIds, activeProfileIds),
                scopeText: FormatScope(history.TenantCode, history.OfficeCode, history.ResponsibleOfficeCode),
                detailText: BuildRentalAssignmentHistoryDetailText(history)))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalAssignmentCurrentScopeMismatchDetailsAsync(CancellationToken cancellationToken)
    {
        var rows = await (
                from history in _officeScopeService.ApplyRentalAssignmentHistoryScope(
                        _dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking())
                    .Where(history => !history.IsDeleted && history.IsCurrent && history.AssetId != Guid.Empty)
                join asset in _dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking().Where(asset => !asset.IsDeleted)
                    on history.AssetId equals asset.Id
                join customer in _dbContext.Customers.IgnoreQueryFilters().AsNoTracking().Where(customer => !customer.IsDeleted)
                    on history.CustomerId equals (Guid?)customer.Id into customerGroup
                from customer in customerGroup.DefaultIfEmpty()
                join profile in _dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().Where(profile => !profile.IsDeleted)
                    on history.BillingProfileId equals (Guid?)profile.Id into profileGroup
                from profile in profileGroup.DefaultIfEmpty()
                where history.TenantCode != asset.TenantCode ||
                      history.ResponsibleOfficeCode != asset.ResponsibleOfficeCode ||
                      (customer != null &&
                       (history.TenantCode != customer.TenantCode ||
                        history.ResponsibleOfficeCode != customer.ResponsibleOfficeCode)) ||
                      (profile != null &&
                       (history.TenantCode != profile.TenantCode ||
                        history.ResponsibleOfficeCode != profile.ResponsibleOfficeCode))
                orderby history.ResponsibleOfficeCode, history.ManagementNumber, history.LinkedAtUtc, history.Id
                select new { History = history, Asset = asset, Customer = customer, Profile = profile })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => CreateDetailRow(
                entityType: "렌탈 임대이력",
                entityIdText: FormatGuid(row.History.Id),
                primaryText: BuildRentalAssignmentHistoryDisplay(row.History),
                secondaryText: CombineParts(row.History.CustomerName, row.History.ItemName, row.History.ManagementNumber),
                referenceText: BuildRentalAssignmentScopeMismatchReferenceText(row.History, row.Asset, row.Customer, row.Profile),
                scopeText: FormatScope(row.History.TenantCode, row.History.OfficeCode, row.History.ResponsibleOfficeCode),
                detailText: CombineParts(
                    BuildRentalAssignmentHistoryDetailText(row.History),
                    "조치: 현재 설치이력과 같은 업체/담당지점의 자산·거래처·청구 프로필로 다시 연결하세요")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalAssignmentHistoricalStaleReferenceDetailsAsync(CancellationToken cancellationToken)
    {
        var histories = await _officeScopeService.ApplyRentalAssignmentHistoryScope(
                _dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking())
            .Where(history => !history.IsDeleted && !history.IsCurrent && history.AssetId != Guid.Empty)
            .Where(history => _dbContext.RentalAssets.IgnoreQueryFilters().Any(asset => !asset.IsDeleted && asset.Id == history.AssetId))
            .Where(history =>
                (history.CustomerId.HasValue && history.CustomerId.Value != Guid.Empty &&
                 !_dbContext.Customers.IgnoreQueryFilters().Any(customer => !customer.IsDeleted && customer.Id == history.CustomerId.Value)) ||
                (history.BillingProfileId.HasValue && history.BillingProfileId.Value != Guid.Empty &&
                 !_dbContext.RentalBillingProfiles.IgnoreQueryFilters().Any(profile => !profile.IsDeleted && profile.Id == history.BillingProfileId.Value)))
            .OrderBy(history => history.ResponsibleOfficeCode)
            .ThenBy(history => history.ManagementNumber)
            .ThenBy(history => history.LinkedAtUtc)
            .ThenBy(history => history.Id)
            .ToListAsync(cancellationToken);

        var activeAssetIds = await LoadActiveRentalAssetIdsAsync(histories.Select(history => history.AssetId), cancellationToken);
        var activeCustomerIds = await LoadActiveCustomerIdsAsync(histories.Select(history => history.CustomerId), cancellationToken);
        var activeProfileIds = await LoadActiveRentalBillingProfileIdsAsync(histories.Select(history => history.BillingProfileId), cancellationToken);

        return histories
            .Select(history => CreateDetailRow(
                entityType: "과거 렌탈 임대이력",
                entityIdText: FormatGuid(history.Id),
                primaryText: BuildRentalAssignmentHistoryDisplay(history),
                secondaryText: CombineParts(history.CustomerName, history.ItemName, history.ManagementNumber),
                referenceText: BuildRentalAssignmentMissingReferenceText(history, activeAssetIds, activeCustomerIds, activeProfileIds),
                scopeText: FormatScope(history.TenantCode, history.OfficeCode, history.ResponsibleOfficeCode),
                detailText: CombineParts("과거 스냅샷 표시값 유지", BuildRentalAssignmentHistoryDetailText(history))))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalAssetMultipleCurrentAssignmentDetailsAsync(CancellationToken cancellationToken)
    {
        var scopedCurrentHistories = _officeScopeService.ApplyRentalAssignmentHistoryScope(
                _dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking())
            .Where(history => !history.IsDeleted && history.IsCurrent && history.AssetId != Guid.Empty);

        var duplicateRows = await scopedCurrentHistories
            .GroupBy(history => history.AssetId)
            .Where(group => group.Count() > 1)
            .Select(group => new { AssetId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        if (duplicateRows.Count == 0)
            return [];

        var duplicateAssetIds = duplicateRows.Select(row => row.AssetId).ToList();
        var duplicateCounts = duplicateRows.ToDictionary(row => row.AssetId, row => row.Count);
        var assets = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => duplicateAssetIds.Contains(asset.Id))
            .ToDictionaryAsync(asset => asset.Id, cancellationToken);
        var histories = await scopedCurrentHistories
            .Where(history => duplicateAssetIds.Contains(history.AssetId))
            .OrderBy(history => history.AssetId)
            .ThenByDescending(history => history.LinkedAtUtc)
            .ThenBy(history => history.Id)
            .ToListAsync(cancellationToken);

        return histories
            .Select(history =>
            {
                assets.TryGetValue(history.AssetId, out var asset);
                var duplicateCount = duplicateCounts.TryGetValue(history.AssetId, out var count) ? count : 0;
                return CreateDetailRow(
                    entityType: "렌탈 임대이력",
                    entityIdText: FormatGuid(history.Id),
                    primaryText: BuildRentalAssignmentHistoryDisplay(history),
                    secondaryText: CombineParts(history.CustomerName, history.ItemName, history.ManagementNumber),
                    referenceText: $"현재 이력 {duplicateCount:N0}건 / 자산 {FormatGuid(history.AssetId)}",
                    scopeText: FormatScope(history.TenantCode, history.OfficeCode, history.ResponsibleOfficeCode),
                    detailText: CombineParts(
                        asset is null ? "자산 행 확인 필요" : $"자산 {FirstNonEmpty(asset.ManagementNumber, asset.AssetKey, FormatGuid(asset.Id))}",
                        BuildRentalAssignmentHistoryDetailText(history)));
            })
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadOrphanTransactionInvoiceDetailsAsync(CancellationToken cancellationToken)
    {
        var transactions = await (
                from transaction in _officeScopeService.ApplyTransactionScope(_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
                    .Where(current => !current.IsDeleted && current.LinkedInvoiceId.HasValue)
                join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    on transaction.LinkedInvoiceId!.Value equals invoice.Id into invoiceGroup
                from invoice in invoiceGroup.DefaultIfEmpty()
                where invoice == null
                orderby transaction.TransactionDate, transaction.Id
                select transaction)
            .ToListAsync(cancellationToken);

        return transactions
            .Select(transaction => CreateDetailRow(
                entityType: "거래내역",
                entityIdText: FormatGuid(transaction.Id),
                primaryText: CombineParts(transaction.TransactionKind, FormatDate(transaction.TransactionDate)),
                secondaryText: FirstNonEmpty(transaction.LinkedInvoiceNumber, transaction.Note, transaction.Memo),
                referenceText: transaction.LinkedInvoiceId.HasValue ? $"누락 전표 {FormatGuid(transaction.LinkedInvoiceId.Value)}" : "전표 미연결",
                scopeText: FormatScope(transaction.TenantCode, transaction.OfficeCode, transaction.ResponsibleOfficeCode),
                detailText: CombineParts(
                    $"정산 {FormatMoney(transaction.SettlementAmount)}",
                    string.IsNullOrWhiteSpace(transaction.LinkedInvoiceNumber) ? null : $"연결전표번호 {transaction.LinkedInvoiceNumber}",
                    string.IsNullOrWhiteSpace(transaction.Note) ? null : $"비고 {transaction.Note}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadOrphanPaymentInvoiceDetailsAsync(CancellationToken cancellationToken)
    {
        var payments = _officeScopeService.HasGlobalDataScope
            ? await (
                    from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                        on payment.InvoiceId equals invoice.Id into invoiceGroup
                    from invoice in invoiceGroup.DefaultIfEmpty()
                    where invoice == null
                    orderby payment.PaymentDate, payment.Id
                    select payment)
                .ToListAsync(cancellationToken)
            : await (
                    from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    join invoice in _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
                            .Where(current => current.IsDeleted)
                        on payment.InvoiceId equals invoice.Id
                    orderby payment.PaymentDate, payment.Id
                    select payment)
                .ToListAsync(cancellationToken);

        return payments
            .Select(payment => CreateDetailRow(
                entityType: "결제",
                entityIdText: FormatGuid(payment.Id),
                primaryText: $"결제 {FormatDate(payment.PaymentDate)}",
                secondaryText: $"금액 {FormatMoney(payment.Amount)}",
                referenceText: $"누락 전표 {FormatGuid(payment.InvoiceId)}",
                scopeText: "공통",
                detailText: CombineParts(
                    string.IsNullOrWhiteSpace(payment.Note) ? null : $"메모 {payment.Note}",
                    $"결제ID {FormatGuid(payment.Id)}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadDeletedPaymentMissingInvoiceRowDetailsAsync(CancellationToken cancellationToken)
    {
        if (!_officeScopeService.HasGlobalDataScope)
            return [];

        var payments = await (
                from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking().Where(current => current.IsDeleted)
                join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
                    on payment.InvoiceId equals invoice.Id into invoiceGroup
                from invoice in invoiceGroup.DefaultIfEmpty()
                where invoice == null
                orderby payment.PaymentDate, payment.Id
                select payment)
            .ToListAsync(cancellationToken);

        return payments
            .Select(payment => CreateDetailRow(
                entityType: "삭제 결제",
                entityIdText: FormatGuid(payment.Id),
                primaryText: $"삭제 결제 {FormatDate(payment.PaymentDate)}",
                secondaryText: $"금액 {FormatMoney(payment.Amount)}",
                referenceText: $"누락 전표 행 {FormatGuid(payment.InvoiceId)}",
                scopeText: "공통",
                detailText: CombineParts(
                    "삭제상태 삭제",
                    string.IsNullOrWhiteSpace(payment.Note) ? null : $"메모 {payment.Note}",
                    $"결제ID {FormatGuid(payment.Id)}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadInvoiceLinkedTransactionPaymentMismatchDetailsAsync(CancellationToken cancellationToken)
    {
        var rows = await LoadInvoiceLinkedTransactionPaymentMismatchRowsAsync(cancellationToken);
        return rows
            .Select(row => CreateDetailRow(
                entityType: "전표 연결 거래내역",
                entityIdText: FormatGuid(row.TransactionId),
                primaryText: CombineParts(row.TransactionKind, FormatDate(row.TransactionDate), FirstNonEmpty(row.InvoiceNumber, row.LocalTempNumber, FormatGuid(row.InvoiceId))),
                secondaryText: $"거래 정산 {FormatMoney(row.TransactionSettlementAmount)} / 수금·지급 {FormatOptionalMoney(row.PaymentAmount)}",
                referenceText: BuildInvoiceLinkedTransactionPaymentMismatchReason(row),
                scopeText: FormatScope(row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode),
                detailText: CombineParts(
                    $"전표ID {FormatGuid(row.InvoiceId)}",
                    $"거래내역ID {FormatGuid(row.TransactionId)}",
                    row.PaymentId.HasValue ? $"결제ID {FormatGuid(row.PaymentId.Value)}" : "결제ID 없음",
                    row.PaymentInvoiceId.HasValue ? $"결제 전표ID {FormatGuid(row.PaymentInvoiceId.Value)}" : "결제 전표ID 없음",
                    row.PaymentIsDeleted.HasValue ? $"결제 삭제상태 {(row.PaymentIsDeleted.Value ? "삭제" : "활성")}" : "결제 행 없음",
                    string.IsNullOrWhiteSpace(row.LinkedInvoiceNumber) ? null : $"거래 전표번호 {row.LinkedInvoiceNumber}")))
            .ToList();
    }

    private async Task<List<InvoiceLinkedTransactionPaymentMismatchRow>> LoadInvoiceLinkedTransactionPaymentMismatchRowsAsync(CancellationToken cancellationToken)
    {
        var rows = await (
                from transaction in _officeScopeService.ApplyTransactionScope(_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
                    .Where(transaction =>
                        !transaction.IsDeleted &&
                        transaction.LinkedInvoiceId.HasValue &&
                        transaction.LinkedInvoiceId.Value != Guid.Empty &&
                        transaction.SettlementAmount > 0m)
                join invoice in _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
                        .Where(invoice => !invoice.IsDeleted)
                    on transaction.LinkedInvoiceId!.Value equals invoice.Id
                join payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking()
                    on transaction.Id equals payment.Id into paymentGroup
                from payment in paymentGroup.DefaultIfEmpty()
                where payment == null ||
                      payment.IsDeleted ||
                      payment.InvoiceId != invoice.Id ||
                      payment.Amount - transaction.SettlementAmount >= 1m ||
                      transaction.SettlementAmount - payment.Amount >= 1m
                orderby transaction.TransactionDate, transaction.Id
                select new InvoiceLinkedTransactionPaymentMismatchRow(
                    transaction.Id,
                    invoice.Id,
                    payment == null ? null : (Guid?)payment.Id,
                    transaction.TenantCode,
                    transaction.OfficeCode,
                    transaction.ResponsibleOfficeCode,
                    transaction.TransactionDate,
                    transaction.TransactionKind,
                    transaction.LinkedInvoiceNumber,
                    transaction.SettlementAmount,
                    invoice.InvoiceNumber,
                    invoice.LocalTempNumber,
                    invoice.InvoiceDate,
                    invoice.TotalAmount,
                    payment == null ? null : (Guid?)payment.InvoiceId,
                    payment == null ? null : (decimal?)payment.Amount,
                    payment == null ? null : (bool?)payment.IsDeleted))
            .ToListAsync(cancellationToken);

        return rows;
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalInvoiceDeletedPaymentDetachedTransactionDetailsAsync(CancellationToken cancellationToken)
    {
        var rows = await (
                from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking().Where(payment => payment.IsDeleted)
                join invoice in _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
                        .Where(invoice =>
                            !invoice.IsDeleted &&
                            invoice.LinkedRentalBillingProfileId.HasValue &&
                            invoice.LinkedRentalBillingProfileId.Value != Guid.Empty)
                    on payment.InvoiceId equals invoice.Id
                join transaction in _officeScopeService.ApplyTransactionScope(_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
                        .Where(transaction => !transaction.IsDeleted)
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
                    invoice.OfficeCode,
                    invoice.ResponsibleOfficeCode,
                    invoice.LinkedRentalBillingProfileId,
                    invoice.LinkedRentalBillingRunId,
                    TransactionId = transaction.Id,
                    transaction.TransactionDate,
                    transaction.TransactionKind,
                    transaction.LinkedInvoiceId,
                    transaction.LinkedInvoiceNumber,
                    transaction.SettlementAmount,
                    TransactionRentalProfileId = transaction.LinkedRentalBillingProfileId,
                    TransactionRentalRunId = transaction.LinkedRentalBillingRunId
                })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => CreateDetailRow(
                entityType: "렌탈 전표/수금",
                entityIdText: FormatGuid(row.PaymentId),
                primaryText: CombineParts("전표", FirstNonEmpty(row.InvoiceNumber, row.LocalTempNumber, FormatGuid(row.InvoiceId)), FormatDate(row.InvoiceDate)),
                secondaryText: CombineParts($"삭제 수금 {FormatDate(row.PaymentDate)}", $"금액 {FormatMoney(row.PaymentAmount)}"),
                referenceText: row.LinkedInvoiceId.HasValue && row.LinkedInvoiceId.Value != Guid.Empty
                    ? $"거래내역 전표링크 {FormatGuid(row.LinkedInvoiceId.Value)}"
                    : "거래내역 전표링크 없음",
                scopeText: FormatScope(row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode),
                detailText: CombineParts(
                    $"전표ID {FormatGuid(row.InvoiceId)}",
                    $"거래내역ID {FormatGuid(row.TransactionId)}",
                    $"거래일 {FormatDate(row.TransactionDate)}",
                    string.IsNullOrWhiteSpace(row.TransactionKind) ? null : $"거래구분 {row.TransactionKind}",
                    $"거래 정산 {FormatMoney(row.SettlementAmount)}",
                    row.LinkedRentalBillingProfileId.HasValue ? $"전표 렌탈프로필 {FormatGuid(row.LinkedRentalBillingProfileId.Value)}" : "전표 렌탈프로필 없음",
                    row.TransactionRentalProfileId.HasValue ? $"거래 렌탈프로필 {FormatGuid(row.TransactionRentalProfileId.Value)}" : "거래 렌탈프로필 없음",
                    string.IsNullOrWhiteSpace(row.Note) ? null : $"메모 {row.Note}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalBillingRunSettlementMismatchDetailsAsync(CancellationToken cancellationToken)
    {
        var rows = await LoadRentalBillingRunSettlementMismatchRowsAsync(cancellationToken);
        return rows
            .Select(row => CreateDetailRow(
                entityType: "렌탈 청구 run",
                entityIdText: FormatGuid(row.ProfileId),
                primaryText: CombineParts(row.ProfileDisplayName, row.RunKey, FormatDate(row.ScheduledDate)),
                secondaryText: $"저장 정산 {FormatMoney(row.StoredSettledAmount)} / 실제 {FormatMoney(row.ActualSettledAmount)}",
                referenceText: $"RunId {FormatGuid(row.RunId)}",
                scopeText: FormatScope(row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode),
                detailText: CombineParts(
                    $"청구액 {FormatMoney(row.BilledAmount)}",
                    $"차이 {FormatMoney(row.StoredSettledAmount - row.ActualSettledAmount)}",
                    $"거래내역 합계 {FormatMoney(row.TransactionSettledAmount)}",
                    $"직접 결제 합계 {FormatMoney(row.DirectPaymentSettledAmount)}",
                    string.IsNullOrWhiteSpace(row.Status) ? null : $"상태 {row.Status}",
                    string.IsNullOrWhiteSpace(row.SettlementStatus) ? null : $"정산상태 {row.SettlementStatus}")))
            .ToList();
    }

    private async Task<List<RentalBillingRunSettlementMismatchRow>> LoadRentalBillingRunSettlementMismatchRowsAsync(CancellationToken cancellationToken)
    {
        var profiles = await _officeScopeService.ApplyRentalBillingProfileScope(
                _dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
            .Where(profile => !profile.IsDeleted)
            .Select(profile => new
            {
                profile.Id,
                profile.TenantCode,
                profile.OfficeCode,
                profile.ResponsibleOfficeCode,
                profile.CustomerName,
                profile.ProfileKey,
                profile.BillingRunsJson
            })
            .ToListAsync(cancellationToken);
        var profileIds = profiles.Select(profile => profile.Id).Distinct().ToList();
        if (profileIds.Count == 0)
            return [];

        var transactions = await _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking()
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
            .ToListAsync(cancellationToken);

        var transactionKeys = transactions
            .Select(transaction => (PaymentId: transaction.Id, transaction.ProfileId))
            .ToHashSet();

        var directPayments = await (
                from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking().Where(payment => !payment.IsDeleted)
                join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().Where(invoice =>
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
            .ToListAsync(cancellationToken);

        var transactionSettledAmounts = transactions
            .GroupBy(transaction => (transaction.ProfileId, RunId: NormalizeRunId(transaction.RunId)))
            .ToDictionary(group => group.Key, group => group.Sum(transaction => transaction.Amount));
        var directPaymentSettledAmounts = directPayments
            .Where(payment => !transactionKeys.Contains((payment.Id, payment.ProfileId)))
            .GroupBy(payment => (payment.ProfileId, RunId: NormalizeRunId(payment.RunId)))
            .ToDictionary(group => group.Key, group => group.Sum(payment => payment.Amount));

        var rows = new List<RentalBillingRunSettlementMismatchRow>();
        foreach (var profile in profiles)
        {
            foreach (var run in ParseRentalBillingRuns(profile.BillingRunsJson))
            {
                if (run.RunId == Guid.Empty)
                    continue;

                var runId = NormalizeRunId(run.RunId);
                var key = (profile.Id, RunId: runId);
                transactionSettledAmounts.TryGetValue(key, out var transactionAmount);
                directPaymentSettledAmounts.TryGetValue(key, out var directPaymentAmount);
                var actualAmount = transactionAmount + directPaymentAmount;
                if (!AmountDiffers(run.SettledAmount, actualAmount))
                    continue;

                rows.Add(new RentalBillingRunSettlementMismatchRow(
                    profile.Id,
                    profile.TenantCode,
                    profile.OfficeCode,
                    profile.ResponsibleOfficeCode,
                    FirstNonEmpty(profile.CustomerName, profile.ProfileKey, FormatGuid(profile.Id)),
                    runId,
                    run.RunKey,
                    run.ScheduledDate,
                    run.BilledAmount,
                    run.SettledAmount,
                    actualAmount,
                    transactionAmount,
                    directPaymentAmount,
                    run.Status,
                    run.SettlementStatus));
            }
        }

        return rows
            .OrderBy(row => row.ProfileDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ScheduledDate)
            .ThenBy(row => row.RunKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalBillingRunMissingRunIdDetailsAsync(CancellationToken cancellationToken)
    {
        var rows = await LoadRentalBillingRunMissingRunIdRowsAsync(cancellationToken);
        return rows
            .Select(row => CreateDetailRow(
                entityType: "렌탈 청구 run",
                entityIdText: FormatGuid(row.ProfileId),
                primaryText: CombineParts(row.ProfileDisplayName, row.RunKey, FormatDate(row.ScheduledDate)),
                secondaryText: $"청구액 {FormatMoney(row.BilledAmount)} / 정산 {FormatMoney(row.SettledAmount)}",
                referenceText: "RunId 없음",
                scopeText: FormatScope(row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode),
                detailText: CombineParts(
                    $"프로필ID {FormatGuid(row.ProfileId)}",
                    string.IsNullOrWhiteSpace(row.RunKey) ? "RunKey 없음" : $"RunKey {row.RunKey}",
                    $"기간 {FormatDate(row.PeriodStartDate)}~{FormatDate(row.PeriodEndDate)}",
                    string.IsNullOrWhiteSpace(row.Status) ? null : $"상태 {row.Status}",
                    string.IsNullOrWhiteSpace(row.SettlementStatus) ? null : $"정산상태 {row.SettlementStatus}",
                    "전표/수금과 안정적으로 대조할 수 없는 과거 청구 JSON입니다. 근거 확인 후 수동 정리 여부를 검토하세요.")))
            .ToList();
    }

    private async Task<List<RentalBillingRunMissingRunIdRow>> LoadRentalBillingRunMissingRunIdRowsAsync(CancellationToken cancellationToken)
    {
        var profiles = await _officeScopeService.ApplyRentalBillingProfileScope(
                _dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
            .Where(profile => !profile.IsDeleted)
            .Select(profile => new
            {
                profile.Id,
                profile.TenantCode,
                profile.OfficeCode,
                profile.ResponsibleOfficeCode,
                profile.CustomerName,
                profile.ProfileKey,
                profile.BillingRunsJson
            })
            .ToListAsync(cancellationToken);

        var rows = new List<RentalBillingRunMissingRunIdRow>();
        foreach (var profile in profiles)
        {
            foreach (var run in ParseRentalBillingRuns(profile.BillingRunsJson))
            {
                if (run.RunId != Guid.Empty)
                    continue;

                rows.Add(new RentalBillingRunMissingRunIdRow(
                    profile.Id,
                    profile.TenantCode,
                    profile.OfficeCode,
                    profile.ResponsibleOfficeCode,
                    FirstNonEmpty(profile.CustomerName, profile.ProfileKey, FormatGuid(profile.Id)),
                    run.RunKey,
                    run.ScheduledDate,
                    run.PeriodStartDate,
                    run.PeriodEndDate,
                    run.BilledAmount,
                    run.SettledAmount,
                    run.Status,
                    run.SettlementStatus));
            }
        }

        return rows
            .OrderBy(row => row.ProfileDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ScheduledDate)
            .ThenBy(row => row.RunKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalBillingProfileSummaryMismatchDetailsAsync(CancellationToken cancellationToken)
    {
        var rows = await LoadRentalBillingProfileSummaryMismatchRowsAsync(cancellationToken);
        return rows
            .Select(row => CreateDetailRow(
                entityType: "렌탈 청구 프로필",
                entityIdText: FormatGuid(row.ProfileId),
                primaryText: CombineParts(row.ProfileDisplayName, row.RunKey, FormatDate(row.ScheduledDate)),
                secondaryText: $"프로필 저장 정산 {FormatMoney(row.StoredProfileSettledAmount)} / 기대 {FormatMoney(row.ExpectedSettledAmount)}",
                referenceText: $"RunId {FormatGuid(row.RunId)}",
                scopeText: FormatScope(row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode),
                detailText: CombineParts(
                    $"프로필 저장 미수 {FormatMoney(row.StoredProfileOutstandingAmount)}",
                    $"기대 미수 {FormatMoney(row.ExpectedOutstandingAmount)}",
                    $"대표 청구액 {FormatMoney(row.ExpectedBilledAmount)}",
                    $"거래내역 합계 {FormatMoney(row.TransactionSettledAmount)}",
                    $"직접 결제 합계 {FormatMoney(row.DirectPaymentSettledAmount)}",
                    string.IsNullOrWhiteSpace(row.ProfileBillingStatus) ? null : $"프로필 청구상태 {row.ProfileBillingStatus}",
                    string.IsNullOrWhiteSpace(row.ProfileSettlementStatus) ? null : $"프로필 정산상태 {row.ProfileSettlementStatus}",
                    string.IsNullOrWhiteSpace(row.ProfileCompletionStatus) ? null : $"프로필 완료상태 {row.ProfileCompletionStatus}",
                    string.IsNullOrWhiteSpace(row.RunStatus) ? null : $"run 상태 {row.RunStatus}",
                    string.IsNullOrWhiteSpace(row.RunSettlementStatus) ? null : $"run 정산상태 {row.RunSettlementStatus}")))
            .ToList();
    }

    private async Task<List<RentalBillingProfileSummaryMismatchRow>> LoadRentalBillingProfileSummaryMismatchRowsAsync(CancellationToken cancellationToken)
    {
        var profiles = await _officeScopeService.ApplyRentalBillingProfileScope(
                _dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
            .Where(profile => !profile.IsDeleted)
            .Select(profile => new
            {
                profile.Id,
                profile.TenantCode,
                profile.OfficeCode,
                profile.ResponsibleOfficeCode,
                profile.CustomerName,
                profile.ProfileKey,
                profile.MonthlyAmount,
                profile.BillingStatus,
                profile.SettlementStatus,
                profile.CompletionStatus,
                profile.SettledAmount,
                profile.OutstandingAmount,
                profile.BillingRunsJson
            })
            .ToListAsync(cancellationToken);
        var profileIds = profiles.Select(profile => profile.Id).Distinct().ToList();
        if (profileIds.Count == 0)
            return [];

        var transactions = await _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking()
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
            .ToListAsync(cancellationToken);

        var transactionKeys = transactions
            .Select(transaction => (PaymentId: transaction.Id, transaction.ProfileId))
            .ToHashSet();

        var directPayments = await (
                from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking().Where(payment => !payment.IsDeleted)
                join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().Where(invoice =>
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
            .ToListAsync(cancellationToken);

        var invoices = await _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
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
            .ToListAsync(cancellationToken);

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

        var rows = new List<RentalBillingProfileSummaryMismatchRow>();
        foreach (var profile in profiles)
        {
            var activeRuns = ParseRentalBillingRuns(profile.BillingRunsJson)
                .Where(run => run.RunId != Guid.Empty)
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

            rows.Add(new RentalBillingProfileSummaryMismatchRow(
                profile.Id,
                profile.TenantCode,
                profile.OfficeCode,
                profile.ResponsibleOfficeCode,
                FirstNonEmpty(profile.CustomerName, profile.ProfileKey, FormatGuid(profile.Id)),
                runId,
                representativeRun.RunKey,
                representativeRun.ScheduledDate,
                profile.SettledAmount,
                profile.OutstandingAmount,
                expectedSettledAmount,
                expectedOutstandingAmount,
                expectedBilledAmount,
                transactionAmount,
                directPaymentAmount,
                profile.BillingStatus,
                profile.SettlementStatus,
                profile.CompletionStatus,
                representativeRun.Status,
                representativeRun.SettlementStatus));
        }

        return rows
            .OrderBy(row => row.ProfileDisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ScheduledDate)
            .ThenBy(row => row.RunKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadOrphanTransactionAttachmentDetailsAsync(CancellationToken cancellationToken)
    {
        var attachments = _officeScopeService.HasGlobalDataScope
            ? await (
                    from attachment in _dbContext.TransactionAttachments.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    join transaction in _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                        on attachment.TransactionId equals transaction.Id into transactionGroup
                    from transaction in transactionGroup.DefaultIfEmpty()
                    where transaction == null
                    orderby attachment.UploadedAtUtc, attachment.Id
                    select attachment)
                .ToListAsync(cancellationToken)
            : await (
                    from attachment in _dbContext.TransactionAttachments.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    join transaction in _officeScopeService.ApplyTransactionScope(_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
                            .Where(current => current.IsDeleted)
                        on attachment.TransactionId equals transaction.Id
                    orderby attachment.UploadedAtUtc, attachment.Id
                    select attachment)
                .ToListAsync(cancellationToken);

        return attachments
            .Select(attachment => CreateDetailRow(
                entityType: "거래첨부",
                entityIdText: FormatGuid(attachment.Id),
                primaryText: FirstNonEmpty(attachment.FileName, FormatGuid(attachment.Id)),
                secondaryText: CombineParts(attachment.AttachmentType, attachment.Description),
                referenceText: $"누락 거래 {FormatGuid(attachment.TransactionId)}",
                scopeText: $"업로드 {FormatUtcDateTime(attachment.UploadedAtUtc)}",
                detailText: CombineParts(
                    string.IsNullOrWhiteSpace(attachment.VerificationStatus) ? null : $"검증상태 {attachment.VerificationStatus}",
                    attachment.SortOrder > 0 ? $"정렬 {attachment.SortOrder:N0}" : null,
                    string.IsNullOrWhiteSpace(attachment.StoragePath) ? null : $"경로 {attachment.StoragePath}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadDeletedTransactionAttachmentMissingTransactionRowDetailsAsync(CancellationToken cancellationToken)
    {
        if (!_officeScopeService.HasGlobalDataScope)
            return [];

        var attachments = await (
                from attachment in _dbContext.TransactionAttachments.IgnoreQueryFilters().AsNoTracking().Where(current => current.IsDeleted)
                join transaction in _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking()
                    on attachment.TransactionId equals transaction.Id into transactionGroup
                from transaction in transactionGroup.DefaultIfEmpty()
                where transaction == null
                orderby attachment.UploadedAtUtc, attachment.Id
                select attachment)
            .ToListAsync(cancellationToken);

        return attachments
            .Select(attachment => CreateDetailRow(
                entityType: "삭제 거래첨부",
                entityIdText: FormatGuid(attachment.Id),
                primaryText: FirstNonEmpty(attachment.FileName, FormatGuid(attachment.Id)),
                secondaryText: CombineParts(attachment.AttachmentType, attachment.Description),
                referenceText: $"누락 거래 행 {FormatGuid(attachment.TransactionId)}",
                scopeText: $"업로드 {FormatUtcDateTime(attachment.UploadedAtUtc)}",
                detailText: CombineParts(
                    "삭제상태 삭제",
                    string.IsNullOrWhiteSpace(attachment.VerificationStatus) ? null : $"검증상태 {attachment.VerificationStatus}",
                    attachment.FileSize > 0 ? $"크기 {attachment.FileSize:N0} bytes" : null,
                    string.IsNullOrWhiteSpace(attachment.StoragePath) ? null : $"경로 {attachment.StoragePath}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadOrphanPaymentAttachmentDetailsAsync(CancellationToken cancellationToken)
    {
        var attachments = _officeScopeService.HasGlobalDataScope
            ? await (
                    from attachment in _dbContext.PaymentAttachments.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    join payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                        on attachment.PaymentId equals payment.Id into paymentGroup
                    from payment in paymentGroup.DefaultIfEmpty()
                    where payment == null
                    orderby attachment.UploadedAtUtc, attachment.Id
                    select attachment)
                .ToListAsync(cancellationToken)
            : await (
                    from attachment in _dbContext.PaymentAttachments.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    join payment in _officeScopeService.ApplyPaymentScope(_dbContext.Payments.IgnoreQueryFilters().AsNoTracking())
                            .Where(current => current.IsDeleted)
                        on attachment.PaymentId equals payment.Id
                    orderby attachment.UploadedAtUtc, attachment.Id
                    select attachment)
                .ToListAsync(cancellationToken);

        return attachments
            .Select(attachment => CreateDetailRow(
                entityType: "결제첨부",
                entityIdText: FormatGuid(attachment.Id),
                primaryText: FirstNonEmpty(attachment.FileName, FormatGuid(attachment.Id)),
                secondaryText: CombineParts(attachment.AttachmentType, attachment.Description),
                referenceText: $"누락 결제 {FormatGuid(attachment.PaymentId)}",
                scopeText: $"업로드 {FormatUtcDateTime(attachment.UploadedAtUtc)}",
                detailText: CombineParts(
                    attachment.FileSize > 0 ? $"크기 {attachment.FileSize:N0} bytes" : null,
                    string.IsNullOrWhiteSpace(attachment.StoragePath) ? null : $"경로 {attachment.StoragePath}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadDeletedPaymentAttachmentMissingPaymentRowDetailsAsync(CancellationToken cancellationToken)
    {
        if (!_officeScopeService.HasGlobalDataScope)
            return [];

        var attachments = await (
                from attachment in _dbContext.PaymentAttachments.IgnoreQueryFilters().AsNoTracking().Where(current => current.IsDeleted)
                join payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking()
                    on attachment.PaymentId equals payment.Id into paymentGroup
                from payment in paymentGroup.DefaultIfEmpty()
                where payment == null
                orderby attachment.UploadedAtUtc, attachment.Id
                select attachment)
            .ToListAsync(cancellationToken);

        return attachments
            .Select(attachment => CreateDetailRow(
                entityType: "삭제 결제첨부",
                entityIdText: FormatGuid(attachment.Id),
                primaryText: FirstNonEmpty(attachment.FileName, FormatGuid(attachment.Id)),
                secondaryText: CombineParts(attachment.AttachmentType, attachment.Description),
                referenceText: $"누락 결제 행 {FormatGuid(attachment.PaymentId)}",
                scopeText: $"업로드 {FormatUtcDateTime(attachment.UploadedAtUtc)}",
                detailText: CombineParts(
                    "삭제상태 삭제",
                    attachment.FileSize > 0 ? $"크기 {attachment.FileSize:N0} bytes" : null,
                    string.IsNullOrWhiteSpace(attachment.StoragePath) ? null : $"경로 {attachment.StoragePath}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadUnsupportedAttachmentFileTypeDetailsAsync(CancellationToken cancellationToken)
    {
        var rows = new List<AttachmentFileTypeIssueRow>();

        var transactionAttachmentRows = await _officeScopeService
            .ApplyTransactionAttachmentScope(_dbContext.TransactionAttachments.IgnoreQueryFilters().AsNoTracking())
            .Where(attachment => !attachment.IsDeleted && attachment.Transaction != null && !attachment.Transaction.IsDeleted)
            .Select(attachment => new
            {
                attachment.Id,
                attachment.FileName,
                attachment.MimeType,
                attachment.AttachmentType,
                attachment.Description,
                attachment.FileSize,
                attachment.UploadedAtUtc,
                attachment.TransactionId,
                TransactionDate = attachment.Transaction == null ? (DateOnly?)null : attachment.Transaction.TransactionDate,
                TransactionKind = attachment.Transaction == null ? string.Empty : attachment.Transaction.TransactionKind,
                LinkedInvoiceNumber = attachment.Transaction == null ? string.Empty : attachment.Transaction.LinkedInvoiceNumber,
                TenantCode = attachment.Transaction == null ? string.Empty : attachment.Transaction.TenantCode,
                OfficeCode = attachment.Transaction == null ? string.Empty : attachment.Transaction.OfficeCode,
                ResponsibleOfficeCode = attachment.Transaction == null ? string.Empty : attachment.Transaction.ResponsibleOfficeCode
            })
            .ToListAsync(cancellationToken);

        rows.AddRange(transactionAttachmentRows.Select(attachment => new AttachmentFileTypeIssueRow(
            EntityType: "거래첨부",
            EntityId: attachment.Id,
            FileName: attachment.FileName,
            MimeType: attachment.MimeType,
            SecondaryText: CombineParts(attachment.AttachmentType, attachment.Description),
            ReferenceText: attachment.TransactionDate.HasValue
                ? CombineParts(
                    $"거래 {FormatDate(attachment.TransactionDate.Value)}",
                    attachment.TransactionKind,
                    string.IsNullOrWhiteSpace(attachment.LinkedInvoiceNumber) ? null : $"전표 {attachment.LinkedInvoiceNumber}")
                : $"거래 {FormatGuid(attachment.TransactionId)}",
            ScopeText: CombineParts(
                FormatScope(attachment.TenantCode, attachment.OfficeCode, attachment.ResponsibleOfficeCode),
                $"업로드 {FormatUtcDateTime(attachment.UploadedAtUtc)}"),
            FileSize: attachment.FileSize)));

        var scopedPayments = _officeScopeService
            .ApplyPaymentScope(_dbContext.Payments.IgnoreQueryFilters().AsNoTracking())
            .Where(payment => !payment.IsDeleted);
        var paymentAttachmentRows = await (
                from attachment in _dbContext.PaymentAttachments.IgnoreQueryFilters().AsNoTracking().Where(attachment => !attachment.IsDeleted)
                join payment in scopedPayments on attachment.PaymentId equals payment.Id
                select new
                {
                    attachment.Id,
                    attachment.FileName,
                    attachment.MimeType,
                    attachment.AttachmentType,
                    attachment.Description,
                    attachment.FileSize,
                    attachment.UploadedAtUtc,
                    payment.PaymentDate,
                    payment.Amount,
                    InvoiceNumber = payment.Invoice == null ? string.Empty : payment.Invoice.InvoiceNumber,
                    TenantCode = payment.Invoice == null ? string.Empty : payment.Invoice.TenantCode,
                    OfficeCode = payment.Invoice == null ? string.Empty : payment.Invoice.OfficeCode,
                    ResponsibleOfficeCode = payment.Invoice == null ? string.Empty : payment.Invoice.ResponsibleOfficeCode
                })
            .ToListAsync(cancellationToken);

        rows.AddRange(paymentAttachmentRows.Select(attachment => new AttachmentFileTypeIssueRow(
            EntityType: "결제첨부",
            EntityId: attachment.Id,
            FileName: attachment.FileName,
            MimeType: attachment.MimeType,
            SecondaryText: CombineParts(attachment.AttachmentType, attachment.Description),
            ReferenceText: CombineParts(
                $"결제 {FormatDate(attachment.PaymentDate)}",
                $"금액 {FormatMoney(attachment.Amount)}",
                string.IsNullOrWhiteSpace(attachment.InvoiceNumber) ? null : $"전표 {attachment.InvoiceNumber}"),
            ScopeText: CombineParts(
                FormatScope(attachment.TenantCode, attachment.OfficeCode, attachment.ResponsibleOfficeCode),
                $"업로드 {FormatUtcDateTime(attachment.UploadedAtUtc)}"),
            FileSize: attachment.FileSize)));

        return rows
            .Where(row => !IsAllowedEvidenceAttachmentFileType(row.FileName, row.MimeType))
            .OrderBy(row => row.EntityType, StringComparer.Ordinal)
            .ThenBy(row => row.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.EntityId)
            .Select(row => CreateDetailRow(
                entityType: row.EntityType,
                entityIdText: FormatGuid(row.EntityId),
                primaryText: FirstNonEmpty(row.FileName, FormatGuid(row.EntityId)),
                secondaryText: row.SecondaryText,
                referenceText: row.ReferenceText,
                scopeText: row.ScopeText,
                detailText: BuildUnsupportedAttachmentFileTypeDetail(row)))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadAttachmentContentSignatureMismatchDetailsAsync(CancellationToken cancellationToken)
    {
        var rows = new List<AttachmentContentSignatureIssueRow>();

        var transactionAttachmentRows = await _officeScopeService
            .ApplyTransactionAttachmentScope(_dbContext.TransactionAttachments.IgnoreQueryFilters().AsNoTracking())
            .Where(attachment => !attachment.IsDeleted && attachment.Transaction != null && !attachment.Transaction.IsDeleted)
            .Select(attachment => new
            {
                attachment.Id,
                attachment.FileName,
                attachment.MimeType,
                attachment.AttachmentType,
                attachment.Description,
                attachment.FileSize,
                attachment.FileHash,
                attachment.StoragePath,
                attachment.FileContent,
                attachment.UploadedAtUtc,
                attachment.TransactionId,
                TransactionDate = attachment.Transaction == null ? (DateOnly?)null : attachment.Transaction.TransactionDate,
                TransactionKind = attachment.Transaction == null ? string.Empty : attachment.Transaction.TransactionKind,
                LinkedInvoiceNumber = attachment.Transaction == null ? string.Empty : attachment.Transaction.LinkedInvoiceNumber,
                TenantCode = attachment.Transaction == null ? string.Empty : attachment.Transaction.TenantCode,
                OfficeCode = attachment.Transaction == null ? string.Empty : attachment.Transaction.OfficeCode,
                ResponsibleOfficeCode = attachment.Transaction == null ? string.Empty : attachment.Transaction.ResponsibleOfficeCode
            })
            .ToListAsync(cancellationToken);

        rows.AddRange(transactionAttachmentRows.Select(attachment => new AttachmentContentSignatureIssueRow(
            EntityType: "거래첨부",
            EntityId: attachment.Id,
            FileName: attachment.FileName,
            MimeType: attachment.MimeType,
            SecondaryText: CombineParts(attachment.AttachmentType, attachment.Description),
            ReferenceText: attachment.TransactionDate.HasValue
                ? CombineParts(
                    $"거래 {FormatDate(attachment.TransactionDate.Value)}",
                    attachment.TransactionKind,
                    string.IsNullOrWhiteSpace(attachment.LinkedInvoiceNumber) ? null : $"전표 {attachment.LinkedInvoiceNumber}")
                : $"거래 {FormatGuid(attachment.TransactionId)}",
            ScopeText: CombineParts(
                FormatScope(attachment.TenantCode, attachment.OfficeCode, attachment.ResponsibleOfficeCode),
                $"업로드 {FormatUtcDateTime(attachment.UploadedAtUtc)}"),
            FileSize: attachment.FileSize,
            FileHash: attachment.FileHash,
            StoragePath: attachment.StoragePath,
            FileContent: attachment.FileContent)));

        var scopedPayments = _officeScopeService
            .ApplyPaymentScope(_dbContext.Payments.IgnoreQueryFilters().AsNoTracking())
            .Where(payment => !payment.IsDeleted);
        var paymentAttachmentRows = await (
                from attachment in _dbContext.PaymentAttachments.IgnoreQueryFilters().AsNoTracking().Where(attachment => !attachment.IsDeleted)
                join payment in scopedPayments on attachment.PaymentId equals payment.Id
                select new
                {
                    attachment.Id,
                    attachment.FileName,
                    attachment.MimeType,
                    attachment.AttachmentType,
                    attachment.Description,
                    attachment.FileSize,
                    attachment.FileHash,
                    attachment.StoragePath,
                    attachment.FileContent,
                    attachment.UploadedAtUtc,
                    payment.PaymentDate,
                    payment.Amount,
                    InvoiceNumber = payment.Invoice == null ? string.Empty : payment.Invoice.InvoiceNumber,
                    TenantCode = payment.Invoice == null ? string.Empty : payment.Invoice.TenantCode,
                    OfficeCode = payment.Invoice == null ? string.Empty : payment.Invoice.OfficeCode,
                    ResponsibleOfficeCode = payment.Invoice == null ? string.Empty : payment.Invoice.ResponsibleOfficeCode
                })
            .ToListAsync(cancellationToken);

        rows.AddRange(paymentAttachmentRows.Select(attachment => new AttachmentContentSignatureIssueRow(
            EntityType: "결제첨부",
            EntityId: attachment.Id,
            FileName: attachment.FileName,
            MimeType: attachment.MimeType,
            SecondaryText: CombineParts(attachment.AttachmentType, attachment.Description),
            ReferenceText: CombineParts(
                $"결제 {FormatDate(attachment.PaymentDate)}",
                $"금액 {FormatMoney(attachment.Amount)}",
                string.IsNullOrWhiteSpace(attachment.InvoiceNumber) ? null : $"전표 {attachment.InvoiceNumber}"),
            ScopeText: CombineParts(
                FormatScope(attachment.TenantCode, attachment.OfficeCode, attachment.ResponsibleOfficeCode),
                $"업로드 {FormatUtcDateTime(attachment.UploadedAtUtc)}"),
            FileSize: attachment.FileSize,
            FileHash: attachment.FileHash,
            StoragePath: attachment.StoragePath,
            FileContent: attachment.FileContent)));

        return rows
            .Where(row => EvidenceAttachmentFilePolicy.IsAllowedFileType(row.FileName, row.MimeType))
            .Where(HasAttachmentContentSignatureMismatch)
            .OrderBy(row => row.EntityType, StringComparer.Ordinal)
            .ThenBy(row => row.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.EntityId)
            .Select(row => CreateDetailRow(
                entityType: row.EntityType,
                entityIdText: FormatGuid(row.EntityId),
                primaryText: FirstNonEmpty(row.FileName, FormatGuid(row.EntityId)),
                secondaryText: row.SecondaryText,
                referenceText: row.ReferenceText,
                scopeText: row.ScopeText,
                detailText: BuildAttachmentContentSignatureMismatchDetail(row)))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadCustomerContractMissingCustomerRowDetailsAsync(CancellationToken cancellationToken)
    {
        if (!_officeScopeService.HasGlobalDataScope)
            return [];

        var contracts = await (
                from contract in _dbContext.CustomerContracts.IgnoreQueryFilters().AsNoTracking()
                join customer in _dbContext.Customers.IgnoreQueryFilters().AsNoTracking()
                    on contract.CustomerId equals customer.Id into customerGroup
                from customer in customerGroup.DefaultIfEmpty()
                where customer == null
                orderby contract.UploadedAtUtc, contract.Id
                select contract)
            .ToListAsync(cancellationToken);

        return contracts
            .Select(contract => CreateDetailRow(
                entityType: "거래처계약서",
                entityIdText: FormatGuid(contract.Id),
                primaryText: FirstNonEmpty(contract.FileName, FormatGuid(contract.Id)),
                secondaryText: CombineParts(contract.ContractType, contract.Description),
                referenceText: $"누락 거래처 행 {FormatGuid(contract.CustomerId)}",
                scopeText: $"업로드 {FormatUtcDateTime(contract.UploadedAtUtc)}",
                detailText: CombineParts(
                    contract.IsDeleted ? "삭제상태 삭제" : "삭제상태 활성",
                    contract.IsPrimary ? "대표 계약" : null,
                    contract.FileSize > 0 ? $"크기 {contract.FileSize:N0} bytes" : null,
                    string.IsNullOrWhiteSpace(contract.StoragePath) ? null : $"경로 {contract.StoragePath}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadRentalBillingLogMissingProfileRowDetailsAsync(CancellationToken cancellationToken)
    {
        var logs = await (
                from log in _officeScopeService.ApplyRentalBillingLogScope(_dbContext.RentalBillingLogs.IgnoreQueryFilters().AsNoTracking())
                join profile in _dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                    on log.BillingProfileId equals profile.Id into profileGroup
                from profile in profileGroup.DefaultIfEmpty()
                where profile == null
                orderby log.BillingYearMonth, log.ScheduledDate, log.Id
                select log)
            .ToListAsync(cancellationToken);

        return logs
            .Select(log => CreateDetailRow(
                entityType: "렌탈 청구로그",
                entityIdText: FormatGuid(log.Id),
                primaryText: FirstNonEmpty(log.BillingYearMonth, FormatGuid(log.Id)),
                secondaryText: CombineParts(log.Status, $"청구 {FormatMoney(log.BilledAmount)}"),
                referenceText: $"누락 청구프로필 행 {FormatGuid(log.BillingProfileId)}",
                scopeText: FormatScope(log.TenantCode, log.OfficeCode, log.ResponsibleOfficeCode),
                detailText: CombineParts(
                    log.IsDeleted ? "삭제상태 삭제" : "삭제상태 활성",
                    $"예정일 {FormatDate(log.ScheduledDate)}",
                    log.ProcessedDate.HasValue ? $"처리일 {FormatDate(log.ProcessedDate.Value)}" : null,
                    string.IsNullOrWhiteSpace(log.Note) ? null : $"비고 {log.Note}")))
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadFileContentUnavailableDetailsAsync(CancellationToken cancellationToken)
    {
        var candidates = await LoadFileStorageIssueCandidatesAsync(cancellationToken);
        return candidates
            .Where(IsFileContentUnavailable)
            .OrderBy(candidate => candidate.EntityType, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.PrimaryText, StringComparer.OrdinalIgnoreCase)
            .Select(CreateFileStorageIssueDetailRow)
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadFileContentDbResidueDetailsAsync(CancellationToken cancellationToken)
    {
        var candidates = await LoadFileStorageIssueCandidatesAsync(cancellationToken);
        return candidates
            .Where(HasDbFileContentResidue)
            .OrderBy(candidate => candidate.EntityType, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.PrimaryText, StringComparer.OrdinalIgnoreCase)
            .Select(CreateFileStorageIssueDetailRow)
            .ToList();
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadStoredFileMissingDetailsAsync(CancellationToken cancellationToken)
    {
        var candidates = await LoadFileStorageIssueCandidatesAsync(cancellationToken);
        return CreateFileStorageIssueDetails(candidates, IsStoredFileMissing);
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadStoredFileSizeMismatchDetailsAsync(CancellationToken cancellationToken)
    {
        var candidates = await LoadFileStorageIssueCandidatesAsync(cancellationToken);
        return CreateFileStorageIssueDetails(candidates, IsStoredFileSizeMismatch);
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadStoredFileHashMismatchDetailsAsync(CancellationToken cancellationToken)
    {
        var candidates = await LoadFileStorageIssueCandidatesAsync(cancellationToken);
        return CreateFileStorageIssueDetails(candidates, IsStoredFileHashMismatch);
    }

    private async Task<List<FileStorageIssueCandidate>> LoadFileStorageIssueCandidatesAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<FileStorageIssueCandidate>();

        var contractRows = await _officeScopeService
            .ApplyCustomerContractScope(_dbContext.CustomerContracts.IgnoreQueryFilters().AsNoTracking())
            .Where(contract => !contract.IsDeleted)
            .Select(contract => new
            {
                contract.Id,
                contract.CustomerId,
                contract.ContractType,
                contract.FileName,
                contract.Description,
                contract.UploadedAtUtc,
                contract.FileSize,
                contract.FileHash,
                contract.StoragePath,
                FileContentLength = contract.FileContent.Length,
                CustomerName = contract.Customer == null ? string.Empty : contract.Customer.NameOriginal,
                TenantCode = contract.Customer == null ? string.Empty : contract.Customer.TenantCode,
                OfficeCode = contract.Customer == null ? string.Empty : contract.Customer.OfficeCode,
                ResponsibleOfficeCode = contract.Customer == null ? string.Empty : contract.Customer.ResponsibleOfficeCode
            })
            .ToListAsync(cancellationToken);

        candidates.AddRange(contractRows.Select(contract => new FileStorageIssueCandidate(
            EntityType: "거래처계약서",
            EntityId: contract.Id,
            PrimaryText: FirstNonEmpty(contract.FileName, FormatGuid(contract.Id)),
            SecondaryText: CombineParts(contract.ContractType, contract.Description),
            ReferenceText: $"거래처 {FirstNonEmpty(contract.CustomerName, FormatGuid(contract.CustomerId))}",
            ScopeText: CombineParts(FormatScope(contract.TenantCode, contract.OfficeCode, contract.ResponsibleOfficeCode), $"업로드 {FormatUtcDateTime(contract.UploadedAtUtc)}"),
            FileSize: contract.FileSize,
            FileHash: contract.FileHash,
            StoragePath: contract.StoragePath,
            FileContentLength: contract.FileContentLength,
            StorageInspection: InspectStoredFile(contract.StoragePath, contract.FileHash))));

        var transactionAttachmentRows = await _officeScopeService
            .ApplyTransactionAttachmentScope(_dbContext.TransactionAttachments.IgnoreQueryFilters().AsNoTracking())
            .Where(attachment => !attachment.IsDeleted)
            .Select(attachment => new
            {
                attachment.Id,
                attachment.TransactionId,
                attachment.AttachmentType,
                attachment.FileName,
                attachment.Description,
                attachment.UploadedAtUtc,
                attachment.FileSize,
                attachment.FileHash,
                attachment.StoragePath,
                FileContentLength = attachment.FileContent.Length,
                TransactionDate = attachment.Transaction == null ? (DateOnly?)null : attachment.Transaction.TransactionDate,
                TransactionKind = attachment.Transaction == null ? string.Empty : attachment.Transaction.TransactionKind,
                LinkedInvoiceNumber = attachment.Transaction == null ? string.Empty : attachment.Transaction.LinkedInvoiceNumber,
                TenantCode = attachment.Transaction == null ? string.Empty : attachment.Transaction.TenantCode,
                OfficeCode = attachment.Transaction == null ? string.Empty : attachment.Transaction.OfficeCode,
                ResponsibleOfficeCode = attachment.Transaction == null ? string.Empty : attachment.Transaction.ResponsibleOfficeCode
            })
            .ToListAsync(cancellationToken);

        candidates.AddRange(transactionAttachmentRows.Select(attachment => new FileStorageIssueCandidate(
            EntityType: "거래첨부",
            EntityId: attachment.Id,
            PrimaryText: FirstNonEmpty(attachment.FileName, FormatGuid(attachment.Id)),
            SecondaryText: CombineParts(attachment.AttachmentType, attachment.Description),
            ReferenceText: attachment.TransactionDate.HasValue
                ? CombineParts(
                    $"거래 {FormatDate(attachment.TransactionDate.Value)}",
                    attachment.TransactionKind,
                    string.IsNullOrWhiteSpace(attachment.LinkedInvoiceNumber) ? null : $"전표 {attachment.LinkedInvoiceNumber}")
                : $"거래 {FormatGuid(attachment.TransactionId)}",
            ScopeText: CombineParts(FormatScope(attachment.TenantCode, attachment.OfficeCode, attachment.ResponsibleOfficeCode), $"업로드 {FormatUtcDateTime(attachment.UploadedAtUtc)}"),
            FileSize: attachment.FileSize,
            FileHash: attachment.FileHash,
            StoragePath: attachment.StoragePath,
            FileContentLength: attachment.FileContentLength,
            StorageInspection: InspectStoredFile(attachment.StoragePath, attachment.FileHash))));

        var scopedPayments = _officeScopeService
            .ApplyPaymentScope(_dbContext.Payments.IgnoreQueryFilters().AsNoTracking())
            .Where(payment => !payment.IsDeleted);
        var paymentAttachmentRows = await (
                from attachment in _dbContext.PaymentAttachments.IgnoreQueryFilters().AsNoTracking().Where(attachment => !attachment.IsDeleted)
                join payment in scopedPayments on attachment.PaymentId equals payment.Id
                select new
                {
                    attachment.Id,
                    attachment.PaymentId,
                    attachment.AttachmentType,
                    attachment.FileName,
                    attachment.Description,
                    attachment.UploadedAtUtc,
                    attachment.FileSize,
                    attachment.FileHash,
                    attachment.StoragePath,
                    FileContentLength = attachment.FileContent.Length,
                    payment.PaymentDate,
                    payment.Amount,
                    InvoiceNumber = payment.Invoice == null ? string.Empty : payment.Invoice.InvoiceNumber,
                    TenantCode = payment.Invoice == null ? string.Empty : payment.Invoice.TenantCode,
                    OfficeCode = payment.Invoice == null ? string.Empty : payment.Invoice.OfficeCode,
                    ResponsibleOfficeCode = payment.Invoice == null ? string.Empty : payment.Invoice.ResponsibleOfficeCode
                })
            .ToListAsync(cancellationToken);

        candidates.AddRange(paymentAttachmentRows.Select(attachment => new FileStorageIssueCandidate(
            EntityType: "결제첨부",
            EntityId: attachment.Id,
            PrimaryText: FirstNonEmpty(attachment.FileName, FormatGuid(attachment.Id)),
            SecondaryText: CombineParts(attachment.AttachmentType, attachment.Description),
            ReferenceText: CombineParts(
                $"결제 {FormatDate(attachment.PaymentDate)}",
                $"금액 {FormatMoney(attachment.Amount)}",
                string.IsNullOrWhiteSpace(attachment.InvoiceNumber) ? null : $"전표 {attachment.InvoiceNumber}"),
            ScopeText: CombineParts(FormatScope(attachment.TenantCode, attachment.OfficeCode, attachment.ResponsibleOfficeCode), $"업로드 {FormatUtcDateTime(attachment.UploadedAtUtc)}"),
            FileSize: attachment.FileSize,
            FileHash: attachment.FileHash,
            StoragePath: attachment.StoragePath,
            FileContentLength: attachment.FileContentLength,
            StorageInspection: InspectStoredFile(attachment.StoragePath, attachment.FileHash))));

        return candidates;
    }

    private async Task<List<IntegrityIssueDetailRowDto>> LoadSharedItemScopeConflictDetailsAsync(CancellationToken cancellationToken)
    {
        var snapshots = await LoadSharedItemScopeConflictSnapshotsAsync(cancellationToken);
        return snapshots
            .Select(snapshot => CreateDetailRow(
                entityType: "품목",
                entityIdText: FormatGuid(snapshot.Item.Id),
                primaryText: FirstNonEmpty(snapshot.Item.NameOriginal, snapshot.Item.NameMatchKey, FormatGuid(snapshot.Item.Id)),
                secondaryText: CombineParts(snapshot.Item.SpecificationOriginal, snapshot.Item.CategoryName),
                referenceText: $"tenant 후보 {string.Join(", ", snapshot.Inference.EvidenceTenantCodes)}",
                scopeText: FormatScope(snapshot.Item.TenantCode, snapshot.Item.OfficeCode),
                detailText: CombineParts(
                    snapshot.Inference.RentalOfficeCodes.Count > 0 ? $"렌탈 {FormatOfficeList(snapshot.Inference.RentalOfficeCodes)}" : null,
                    snapshot.Inference.WarehouseOfficeCodes.Count > 0 ? $"창고 {FormatOfficeList(snapshot.Inference.WarehouseOfficeCodes)}" : null,
                    snapshot.Inference.InvoiceOfficeCodes.Count > 0 ? $"전표 {FormatOfficeList(snapshot.Inference.InvoiceOfficeCodes)}" : null,
                    string.IsNullOrWhiteSpace(snapshot.Item.TrackingType) ? null : $"재고방식 {snapshot.Item.TrackingType}")))
            .ToList();
    }

    private async Task<List<SharedItemScopeConflictSnapshot>> LoadSharedItemScopeConflictSnapshotsAsync(CancellationToken cancellationToken)
    {
        var sharedItems = await _officeScopeService.ApplyItemScope(_dbContext.Items.IgnoreQueryFilters().AsNoTracking())
            .Where(item => !item.IsDeleted && item.OfficeCode == OfficeCodeCatalog.Shared)
            .ToListAsync(cancellationToken);

        return await LoadSharedItemScopeConflictSnapshotsAsync(sharedItems, cancellationToken);
    }

    private async Task<List<SharedItemScopeConflictSnapshot>> LoadSharedItemScopeConflictSnapshotsAsync(
        IReadOnlyCollection<Item> scopedItems,
        CancellationToken cancellationToken)
    {
        var sharedItems = scopedItems
            .Where(item => !item.IsDeleted && string.Equals(item.OfficeCode, OfficeCodeCatalog.Shared, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sharedItems.Count == 0)
            return [];

        var itemIds = sharedItems
            .Select(item => item.Id)
            .Where(itemId => itemId != Guid.Empty)
            .Distinct()
            .ToList();
        if (itemIds.Count == 0)
            return [];

        var rentalOfficeLookup = (await _dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking()
                .Where(asset => !asset.IsDeleted && asset.ItemId.HasValue && itemIds.Contains(asset.ItemId.Value))
                .Select(asset => new
                {
                    ItemId = asset.ItemId!.Value,
                    asset.OfficeCode,
                    asset.ResponsibleOfficeCode,
                    asset.ManagementCompanyCode
                })
                .ToListAsync(cancellationToken))
            .Select(asset => new
            {
                asset.ItemId,
                OfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(
                    asset.OfficeCode,
                    asset.ResponsibleOfficeCode,
                    asset.ManagementCompanyCode)
            })
            .Where(entry => OfficeCodeCatalog.TryNormalizeOfficeCode(entry.OfficeCode, out _))
            .GroupBy(entry => entry.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(entry => entry.OfficeCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var warehouseOfficeLookup = (await _dbContext.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock => itemIds.Contains(stock.ItemId))
                .Select(stock => new
                {
                    stock.ItemId,
                    stock.WarehouseCode
                })
                .ToListAsync(cancellationToken))
            .Select(stock => new
            {
                stock.ItemId,
                OfficeCode = ResolveOfficeCodeFromWarehouseCode(stock.WarehouseCode)
            })
            .Where(entry => OfficeCodeCatalog.TryNormalizeOfficeCode(entry.OfficeCode, out _))
            .GroupBy(entry => entry.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(entry => entry.OfficeCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var invoiceOfficeLookup = (await (
                from line in _dbContext.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking()
                    on line.InvoiceId equals invoice.Id
                where !line.IsDeleted && !invoice.IsDeleted && line.ItemId.HasValue && itemIds.Contains(line.ItemId.Value)
                select new
                {
                    ItemId = line.ItemId!.Value,
                    invoice.OfficeCode,
                    invoice.ResponsibleOfficeCode
                })
            .ToListAsync(cancellationToken))
            .Select(entry => new
            {
                entry.ItemId,
                OfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(
                    entry.OfficeCode,
                    entry.ResponsibleOfficeCode,
                    OfficeCodeCatalog.Shared)
            })
            .Where(entry => OfficeCodeCatalog.TryNormalizeOfficeCode(entry.OfficeCode, out _))
            .GroupBy(entry => entry.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(entry => entry.OfficeCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());

        return sharedItems
            .Select(item => new SharedItemScopeConflictSnapshot(
                item,
                ItemScopeInference.Analyze(
                    item.OfficeCode,
                    item.TenantCode,
                    rentalOfficeLookup.TryGetValue(item.Id, out var rentalOfficeCodes) ? rentalOfficeCodes : [],
                    warehouseOfficeLookup.TryGetValue(item.Id, out var warehouseOfficeCodes) ? warehouseOfficeCodes : [],
                    invoiceOfficeLookup.TryGetValue(item.Id, out var invoiceOfficeCodes) ? invoiceOfficeCodes : [])))
            .Where(snapshot => snapshot.Inference.HasCrossTenantEvidence)
            .OrderBy(snapshot => snapshot.Item.NameOriginal, StringComparer.CurrentCulture)
            .ThenBy(snapshot => snapshot.Item.SpecificationOriginal, StringComparer.CurrentCulture)
            .ThenBy(snapshot => snapshot.Item.Id)
            .ToList();
    }

    private async Task<List<RentalTemplateScanRow>> LoadRentalTemplateScanRowsAsync(CancellationToken cancellationToken)
    {
        var profiles = await _officeScopeService.ApplyRentalBillingProfileScope(_dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
            .Where(profile => !profile.IsDeleted && profile.IsActive)
            .ToListAsync(cancellationToken);
        if (profiles.Count == 0)
            return [];

        var profileIds = profiles
            .Select(profile => profile.Id)
            .Where(id => id != Guid.Empty)
            .ToHashSet();

        var scopedAssets = await _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
            .Where(asset => !asset.IsDeleted)
            .ToListAsync(cancellationToken);
        var assetsByProfile = scopedAssets
            .Where(asset => asset.BillingProfileId.HasValue && profileIds.Contains(asset.BillingProfileId.Value))
            .GroupBy(asset => asset.BillingProfileId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(asset => FirstNonEmpty(asset.ManagementNumber, asset.AssetKey, asset.ManagementId), StringComparer.OrdinalIgnoreCase)
                    .ToList());
        var assetsById = scopedAssets
            .Where(asset => asset.Id != Guid.Empty)
            .GroupBy(asset => asset.Id)
            .ToDictionary(group => group.Key, group => group.First());

        return profiles
            .Select(profile =>
            {
                var parsed = ParseRentalBillingTemplateItems(profile);
                var linkedAssets = assetsByProfile.TryGetValue(profile.Id, out var foundAssets)
                    ? foundAssets
                    : [];
                return new RentalTemplateScanRow(
                    profile,
                    parsed.Success,
                    parsed.Items,
                    parsed.Items.Sum(ResolveTemplateMonthlyAmount),
                    linkedAssets,
                    linkedAssets.Sum(asset => Math.Max(0m, asset.MonthlyFee)),
                    assetsById);
            })
            .ToList();
    }

    private static int CountRentalAssetTemplateMonthlyMismatches(IEnumerable<RentalTemplateScanRow> rows)
        => rows.SelectMany(CreateRentalAssetTemplateMonthlyMismatchRows).Count();

    private static bool ShouldWarnRentalProfileAssetMonthlyMismatch(RentalTemplateScanRow row)
    {
        if (row.LinkedAssets.Count == 0 ||
            row.LinkedAssetMonthlyAmount <= 0m ||
            !AmountDiffers(row.Profile.MonthlyAmount, row.LinkedAssetMonthlyAmount))
        {
            return false;
        }

        if (row.TemplateParseSucceeded &&
            row.TemplateItems.Count > 0 &&
            !AmountDiffers(row.Profile.MonthlyAmount, row.TemplateMonthlyAmount))
        {
            return false;
        }

        return true;
    }

    private static IEnumerable<RentalAssetTemplateMonthlyMismatchRow> CreateRentalAssetTemplateMonthlyMismatchRows(RentalTemplateScanRow row)
    {
        if (!row.TemplateParseSucceeded)
            yield break;

        foreach (var templateItem in row.TemplateItems)
        {
            var includedAssetIds = (templateItem.IncludedAssetIds ?? [])
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            if (includedAssetIds.Count == 0)
                continue;

            var linkedAssets = includedAssetIds
                .Select(id => row.ScopedAssetsById.TryGetValue(id, out var asset) ? asset : null)
                .Where(asset => asset is not null)
                .Cast<RentalAsset>()
                .ToList();
            if (linkedAssets.Count == 0)
                continue;

            var assetMonthlyAmount = linkedAssets.Sum(asset => Math.Max(0m, asset.MonthlyFee));
            var templateMonthlyAmount = ResolveTemplateMonthlyAmount(templateItem);
            if (assetMonthlyAmount <= 0m || !AmountDiffers(assetMonthlyAmount, templateMonthlyAmount))
                continue;

            yield return new RentalAssetTemplateMonthlyMismatchRow(
                row.Profile,
                templateItem,
                linkedAssets,
                templateMonthlyAmount,
                assetMonthlyAmount);
        }
    }

    private static ParsedRentalBillingTemplateItems ParseRentalBillingTemplateItems(RentalBillingProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.BillingTemplateJson))
            return new ParsedRentalBillingTemplateItems(true, []);

        try
        {
            var parsed = JsonSerializer.Deserialize<List<RentalBillingTemplateItemSnapshot>>(profile.BillingTemplateJson, RentalTemplateJsonOptions) ?? [];
            var normalized = parsed
                .Where(item => item is not null)
                .Select(item => new RentalBillingTemplateItemSnapshot
                {
                    ItemId = item.ItemId == Guid.Empty ? Guid.NewGuid() : item.ItemId,
                    DisplayItemName = FirstNonEmpty(item.DisplayItemName, profile.ItemName, "렌탈 임대료"),
                    BillingLineMode = item.BillingLineMode ?? string.Empty,
                    Specification = item.Specification ?? string.Empty,
                    Unit = item.Unit ?? string.Empty,
                    MaterialNumber = item.MaterialNumber ?? string.Empty,
                    RepresentativeAssetId = item.RepresentativeAssetId,
                    Quantity = item.Quantity <= 0m ? 1m : item.Quantity,
                    UnitPrice = Math.Max(0m, item.UnitPrice),
                    Amount = Math.Max(0m, item.Amount),
                    Note = item.Note ?? string.Empty,
                    IncludedAssetIds = item.IncludedAssetIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? []
                })
                .ToList();

            return new ParsedRentalBillingTemplateItems(true, normalized);
        }
        catch
        {
            return new ParsedRentalBillingTemplateItems(false, []);
        }
    }

    private static List<RentalBillingRunSettlementSnapshot> ParseRentalBillingRuns(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<RentalBillingRunSettlementSnapshot>>(json, RentalTemplateJsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static Guid NormalizeRunId(Guid? runId)
        => runId.HasValue && runId.Value != Guid.Empty ? runId.Value : Guid.Empty;

    private static decimal ResolveTemplateMonthlyAmount(RentalBillingTemplateItemSnapshot item)
    {
        var quantity = item.Quantity <= 0m ? 1m : item.Quantity;
        var unitPrice = Math.Max(0m, item.UnitPrice);
        var calculated = quantity * unitPrice;
        return calculated > 0m ? calculated : Math.Max(0m, item.Amount);
    }

    private static bool AmountDiffers(decimal left, decimal right)
        => Math.Abs(left - right) >= 1m;

    private static List<Customer> FindSimilarRentalCustomers(
        RentalBillingProfile profile,
        IReadOnlyCollection<RentalAsset> assets,
        IReadOnlyCollection<Customer> customers)
    {
        var candidateKeys = BuildSimilarCustomerCandidateKeys(
            [profile.CustomerName, .. assets.SelectMany(asset => new[]
            {
                asset.CustomerName,
                asset.CurrentCustomerName,
                asset.LastCustomerName
            })]);
        if (candidateKeys.Count == 0)
            return [];

        return customers
            .Select(customer => new
            {
                Customer = customer,
                Score = GetBestCustomerSimilarityScore(candidateKeys, customer)
            })
            .Where(row => row.Score >= 0)
            .OrderBy(row => row.Score)
            .ThenBy(row => row.Customer.ResponsibleOfficeCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Customer.NameOriginal, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(row => row.Customer)
            .ToList();
    }

    private static List<string> BuildSimilarCustomerCandidateKeys(IEnumerable<string?> names)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            foreach (var key in new[]
            {
                NormalizeComparableCustomerKey(name),
                NormalizeComparableCustomerKey(ExtractBracketSuffix(name))
            })
            {
                if (key.Length >= 4 && seen.Add(key))
                    keys.Add(key);
            }
        }

        return keys;
    }

    private static int GetBestCustomerSimilarityScore(IReadOnlyCollection<string> candidateKeys, Customer customer)
    {
        var customerKeys = BuildSimilarCustomerCandidateKeys([
            customer.NameOriginal,
            customer.NameMatchKey
        ]);
        var best = -1;
        foreach (var candidateKey in candidateKeys)
        {
            foreach (var customerKey in customerKeys)
            {
                var score = GetCustomerSimilarityScore(candidateKey, customerKey);
                if (score < 0)
                    continue;

                if (best < 0 || score < best)
                    best = score;
            }
        }

        return best;
    }

    private static int GetCustomerSimilarityScore(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return -1;

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return 0;

        var shorter = left.Length <= right.Length ? left : right;
        var longer = left.Length > right.Length ? left : right;
        if (shorter.Length >= 4 &&
            longer.Contains(shorter, StringComparison.OrdinalIgnoreCase) &&
            shorter.Length >= Math.Max(4, longer.Length / 2))
        {
            return longer.Length - shorter.Length;
        }

        var distance = ComputeLevenshteinDistance(left, right);
        var allowedDistance = longer.Length <= 8
            ? 1
            : Math.Min(4, Math.Max(2, longer.Length / 4));
        return distance <= allowedDistance ? distance : -1;
    }

    private static string NormalizeComparableCustomerKey(string? value)
        => RentalCatalogValueNormalizer.NormalizeLooseKey(value);

    private static string ExtractBracketSuffix(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        var bracketIndex = text.LastIndexOf(']');
        if (bracketIndex >= 0 && bracketIndex + 1 < text.Length)
            return text[(bracketIndex + 1)..].Trim();

        return string.Empty;
    }

    private static int ComputeLevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
            return right.Length;
        if (right.Length == 0)
            return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var substitutionCost = char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 1]) ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static string BuildAssetMonthlyBreakdown(IReadOnlyCollection<RentalAsset> assets)
    {
        if (assets.Count == 0)
            return string.Empty;

        return "자산별 " + string.Join(", ", assets
            .OrderBy(asset => FirstNonEmpty(asset.ManagementNumber, asset.AssetKey, asset.ManagementId), StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .Select(asset => $"{FirstNonEmpty(asset.ManagementNumber, asset.AssetKey, asset.ManagementId, FormatGuid(asset.Id))}:{FormatMoney(asset.MonthlyFee)}"));
    }

    private static void AddIssue(ICollection<IntegrityIssueDto> issues, string code, int count, string severity, string message)
    {
        if (count <= 0)
            return;

        issues.Add(new IntegrityIssueDto
        {
            Code = code,
            Severity = severity,
            Count = count,
            Message = message
        });
    }

    private static int CountDuplicateRows<T>(IEnumerable<T> source, Func<T, string> keySelector)
    {
        return source
            .Select(keySelector)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Sum(group => group.Count());
    }

    private static string BuildScopedCustomerMatchKey(Customer customer)
    {
        var nameKey = RentalCatalogValueNormalizer.NormalizeLooseKey(
            string.IsNullOrWhiteSpace(customer.NameMatchKey)
                ? customer.NameOriginal
                : customer.NameMatchKey);
        if (string.IsNullOrWhiteSpace(nameKey))
            return string.Empty;

        return string.Join('|',
            NormalizeDuplicateScopeValue(customer.TenantCode),
            NormalizeDuplicateScopeValue(FirstNonEmpty(customer.ResponsibleOfficeCode, customer.OfficeCode)),
            nameKey);
    }

    private static string BuildScopedItemNameMatchKey(Item item)
        => ItemDuplicateKeyBuilder.BuildScopedItemNameMatchKey(
            item.TenantCode,
            item.OfficeCode,
            item.NameMatchKey,
            item.NameOriginal);

    private static string BuildScopedItemDescriptorConflictKey(Item item)
    {
        var baseKey = ItemDuplicateKeyBuilder.BuildScopedItemDescriptorConflictKey(
            item.TenantCode,
            item.OfficeCode,
            item.NameMatchKey,
            item.NameOriginal,
            item.SpecificationMatchKey,
            item.SpecificationOriginal,
            item.CategoryName,
            item.ItemKind,
            item.TrackingType,
            item.IsRental);
        if (string.IsNullOrWhiteSpace(baseKey))
            return string.Empty;

        return string.Join('|',
            baseKey,
            RentalCatalogValueNormalizer.NormalizeLooseKey(item.MaterialNumber));
    }

    private static bool IsPotentiallyAmbiguousItemDuplicate(Item item)
    {
        if (item.IsRental)
            return false;

        var trackingType = ItemOperationalPolicy.NormalizeTrackingType(
            item.TrackingType,
            item.ItemKind,
            item.CategoryName,
            item.IsRental);
        var itemKind = ItemOperationalPolicy.NormalizeItemKind(
            item.ItemKind,
            trackingType,
            item.CategoryName,
            item.IsRental);

        if (string.Equals(trackingType, ItemTrackingTypes.NonStock, StringComparison.Ordinal) ||
            string.Equals(itemKind, ItemKinds.Billing, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static string NormalizeDuplicateScopeValue(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static bool TryResolveIssueDefinition(string code, out IntegrityIssueDefinition definition)
    {
        definition = (code ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "duplicate_rental_profile_keys" => new IntegrityIssueDefinition("duplicate_rental_profile_keys", "Error", "중복된 렌탈 청구 프로필 키가 존재합니다."),
            "duplicate_rental_asset_keys" => new IntegrityIssueDefinition("duplicate_rental_asset_keys", "Error", "중복된 렌탈 자산 키가 존재합니다."),
            "duplicate_customer_match_keys" => new IntegrityIssueDefinition("duplicate_customer_match_keys", "Warning", "중복된 거래처 매칭키가 존재합니다."),
            "duplicate_item_name_match_keys" => new IntegrityIssueDefinition("duplicate_item_name_match_keys", "Info", "동일 품명 매칭키를 공유하는 품목이 있습니다. 규격/분류가 다르면 정상일 수 있습니다."),
            "duplicate_item_match_keys" => new IntegrityIssueDefinition("duplicate_item_match_keys", "Warning", "동일한 품명/규격/분류/구분/재고방식 조합이 중복됩니다."),
            "ambiguous_shared_item_tenant_scope" => new IntegrityIssueDefinition("ambiguous_shared_item_tenant_scope", "Warning", "공용(ALL) 품목 중 사용 이력이 서로 다른 업체로 섞여 tenant 자동 보정이 보류된 항목이 있습니다."),
            "deleted_item_stock_residue" => new IntegrityIssueDefinition("deleted_item_stock_residue", "Error", "삭제된 품목에 현재재고 또는 창고 재고 행이 남아 있습니다."),
            "cross_tenant_inventory_transfers" => new IntegrityIssueDefinition("cross_tenant_inventory_transfers", "Error", "업체 간 직접 재고이동 문서가 존재합니다."),
            "inventory_transfer_line_missing_transfer_rows" => new IntegrityIssueDefinition("inventory_transfer_line_missing_transfer_rows", "Error", "부모 재고이동 문서가 없는 재고이동 세부내역이 존재합니다."),
            "orphan_item_warehouse_stock_refs" => new IntegrityIssueDefinition("orphan_item_warehouse_stock_refs", "Error", "품목이 없는 창고 재고 행이 존재합니다."),
            "item_stock_snapshot_mismatch" => new IntegrityIssueDefinition("item_stock_snapshot_mismatch", "Warning", "품목 현재재고와 창고 합계가 일치하지 않는 항목이 있습니다."),
            "orphan_invoice_customer_refs" => new IntegrityIssueDefinition("orphan_invoice_customer_refs", "Error", "거래처가 없는 전표 참조가 존재합니다."),
            "active_invoice_lines_deleted_invoice" => new IntegrityIssueDefinition("active_invoice_lines_deleted_invoice", "Error", "삭제된 전표에 활성 세부내역 행이 남아 있습니다."),
            "active_invoice_deleted_line_only" => new IntegrityIssueDefinition("active_invoice_deleted_line_only", "Warning", "활성 전표에 활성 세부내역이 없고 삭제된 세부내역만 남아 있습니다."),
            "invoice_total_active_line_mismatch" => new IntegrityIssueDefinition("invoice_total_active_line_mismatch", "Error", "활성 전표 금액 합계와 활성 세부내역 기준 계산값이 다릅니다."),
            "invoice_line_missing_invoice_rows" => new IntegrityIssueDefinition("invoice_line_missing_invoice_rows", "Error", "부모 전표 행이 없는 전표 세부내역이 존재합니다."),
            "orphan_transaction_customer_refs" => new IntegrityIssueDefinition("orphan_transaction_customer_refs", "Error", "거래처가 없는 수금/지불 참조가 존재합니다."),
            "orphan_rental_profile_customer_refs" => new IntegrityIssueDefinition("orphan_rental_profile_customer_refs", "Error", "거래처가 없는 렌탈 청구 프로필 참조가 존재합니다."),
            "rental_profile_customer_scope_mismatch" => new IntegrityIssueDefinition("rental_profile_customer_scope_mismatch", "Error", "렌탈 청구 프로필이 다른 업체/담당지점 거래처를 참조합니다."),
            "rental_profile_customer_unlinked" => new IntegrityIssueDefinition("rental_profile_customer_unlinked", "Warning", "거래처 ID 없이 거래처명만 저장된 렌탈 청구 프로필이 있습니다."),
            "rental_profile_monthly_amount_mismatch" => new IntegrityIssueDefinition("rental_profile_monthly_amount_mismatch", "Warning", "렌탈 청구 프로필 월 기준금액과 청구 품목 합계가 다릅니다."),
            "rental_profile_asset_monthly_amount_mismatch" => new IntegrityIssueDefinition("rental_profile_asset_monthly_amount_mismatch", "Warning", "렌탈 청구 프로필 월 기준금액과 연결 자산 월요금 합계가 다릅니다."),
            "rental_asset_template_monthly_mismatch" => new IntegrityIssueDefinition("rental_asset_template_monthly_mismatch", "Warning", "렌탈 자산 월요금 합계와 청구 품목 금액이 다릅니다."),
            "orphan_rental_asset_customer_refs" => new IntegrityIssueDefinition("orphan_rental_asset_customer_refs", "Error", "거래처가 없는 렌탈 자산 참조가 존재합니다."),
            "rental_asset_customer_scope_mismatch" => new IntegrityIssueDefinition("rental_asset_customer_scope_mismatch", "Error", "렌탈 자산이 다른 업체/담당지점 거래처를 참조합니다."),
            "orphan_rental_asset_profile_refs" => new IntegrityIssueDefinition("orphan_rental_asset_profile_refs", "Error", "렌탈 청구 프로필이 없는 자산 연결이 존재합니다."),
            "rental_asset_profile_scope_mismatch" => new IntegrityIssueDefinition("rental_asset_profile_scope_mismatch", "Error", "렌탈 자산이 다른 업체/담당지점 청구 프로필을 참조합니다."),
            "orphan_rental_asset_item_refs" => new IntegrityIssueDefinition("orphan_rental_asset_item_refs", "Error", "품목이 없는 렌탈 자산 연결이 존재합니다."),
            "rental_assignment_missing_reference_rows" => new IntegrityIssueDefinition("rental_assignment_missing_reference_rows", "Error", "렌탈 임대이력이 존재하지 않거나 삭제된 자산/거래처/청구 프로필을 참조합니다."),
            "rental_assignment_current_scope_mismatch" => new IntegrityIssueDefinition("rental_assignment_current_scope_mismatch", "Error", "현재 렌탈 설치이력이 다른 업체/담당지점 자산/거래처/청구 프로필을 참조합니다."),
            "rental_assignment_historical_stale_reference_rows" => new IntegrityIssueDefinition("rental_assignment_historical_stale_reference_rows", "Info", "과거 렌탈 임대이력의 거래처/청구 프로필 참조가 현재 마스터에서 사라졌지만 스냅샷 표시값은 남아 있습니다."),
            "rental_asset_multiple_current_assignments" => new IntegrityIssueDefinition("rental_asset_multiple_current_assignments", "Error", "하나의 렌탈 자산에 현재 임대중으로 표시된 이력이 여러 개 있습니다."),
            "orphan_transaction_invoice_refs" => new IntegrityIssueDefinition("orphan_transaction_invoice_refs", "Error", "전표가 없는 거래/수금 참조가 존재합니다."),
            "orphan_payment_invoice_refs" => new IntegrityIssueDefinition("orphan_payment_invoice_refs", "Error", "전표가 없는 수금/지급 참조가 존재합니다."),
            "deleted_payment_missing_invoice_rows" => new IntegrityIssueDefinition("deleted_payment_missing_invoice_rows", "Error", "영구 삭제된 전표의 삭제 결제 잔여 행이 존재합니다."),
            "invoice_linked_transaction_payment_mismatch" => new IntegrityIssueDefinition("invoice_linked_transaction_payment_mismatch", "Error", "전표 연결 거래내역과 파생 수금/지급 행의 전표·금액 상태가 다릅니다."),
            "rental_invoice_deleted_payment_detached_transaction" => new IntegrityIssueDefinition("rental_invoice_deleted_payment_detached_transaction", "Error", "활성 렌탈 전표에 삭제 상태 수금/지급과 전표 링크가 끊긴 활성 거래내역이 함께 남아 있습니다."),
            "rental_billing_run_settlement_mismatch" => new IntegrityIssueDefinition("rental_billing_run_settlement_mismatch", "Error", "렌탈 청구 run의 저장 정산금액과 실제 활성 수금/거래내역 합계가 다릅니다."),
            "rental_billing_run_missing_run_id" => new IntegrityIssueDefinition("rental_billing_run_missing_run_id", "Info", "렌탈 청구 프로필에 run ID가 비어 있는 과거 청구 JSON이 있습니다."),
            "rental_billing_profile_summary_mismatch" => new IntegrityIssueDefinition("rental_billing_profile_summary_mismatch", "Error", "렌탈 청구 프로필 요약 정산/미수금액이 대표 청구 run의 실제 입금 근거와 다릅니다."),
            "orphan_attachment_transaction_refs" => new IntegrityIssueDefinition("orphan_attachment_transaction_refs", "Error", "거래내역이 없는 증빙 첨부가 존재합니다."),
            "deleted_transaction_attachment_missing_transaction_rows" => new IntegrityIssueDefinition("deleted_transaction_attachment_missing_transaction_rows", "Error", "영구 삭제된 거래내역의 삭제 첨부 잔여 행이 존재합니다."),
            "orphan_payment_attachment_refs" => new IntegrityIssueDefinition("orphan_payment_attachment_refs", "Error", "결제내역이 없는 결제 첨부가 존재합니다."),
            "deleted_payment_attachment_missing_payment_rows" => new IntegrityIssueDefinition("deleted_payment_attachment_missing_payment_rows", "Error", "영구 삭제된 결제의 삭제 첨부 잔여 행이 존재합니다."),
            "unsupported_attachment_file_type" => new IntegrityIssueDefinition("unsupported_attachment_file_type", "Warning", "PDF/이미지 정책과 맞지 않는 거래/결제 증빙 첨부가 있습니다."),
            "attachment_content_signature_mismatch" => new IntegrityIssueDefinition("attachment_content_signature_mismatch", "Warning", "파일명/MIME과 실제 저장 파일 내용이 일치하지 않는 거래/결제 증빙 첨부가 있습니다."),
            "customer_contract_missing_customer_rows" => new IntegrityIssueDefinition("customer_contract_missing_customer_rows", "Error", "부모 거래처 행이 없는 계약/첨부가 존재합니다."),
            "rental_billing_log_missing_profile_rows" => new IntegrityIssueDefinition("rental_billing_log_missing_profile_rows", "Error", "부모 청구 프로필 행이 없는 렌탈 청구 로그가 존재합니다."),
            "file_content_unavailable" => new IntegrityIssueDefinition("file_content_unavailable", "Error", "파일 크기는 있으나 저장소 경로와 DB 파일 본문이 모두 비어 있는 첨부/계약서가 있습니다."),
            "file_content_db_residue" => new IntegrityIssueDefinition("file_content_db_residue", "Warning", "파일 본문이 DB에 남아 저장소 이동이 완료되지 않은 첨부/계약서가 있습니다."),
            "file_storage_missing" => new IntegrityIssueDefinition("file_storage_missing", "Error", "저장소 경로가 있으나 실제 저장 파일을 읽을 수 없는 첨부/계약서가 있습니다."),
            "file_storage_size_mismatch" => new IntegrityIssueDefinition("file_storage_size_mismatch", "Error", "저장소 실제 파일 크기와 DB 파일 크기가 다른 첨부/계약서가 있습니다."),
            "file_storage_hash_mismatch" => new IntegrityIssueDefinition("file_storage_hash_mismatch", "Error", "저장소 실제 파일 SHA256과 DB 파일 해시가 다른 첨부/계약서가 있습니다."),
            _ => new IntegrityIssueDefinition(string.Empty, string.Empty, string.Empty)
        };

        return !string.IsNullOrWhiteSpace(definition.Code);
    }

    private static bool IsCrossTenantInventoryTransfer(InventoryTransferRouteSnapshot transfer)
    {
        var route = DescribeInventoryTransfer(transfer);
        return !string.Equals(route.SourceTenantCode, route.TargetTenantCode, StringComparison.OrdinalIgnoreCase);
    }

    private static InventoryTransferRouteDescription DescribeInventoryTransfer(InventoryTransferRouteSnapshot transfer)
    {
        var normalizedSourceOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeLoose(
            transfer.SourceOfficeCode,
            transfer.FromWarehouseCode,
            OfficeCodeCatalog.Usenet);
        var normalizedTargetOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeLoose(
            transfer.TargetOfficeCode,
            transfer.ToWarehouseCode,
            OfficeCodeCatalog.Yeonsu);
        var normalizedSourceTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            transfer.TenantCode,
            normalizedSourceOfficeCode);
        var normalizedTargetTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            null,
            normalizedTargetOfficeCode);

        return new InventoryTransferRouteDescription(
            normalizedSourceTenantCode,
            normalizedTargetTenantCode,
            normalizedSourceOfficeCode,
            normalizedTargetOfficeCode);
    }

    private async Task<HashSet<Guid>> LoadActiveRentalAssetIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var targetIds = ids
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (targetIds.Count == 0)
            return [];

        var activeIds = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => !asset.IsDeleted && targetIds.Contains(asset.Id))
            .Select(asset => asset.Id)
            .ToListAsync(cancellationToken);
        return activeIds.ToHashSet();
    }

    private async Task<HashSet<Guid>> LoadActiveCustomerIdsAsync(IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var targetIds = ids
            .Where(id => id.HasValue && id.Value != Guid.Empty)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (targetIds.Count == 0)
            return [];

        var activeIds = await _dbContext.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(customer => !customer.IsDeleted && targetIds.Contains(customer.Id))
            .Select(customer => customer.Id)
            .ToListAsync(cancellationToken);
        return activeIds.ToHashSet();
    }

    private async Task<HashSet<Guid>> LoadActiveRentalBillingProfileIdsAsync(IEnumerable<Guid?> ids, CancellationToken cancellationToken)
    {
        var targetIds = ids
            .Where(id => id.HasValue && id.Value != Guid.Empty)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (targetIds.Count == 0)
            return [];

        var activeIds = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => !profile.IsDeleted && targetIds.Contains(profile.Id))
            .Select(profile => profile.Id)
            .ToListAsync(cancellationToken);
        return activeIds.ToHashSet();
    }

    private static string BuildRentalAssignmentHistoryDisplay(RentalAssetAssignmentHistory history)
    {
        var number = FirstNonEmpty(history.ManagementNumber, history.MachineNumber, history.AssetId == Guid.Empty ? string.Empty : FormatGuid(history.AssetId));
        var customer = FirstNonEmpty(history.CustomerName, "거래처 미지정");
        var item = FirstNonEmpty(history.ItemName, "품목 미지정");
        var period = string.Join("~", new[]
        {
            history.ContractStartDate.HasValue ? FormatDate(history.ContractStartDate.Value) : string.Empty,
            history.ContractEndDate.HasValue ? FormatDate(history.ContractEndDate.Value) : string.Empty
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return CombineParts(number, customer, item, period);
    }

    private static string BuildRentalAssignmentMissingReferenceText(
        RentalAssetAssignmentHistory history,
        ISet<Guid> activeAssetIds,
        ISet<Guid> activeCustomerIds,
        ISet<Guid> activeProfileIds)
    {
        var missingReferences = new List<string>();
        if (history.AssetId == Guid.Empty || !activeAssetIds.Contains(history.AssetId))
            missingReferences.Add($"자산 {FormatGuid(history.AssetId)}");
        if (history.CustomerId.HasValue && history.CustomerId.Value != Guid.Empty && !activeCustomerIds.Contains(history.CustomerId.Value))
            missingReferences.Add($"거래처 {FormatGuid(history.CustomerId.Value)}");
        if (history.BillingProfileId.HasValue && history.BillingProfileId.Value != Guid.Empty && !activeProfileIds.Contains(history.BillingProfileId.Value))
            missingReferences.Add($"청구프로필 {FormatGuid(history.BillingProfileId.Value)}");

        return missingReferences.Count == 0
            ? "참조 정상"
            : string.Join(" / ", missingReferences);
    }

    private static string BuildRentalAssignmentScopeMismatchReferenceText(
        RentalAssetAssignmentHistory history,
        RentalAsset asset,
        Customer? customer,
        RentalBillingProfile? profile)
    {
        return CombineParts(
            HasDifferentTenantOrResponsibleOffice(history.TenantCode, history.ResponsibleOfficeCode, asset.TenantCode, asset.ResponsibleOfficeCode)
                ? $"자산 범위 불일치 {FormatGuid(asset.Id)} / 자산 범위 {FormatScope(asset.TenantCode, asset.OfficeCode, asset.ResponsibleOfficeCode)}"
                : null,
            customer is not null && HasDifferentTenantOrResponsibleOffice(history.TenantCode, history.ResponsibleOfficeCode, customer.TenantCode, customer.ResponsibleOfficeCode)
                ? $"거래처 범위 불일치 {FormatGuid(customer.Id)} / 거래처 범위 {FormatScope(customer.TenantCode, customer.OfficeCode, customer.ResponsibleOfficeCode)}"
                : null,
            profile is not null && HasDifferentTenantOrResponsibleOffice(history.TenantCode, history.ResponsibleOfficeCode, profile.TenantCode, profile.ResponsibleOfficeCode)
                ? $"청구프로필 범위 불일치 {FormatGuid(profile.Id)} / 청구프로필 범위 {FormatScope(profile.TenantCode, profile.OfficeCode, profile.ResponsibleOfficeCode)}"
                : null);
    }

    private static bool HasDifferentTenantOrResponsibleOffice(
        string? tenantCode,
        string? responsibleOfficeCode,
        string? referenceTenantCode,
        string? referenceResponsibleOfficeCode)
        => !string.Equals(NormalizeScopeComparisonValue(tenantCode), NormalizeScopeComparisonValue(referenceTenantCode), StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(NormalizeScopeComparisonValue(responsibleOfficeCode), NormalizeScopeComparisonValue(referenceResponsibleOfficeCode), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeScopeComparisonValue(string? value)
        => (value ?? string.Empty).Trim();

    private static string BuildRentalAssignmentHistoryDetailText(RentalAssetAssignmentHistory history)
        => CombineParts(
            history.IsCurrent ? "현재이력" : "과거이력",
            $"월요금 {FormatMoney(history.MonthlyFee)}",
            $"연결일 {FormatUtcDateTime(history.LinkedAtUtc)}",
            history.UnlinkedAtUtc.HasValue ? $"해제일 {FormatUtcDateTime(history.UnlinkedAtUtc.Value)}" : null,
            string.IsNullOrWhiteSpace(history.BillingProfileDisplay) ? null : $"청구표시 {history.BillingProfileDisplay}",
            string.IsNullOrWhiteSpace(history.InstallLocation) ? null : $"설치위치 {history.InstallLocation}",
            string.IsNullOrWhiteSpace(history.ChangeReason) ? null : $"사유 {history.ChangeReason}");

    private static IntegrityIssueDetailRowDto CreateDetailRow(
        string entityType,
        string entityIdText,
        string primaryText,
        string secondaryText,
        string referenceText,
        string scopeText,
        string detailText)
    {
        return new IntegrityIssueDetailRowDto
        {
            EntityType = NormalizeCellText(entityType),
            EntityIdText = NormalizeCellText(entityIdText),
            PrimaryText = NormalizeCellText(primaryText),
            SecondaryText = NormalizeCellText(secondaryText),
            ReferenceText = NormalizeCellText(referenceText),
            ScopeText = NormalizeCellText(scopeText),
            DetailText = NormalizeCellText(detailText)
        };
    }

    private static IntegrityIssueDetailRowDto CreateFileStorageIssueDetailRow(FileStorageIssueCandidate candidate)
    {
        return CreateDetailRow(
            entityType: candidate.EntityType,
            entityIdText: FormatGuid(candidate.EntityId),
            primaryText: candidate.PrimaryText,
            secondaryText: candidate.SecondaryText,
            referenceText: candidate.ReferenceText,
            scopeText: candidate.ScopeText,
            detailText: CombineParts(
                $"FileSize {candidate.FileSize:N0} bytes",
                string.IsNullOrWhiteSpace(candidate.FileHash) ? null : $"FileHash {candidate.FileHash}",
                string.IsNullOrWhiteSpace(candidate.StoragePath) ? "StoragePath 비어 있음" : $"StoragePath {candidate.StoragePath}",
                FormatStorageInspection(candidate.StorageInspection),
                $"DB FileContent {candidate.FileContentLength:N0} bytes"));
    }

    private static List<IntegrityIssueDetailRowDto> CreateFileStorageIssueDetails(
        IEnumerable<FileStorageIssueCandidate> candidates,
        Func<FileStorageIssueCandidate, bool> predicate)
    {
        return candidates
            .Where(predicate)
            .OrderBy(candidate => candidate.EntityType, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.PrimaryText, StringComparer.OrdinalIgnoreCase)
            .Select(CreateFileStorageIssueDetailRow)
            .ToList();
    }

    private FileStorageInspectionResult InspectStoredFile(string? storagePath, string? fileHash)
        => _fileStorage.Inspect(storagePath, computeHash: IsSha256Hex(fileHash));

    private static bool IsFileContentUnavailable(FileStorageIssueCandidate candidate)
        => candidate.FileSize > 0 &&
           string.IsNullOrWhiteSpace(candidate.StoragePath) &&
           candidate.FileContentLength <= 0;

    private static bool HasDbFileContentResidue(FileStorageIssueCandidate candidate)
        => candidate.FileContentLength > 0;

    private static bool IsStoredFileMissing(FileStorageIssueCandidate candidate)
        => candidate.FileSize > 0 &&
           !string.IsNullOrWhiteSpace(candidate.StoragePath) &&
           (!candidate.StorageInspection.IsSafePath || !candidate.StorageInspection.Exists);

    private static bool IsStoredFileSizeMismatch(FileStorageIssueCandidate candidate)
        => candidate.FileSize > 0 &&
           candidate.StorageInspection.Exists &&
           candidate.StorageInspection.Length.HasValue &&
           candidate.StorageInspection.Length.Value != candidate.FileSize;

    private static bool IsStoredFileHashMismatch(FileStorageIssueCandidate candidate)
        => IsSha256Hex(candidate.FileHash) &&
           candidate.StorageInspection.Exists &&
           !string.IsNullOrWhiteSpace(candidate.StorageInspection.Hash) &&
           !string.Equals(candidate.StorageInspection.Hash, candidate.FileHash.Trim(), StringComparison.OrdinalIgnoreCase);

    private bool HasAttachmentContentSignatureMismatch(AttachmentContentSignatureIssueRow row)
    {
        var content = _fileStorage.ReadBytes(row.StoragePath, row.FileContent);
        return content.Length > 0 &&
               !EvidenceAttachmentFilePolicy.ContentMatchesFileType(row.FileName, row.MimeType, content);
    }

    private static bool IsSha256Hex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (trimmed.Length != 64)
            return false;

        return trimmed.All(character =>
            (character >= '0' && character <= '9') ||
            (character >= 'a' && character <= 'f') ||
            (character >= 'A' && character <= 'F'));
    }

    private static bool IsAllowedEvidenceAttachmentFileType(string? fileName, string? mimeType)
    {
        var safeFileName = Path.GetFileName(fileName ?? string.Empty);
        var extension = Path.GetExtension(safeFileName);
        if (string.IsNullOrWhiteSpace(safeFileName) || !AllowedEvidenceAttachmentExtensions.Contains(extension))
            return false;

        var normalizedMimeType = NormalizeEvidenceAttachmentContentType(mimeType, safeFileName);
        if (AllowedEvidenceAttachmentContentTypes.Contains(normalizedMimeType))
            return true;

        return string.Equals(normalizedMimeType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildUnsupportedAttachmentFileTypeDetail(AttachmentFileTypeIssueRow row)
    {
        var safeFileName = Path.GetFileName(row.FileName ?? string.Empty);
        var extension = Path.GetExtension(safeFileName);
        var normalizedMimeType = NormalizeEvidenceAttachmentContentType(row.MimeType, safeFileName);
        var hasUnsupportedExtension = string.IsNullOrWhiteSpace(safeFileName) ||
                                      !AllowedEvidenceAttachmentExtensions.Contains(extension);
        var hasUnsupportedMimeType =
            !AllowedEvidenceAttachmentContentTypes.Contains(normalizedMimeType) &&
            !(string.Equals(normalizedMimeType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) &&
              string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase));

        return CombineParts(
            hasUnsupportedExtension
                ? $"지원하지 않는 확장자 {FirstNonEmpty(extension, "(없음)")}"
                : null,
            hasUnsupportedMimeType ? "지원하지 않는 MIME" : null,
            string.IsNullOrWhiteSpace(row.MimeType) ? "MimeType 비어 있음" : $"MimeType {row.MimeType.Trim()}",
            row.FileSize > 0 ? $"크기 {row.FileSize:N0} bytes" : null);
    }

    private string BuildAttachmentContentSignatureMismatchDetail(AttachmentContentSignatureIssueRow row)
    {
        var content = _fileStorage.ReadBytes(row.StoragePath, row.FileContent);
        var normalizedMimeType = EvidenceAttachmentFilePolicy.NormalizeContentType(row.MimeType, row.FileName);
        return CombineParts(
            "파일 내용 불일치",
            $"MimeType {normalizedMimeType}",
            row.FileSize > 0 ? $"DB 크기 {row.FileSize:N0} bytes" : null,
            content.Length > 0 ? $"읽은 크기 {content.Length:N0} bytes" : "읽은 파일 내용 없음",
            string.IsNullOrWhiteSpace(row.FileHash) ? null : $"FileHash {row.FileHash}",
            string.IsNullOrWhiteSpace(row.StoragePath) ? "StoragePath 비어 있음" : $"StoragePath {row.StoragePath}");
    }

    private static string NormalizeEvidenceAttachmentContentType(string? contentType, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType.Split(';', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".tif" or ".tiff" => "image/tiff",
            ".heic" => "image/heic",
            ".heif" => "image/heif",
            _ => "application/octet-stream"
        };
    }

    private static string FormatStorageInspection(FileStorageInspectionResult inspection)
    {
        if (!inspection.HasStoredPath)
            return "저장파일 경로 없음";
        if (!inspection.IsSafePath)
            return CombineParts("저장파일 경로 불안전", inspection.Error);
        if (!inspection.Exists)
            return CombineParts("저장파일 없음", inspection.Error);

        return CombineParts(
            inspection.Length.HasValue ? $"저장파일 크기 {inspection.Length.Value:N0} bytes" : null,
            string.IsNullOrWhiteSpace(inspection.Hash) ? null : $"저장파일 SHA256 {inspection.Hash}");
    }

    private static string FormatScope(params string?[] parts)
        => string.Join(" / ", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));

    private static string CombineParts(params string?[] parts)
        => string.Join(" / ", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));

    private static string FirstNonEmpty(params string?[] parts)
        => parts.FirstOrDefault(part => !string.IsNullOrWhiteSpace(part))?.Trim() ?? string.Empty;

    private static string FormatOfficeList(IReadOnlyCollection<string> officeCodes)
        => officeCodes.Count == 0
            ? "-"
            : string.Join(", ", officeCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));

    private static string ResolveOfficeCodeFromWarehouseCode(string? warehouseCode)
    {
        var normalizedWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeLoose(warehouseCode);
        return normalizedWarehouseCode switch
        {
            OfficeCodeCatalog.ItworldMainWarehouse => OfficeCodeCatalog.Itworld,
            OfficeCodeCatalog.YeonsuMainWarehouse => OfficeCodeCatalog.Yeonsu,
            _ => OfficeCodeCatalog.Usenet
        };
    }

    private static string NormalizeCellText(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    private static string FormatGuid(Guid value)
        => value.ToString("D");

    private static string FormatDate(DateOnly value)
        => value.ToString("yyyy-MM-dd");

    private static string FormatNumber(decimal value)
        => value.ToString("#,##0.##");

    private static string FormatSignedNumber(decimal value)
        => value > 0m ? "+" + FormatNumber(value) : FormatNumber(value);

    private static string FormatMoney(decimal value)
        => value.ToString("#,##0.##");

    private static string FormatOptionalMoney(decimal? value)
        => value.HasValue ? FormatMoney(value.Value) : "행 없음";

    private static string BuildInvoiceLinkedTransactionPaymentMismatchReason(InvoiceLinkedTransactionPaymentMismatchRow row)
    {
        if (!row.PaymentId.HasValue)
            return "수금·지급 행 없음";
        if (row.PaymentIsDeleted == true)
            return "수금·지급 삭제상태";
        if (row.PaymentInvoiceId != row.InvoiceId)
            return "수금·지급 전표 링크 불일치";
        if (!row.PaymentAmount.HasValue || AmountDiffers(row.PaymentAmount.Value, row.TransactionSettlementAmount))
            return "수금·지급 금액 불일치";

        return "전표 연결 거래내역/수금·지급 불일치";
    }

    private static string FormatVoucherType(VoucherType value)
        => value switch
        {
            VoucherType.Sales => "판매",
            VoucherType.Purchase => "구매",
            VoucherType.Procurement => "발주",
            VoucherType.Expense => "경비",
            VoucherType.Collection => "수금",
            _ => value.ToString()
        };

    private static string FormatNegativeStockInvoiceEvidence(NegativeStockInvoiceEvidence evidence)
    {
        var warehouseCode = evidence.VoucherType == VoucherType.Purchase && !string.IsNullOrWhiteSpace(evidence.PurchaseReceivingWarehouseCode)
            ? evidence.PurchaseReceivingWarehouseCode
            : evidence.SourceWarehouseCode;

        return CombineParts(
            FormatDate(evidence.InvoiceDate),
            FormatVoucherType(evidence.VoucherType),
            string.IsNullOrWhiteSpace(evidence.InvoiceNumber) ? null : $"전표 {evidence.InvoiceNumber}",
            string.IsNullOrWhiteSpace(evidence.CustomerName) ? null : $"거래처 {evidence.CustomerName}",
            $"수량 {FormatNumber(evidence.Quantity)}",
            string.IsNullOrWhiteSpace(warehouseCode) ? null : $"창고 {warehouseCode}");
    }

    private static string FormatUtcDateTime(DateTime value)
        => value == default ? "-" : $"{value:yyyy-MM-dd HH:mm:ss} UTC";

    private sealed record IntegrityIssueDefinition(string Code, string Severity, string Message);

    private sealed record FileStorageIssueCandidate(
        string EntityType,
        Guid EntityId,
        string PrimaryText,
        string SecondaryText,
        string ReferenceText,
        string ScopeText,
        long FileSize,
        string FileHash,
        string StoragePath,
        int FileContentLength,
        FileStorageInspectionResult StorageInspection);

    private sealed record AttachmentFileTypeIssueRow(
        string EntityType,
        Guid EntityId,
        string FileName,
        string MimeType,
        string SecondaryText,
        string ReferenceText,
        string ScopeText,
        long FileSize);

    private sealed record AttachmentContentSignatureIssueRow(
        string EntityType,
        Guid EntityId,
        string FileName,
        string MimeType,
        string SecondaryText,
        string ReferenceText,
        string ScopeText,
        long FileSize,
        string FileHash,
        string StoragePath,
        byte[] FileContent);

    private sealed record NegativeStockInvoiceEvidence(
        Guid ItemId,
        string InvoiceNumber,
        DateOnly InvoiceDate,
        VoucherType VoucherType,
        string SourceWarehouseCode,
        string PurchaseReceivingWarehouseCode,
        string CustomerName,
        decimal Quantity,
        decimal LineAmount);

    private sealed record InvoiceTotalActiveLineMismatchRow(
        Guid InvoiceId,
        Guid CustomerId,
        string TenantCode,
        string OfficeCode,
        string ResponsibleOfficeCode,
        string InvoiceNumber,
        string LocalTempNumber,
        DateOnly InvoiceDate,
        VoucherType VoucherType,
        string VatMode,
        decimal SupplyAmount,
        decimal VatAmount,
        decimal TotalAmount,
        int ActiveLineCount,
        decimal ActiveLineTotal,
        decimal ExpectedSupplyAmount,
        decimal ExpectedVatAmount,
        decimal ExpectedTotalAmount);

    private sealed record InventoryTransferRouteSnapshot(
        string? TenantCode,
        string? SourceOfficeCode,
        string? TargetOfficeCode,
        string? FromWarehouseCode,
        string? ToWarehouseCode);

    private sealed record InventoryTransferRouteDescription(
        string SourceTenantCode,
        string TargetTenantCode,
        string SourceOfficeCode,
        string TargetOfficeCode);

    private sealed record InventoryTransferDetailSnapshot(
        Guid Id,
        string? TenantCode,
        string? SourceOfficeCode,
        string? TargetOfficeCode,
        string? FromWarehouseCode,
        string? ToWarehouseCode,
        string? TransferNumber,
        DateOnly TransferDate,
        string? TransferStatus,
        string? Memo);

    private sealed record SharedItemScopeConflictSnapshot(
        Item Item,
        ItemScopeInferenceResult Inference);

    private sealed record DeletedItemSnapshot(
        Guid Id,
        string TenantCode,
        string OfficeCode,
        string NameOriginal,
        string NameMatchKey,
        string SpecificationOriginal,
        string CategoryName,
        decimal CurrentStock);

    private sealed record DeletedItemStockResidueSnapshot(
        DeletedItemSnapshot Item,
        int WarehouseRowCount,
        decimal WarehouseSum,
        string WarehouseBreakdown);

    private sealed record ItemWarehouseStockSnapshot(Guid ItemId, string WarehouseCode, decimal Quantity);

    private sealed record ParsedRentalBillingTemplateItems(
        bool Success,
        List<RentalBillingTemplateItemSnapshot> Items);

    private sealed record RentalTemplateScanRow(
        RentalBillingProfile Profile,
        bool TemplateParseSucceeded,
        List<RentalBillingTemplateItemSnapshot> TemplateItems,
        decimal TemplateMonthlyAmount,
        List<RentalAsset> LinkedAssets,
        decimal LinkedAssetMonthlyAmount,
        IReadOnlyDictionary<Guid, RentalAsset> ScopedAssetsById);

    private sealed record RentalAssetTemplateMonthlyMismatchRow(
        RentalBillingProfile Profile,
        RentalBillingTemplateItemSnapshot TemplateItem,
        List<RentalAsset> LinkedAssets,
        decimal TemplateMonthlyAmount,
        decimal AssetMonthlyAmount);

    private sealed record InvoiceLinkedTransactionPaymentMismatchRow(
        Guid TransactionId,
        Guid InvoiceId,
        Guid? PaymentId,
        string TenantCode,
        string OfficeCode,
        string ResponsibleOfficeCode,
        DateOnly TransactionDate,
        string TransactionKind,
        string LinkedInvoiceNumber,
        decimal TransactionSettlementAmount,
        string InvoiceNumber,
        string LocalTempNumber,
        DateOnly InvoiceDate,
        decimal InvoiceTotalAmount,
        Guid? PaymentInvoiceId,
        decimal? PaymentAmount,
        bool? PaymentIsDeleted);

    private sealed record RentalBillingRunSettlementMismatchRow(
        Guid ProfileId,
        string TenantCode,
        string OfficeCode,
        string ResponsibleOfficeCode,
        string ProfileDisplayName,
        Guid RunId,
        string RunKey,
        DateOnly ScheduledDate,
        decimal BilledAmount,
        decimal StoredSettledAmount,
        decimal ActualSettledAmount,
        decimal TransactionSettledAmount,
        decimal DirectPaymentSettledAmount,
        string Status,
        string SettlementStatus);

    private sealed record RentalBillingRunMissingRunIdRow(
        Guid ProfileId,
        string TenantCode,
        string OfficeCode,
        string ResponsibleOfficeCode,
        string ProfileDisplayName,
        string RunKey,
        DateOnly ScheduledDate,
        DateOnly PeriodStartDate,
        DateOnly PeriodEndDate,
        decimal BilledAmount,
        decimal SettledAmount,
        string Status,
        string SettlementStatus);

    private sealed record RentalBillingProfileSummaryMismatchRow(
        Guid ProfileId,
        string TenantCode,
        string OfficeCode,
        string ResponsibleOfficeCode,
        string ProfileDisplayName,
        Guid RunId,
        string RunKey,
        DateOnly ScheduledDate,
        decimal StoredProfileSettledAmount,
        decimal StoredProfileOutstandingAmount,
        decimal ExpectedSettledAmount,
        decimal ExpectedOutstandingAmount,
        decimal ExpectedBilledAmount,
        decimal TransactionSettledAmount,
        decimal DirectPaymentSettledAmount,
        string ProfileBillingStatus,
        string ProfileSettlementStatus,
        string ProfileCompletionStatus,
        string RunStatus,
        string RunSettlementStatus);

    private sealed class RentalBillingRunSettlementSnapshot
    {
        public Guid RunId { get; set; }
        public string RunKey { get; set; } = string.Empty;
        public DateOnly ScheduledDate { get; set; }
        public DateOnly PeriodStartDate { get; set; }
        public DateOnly PeriodEndDate { get; set; }
        public decimal BilledAmount { get; set; }
        public decimal SettledAmount { get; set; }
        public string Status { get; set; } = string.Empty;
        public string SettlementStatus { get; set; } = string.Empty;
    }

    private sealed class RentalBillingTemplateItemSnapshot
    {
        public Guid ItemId { get; set; } = Guid.NewGuid();
        public string DisplayItemName { get; set; } = string.Empty;
        public string BillingLineMode { get; set; } = string.Empty;
        public string Specification { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public string MaterialNumber { get; set; } = string.Empty;
        public Guid? RepresentativeAssetId { get; set; }
        public decimal Quantity { get; set; } = 1m;
        public decimal UnitPrice { get; set; }
        public decimal Amount { get; set; }
        public string Note { get; set; } = string.Empty;
        public List<Guid> IncludedAssetIds { get; set; } = new();
    }
}
