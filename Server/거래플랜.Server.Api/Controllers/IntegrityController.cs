using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
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
    private readonly AppDbContext _dbContext;
    private readonly OfficeScopeService _officeScopeService;

    public IntegrityController(AppDbContext dbContext, OfficeScopeService officeScopeService)
    {
        _dbContext = dbContext;
        _officeScopeService = officeScopeService;
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

        var orphanTransactionCustomerCount = await _officeScopeService.ApplyTransactionScope(_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
            .Where(transaction => !transaction.IsDeleted)
            .CountAsync(transaction => !_dbContext.Customers.IgnoreQueryFilters().Any(customer => !customer.IsDeleted && customer.Id == transaction.CustomerId), cancellationToken);
        AddIssue(issues, "orphan_transaction_customer_refs", orphanTransactionCustomerCount, "Error", "거래처가 없는 수금/지불 참조가 존재합니다.");

        var orphanRentalProfileCustomerCount = await _officeScopeService.ApplyRentalBillingProfileScope(_dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
            .Where(profile => !profile.IsDeleted && profile.CustomerId.HasValue)
            .CountAsync(profile => !_dbContext.Customers.IgnoreQueryFilters().Any(customer => !customer.IsDeleted && customer.Id == profile.CustomerId), cancellationToken);
        AddIssue(issues, "orphan_rental_profile_customer_refs", orphanRentalProfileCustomerCount, "Error", "거래처가 없는 렌탈 청구 프로필 참조가 존재합니다.");

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
            "cross_tenant_inventory_transfers" => await LoadCrossTenantInventoryTransferDetailsAsync(cancellationToken),
            "orphan_item_warehouse_stock_refs" => await LoadOrphanItemWarehouseStockDetailsAsync(cancellationToken),
            "item_stock_snapshot_mismatch" => await LoadItemStockSnapshotMismatchDetailsAsync(cancellationToken),
            "orphan_invoice_customer_refs" => await LoadOrphanInvoiceCustomerDetailsAsync(cancellationToken),
            "orphan_transaction_customer_refs" => await LoadOrphanTransactionCustomerDetailsAsync(cancellationToken),
            "orphan_rental_profile_customer_refs" => await LoadOrphanRentalProfileCustomerDetailsAsync(cancellationToken),
            "orphan_rental_asset_customer_refs" => await LoadOrphanRentalAssetCustomerDetailsAsync(cancellationToken),
            "orphan_rental_asset_profile_refs" => await LoadOrphanRentalAssetProfileDetailsAsync(cancellationToken),
            "orphan_rental_asset_item_refs" => await LoadOrphanRentalAssetItemDetailsAsync(cancellationToken),
            "orphan_transaction_invoice_refs" => await LoadOrphanTransactionInvoiceDetailsAsync(cancellationToken),
            "orphan_payment_invoice_refs" => await LoadOrphanPaymentInvoiceDetailsAsync(cancellationToken),
            "orphan_attachment_transaction_refs" => await LoadOrphanTransactionAttachmentDetailsAsync(cancellationToken),
            "orphan_payment_attachment_refs" => await LoadOrphanPaymentAttachmentDetailsAsync(cancellationToken),
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
            "cross_tenant_inventory_transfers" => new IntegrityIssueDefinition("cross_tenant_inventory_transfers", "Error", "업체 간 직접 재고이동 문서가 존재합니다."),
            "orphan_item_warehouse_stock_refs" => new IntegrityIssueDefinition("orphan_item_warehouse_stock_refs", "Error", "품목이 없는 창고 재고 행이 존재합니다."),
            "item_stock_snapshot_mismatch" => new IntegrityIssueDefinition("item_stock_snapshot_mismatch", "Warning", "품목 현재재고와 창고 합계가 일치하지 않는 항목이 있습니다."),
            "orphan_invoice_customer_refs" => new IntegrityIssueDefinition("orphan_invoice_customer_refs", "Error", "거래처가 없는 전표 참조가 존재합니다."),
            "orphan_transaction_customer_refs" => new IntegrityIssueDefinition("orphan_transaction_customer_refs", "Error", "거래처가 없는 수금/지불 참조가 존재합니다."),
            "orphan_rental_profile_customer_refs" => new IntegrityIssueDefinition("orphan_rental_profile_customer_refs", "Error", "거래처가 없는 렌탈 청구 프로필 참조가 존재합니다."),
            "orphan_rental_asset_customer_refs" => new IntegrityIssueDefinition("orphan_rental_asset_customer_refs", "Error", "거래처가 없는 렌탈 자산 참조가 존재합니다."),
            "orphan_rental_asset_profile_refs" => new IntegrityIssueDefinition("orphan_rental_asset_profile_refs", "Error", "렌탈 청구 프로필이 없는 자산 연결이 존재합니다."),
            "orphan_rental_asset_item_refs" => new IntegrityIssueDefinition("orphan_rental_asset_item_refs", "Error", "품목이 없는 렌탈 자산 연결이 존재합니다."),
            "orphan_transaction_invoice_refs" => new IntegrityIssueDefinition("orphan_transaction_invoice_refs", "Error", "전표가 없는 거래/수금 참조가 존재합니다."),
            "orphan_payment_invoice_refs" => new IntegrityIssueDefinition("orphan_payment_invoice_refs", "Error", "전표가 없는 수금/지급 참조가 존재합니다."),
            "orphan_attachment_transaction_refs" => new IntegrityIssueDefinition("orphan_attachment_transaction_refs", "Error", "거래내역이 없는 증빙 첨부가 존재합니다."),
            "orphan_payment_attachment_refs" => new IntegrityIssueDefinition("orphan_payment_attachment_refs", "Error", "결제내역이 없는 결제 첨부가 존재합니다."),
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

    private static string FormatMoney(decimal value)
        => value.ToString("#,##0.##");

    private static string FormatUtcDateTime(DateTime value)
        => value == default ? "-" : $"{value:yyyy-MM-dd HH:mm:ss} UTC";

    private sealed record IntegrityIssueDefinition(string Code, string Severity, string Message);

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

    private sealed record ItemWarehouseStockSnapshot(Guid ItemId, string WarehouseCode, decimal Quantity);
}
