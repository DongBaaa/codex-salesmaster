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

        var duplicateProfileKeyCount = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => !profile.IsDeleted && !string.IsNullOrWhiteSpace(profile.ProfileKey))
            .GroupBy(profile => profile.ProfileKey)
            .Where(group => group.Count() > 1)
            .Select(group => group.Count())
            .SumAsync(cancellationToken);
        AddIssue(issues, "duplicate_rental_profile_keys", duplicateProfileKeyCount, "Error", "중복된 렌탈 청구 프로필 키가 존재합니다.");

        var duplicateAssetKeyCount = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
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

        var orphanWarehouseStockCount = await _dbContext.ItemWarehouseStocks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(stock => !_dbContext.Items.IgnoreQueryFilters().Any(item => !item.IsDeleted && item.Id == stock.ItemId), cancellationToken);
        AddIssue(issues, "orphan_item_warehouse_stock_refs", orphanWarehouseStockCount, "Error", "품목이 없는 창고 재고 행이 존재합니다.");

        var warehouseStocks = await _dbContext.ItemWarehouseStocks
            .AsNoTracking()
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

        var invoiceLineMissingInvoiceRowCount = await _dbContext.InvoiceLines
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(line => !_dbContext.Invoices.IgnoreQueryFilters().Any(invoice => invoice.Id == line.InvoiceId), cancellationToken);
        AddIssue(issues, "invoice_line_missing_invoice_rows", invoiceLineMissingInvoiceRowCount, "Error", "부모 전표 행이 없는 전표 세부내역이 존재합니다.");

        var orphanTransactionCustomerCount = await _officeScopeService.ApplyTransactionScope(_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
            .Where(transaction => !transaction.IsDeleted)
            .CountAsync(transaction => !_dbContext.Customers.IgnoreQueryFilters().Any(customer => !customer.IsDeleted && customer.Id == transaction.CustomerId), cancellationToken);
        AddIssue(issues, "orphan_transaction_customer_refs", orphanTransactionCustomerCount, "Error", "거래처가 없는 수금/지불 참조가 존재합니다.");

        var orphanRentalProfileCustomerCount = await _officeScopeService.ApplyRentalBillingProfileScope(_dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
            .Where(profile => !profile.IsDeleted && profile.CustomerId.HasValue)
            .CountAsync(profile => !_dbContext.Customers.IgnoreQueryFilters().Any(customer => !customer.IsDeleted && customer.Id == profile.CustomerId), cancellationToken);
        AddIssue(issues, "orphan_rental_profile_customer_refs", orphanRentalProfileCustomerCount, "Error", "거래처가 없는 렌탈 청구 프로필 참조가 존재합니다.");

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

        var orphanRentalAssetProfileCount = await _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
            .Where(asset => !asset.IsDeleted && asset.BillingProfileId.HasValue)
            .CountAsync(asset => !_dbContext.RentalBillingProfiles.IgnoreQueryFilters().Any(profile => !profile.IsDeleted && profile.Id == asset.BillingProfileId), cancellationToken);
        AddIssue(issues, "orphan_rental_asset_profile_refs", orphanRentalAssetProfileCount, "Error", "렌탈 청구 프로필이 없는 자산 연결이 존재합니다.");

        var orphanRentalAssetItemCount = await _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
            .Where(asset => !asset.IsDeleted && asset.ItemId.HasValue)
            .CountAsync(asset => !_dbContext.Items.IgnoreQueryFilters().Any(item => !item.IsDeleted && item.Id == asset.ItemId), cancellationToken);
        AddIssue(issues, "orphan_rental_asset_item_refs", orphanRentalAssetItemCount, "Error", "품목이 없는 렌탈 자산 연결이 존재합니다.");

        var orphanTransactionInvoiceCount = await _officeScopeService.ApplyTransactionScope(_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
            .Where(transaction => !transaction.IsDeleted && transaction.LinkedInvoiceId.HasValue)
            .CountAsync(transaction => !_dbContext.Invoices.IgnoreQueryFilters().Any(invoice => !invoice.IsDeleted && invoice.Id == transaction.LinkedInvoiceId), cancellationToken);
        AddIssue(issues, "orphan_transaction_invoice_refs", orphanTransactionInvoiceCount, "Error", "전표가 없는 거래/수금 참조가 존재합니다.");

        var orphanPaymentInvoiceCount = await _dbContext.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(payment => !payment.IsDeleted)
            .CountAsync(payment => !_dbContext.Invoices.IgnoreQueryFilters().Any(invoice => !invoice.IsDeleted && invoice.Id == payment.InvoiceId), cancellationToken);
        AddIssue(issues, "orphan_payment_invoice_refs", orphanPaymentInvoiceCount, "Error", "전표가 없는 수금/지급 참조가 존재합니다.");

        var deletedPaymentMissingInvoiceRowCount = await _dbContext.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(payment => payment.IsDeleted)
            .CountAsync(payment => !_dbContext.Invoices.IgnoreQueryFilters().Any(invoice => invoice.Id == payment.InvoiceId), cancellationToken);
        AddIssue(issues, "deleted_payment_missing_invoice_rows", deletedPaymentMissingInvoiceRowCount, "Error", "영구 삭제된 전표의 삭제 결제 잔여 행이 존재합니다.");

        var orphanTransactionAttachmentCount = await _dbContext.TransactionAttachments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(attachment => !attachment.IsDeleted)
            .CountAsync(attachment => !_dbContext.Transactions.IgnoreQueryFilters().Any(transaction => !transaction.IsDeleted && transaction.Id == attachment.TransactionId), cancellationToken);
        AddIssue(issues, "orphan_attachment_transaction_refs", orphanTransactionAttachmentCount, "Error", "거래내역이 없는 증빙 첨부가 존재합니다.");

        var orphanPaymentAttachmentCount = await _dbContext.PaymentAttachments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(attachment => !attachment.IsDeleted)
            .CountAsync(attachment => !_dbContext.Payments.IgnoreQueryFilters().Any(payment => !payment.IsDeleted && payment.Id == attachment.PaymentId), cancellationToken);
        AddIssue(issues, "orphan_payment_attachment_refs", orphanPaymentAttachmentCount, "Error", "결제내역이 없는 결제 첨부가 존재합니다.");

        var deletedPaymentAttachmentMissingPaymentRowCount = await _dbContext.PaymentAttachments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(attachment => attachment.IsDeleted)
            .CountAsync(attachment => !_dbContext.Payments.IgnoreQueryFilters().Any(payment => payment.Id == attachment.PaymentId), cancellationToken);
        AddIssue(issues, "deleted_payment_attachment_missing_payment_rows", deletedPaymentAttachmentMissingPaymentRowCount, "Error", "영구 삭제된 결제의 삭제 첨부 잔여 행이 존재합니다.");

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
            "orphan_item_warehouse_stock_refs" => await LoadOrphanItemWarehouseStockDetailsAsync(cancellationToken),
            "item_stock_snapshot_mismatch" => await LoadItemStockSnapshotMismatchDetailsAsync(cancellationToken),
            "orphan_invoice_customer_refs" => await LoadOrphanInvoiceCustomerDetailsAsync(cancellationToken),
            "active_invoice_lines_deleted_invoice" => await LoadActiveInvoiceLinesDeletedInvoiceDetailsAsync(cancellationToken),
            "invoice_line_missing_invoice_rows" => await LoadInvoiceLineMissingInvoiceRowDetailsAsync(cancellationToken),
            "orphan_transaction_customer_refs" => await LoadOrphanTransactionCustomerDetailsAsync(cancellationToken),
            "orphan_rental_profile_customer_refs" => await LoadOrphanRentalProfileCustomerDetailsAsync(cancellationToken),
            "rental_profile_customer_unlinked" => await LoadRentalProfileCustomerUnlinkedDetailsAsync(cancellationToken),
            "rental_profile_monthly_amount_mismatch" => await LoadRentalProfileMonthlyAmountMismatchDetailsAsync(cancellationToken),
            "rental_profile_asset_monthly_amount_mismatch" => await LoadRentalProfileAssetMonthlyAmountMismatchDetailsAsync(cancellationToken),
            "rental_asset_template_monthly_mismatch" => await LoadRentalAssetTemplateMonthlyMismatchDetailsAsync(cancellationToken),
            "orphan_rental_asset_customer_refs" => await LoadOrphanRentalAssetCustomerDetailsAsync(cancellationToken),
            "orphan_rental_asset_profile_refs" => await LoadOrphanRentalAssetProfileDetailsAsync(cancellationToken),
            "orphan_rental_asset_item_refs" => await LoadOrphanRentalAssetItemDetailsAsync(cancellationToken),
            "orphan_transaction_invoice_refs" => await LoadOrphanTransactionInvoiceDetailsAsync(cancellationToken),
            "orphan_payment_invoice_refs" => await LoadOrphanPaymentInvoiceDetailsAsync(cancellationToken),
            "deleted_payment_missing_invoice_rows" => await LoadDeletedPaymentMissingInvoiceRowDetailsAsync(cancellationToken),
            "orphan_attachment_transaction_refs" => await LoadOrphanTransactionAttachmentDetailsAsync(cancellationToken),
            "orphan_payment_attachment_refs" => await LoadOrphanPaymentAttachmentDetailsAsync(cancellationToken),
            "deleted_payment_attachment_missing_payment_rows" => await LoadDeletedPaymentAttachmentMissingPaymentRowDetailsAsync(cancellationToken),
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
        var duplicateKeys = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => !profile.IsDeleted && !string.IsNullOrWhiteSpace(profile.ProfileKey))
            .GroupBy(profile => profile.ProfileKey)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToListAsync(cancellationToken);

        if (duplicateKeys.Count == 0)
            return [];

        var profiles = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
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
        var duplicateKeys = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => !asset.IsDeleted && !string.IsNullOrWhiteSpace(asset.AssetKey))
            .GroupBy(asset => asset.AssetKey)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToListAsync(cancellationToken);

        if (duplicateKeys.Count == 0)
            return [];

        var assets = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
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

    private async Task<List<IntegrityIssueDetailRowDto>> LoadOrphanItemWarehouseStockDetailsAsync(CancellationToken cancellationToken)
    {
        var orphanStocks = await (
                from stock in _dbContext.ItemWarehouseStocks.IgnoreQueryFilters().AsNoTracking()
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
            warehouseStocks.AddRange(await _dbContext.ItemWarehouseStocks
                .IgnoreQueryFilters()
                .AsNoTracking()
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

        var warehouseStocks = await _dbContext.ItemWarehouseStocks
            .AsNoTracking()
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

    private async Task<List<IntegrityIssueDetailRowDto>> LoadInvoiceLineMissingInvoiceRowDetailsAsync(CancellationToken cancellationToken)
    {
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
        var payments = await (
                from payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                join invoice in _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    on payment.InvoiceId equals invoice.Id into invoiceGroup
                from invoice in invoiceGroup.DefaultIfEmpty()
                where invoice == null
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

    private async Task<List<IntegrityIssueDetailRowDto>> LoadOrphanTransactionAttachmentDetailsAsync(CancellationToken cancellationToken)
    {
        var attachments = await (
                from attachment in _dbContext.TransactionAttachments.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                join transaction in _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    on attachment.TransactionId equals transaction.Id into transactionGroup
                from transaction in transactionGroup.DefaultIfEmpty()
                where transaction == null
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

    private async Task<List<IntegrityIssueDetailRowDto>> LoadOrphanPaymentAttachmentDetailsAsync(CancellationToken cancellationToken)
    {
        var attachments = await (
                from attachment in _dbContext.PaymentAttachments.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                join payment in _dbContext.Payments.IgnoreQueryFilters().AsNoTracking().Where(current => !current.IsDeleted)
                    on attachment.PaymentId equals payment.Id into paymentGroup
                from payment in paymentGroup.DefaultIfEmpty()
                where payment == null
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
            "orphan_item_warehouse_stock_refs" => new IntegrityIssueDefinition("orphan_item_warehouse_stock_refs", "Error", "품목이 없는 창고 재고 행이 존재합니다."),
            "item_stock_snapshot_mismatch" => new IntegrityIssueDefinition("item_stock_snapshot_mismatch", "Warning", "품목 현재재고와 창고 합계가 일치하지 않는 항목이 있습니다."),
            "orphan_invoice_customer_refs" => new IntegrityIssueDefinition("orphan_invoice_customer_refs", "Error", "거래처가 없는 전표 참조가 존재합니다."),
            "active_invoice_lines_deleted_invoice" => new IntegrityIssueDefinition("active_invoice_lines_deleted_invoice", "Error", "삭제된 전표에 활성 세부내역 행이 남아 있습니다."),
            "invoice_line_missing_invoice_rows" => new IntegrityIssueDefinition("invoice_line_missing_invoice_rows", "Error", "부모 전표 행이 없는 전표 세부내역이 존재합니다."),
            "orphan_transaction_customer_refs" => new IntegrityIssueDefinition("orphan_transaction_customer_refs", "Error", "거래처가 없는 수금/지불 참조가 존재합니다."),
            "orphan_rental_profile_customer_refs" => new IntegrityIssueDefinition("orphan_rental_profile_customer_refs", "Error", "거래처가 없는 렌탈 청구 프로필 참조가 존재합니다."),
            "rental_profile_customer_unlinked" => new IntegrityIssueDefinition("rental_profile_customer_unlinked", "Warning", "거래처 ID 없이 거래처명만 저장된 렌탈 청구 프로필이 있습니다."),
            "rental_profile_monthly_amount_mismatch" => new IntegrityIssueDefinition("rental_profile_monthly_amount_mismatch", "Warning", "렌탈 청구 프로필 월 기준금액과 청구 품목 합계가 다릅니다."),
            "rental_profile_asset_monthly_amount_mismatch" => new IntegrityIssueDefinition("rental_profile_asset_monthly_amount_mismatch", "Warning", "렌탈 청구 프로필 월 기준금액과 연결 자산 월요금 합계가 다릅니다."),
            "rental_asset_template_monthly_mismatch" => new IntegrityIssueDefinition("rental_asset_template_monthly_mismatch", "Warning", "렌탈 자산 월요금 합계와 청구 품목 금액이 다릅니다."),
            "orphan_rental_asset_customer_refs" => new IntegrityIssueDefinition("orphan_rental_asset_customer_refs", "Error", "거래처가 없는 렌탈 자산 참조가 존재합니다."),
            "orphan_rental_asset_profile_refs" => new IntegrityIssueDefinition("orphan_rental_asset_profile_refs", "Error", "렌탈 청구 프로필이 없는 자산 연결이 존재합니다."),
            "orphan_rental_asset_item_refs" => new IntegrityIssueDefinition("orphan_rental_asset_item_refs", "Error", "품목이 없는 렌탈 자산 연결이 존재합니다."),
            "orphan_transaction_invoice_refs" => new IntegrityIssueDefinition("orphan_transaction_invoice_refs", "Error", "전표가 없는 거래/수금 참조가 존재합니다."),
            "orphan_payment_invoice_refs" => new IntegrityIssueDefinition("orphan_payment_invoice_refs", "Error", "전표가 없는 수금/지급 참조가 존재합니다."),
            "deleted_payment_missing_invoice_rows" => new IntegrityIssueDefinition("deleted_payment_missing_invoice_rows", "Error", "영구 삭제된 전표의 삭제 결제 잔여 행이 존재합니다."),
            "orphan_attachment_transaction_refs" => new IntegrityIssueDefinition("orphan_attachment_transaction_refs", "Error", "거래내역이 없는 증빙 첨부가 존재합니다."),
            "orphan_payment_attachment_refs" => new IntegrityIssueDefinition("orphan_payment_attachment_refs", "Error", "결제내역이 없는 결제 첨부가 존재합니다."),
            "deleted_payment_attachment_missing_payment_rows" => new IntegrityIssueDefinition("deleted_payment_attachment_missing_payment_rows", "Error", "영구 삭제된 결제의 삭제 첨부 잔여 행이 존재합니다."),
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
